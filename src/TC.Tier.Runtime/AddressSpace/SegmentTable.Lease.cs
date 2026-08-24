using System.Runtime.CompilerServices;

namespace TC.Tier.Runtime.AddressSpace;

public sealed partial class SegmentTable
{
    #region 私有方法

    /// <summary>
    /// 验证逻辑地址区间 [from, to) 是否有效（from ≤ to，from ≥ MinAddress，to ≤ CommittedTail）。
    /// </summary>
    /// <param name="from">逻辑地址区间的起始地址。</param>
    /// <param name="to">逻辑地址区间的结束地址（不包含）。</param>
    /// <param name="opName">操作名称，用于异常消息。</param>
    /// <exception cref="ArgumentOutOfRangeException">当逻辑地址区间无效时抛出。</exception>
    private void ValidateRange(LogicalAddress from, LogicalAddress to, string opName)
        => ValidateRange(from, to, CommittedTail, opName);

    /// <summary>
    /// 验证逻辑地址区间 [from, to) 是否有效（from ≤ to，from ≥ MinAddress，to ≤ upperBound）。
    /// </summary>
    /// <param name="from">逻辑地址区间的起始地址。</param>
    /// <param name="to">逻辑地址区间的结束地址（不包含）。</param>
    /// <param name="upperBound">上界（默认 CommittedTail；AppendLease 未提交时传 AllocatedTail）。</param>
    /// <param name="opName">操作名称，用于异常消息。</param>
    /// <exception cref="ArgumentOutOfRangeException">当逻辑地址区间无效时抛出。</exception>
    private void ValidateRange(LogicalAddress from, LogicalAddress to, LogicalAddress upperBound, string opName)
    {
        if (from > to) throw new ArgumentOutOfRangeException(nameof(from), $"{opName}: from {from} > to {to}");
        if (from.SegId < MinSegId)
            throw new ArgumentOutOfRangeException(nameof(from), $"{opName}: from {from} < MinAddress");
        if (to > upperBound)
            throw new ArgumentOutOfRangeException(nameof(to),
                $"{opName}: [{from}, {to}) 超过上界 {upperBound}");
    }

    #endregion


    #region LeaseRef

    /// <summary>
    /// ★ 是否启用诊断跟踪——从工厂读，false 时 RegisterLease/UnregisterLease 零开销。
    /// </summary>
    public bool EnableDiagnostics => _leaseFactory.EnableDiagnostics;

    /// <summary>
    /// 注册 lease（lease Create 时调）。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void ILeaseSource.RegisterLease(ITrackedLease leaseRef)
    {
        if (!_leaseFactory.EnableDiagnostics) return; // ★ 生产模式零开销
        _activeLeaseRefs.AddOrUpdate(leaseRef.Id, leaseRef);
    }

    /// <summary>
    /// 注销 lease（lease Dispose 时调）。
    /// </summary>
    /// <param name="leaseId">Lease 的唯一标识符。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void ILeaseSource.UnregisterLease(Guid leaseId)
    {
        if (!_leaseFactory.EnableDiagnostics) return; // ★ 生产模式零开销
        _activeLeaseRefs.Remove(leaseId);
    }

    #endregion
    // ════════════════════════════════════════════════════════════
    // === lease/状态变更包装（段表收回控制权——外部不直接调段方法）===
    // <para>★ lease 三级模式：占位（此处）→ 锁外 IO → CAS 提交。extent 变更（CompleteAndMerge
    //   等）在第三级锁外执行，保持 IExtentLeaseSource 直调（不进段表，避免锁争用破坏锁外设计）。</para>
    // <para>★ 段表只包装"段级入口操作"：占区间（AcquireExtent）、失效（InvalidateSegment）、
    //   替换（ReplaceSegment）。这些是段表该知道、该触发事件的点。</para>
    // ════════════════════════════════════════════════════════════
    /// <summary>计算 [start, end) 跨段 chunk 数（零分配——无回调/闭包）。</summary>
    int ILeaseSource.GetExtentCount(LogicalAddress start, LogicalAddress end)
    {
        if (start >= end) return 0;
        var count = 0;
        var segId = start.SegId;
        var segOff = start.Offset;
        var maxSegId = MinSegId + SegCount - 1;
        while (segId <= maxSegId && (segId < end.SegId || (segId == end.SegId && segOff < end.Offset)))
        {
            if (!TryGetSegmentRaw(segId, out var seg) || seg is null || !seg.IsValid) { segId++; segOff = 0; continue; }
            var segEnd = segId == end.SegId ? end.Offset : seg.GrowthLimit;
            if (segEnd > segOff) count++;
            segId++;
            segOff = 0;
        }
        return count;
    }

    /// <summary>
    /// 遍历 [start, end) 跨段区间，逐个占住填到 buffer——零中间分配（无 List/rangesBuf）。
    /// </summary>
    int ILeaseSource.AcquireExtentsForLease(LogicalAddress start, LogicalAddress end, byte extentState,
        ExtentLease[] buffer)
    {
        if (start >= end) return 0;
        var idx = 0;
        var segId = start.SegId;
        var segOff = start.Offset;
        var maxSegId = MinSegId + SegCount - 1;
        while (segId <= maxSegId && (segId < end.SegId || (segId == end.SegId && segOff < end.Offset)))
        {
            if (!TryGetSegmentRaw(segId, out var seg) || seg is null || !seg.IsValid) { segId++; segOff = 0; continue; }
            var segEnd = segId == end.SegId ? end.Offset : seg.GrowthLimit;
            if (segEnd > segOff)
            {
                buffer[idx++] = ((IExtentLeaseSource)this).AcquireExtent(segId, segOff, segEnd, extentState);
            }
            segId++;
            segOff = 0;
        }
        return idx;
    }
    #region Lease

