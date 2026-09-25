using System.Buffers;
using System.Buffers.Binary;
using TC.Tier.Core.Primitives;

namespace TC.Tier.Products.Collections;

/// <summary>导入结果统计。</summary>
/// <param name="ImportedCount">导入成员总数（幂等去重计入——按写路径实际执行数）。</param>
/// <param name="DomainCount">涉及域数（流内出现过的不同 domainId）。</param>
public readonly record struct TierSetImportResult(long ImportedCount, int DomainCount);

/// <summary>
/// TierSet 冷备份导出/导入算子（组合糖——TierHashBackup 同族形态，tc-tier-collections-spec §8）。
/// <para>★ 流格式（全部小端，CRC32C 覆盖头 + 全部记录）：</para>
///   <code>
///   Header    12B  magic "TSE1"(4) + version(4) + reserved(4)
///   Record    8+n  domainId(4) + memberLength(4) + member(n)
///   EndMark   8B   domainId = 0xFFFFFFFF 保留终结标记（len 0——不计入记录数）
///   Footer    12B  recordCount(8) + crc32c(4，覆盖 Header 与全部记录含 EndMark，Footer 本身除外)
///   </code>
/// <para>★ 导出序：按域升序分组、域内 Ring 地址序（SMEMBERS 同源交付序）。</para>
/// <para>★ 导入契约：写路径与 SAdd 同构（幂等判重/容量护栏/侧账）；last-write = 导入时刻。
///   ★ 流式导入非原子：尾部损坏已导入前缀不回滚。导入面串行使用（内部 _opGate）。</para>
/// </summary>
public static class TierSetBackup
{
    private const uint Magic = 0x31455354u;   // "TSE1" 小端（spec §8——Set 备份流魔数）
    private const uint FormatVersion = 1;
    private const int HeaderSize = 12;
    private const int RecordHeaderSize = 8;    // domainId(4) + memberLength(4)
    private const int FooterSize = 12;
    private const int ImportBatchSize = 512;

    /// <summary>终结标记域标识（保留值——导出面拒绝该域）。</summary>
    public const uint EndOfRecordsDomainId = uint.MaxValue;

    // ══ 导出 ══

    /// <summary>导出全部已注册域到备份流（域升序、域内 Ring 地址序）。</summary>
    public static ValueTask ExportAsync(ITierSet source, Stream destination, CancellationToken ct = default)
        => ExportAsync(source, destination, source.DomainIds, ct);

    /// <summary>导出指定域集合到备份流（升序去重交付）。</summary>
    public static async ValueTask ExportAsync(ITierSet source, Stream destination,
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
            await foreach (var member in source.SMembersAsync(domain, ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                BinaryPrimitives.WriteUInt32LittleEndian(recordHeader, domain);
                BinaryPrimitives.WriteInt32LittleEndian(recordHeader.AsSpan(4), member.Length);
                writer.Feed(recordHeader);
                writer.Feed(member.AsSpan());
                writer.RecordCount++;
            }
        }

        BinaryPrimitives.WriteUInt32LittleEndian(recordHeader, EndOfRecordsDomainId);
        BinaryPrimitives.WriteInt32LittleEndian(recordHeader.AsSpan(4), 0);
        writer.Feed(recordHeader);

        await writer.CompleteAsync(ct).ConfigureAwait(false);
    }

    // ══ 导入 ══

    /// <summary>从备份流导入全部成员到目标实例（逐批重放——幂等判重）。</summary>
    /// <exception cref="InvalidDataException">magic/version/footer 记录数或 CRC 校验失败——流损坏 fail-fast。
    ///   ★ 流式导入非原子：尾部损坏时已导入前缀不回滚。</exception>
    /// <exception cref="InvalidOperationException">容量护栏超限 / DomainMaxBytes 超限（同 SAdd 守卫）。</exception>
    public static async ValueTask<TierSetImportResult> ImportAsync(ITierSet target, Stream source,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);
        if (target is not TierSet store)
            throw new NotSupportedException(
                $"导入面由 {nameof(TierSet)} 承担（内部批次导入 + 写闸互斥）——{target.GetType().Name} 不受支持");

