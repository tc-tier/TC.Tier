using System.Collections.Concurrent;
using System.Diagnostics;

namespace TC.Tier.Runtime.Storage;

/// <summary>
/// 后台任务管理 partial——基类统一注册/跟踪/取消/等待所有 device 级后台 Task。
/// <para>★ 统一管理：Compact / Reclaim / 未来其他后台任务都注册到 <see cref="_backgroundTasks"/> 表，
///   Dispose 时统一 cancel（<see cref="_backgroundCts"/>）+ 等待全部退出。</para>
/// <para>★ 范式：调用方持引用（绝不 <c>_ = Task.Run(...)</c>），通过 <see cref="RunBackgroundTask"/> 注册。</para>
/// <para>★ Compact 额外排他：<see cref="TryEnterCompactingOrFail"/> CAS 保证同一时刻至多 1 个 Compact
///   （Reclaim 不排他，可并发）。</para>
/// </summary>
internal sealed partial class StorageEngine
{
    // === 后台任务统一跟踪（device 级） ===

    // ★ 后台任务取消令牌——Dispose 时 cancel，触发所有 in-flight 后台任务回滚/退出
    private readonly CancellationTokenSource _backgroundCts = new();

    // ★ 后台 Task 跟踪表——精确观测/等待 in-flight Task（Compact/Reclaim/... 统一注册）
    private readonly ConcurrentDictionary<Guid, Task> _backgroundTasks = new();

    // ★ Compact 排他标志——CAS 0→1，保证同一时刻至多 1 个 Compact（Reclaim 等不排他）
    private int _compacting;

    // ★ L20（）：活跃一致快照读者计数——与 _compacting 双相门：
    //   读者构造：等 compacting==0 → ++count → 复查 compacting（仍 0 才生效，否则让位重试）；
    //   Compact 入闸后：等 count==0 再开工。互斥闭环：Compact 期间新一致读者进不来，
    //   在途一致读者读完才换内脏——快照语义从"锁实例"升级为"布局切换前清场"。
    private int _consistentReaders;

    /// <summary>L20：等活跃一致快照读者清零——Compact 三个入口在 TryEnterCompactingOrFail 之后、
    /// lease 建立之后调用（compacting 已置 1，读者侧复查必让位）。</summary>
    private void WaitForConsistentReadersIdle()
    {
        var spinner = new SpinWait();
        while (Volatile.Read(ref _consistentReaders) != 0)
            spinner.SpinOnce();
    }

    // ★ Compact 覆盖的段范围（见下方 CompactRangeInfo struct——单 struct 原子读，防撕裂状态）。
    //   Compact 原地替换段文件（rename），worker 队列里对这些段的遗留 Full 任务若被处理，
    //   会 GetWriteHandle 建句柄占住尚未 rename 的旧文件致 rename 失败。
    //   范围在 Compact 入口记录、finally 清除（Volatile 写，worker 异步读）。

    /// <summary>当前 in-flight 后台 Task 数（观测指标）。</summary>
    private int BackgroundTaskCount => _backgroundTasks.Count;

    /// <summary>
    /// 统一启动后台任务——注册到 <see cref="_backgroundTasks"/>，完成（成功/失败/取消）后自动清理。
    /// <para>★ 调用方持返回的 Task 引用（或经 <see cref="IAsyncOperation"/> 句柄对外），
    ///   绝不 <c>_ = Task.Run(...)</c> 丢弃引用。</para>
    /// <para>★ taskBody 内部应响应 <paramref name="ct"/>（= BackgroundCts.Token，Dispose 时 cancel）。</para>
    /// <para>★ 无论 taskBody 成功/抛异常/取消，finally 都会从 BackgroundTasks 移除。</para>
    /// <para>★ 不带排他——Reclaim 等可并发；Compact 入口调用前须自己 <see cref="TryEnterCompactingOrFail"/>。</para>
    /// </summary>
    /// <param name="taskBody">后台主体——接收取消令牌（绑定 BackgroundCts），返回后台 Task。</param>
    /// <returns>该后台 Task（已注册到跟踪表）。</returns>
    private Task RunBackgroundTask(Func<CancellationToken, Task> taskBody)
    {
        var ct = _backgroundCts.Token;
        var taskId = Guid.NewGuid();

        var task = Task.Run(async () =>
        {
            try
            {
                await taskBody(ct).ConfigureAwait(false);
            }
            finally
            {
                _backgroundTasks.TryRemove(taskId, out _);
            }
        }, ct);

        _backgroundTasks[taskId] = task;
        return task;
    }

