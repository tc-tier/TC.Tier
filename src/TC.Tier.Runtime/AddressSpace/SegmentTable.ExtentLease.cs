namespace TC.Tier.Runtime.AddressSpace;

public sealed partial class SegmentTable
{

    /// <summary>
    /// 占段内区间（lease）——段表封装锁/可读性检查，外部不接触段内部。
    /// </summary>
    /// <param name="segId">段 ID。</param>
    /// <param name="start">起始偏移量。</param>
    /// <param name="end">结束偏移量（不含）。</param>
    /// <param name="extentState">区间状态。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>返回一个 ExtentLease 对象。</returns>
    /// <exception cref="InvalidOperationException"></exception>
    /// <exception cref="TimeoutException">占区间超时（区间被长期排他占用）。</exception>
    ExtentLease IExtentLeaseSource.AcquireExtent(int segId, long start, long end, byte extentState, CancellationToken ct)
    {
        if (TryGetSegmentRaw(segId, out var seg) && seg is not null && seg.IsValid)
        {
            var spinner = new SpinWait();
            var deadline = Environment.TickCount64 + _spinMilliseconds;
            var attempts = 0;
            // ★ L12 版本哨兵（）：等待/退避期间段被 Compact 原位重整（CompactVersion
            //   变化）= 本轮认知基于旧内脏。原位更新后引用恒稳（互斥已闭环），此哨兵为纵深防御。
            //   ★ 快路径零税（性能税批次 ）：入口引用本就新鲜——重取只在重试路径
            //   （refresh 标记）；版本只在锁内读权威值（返回用）/FairGate 捕获点现读。
            var refresh = false;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (refresh)
                {
                    // 重试路径重取（失败退避/FairGate park 过——世界可能已变：引用/版本整套认知刷新）
                    if (!TryGetSegmentRaw(segId, out var cur) || cur is null || !cur.IsValid)
                        throw new InvalidOperationException(
                            $"AcquireExtent 段不存在 segId={segId}——lease 应在 Allocate 之后创建");
                    seg = cur;
                    refresh = false;
                }
                // ★ 公平让位：已有等待者时，新到者不走热插路径（否则零间隙复占者永远插队，
                //   被唤醒者的调度延迟 > 让渡窗口时持续失手——实测 3/8 残余超时根因）。
                if (_extentGate.HasWaiters)
                {
                    // 慢路径（Core.FairGate）：登记等待者 → 门锁内尝试占用 → 失败 park 等终态 Wake
                    var capturedSeg = seg;
                    var capturedVer = capturedSeg.CompactVersion;   // 捕获点现读（lambda 锁内校验）
                    if (_extentGate.TryAcquireSlow(() =>
                        {
                            using var gk = capturedSeg.AcquireExtentLock();
                            // 版本校验：park 期间段被 Compact 重整 = 旧认知作废，交还外层重查
                            if (capturedSeg.CompactVersion != capturedVer) return false;
                            if (!capturedSeg.CanAcquireUnsafe(start, end, extentState)) return false;
                            capturedSeg.InsertUnsafe(start, end, extentState, refresh: true);
                            return true;
                        }))
                    {
                        // lambda 内锁下哨兵已验版本未变——capturedVer 即生效版本
                        return new ExtentLease(this, segId, start, end, extentState, capturedVer);
                    }
                    spinner.Reset();
                    refresh = true;   // park 过——下轮重取
                    continue;
                }
                using var lk = seg.AcquireExtentLock();

                {
                    var version = seg.CompactVersion;   // 锁内权威（与内脏变更互斥）
                    if (seg.CanAcquireUnsafe(start, end, extentState))
                    {
                        seg.InsertUnsafe(start, end, extentState, refresh: true);
                        return new ExtentLease(this, segId, start, end, extentState, version);
                    }
                }
                if (Environment.TickCount64 > deadline)
                    throw new TimeoutException(
                        $"AcquireExtent 占区间超时 segId={segId} [{start},{end})——区间被长期排他占用 attempts={attempts}");
                if (++attempts % _warnEvery == 0)
                    _logger?.LogWarning("AcquireExtent 占区间退避 segId={segId} [{start},{end}) attempts={attempts}", segId, start, end);
                spinner.SpinOnce();
                refresh = true;   // 本轮失败——下轮重取
            }
        }
        throw new InvalidOperationException(
            $"AcquireExtent 段不存在 segId={segId}——lease 应在 Allocate 之后创建");
    }
    // ════════════════════════════════════════════════════════════
    // === {Kind}Commit/Rollback（IExtentLeaseSource 实现——单 Chunk 级）===
    // ★ kind 隐含在方法名里——状态处理封装在此，lease 不 switch 状态逻辑。
    // ════════════════════════════════════════════════════════════

    void IExtentLeaseSource.AppendCommit(int segId, long start, long end, int compactVersion)
    {
        if (!TryGetSegmentRaw(segId, out var seg) || seg is null || !seg.IsValid) return;
        seg.ThrowIfStaleVersion(segId, compactVersion);   // ★ L12：旧世界 lease 快速失败
        // ★ 用实际 start（跨段时 segOff ≠ 0）——之前硬编码 0 导致跨段 Append 区间不转 Committed。
        seg.CompleteAndMerge(start, end, sparse: false);
        seg.AdvanceOffset(end);
        _extentGate.Wake();   // ★ 区间已转 Committed（可占）再唤醒——顶部唤醒过早（态未转换，被唤醒者双检必败）
        if (!seg.IsFull || _handler is null) return;
        _handler?.OnSegmentFull(segId, seg.RealSize, seg.GrowthLimit);
        _handler?.OnSegmentCreate(segId + 1, GrowthLimit, isHighPriority: false); // 预建下一段
    }

    void IExtentLeaseSource.AppendRollback(int segId, long start, long end, int compactVersion)
    {
        if (!TryGetSegmentRaw(segId, out var seg) || seg is null || !seg.IsValid) return;
        seg.ThrowIfStaleVersion(segId, compactVersion);   // ★ L12：旧世界 lease 快速失败
        seg.MarkWasted(start, end);
        seg.AdvanceOffset(end);
        _extentGate.Wake();   // ★ 终态转换后唤醒
        if (!seg.IsFull || _handler is null) return;
        _handler?.OnSegmentFull(segId, seg.RealSize, seg.GrowthLimit);
        _handler?.OnSegmentCreate(segId + 1, GrowthLimit, isHighPriority: false); // 预建下一段
    }

    void IExtentLeaseSource.WriteCommit(int segId, long start, long end, int compactVersion)
    {
        if (!TryGetSegmentRaw(segId, out var seg) || seg is null || !seg.IsValid) return;
        seg.ThrowIfStaleVersion(segId, compactVersion);   // ★ L12：旧世界 lease 快速失败
        seg.CompleteAndMerge(start, end, sparse: false);
        _extentGate.Wake();   // ★ 区间已转 Committed（可占）再唤醒——顶部唤醒过早
    }

    void IExtentLeaseSource.WriteRollback(int segId, long start, long end, int compactVersion)
    {
        if (TryGetSegmentRaw(segId, out var seg) && seg is not null)
        {
            seg.ThrowIfStaleVersion(segId, compactVersion);   // ★ L12：旧世界 lease 快速失败
            seg.MarkWasted(start, end);
        }
        _extentGate.Wake();   // ★ 终态转换后唤醒
    }

    void IExtentLeaseSource.ReclaimCommit(int segId, long start, long end, int compactVersion)
    {
        // ★ OS 稀疏文件语义：Reclaim 打洞（PunchHole）= fallocate(FALLOC_FL_PUNCH_HOLE)——
        //   物理块归还 OS，区间仍 Committed（可读=读零、可写=地址空间复用）。
        //   故打洞成功后区间保持 Committed+sparse，**不**标 Wasted（Wasted 是「失败写/不可读垃圾」，
        //   会把读门 IsRangeFullyReadable 阻断致 AcquireReadPlan 死循环）。
        //   PunchHole 已在 lease.Commit 前物理归零数据，此处只把在途区间（ReclaimLeased）落回 Committed。
        if (!TryGetSegmentRaw(segId, out var seg) || seg is null || !seg.IsValid) return;
        seg.CompleteAndMerge(start, end, sparse: true);
        _extentGate.Wake();   // ★ L26：终态转换后唤醒 FairGate 等待者（对齐 Append/Write 先例）
    }

    void IExtentLeaseSource.ReclaimRollback(int segId, long start, long end, int compactVersion)
    {
        // ★ 流转图语义（保守正确）：punch 与 CommitCurrent 非原子——回滚时无法区分该 chunk
        //   是否已物理打洞（数据可能已归零），标 Aborted 显式拒绝读（"永久洞，只 Compact 修"）。
        //   落 Committed 会把可能已归零的区间当完好数据 = 静默数据错误，比拒绝读更危险。
        //   （Abort 阻断读门是设计行为——配套活性修复在读侧：终态不可读不.spn，见 AcquireReadPlan。）
        if (TryGetSegmentRaw(segId, out var seg) && seg is not null)
            seg.MarkAbort(start, end);
        _extentGate.Wake();   // ★ L26：Aborted 对 Reclaim 族可占（L1）——唤醒等待者重试
    }

    void IExtentLeaseSource.CompactCommit(int segId, long start, long end, int compactVersion)
    {
        if (TryGetSegmentRaw(segId, out var seg) && seg is not null)
            seg.ReleaseCompact(start, end);
        _extentGate.Wake();   // ★ L26：Compact 释放区间后唤醒（写者无需等 50ms park 超时）
    }

    void IExtentLeaseSource.CompactRollback(int segId, long start, long end, int compactVersion)
    {
        if (TryGetSegmentRaw(segId, out var seg) && seg is not null)
            seg.ReleaseCompact(start, end);
        _extentGate.Wake();   // ★ L26：回滚同样释放区间——对称唤醒
    }
}