namespace TC.Tier.Runtime.Storage;

internal sealed partial class StorageEngine
{
    // ═══════════════════════════════════════════════════════════════
    //  Read（跨段查表）
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public int Read(LogicalAddress source, Span<byte> destination)
    {
        ThrowIfDisposed();
        EnsureReady();
        if (destination.IsEmpty) return 0;

        // ★ 单段零分配快路径：请求整体落在单段可见前缀内（节点/记录读的绝对主流形态）——
        //   走 List 计划 + SpinRWLock[] + plan.All 闭包的慢路径每读 ~0.5KB 堆分配，
        //   是未缓存层节点读的主税负（KV 组合点查 3 读/FIND ≈ 1KB+/FIND 的来源）。
        var seg = _segmentTable.GetSegment(source.SegId);
        if (seg.StableState == StableState.Invalid)
            throw new PartitionInvalidException("Segment not found.", source);
        if (seg.VisibleOffset - source.Offset >= destination.Length)
        {
            ReadSingleSegment(source.SegId, source.Offset, destination);
            return destination.Length;
        }

        var plan = BuildReadPlan(source, destination.Length);
        var locks = AcquireReadPlan(_segmentTable, plan, CancellationToken.None);
        try
        {
            var totalRead = 0;
            foreach (var chunk in plan)
            {
                using var handle = GetReadHandle(chunk.SegId, usePageCache: true);
                totalRead += handle.Read(
                    chunk.Offset,
                    destination.Slice(totalRead, chunk.Length));
            }
            return totalRead;
        }
        finally
        {
            ReleaseReadPlan(locks);
        }
    }

    /// <summary>
    /// 单段读——锁协议对齐 <see cref="AcquireReadPlan"/> 的全有或全无/终验/活性守卫（单段退化为单锁）
    /// <para>★ 前置条件（调用方守卫）：[offset, offset+len) 整体在该段可见前缀内——无跨段、无裁剪。</para>
    /// </summary>
    private void ReadSingleSegment(int segId, long offset, Span<byte> destination)
    {
        var end = offset + destination.Length;
        var spinner = new SpinWait();
        SpinRWLock held = null!;
        while (true)
        {
            while (!_segmentTable.IsRangeFullyReadable(segId, offset, end))
            {
                // 活性守卫（对齐 AcquireReadPlan）：终态不可读（Aborted/Wasted）永不变可读，无限自旋=挂死
                if ((spinner.Count & 63) == 63
                    && _segmentTable.ContainsPermanentlyUnreadable(segId, offset, end))
                    throw new PartitionInvalidException(
                        $"Range [{segId}@{offset}..{end}) 含终态不可读区间（aborted/wasted）。",
                        new LogicalAddress(segId, offset));
                spinner.SpinOnce();
            }

            if (_segmentTable.TryGetLock(segId, out var segLock) && segLock is not null)
            {
                segLock.AcquireShared();
                if (_segmentTable.IsRangeFullyReadable(segId, offset, end))
                {
                    held = segLock;
                    break;
                }
                segLock.ReleaseShared();   // 终验失败（建段/COW 替换竞态）——回退重试
            }
            spinner.SpinOnce();
        }

        try
        {
            using var handle = GetReadHandle(segId, usePageCache: true);
            handle.Read(offset, destination);
        }
        finally
        {
            held.ReleaseShared();
        }
    }

    /// <inheritdoc/>
    /// <remarks>真异步：走 <see cref="IFileHandle.ReadAsync"/>，不阻塞调用线程。</remarks>
    public async ValueTask<int> ReadAsync(
        LogicalAddress source,
        Memory<byte> destination,
        CancellationToken ct)
    {
        ThrowIfDisposed();
        EnsureReady();
        if (destination.IsEmpty) return 0;

        var plan = BuildReadPlan(source, destination.Length);
        var locks = AcquireReadPlan(_segmentTable, plan, ct);
        try
        {
            var totalRead = 0;
            foreach (var chunk in plan)
            {
                ct.ThrowIfCancellationRequested();
                using var handle = GetReadHandle(chunk.SegId, usePageCache: true);
                totalRead += await handle.ReadAsync(
                        chunk.Offset,
                        destination.Slice(totalRead, chunk.Length),
                        ct)
                    .ConfigureAwait(false);
            }
            return totalRead;
        }
        finally
        {
            ReleaseReadPlan(locks);
        }
    }

    private List<ReadPlanChunk> BuildReadPlan(LogicalAddress source, int requestedLength)
    {
        var plan = new List<ReadPlanChunk>();
        var remaining = requestedLength;
        var segId = source.SegId;
        var segOffset = source.Offset;
        var maxSegId = _segmentTable.MinSegId + _segmentTable.SegCount - 1;

        while (remaining > 0 && segId <= maxSegId)
        {
            // ★ 读属性用 SegmentView（只读视图，不持 Segment 引用）
            var segment = _segmentTable.GetSegment(segId);
            if (segment.StableState == StableState.Invalid)
                throw new PartitionInvalidException(
                    "Segment not found.",
                    new LogicalAddress(segId, segOffset));

            // ★ 按 VisibleOffset（读门权威）裁剪，不按 MaxOffset：CommittedTail/MaxOffset 是游标
            //   （Allocate 即推），可读性跟物理提交的 extent 走。旧实现按 MaxOffset 裁出
            //   "游标-可见差值区间"（未写占位，永不可见）→ AcquireReadPlan 的 while(!Readable)
            //   永真自旋楔死（DiskDevice_AllocateThenWrite 取证：Allocate 1024 只写 1000，
            //   Read(1024) 请求 [0,1024) 而 VisibleOffset=1000 → 无限自旋）。
            //   语义：请求超出可见前缀 → 返回可见部分（部分读，对齐 Read_Partial 契约）。
            var readable = segment.VisibleOffset - segOffset;
            if (readable > 0)
            {
                var length = (int)Math.Min(remaining, readable);
                plan.Add(new ReadPlanChunk(segId, segOffset, length));   // ★ 只持 segId，不持 Segment
                remaining -= length;
            }

            segId++;
            segOffset = 0;
        }

        return plan;
    }