    /// <summary>
    /// 统一启动后台任务（带返回值版本）——注册到 <see cref="_backgroundTasks"/>，完成后自动清理。
    /// </summary>
    private Task<T> RunBackgroundTask<T>(Func<CancellationToken, Task<T>> taskBody)
    {
        var ct = _backgroundCts.Token;
        var taskId = Guid.NewGuid();

        var task = Task.Run(async () =>
        {
            try
            {
                return await taskBody(ct).ConfigureAwait(false);
            }
            finally
            {
                _backgroundTasks.TryRemove(taskId, out _);
            }
        }, ct);

        _backgroundTasks[taskId] = task;
        return task;
    }

    // === Compact 排他（仅 Compact 用，Reclaim 不用） ===

    /// <summary>
    /// 排他进入 Compact——CAS _compacting 0→1，失败抛 InvalidOperationException。
    /// </summary>
    private void TryEnterCompactingOrFail()
    {
        if (Interlocked.CompareExchange(ref _compacting, 1, 0) != 0)
            throw new InvalidOperationException("另一个 Compact 操作正在进行中");
    }

    /// <summary>退出 Compact 排他——_compacting 1→0。</summary>
    private void ExitCompacting() => Volatile.Write(ref _compacting, 0);

    /// <summary>
    /// ★ Compact 范围信息——类引用类型以支持 Volatile.Read 单次原子读取。
    /// <para>每次 EnterCompactRange 创建新实例并 Volatile.Write，保证 reader 看到的(Action, StartSeg, EndSeg)三元组一致。</para>
    /// </summary>
    private CompactRangeInfo _compactRange = CompactRangeInfo.Inactive;

    private sealed class CompactRangeInfo
    {
        public bool Active;
        public int StartSeg;
        public int EndSeg;

        public static readonly CompactRangeInfo Inactive = new();

        public bool Contains(int segId) => Active && segId >= StartSeg && segId <= EndSeg;
    }

    /// <summary>
    /// 记录 Compact 覆盖的段范围——worker 的遗留 Full 任务据此跳过整理中段。
    /// <para>★ 必须在 <see cref="TryEnterCompactingOrFail"/> 之后调。替换文件的全量 Compact 还须先
    ///   <see cref="ReleaseAllHandles"/>；原地搬移的 RangeCompact 不释放共享句柄。
    ///   范围用闭区间 [startSeg, endSeg]（含两端段）。</para>
    /// </summary>
    private void EnterCompactRange(LogicalAddress start, LogicalAddress end)
    {
        var info = new CompactRangeInfo
        {
            Active = true,
            StartSeg = start.SegId,
            EndSeg = end.SegId,
        };
        Volatile.Write(ref _compactRange, info);
    }

    /// <summary>清除 Compact 段范围——Compact 完成（finally）调。</summary>
    private void ExitCompactRange() => Volatile.Write(ref _compactRange, CompactRangeInfo.Inactive);

    /// <summary>
    /// 判断 segId 是否在当前 Compact 范围内——<see cref="OnSegmentFullAsyncCore"/> 据此跳过。
    /// <para>★ 单次 Volatile.Read 读取整个 CompactRangeInfo 引用，避免三元组撕裂。</para>
    /// </summary>
    private bool IsSegmentUnderCompact(int segId)
    {
        var range = Volatile.Read(ref _compactRange);
        return range.Contains(segId);
    }

    /// <summary>等待 Compact 完全终止——SpinWait 等 _compacting 释放，超时返回 false（Dispose 用）。</summary>
    private bool WaitForCompactingRelease(TimeSpan timeout)
    {
        var spin = new SpinWait();
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (Volatile.Read(ref _compacting) != 0)
        {
            if (DateTimeOffset.UtcNow > deadline) return false;
            spin.SpinOnce();
        }

        return true;
    }

