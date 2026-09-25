using TC.Tier.Contracts.Storage;
using TC.Tier.Runtime.Structures.Ring;
using TC.Tier.Runtime.Structures.Ring.Contracts;
using TC.Tier.Runtime.Structures.SortedIndex;
using TC.Tier.Runtime.Structures.SortedIndex.Contracts;

namespace TC.Tier.Products.TimeSeries;

/// <summary>
/// 时序键域抽象（#443 设计稿 §2/§5——键类型擦除门面）：把「键构造 + Ring 写读 + 索引键域操作 +
/// envelope 编解码」从流程逻辑中擦出，<see cref="TierTimeSeries"/> 流程代码单份共享两种模式——
/// 单序列（TimeKey 16B）与稠密多序列（DenseTimeKey 20B，逐序列 = 键前缀域）。
/// <para>★ 生成封闭类型只在此层出现（RingOfTimeKey/BTreeOfTimeKey vs RingOfDenseTimeKey/
///   BTreeOfDenseTimeKey）——封闭形态不落流程面。</para>
/// </summary>
internal interface ITimeSeriesKeySpace
{
    /// <summary>dense 模式（DenseTimeKey 键域）。</summary>
    bool Dense { get; }

    // ══ envelope（TTS1 单序列 / TTS2 dense——SeriesId 入头）══

    /// <summary>envelope 固定头长（TTS1 = 21B；TTS2 = 25B）。</summary>
    int EnvelopeHeaderSize { get; }

    /// <summary>编码 envelope 头（Ring scatter 写——头/值分离零拼接）。</summary>
    void WriteEnvelopeHeader(Span<byte> destination, uint seriesId, long timestamp, long? seq);

    /// <summary>解样本（envelope 权威 ts；dense 校验头 SeriesId == 预期——损坏 fail-fast null）。</summary>
    bool TryReadSample(ReadOnlySpan<byte> envelope, uint expectedSeriesId, out long timestamp, out byte[] payload);

    // ══ 写路径（Ring record + 索引写后即知插入归流程层调）══

    /// <summary>同步内联写（record key = (sid, ts, 0)——分类键；envelope 头 + payload 分离写）。</summary>
    LogicalAddress WriteRecord(uint seriesId, long timestamp, ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload);

    /// <summary>异步整段写（超大值溢出路径——header+payload 已拼接的单块）。</summary>
    ValueTask<LogicalAddress> WriteRecordAsync(uint seriesId, long timestamp, ReadOnlyMemory<byte> envelope, CancellationToken ct);

    /// <summary>
    /// Ring 批量窗口写（tail 锁每批一次；ref struct 窗口封在本方法内——键擦除面不跨 await）。
    /// 守卫经 <paramref name="validate"/> 逐条先行注入（批内第 k 条违规抛出时前 k-1 已写入——既有语义）。
    /// </summary>
    LogicalAddress[] WriteBatch(ReadOnlySpan<BatchSample> samples, Action<uint, long> validate, CancellationToken ct);

    /// <summary>读单条样本（冷热透明异步回源；非本格式/损坏 = null）。</summary>
    ValueTask<(long Ts, byte[] Payload)?> ReadSampleAsync(LogicalAddress addr, uint expectedSeriesId, CancellationToken ct);

    /// <summary>Ring 流扫描（地址序；分类键不解析——样本语义由 <see cref="ScanSamplesAsync"/> 承担）。</summary>
    IAsyncEnumerable<(LogicalAddress Addr, bool Tombstone)> ScanRecordsAsync(
        LogicalAddress begin, LogicalAddress end, CancellationToken ct = default);

    /// <summary>全环样本流（envelope 权威解析 + payload 一次读齐——恢复重放数据面 / 降档扫描共用；非本产品 record 跳过）。</summary>
    IAsyncEnumerable<(long Ts, uint Sid, byte[] Payload, LogicalAddress Addr)> ScanSamplesAsync(CancellationToken ct = default);

    // ══ Ring 几何 / 水位 ══

    LogicalAddress BeginAddress { get; }
    LogicalAddress TailAddress { get; }
    LogicalAddress FlushedUntilAddress { get; }
    long GetByteDistance(LogicalAddress from, LogicalAddress to);
    ValueTask FlushUntilAsync(LogicalAddress target, CancellationToken ct);

    /// <summary>Ring 前缀截断（≤ FlushedUntilAddress 守卫由既有 Ring 语义承担）。</summary>
    void TruncateRecordsPrefix(LogicalAddress target);