    /// <summary>
    /// 申请一个 <see cref="AppendLease"/>，返回 lease 对象，lease 内部包含 start 和 end。
    /// </summary>
    /// <param name="length">要分配的字节长度。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>返回一个 <see cref="LeaseBase"/> 对象。</returns>
    public AppendLease AppendLease(long length, CancellationToken ct = default)
    {
        var (start, to) = AllocateRaw(length, false, ct);
        // ★ 上界校验用 to 自身（CAS 确定值），不重读 AllocatedTail——裸读无屏障，JIT 可将其 CSE 成
        //   AllocateRaw 循环内读过的旧快照（stale start），导致"区间超出上界"误判
        //   （2026-08-21 并发撞段界实录：上界恒为"段首+payload"旧值）。CAS 成功 ⇒ to 即当前分配水位。
        ValidateRange(start, to, to, nameof(AppendLease));
        var lease = _leaseFactory.NewAppend(this, start, to, _logger);
        // ★ L13 收口（2026-08-21）：物化后校验——尾推进（AllocateRaw）与区间物化（NewAppend 占位）
        //   非原子：期间 ReclaimTail 可覆盖并退水到本区间之下（其 lease 端点含本区间），
        //   本 lease 的地址已死（后续分配将重叠发放）。物化完成时 B 已被 extent 互斥挡住
        //   （其覆盖含本区间）——此刻尾 < to ⟺ 已被退水，放弃整笔让上层重试。
        //   校验后到返回之间安全：B 越不过已物化的在途区间（CanAcquireUnsafe 排他）。
        //   ★ 读法必须撕裂/CSE 免疫（IsAllocatedBelow）：裸单读在 exact-fill 段界
        //   （下一推进 segId+offset 双变）会读出旧值假阳性（并发纯 Append 误报实录）。
        if (_tailSlot.IsAllocatedBelow(to))
        {
            lease.Dispose();
            throw new InvalidOperationException(
                $"AppendLease 放弃：区间 {start}..{to} 物化期间被 ReclaimTail 退水——请重试");
        }
        return lease;
    }

    /// <summary>
    /// 申请一个逻辑地址区间 [start, end)，长度为 length，并推进 CommittedTail，返回 (start, end)。
    /// </summary>
    /// <param name="length">要分配的字节长度。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>返回一个包含 start 和 end 的元组。</returns>
    public (LogicalAddress Start, LogicalAddress End) AllocateLease(long length, CancellationToken ct = default)
    {
        return AllocateRaw(length, true, ct);
    }