    /// <summary>HoleRatio 缓存——避免重复 syscall 查询 OS 分配。</summary>
    private readonly ConcurrentDictionary<int, (double Ratio, long Timestamp)> _holeRatioCache = new();

    /// <summary>缓存 TTL（毫秒）。历史段分配不变，可长期缓存；活跃段短 TTL 防脏读。</summary>
    private const long HoleRatioCacheTtlMs = 30_000;

    /// <summary>失效指定段的 HoleRatio 缓存（PunchHole/Reclaim 后调用）。</summary>
    private void InvalidateHoleRatioCache(int segId)
        => _holeRatioCache.TryRemove(segId, out _);

    /// <inheritdoc/>
    public double GetHoleRatio(int segId)
    {
        var ttlMs = HoleRatioCacheTtlMs;
        if (_holeRatioCache.TryGetValue(segId, out var entry))
        {
            // ★ 使用 Timestamp * 1000 / Frequency 标准模式，避免 Frequency < 1000 时除以零
            long nowMs = Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;
            if (nowMs - entry.Timestamp < ttlMs)
                return entry.Ratio;
        }

        long nowMs2 = Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;
        var ratio = ComputeHoleRatio(segId);
        _holeRatioCache[segId] = (ratio, nowMs2);
        return ratio;
    }

    private double ComputeHoleRatio(int segId)
    {
        // ★ 恢复被注释的实现（适配 Registry→SegmentTable + SegmentView）。
        //   Evidence5 契约：Append 后 HoleRatio 不为负——RealSize 修复（minOffset 位序）后逻辑值正常。
        var segment = _segmentTable.GetSegment(segId);
        if (!segment.IsValid || segment.StableState == StableState.Invalid)
            return 0.0;

        if (!TryEnumerateAllocatedRanges(segId, out var ranges))
            return 0.0;

        long allocated = ranges.Sum(r => Math.Min(r.End, segment.RealSize) - Math.Max(r.Start, 0));
        long logical = segment.RealSize;
        if (logical <= 0) return 0.0;
        allocated = Math.Min(allocated, logical);
        return 1.0 - (double)allocated / logical;
    }

    /// <inheritdoc/>
    public double GetHoleRatio(LogicalAddress from, LogicalAddress to)
    {
        // ★ 恢复被注释的实现（适配 Registry→SegmentTable + SegmentView）。
        if (from >= to)
            return 0.0;

        long totalLogical = 0, totalAllocated = 0;
        int startSeg = from.SegId;
        int endSeg = to.SegId;

        for (int seg = startSeg; seg <= endSeg; seg++)
        {
            var segment = _segmentTable.GetSegment(seg);
            if (!segment.IsValid || segment.StableState == StableState.Invalid)
                continue;

            long segStart = (seg == startSeg) ? from.Offset : 0;
            long segEnd = (seg == endSeg) ? Math.Min(to.Offset, segment.RealSize) : segment.RealSize;
            long segLogical = segEnd - segStart;
            if (segLogical <= 0) continue;

            if (!TryEnumerateAllocatedRanges(seg, out var ranges))
                continue;

            long segAllocated = 0;
            foreach (var r in ranges)
            {
                long start = Math.Max(r.Start, segStart);
                long end = Math.Min(r.End, segEnd);
                if (end > start)
                    segAllocated += end - start;
            }

            totalLogical += segLogical;
            totalAllocated += Math.Min(segAllocated, segLogical);
        }

        if (totalLogical <= 0) return 0.0;
        return 1.0 - (double)totalAllocated / totalLogical;
    }

    private bool TryEnumerateAllocatedRanges(int segId, out IReadOnlyCollection<(long Start, long End)> ranges)
    {
        ranges = Array.Empty<(long, long)>();
        try
        {
            using var handle = GetReadHandle(segId, usePageCache: true);
            ranges = handle.EnumerateAllocatedRanges();
            return true;
        }
        catch
        {
            return false;
        }
    }


    // === Compact 4 入口（基类统一实现，委托 _compact）===