    // ══ Ring / 索引生命周期（结构由 Builder 装配、键域持有、流程层编排）══

    /// <summary>Ring 启动（OnInitializeBegin 并行阶段——与水位 meta 并行）。</summary>
    void InitializeRing();

    /// <summary>Ring 就绪等待（恢复 join 第一腿）。</summary>
    ValueTask WaitForRingReadyAsync(CancellationToken ct);

    /// <summary>Ring + 索引结构释放（流程层 Dispose 调——索引先于 Ring，对齐既有 Resources LIFO 序）。</summary>
    void DisposeStructures();

    // ══ 索引（单树共享；dense 逐序列 = 键前缀域）══

    /// <summary>索引装配开关（Indexed=false 降档 = 纯追加）。</summary>
    bool HasIndex { get; }

    /// <summary>索引条目数（全实例——含全部序列域）。</summary>
    long IndexEntryCount { get; }

    /// <summary>索引启动（Ring Ready 后——resolver 数据面可用；Queue 幂等索引同款时序）。</summary>
    void InitializeIndex(LogicalAddress begin, LogicalAddress tail);

    ValueTask WaitForIndexReadyAsync(CancellationToken ct);

    /// <summary>索引插入（键 (sid, ts, addr.Offset)——写后即知）。</summary>
    void InsertIndex(uint seriesId, long timestamp, LogicalAddress addr);

    /// <summary>域内最早样本（dense = seek (s, min) 单步出域校验；单序列 = 全局首键）。</summary>
    bool TryGetFirst(uint seriesId, out long timestamp);

    /// <summary>域内最新样本（dense = floor (s, max) 出域校验；单序列 = 全局最大键）。</summary>
    bool TryGetLatest(uint seriesId, out long timestamp, out LogicalAddress addr);

    /// <summary>≤ ts 的最近样本（域内 floor——同刻取地址最大 = 写入序末条）。</summary>
    bool TryGetFloor(uint seriesId, long ts, out long floorTs, out LogicalAddress addr);

    /// <summary>域内首个 key ≥ (sid, ts) 的地址（trim bound 反查——无命中 false）。</summary>
    bool TrySeekLowerBound(uint seriesId, long fromInclusive, out LogicalAddress addr);

    /// <summary>精确键删除（恢复悬空清理——tiebreaker 全键定位）。</summary>
    bool DeleteIndexKey(uint seriesId, long timestamp, long tiebreaker);

    /// <summary>序列内前缀截断（本序列域内 ts &lt; before 的条目批量删——返回删除数）。
    /// dense = 双侧界住本序列键域 [sid 域起点, (sid, before, min))——共享键空间含领先 sid 字段，
    /// 单侧全局前缀会把更低 sid 序列的存活条目一并判入前缀，必须 TruncateRange 双侧收口。</summary>
    long TruncateIndexPrefix(uint seriesId, long beforeExclusive);

    /// <summary>索引游标（同步迭代——恢复对账/字节反查/统计的既有形态）。</summary>
    ITimeSeriesIndexCursor CreateIndexCursor();
}

/// <summary>批量样本（<see cref="ITimeSeriesKeySpace.WriteBatch"/> 入参——sid/ts/值三元）。</summary>
/// <param name="SeriesId">序列标识（单序列恒 0）。</param>
/// <param name="Timestamp">样本时刻（UTC Ticks）。</param>
/// <param name="Value">样本值。</param>
internal readonly record struct BatchSample(uint SeriesId, long Timestamp, ReadOnlyMemory<byte> Value);

/// <summary>键擦除索引游标（BTree 游标转发——流程层只读 (sid, ts, addr) 三元）。</summary>
internal interface ITimeSeriesIndexCursor : IDisposable
{
    /// <summary>定位首条 key ≥ (sid, ts)——false = 无（游标不可用）。</summary>
    bool SeekLowerBound(uint seriesId, long fromInclusive);

    /// <summary>游标已定位后前/后进一步（方向由创建参数决定——本产品只用 Forward）。</summary>
    bool MoveNext();

    long CurrentTimestamp { get; }
    uint CurrentSeriesId { get; }
    long CurrentTiebreaker { get; }
    LogicalAddress CurrentAddress { get; }
}

/// <summary>
/// 单序列键域（TimeKey 16B——既有行为零迁移）：record key (ts, 0) / 索引键 (ts, addr.Offset)，
/// envelope TTS1（21B 头），SeriesId 参数仅 0 合法（默认序列——流程层守卫）。
/// </summary>
internal sealed class SingleSeriesKeySpace(RingOfTimeKey ring, BTreeOfTimeKey? index) : ITimeSeriesKeySpace
{
    private const uint DefaultSeriesId = 0;