        byte[] header = new byte[HeaderSize];
        await ReadExactAsync(source, header, ct).ConfigureAwait(false);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic)
            throw new InvalidDataException($"备份流 magic 不符（0x{BinaryPrimitives.ReadUInt32LittleEndian(header):X8} ≠ 0x{Magic:X8}）——非 TSE1 备份流");
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        if (version != FormatVersion)
            throw new InvalidDataException($"备份流版本不符（{version} ≠ {FormatVersion}）——升级工具链后重试");

        using var importer = new BatchImporter(store);
        uint crc = UnifiedCrc.ComputeCrc32C(header);
        byte[] recordHeader = new byte[RecordHeaderSize];
        while (await TryReadExactAsync(source, recordHeader, ct).ConfigureAwait(false))
        {
            uint domain = BinaryPrimitives.ReadUInt32LittleEndian(recordHeader);
            int memberLength = BinaryPrimitives.ReadInt32LittleEndian(recordHeader.AsSpan(4));

            if (domain == EndOfRecordsDomainId)
            {
                if (memberLength != 0)
                    throw new InvalidDataException("终结标记损坏（len 非零）——备份流损坏");
                crc = UnifiedCrc.ComputeCrc32C(crc, recordHeader);
                break;
            }
            if (memberLength < 0)
                throw new InvalidDataException($"记录 member 长度非法（{memberLength}）——备份流损坏");

            byte[] buffer = ArrayPool<byte>.Shared.Rent(memberLength);
            try
            {
                await ReadExactAsync(source, buffer.AsMemory(0, memberLength), ct).ConfigureAwait(false);
                crc = UnifiedCrc.ComputeCrc32C(crc, recordHeader);
                crc = UnifiedCrc.ComputeCrc32C(crc, buffer.AsSpan(0, memberLength));
                await importer.AddAsync(domain, buffer, memberLength, ct).ConfigureAwait(false);
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
        return new TierSetImportResult(importer.RecordCount, importer.DomainCount);
    }

    private static async ValueTask ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        if (!await TryReadExactAsync(stream, buffer, ct).ConfigureAwait(false))
            throw new EndOfStreamException($"备份流提前终止（需 {buffer.Length}B）——流损坏或截断");
    }

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

    private sealed class BackupStreamWriter : IDisposable
    {
        private readonly Stream _destination;
        private readonly byte[] _buffer = ArrayPool<byte>.Shared.Rent(64 << 10);
        private int _position;
        private uint _crc;

        public long RecordCount { get; set; }

        public BackupStreamWriter(Stream destination) => _destination = destination;

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

    // ══ 导入批次聚合（同域连续段）══

    private sealed class BatchImporter(TierSet store) : IDisposable
    {
        private readonly List<(byte[] Buffer, int Length)> _members = new(ImportBatchSize);
        private readonly HashSet<uint> _domains = new();
        private uint _currentDomain = uint.MaxValue;

        public long RecordCount { get; private set; }

        public int DomainCount => _domains.Count;

        public async ValueTask AddAsync(uint domain, byte[] rentedBuffer, int length, CancellationToken ct)
        {
            if (_members.Count > 0 && (domain != _currentDomain || _members.Count >= ImportBatchSize))
                await FlushAsync(ct).ConfigureAwait(false);
            _currentDomain = domain;
            _domains.Add(domain);
            _members.Add((rentedBuffer, length));
        }

        public async ValueTask FlushAsync(CancellationToken ct)
        {
            if (_members.Count == 0) return;
            var batch = new ReadOnlyMemory<byte>[_members.Count];
            for (int i = 0; i < _members.Count; i++)
                batch[i] = _members[i].Buffer.AsMemory(0, _members[i].Length);
            try
            {
                RecordCount += await store.ImportBatchAsync(_currentDomain, batch, ct).ConfigureAwait(false);
            }
            finally
            {
                foreach (var (buffer, _) in _members)
                    ArrayPool<byte>.Shared.Return(buffer);
                _members.Clear();
            }
        }

        public void Dispose()
        {
            foreach (var (buffer, _) in _members)
                ArrayPool<byte>.Shared.Return(buffer);
            _members.Clear();
        }
    }
}
