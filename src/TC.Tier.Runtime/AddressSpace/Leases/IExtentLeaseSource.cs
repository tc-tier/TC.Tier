namespace TC.Tier.Runtime.AddressSpace.Leases;

/// <summary>
/// 段表对 ExtentLease 的契约——单 Chunk 级 {Kind}Commit/Rollback。
/// <para>★ ExtentLease 持此接口引用，按自己的 kind 调对应方法，零 switch 状态逻辑。</para>
/// <para>★ kind 隐含在方法名里——AppendCommit/WriteCommit/ReclaimCommit 等，调用方一看方法名就知道是什么操作。</para>
/// <para>★ 状态处理封装在段表实现里——AppendCommit 内部知道要 CompleteAndMerge + AdvanceMaxOffset，调用方不用管。</para>
/// </summary>
public interface IExtentLeaseSource
{

    /// <summary>
    /// 申请 ExtentLease——按 segId、start、end、extentState 申请单 Chunk 区间 lease。
    /// </summary>
    /// <param name="segId">段 ID。</param>
    /// <param name="start">起始地址。</param>
    /// <param name="end">结束地址（不包含）。</param>
    /// <param name="extentState">区间状态（ExtentStateCode.XxxLeased）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>返回申请到的 ExtentLease。</returns>
    ExtentLease AcquireExtent(int segId, long start, long end, byte extentState, CancellationToken ct = default);

    // ═══ Append（单 Chunk）═══

    /// <summary>
    /// Append 单 Chunk 提交——CompleteAndMerge + AdvanceMaxOffset（地址已占不可逆）。
    /// </summary>
    /// <param name="segId">段 ID。</param>
    /// <param name="start">段内起始偏移（跨段时 segOff ≠ 0，不能硬编码 0）。</param>
    /// <param name="end">段内结束偏移（不包含）。</param>
    void AppendCommit(int segId, long start, long end, int compactVersion = 0);

    /// <summary>
    /// Append 单 Chunk 回滚——MarkWasted（地址已占不可逆，段表可修复）。
    /// </summary>
    /// <param name="segId">段 ID。</param>
    /// <param name="start">段内起始偏移。</param>
    /// <param name="end">段内结束偏移（不包含）。</param>
    void AppendRollback(int segId, long start, long end, int compactVersion = 0);

    // ═══ Write（单 Chunk）═══

    /// <summary>Write 单 Chunk 提交——CompleteAndMerge。</summary>
    /// <param name="segId">段 ID。</param>
    /// <param name="start">起始地址。</param>
    /// <param name="end">结束地址（不包含）。</param>
    void WriteCommit(int segId, long start, long end, int compactVersion = 0);

    /// <summary>Write 单 Chunk 回滚——MarkWasted（可覆写修复）。</summary>
    /// <param name="segId">段 ID。</param>
    /// <param name="start">起始地址。</param>
    /// <param name="end">结束地址（不包含）。</param>
    void WriteRollback(int segId, long start, long end, int compactVersion = 0);

    // ═══ Reclaim（单 Chunk，中间回收）═══

    /// <summary>Reclaim 单 Chunk 提交——CompleteAndMerge(sparse) → Wasted 空洞。</summary>
    /// <param name="segId">段 ID。</param>
    /// <param name="start">起始地址。</param>
    /// <param name="end">结束地址（不包含）。</param>
    void ReclaimCommit(int segId, long start, long end, int compactVersion = 0);

    /// <summary>Reclaim 单 Chunk 回滚——Abort → Aborted 永久洞（只 Compact 修）。</summary>
    /// <param name="segId">段 ID。</param>
    /// <param name="start">起始地址。</param>
    /// <param name="end">结束地址（不包含）。</param>
    void ReclaimRollback(int segId, long start, long end, int compactVersion = 0);

    // ═══ Compact（单 Chunk）═══

    /// <summary>Compact 单 Chunk 提交——ReleaseCompact（overlay 释放）。</summary>
    /// <param name="segId">段 ID。</param>
    /// <param name="start">起始地址。</param>
    /// <param name="end">结束地址（不包含）。</param>
    void CompactCommit(int segId, long start, long end, int compactVersion = 0);

    /// <summary>Compact 单 Chunk 回滚——ReleaseCompact（overlay 释放，段表不变）。</summary>
    /// <param name="segId">段 ID。</param>
    /// <param name="start">起始地址。</param>
    /// <param name="end">结束地址（不包含）。</param>
    void CompactRollback(int segId, long start, long end, int compactVersion = 0);
}