    /// <summary>
    /// ★ 整理入口释放范围内句柄（A8：整理不挡追加）——只释放 [from..to] 覆盖段的缓存句柄，
    /// 前沿段（CommittedTail 之外、并发写者正在写的段）句柄不动。
    /// <para>★ 背景：promote 是 rename 替换段文件——只有范围内段的旧句柄会指向旧 inode（III-2）。
    ///   原 ReleaseAllHandles 全引擎清句柄会把并发追加者正在用的前沿段句柄一并 dispose
    ///   （ChaseCompactionSimulationTests 实锤：写者 seg#212 远在范围外仍 ODE）——
    ///   A8 追赶模型（边追加边整理）下违约。范围外句柄零收益零必要，收窄为范围释放。</para>
    /// <para>★ 尾段（部分在范围内）的句柄仍会释放——尾段 promote 整文件换名，其句柄必须换新
    ///   （写路径 disposed-重试兜底，见 CopyChunks）。</para>
    /// </summary>
    private void ReleaseCompactRangeHandles(LogicalAddress from, LogicalAddress to)
    {
        var lastSeg = to.Offset == 0 ? to.SegId - 1 : to.SegId;
        for (var segId = from.SegId; segId <= lastSeg; segId++)
            ReleaseSegmentHandles(segId);
    }

    /// <summary>
    /// Compact 失败分流（设计决策：失败决策权归使用方）——句柄冲突（rename 撞共享违例）
    /// 由引擎自己处理：关缓存句柄 + ICompact.Retry 从 marker 续传（补执行，不重拷贝）。
    /// <para>★ 挂 op.Failed 事件且<b>先于门闩释放订阅</b>（事件按订阅顺序同步触发）：续传在引擎排他
    ///   （<c>_compacting</c>=1）内执行，无并发 Compact 干扰；续传后门闩正常释放。</para>
    /// <para>★ 非句柄冲突失败不处理（回滚/传播由调用方决策）；续传失败现场保留
    ///   （marker+临时文件）——下次启动 Recover 兜底。</para>
    /// <para>★ 语义注记：续传成功后调用方看到的仍是 op.Failed（终态不可逆）——引擎已恢复现场，
    ///   调用方重试安全（无 marker 阻挡、零重拷贝）。</para>
    /// </summary>
    private void TryRecoverCompactFromHandleConflict(Exception ex)
    {
        if (ex is not FileIOException fie || fie.Error != IOError.SharingViolation) return;
        try
        {
            Logger?.LogWarning(fie, "Compact rename 撞句柄占用（SharingViolation）——关缓存句柄后从 marker 续传");
            ReleaseCompactRangeHandles(MinAddress, CommittedTail);
            _compact.Retry((start, end) => _segmentTable.CompactLease(start, end));
            Logger?.LogInformation("Compact 句柄冲突续传成功（未重拷贝）");
        }
        catch (Exception recoverEx)
        {
            Logger?.LogWarning(recoverEx, "Compact 句柄冲突续传失败——现场保留（marker+临时文件），下次启动恢复兜底");
        }
    }

    /// <summary>
    /// 启动区间 Compact（申报活区间版，§XVIII/A8）——搬迁规划按使用方申报的活记录区间执行（记录粒度）。
    /// <para>★ 根因：Reclaim 打洞是记录粒度，物理 allocated 是簇粒度（NTFS 簇内有存活邻居的洞整簇
    ///   保配，fsutil 实证）、区间表无记录粒度洞位（VII-3 合并大记录 sparse 位）——<b>记录粒度真相
    ///   只在使用方</b>。小记录追赶整理必须由使用方申报活区间，否则全量拷贝零回收。</para>
    /// <para>★ 契约：申报区间必须 ⊆ [from, to)（越界抛）；未申报的已分配区间视为洞不搬迁——
    ///   漏报活数据 = 该数据整理后不可达。范围外（from 前、to 后）仍物理保守保留。</para>
    /// <para>★ 同步入口废除（强制等待死锁风险）——后台句柄形态，0 等待返回。</para>
    /// </summary>
    public IAsyncOperation<CompactResult> StartRangeCompact(LogicalAddress from, LogicalAddress to,
        IReadOnlyList<(LogicalAddress Start, long Length)> liveRecords)
    {
        ArgumentNullException.ThrowIfNull(liveRecords);
        var (addresses, plan) = BuildLiveCompactPlan(from, to, liveRecords);
        return StartRangeCompactCore(from, to, addresses, plan);
    }

