using System.Buffers;
using System.Runtime.CompilerServices;
using TC.Tier.Contracts.Lifecycle;
using TC.Tier.Contracts.Storage;
using TC.Tier.Contracts.Transactions;
using TC.Tier.Core.Execution;
using TC.Tier.Core.Logging;
using TC.Tier.Core.Lifecycle;
using TC.Tier.Core.Resources;
using TC.Tier.Runtime.Storage;
using TC.Tier.Runtime.Structures.Metadata;
using TC.Tier.Runtime.Structures.Ring.Contracts;
using TC.Tier.Runtime.Structures.SortedIndex.Contracts;

namespace TC.Tier.Products.TimeSeries;

/// <summary>
/// TierTimeSeries——本地时序序列（tc-tier-timeseries-spec：Ring×SortedIndex 组合特化，零新结构）。
/// <para>★ 组合配方（spec §1 四件积木）：RingOfTimeKey/RingOfDenseTimeKey（数据真相源——record key
///   只做分类，append-only）+ BTree 时间索引（键 (ts, addr) 写后即知，乱序吸收/范围序/最新点查）+
///   VersionedMetadata（水位真相源——retention 边界原子持久）+ Session 域（可选 2PC 经
///   <see cref="GetWatermarkParticipant"/>）。键类型由 <see cref="ITimeSeriesKeySpace"/> 擦除——
///   单序列（TimeKey 16B）与稠密多序列（DenseTimeKey 20B，#443 设计稿）共用本流程面。</para>
/// <para>★ dense 模式（<see cref="TimeSeriesOptions.DenseSeries"/>，#443 设计稿 §2）：单实例共享全部
///   重资源（Ring 页池/索引树体/水位引擎文件各一份）；每序列成本 = O(1) 侧账（SeriesEntry ~64B，
///   路由 + 统计 + 水位缓存）；逐序列 = 单树键前缀域（SeriesId 领先字节）。两参 API 落 seriesId 0
///   默认序列（零迁移——设计稿裁决点 3）。</para>
/// <para>★ 线程模型：多生产者并发 Append（Ring tail 串行 + BTree 操作闸）；trim 经
///   <see cref="_trimGate"/> 串行（索引清理 → Ring 截断 → 水位持久顺序敏感）；查询无锁
///   （BTree epoch 读保护 + Ring 冷热透明）。</para>
/// <para>★ retention（spec §5）：显式 TruncateAsync + 后台自动（RetentionTime TTL / MaxBytes 字节上限）；
///   截断先行、水位随后，崩溃窗口由恢复对账收口（spec §7）。dense：TTL 逐序列推进（O(活跃序列)，
///   惰性容忍长尾）；Ring 截断下限 = 全体序列钉住地址的 min（慢序列钉住——设计稿 §5.4）。</para>
/// </summary>
public sealed class TierTimeSeries : LifecycleBase<TimeSeriesRecoveryHints>, ITierTimeSeries
{
    private const uint DefaultSeriesId = ITierTimeSeries.DefaultSeriesId;

    private readonly ITimeSeriesKeySpace _keys;
    private readonly VersionedMetadata _watermark;
    private readonly SeriesRegistry? _registry;   // dense only——O(1) 路由/统计/水位缓存（每序列唯一成本）
    private readonly TimeSeriesOptions _options;
    private readonly TimeProvider _clock;   // 时钟供给源（时钟缝 件一 P1——保留扫描/乱序下界）
    private readonly IFileSystem _fs;   // 保留：后续降档/运维路径的介质面
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _trimGate = new(1, 1);   // trim 串行（索引/Ring/水位三步原子性窗口）

    private BackgroundWorkerLoop? _retentionWorker;

    // === 内存镜像（真相源分别是索引/水位 meta；镜像供守卫与降档统计）===
    // dense 语义：_trimmedUntil = series 0（默认序列）边界；_sampleCount = 全实例合计
    private long _trimmedUntil;         // 回收边界（trim/恢复写，Append 读）
    private long _sampleCount;          // 存活样本计数（trim/恢复/Append 增量维护）
    private long _watermarkEpoch;       // 水位提交代次
    private long _firstTs = -1;         // 最早样本时刻（-1 = 未知；ts=UTC Ticks 恒 ≥ 0，-1 安全哨兵）
    private long _lastTs = -1;          // 最新样本时刻（同上）

    /// <summary>构造 internal——外部只能经 <see cref="TierTimeSeriesBuilder.StartAsync"/>。</summary>
    /// <param name="keys">键域（单序列/dense 双模式装配——键类型与 Ring/索引封闭类型封在其内）。</param>
    /// <param name="watermark">水位 meta（retention 边界持久域）。</param>
    /// <param name="fs">文件系统抽象（介质面）。</param>
    /// <param name="options">TimeSeries 选项（序列名/几何/retention/索引开关/dense 开关）。</param>
    /// <param name="logger">日志（缺省 null）。</param>
    internal TierTimeSeries(ITimeSeriesKeySpace keys, VersionedMetadata watermark,
        IFileSystem fs, TimeSeriesOptions options, ILogger? logger)
        : base(recovery: null, logger)
    {
        _keys = keys;
        _watermark = watermark;
        _registry = keys.Dense ? new SeriesRegistry(options.SeriesCapacity) : null;
        _fs = fs;
        _options = options;
        _clock = options.Clock;
        _logger = logger;
        Resources.Add(watermark, ownership: ResourceOwnership.Owned);
    }

    /// <summary>键域内部诊断（测试/白盒）。</summary>
    internal ITimeSeriesKeySpace DiagnosticKeys => _keys;

    /// <summary>已注册序列数（dense = 注册表计数；单序列恒 1）。</summary>
    public int SeriesCount => _registry?.Count ?? 1;

    /// <inheritdoc/>
    public long TrimmedUntilTimestamp => Volatile.Read(ref _trimmedUntil);

    /// <inheritdoc/>
    public LogicalAddress TailAddress => _keys.TailAddress;

    /// <inheritdoc/>
    public LogicalAddress DurableTail => _keys.FlushedUntilAddress;

    // ═══════════════════════════════════════════════════════════════════
    // 生命周期 / 恢复编排
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>★ Initialize 第一阶段钩子：并行启动 Ring + 水位 meta（索引延后到恢复核心——依赖 Ring Ready）。</summary>
    protected override void OnInitializeBegin()
    {
        _keys.InitializeRing();
        _watermark.Initialize();
    }

