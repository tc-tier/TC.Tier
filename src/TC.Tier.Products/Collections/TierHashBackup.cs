using System.Buffers;
using System.Buffers.Binary;
using TC.Tier.Core.Primitives;

namespace TC.Tier.Products.Collections;

/// <summary>导入结果统计。</summary>
/// <param name="ImportedCount">导入记录总数（幂等覆盖计入——按写路径实际执行数）。</param>
/// <param name="DomainCount">涉及域数（流内出现过的不同 domainId）。</param>
public readonly record struct TierHashImportResult(long ImportedCount, int DomainCount);

/// <summary>
/// TierHash 冷备份导出/导入算子（组合糖——TimeSeriesBackup 同族形态，tc-tier-collections-spec §8）：
/// 把全域 field→value 流导出为自描述备份流，导入端逐批重放（判重/覆盖语义原样生效——幂等）。
/// <para>★ 消费形态：实例迁移/灾备重建/冷档归档。</para>
/// <para>★ 流格式（全部小端，CRC32C 覆盖头 + 全部记录——<see cref="UnifiedCrc"/> 仓库统一工具）：</para>
///   <code>
///   Header    12B  magic "THB1"(4) + version(4) + reserved(4)
///   Record    12+n+m  domainId(4) + fieldLength(4) + valueLength(4) + field(n) + value(m)
///   EndMark   12B  domainId = 0xFFFFFFFF 保留终结标记（len 双零——不计入记录数）
///   Footer    12B  recordCount(8) + crc32c(4，覆盖 Header 与全部记录含 EndMark，Footer 本身除外)
///   </code>
/// <para>★ 导出序：按域升序分组、域内 Ring 地址序（HGETALL 同源交付序）。</para>
/// <para>★ 导入契约：写路径与 HSet 同构（判重/覆盖/容量护栏/侧账），幂等——重复导入不重复计数；
///   last-write = 导入时刻（备份流无写入时刻事实）。★ 流式导入非原子：尾部损坏时已导入前缀
///   不回滚（append-only）——损坏流导入请用新实例。导入面串行使用（内部 _opGate 与写/TTL 互斥）。</para>
/// <para>★ 不自动 Flush：导入完成 = 内存可见（HSet 同语义）；落盘由调用方 <c>FlushAsync</c> 决定。
///   导入目标若启用 TTL，历史数据按导入时刻起算 TTL——冷档实例应配 <c>DomainTtl = null</c>。</para>
/// </summary>
public static class TierHashBackup
{
    private const uint Magic = 0x31424854u;   // "THB1" 小端
    private const uint FormatVersion = 1;
    private const int HeaderSize = 12;
    private const int RecordHeaderSize = 12;   // domainId(4) + fieldLength(4) + valueLength(4)
    private const int FooterSize = 12;
    private const int ImportBatchSize = 512;   // 导入批次上限（同域连续段——单闸窗口一次）

    /// <summary>终结标记域标识（保留值——导出面拒绝该域）。</summary>
    public const uint EndOfRecordsDomainId = uint.MaxValue;

    // ══ 导出 ══

    /// <summary>导出全部已注册域到备份流（域升序、域内 Ring 地址序）。</summary>
    /// <param name="source">源实例。</param>
    /// <param name="destination">目标流（本方法结束前不关闭——调用方管生命周期）。</param>
    /// <param name="ct">取消令牌。</param>
    public static ValueTask ExportAsync(ITierHash source, Stream destination, CancellationToken ct = default)
        => ExportAsync(source, destination, source.DomainIds, ct);