    /// <inheritdoc/>
    public IAsyncOperation<CompactResult> StartRangeCompact(LogicalAddress from, LogicalAddress to,
        IReadOnlyList<LogicalAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        return StartRangeCompactCore(from, to, addresses, livePlan: null);
    }

    /// <summary>
    /// 启动区间 Compact 核心——入闸/lease/读者门同步完成（排他语义同 StartCompact），
    /// 搬迁执行体丢后台（RunBackgroundTask），句柄驱动完成/失败/取消（0 等待，无强制等待死锁面）。
    /// </summary>
    private IAsyncOperation<CompactResult> StartRangeCompactCore(LogicalAddress from, LogicalAddress to,
        IReadOnlyList<LogicalAddress> addresses,
        IReadOnlyDictionary<int, List<(long Start, long End)>>? livePlan)
    {
        ThrowIfDisposed();
        EnsureReady();
        EnsureCompactSupported();
        ValidateRangeCompactBounds(from, to);

        TryEnterCompactingOrFail();
        CompactLease? lease = null;
        try
        {
            var lastSegId = to.Offset == 0 ? to.SegId - 1 : to.SegId;
            var leaseFrom = new LogicalAddress(from.SegId, 0);
            // ★ L19（）：lease 覆盖 [from 段@0, 尾段 GrowthLimit] 整段——
            //   尾段不再钳到 CommittedTail：追加在尾段 [CommittedTail.Offset, GrowthLimit)
            //   贴边启动，旧钳制下 CanAcquireUnsafe 判无重叠放行 → promote rename 旧 inode
            //   丢写 / 换段后 CompleteAndMerge 静默 no-op（P0）。全 GrowthLimit 覆盖使
            //   追加与整理在尾段互斥（整理等写入者、写入者等整理），A8不挡追加以
            //   有界阻塞不失败形式保持（范围外段仍零影响）。
            //   整理数据窗仍为 [from, to]（to ≤ CommittedTail，bounds 校验保证）——lease
            //   只加锁不加数据。
            var leaseTo = new LogicalAddress(lastSegId, _segmentTable.SegmentGrowthLimit(lastSegId));

            // ★ promote 是 rename 替换段文件，引擎 _handleCache 的旧句柄指向旧 inode——不预释放则
            //   紧凑后现场读全零（探针取证）。★ A8 收窄：只释放整理范围段——前沿段句柄归并发写者
            //   （ChaseCompactionSimulationTests 实锤全量释放致范围外写者 ODE）。
            ReleaseCompactRangeHandles(leaseFrom, leaseTo);

            lease = _segmentTable.CompactLease(leaseFrom, leaseTo);
            // ★ 使用租约的实际范围（非子范围 from→to），确保租约覆盖的整段都被标记为 compact 中
            EnterCompactRange(leaseFrom, leaseTo);
            // ★ L20（）：一致读者清零门（同 StartCompact 契约）。
            WaitForConsistentReadersIdle();

            // ★ 子系统统一异步形态（）：ICompact.RangeCompact 返回句柄（后台执行在子系统内），
            //   引擎直接持有——同 StartCompact 完全同构；lease 生命周期移交子系统（完成后释放）
            var op = _compact.RangeCompact(lease, from, to, addresses, livePlan);

            // 门闩挂终态事件（时序契约同 StartCompact——事件先于 TCS 置位；失败分流先于门闩释放，
            // 续传在引擎排他内执行）
            void ReleaseCompactGate()
            {
                ExitCompactRange();
                ExitCompacting();
            }
            op.Failed += (_, ex) => TryRecoverCompactFromHandleConflict(ex);
            op.Failed += (_, _) => ReleaseCompactGate();
            op.Completed += (_, _) => ReleaseCompactGate();
            if (op.IsCompleted) ReleaseCompactGate();
            return op;
        }
        catch
        {
            lease?.Dispose();
            ExitCompactRange();
            ExitCompacting();
            throw;
        }
    }

