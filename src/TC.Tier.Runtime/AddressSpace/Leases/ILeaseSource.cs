namespace TC.Tier.Runtime.AddressSpace.Leases;

/// <summary>
/// 段表对操作 lease 的契约——整体级 {Kind}Commit/Rollback + 段查询 + 建段协调。
/// <para>★ 继承 IExtentLeaseSource（操作 lease 也需要区间级操作）。</para>
/// <para>★ LeaseBase / CompactLease 持此接口引用。</para>
/// <para>★ 重载区分粒度：有 int segId = 单 Chunk（ExtentLease 级），无 segId = 整体（LeaseBase 级）。</para>
/// </summary>
public interface ILeaseSource : IExtentLeaseSource
{
    /// <summary>
    /// ★ 是否启用诊断跟踪——false 时 RegisterLease/UnregisterLease 零开销（生产模式）。
    /// <para>lease Reset/Dispose 检查此标志，false 时跳过诊断操作（省 ~250ns + ~168B/op）。</para>
    /// </summary>
    bool EnableDiagnostics { get; }

    /// <summary>
    /// 按 [start, end) 跨段边界切分，返回每段区间 (segId, segOff, segEnd)。
    /// <para>★ lease 构造时调——一次拿全所有 chunk 区间，直接占住，不需要两遍扫描。</para>
    /// </summary>
    IReadOnlyList<(int SegId, long SegOff, long SegEnd,long GrowthLimit)> GetExtentRanges(LogicalAddress start, LogicalAddress end);

    // ═══ 工厂 ═══
    /// <summary>
    /// 计算 [start, end) 跨段 chunk 数（零分配，纯段表遍历）。
    /// <para>★ lease 先调此方法拿 count，再 ArrayPool.Rent buffer，再调 <see cref="AcquireExtentsForLease"/> 占住。</para>
    /// </summary>
    int GetExtentCount(LogicalAddress start, LogicalAddress end);

    /// <summary>
    /// 遍历 [start, end) 跨段区间，逐个占住（AcquireExtent）填到 buffer——零中间分配（无 List/rangesBuf）。
    /// <para>★ lease Reset 核心调用：一次遍历完成"算 chunk + 占住 + 填 buffer"，消除 GetExtentRanges 的 List 分配。</para>
    /// <para>★ buffer 须 ≥ <see cref="GetExtentCount"/> 返回值。部分失败时 lease 的 catch 回滚已占 chunk。</para>
    /// </summary>
    int AcquireExtentsForLease(LogicalAddress start, LogicalAddress end, byte extentState, ExtentLease[] buffer);

    /// <summary>
    /// 注册 lease 引用——lease 生命周期结束时必须 UnregisterLease，否则段表无法回收。
    /// </summary>
    /// <param name="leaseRef">要注册的 lease 引用。</param>
    void RegisterLease(ITrackedLease leaseRef);

    /// <summary>
    /// 注销 lease 引用——lease 生命周期结束时必须调用，否则段表无法回收。
    /// </summary>
    /// <param name="leaseId">要注销的 lease 的 ID。</param>
    void UnregisterLease(Guid leaseId);
    // ═══ 建段协调 ═══

    /// <summary>等待段物理就绪（建段协调）——段建好或不在表立即返回；建段失败抛 SegmentCreationException。</summary>
    void WaitSegmentReady(int segId, ILogger? logger = null);

    // ═══ 终态收敛（Finalize——无方向：三对整体级 {Kind}Commit/Rollback 实现两两同体，
    //     物理不可逆决定终态收敛与成败方向无关。设计文档 §3）═══

    /// <summary>Append 终态收敛——推 CommittedTail 到 end（max-CAS 幂等，越 Wasted 空洞）。</summary>
    void AppendFinalize(LogicalAddress end);

    /// <summary>ReclaimHead 终态收敛——ShrinkHead（跨段 MarkInvalid + 推 MinAddress + OnSegmentDelete）。</summary>
    void ReclaimHeadFinalize(LogicalAddress end);

    /// <summary>ReclaimTail 终态收敛——ShrinkTail（退双尾水位）+ 释放双尾独占标志。</summary>
    void ReclaimTailFinalize(LogicalAddress start);

    // ═══ Compact（整体——唯一真方向性：Commit 换表、Rollback 无操作）═══

    /// <summary>Compact 整体提交——AtomicCompactReplace（段表原子替换）。</summary>
    void CompactCommit(IReadOnlyList<int> toInvalidate,
        IReadOnlyList<(int SegId, SegmentSpec Spec)> toReplace);

    /// <summary>Compact 整体回滚——无段表副作用（操作级 Retry）。</summary>
    void CompactRollback();
}