    // ════════════════════════════════════════════════════════════
    // === Write / Reclaim / ReclaimHead / ReclaimTail 入口 ===
    // ★ 地址已知不分配（与 Append 不同）——直接 ValidateRange 后经工厂造 lease。
    // ★ lease 占区间在 LeaseBase.Reset 内部完成（source.AcquireExtent 逐 chunk 占）。
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 申请 Write lease 覆写 [start, start+length)——地址已知不分配，复用已提交区间。
    /// <para>★ Write 是 pwrite 覆写，目标区间必须 ≤ CommittedTail（已提交数据才能覆写）。</para>
    /// <para>★ Commit 走单 chunk 级 CompleteAndMerge；Rollback 走 MarkWasted（可重入修复）。</para>
    /// </summary>
    /// <param name="start">起始逻辑地址（包含）。</param>
    /// <param name="length">覆写长度（字节）。</param>
    /// <param name="ct">取消令牌（占区间自旋时检查）。</param>
    /// <returns>返回一个 <see cref="LeaseBase"/> 对象。</returns>
    public WriteLease WriteLease(LogicalAddress start, long length, CancellationToken ct = default)
    {
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length), length, "Write length must be >= 0.");
        var end = AdvanceAddress(start, length);
        // ★ Write 范围 ≤ CommittedTail——写的地址全在已提交水位线内（Append 无论 Commit/Rollback 都已推
        //   CommittedTail，区别只是 Committed 有数据 vs Wasted 空记录）。Write 不改水位线，只状态流转。
        ValidateRange(start, end, nameof(WriteLease));
        // ★ 用 NewWriteRange（跨段版）——入口已算好跨段 end，NewWrite 内部用 from.Offset+length 不跨段。
        return _leaseFactory.NewWriteRange(this, start, end, _logger);
    }

    /// <summary>
    /// 申请 Reclaim lease 回收 [from, to) 中间区间——逐段 PunchHole 成空洞（Wasted）。
    /// <para>★ 设计文档 §4.1：中间 Reclaim Commit → Wasted（可被 Write 覆写的空洞）。</para>
    /// <para>★ Rollback → Aborted（PunchHole 不一致，永久洞，只 Compact 修）。</para>
    /// <para>★ 段表不变、段水位不变——只改区间状态。</para>
    /// </summary>
    /// <param name="from">回收起始逻辑地址（包含）。</param>
    /// <param name="to">回收结束逻辑地址（不包含）。</param>
    /// <param name="ct">取消令牌（占区间自旋时检查）。</param>
    /// <returns>返回一个 <see cref="LeaseBase"/> 对象。</returns>
    public ReclaimLease ReclaimLease(LogicalAddress from, LogicalAddress to, CancellationToken ct = default)
    {
        if (from >= to)
            throw new ArgumentOutOfRangeException(nameof(from), $"ReclaimLease: from {from} >= to {to}（空区间无意义）");
        ValidateRange(from, to, nameof(ReclaimLease));
        return _leaseFactory.NewReclaim(this, from, to, _logger);
    }

    /// <summary>
    /// 申请 Compact lease 占住 [from, to) 区间——整体原子提交（段表替换/失效）。
    /// <para>★ 设计文档 §4.0/§5：Compact = overlay 占住待整理区间 → 拷贝 → 原子替换段表。</para>
    /// <para>★ lease 第二阶段：消费方拷贝数据 + chunk.SetReplacement/MarkInvalid，然后 Commit。</para>
    /// </summary>
    /// <param name="from">起始逻辑地址（包含）。</param>
    /// <param name="to">结束逻辑地址（不包含）。</param>
    /// <returns>返回一个 <see cref="CompactLease"/> 对象。</returns>
    public CompactLease CompactLease(LogicalAddress from, LogicalAddress to)
    {
        if (from > to)
            throw new ArgumentOutOfRangeException(nameof(from), $"CompactLease: from {from} > to {to}");
        if (from.SegId < MinSegId)
            throw new ArgumentOutOfRangeException(nameof(from), $"CompactLease: from {from} < MinAddress");
        // ★ L19（2026-08-22）：上界校验从"to ≤ CommittedTail"放宽为"to ≤ 段几何界"——
        //   尾段 lease 须扩到 GrowthLimit 以阻断贴边追加（占区间 ≠ 数据窗；数据打包由
        //   lease.DataEnd 钳在 CommittedTail）。区间统一下 to 恒为段末边界规范形 (seg, limit)
        //   或旧哨兵 (maxSegId+1, 0)（恢复路径钳制后不再产出，防御性保留）。
        var maxSegId = MinSegId + SegCount - 1;
        if (to.SegId > maxSegId + 1 || (to.SegId == maxSegId + 1 && to.Offset != 0))
            throw new ArgumentOutOfRangeException(nameof(to), $"CompactLease: to {to} 越过段表末段 seg{maxSegId}");
        if (to.SegId <= maxSegId
            && TryGetSegmentRaw(to.SegId, out var toSeg) && toSeg is { IsValid: true }
            && to.Offset > toSeg.GrowthLimit)
            throw new ArgumentOutOfRangeException(nameof(to),
                $"CompactLease: to {to} 越过 seg{to.SegId} GrowthLimit {toSeg.GrowthLimit}");
        return _leaseFactory.NewCompact(this, from, to, _logger);
    }

    /// <summary>
    /// 申请 ReclaimHead lease 回收头部到 <paramref name="to"/>——跨段删 + ShrinkHead 推 MinAddress。
    /// <para>★ 设计文档 §4.0/§5：ReclaimHead = 跨段 MarkInvalid（段级）+ 段内 [0,to.Offset) 打洞（区间级）。</para>
    /// <para>★ lease 占 [当前 MinAddress, to) 区间；Commit 时 ShrinkHead 推 MinAddress 到 to。</para>
    /// <para>★ Commit 和 Rollback 都推 MinAddress——物理已 DeleteSegment 不可逆（§4.2）。</para>
    /// </summary>
    /// <param name="to">新 MinAddress（回收后头部边界）。</param>
    /// <param name="ct">取消令牌（占区间自旋时检查）。</param>
    /// <returns>返回一个 <see cref="LeaseBase"/> 对象。</returns>
    public ReclaimHeadLease ReclaimHeadLease(LogicalAddress to, CancellationToken ct = default)
    {
        var from = MinAddress;
        if (to < from)
            throw new ArgumentOutOfRangeException(nameof(to), to,
                $"ReclaimHead: to {to} < 当前 MinAddress {from}（头部不能反向推进）。");
        if (to == from)
            throw new ArgumentException($"ReclaimHead: to {to} == MinAddress（空操作）", nameof(to));
        ValidateRange(from, to, nameof(ReclaimHeadLease));
        return _leaseFactory.NewReclaimHead(this, from, to, _logger);
    }

    /// <summary>
    /// 申请 ReclaimTail lease 截断尾部到 <paramref name="newTail"/>——ShrinkTail 退双尾水位。
    /// <para>★ 设计文档 §4.0/§5：ReclaimTail = 尾段区间占住→删除（物理截断后跟随消失）。</para>
    /// <para>★ lease 占 [newTail, 当前 CommittedTail) 区间（被截断的部分）；Commit 时 ShrinkTail 退双尾到 newTail。</para>
    /// <para>★ Commit 和 Rollback 都退水位——物理已 SetLength 截断不可逆（§4.2）。</para>
    /// </summary>
    /// <param name="newTail">新 CommittedTail（截断后尾部边界）。</param>
    /// <param name="ct">取消令牌（占区间自旋时检查）。</param>
    /// <returns>返回一个 <see cref="LeaseBase"/> 对象。</returns>
    public ReclaimTailLease ReclaimTailLease(LogicalAddress newTail, CancellationToken ct = default)
    {
        var minAddr = MinAddress;
        if (newTail < minAddr)
            throw new ArgumentOutOfRangeException(nameof(newTail), newTail,
                $"ReclaimTail: newTail {newTail} < MinAddress {minAddr}。");
        // ★ 三种场景（基于 newTail 相对两个水位线的位置）：
        //   ① newTail < CommittedTail → 退两个水位线（截已提交数据，Committed+Allocated 都退）
        //   ② CommittedTail ≤ newTail < AllocatedTail → 退一个（只退 Allocated，中间差值区域）
        //   ③ newTail ≥ AllocatedTail → 报错（不能截断未预分配的地址）
        var allocTail = AllocatedTail;
        if (newTail >= allocTail)
            throw new ArgumentOutOfRangeException(nameof(newTail), newTail,
                $"ReclaimTail: newTail {newTail} >= AllocatedTail {allocTail}（不能截断未预分配的地址）");
        // lease 占 [newTail, AllocatedTail)——整个被截断的范围（含两水位线之间的差值区域）
        // ★ 双尾水位处理中（另一个 ReclaimTail 在途）→ 抛异常，不等待（ReclaimTail 之间不能并发退水位，不像 Append 是热路径）
        if (!_tailSlot.TryHoldTailWatermark())
            throw new InvalidOperationException(
                "ReclaimTail: 双尾水位正在被另一个 ReclaimTail 处理，不能并发");
        // ★ L13 修复（2026-08-21）：闭合"检查→CAS"反向窗口——分配者过 hold 检查后、CAS 前，
        //   本线程恰好置 hold 并重读，会误以为分配者不存在。持 hold 后重读：若尾已推进
        //   （有 CAS 在 hold 置位瞬间完成）则释放重来——分配者下一轮必被 hold 挡住，
        //   本线程下一轮快照含其区间，覆盖完备。
        allocTail = AllocatedTail;
        if (newTail >= allocTail)
        {
            _tailSlot.ReleaseTailWatermark();
            throw new ArgumentOutOfRangeException(nameof(newTail), newTail,
                $"ReclaimTail: newTail {newTail} >= AllocatedTail {allocTail}（不能截断未预分配的地址）");
        }
        try
        {
            return _leaseFactory.NewReclaimTail(this, newTail, allocTail, _logger);
        }
        catch
        {
            _tailSlot.ReleaseTailWatermark();   // 占住失败，归还标志
            throw;
        }
    }

    #endregion

    // ════════════════════════════════════════════════════════════
    // === 整体级 {Kind}Commit/Rollback（ILeaseSource——操作 lease 整体提交时调）===
    // ════════════════════════════════════════════════════════════

    #region AppendCommit/AppendRollback

    /// <summary>
    /// 申请一个逻辑地址区间 [start, end)，长度为 length，返回 start。
    /// </summary>
    /// <param name="length">要分配的字节长度。</param>
    /// <param name="isCommit">是否提交分配的地址。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>返回逻辑地址区间的起始地址。</returns>
    /// <exception cref="ArgumentOutOfRangeException">当 length 小于 0 时抛出。</exception>
    /// <exception cref="TimeoutException">当分配超时未完成时抛出。</exception>
    private (LogicalAddress Start, LogicalAddress End) AllocateRaw(long length, bool isCommit = false,
        CancellationToken ct = default)
    {
        switch (length)
        {
            case < 0:
                throw new ArgumentOutOfRangeException(nameof(length), length, "Allocate length must be >= 0.");
            case 0:
            return (AllocatedTail, AllocatedTail);
    }

    // ★ 单段模式：① tail 已跨段（SegId > MinSegId）= seg0 满 → 直接抛；② 数据超 seg0 容量 → 抛。
    //   ★ 区间统一（2026-08-21）：exact-fill（offset+length == GrowthLimit）放行后尾停驻 (seg,limit)
    //   不跨段——① 变防御性不可达；刚好装满 seg0 后下次写由 ② 抛"容量超限"（正确错误语义）。
    if (EnableSingleSegment)
    {
        var st = AllocatedTail;
        if (st.SegId > MinSegId)
            throw new InvalidOperationException($"Single-segment: seg0 full (tail at seg{st.SegId})");
        if (st.Offset + length > GrowthLimit)
            throw new InvalidOperationException(
                $"Single-segment capacity exceeded: {st.Offset} + {length} > {GrowthLimit}");
    }

    var deadline = ct.CanBeCanceled
            ? long.MaxValue
            : Environment.TickCount64 + _spinMilliseconds;
        var spinner = new SpinWait();
        var attempts = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (!ct.CanBeCanceled && Environment.TickCount64 > deadline)
                throw new TimeoutException($"Allocate 超时 attempts={attempts}");
            // ★ 双尾水位被 ReclaimTail 独占时退避（超时由上面 deadline 覆盖——和等待建段同一套）
            if (_tailSlot.IsTailWatermarkHeld)
            {
                spinner.SpinOnce();
                continue;
            }
            EnsureSegmentsForLength(length);

            var start = AllocatedTail;
            var end = AdvanceAddress(start, length);

            // ★ A7 Broken 段分配跳过：Broken = 建段失败终态（物理门永关），其地址永不可交付——
            //   分配区间不得包含 Broken 段的任何字节。命中即烧洞（水位 CAS 前推过洞，地址消费不交付）
            //   后重试，新尾在洞后、区间全落好段。lease 区间连续性契约由此保持。
            if (TrySkipBrokenHole(start, end, out var brokenSegId))
            {
                // ★ 单段模式无洞可烧——seg0 Broken = 地址空间不可用，快速失败
                if (EnableSingleSegment)
                    throw new InvalidOperationException(
                        $"Single-segment: seg{brokenSegId} build failed (Broken) — address space unusable");
                if (++attempts % _warnEvery == 0)
                    _logger?.LogWarning("Allocate 烧洞跳过 Broken 段 seg#{SegId}（建段失败终态）attempts={attempts}",
                        brokenSegId, attempts);
                spinner.SpinOnce();
                continue;
            }

            // ★ EnsureSegmentsForLength 已保证段存在——这里只做轻量校验：
            //   end 所在段（offset>0）或前一段（offset==0 段满进位）必须存在
            var checkSegId = end.Offset > 0 ? end.SegId : end.SegId - 1;
            if (!TryGetSegmentRaw(checkSegId, out _))
            {
                if (++attempts % _warnEvery == 0)
                    _logger?.LogWarning("Allocate 缺段退避 length={length} attempts={attempts}", length, attempts);
                spinner.SpinOnce();
                continue;
            }

            end = new LogicalAddress(end.SegId, start.Extension, end.Offset);
            if (_tailSlot.TryUpdateAllocated(start, end))
            {
                // ★ 8.1：首次 Allocate 成功推进水位 → 立即锁定运行阶段（恢复阶段一次性，不可逆）。
                //   TryUpdateAllocated 成功到此 CAS 的间隙理论上 ApplyHints 可读 _phase==Recovery 裸写水位，
                //   但 ApplyHints 设计为恢复期单线程（Allocate 之前同步完成），故间隙不构成活跃竞态。
                Interlocked.CompareExchange(ref _phase, (int)LifecyclePhase.Runtime, (int)LifecyclePhase.Recovery);
                if (!isCommit) return (start, end);
                // ★ L27 收口（2026-08-22）：committed 推进改 CAS 重试循环。旧实现单发失败即返回
                //   (start,end) 且不补种——并发 AllocateLease 先推尾时（其种子只盖自身区间），
                //   本区间 [start,end) 无 sparse 种子：区间在 VisibleOffset 之下却无记录 → 读门
                //   永阻（占位区永久不可读）。重试直到本区间种子落位（他人已推过 end 时只补种）。
                while (true)
                {
                    var cur = _tailSlot.Committed;
                    if (end <= cur) break;
                    var next = new LogicalAddress(end.SegId, cur.Extension, end.Offset);
                    if (!_tailSlot.TryUpdateCommitted(cur, next))
                    {
                        spinner.SpinOnce();
                        continue;
                    }
                    // ★ L25 同款防护：CAS 成功瞬间 hold 被置（ReclaimTail 退 Allocated 不动 Committed）→
                    //   本推进越过新边界——自撤销回退，下轮被外层 hold 退避挡住。
                    if (_tailSlot.IsTailWatermarkHeld)
                    {
                        _tailSlot.TryUpdateCommitted(next, cur);
                        spinner.SpinOnce();
                        continue;
                    }
                    break;
                }
                {
                    // ★ 内联遍历段表——零 List 分配（之前 GetExtentRanges 返回 new List）
                    // ★ Allocate 占位 = Committed+sparse（对齐 558fe3b9 Reclaim 打洞语义 + 打包腾空区语义）：
                    //   占位可读（读零——内存引擎零缓冲；磁盘引擎物理文件未写则 EOF 自然截短）、
                    //   可 Write 覆写（Write 的 CompleteAndMerge 落真实 Committed）。
                    //   旧实现 MarkWasted 是 Wasted conflation 残留（读门阻断 → Allocate→Write→Read 必红）。
                    var segId = start.SegId;
                    var segOff = start.Offset;
                    while (segId < end.SegId || (segId == end.SegId && segOff < end.Offset))
                    {
                        if (TryGetSegmentRaw(segId, out var seg) && seg is not null && seg.IsValid)
                        {
                            var segEnd = segId == end.SegId ? end.Offset : seg.GrowthLimit;
                            if (segEnd > segOff)
                            {
                                seg.CompleteAndMerge(segOff, segEnd, sparse: true);
                                seg.AdvanceOffset(segEnd);
                            }
                        }

                        segId++;
                        segOff = 0;
                    }

                    // ★ L13 收口（2026-08-21）：种子后校验——尾推进与 sparse 种子非原子，期间
                    //   ReclaimTail 可覆盖并退水到 end 之下（返回的预约地址已死，后续分配将重叠）。
                    //   死区 sparse 记录为可占终态，后续分配到位时自然分裂复用——无需清理。
                    //   ★ 读法撕裂/CSE 免疫（IsAllocatedBelow——裸单读 exact-fill 段界假阳性）。
                    if (_tailSlot.IsAllocatedBelow(end))
                        throw new InvalidOperationException(
                            $"Allocate 放弃：区间 {start}..{end} 种子期间被 ReclaimTail 退水——请重试");

                    return (start, end);
                }
            }

            if (++attempts % _warnEvery == 0)
                _logger?.LogWarning("Allocate CAS 重试 length={length} attempts={attempts}", length, attempts);
            spinner.SpinOnce();
        }
    }

    /// <summary>
    /// A7 Broken 段分配跳过——探测 [start, end) 覆盖的段中有无 Broken（建段失败终态，物理门永关）。
    /// <para>★ 命中即<b>烧洞</b>：AllocatedTail 水位 CAS 一次性前推过洞尾（(segId+1, 0)），洞的地址
    ///   <b>消费但不交付</b>（无 extents——读门天然阻断，读者按可见水位自然跳过）。洞前好段余量
    ///   随洞一并牺牲（上界 &lt; length）：lease 区间连续性契约要求请求要么完整落在好段、要么整段跳过，
    ///   不可部分交付。</para>
    /// <para>★ 与 Hollow 段算术同构（§3.1）：被跳过的段在逻辑地址上仍占位，AdvanceAddress/GetDistance
    ///   跨过它不塌陷——烧洞只移动水位游标，不动段/区间两支柱。</para>
    /// <para>★ 竞态界：扫描后、CAS 前才 Broken 的段会随本次区间交付（写者物理门快失败、lease 回滚），
    ///   下一次分配扫描即烧洞跳过——单次有界，不构成研磨。物理失败本就异步，物理门是裁决点
    ///   （lease-protocol §1）。</para>
    /// <para>★ 调用方：<see cref="AllocateRaw"/>（分配唯一入口，Append/Allocate 两态同规则）。
    ///   CommittedTail 不动——A6 水位双轨：粗游标可跨洞（可读性跟 extent 走，跟游标无关）。</para>
    /// </summary>
    /// <returns>true = 已烧洞（调用方重试分配）；brokenSegId = 命中的 Broken 段号。</returns>
    private bool TrySkipBrokenHole(LogicalAddress start, LogicalAddress end, out int brokenSegId)
    {
        brokenSegId = -1;
        // ★ 覆盖范围与 AllocateRaw 的存在性校验同构：end=(N,x>0) 覆盖到 N；end=(N,0)（段首/存量形态输入）只覆盖到 N-1
        var lastSeg = end.Offset > 0 ? end.SegId : end.SegId - 1;
        for (var sid = start.SegId; sid <= lastSeg; sid++)
        {
            if (!TryGetSegmentRaw(sid, out var seg) || seg is null) continue;
            if (seg.StableState != StableState.Broken) continue;
            brokenSegId = sid;
            // ★ 烧洞 CAS（expected=start 位精确）——输家（他方已烧/已推尾）由调用方循环自然重试
            _tailSlot.TryUpdateAllocated(start, new LogicalAddress(sid + 1, start.Extension, 0));
            return true;
        }
        return false;
    }

    /// <summary>
    /// 确保从当前 AllocatedTail 起有 length 字节的段空间——缺段时自动建段。
    /// </summary>
    /// <param name="length">要分配的字节数。</param>
    private void EnsureSegmentsForLength(long length)
    {
        // ★ 不读 MaxSegId——用 AllocatedTail.SegId 直接定位当前段，段不存在就建。
        //   之前每次读 MaxSegId（Volatile.Read _segCount + _segments 数组 + [count-1].SegId），
        //   且 while 循环里多次重读。改用"段不存在就建"的简单策略，零 MaxSegId 读。
        var tail = AllocatedTail;
        var segId = tail.SegId;
        var segOff = tail.Offset;
        var remaining = length;

        while (remaining > 0)
        {
            if (!TryGetSegmentRaw(segId, out var seg) || seg is null || !seg.IsValid)
            {
                AppendSegmentRaw(segId, isHighPriority: true);
                continue; // 重新尝试同一段（建完后 TryGetSegmentRaw 能拿到）
            }

            var available = seg.GrowthLimit - segOff;
            if (available <= 0)
            {
                segId++;
                segOff = 0;
                continue;
            }

            var consumed = Math.Min(remaining, available);
            remaining -= consumed;
            // ★ 单段模式：消耗完 seg0 就结束——不预建下一段（exact-fill 不跨段、不建 seg1）
            if (EnableSingleSegment)
                break;
            // 需要进下一段：① 还有剩余 ② 恰好填满当前段（预建下一段，消除后续 Append 建段延迟）
            if (remaining > 0 || segOff + consumed >= seg.GrowthLimit)
            {
                segId++;
                segOff = 0;
                // ★ 预建下一段（如果不存在）——EnsureSegmentsForLengthFixTests 验证此行为
                if (!TryGetSegmentRaw(segId, out _))
                    AppendSegmentRaw(segId, isHighPriority: true);
            }
            else break;
        }
    }

    /// <summary>
    /// 直接推进 CommittedTail 到至少 end（end ≤ cur 直接返回），绕过 lease 协议。
    /// </summary>
    /// <param name="end">要推进到的逻辑地址。</param>
    private void PromoteCommittedTailRaw(LogicalAddress end)
    {
        var spinner = new SpinWait();
        while (true)
        {
            // ★ L13 收口（2026-08-21）：水位独占（ReclaimTail 回退中）期间 Committed 推进退避。
            if (_tailSlot.IsTailWatermarkHeld)
            {
                spinner.SpinOnce();
                continue;
            }
            var cur = _tailSlot.Committed;
            if (end <= cur) return;
            // ★ L13 钳制：promote 目标不得越过 Allocated——extent 已提交但 Finalize 尚未跑完的
            //   lease（chunk Committed 可占 ⟹ ReclaimTail 可合法截断它）退水后，end 越过现行
            //   Allocated = 区间已被截掉，推上去即 Committed>Allocated 持久破坏（探针实锤）。
            //   钳到 Allocated：部分截断场景保住未截前缀，全截场景等于不推。CAS 与 Retreat 同槽
            //   串行——交错要么令我 CAS 失败重试（重读新值），要么 Retreat 随后拉回，终态恒 ≤。
            var allocated = _tailSlot.Allocated;
            var target = end < allocated ? end : allocated;
            if (target <= cur) return;
            var next = new LogicalAddress(target.SegId, cur.Extension, target.Offset);
            if (_tailSlot.TryUpdateCommitted(cur, next))
            {
                // ★ L25 收口（2026-08-22）：CAS 成功瞬间 ReclaimTail 可能已持 hold 并退 Allocated
                //   （newTail ∈ [Committed, Allocated) 场景走 RetreatAllocatedOnly——不动 Committed，
                //   expected 位精确 CAS 挡不住）→ 本推进越过新边界，C>A 持久破坏。hold 自撤销：
                //   回退 CAS（next→cur）——失败（他人已在此上推进）可容忍：其路径同样带本防护。
                if (_tailSlot.IsTailWatermarkHeld)
                {
                    _tailSlot.TryUpdateCommitted(next, cur);
                    spinner.SpinOnce();
                    continue;
                }
                return;
            }
            spinner.SpinOnce();
        }
    }

    // ════════════════════════════════════════════════════════════
    // === 终态收敛 Finalize（ILeaseSource——lease 终态时调，无方向）===
    // ★ 三对整体级 {Kind}Commit/Rollback 实现两两同体（物理不可逆）→ 收敛为单方法（设计文档 §3）。
    // ════════════════════════════════════════════════════════════

    void ILeaseSource.AppendFinalize(LogicalAddress end) => PromoteCommittedTailRaw(end);

    #endregion

    #region WriteCommit/WriteRollback

    // ★ 设计文档 §3：Write/Reclaim 无终态收敛（整体级无段表副作用）——
    //   单 chunk 级（IExtentLeaseSource）已 CompleteAndMerge/MarkWasted 改区间状态。
    //   原 4 个空方法已随接口收缩删除。

    #endregion

    #region ReclaimHeadFinalize

    void ILeaseSource.ReclaimHeadFinalize(LogicalAddress end) => ShrinkHead(end);

    /// <summary>
    /// 收缩段表头部到 end（end ≤ cur 直接返回），绕过 lease 协议。
    /// </summary>
    /// <param name="address">要收缩到的逻辑地址。</param>
    private void ShrinkHead(LogicalAddress address)
    {
        List<int>? deletedSegIds; // 锁外触发 OnSegmentDelete
        var segId = address.SegId;
        lock (_mutationLock)
        {
            var cur = MinAddress;
            if (segId <= cur.SegId)
            {
                if (address.Offset > cur.Offset)
                {
                    SetMinAddress(new LogicalAddress(cur.SegId, cur.Extension + 1, address.Offset));
                    // ★ 推段内 _minOffset——[0, address.Offset) 已被 lease 打洞回收，
                    //   不再属于本段。否则 RealSize 含已回收头部。
                    if (TryGetSegmentRaw(cur.SegId, out var sameSeg) && sameSeg is not null)
                        sameSeg.AdvanceMinOffset(address.Offset);
                }

                return;
            }

            var wasteCount = segId - cur.SegId;
            var segs = Volatile.Read(ref _segments);
            deletedSegIds = new List<int>(wasteCount);
            // ★ 按 segId 遍历 [cur.SegId, segId) 要删的段，不按数组下标——
            //   前次 ShrinkHead 标 Invalid 的段未 Compact 仍在数组前部，按下标 i 遍历会错位漏删
            for (var sid = cur.SegId; sid < segId; sid++)
            {
                var idx = sid < _segIndex.Length ? _segIndex[sid] : -1;
                if (idx < 0) continue; // segId 不存在（空洞）
                var seg = segs[idx];
                if (seg.StableState == StableState.Invalid) continue;
                seg.MarkInvalid();
                deletedSegIds.Add(seg.SegId);
            }

            SetMinAddress(new LogicalAddress(segId, cur.Extension + 1, address.Offset));
            // ★ 新首段（segId）的 [0, address.Offset) 已被 lease 打洞回收——推段内 _minOffset
            if (address.Offset > 0 && TryGetSegmentRaw(segId, out var headSeg) && headSeg is not null)
                headSeg.AdvanceMinOffset(address.Offset);
        }

        // ★ 锁外触发 OnSegmentDelete——通知设备层删物理段（异步通知，不等待）
        if (_handler is null) return;
        foreach (var deletedSegId in deletedSegIds)
            _handler.OnSegmentDelete(deletedSegId);
    }

    #endregion

    #region ReclaimTailFinalize

    void ILeaseSource.ReclaimTailFinalize(LogicalAddress start)
    { try { ShrinkTail(start); } finally { _tailSlot.ReleaseTailWatermark(); } }

    /// <summary>
    /// 尾截断第三阶段——原子退化全部逻辑水位线（尾截断 = Append 反向）。
    /// <para>★ 三层同步退后，保证逻辑地址一致性：</para>
    /// <para>  1. 段偏移 MaxOffset 回退到 newTail.Offset（持 SpinRWLock 排他）</para>
    /// <para>  2. 段区间表 ReclaimTail（回收 newTail 之后的 ExtentRecord）</para>
    /// <para>  3. 双尾水位线回退（TruncateTail CAS version+1 防 ABA）</para>
    /// <para>★ 顺序：先回退段偏移（读侧立即看到新边界），再回退水位——并发 Read 在水位回退前
    ///   看到已回退的 MaxOffset，不会读到已截断区。</para>
    /// <para>★ LeaseBase.ReclaimTail Commit 第三阶段调用。</para>
    /// </summary>
    private void ShrinkTail(LogicalAddress newTail)
    {
        var committed = _tailSlot.Committed;
        var newTailBelowCommitted = newTail < committed;

        long oldMaxOffset = 0;
        // ① 段偏移 + 区间表回退（持 SpinRWLock 排他，结构变更原子）
        //   ★ 只有 newTail < CommittedTail（退两个水位线的场景）才需要 RetreatOffset——
        //     截的是已提交数据，段 MaxOffset 要退。若 newTail 在 [Committed, Allocated) 之间
        //     （只退一个），段内已提交数据没变，不退 MaxOffset。
        if (newTailBelowCommitted)
        {
            if (!TryGetSegmentRaw(newTail.SegId, out var seg) || seg is null) return;
            seg.SegmentLock.AcquireExclusive();
            try
            {
                oldMaxOffset = seg.MaxOffset;
                seg.RetreatOffset(newTail.Offset); // ★ 水位回退 + 删除后续区间记录（绑定）
            }
            finally
            {
                seg.SegmentLock.ReleaseExclusive();
            }
        }

        // ② 水位线回退（CAS version+1 防 ABA）
        //   ★ newTail < CommittedTail → 退两个（_tailSlot.Retreat 退 Committed + Allocated）
        //     newTail ≥ CommittedTail → 只退 Allocated（手写 CAS 只退 Allocated，不碰 Committed）
        if (newTailBelowCommitted)
        {
            _tailSlot.Retreat(newTail); // 退两个
        }
        else
        {
            // 只退 AllocatedTail 到 newTail——CommittedTail 不变（newTail ≥ CommittedTail）
            _tailSlot.RetreatAllocatedOnly(newTail);
        }

        // ③ 经 handler 通知设备层回收段内物理空间（异步通知，不等待）——只在退两个时才有物理截断
        if (newTailBelowCommitted && _handler is not null && oldMaxOffset > newTail.Offset)
            _handler.OnSegmentReclaim(newTail.SegId, newTail.Offset, oldMaxOffset, GrowthLimit);
    }

    #endregion

    #region CompactCommit/CompactRollback

    void ILeaseSource.CompactCommit(IReadOnlyList<int> toInvalidate,
        IReadOnlyList<(int SegId, SegmentSpec Spec)> toReplace)
        => AtomicCompactReplace(toInvalidate, toReplace);

    void ILeaseSource.CompactRollback()
    {
    }

    /// <summary>
    /// 原子批量替换/失效段——CompactLease 整体提交用（一把锁内完成，中间状态不可见）。
    /// <para>★ 持 _mutationLock 原子完成所有 invalidate + replace，锁外逐个触发 OnSegmentDelete/OnSegmentReplace。</para>
    /// <para>★ 段对象由段表内部 Create——外部只传建段规格 SegmentSpec，不接触 Segment 引用。</para>
    /// </summary>
    /// <param name="toInvalidate">要失效（标 Invalid）的 segId 列表。</param>
    /// <param name="toReplace">要替换的 (segId, spec) 列表——spec 含建段参数。</param>
    private void AtomicCompactReplace(
        IReadOnlyList<int> toInvalidate,
        IReadOnlyList<(int SegId, SegmentSpec Spec)> toReplace)
    {
        List<(int SegId, long GrowthLimit, long MaxOffset)>? replacedEvents = null;
        List<int>? deletedEvents = null;
        lock (_mutationLock)
        {
            var segs = Volatile.Read(ref _segments);
            foreach (var segId in toInvalidate)
            {
                var idx = SegToIndex(segId);
                if ((uint)idx >= (uint)Volatile.Read(ref _segCount)) continue;
                var seg = segs[idx];
                if (seg.StableState == StableState.Invalid) continue;
                seg.MarkInvalid();
                (deletedEvents ??= new List<int>()).Add(segId);
            }

            foreach (var (segId, spec) in toReplace)
            {
                var idx = SegToIndex(segId);
                if ((uint)idx >= (uint)Volatile.Read(ref _segCount)) continue;
                var seg = segs[idx];
                if (seg is null || !seg.IsValid) continue;
                // ★ L12 修复（2026-08-21）：换段从"新建对象换槽"改为【原位更新】——对象身份/锁/
                //   物理门不变，自旋写者（AcquireExtent 持旧引用）与读计划锁天然互斥。
                //   区间表布局在 extent lock 内单发布 + CompactVersion 递增（陈旧认知快速失败）。
                //   旧实现 new Segment + Volatile.Write 换槽：自旋写者醒来插进死对象（对全宇宙
                //   不可见）→ Commit 在新对象找不到记录静默 no-op → 双占/丢写（探针实锤）。
                var oldMax = seg.MaxOffset;
                var layout = new List<ExtentRecord>(2);
                if (spec.MaxOffset > 0)
                    layout.Add(new ExtentRecord(spec.MinOffset, spec.MaxOffset, ExtentStateCode.Committed, sparse: false));
                // ★ L19 收口（2026-08-22）：[新 MaxOffset, min(旧 MaxOffset, preserveFrom)) = 打包释放区
                //   （窗口数据已滑走）→ sparse 空槽；[preserveFrom, 旧 MaxOffset) 的旧终态区间由
                //   ApplyCompactReplacement 原样拼接保留——写者恰在 lease 获取前提交、数据落在窗口外时
                //   不再被 blanket sparse 洗成读零（磁盘实锤：单轮丢 512B 记录）。
                var sparseEnd = Math.Min(oldMax, spec.PreserveFrom);
                if (spec.MaxOffset < sparseEnd)
                    layout.Add(new ExtentRecord(spec.MaxOffset, sparseEnd, ExtentStateCode.Committed, sparse: true));
                seg.ApplyCompactReplacement(spec.GrowthLimit, spec.MaxOffset, spec.MinOffset, spec.PreserveFrom, layout);
                (replacedEvents ??= new List<(int, long, long)>()).Add((segId, spec.GrowthLimit, spec.MaxOffset));
                // ★ Compact 新段如果是本次生命周期最大的段号，更新阈值
                //   （Compact 段大小可能 ≠ 全局，但更新阈值后 > 阈值的段仍走全局——它们是更新的新段）
                if (segId > _runtimeCreatedSegIdThreshold)
                    _runtimeCreatedSegIdThreshold = segId;
            }
        }

        // ★ 锁外触发事件（避免锁内回调死锁）
        if (_handler is null) return;
        if (deletedEvents is not null)
            foreach (var segId in deletedEvents)
                _handler.OnSegmentDelete(segId);
        if (replacedEvents is not null)
            foreach (var (segId, gl, mo) in replacedEvents)
                _handler.OnSegmentReplace(segId, gl, mo);
    }

    #endregion
}