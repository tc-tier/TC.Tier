using System.Buffers;
using System.Buffers.Binary;
using TC.Tier.Core.Primitives;

namespace TC.Tier.Products.TimeSeries;

/// <summary>导入结果统计。</summary>
/// <param name="ImportedCount">导入样本总数。</param>
/// <param name="SeriesCount">涉及序列数（流内出现过的不同 seriesId）。</param>
public readonly record struct TimeSeriesImportResult(long ImportedCount, int SeriesCount);

/// <summary>
/// 冷备份导出/导入算子（组合糖——<see cref="TimeSeriesRollup"/> 同族形态）：按时间区间把样本流导出为
/// 自描述备份流，导入端逐批重放（含序列水位回落——恢复对账「数据事实优先」同款语义）。
/// <para>★ 消费形态（#521）：细序列按 retention 淘汰前归档；跨节点迁移。与 Rollup 降采样组合成
///   标准分层：细粒度短保留 → Rollup 粗粒度长保留 → 冷档导出。</para>
/// <para>★ 流格式（全部小端，CRC32C 覆盖头 + 全部记录——<see cref="UnifiedCrc"/> 仓库统一工具）：</para>
///   <code>
///   Header    12B  magic "TSB1"(4) + version(4) + reserved(4)
///   Record    16+n seriesId(4) + timestamp(8, UTC Ticks) + valueLength(4) + value(n)
///   EndMark   16B  seriesId = 0xFFFFFFFF 保留终结标记（ts = 0 / len = 0——不计入记录数）
///   Footer    12B  recordCount(8) + crc32c(4，覆盖 Header 与全部记录含 EndMark，Footer 本身除外)
///   </code>
/// <para>★ 导入契约：写路径与 Append 同构（容量护栏/溢出分流/索引/侧账），唯二差别 = 跳过 live
///   追加守卫（TrimmedUntil/MaxOutOfOrderPast——历史数据不适用）+ 序列水位按导入事实 min 回拉。
///   单序列实例遇非零 seriesId fail-fast（同 Append 守卫）。</para>
/// <para>★ 不自动 Flush：导入完成 = 内存可见（Append 同语义）；落盘由调用方 <c>FlushAsync</c> 决定。
///   导入目标若启用 TTL retention，恢复的历史数据会被下轮 retention 按 TTL 再淘汰——冷档实例应配
///   <c>RetentionTime = null</c>。导入面串行使用（内部 _trimGate 与 trim/其他导入批次互斥）。</para>
/// </summary>
public static class TimeSeriesBackup
{
    private const uint Magic = 0x31425354u;   // "TSB1" 小端
    private const uint FormatVersion = 1;
    private const int HeaderSize = 12;
    private const int RecordHeaderSize = 16;   // seriesId(4) + timestamp(8) + valueLength(4)
    private const int FooterSize = 12;
    private const int ImportBatchSize = 512;   // 导入批次上限（同序列连续段——Ring 批量窗口一次）

    /// <summary>终结标记序列标识（保留值——导出面拒绝该序列）。</summary>
    public const uint EndOfRecordsSeriesId = uint.MaxValue;

    // ══ 导出 ══

    /// <summary>导出全序列 [fromTsInclusive, toTsExclusive) 样本到备份流（dense = 注册表全体升序）。</summary>
    /// <param name="source">源实例。</param>
    /// <param name="destination">目标流（本方法结束前不关闭——调用方管生命周期）。</param>
    /// <param name="fromTsInclusive">起点（含）。</param>
    /// <param name="toTsExclusive">终点（不含）。</param>
    /// <param name="ct">取消令牌。</param>
    public static ValueTask ExportAsync(ITierTimeSeries source, Stream destination,
        long fromTsInclusive, long toTsExclusive, CancellationToken ct = default)
        => ExportAsync(source, destination, fromTsInclusive, toTsExclusive, source.SeriesIds, ct);