    /// <summary>★ 默认 Recovery 单一创建点——恢复核心 = 水位载入 + 索引启动 + 对账（spec §7）。</summary>
    /// <returns>恢复算法实例（由基类持有并在 Initialize 中执行）。</returns>
    protected override IRecovery<TimeSeriesRecoveryHints> CreateRecovery() => new TimeSeriesRecovery(this);

    /// <summary>恢复完成 + 装配就绪后启动 retention 后台循环（spec §5——低频扫水位；TTL/字节上限任一启用才装配）。</summary>
    protected override void OnInitializeComplete()
    {
        if (_options.RetentionTime is null && _options.MaxBytes is null) return;
        _retentionWorker = new RetentionWorker(this, _options.RetentionScanInterval);
        ConfigureBackgroundWorker(_retentionWorker);
    }

    /// <inheritdoc/>
    protected override void DisposeOverride(bool disposing)
    {
        base.DisposeOverride(disposing);
        _keys.DisposeStructures();
        _trimGate.Dispose();
    }

    /// <inheritdoc/>
    protected override async ValueTask DisposeOverrideAsync(bool disposing)
    {
        await base.DisposeOverrideAsync(disposing).ConfigureAwait(false);
        _keys.DisposeStructures();
        _trimGate.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════
    // 写入（乱序天然吸收——spec §4.1）
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public ValueTask<SampleAppendResult> AppendAsync(long timestamp, ReadOnlyMemory<byte> value, CancellationToken ct)
        => AppendAsync(DefaultSeriesId, timestamp, value, ct);

    /// <inheritdoc/>
    public ValueTask<SampleAppendResult> AppendAsync(uint seriesId, long timestamp, ReadOnlyMemory<byte> value, CancellationToken ct)
    {
        EnsureReady();
        ValidateSeriesId(seriesId);
        ValidateAppendGuards(seriesId, timestamp);

        // 溢出启用且超大值走异步协调路（Queue 同款）；常规值同步内联写（stackalloc 头不进 async 域）
        if (_options.OverflowPolicy == OverflowPolicy.Enabled
            && checked(_keys.EnvelopeHeaderSize + value.Length) > _options.MinOverflowSize)
        {
            return AppendOversizedAsync(seriesId, timestamp, value, ct);
        }

        // 写前序列守卫（dense：容量护栏 fail-fast——数据未写即拒绝）
        var entry = EnsureSeriesSlot(seriesId);

        Span<byte> header = stackalloc byte[_keys.EnvelopeHeaderSize];
        _keys.WriteEnvelopeHeader(header, seriesId, timestamp, null);
        var addr = _keys.WriteRecord(seriesId, timestamp, header, value.Span);
        CommitAppendedSample(entry, seriesId, timestamp, addr);
        return ValueTask.FromResult(new SampleAppendResult(addr));
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<SampleAppendResult>> AppendBatchAsync(
        IReadOnlyList<(long Timestamp, ReadOnlyMemory<byte> Value)> samples, CancellationToken ct)
        => AppendBatchAsync(DefaultSeriesId, samples, ct);

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<SampleAppendResult>> AppendBatchAsync(
        uint seriesId, IReadOnlyList<(long Timestamp, ReadOnlyMemory<byte> Value)> samples, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(samples);
        EnsureReady();
        ValidateSeriesId(seriesId);

        // 溢出启用走逐条路径（大值异步协调——Queue 同款不混用批量窗口）
        if (_options.OverflowPolicy == OverflowPolicy.Enabled)
        {
            return AppendBatchOversizedAsync(seriesId, samples, ct);
        }

        // 写前序列守卫（dense 容量护栏）
        var entry = EnsureSeriesSlot(seriesId);

        // ★ Ring 独占写窗口（同步域——stackalloc 头不进 async 方法）；
        //   守卫逐条先行——批内第 k 条违规抛出时前 k-1 已写入（地址即凭证，调用方按需取舍）。
        var batchSamples = new BatchSample[samples.Count];
        for (int i = 0; i < samples.Count; i++)
            batchSamples[i] = new BatchSample(seriesId, samples[i].Timestamp, samples[i].Value);
        var addresses = _keys.WriteBatch(batchSamples, ValidateAppendGuards, ct);

        // 索引批量补插（写后即知——顺序无关，崩溃窗口由恢复对账兜底）
        for (int i = 0; i < samples.Count; i++)
            CommitAppendedSample(entry, seriesId, samples[i].Timestamp, addresses[i]);
        return ValueTask.FromResult<IReadOnlyList<SampleAppendResult>>(
            addresses.Select(a => new SampleAppendResult(a)).ToArray());
    }

    private async ValueTask<IReadOnlyList<SampleAppendResult>> AppendBatchOversizedAsync(
        uint seriesId, IReadOnlyList<(long Timestamp, ReadOnlyMemory<byte> Value)> samples, CancellationToken ct)
    {
        var results = new SampleAppendResult[samples.Count];
        for (int i = 0; i < samples.Count; i++)
            results[i] = await AppendAsync(seriesId, samples[i].Timestamp, samples[i].Value, ct).ConfigureAwait(false);
        return results;
    }

    /// <summary>追加守卫（spec §4.1——fail-fast 不静默丢）。dense 按序列边界（未 trim = long.MinValue）。</summary>
    /// <exception cref="InvalidOperationException">ts 早于回收边界 / 超出 MaxOutOfOrderPast。</exception>
    private void ValidateAppendGuards(uint seriesId, long timestamp)
    {
        long trimmed = SeriesTrimmedUntil(seriesId);
        if (timestamp < trimmed)
            throw new InvalidOperationException(
                $"样本时刻 {timestamp} 早于回收边界 {trimmed}（序列 {seriesId} 已回收区间——数据必丢，fail-fast）");
        if (_options.MaxOutOfOrderPast is { } past && timestamp < _clock.GetUtcNow().Ticks - past.Ticks)
            throw new InvalidOperationException(
                $"样本时刻 {timestamp} 超出 MaxOutOfOrderPast（{past}）——乱序下界保护（老样本钉住索引/截断，锚定同 Queue MaxDelay）");
    }

    /// <summary>序列守卫校验（单序列实例仅默认序列；dense 任意 uint）。</summary>
    private void ValidateSeriesId(uint seriesId)
    {
        if (!_keys.Dense && seriesId != DefaultSeriesId)
            throw new InvalidOperationException(
                $"单序列实例仅支持默认序列（seriesId=0）——多序列写入请启用 {nameof(TimeSeriesOptions.DenseSeries)}");
    }

    /// <summary>序列回收边界（dense = 注册表缓存；单序列 = 实例镜像）。</summary>
    private long SeriesTrimmedUntil(uint seriesId)
    {
        if (_registry is { } reg && reg.TryGet(seriesId, out var entry))
            return Volatile.Read(ref entry.TrimmedUntil);
        return Volatile.Read(ref _trimmedUntil);
    }

    /// <summary>写前序列条目就位（dense：惰性注册 + 容量护栏 fail-fast；单序列 null）。</summary>
    private SeriesEntry? EnsureSeriesSlot(uint seriesId)
        => _registry is { } reg ? reg.Register(seriesId, LogicalAddress.Invalid) : null;

    /// <summary>写后侧账提交（注册钉住 + 索引插入 + 镜像推进——写前守卫已过）。</summary>
    private void CommitAppendedSample(SeriesEntry? entry, uint seriesId, long timestamp, LogicalAddress addr)
    {
        if (entry is not null)
        {
            entry.InitPinnedAddress(addr);   // 首样本钉住（此后只升不降——地址单调）
            Interlocked.Increment(ref entry.SampleCount);
        }
        _keys.InsertIndex(seriesId, timestamp, addr);
        TrackTimestamps(timestamp);
        Interlocked.Increment(ref _sampleCount);
    }

    /// <summary>超大值追加（溢出引擎承担——Queue WriteOverflowEnvelopeAsync 同款）。</summary>
    private async ValueTask<SampleAppendResult> AppendOversizedAsync(uint seriesId, long timestamp, ReadOnlyMemory<byte> value, CancellationToken ct)
    {
        int length = checked(_keys.EnvelopeHeaderSize + value.Length);
        byte[] rented = ArrayPool<byte>.Shared.Rent(length);
        LogicalAddress addr;
        try
        {
            _keys.WriteEnvelopeHeader(rented.AsSpan(), seriesId, timestamp, null);
            value.Span.CopyTo(rented.AsSpan(_keys.EnvelopeHeaderSize, value.Length));
            addr = await _keys.WriteRecordAsync(seriesId, timestamp, rented.AsMemory(0, length), ct)
                .ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
        CommitAppendedSample(EnsureSeriesSlot(seriesId), seriesId, timestamp, addr);
        return new SampleAppendResult(addr);
    }

    /// <summary>首/末样本时刻跟踪（多生产者并发——CAS 环；-1 哨兵 = 未知）。</summary>
    private void TrackTimestamps(long ts)
    {
        while (true)
        {
            long cur = Volatile.Read(ref _firstTs);
            if (cur != -1 && cur <= ts) break;
            if (Interlocked.CompareExchange(ref _firstTs, ts, cur) == cur) break;
        }
        while (true)
        {
            long cur = Volatile.Read(ref _lastTs);
            if (cur != -1 && cur >= ts) break;
            if (Interlocked.CompareExchange(ref _lastTs, ts, cur) == cur) break;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // 查询（索引驱动——spec §4.2/4.3）
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public IAsyncEnumerable<(long Timestamp, ReadOnlyMemory<byte> Value, LogicalAddress Address)> RangeAsync(
        long fromInclusive, long toExclusive, CancellationToken ct = default)
        => RangeAsync(DefaultSeriesId, fromInclusive, toExclusive, ct);

    /// <inheritdoc/>
    public async IAsyncEnumerable<(long Timestamp, ReadOnlyMemory<byte> Value, LogicalAddress Address)> RangeAsync(
        uint seriesId, long fromInclusive, long toExclusive,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        EnsureReady();
        ValidateSeriesId(seriesId);
        if (fromInclusive >= toExclusive) yield break;

        if (!_keys.HasIndex)
        {
            // 降档（定案⑨）：Ring 地址序交付——仅写序=时间序的场景（显式契约）
            await foreach (var (ts, payload, addr) in ScanSamplesAsync(seriesId, fromInclusive, toExclusive, ct)
                .ConfigureAwait(false))
            {
                yield return (ts, payload, addr);
            }
            yield break;
        }

        using var cursor = _keys.CreateIndexCursor();
        if (!cursor.SeekLowerBound(seriesId, fromInclusive)) yield break;
        do
        {
            ct.ThrowIfCancellationRequested();
            if (_keys.Dense && cursor.CurrentSeriesId != seriesId) yield break;   // 键前缀域出域
            if (cursor.CurrentTimestamp >= toExclusive) yield break;
            var sample = await _keys.ReadSampleAsync(cursor.CurrentAddress, seriesId, ct).ConfigureAwait(false);
            if (sample is { } s)
                yield return (cursor.CurrentTimestamp, s.Payload, cursor.CurrentAddress);
        }
        while (cursor.MoveNext());
    }

    /// <inheritdoc/>
    public ValueTask<(long Timestamp, ReadOnlyMemory<byte> Value, LogicalAddress Address)?> LatestAsync(CancellationToken ct)
        => LatestAsync(DefaultSeriesId, ct);

    /// <inheritdoc/>
    public async ValueTask<(long Timestamp, ReadOnlyMemory<byte> Value, LogicalAddress Address)?> LatestAsync(uint seriesId, CancellationToken ct)
    {
        EnsureReady();
        ValidateSeriesId(seriesId);
        if (_keys.HasIndex)
        {
            // 定案⑥：索引最大键 O(log n) 点查（dense = 域内 floor (s, max)）
            if (!_keys.TryGetLatest(seriesId, out var key, out var addr)) return null;
            var sample = await _keys.ReadSampleAsync(addr, seriesId, ct).ConfigureAwait(false);
            return sample is { } s ? (key, s.Payload, addr) : null;
        }

        // 降档：全扫取末条（O(n)——地址序末条即写序最新）
        (long Ts, byte[] Payload, LogicalAddress Addr)? last = null;
        await foreach (var (ts, payload, addr) in ScanSamplesAsync(seriesId, long.MinValue, long.MaxValue, ct)
            .ConfigureAwait(false))
        {
            last = (ts, payload, addr);
        }
        return last is null ? null : (last.Value.Ts, (ReadOnlyMemory<byte>)last.Value.Payload, last.Value.Addr);
    }

    /// <inheritdoc/>
    public ValueTask<(long Timestamp, ReadOnlyMemory<byte> Value, LogicalAddress Address)?> FloorAsync(
        long ts, CancellationToken ct)
        => FloorAsync(DefaultSeriesId, ts, ct);

    /// <inheritdoc/>
    public async ValueTask<(long Timestamp, ReadOnlyMemory<byte> Value, LogicalAddress Address)?> FloorAsync(
        uint seriesId, long ts, CancellationToken ct)
    {
        EnsureReady();
        ValidateSeriesId(seriesId);
        if (_keys.HasIndex)
        {
            // floor((ts, MaxValue)) = ≤ ts 的最大键（同刻多条取地址最大 = 写入序末条——采样语义）
            if (!_keys.TryGetFloor(seriesId, ts, out var floorTs, out var addr)) return null;
            var sample = await _keys.ReadSampleAsync(addr, seriesId, ct).ConfigureAwait(false);
            return sample is { } s ? (floorTs, s.Payload, addr) : null;
        }

        // 降档：全扫取 ≤ ts 末条
        (long Ts, byte[] Payload, LogicalAddress Addr)? floor = null;
        await foreach (var (sts, payload, addr) in ScanSamplesAsync(seriesId, long.MinValue, long.MaxValue, ct)
            .ConfigureAwait(false))
        {
            if (sts > ts) break;
            floor = (sts, payload, addr);
        }
        return floor is null ? null : (floor.Value.Ts, (ReadOnlyMemory<byte>)floor.Value.Payload, floor.Value.Addr);
    }

    /// <inheritdoc/>
    public ValueTask<TimeSeriesStats> GetStatsAsync(CancellationToken ct)
        => GetStatsAsync(DefaultSeriesId, ct);

    /// <inheritdoc/>
    public ValueTask<TimeSeriesStats> GetStatsAsync(uint seriesId, CancellationToken ct)
    {
        EnsureReady();
        ValidateSeriesId(seriesId);

        long? first = null, last = null;
        long count;
        long indexCount = 0;
        if (_keys.HasIndex)
        {
            indexCount = _keys.IndexEntryCount;
            if (_keys.Dense && seriesId != DefaultSeriesId)
            {
                // dense 命名序列：注册表计数 + 域内首末键
                count = _registry is { } reg && reg.TryGet(seriesId, out var e)
                    ? Volatile.Read(ref e.SampleCount) : 0;
                if (_keys.TryGetFirst(seriesId, out var f)) first = f;
                if (_keys.TryGetLatest(seriesId, out var l, out _)) last = l;
            }
            else if (_keys.Dense)
            {
                // dense 默认序列（两参 API）= 全实例口径：注册表聚合计数 + 实例首末镜像
                count = _registry!.TotalSampleCount();
                long f = Volatile.Read(ref _firstTs), l = Volatile.Read(ref _lastTs);
                if (f != -1) first = f;
                if (l != -1) last = l;
            }
            else
            {
                count = indexCount;   // 索引条目数即存活样本数（一一样本对应）
                if (_keys.TryGetFirst(seriesId, out var f)) first = f;
                if (_keys.TryGetLatest(seriesId, out var l, out _)) last = l;
            }
        }
        else
        {
            long f = Volatile.Read(ref _firstTs);
            long l = Volatile.Read(ref _lastTs);
            if (f != -1) first = f;
            if (l != -1) last = l;
            count = _registry is { } reg && reg.TryGet(seriesId, out var e)
                ? Volatile.Read(ref e.SampleCount)
                : Interlocked.Read(ref _sampleCount);
        }
        return ValueTask.FromResult(new TimeSeriesStats(
            first, last, count, indexCount,
            SeriesTrimmedUntil(seriesId), _keys.BeginAddress, _keys.TailAddress, _keys.FlushedUntilAddress));
    }

    /// <summary>降档查询路——Ring 流扫描 + 序列/时间过滤（地址序即交付序）。
    /// dense 附加序列水位过滤：被其它序列钉住的已回收样本仍在 Ring 上（索引已删），按 TrimmedUntil 跳过。</summary>
    private async IAsyncEnumerable<(long Ts, byte[] Payload, LogicalAddress Addr)> ScanSamplesAsync(
        uint seriesId, long fromInclusive, long toExclusive,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        bool dense = _keys.Dense;
        long trimmed = dense ? SeriesTrimmedUntil(seriesId) : long.MinValue;
        await foreach (var (ts, sid, payload, addr) in _keys.ScanSamplesAsync(ct).ConfigureAwait(false))
        {
            if (sid != seriesId) continue;
            if (ts < fromInclusive || ts >= toExclusive) continue;
            if (dense && ts < trimmed) continue;   // 已回收区间（Ring 残留——降档无索引过滤）
            yield return (ts, payload, addr);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // 持久化 / retention（spec §5——截断先行、水位随后）
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public ValueTask FlushAsync(CancellationToken ct)
    {
        EnsureReady();
        return _keys.FlushUntilAsync(_keys.TailAddress, ct);
    }

    /// <inheritdoc/>
    public ValueTask<long> TruncateAsync(long beforeTimestampExclusive, CancellationToken ct)
        => TruncateAsync(DefaultSeriesId, beforeTimestampExclusive, ct);

    /// <inheritdoc/>
    public async ValueTask<long> TruncateAsync(uint seriesId, long beforeTimestampExclusive, CancellationToken ct)
    {
        EnsureReady();
        ValidateSeriesId(seriesId);
        await _trimGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 前置 flush（截断守卫：Ring TruncatePrefix 须 ≤ FlushedUntilAddress——spec §8）
            await _keys.FlushUntilAsync(_keys.TailAddress, ct).ConfigureAwait(false);

            long deleted;
            if (_keys.Dense)
                deleted = await TruncateDenseAsync(seriesId, beforeTimestampExclusive, ct).ConfigureAwait(false);
            else
                deleted = await TruncateSingleAsync(beforeTimestampExclusive, ct).ConfigureAwait(false);
            return deleted;
        }
        finally
        {
            _trimGate.Release();
        }
    }

    /// <summary>单序列 trim（既有语义逐字保留：索引清理 → Ring 截断 → 水位持久）。</summary>
    private async ValueTask<long> TruncateSingleAsync(long beforeTimestampExclusive, CancellationToken ct)
    {
        LogicalAddress bound;
        long deleted;
        if (_keys.HasIndex)
        {
            // bound = 索引内首条 key ≥ beforeTs 的地址（无 → 尾——全部可回收）
            bound = _keys.TrySeekLowerBound(DefaultSeriesId, beforeTimestampExclusive, out var b)
                ? b
                : _keys.FlushedUntilAddress;
            // 索引清理（键 < bound 批量删——O(路径 + 覆盖叶数)）
            deleted = _keys.TruncateIndexPrefix(DefaultSeriesId, beforeTimestampExclusive);
        }
        else
        {
            // 降档：Ring 流扫描反查边界与计数（O(n)——显式降档契约）
            (bound, deleted) = await ScanTrimBoundAsync(beforeTimestampExclusive, ct).ConfigureAwait(false);
        }

        // 数据回收（既有守卫：≤ FlushedUntilAddress——前置 flush 已保证可推进到尾）
        LogicalAddress target = bound.IsValid && bound <= _keys.FlushedUntilAddress
            ? bound
            : _keys.FlushedUntilAddress;
        if (target > _keys.BeginAddress)
            _keys.TruncateRecordsPrefix(target);

        // 水位随后（原子提交——单调不回退）
        long newCount = Interlocked.Add(ref _sampleCount, -deleted);
        WriteWatermarkLocked(Math.Max(Volatile.Read(ref _trimmedUntil), beforeTimestampExclusive),
            target, newCount);
        return deleted;
    }

    /// <summary>
    /// dense trim（#443 设计稿 §5.1/§5.4）：本序列索引键域前缀截断 → 本序列水位/钉住地址提升 →
    /// Ring 截断下限 = 全体序列钉住地址的 min（慢序列钉住——其它序列老数据不受影响）→ 水位持久。
    /// </summary>
    private async ValueTask<long> TruncateDenseAsync(uint seriesId, long beforeTimestampExclusive, CancellationToken ct)
    {
        var reg = _registry!;
        LogicalAddress firstSurviving;
        long deleted;
        if (_keys.HasIndex)
        {
            firstSurviving = _keys.TrySeekLowerBound(seriesId, beforeTimestampExclusive, out var b)
                ? b
                : _keys.FlushedUntilAddress;   // 域内无存活条目——钉住解除
            deleted = _keys.TruncateIndexPrefix(seriesId, beforeTimestampExclusive);
        }
        else
        {
            // 降档：Ring 流扫描（本序列）反查边界与计数
            (firstSurviving, deleted) = await ScanTrimBoundDenseAsync(seriesId, beforeTimestampExclusive, ct)
                .ConfigureAwait(false);
        }

        // 本序列水位 + 钉住地址提升（trim 后首个存活条目 = 慢序列新钉点）
        var entry = reg.Register(seriesId, firstSurviving);
        long trimmed = Math.Max(Volatile.Read(ref entry.TrimmedUntil), beforeTimestampExclusive);
        Volatile.Write(ref entry.TrimmedUntil, trimmed);
        entry.TrimmedAddress = firstSurviving;
        entry.RaisePinnedAddress(firstSurviving);
        Interlocked.Add(ref entry.SampleCount, -deleted);

        // Ring 截断下限 = 全体序列钉住地址的 min（慢序列钉住；空注册表 = 尾全部可回收）
        LogicalAddress target = reg.MinPinnedAddress(_keys.FlushedUntilAddress);
        if (target.IsValid && target <= _keys.FlushedUntilAddress && target > _keys.BeginAddress)
            _keys.TruncateRecordsPrefix(target);

        // 水位持久（默认槽 series 0 + 稀疏块——原子提交，单调不回退；非 0 序列 trim 不动默认槽边界）
        long slotTrim = seriesId == DefaultSeriesId
            ? Math.Max(Volatile.Read(ref _trimmedUntil), beforeTimestampExclusive)
            : Volatile.Read(ref _trimmedUntil);
        long newCount = Interlocked.Add(ref _sampleCount, -deleted);
        WriteWatermarkLocked(slotTrim, target, newCount);
        return deleted;
    }

    /// <summary>降档 trim（单序列）：Ring 流扫描（地址序≈时间序契约）反查截断锚与回收计数。</summary>
    private async ValueTask<(LogicalAddress Bound, long Deleted)> ScanTrimBoundAsync(
        long beforeTimestampExclusive, CancellationToken ct)
    {
        LogicalAddress bound = _keys.FlushedUntilAddress;   // 无存活样本形态：全部回收
        long deleted = 0;
        await foreach (var (ts, _, _, addr) in _keys.ScanSamplesAsync(ct).ConfigureAwait(false))
        {
            if (ts < beforeTimestampExclusive)
            {
                deleted++;
                continue;
            }
            bound = addr;   // 首条存活样本 = 截断锚（其前的全部待回收样本被截）
            break;
        }
        if (deleted == 0) bound = _keys.BeginAddress;   // 无可回收——截断 no-op
        return (bound, deleted);
    }

    /// <summary>降档 trim（dense 本序列）：Ring 流扫描按序列过滤反查。
    /// 计数口径 = [已回收边界, before) 区间样本（Ring 残留的更老样本已被上次 trim 记账，不重复计）。</summary>
    private async ValueTask<(LogicalAddress Bound, long Deleted)> ScanTrimBoundDenseAsync(
        uint seriesId, long beforeTimestampExclusive, CancellationToken ct)
    {
        long prevTrimmed = SeriesTrimmedUntil(seriesId);
        LogicalAddress bound = _keys.FlushedUntilAddress;
        long deleted = 0;
        await foreach (var (ts, sid, _, addr) in _keys.ScanSamplesAsync(ct).ConfigureAwait(false))
        {
            if (sid != seriesId) continue;
            if (ts < prevTrimmed) continue;   // 上次 trim 已记账（Ring 残留——被其它序列钉住）
            if (ts < beforeTimestampExclusive)
            {
                deleted++;
                continue;
            }
            bound = addr;
            break;
        }
        if (deleted == 0) bound = _keys.BeginAddress;
        return (bound, deleted);
    }

    /// <summary>水位持久（写 meta + Persist 原子提交 + 内存镜像推进——_trimGate 内或恢复单线程调）。
    /// dense：payload = 默认槽（series 0）+ 稀疏块（只存显式 trim 过的序列——设计稿 §5.2）。</summary>
    private void WriteWatermarkLocked(long trimmedUntil, LogicalAddress bound, long sampleCount)
    {
        var state = new TimeSeriesWatermarkState(trimmedUntil, bound, sampleCount,
            Interlocked.Read(ref _watermarkEpoch) + 1);
        byte[] payload;
        if (_registry is null)
        {
            payload = state.WriteTo(new byte[TimeSeriesWatermarkState.PayloadSize]);
        }
        else
        {
            // series 0 槽水位 = 序列 0 自身边界（未 trim 保持既有值——传入 trimmedUntil 已做 max 合并）
            payload = DenseSeriesWatermarkDoc.Encode(state, CollectTrimmedBlocks());
        }
        _watermark.Write(payload);
        _watermark.Persist();
        _watermark.ReclaimOldVersions();
        Volatile.Write(ref _trimmedUntil, state.TrimmedUntilTimestamp);
        Interlocked.Exchange(ref _watermarkEpoch, state.WatermarkEpoch);
    }

    /// <summary>稀疏块收集（只存显式 trim 过的序列——未 trim 序列水位 = 派生值不回写）。</summary>
    private List<DenseSeriesWatermarkBlock> CollectTrimmedBlocks()
    {
        var blocks = new List<DenseSeriesWatermarkBlock>();
        var reg = _registry!;
        foreach (var sid in reg.SeriesIds)
        {
            if (!reg.TryGet(sid, out var entry)) continue;
            long trimmed = Volatile.Read(ref entry.TrimmedUntil);
            if (trimmed == long.MinValue) continue;
            blocks.Add(new DenseSeriesWatermarkBlock(
                sid, trimmed, entry.TrimmedAddress, Volatile.Read(ref entry.SampleCount)));
        }
        return blocks;
    }

    // ═══════════════════════════════════════════════════════════════════
    // retention 后台循环（spec §5——TTL / MaxBytes 同路）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>retention 单轮（worker 周期调）：时间 TTL 与字节上限取更晚的时间锚，一次 TruncateAsync 落地。
    /// dense：TTL 逐序列推进（O(活跃序列)）；字节上限按地址阈值反查逐序列 cutoff（设计稿 §9 预算）。</summary>
    internal async ValueTask RunRetentionAsync(CancellationToken ct)
    {
        EnsureReady();
        long cutoff = long.MinValue;
        if (_options.RetentionTime is { } retention)
            cutoff = _clock.GetUtcNow().Ticks - retention.Ticks;
        if (_options.MaxBytes is { } maxBytes)
        {
            long usage = _keys.GetByteDistance(_keys.BeginAddress, _keys.TailAddress);
            if (usage > maxBytes)
            {
                if (_keys.Dense)
                {
                    await TrimToByteBudgetAsync(usage - maxBytes, ct).ConfigureAwait(false);
                    return;   // 字节上限轮独立落地（地址阈值语义，与 TTL 时间锚不可合并）
                }
                long bytesCutoff = ComputeBytesCutoff(usage - maxBytes);
                if (bytesCutoff > cutoff) cutoff = bytesCutoff;
            }
        }
        if (cutoff == long.MinValue) return;

        if (_keys.Dense)
        {
            // 逐序列推进（惰性容忍长尾——空序列/无老样本的序列 O(log n) seek 即跳过）
            foreach (var sid in _registry!.SeriesIds)
            {
                ct.ThrowIfCancellationRequested();
                if (!_keys.TryGetFirst(sid, out var firstTs) || firstTs >= cutoff) continue;
                await TruncateAsync(sid, cutoff, ct).ConfigureAwait(false);
            }
            return;
        }

        long first0 = await FirstSampleTimestampAsync(ct).ConfigureAwait(false);
        if (first0 == -1 || first0 >= cutoff) return;   // 无老于锚的样本——不空转写水位
        await TruncateAsync(cutoff, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// dense 字节上限轮（#443 设计稿 §5.4）：全索引按地址排序累计覆盖至超额阈值 →
    /// 阈值下逐序列取最大 ts 为 cutoff → 逐序列 TruncateAsync（Ring 下限由钉住 min 收口）。
    /// 时间锚 = ts（与单序列 trim 同语义——乱序样本按 ts 回收，字节预算软收敛）。
    /// </summary>
    private async ValueTask TrimToByteBudgetAsync(long excess, CancellationToken ct)
    {
        var entries = new List<(LogicalAddress Addr, uint Sid, long Ts)>();
        using (var cursor = _keys.CreateIndexCursor())
        {
            while (cursor.MoveNext())
                entries.Add((cursor.CurrentAddress, cursor.CurrentSeriesId, cursor.CurrentTimestamp));
        }
        if (entries.Count == 0) return;
        entries.Sort(static (a, b) => a.Addr.CompareTo(b.Addr));

        long acc = 0;
        LogicalAddress prev = _keys.BeginAddress, threshold = _keys.TailAddress;
        foreach (var (addr, _, _) in entries)
        {
            if (addr > prev)
            {
                acc += _keys.GetByteDistance(prev, addr);
                prev = addr;
            }
            if (acc >= excess)
            {
                threshold = addr;
                break;
            }
        }

        // 阈值下逐序列最大 ts（ts + 1 tick = beforeExclusive——旧于等于该 ts 的样本全部回收）
        var cutoffs = new Dictionary<uint, long>();
        foreach (var (addr, sid, ts) in entries)
        {
            if (addr >= threshold) continue;
            if (cutoffs.TryGetValue(sid, out var cur))
            {
                if (ts > cur) cutoffs[sid] = ts;
            }
            else cutoffs[sid] = ts;
        }
        foreach (var (sid, maxTs) in cutoffs)
        {
            ct.ThrowIfCancellationRequested();
            long before = maxTs == long.MaxValue ? long.MaxValue : checked(maxTs + 1);
            await TruncateAsync(sid, before, ct).ConfigureAwait(false);
        }
    }

    /// <summary>字节上限的时间锚反查（spec §5——索引序累积 record 字节至超额，取保留首条的 ts）。
    /// 降档无索引不可反查（返回 long.MinValue 跳过——TTL 仍可用）。同步（BTree 游标同步迭代）。</summary>
    private long ComputeBytesCutoff(long excess)
    {
        if (!_keys.HasIndex) return long.MinValue;
        long acc = 0;
        using var cursor = _keys.CreateIndexCursor();
        if (!cursor.MoveNext()) return long.MinValue;
        LogicalAddress prev = cursor.CurrentAddress < _keys.BeginAddress ? _keys.BeginAddress : cursor.CurrentAddress;
        while (cursor.MoveNext())
        {
            var addr = cursor.CurrentAddress < _keys.BeginAddress ? _keys.BeginAddress : cursor.CurrentAddress;
            if (addr > prev) acc += _keys.GetByteDistance(prev, addr);
            prev = addr;
            if (acc >= excess)
                return cursor.CurrentTimestamp;   // 截到本条之前——本条保留（超额已覆盖）
        }
        return long.MinValue;   // 全量字节不足超额（计量口径差异）——不动，等下轮
    }

    /// <summary>最早存活样本时刻（-1 = 空序列；索引 O(log n) 首键 / 降档用内存镜像）。</summary>
    private async ValueTask<long> FirstSampleTimestampAsync(CancellationToken ct)
    {
        if (_keys.HasIndex)
            return _keys.TryGetFirst(DefaultSeriesId, out var ts) ? ts : -1;
        long f = Volatile.Read(ref _firstTs);
        if (f != -1) return f;
        await foreach (var (ts, _, _, _) in _keys.ScanSamplesAsync(ct).ConfigureAwait(false))
            return ts;   // 地址序首条≈时间序首条（降档契约）；镜像缺失时现场取
        return -1;
    }

    /// <summary>retention 后台循环（低频——默认 1 分钟扫一次水位，spec §5）。</summary>
    private sealed class RetentionWorker(TierTimeSeries owner, TimeSpan interval)
        : BackgroundWorkerLoop(null, 1, "TimeSeriesRetentionWorker")
    {
        /// <summary>单周期：到点跑一轮 retention（TTL / 字节上限）。</summary>
        /// <param name="ct">取消令牌（Delay 响应取消——取消即终止循环）。</param>
        /// <returns>true = 继续下一周期。</returns>
        protected override async ValueTask<bool> RunOneCycleAsync(CancellationToken ct)
        {
            await owner._clock.Delay(interval, ct).ConfigureAwait(false);
            await owner.RunRetentionAsync(ct).ConfigureAwait(false);
            return true;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Session 接线（spec §10）
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public ITransactionParticipant GetWatermarkParticipant() => _watermark;

    // ═══════════════════════════════════════════════════════════════════
    // ★ 恢复核心（RecoveryBase 模板派生——join Ring/水位 → 水位载入 → 索引启动 → 对账）
    // ═══════════════════════════════════════════════════════════════════

    private sealed class TimeSeriesRecovery(TierTimeSeries owner) : RecoveryBase<TimeSeriesRecoveryHints>
    {
        /// <summary>层间 join——Ring + 水位 meta（OnInitializeBegin 已并行启动）。</summary>
        /// <param name="ct">取消令牌。</param>
        /// <returns>任务在 Ring 与水位 meta 均 Ready 后完成。</returns>
        protected override async ValueTask WaitForDependenciesAsync(CancellationToken ct)
        {
            await owner._keys.WaitForRingReadyAsync(ct).ConfigureAwait(false);
            await owner._watermark.WaitForReadyAsync(ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 恢复核心（spec §7）：水位 meta 载入 → 时间索引启动（Ring Ready 后——resolver 数据面可用，
        /// 载帧/增量重放）→ 对账三步（清理已回收/悬空条目 + 重建崩溃窗口缺失 + 水位校正）→ 放行。
        /// dense（#443 设计稿 §5.5）：水位块装载种子化注册表；重放按 envelope SeriesId 路由
        /// 逐序列重建索引 + 钉住地址/首末样本登记；水位块与重放事实 min 校正（单调不回退）。
        /// </summary>
        /// <param name="hints">恢复 hints（无注入项——预留）。</param>
        /// <param name="ct">取消令牌。</param>
        protected override async ValueTask OnRecoveryCoreAsync(TimeSeriesRecoveryHints hints, CancellationToken ct)
        {
            // ── 2. 水位 meta 载入（首次启动 payload 缺失 → Initial；dense = keyed 文档 + 块种子化）──
            int wlen = Math.Max(owner._watermark.CurrentPayloadLength,
                TimeSeriesWatermarkState.PayloadSize);
            var wbuf = new byte[wlen];
            int wread = owner._watermark.Read(wbuf);
            if (owner._keys.Dense)
                LoadDenseWatermark(wbuf.AsSpan(0, Math.Max(wread, 0)));
            else if (TimeSeriesWatermarkState.TryRead(wbuf.AsSpan(0, Math.Max(wread, 0)), out var state))
            {
                Volatile.Write(ref owner._trimmedUntil, state.TrimmedUntilTimestamp);
                Interlocked.Exchange(ref owner._sampleCount, state.SampleCount);
                Interlocked.Exchange(ref owner._watermarkEpoch, state.WatermarkEpoch);
            }

            int cleaned = 0, reinserted = 0;
            if (owner._keys.HasIndex)
            {
                // ── 3. 索引启动（Queue 幂等索引同款时序：Ring Ready 之后，重放窗口 [Begin, Tail)）──
                owner._keys.InitializeIndex(owner._keys.BeginAddress, owner._keys.TailAddress);
                await owner._keys.WaitForIndexReadyAsync(ct).ConfigureAwait(false);

                // ── 4a. 索引清理：地址 < Ring 头（已回收）或 > 尾（悬空——掉电丢内存尾但锚点帧已物化）──
                var stale = new List<(uint Sid, long Ts, long Tiebreaker)>();
                using (var cursor = owner._keys.CreateIndexCursor())
                {
                    while (cursor.MoveNext())
                    {
                        var value = cursor.CurrentAddress;
                        if (value < owner._keys.BeginAddress || value > owner._keys.TailAddress)
                            stale.Add((cursor.CurrentSeriesId, cursor.CurrentTimestamp, cursor.CurrentTiebreaker));
                    }
                }
                foreach (var (sid, ts, tb) in stale)
                {
                    if (owner._keys.DeleteIndexKey(sid, ts, tb)) cleaned++;
                }

                // ── 4b. 索引重建：Ring 扫 [Begin, Tail) 的时序 record 不在索引 → 重插（dense 按
                //        envelope SeriesId 路由——悬空清理/缺条重建/注册表装载三合一）──
                var known = new HashSet<LogicalAddress>();
                using (var cursor = owner._keys.CreateIndexCursor())
                {
                    while (cursor.MoveNext())
                        known.Add(cursor.CurrentAddress);
                }
                long firstTs = -1, lastTs = -1;
                // 逐序列存活计数 = 重放事实（权威）——持久块计数是上次持久时刻快照，可能落后
                var liveCounts = owner._keys.Dense ? new Dictionary<uint, long>() : null;
                await foreach (var (ts, sid, _, addr) in owner._keys.ScanSamplesAsync(ct).ConfigureAwait(false))
                {
                    var entry = owner._registry?.Seed(sid);
                    if (firstTs == -1 || ts < firstTs) firstTs = ts;
                    if (lastTs == -1 || ts > lastTs) lastTs = ts;
                    if (entry is { } e)
                    {
                        e.InitPinnedAddress(addr);   // 重放地址序——首见 = 最早存活样本
                        if (liveCounts is not null)
                            liveCounts[sid] = (liveCounts.TryGetValue(sid, out var c) ? c : 0) + 1;
                    }
                    if (known.Contains(addr)) continue;
                    owner._keys.InsertIndex(sid, ts, addr);
                    reinserted++;
                }
                if (liveCounts is { } counts)
                {
                    foreach (var (sid, live) in counts)
                    {
                        if (owner._registry!.TryGet(sid, out var seeded))
                            Interlocked.Exchange(ref seeded.SampleCount, live);
                    }
                }
                Volatile.Write(ref owner._firstTs, firstTs);
                Volatile.Write(ref owner._lastTs, lastTs);
                Interlocked.Exchange(ref owner._sampleCount,
                    owner._keys.Dense ? owner._registry!.TotalSampleCount() : owner._keys.IndexEntryCount);

                // ── 4c. 水位校正：TrimmedUntil = min(水位值, 首样本 ts)——截断先行未推水位的窗口收口
                //        （数据事实优先：水位超前于实际回收边界时回拉，防老样本被拒）──
                long trimmed = Volatile.Read(ref owner._trimmedUntil);
                if (!owner._keys.Dense)
                {
                    if (firstTs != -1 && firstTs < trimmed)
                        owner.WriteWatermarkLocked(firstTs, owner._keys.BeginAddress, owner._keys.IndexEntryCount);
                }
                else if (CorrectDenseWatermarks())
                {
                    owner.WriteWatermarkLocked(Volatile.Read(ref owner._trimmedUntil),
                        owner._keys.BeginAddress, Interlocked.Read(ref owner._sampleCount));
                }
            }
            else
            {
                // 降档：无索引可对账——扫一遍统计（计数/首末）+ 水位校正同款
                long count = 0, firstTs = -1, lastTs = -1;
                var liveCounts = owner._keys.Dense ? new Dictionary<uint, long>() : null;
                await foreach (var (ts, sid, _, addr) in owner._keys.ScanSamplesAsync(ct).ConfigureAwait(false))
                {
                    var entry = owner._registry?.Seed(sid);
                    if (entry is { } e)
                    {
                        e.InitPinnedAddress(addr);
                        if (liveCounts is not null)
                            liveCounts[sid] = (liveCounts.TryGetValue(sid, out var c) ? c : 0) + 1;
                    }
                    count++;
                    if (firstTs == -1 || ts < firstTs) firstTs = ts;
                    if (lastTs == -1 || ts > lastTs) lastTs = ts;
                }
                if (liveCounts is { } counts)
                {
                    foreach (var (sid, live) in counts)
                    {
                        if (owner._registry!.TryGet(sid, out var seeded))
                            Interlocked.Exchange(ref seeded.SampleCount, live);
                    }
                }
                Volatile.Write(ref owner._firstTs, firstTs);
                Volatile.Write(ref owner._lastTs, lastTs);
                Interlocked.Exchange(ref owner._sampleCount,
                    owner._keys.Dense ? owner._registry!.TotalSampleCount() : count);

                long trimmed = Volatile.Read(ref owner._trimmedUntil);
                if (!owner._keys.Dense)
                {
                    if (firstTs != -1 && firstTs < trimmed)
                        owner.WriteWatermarkLocked(firstTs, owner._keys.BeginAddress, count);
                }
                else if (CorrectDenseWatermarks())
                {
                    owner.WriteWatermarkLocked(Volatile.Read(ref owner._trimmedUntil),
                        owner._keys.BeginAddress, Interlocked.Read(ref owner._sampleCount));
                }
            }

            RaiseProgress(90, $"cleaned={cleaned} reinserted={reinserted} samples={Interlocked.Read(ref owner._sampleCount)}");
        }

        /// <summary>dense 水位块载入：默认槽（series 0）+ 稀疏块种子化注册表（TrimmedUntil/锚/计数）。</summary>
        private void LoadDenseWatermark(ReadOnlySpan<byte> payload)
        {
            if (!DenseSeriesWatermarkDoc.TryDecode(payload, out var slot, out var blocks))
                return;
            Volatile.Write(ref owner._trimmedUntil, slot.TrimmedUntilTimestamp);
            Interlocked.Exchange(ref owner._sampleCount, slot.SampleCount);
            Interlocked.Exchange(ref owner._watermarkEpoch, slot.WatermarkEpoch);

            var reg = owner._registry!;
            var e0 = reg.Seed(DefaultSeriesId);
            Volatile.Write(ref e0.TrimmedUntil, slot.TrimmedUntilTimestamp);
            e0.TrimmedAddress = slot.TrimmedAddress;
            foreach (var block in blocks)
            {
                var entry = reg.Seed(block.SeriesId);
                Volatile.Write(ref entry.TrimmedUntil, block.TrimmedUntilTimestamp);
                entry.TrimmedAddress = block.TrimmedAddress;
                Interlocked.Exchange(ref entry.SampleCount, block.SampleCount);
            }
        }

        /// <summary>dense 逐序列水位校正（重放事实优先回拉）：true = 有回拉（需重持久化）。</summary>
        private bool CorrectDenseWatermarks()
        {
            bool pulled = false;
            var reg = owner._registry!;
            foreach (var sid in reg.SeriesIds)
            {
                if (!reg.TryGet(sid, out var entry)) continue;
                if (!owner._keys.TryGetFirst(sid, out var firstTs)) continue;
                long trimmed = Volatile.Read(ref entry.TrimmedUntil);
                if (firstTs < trimmed)
                {
                    Volatile.Write(ref entry.TrimmedUntil, firstTs);
                    if (sid == DefaultSeriesId) Volatile.Write(ref owner._trimmedUntil, firstTs);
                    pulled = true;
                }
            }
            return pulled;
        }
    }
}