    public bool Dense => false;
    public int EnvelopeHeaderSize => TimeSeriesEnvelope.HeaderSize;

    public void WriteEnvelopeHeader(Span<byte> destination, uint seriesId, long timestamp, long? seq)
        => TimeSeriesEnvelope.WriteHeader(destination, timestamp, seq);

    public bool TryReadSample(ReadOnlySpan<byte> envelope, uint expectedSeriesId, out long timestamp, out byte[] payload)
        => TimeSeriesEnvelope.TryUnwrap(envelope, out _, out timestamp, out _, out payload);

    public LogicalAddress WriteRecord(uint seriesId, long timestamp, ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload)
        => ring.Write(new TimeKey(timestamp, 0), header, payload);

    public ValueTask<LogicalAddress> WriteRecordAsync(uint seriesId, long timestamp, ReadOnlyMemory<byte> envelope, CancellationToken ct)
        => ring.WriteAsync(new TimeKey(timestamp, 0), envelope, ct);

    public LogicalAddress[] WriteBatch(ReadOnlySpan<BatchSample> samples, Action<uint, long> validate, CancellationToken ct)
    {
        var results = new LogicalAddress[samples.Length];
        using var batch = ring.BeginWriteBatch();
        Span<byte> header = stackalloc byte[TimeSeriesEnvelope.HeaderSize];
        for (int i = 0; i < samples.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            validate(samples[i].SeriesId, samples[i].Timestamp);
            TimeSeriesEnvelope.WriteHeader(header, samples[i].Timestamp, null);
            results[i] = batch.Append(new TimeKey(samples[i].Timestamp, 0), header, samples[i].Value.Span);
        }
        return results;
    }

    public async ValueTask<(long Ts, byte[] Payload)?> ReadSampleAsync(LogicalAddress addr, uint expectedSeriesId, CancellationToken ct)
    {
        var recordKey = await ring.GetKeyAsync(addr, ct).ConfigureAwait(false);
        var buf = new byte[recordKey.ValueLength];
        if (buf.Length == 0) return null;
        await ring.GetValueAsync(addr, buf, ct).ConfigureAwait(false);
        if (!TryReadSample(buf, expectedSeriesId, out var ts, out var payload)) return null;
        return (ts, payload);
    }