    /// <summary>
    /// 申报活记录 → (迁移地址集, 段内活区间 plan)——逐记录校验 ⊆ [from, to) 并按段边界切分
    /// （记录可跨段：按各段 GrowthLimit 切片）。
    /// </summary>
    private (List<LogicalAddress> Addresses, Dictionary<int, List<(long Start, long End)>> Plan)
        BuildLiveCompactPlan(LogicalAddress from, LogicalAddress to,
            IReadOnlyList<(LogicalAddress Start, long Length)> liveRecords)
    {
        var addresses = new List<LogicalAddress>(liveRecords.Count);
        var plan = new Dictionary<int, List<(long Start, long End)>>();
        foreach (var (start, length) in liveRecords)
        {
            if (length <= 0)
                throw new ArgumentOutOfRangeException(nameof(liveRecords),
                    $"live record @{start} length={length} 必须 > 0");
            var end = _segmentTable.AdvanceAddress(start, length);
            // ★ 窗口外记录（已定居 cursor 前 / 超尾 to 后）——不规划不迁移（MigrationMap 无项 = 地址不变）：
            //   使用方记录簿天然覆盖全引擎，窗口筛选取交集而非报错。
            if (start >= to || end <= from) continue;
            if (start < from || end > to)
                throw new ArgumentOutOfRangeException(nameof(liveRecords),
                    $"live record [{start}, {end}) 骑跨整理窗口边界（须完全在 [{from}, {to}) 内或外）——调用方记录簿与窗口不一致");
            addresses.Add(start);

            var segId = start.SegId;
            var off = start.Offset;
            var remaining = length;
            while (remaining > 0)
            {
                var limit = _segmentTable.GetSegment(segId).GrowthLimit;
                var take = Math.Min(remaining, limit - off);
                // ★ 区间统一：record 起点可以是段末边界 (seg, limit)（首字节在下一段）——take==0 合法，
                //   跳过边界进位；take<0（off 越段）才是真异常。
                if (take < 0) throw new InvalidOperationException(
                    $"申报记录切分失败 seg{segId}@{off}（段边界异常）");
                if (take == 0) { segId++; off = 0; continue; }
                if (!plan.TryGetValue(segId, out var list))
                    plan[segId] = list = new List<(long, long)>();
                list.Add((off, off + take));
                remaining -= take;
                segId++;
                off = 0;
            }
        }

        // ★ 窗口内所有段必须有 plan 条目（含空表）：CopyCompactedRange 以"缺段"判回退物理枚举——
        //   全洞段若无条目会回退物理拷簇影子数据（零字节当活数据，探针实锤 seg0 占 4KB 假前缀）。
        //   空表 = 全洞零拷贝（整理消除空洞的语义本身）。
        var windowLast = to.Offset == 0 ? to.SegId - 1 : to.SegId;
        for (var segId = from.SegId; segId <= windowLast; segId++)
            if (!plan.ContainsKey(segId))
                plan[segId] = new List<(long Start, long End)>();
        foreach (var list in plan.Values)
            list.Sort((a, b) => a.Start.CompareTo(b.Start));
        return (addresses, plan);
    }

    private void ValidateRangeCompactBounds(LogicalAddress from, LogicalAddress to)
    {
        // ★ 恢复被注释的真实实现（适配 Registry→SegmentTable + SegmentView）——
        //   RangeCompact 主体管线（CompactorBase.Range Phase 0/1/2）一直是活的，
        //   仅此处/GetHoleRatio 被桩挡住（8 个 RangeCompact 测试 + Evidence5 的 NIE 根因）。
        if (from >= to)
            throw new ArgumentOutOfRangeException(nameof(to), to, "RangeCompact requires from < to.");
        if (from < MinAddress)
            throw new ArgumentOutOfRangeException(nameof(from), from, $"from must be >= {MinAddress}.");
        if (to > CommittedTail)
            throw new ArgumentOutOfRangeException(nameof(to), to, $"to must be <= {CommittedTail}.");

        var fromSegment = _segmentTable.GetSegment(from.SegId);
        if (!fromSegment.IsValid)
            throw new ArgumentOutOfRangeException(nameof(from), from, "from references a missing segment.");
        // ★ 区间统一：from 可为段末边界 (seg, GrowthLimit)（范围首字节在下一段）——offset == GrowthLimit 合法
        if (from.Offset < 0 || from.Offset > fromSegment.GrowthLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(from), from, $"from offset must be in [0, {fromSegment.GrowthLimit}].");
        }

