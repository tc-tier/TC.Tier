namespace TC.Tier.Runtime.AddressSpace.Leases;

/// <summary>CompactChunk 的互斥终态——由 <see cref="CompactChunk.SetReplacement"/> / <see cref="CompactChunk.MarkInvalid"/> 一次性流转。</summary>
internal enum CompactChunkState : byte
{
    /// <summary>未填——使用方第一阶段尚未调 SetReplacement/MarkInvalid。</summary>
    Pending = 0,
    /// <summary>已设新段替换（4 标量已填）。</summary>
    Replacement = 1,
    /// <summary>已标旧段无效。</summary>
    Invalid = 2,
}

/// <summary>
/// Compact 操作的区间块信息——用于记录旧段和新段的状态，支持分阶段提交。
/// <para>★ 互斥终态（设计文档 §5）：Replacement/Invalid 二选一、只能从 Pending 流转一次——
///   重复/交叉调用 throw（fail-fast，对齐 SegmentScanEntry 强校验绊线制度），
///   "双未设静默跳过 / 双设静默丢一"两类非法态从类型上消灭。</para>
/// </summary>
/// <param name="segId">段 ID。</param>
/// <param name="oldGrowthLimit">旧段的增长上限。</param>
public sealed class CompactChunk(int segId, long oldGrowthLimit)
{
    private CompactChunk((int SegId, long SegOff, long SegEnd, long GrowthLimit) range)
        : this(range.SegId, range.GrowthLimit)
    {
    }

    /// <summary>
    /// 隐式转换：从元组 (SegId, SegOff, SegEnd, RealSize, GrowthLimit) 转换为 CompactChunk。
    /// </summary>
    /// <param name="range">包含段信息的元组。</param>
    /// <returns>对应的 CompactChunk 实例。</returns>
    public static implicit operator CompactChunk((int SegId, long SegOff, long SegEnd, long GrowthLimit) range)
        => new(range);

    // 底层第一阶段填（外部只读）
    public int SegId { get; } = segId;

    /// <summary>
    /// 旧段的增长上限（使用方第二阶段计算新段 segLimit 用）。
    /// </summary>
    public long OldGrowthLimit { get; } = oldGrowthLimit;

    /// <summary>
    /// 互斥终态——Pending / Replacement / Invalid。
    /// </summary>
    internal CompactChunkState State { get; private set; }

    // 使用方第二阶段填（不传段号）：

    /// <summary>
    /// 新段的增长上限（使用方第一阶段调 SetReplacement）。
    /// </summary>
    internal long NewGrowthLimit { get; private set; }

    /// <summary>
    /// 新段的最大偏移（使用方第一阶段调 SetReplacement）。
    /// </summary>
    internal long NewMaxOffset { get; private set; }

    /// <summary>
    /// 新段的最小偏移（使用方第一阶段调 SetReplacement）。
    /// </summary>
    internal long NewMinOffset { get; private set; }

    /// <summary>
    /// ★ L19 收口（）：布局保留边界——≥ 此偏移的旧区间记录原样保留（状态/sparse 位照搬）。
    /// <para>默认 long.MaxValue = 不保留（[新 MaxOffset, 旧 MaxOffset) blanket sparse——全量 Compact
    /// 与恢复路径的语义）。RangeCompact 对窗口尾段设 to.Offset：写者恰在 lease 获取前提交、
    /// 数据落在窗口外的区间不被洗成读零（旧实现静默丢写实锤）。</para>
    /// </summary>
    internal long NewPreserveFrom { get; private set; } = long.MaxValue;

    /// <summary>
    /// 新段的稳定状态（使用方第一阶段调 SetReplacement）。
    /// </summary>
    internal StableState NewStableState { get; private set; }

    /// <summary>
    /// 设置这个旧段被新段替换——Complete 时底层从段表移除。
    /// </summary>
    /// <param name="growthLimit">新段的增长上限。</param>
    /// <param name="maxOffset">新段的最大偏移。</param>
    /// <param name="stableState">新段的稳定状态。</param>
    /// <param name="minOffset">新段的最小偏移。</param>
    /// <param name="preserveFrom">布局保留边界（≥ 此偏移的旧终态区间原样保留；默认不保留）。</param>
    internal void SetReplacement(
        long growthLimit,
        long maxOffset,
        StableState stableState = StableState.Ready,
        long minOffset = 0,
        long preserveFrom = long.MaxValue)
    {
        if (State != CompactChunkState.Pending)
            throw new InvalidOperationException(
                $"CompactChunk seg{SegId} 已处于 {State} 终态，不能重复/交叉设置（当前调用 SetReplacement）");
        State = CompactChunkState.Replacement;
        NewGrowthLimit = growthLimit;
        NewMaxOffset = maxOffset;
        NewMinOffset = minOffset;
        NewStableState = stableState;
        NewPreserveFrom = preserveFrom;
    }

    /// <summary>
    /// 标记这个旧段无效——Complete 时底层从段表移除。
    /// </summary>
    internal void MarkInvalid()
    {
        if (State != CompactChunkState.Pending)
            throw new InvalidOperationException(
                $"CompactChunk seg{SegId} 已处于 {State} 终态，不能重复/交叉设置（当前调用 MarkInvalid）");
        State = CompactChunkState.Invalid;
    }
}