    public async IAsyncEnumerable<(LogicalAddress Addr, bool Tombstone)> ScanRecordsAsync(
        LogicalAddress begin, LogicalAddress end,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var (_, addr, tomb) in ring.ScanAsync(begin, end, ct).ConfigureAwait(false))
            yield return (addr, tomb);
    }

    public async IAsyncEnumerable<(long Ts, uint Sid, byte[] Payload, LogicalAddress Addr)> ScanSamplesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var (_, addr, tomb) in ring.ScanAsync(ct).ConfigureAwait(false))
        {
            if (tomb) continue;
            var sample = await ReadSampleAsync(addr, DefaultSeriesId, ct).ConfigureAwait(false);
            if (sample is { } s) yield return (s.Ts, DefaultSeriesId, s.Payload, addr);
        }
    }

    public void InitializeRing() => ring.Initialize();
    public ValueTask WaitForRingReadyAsync(CancellationToken ct) => new(ring.WaitForReadyAsync(ct));
    public void DisposeStructures()
    {
        index?.Dispose();
        ring.Dispose();
    }

    public LogicalAddress BeginAddress => ring.BeginAddress;
    public LogicalAddress TailAddress => ring.TailAddress;
    public LogicalAddress FlushedUntilAddress => ring.FlushedUntilAddress;
    public long GetByteDistance(LogicalAddress from, LogicalAddress to) => ring.GetByteDistance(from, to);
    public ValueTask FlushUntilAsync(LogicalAddress target, CancellationToken ct) => ring.FlushUntilAsync(target, ct);
    public void TruncateRecordsPrefix(LogicalAddress target) => ring.TruncatePrefix(target);

    public bool HasIndex => index is not null;
    public long IndexEntryCount => index?.EntryCount ?? 0;
    public void InitializeIndex(LogicalAddress begin, LogicalAddress tail)
        => index?.Initialize(new SortedIndexRecoveryHints(begin, tail));
    public ValueTask WaitForIndexReadyAsync(CancellationToken ct)
        => index is { } idx ? new ValueTask(idx.WaitForReadyAsync(ct)) : ValueTask.CompletedTask;

    public void InsertIndex(uint seriesId, long timestamp, LogicalAddress addr)
    {
        if (index is not { } idx) return;
        idx.Insert(new TimeKey(timestamp, addr.Offset), addr, idx.BeginAddress);
    }

    public bool TryGetFirst(uint seriesId, out long timestamp)
    {
        timestamp = default;
        if (index is not { } idx) return false;
        using var cursor = idx.CreateScanCursor(ReadDirection.Forward);
        if (!cursor.MoveNext()) return false;
        timestamp = cursor.CurrentKey.Timestamp;
        return true;
    }

    public bool TryGetLatest(uint seriesId, out long timestamp, out LogicalAddress addr)
    {
        timestamp = default; addr = default;
        if (index is not { } idx || !idx.TryGetMax(out var key, out addr)) return false;
        timestamp = key.Timestamp;
        return true;
    }

    public bool TryGetFloor(uint seriesId, long ts, out long floorTs, out LogicalAddress addr)
    {
        floorTs = default; addr = default;
        if (index is not { } idx || !idx.TryGetFloor(new TimeKey(ts, long.MaxValue), out var floorKey, out addr))
            return false;
        floorTs = floorKey.Timestamp;
        return true;
    }

    public bool TrySeekLowerBound(uint seriesId, long fromInclusive, out LogicalAddress addr)
    {
        addr = default;
        if (index is not { } idx) return false;
        using var cursor = idx.CreateScanCursor(ReadDirection.Forward);
        if (!cursor.SeekLowerBound(new TimeKey(fromInclusive, long.MinValue))) return false;
        addr = cursor.CurrentValue;
        return true;
    }

    public bool DeleteIndexKey(uint seriesId, long timestamp, long tiebreaker)
        => index is { } idx && idx.Delete(new TimeKey(timestamp, tiebreaker));

    public long TruncateIndexPrefix(uint seriesId, long beforeExclusive)
        => index?.TruncatePrefix(new TimeKey(beforeExclusive, long.MinValue)) ?? 0;

    public ITimeSeriesIndexCursor CreateIndexCursor()
        => index is { } idx ? new CursorAdapter(idx.CreateScanCursor(ReadDirection.Forward))
                            : new EmptyCursor();

    // ══ 键擦除薄壳 ══

    private sealed class CursorAdapter(IIndexScanCursor<TimeKey> inner) : ITimeSeriesIndexCursor
    {
        public bool SeekLowerBound(uint seriesId, long fromInclusive)
            => inner.SeekLowerBound(new TimeKey(fromInclusive, long.MinValue));
        public bool MoveNext() => inner.MoveNext();
        public long CurrentTimestamp => inner.CurrentKey.Timestamp;
        public uint CurrentSeriesId => DefaultSeriesId;
        public long CurrentTiebreaker => inner.CurrentKey.Tiebreaker;
        public LogicalAddress CurrentAddress => inner.CurrentValue;
        public void Dispose() => inner.Dispose();
    }

    private sealed class EmptyCursor : ITimeSeriesIndexCursor
    {
        public bool SeekLowerBound(uint seriesId, long fromInclusive) => false;
        public bool MoveNext() => false;
        public long CurrentTimestamp => default;
        public uint CurrentSeriesId => default;
        public long CurrentTiebreaker => default;
        public LogicalAddress CurrentAddress => default;
        public void Dispose() { }
    }
}

/// <summary>空索引游标（Indexed=false 降档——CreateIndexCursor 兜底）。</summary>
internal sealed class EmptyIndexCursor : ITimeSeriesIndexCursor
{
    public bool SeekLowerBound(uint seriesId, long fromInclusive) => false;
    public bool MoveNext() => false;
    public long CurrentTimestamp => default;
    public uint CurrentSeriesId => default;
    public long CurrentTiebreaker => default;
    public LogicalAddress CurrentAddress => default;
    public void Dispose() { }
}

/// <summary>
/// 稠密多序列键域（DenseTimeKey 20B——#443 设计稿 §3/§5.1）：record key (sid, ts, 0) /
/// 索引键 (sid, ts, addr.Offset)，envelope TTS2（25B 头 SeriesId 入头）。
/// <para>★ SeriesId 领先字节 → 单树内逐序列键域连续：域内 seek/迭代出域即停（前缀域语义）。</para>
/// </summary>
internal sealed class DenseSeriesKeySpace(RingOfDenseTimeKey ring, BTreeOfDenseTimeKey? index) : ITimeSeriesKeySpace
{
    public bool Dense => true;
    public int EnvelopeHeaderSize => TimeSeriesEnvelope.DenseHeaderSize;