        var toSegment = _segmentTable.GetSegment(to.SegId);
        if (!toSegment.IsValid)
        {
            // ★ 存量/外部输入的旧哨兵形态 to=(maxSegId+1,0)（≡ 末段段末边界）防御性接受；其余抛。
            //   区间统一后新写 to 恒落真实段内（含段末边界 (seg,limit)，走下方常规校验）。
            var maxSegId = _segmentTable.MinSegId + _segmentTable.SegCount - 1;
            if (to.Offset != 0 || to.SegId != maxSegId + 1)
                throw new ArgumentOutOfRangeException(nameof(to), to, "to references a missing segment.");
            return;
        }

        if (to.Offset < 0 || to.Offset > toSegment.GrowthLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(to), to, $"to offset must be in [0, {toSegment.GrowthLimit}].");
        }
    }

    /// <inheritdoc/>
    public IAsyncOperation<CompactResult> StartCompact()
    {
        ThrowIfDisposed();
        EnsureReady();
        EnsureCompactSupported();
        TryEnterCompactingOrFail();
        CompactLease? lease = null;
        try
        {
            // ★ L19（）：lease 上界扩到尾段 GrowthLimit（同 Compact(timeout) 契约），
            //   数据窗钳 CommittedTail（lease.DataEnd）。
            var committed = CommittedTail;
            var leaseTo = new LogicalAddress(committed.SegId, _segmentTable.SegmentGrowthLimit(committed.SegId));
            ReleaseCompactRangeHandles(MinAddress, committed);  // ★ 同 Compact(timeout)：范围释放（A8）
            // 异步入口：立即返回句柄，调用方控制 Cancel/WaitAsync
            lease = _segmentTable.CompactLease(MinAddress, leaseTo);
            lease.DataEnd = committed;
            EnterCompactRange(MinAddress, committed);
            // ★ L20（）：一致读者清零门（同 Compact(timeout)）。
            WaitForConsistentReadersIdle();
            var op = _compact.Compact(lease);
            // ★ 完成时序契约（修复 flaky——故障注入重试撞"另一个 Compact 正在进行中"）：
            //   调用方经 WaitAsync 观察到完成/失败时，排他门闩必须已释放。释放挂在
            //   op 终态事件上（事件同步触发先于 TCS 置位——等待者苏醒即可重新入闸）。
            //   IsCompleted 兜底订阅竞态（op 在订阅前已终止则事件不再投递）。
            void ReleaseCompactGate()
            {
                ExitCompactRange();
                ExitCompacting();
            }
            // ★ 失败分流（同 Compact(timeout)：句柄冲突恢复先于门闩释放，续传在引擎排他内执行）。
            op.Failed += (_, ex) => TryRecoverCompactFromHandleConflict(ex);
            op.Failed += (_, _) => ReleaseCompactGate();
            op.Completed += (_, _) => ReleaseCompactGate();
            if (op.IsCompleted) ReleaseCompactGate();
            return op;
        }
        catch
        {
            lease?.Dispose();
            ExitCompactRange();
            ExitCompacting();
            throw;
        }
    }

    /// <summary>防御不变量：Compact 要求 _compact 非空——当前构造恒满足（<c>compact ?? DefaultCompactor</c>），
    /// 防未来引入"无 Compact"显式配置时公共入口仍 fail-fast。</summary>
    private void EnsureCompactSupported()
    {
        if (_compact is null)
            throw new NotSupportedException($"{nameof(StorageEngine)} does not support Compact.");
    }
}
