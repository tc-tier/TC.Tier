using TC.Tier.Contracts.Storage;
using TC.Tier.Runtime.Structures.Ring;
using TC.Tier.Runtime.Structures.SortedIndex;
using TC.Tier.Runtime.Structures.SortedIndex.Contracts;

namespace TC.Tier.Products.Queue;

/// <summary>延迟消息窥视项（ListDelayedAsync 产物——排队中延迟消息的只读视图）。</summary>
/// <param name="Address">消息地址。</param>
/// <param name="DueTime">延迟投递时刻（UTC Ticks）。</param>
public readonly record struct DelayedInfo(LogicalAddress Address, long DueTime);

/// <summary>
/// 延迟就绪索引（tc-tier-queue-spec §4——SortedIndex 搭配件）。
/// <para>★ 键模型（消除写入前不知地址的鸡生蛋）：Ring record key = (dueTime, 0)——仅作
///   "延迟消息"标记 + 恢复对账自述；索引 key = (dueTime, record 地址)——写完即知，
///   同 dueTime 按地址 FIFO（定案④）。</para>
/// <para>★ 生命周期：条目存续 = 消息数据存续（取消/被截断时删）；就绪投递后保留
///   （due ≤ now 的条目对多组各自投递；全部组终结后地址 &lt; min 组下界 → 对账清理）。</para>
/// <para>★ 非正确性必需场景（无延迟消息）不装配（Options.Delayed = null）。</para>
/// <para>★ 恢复（2026-08-27 启用主存储载入）：StartAsync 传重放窗口 [Begin, Tail)——BTree 锚点帧
///   有效则载帧 + 增量重放 (W, Tail)，无效 fail-safe 全量重放（同一路径）——此前不传 hints，
///   锚点帧只写不读（30s 后台 dump 白写）。键空间经 <see cref="DelayedKeyResolver"/> 适配
///   （record (due,0) → 索引 (due, addr.Offset)——Ring 原生键不含 tiebreaker）。</para>
/// </summary>
internal sealed class DelayedIndex : IDisposable, IAsyncDisposable
{
    private readonly BTreeOfQueueKey _index;

    /// <summary>构造（internal——经 TierQueueBuilder 装配）。</summary>
    /// <param name="index">底层 BTree 索引实例。</param>
    internal DelayedIndex(BTreeOfQueueKey index) => _index = index;

    /// <summary>诊断可观测（测试/运维）——上次恢复是否走了锚点帧载入（false=全量重放）。</summary>
    internal bool MainStorageAppliedLastRecovery => _index.MainStorageAppliedLastRecovery;

    /// <summary>
    /// ★ 启动编排（宿主恢复核心调——Ring 就绪之后；构造期零 IO 不 Initialize）：
    /// Initialize + WaitForReady（载帧/增量重放——重放窗口 [Begin, Tail)，resolver 依赖 Ring 已 Ready）。
    /// </summary>
    /// <param name="ring">已就绪的数据 Ring（提供重放窗口 [Begin, Tail)）。</param>
    /// <param name="ct">取消令牌。</param>
    internal async Task StartAsync(RingOfQueueKey ring, CancellationToken ct)
    {
        _index.Initialize(new SortedIndexRecoveryHints(ring.BeginAddress, ring.TailAddress));
        await _index.WaitForReadyAsync(ct).ConfigureAwait(false);
    }

    /// <summary>插入（Enqueue 延迟路径——record 已落 Ring 后调）。</summary>
    /// <param name="dueTime">延迟投递时刻（UTC Ticks）。</param>
    /// <param name="addr">消息地址（索引值 + tiebreaker 来源）。</param>
    internal void Insert(long dueTime, LogicalAddress addr)
        => _index.Insert(new QueueKey(dueTime, addr.Offset), addr, _index.BeginAddress);

    /// <summary>取消（按索引键删——dueTime 已知形态）。</summary>
    /// <param name="dueTime">延迟投递时刻。</param>
    /// <param name="addr">消息地址。</param>
    /// <returns>true = 删除成功；false = 条目不存在。</returns>
    internal bool Remove(long dueTime, LogicalAddress addr)
        => _index.Delete(new QueueKey(dueTime, addr.Offset));

    /// <summary>取消（按地址形态——先从 Ring 读 dueTime 再删）。</summary>
    /// <param name="ring">数据 Ring（读 record key 取 dueTime）。</param>
    /// <param name="addr">消息地址。</param>
    /// <returns>true = 删除成功；false = 非延迟消息或条目不存在。</returns>
    internal bool Remove(RingOfQueueKey ring, LogicalAddress addr)
    {
        if (!ring.TryGetKey(addr, out var key) || key.DueTime == 0) return false;
        return Remove(key.DueTime, addr);
    }