    /// <summary>导出指定域集合到备份流（升序去重交付——流内按域分组、域内地址序）。</summary>
    /// <param name="source">源实例。</param>
    /// <param name="destination">目标流。</param>
    /// <param name="domainIds">域集合。</param>
    /// <param name="ct">取消令牌。</param>
    public static async ValueTask ExportAsync(ITierHash source, Stream destination,
        IEnumerable<uint> domainIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(domainIds);
        if (domainIds.Contains(EndOfRecordsDomainId))
            throw new ArgumentException(
                $"域 {EndOfRecordsDomainId} 为备份流终结标记保留值——不可导出", nameof(domainIds));

        using var writer = new BackupStreamWriter(destination);
        byte[] header = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), FormatVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 0);
        writer.Feed(header);

        byte[] recordHeader = new byte[RecordHeaderSize];
        foreach (var domain in domainIds)
        {
            await foreach (var (field, value) in source.HGetAllAsync(domain, ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                BinaryPrimitives.WriteUInt32LittleEndian(recordHeader, domain);
                BinaryPrimitives.WriteInt32LittleEndian(recordHeader.AsSpan(4), field.Length);
                BinaryPrimitives.WriteInt32LittleEndian(recordHeader.AsSpan(8), value.Length);
                writer.Feed(recordHeader);
                writer.Feed(field.AsSpan());
                writer.Feed(value.AsSpan());
                writer.RecordCount++;
            }
        }

        // 终结标记（不计入记录数——导入侧据此区分记录区结束与 Footer 边界）
        BinaryPrimitives.WriteUInt32LittleEndian(recordHeader, EndOfRecordsDomainId);
        BinaryPrimitives.WriteInt32LittleEndian(recordHeader.AsSpan(4), 0);
        BinaryPrimitives.WriteInt32LittleEndian(recordHeader.AsSpan(8), 0);
        writer.Feed(recordHeader);

        await writer.CompleteAsync(ct).ConfigureAwait(false);
    }

    // ══ 导入 ══

    /// <summary>从备份流导入全部记录到目标实例（逐批重放——幂等，判重/覆盖语义原样生效）。</summary>
    /// <param name="target">目标实例。</param>
    /// <param name="source">备份流（读到 Footer 为止——不关闭）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>导入统计。</returns>
    /// <exception cref="InvalidDataException">magic/version/footer 记录数或 CRC 校验失败——流损坏 fail-fast。
    ///   ★ 流式导入非原子：尾部损坏时已导入前缀不回滚（append-only）——损坏流导入请用新实例。</exception>
    /// <exception cref="InvalidOperationException">容量护栏超限 / DomainMaxBytes 超限（同 HSet 守卫）。</exception>
    public static async ValueTask<TierHashImportResult> ImportAsync(ITierHash target, Stream source,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);
        if (target is not TierHash store)
            throw new NotSupportedException(
                $"导入面由 {nameof(TierHash)} 承担（内部批次导入 + 写闸互斥）——{target.GetType().Name} 不受支持");

        byte[] header = new byte[HeaderSize];
        await ReadExactAsync(source, header, ct).ConfigureAwait(false);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic)
            throw new InvalidDataException($"备份流 magic 不符（0x{BinaryPrimitives.ReadUInt32LittleEndian(header):X8} ≠ 0x{Magic:X8}）——非 THB1 备份流");
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        if (version != FormatVersion)
            throw new InvalidDataException($"备份流版本不符（{version} ≠ {FormatVersion}）——升级工具链后重试");

        using var importer = new BatchImporter(store);
        uint crc = UnifiedCrc.ComputeCrc32C(header);
        byte[] recordHeader = new byte[RecordHeaderSize];
        while (await TryReadExactAsync(source, recordHeader, ct).ConfigureAwait(false))
        {
            uint domain = BinaryPrimitives.ReadUInt32LittleEndian(recordHeader);
            int fieldLength = BinaryPrimitives.ReadInt32LittleEndian(recordHeader.AsSpan(4));
            int valueLength = BinaryPrimitives.ReadInt32LittleEndian(recordHeader.AsSpan(8));

            if (domain == EndOfRecordsDomainId)
            {
                if (fieldLength != 0 || valueLength != 0)
                    throw new InvalidDataException("终结标记损坏（len 非零）——备份流损坏");
                crc = UnifiedCrc.ComputeCrc32C(crc, recordHeader);
                break;
            }
            if (fieldLength < 0 || valueLength < 0)
                throw new InvalidDataException($"记录长度非法（field {fieldLength} / value {valueLength}）——备份流损坏");

            byte[] buffer = ArrayPool<byte>.Shared.Rent(fieldLength + valueLength);
            try
            {
                await ReadExactAsync(source, buffer.AsMemory(0, fieldLength + valueLength), ct).ConfigureAwait(false);
                crc = UnifiedCrc.ComputeCrc32C(crc, recordHeader);
                crc = UnifiedCrc.ComputeCrc32C(crc, buffer.AsSpan(0, fieldLength + valueLength));
                // 租赁数组所有权移交批次（ImportBatchAsync 拷入 Ring 后归还——批内引用在落盘前必须存活）
                await importer.AddAsync(domain, buffer, fieldLength, valueLength, ct).ConfigureAwait(false);
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(buffer);
                throw;
            }
        }

        await importer.FlushAsync(ct).ConfigureAwait(false);

        byte[] footer = new byte[FooterSize];
        await ReadExactAsync(source, footer, ct).ConfigureAwait(false);
        long recordCount = BinaryPrimitives.ReadInt64LittleEndian(footer);
        uint footerCrc = BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(8));
        if (recordCount != importer.RecordCount)
            throw new InvalidDataException($"备份流记录数不符（footer {recordCount} ≠ 实际 {importer.RecordCount}）——流损坏或截断");
        if (footerCrc != crc)
            throw new InvalidDataException($"备份流 CRC 校验失败（footer 0x{footerCrc:X8} ≠ 实算 0x{crc:X8}）——流损坏");
        return new TierHashImportResult(importer.RecordCount, importer.DomainCount);
    }

    /// <summary>精确读满（不足 = 流提前终止——损坏 fail-fast）。</summary>
    private static async ValueTask ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        if (!await TryReadExactAsync(stream, buffer, ct).ConfigureAwait(false))
            throw new EndOfStreamException($"备份流提前终止（需 {buffer.Length}B）——流损坏或截断");
    }

    /// <summary>精确读满（0 字节起始 = false——Footer 边界探测）；部分读 = 流损坏抛出。</summary>
    private static async ValueTask<bool> TryReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[total..], ct).ConfigureAwait(false);
            if (read == 0)
            {
                if (total == 0) return false;
                throw new EndOfStreamException($"备份流记录部分截断（{total}/{buffer.Length}B）——流损坏");
            }
            total += read;
        }
        return true;
    }

    // ══ 导出写缓冲（64KB 聚合 + 增量 CRC）══

    /// <summary>备份流写侧（缓冲聚合零碎小写 + CRC32C 增量；Footer = 记录数 + CRC）。</summary>
    private sealed class BackupStreamWriter : IDisposable
    {
        private readonly Stream _destination;
        private readonly byte[] _buffer = ArrayPool<byte>.Shared.Rent(64 << 10);
        private int _position;
        private uint _crc;

        /// <summary>已写入记录数（Footer 载荷）。</summary>
        public long RecordCount { get; set; }

        public BackupStreamWriter(Stream destination) => _destination = destination;

        /// <summary>喂入字节（CRC 先算——直写大块与缓冲块同覆盖面）。</summary>
        public void Feed(ReadOnlySpan<byte> data)
        {
            _crc = UnifiedCrc.ComputeCrc32C(_crc, data);
            while (!data.IsEmpty)
            {
                int free = _buffer.Length - _position;
                if (free == 0)
                {
                    _destination.Write(_buffer, 0, _position);
                    _position = 0;
                    free = _buffer.Length;
                }
                int take = Math.Min(free, data.Length);
                data[..take].CopyTo(_buffer.AsSpan(_position));
                _position += take;
                data = data[take..];
            }
        }

        /// <summary>收尾：缓冲数据先落流 → Footer 直写（记录数 + CRC——Footer 本身不计入 CRC 覆盖面）→ 流级 flush。</summary>
        public async ValueTask CompleteAsync(CancellationToken ct)
        {
            if (_position > 0)
                await _destination.WriteAsync(_buffer.AsMemory(0, _position), ct).ConfigureAwait(false);
            _position = 0;
            byte[] footer = new byte[FooterSize];
            BinaryPrimitives.WriteInt64LittleEndian(footer, RecordCount);
            BinaryPrimitives.WriteUInt32LittleEndian(footer.AsSpan(8), _crc);
            _destination.Write(footer);
            await _destination.FlushAsync(ct).ConfigureAwait(false);
        }

        public void Dispose() => ArrayPool<byte>.Shared.Return(_buffer);
    }

    // ══ 导入批次聚合（同域连续段——单闸窗口一次）══

    /// <summary>导入批次聚合器：同域连续记录攒批（≤ <see cref="ImportBatchSize"/>），换域/满批/收尾即落。
    /// ★ 持有租赁数组直至批次导入完成（拷入 Ring 后归还——所有权移交避免池复用别名）。</summary>
    private sealed class BatchImporter(TierHash store) : IDisposable
    {
        private readonly List<(byte[] Buffer, int FieldLength, int ValueLength)> _records = new(ImportBatchSize);
        private readonly HashSet<uint> _domains = new();
        private uint _currentDomain = uint.MaxValue;

        /// <summary>已导入记录数。</summary>
        public long RecordCount { get; private set; }

        /// <summary>涉及域数。</summary>
        public int DomainCount => _domains.Count;

        public async ValueTask AddAsync(uint domain, byte[] rentedBuffer, int fieldLength, int valueLength, CancellationToken ct)
        {
            if (_records.Count > 0 && (domain != _currentDomain || _records.Count >= ImportBatchSize))
                await FlushAsync(ct).ConfigureAwait(false);
            _currentDomain = domain;
            _domains.Add(domain);
            _records.Add((rentedBuffer, fieldLength, valueLength));
        }

        public async ValueTask FlushAsync(CancellationToken ct)
        {
            if (_records.Count == 0) return;
            var batch = new (ReadOnlyMemory<byte> Field, ReadOnlyMemory<byte> Value)[_records.Count];
            for (int i = 0; i < _records.Count; i++)
            {
                var (buffer, fieldLength, valueLength) = _records[i];
                batch[i] = (buffer.AsMemory(0, fieldLength), buffer.AsMemory(fieldLength, valueLength));
            }
            try
            {
                RecordCount += await store.ImportBatchAsync(_currentDomain, batch, ct).ConfigureAwait(false);
            }
            finally
            {
                foreach (var (buffer, _, _) in _records)
                    ArrayPool<byte>.Shared.Return(buffer);
                _records.Clear();
            }
        }

        public void Dispose()
        {
            // 异常路径兜底（AddAsync 中途抛出——未 flush 批次的租赁数组归还）
            foreach (var (buffer, _, _) in _records)
                ArrayPool<byte>.Shared.Return(buffer);
            _records.Clear();
        }
    }
}