    /// <summary>
    /// 加共享读锁 + 校验可读——经段表包装（segId 级，不接触 Segment）。
    /// <para>★ <b>全有或全无</b>：任一 plan 段的锁拿不到（TryGetLock=false，建段窗口内段未入表）→ 整体回退重试。
    ///   保证不变量：<b>成功返回时持有全部 plan 段的共享锁</b>——释放（ReleaseReadPlan）才永远配对。</para>
    /// <para>★ 修复的 bug（dump 取证 2026-08-14）：旧实现按 TryGetLock 逐段"有则拿"，失败回退时重新 TryGetLock 释放——
    ///   建段竞态窗口内 acquire=false/release=true 两阶段结果翻转 → <b>释放从未获取的锁</b> → 读计数变负
    ///   （实测 -3）→ 负数借位到高位假"写者位" → reader/Dispose 全部永久自旋楔死。</para>
    /// </summary>
    private static SpinRWLock[] AcquireReadPlan(
        SegmentTable segmentTable,
        IReadOnlyList<ReadPlanChunk> plan,
        CancellationToken cancellationToken)
    {
        var locks = new SpinRWLock[plan.Count];
        var spinner = new SpinWait();
        while (true)
        {
            while (!IsReadPlanReadable(segmentTable, plan))
            {
                cancellationToken.ThrowIfCancellationRequested();
                spinner.SpinOnce();
                // ★ 活性守卫：终态不可读（Aborted/Wasted——失败写/回收垃圾）永不变可读，
                //   无限自旋 = 挂死（ReclaimAsync 部分失败后读 Abort 区间实测楔死）——
                //   每 64 圈自旋探测一次，命中即快速失败（与"读已删段抛 PartitionInvalidException"同语义）
                if ((spinner.Count & 63) == 63)
                    foreach (var c in plan)
                        if (segmentTable.ContainsPermanentlyUnreadable(c.SegId, c.Offset, c.Offset + c.Length))
                            throw new PartitionInvalidException(
                                $"Range [{c.SegId}@{c.Offset}..{c.Offset + c.Length}) 含终态不可读区间（aborted/wasted）。",
                                new LogicalAddress(c.SegId, c.Offset));
            }

            var acquired = 0;
            var success = false;
            try
            {
                for (; acquired < plan.Count; acquired++)
                {
                    // ★ 全有或全无：任一段锁拿不到 → break 整体回退；解析到的实例存 locks[]，释放只认它
                    if (!segmentTable.TryGetLock(plan[acquired].SegId, out var segLock) || segLock is null)
                        break;
                    segLock.AcquireShared();
                    locks[acquired] = segLock;
                }

                // ★ 全部 plan 锁在手才做终验；成功返回 locks——释放方只释放这些实例，配对由此保证
                if (acquired == plan.Count)
                {
                    success = IsReadPlanReadable(segmentTable, plan);
                    if (success)
                        return locks;
                }
            }
            finally
            {
                if (!success)
                {
                    for (var i = acquired - 1; i >= 0; i--)
                        locks[i]?.ReleaseShared();
                }
            }

            spinner.SpinOnce();
        }
    }

    /// <summary>
    /// 校验读计划可读——经段表包装（segId 级，不接触 Segment）。
    /// <para>★ 手写循环不用 LINQ <c>All</c>：闭包+委托+接口枚举器装箱在自旋重试路径上每圈分配
    /// （<see cref="AcquireReadPlan"/> 首检+终验两处调用）——读热路径禁 LINQ。</para>
    /// </summary>
    private static bool IsReadPlanReadable(
        SegmentTable segmentTable,
        IReadOnlyList<ReadPlanChunk> plan)
    {
        var count = plan.Count;
        for (var i = 0; i < count; i++)
        {
            var chunk = plan[i];
            if (!segmentTable.IsRangeFullyReadable(chunk.SegId, chunk.Offset, chunk.Offset + chunk.Length))
                return false;
        }
        return true;
    }

    /// <summary>释放读计划锁——只释放 AcquireReadPlan 返回的<b>实际获取实例</b>（逆序），不重新解析 segId→锁。</summary>
    private static void ReleaseReadPlan(SpinRWLock[] locks)
    {
        for (var i = locks.Length - 1; i >= 0; i--)
            locks[i]?.ReleaseShared();
    }

    /// <summary>读计划条目——只持 segId（稳定标识），不持 Segment 引用（COW 替换会让段引用悬挂）。</summary>
    private readonly record struct ReadPlanChunk(
        int SegId,
        long Offset,
        int Length);
}