    /// <summary>就绪扫描（Forward 从头起——dueTime 主序，遇未到期停）。yield (dueTime, addr)。</summary>
    /// <param name="nowUtcTicks">当前时刻（UTC Ticks——due ≤ now 即就绪）。</param>
    /// <returns>就绪消息序列（dueTime, addr）——有序，遇未到期停止。</returns>
    internal IEnumerable<(long DueTime, LogicalAddress Addr)> ScanReady(long nowUtcTicks)
    {
        using var cursor = _index.CreateScanCursor(ReadDirection.Forward);
        if (!cursor.SeekLowerBound(new QueueKey(long.MinValue, long.MinValue))) yield break;
        do
        {
            var key = cursor.CurrentKey;
            if (key.DueTime > nowUtcTicks) yield break;   // 有序——首条未到期即全未到期
            yield return (key.DueTime, cursor.CurrentValue);
        }
        while (cursor.MoveNext());
    }

    /// <summary>全量窥视（ListDelayed——分页在调用方）。</summary>
    internal IEnumerable<DelayedInfo> List()
    {
        using var cursor = _index.CreateScanCursor(ReadDirection.Forward);
        while (cursor.MoveNext())
            yield return new DelayedInfo(cursor.CurrentValue, cursor.CurrentKey.DueTime);
    }

    /// <summary>按 dueTime 区间和 Offset/Limit 分页窥视。</summary>
    /// <param name="query">查询范围与分页参数。</param>
    /// <returns>延迟消息视图（dueTime 序，分页后）。</returns>
    /// <exception cref="ArgumentOutOfRangeException">Offset 或 Limit 为负数。</exception>
    internal IEnumerable<DelayedInfo> List(DelayedQuery query)
    {
        if (query.Offset < 0)
            throw new ArgumentOutOfRangeException(nameof(query), "Offset 不能为负数");
        if (query.Limit is < 0)
            throw new ArgumentOutOfRangeException(nameof(query), "Limit 不能为负数");

        int skipped = 0;
        int emitted = 0;
        int limit = query.Limit ?? int.MaxValue;
        using var cursor = _index.CreateScanCursor(ReadDirection.Forward);
        long start = query.FromDueInclusive ?? long.MinValue;
        if (!cursor.SeekLowerBound(new QueueKey(start, long.MinValue))) yield break;
        do
        {
            long due = cursor.CurrentKey.DueTime;
            if (query.ToDueInclusive is { } to && due > to) yield break;
            if (skipped++ < query.Offset) continue;
            if (emitted++ >= limit) yield break;
            yield return new DelayedInfo(cursor.CurrentValue, due);
        }
        while (cursor.MoveNext());
    }

    /// <summary>按 dueTime 区间取消（返回被删地址集——调用方做组 skip 标记）。</summary>
    /// <param name="fromDueInclusive">区间下界（含）。</param>
    /// <param name="toDueInclusive">区间上界（含）。</param>
    /// <returns>被删条目的地址列表。</returns>
    internal List<LogicalAddress> RemoveRange(long fromDueInclusive, long toDueInclusive)
    {
        var removed = new List<LogicalAddress>();
        using var cursor = _index.CreateScanCursor(ReadDirection.Forward);
        var pendingDelete = new List<QueueKey>();
        if (cursor.SeekLowerBound(new QueueKey(fromDueInclusive, long.MinValue)))
        {
            do
            {
                var key = cursor.CurrentKey;
                if (key.DueTime > toDueInclusive) break;
                pendingDelete.Add(key);
                removed.Add(cursor.CurrentValue);
            }
            while (cursor.MoveNext());
        }
        foreach (var k in pendingDelete)
            _index.Delete(k);
        return removed;
    }

    /// <summary>条目数（治理观测）。</summary>
    internal long Count => _index.EntryCount;