    public void WriteEnvelopeHeader(Span<byte> destination, uint seriesId, long timestamp, long? seq)
        => TimeSeriesEnvelope.WriteDenseHeader(destination, seriesId, timestamp, seq);

    public bool TryReadSample(ReadOnlySpan<byte> envelope, uint expectedSeriesId, out long timestamp, out byte[] payload)
    {
        timestamp = default;
        payload = Array.Empty<byte>();
        if (!TimeSeriesEnvelope.TryUnwrapDense(envelope, out _, out var sid, out timestamp, out _, out payload))
            return false;
        if (sid != expectedSeriesId) return false;   // 键域 sid ≠ 头 sid = 损坏 fail-fast（读侧冗余校验）
        return true;
    }

    public LogicalAddress WriteRecord(uint seriesId, long timestamp, ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload)
        => ring.Write(new DenseTimeKey(seriesId, timestamp, 0), header, payload);

    public ValueTask<LogicalAddress> WriteRecordAsync(uint seriesId, long timestamp, ReadOnlyMemory<byte> envelope, CancellationToken ct)
        => ring.WriteAsync(new DenseTimeKey(seriesId, timestamp, 0), envelope, ct);

    public LogicalAddress[] WriteBatch(ReadOnlySpan<BatchSample> samples, Action<uint, long> validate, CancellationToken ct)
    {
        var results = new LogicalAddress[samples.Length];
        using var batch = ring.BeginWriteBatch();
        Span<byte> header = stackalloc byte[TimeSeriesEnvelope.DenseHeaderSize];
        for (int i = 0; i < samples.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            validate(samples[i].SeriesId, samples[i].Timestamp);
            TimeSeriesEnvelope.WriteDenseHeader(header, samples[i].SeriesId, samples[i].Timestamp, null);
            results[i] = batch.Append(
                new DenseTimeKey(samples[i].SeriesId, samples[i].Timestamp, 0),
                header, samples[i].Value.Span);
        }
        return results;
    }

    public async ValueTask<(long Ts, byte[] Payload)?> ReadSampleAsync(LogicalAddress addr, uint expectedSeriesId, CancellationToken ct)
    {
        var recordKey = await ring.GetKeyAsync(addr, ct).ConfigureAwait(false);
        var buf = new byte[recordKey.ValueLength];
        if (buf.Length == 0) return null;
        await ring.GetValueAsync(addr, buf, ct).ConfigureAwait(false);
        if (!TryReadSample(buf, expectedSeriesId, out var ts, out var payload)) return null;
        return (ts, payload);
    }

