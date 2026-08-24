namespace TC.Tier.Runtime.AddressSpace;

/// <summary>
/// 段规格：描述段的增长上限、最大偏移、稳定状态和最小偏移。
/// </summary>
public readonly record struct SegmentSpec
{
    public long GrowthLimit { get; }
    public long MaxOffset { get; }
    public StableState StableState { get; }
    public long MinOffset { get; }

    /// <summary>
    /// ★ L19 收口（）：布局保留边界——≥ 此偏移的旧终态区间记录原样保留
    /// （Compact 原位换内脏时拼接进新布局）。默认 long.MaxValue = 不保留（[MaxOffset, 旧 MaxOffset)
    /// blanket sparse）。RangeCompact 尾段设窗口尾偏移：窗口外已提交数据不洗零。
    /// </summary>
    public long PreserveFrom { get; }

    /// <summary>
    /// 构造一个段规格。
    /// </summary>
    /// <param name="minOffset">段的最小偏移。</param>
    /// <param name="growthLimit">段的增长上限。</param>
    /// <param name="maxOffset">段的最大偏移。</param>
    /// <param name="stableState">段的稳定状态。</param>
    /// <param name="preserveFrom">布局保留边界（默认不保留）。</param>
    /// <exception cref="ArgumentException"></exception>
    public SegmentSpec(long minOffset, long growthLimit, long maxOffset,
        StableState stableState = StableState.Ready, long preserveFrom = long.MaxValue)
    {
        if (growthLimit <= 0)
            throw new ArgumentException($"SegmentSpec Segment growthLimit={growthLimit} 非法（须 > 0）", nameof(growthLimit));
        if (minOffset < 0 || maxOffset < 0)
            throw new ArgumentException($"SegmentSpec Segment 偏移为负：minOffset={minOffset} maxOffset={maxOffset}", nameof(minOffset));
        if (minOffset > maxOffset || maxOffset > growthLimit)
            throw new ArgumentException(
                $"SegmentSpec Segment 关系不变量破坏：minOffset={minOffset} ≤ maxOffset={maxOffset} ≤ growthLimit={growthLimit} 不成立" +
                "（典型的元组位序错位——检查构造点参数顺序）");
        MinOffset = minOffset;
        GrowthLimit = growthLimit;
        MaxOffset = maxOffset;
        StableState = stableState;
        PreserveFrom = preserveFrom;
    }
   /// <summary>
   /// 构造一个段规格，默认最小偏移为 0。
   /// </summary>
   /// <param name="growthLimit">段的增长上限。</param>
   /// <param name="maxOffset">段的最大偏移。</param>
   /// <param name="stableState">段的稳定状态。</param>
    public SegmentSpec(long growthLimit, long maxOffset, StableState stableState = StableState.Ready):
        this(minOffset: 0, growthLimit: growthLimit, maxOffset: maxOffset, stableState: stableState)
    {

    }
}