    /// <summary>
    /// ★ 恢复对账（spec §9.4）：① 索引清理——条目地址 &lt; floor（已被截断/全部组终结）删 +
    /// 地址 &gt; Ring 尾删（超尾悬空——掉电丢内存尾但锚点帧已物化在途条目，
    /// 2026-08-27 启用载帧后此窗口真实可达）；② 索引重建——Ring 扫 [floor, Tail) 的延迟 record
    /// （key.DueTime ≠ 0）不在索引则重插（崩溃窗口：record 已落盘、索引未持久）。
    /// 返回 (清理数, 重插数)。
    /// </summary>
    /// <param name="ring">已就绪的数据 Ring（重建扫 [floor, Tail)）。</param>
    /// <param name="floor">截断下界（清理地址 &lt; floor 的悬空条目；Invalid = 跳过清理）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(清理条数, 重插条数)。</returns>
    internal async Task<(int Cleaned, int Reinserted)> ReconcileAsync(
        RingOfQueueKey ring, LogicalAddress floor, CancellationToken ct)
    {
        int cleaned = 0;
        // ① 清理：地址 < floor（已截断）或 > Ring 尾（悬空）的条目
        var stale = new List<QueueKey>();
        using (var cursor = _index.CreateScanCursor(ReadDirection.Forward))
        {
            while (cursor.MoveNext())
            {
                var value = cursor.CurrentValue;
                if (floor.IsValid && value < floor)
                    stale.Add(cursor.CurrentKey);
                else if (value > ring.TailAddress)
                    stale.Add(cursor.CurrentKey);
            }
        }
        foreach (var k in stale)
        {
            if (_index.Delete(k)) cleaned++;
        }

        // ② 重建：Ring 扫延迟 record → 索引缺失重插
        int reinserted = 0;
        if (floor.IsValid)
        {
            var known = new HashSet<LogicalAddress>();
            using (var cursor = _index.CreateScanCursor(ReadDirection.Forward))
            {
                while (cursor.MoveNext())
                    known.Add(cursor.CurrentValue);
            }

            await foreach (var (key, addr, _) in ring.ScanAsync(floor, ring.TailAddress, ct).ConfigureAwait(false))
            {
                if (key.DueTime == 0) continue;   // 即时消息
                if (known.Contains(addr)) continue;
                Insert(key.DueTime, addr);
                reinserted++;
            }
        }
        return (cleaned, reinserted);
    }

    /// <summary>截断下界分量：索引最小存活地址（未就绪/未终结延迟消息钉住数据——spec §8.1）。</summary>
    /// <param name="fallback">索引空时的回退地址（= Ring 头）。</param>
    /// <returns>索引最小存活地址；索引空 = fallback。</returns>
    internal LogicalAddress MinLiveAddress(LogicalAddress fallback)
    {
        using var cursor = _index.CreateScanCursor(ReadDirection.Forward);
        return cursor.MoveNext() ? cursor.CurrentValue : fallback;
    }

    /// <inheritdoc/>
    public void Dispose() => _index.Dispose();

    /// <inheritdoc/>
    public async ValueTask DisposeAsync() => await _index.DisposeAsync().ConfigureAwait(false);
}

/// <summary>
/// 延迟索引专用键空间适配 resolver：record 分类键 (due, 0) → 索引键 (due, addr.Offset)
/// （Ring 原生 TryGetKey 返回 record 键——索引键 tiebreaker 是地址，写后即知同款；
/// 即时消息 DueTime=0 过滤——非延迟 record 返回 false）。
/// </summary>
/// <param name="ring">数据 Ring（读 record key 取 dueTime + 地址作 tiebreaker）。</param>
internal sealed class DelayedKeyResolver(RingOfQueueKey ring) : IKeyResolver<QueueKey>
{
    /// <inheritdoc/>
    public bool TryGetKey(LogicalAddress addr, out QueueKey key)
    {
        if (!ring.TryGetKey(addr, out var recordKey) || recordKey.DueTime == 0)
        {
            key = default;
            return false;
        }
        key = new QueueKey(recordKey.DueTime, addr.Offset);
        return true;
    }

    /// <inheritdoc/>
    public LogicalAddress GetFlushedWatermark() => ring.GetFlushedWatermark();

    /// <inheritdoc/>
    public async IAsyncEnumerable<(QueueKey Key, LogicalAddress Address, bool IsTombstone)> ScanAsync(
        LogicalAddress begin, LogicalAddress end,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var (recordKey, addr, tomb) in ring.ScanAsync(begin, end, ct).ConfigureAwait(false))
        {
            if (recordKey.DueTime != 0)
                yield return (new QueueKey(recordKey.DueTime, addr.Offset), addr, tomb);
        }
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<(QueueKey Key, LogicalAddress Address, bool IsTombstone)> ScanAsync(CancellationToken ct = default)
        => ScanAsync(ring.BeginAddress, ring.TailAddress, ct);
}