    public async IAsyncEnumerable<(LogicalAddress Addr, bool Tombstone)> ScanRecordsAsync(
        LogicalAddress begin, LogicalAddress end,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var (_, addr, tomb) in ring.ScanAsync(begin, end, ct).ConfigureAwait(false))
            yield return (addr, tomb);
    }

    public async IAsyncEnumerable<(long Ts, uint Sid, byte[] Payload, LogicalAddress Addr)> ScanSamplesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var (_, addr, tomb) in ring.ScanAsync(ct).ConfigureAwait(false))
        {
            if (tomb) continue;
            var recordKey = ring.GetKey(addr);
            if (recordKey.ValueLength < TimeSeriesEnvelope.DenseHeaderSize) continue;
            var buf = new byte[recordKey.ValueLength];
            if (ring.GetValue(addr, buf) < TimeSeriesEnvelope.DenseHeaderSize) continue;
            if (!TimeSeriesEnvelope.TryUnwrapDense(buf, out _, out var sid, out var ts, out _, out var payload)) continue;
            yield return (ts, sid, payload, addr);
        }
    }

    public void InitializeRing() => ring.Initialize();
    public ValueTask WaitForRingReadyAsync(CancellationToken ct) => new(ring.WaitForReadyAsync(ct));
    public void DisposeStructures()
    {
        index?.Dispose();
        ring.Dispose();
    }

    public LogicalAddress BeginAddress => ring.BeginAddress;
    public LogicalAddress TailAddress => ring.TailAddress;
    public LogicalAddress FlushedUntilAddress => ring.FlushedUntilAddress;
    public long GetByteDistance(LogicalAddress from, LogicalAddress to) => ring.GetByteDistance(from, to);
    public ValueTask FlushUntilAsync(LogicalAddress target, CancellationToken ct) => ring.FlushUntilAsync(target, ct);
    public void TruncateRecordsPrefix(LogicalAddress target) => ring.TruncatePrefix(target);

    public bool HasIndex => index is not null;
    public long IndexEntryCount => index?.EntryCount ?? 0;
    public void InitializeIndex(LogicalAddress begin, LogicalAddress tail)
        => index?.Initialize(new SortedIndexRecoveryHints(begin, tail));
    public ValueTask WaitForIndexReadyAsync(CancellationToken ct)
        => index is { } idx ? new ValueTask(idx.WaitForReadyAsync(ct)) : ValueTask.CompletedTask;

    public void InsertIndex(uint seriesId, long timestamp, LogicalAddress addr)
    {
        if (index is not { } idx) return;
        idx.Insert(new DenseTimeKey(seriesId, timestamp, addr.Offset), addr, idx.BeginAddress);
    }

    public bool TryGetFirst(uint seriesId, out long timestamp)
    {
        timestamp = default;
        if (index is not { } idx) return false;
        using var cursor = idx.CreateScanCursor(ReadDirection.Forward);
        if (!cursor.SeekLowerBound(new DenseTimeKey(seriesId, long.MinValue, long.MinValue))) return false;
        if (cursor.CurrentKey.SeriesId != seriesId) return false;   // 出域 = 该序列无条目
        timestamp = cursor.CurrentKey.Timestamp;
        return true;
    }

    public bool TryGetLatest(uint seriesId, out long timestamp, out LogicalAddress addr)
    {
        timestamp = default; addr = default;
        if (index is not { } idx) return false;
        // floor (s, max, max) = 域内最大键（出域 = 该序列无条目）
        if (!idx.TryGetFloor(new DenseTimeKey(seriesId, long.MaxValue, long.MaxValue), out var key, out addr))
            return false;
        if (key.SeriesId != seriesId) return false;
        timestamp = key.Timestamp;
        return true;
    }

    public bool TryGetFloor(uint seriesId, long ts, out long floorTs, out LogicalAddress addr)
    {
        floorTs = default; addr = default;
        if (index is not { } idx) return false;
        if (!idx.TryGetFloor(new DenseTimeKey(seriesId, ts, long.MaxValue), out var floorKey, out addr))
            return false;
        if (floorKey.SeriesId != seriesId) return false;   // 出域 = 域内无 ≤ ts 样本
        floorTs = floorKey.Timestamp;
        return true;
    }

    public bool TrySeekLowerBound(uint seriesId, long fromInclusive, out LogicalAddress addr)
    {
        addr = default;
        if (index is not { } idx) return false;
        using var cursor = idx.CreateScanCursor(ReadDirection.Forward);
        if (!cursor.SeekLowerBound(new DenseTimeKey(seriesId, fromInclusive, long.MinValue))) return false;
        if (cursor.CurrentKey.SeriesId != seriesId) return false;
        addr = cursor.CurrentValue;
        return true;
    }

    public bool DeleteIndexKey(uint seriesId, long timestamp, long tiebreaker)
        => index is { } idx && idx.Delete(new DenseTimeKey(seriesId, timestamp, tiebreaker));

    public long TruncateIndexPrefix(uint seriesId, long beforeExclusive)
        => index?.TruncateRange(
            new DenseTimeKey(seriesId, long.MinValue, long.MinValue),
            new DenseTimeKey(seriesId, beforeExclusive, long.MinValue)) ?? 0;

    public ITimeSeriesIndexCursor CreateIndexCursor()
        => index is { } idx ? new DenseCursorAdapter(idx.CreateScanCursor(ReadDirection.Forward))
                            : new EmptyIndexCursor();

    private sealed class DenseCursorAdapter(IIndexScanCursor<DenseTimeKey> inner) : ITimeSeriesIndexCursor
    {
        public bool SeekLowerBound(uint seriesId, long fromInclusive)
            => inner.SeekLowerBound(new DenseTimeKey(seriesId, fromInclusive, long.MinValue));
        public bool MoveNext() => inner.MoveNext();
        public long CurrentTimestamp => inner.CurrentKey.Timestamp;
        public uint CurrentSeriesId => inner.CurrentKey.SeriesId;
        public long CurrentTiebreaker => inner.CurrentKey.Tiebreaker;
        public LogicalAddress CurrentAddress => inner.CurrentValue;
        public void Dispose() => inner.Dispose();
    }
}