    /// <summary>导出指定序列集合的 [fromTsInclusive, toTsExclusive) 样本到备份流。</summary>
    /// <param name="source">源实例。</param>
    /// <param name="destination">目标流。</param>
    /// <param name="fromTsInclusive">起点（含）。</param>
    /// <param name="toTsExclusive">终点（不含）。</param>
    /// <param name="seriesIds">序列集合（升序去重交付——流内按序列分组、组内时间序）。</param>
    /// <param name="ct">取消令牌。</param>
    public static async ValueTask ExportAsync(ITierTimeSeries source, Stream destination,
        long fromTsInclusive, long toTsExclusive, IEnumerable<uint> seriesIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(seriesIds);
        // 空区间/空序列 = 合法空备份流（Header + 零记录 + Footer）——Rollup 家族先例（fromTs ≥ toTs 不抛）
        if (seriesIds.Contains(EndOfRecordsSeriesId))
            throw new ArgumentException(
                $"序列 {EndOfRecordsSeriesId} 为备份流终结标记保留值——不可导出", nameof(seriesIds));

        using var writer = new BackupStreamWriter(destination);
        byte[] header = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), FormatVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 0);
        writer.Feed(header);

        byte[] recordHeader = new byte[RecordHeaderSize];
        foreach (var sid in seriesIds)
        {
            await foreach (var (ts, value, _) in source.RangeAsync(sid, fromTsInclusive, toTsExclusive, ct)
                               .ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                BinaryPrimitives.WriteUInt32LittleEndian(recordHeader, sid);
                BinaryPrimitives.WriteInt64LittleEndian(recordHeader.AsSpan(4), ts);
                BinaryPrimitives.WriteInt32LittleEndian(recordHeader.AsSpan(12), value.Length);
                writer.Feed(recordHeader);
                writer.Feed(value.Span);
                writer.RecordCount++;
            }
        }

        // 终结标记（不计入记录数——导入侧据此区分记录区结束与 Footer 边界）
        BinaryPrimitives.WriteUInt32LittleEndian(recordHeader, EndOfRecordsSeriesId);
        BinaryPrimitives.WriteInt64LittleEndian(recordHeader.AsSpan(4), 0);
        BinaryPrimitives.WriteInt32LittleEndian(recordHeader.AsSpan(12), 0);
        writer.Feed(recordHeader);

        await writer.CompleteAsync(ct).ConfigureAwait(false);
    }

    // ══ 导入 ══

    /// <summary>从备份流导入全部记录到目标实例（逐批重放——序列水位按导入事实 min 回拉）。</summary>
    /// <param name="target">目标实例（同序列实例重放合法——水位回拉使回收边界让位于数据事实）。</param>
    /// <param name="source">备份流（读到 Footer 为止——不关闭）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>导入统计。</returns>
    /// <exception cref="InvalidDataException">magic/version/footer 记录数或 CRC 校验失败——流损坏 fail-fast。
    ///   ★ 流式导入非原子：尾部损坏时已导入前缀不回滚（append-only）——损坏流导入请用新实例。</exception>
    /// <exception cref="InvalidOperationException">单序列实例遇非零 seriesId / dense 容量护栏超限（同 Append 守卫）。</exception>
    public static async ValueTask<TimeSeriesImportResult> ImportAsync(ITierTimeSeries target, Stream source,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);
        if (target is not TierTimeSeries store)
            throw new NotSupportedException(
                $"导入面由 {nameof(TierTimeSeries)} 承担（内部批次导入 + 水位回拉）——{target.GetType().Name} 不受支持");

        byte[] header = new byte[HeaderSize];
        await ReadExactAsync(source, header, ct).ConfigureAwait(false);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic)
            throw new InvalidDataException($"备份流 magic 不符（0x{BinaryPrimitives.ReadUInt32LittleEndian(header):X8} ≠ 0x{Magic:X8}）——非 TSB1 备份流");
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        if (version != FormatVersion)
            throw new InvalidDataException($"备份流版本不符（{version} ≠ {FormatVersion}）——升级工具链后重试");

        using var importer = new BatchImporter(store);
        uint crc = UnifiedCrc.ComputeCrc32C(header);
        byte[] recordHeader = new byte[RecordHeaderSize];
        while (await TryReadExactAsync(source, recordHeader, ct).ConfigureAwait(false))
        {
            uint sid = BinaryPrimitives.ReadUInt32LittleEndian(recordHeader);
            long ts = BinaryPrimitives.ReadInt64LittleEndian(recordHeader.AsSpan(4));
            int length = BinaryPrimitives.ReadInt32LittleEndian(recordHeader.AsSpan(12));

            if (sid == EndOfRecordsSeriesId)
            {
                if (ts != 0 || length != 0)
                    throw new InvalidDataException("终结标记损坏（ts/len 非零）——备份流损坏");
                crc = UnifiedCrc.ComputeCrc32C(crc, recordHeader);
                break;
            }
            if (length < 0)
                throw new InvalidDataException($"记录值长度非法（{length}）——备份流损坏");

            byte[] value = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                await ReadExactAsync(source, value.AsMemory(0, length), ct).ConfigureAwait(false);
                crc = UnifiedCrc.ComputeCrc32C(crc, recordHeader);
                crc = UnifiedCrc.ComputeCrc32C(crc, value.AsSpan(0, length));
                // 租赁数组所有权移交批次（ImportBatchAsync 拷入 Ring 后由 FlushAsync 统一归还——
                // 批内引用在落盘前必须存活，提前归还 = 池复用覆盖别名）
                await importer.AddAsync(sid, ts, value, length, ct).ConfigureAwait(false);
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(value);
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
        return new TimeSeriesImportResult(importer.RecordCount, importer.SeriesCount);
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

    // ══ 导入批次聚合（同序列连续段——Ring 批量窗口一次）══

    /// <summary>导入批次聚合器：同序列连续记录攒批（≤ <see cref="ImportBatchSize"/>），换序列/满批/收尾即落。
    /// ★ 持有样本租赁数组直至批次导入完成（拷入 Ring 后归还——所有权移交避免池复用别名）。</summary>
    private sealed class BatchImporter(TierTimeSeries store) : IDisposable
    {
        private readonly List<(long Timestamp, byte[] Buffer, int Length)> _samples = new(ImportBatchSize);
        private readonly HashSet<uint> _series = new();
        private uint _currentSeries = uint.MaxValue;

        /// <summary>已导入记录数。</summary>
        public long RecordCount { get; private set; }

        /// <summary>涉及序列数。</summary>
        public int SeriesCount => _series.Count;

        public async ValueTask AddAsync(uint seriesId, long timestamp, byte[] rentedBuffer, int length, CancellationToken ct)
        {
            if (_samples.Count > 0 && (seriesId != _currentSeries || _samples.Count >= ImportBatchSize))
                await FlushAsync(ct).ConfigureAwait(false);
            _currentSeries = seriesId;
            _series.Add(seriesId);
            _samples.Add((timestamp, rentedBuffer, length));
        }

        public async ValueTask FlushAsync(CancellationToken ct)
        {
            if (_samples.Count == 0) return;
            var batch = new (long Timestamp, ReadOnlyMemory<byte> Value)[_samples.Count];
            for (int i = 0; i < _samples.Count; i++)
                batch[i] = (_samples[i].Timestamp, _samples[i].Buffer.AsMemory(0, _samples[i].Length));
            try
            {
                (long Written, _) = await store.ImportBatchAsync(_currentSeries, batch, ct).ConfigureAwait(false);
                RecordCount += Written;
            }
            finally
            {
                foreach (var (_, buffer, _) in _samples)
                    ArrayPool<byte>.Shared.Return(buffer);
                _samples.Clear();
            }
        }

        public void Dispose()
        {
            // 异常路径兜底（AddAsync 中途抛出——未 flush 批次的租赁数组归还）
            foreach (var (_, buffer, _) in _samples)
                ArrayPool<byte>.Shared.Return(buffer);
            _samples.Clear();
        }
    }
}
