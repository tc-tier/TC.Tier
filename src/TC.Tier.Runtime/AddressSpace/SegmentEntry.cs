namespace TC.Tier.Runtime.AddressSpace;

/// <summary>段条目——段 ID + 段规格的不可变组合（段表的公开条目形态）。</summary>
public readonly struct SegmentEntry
{
    /// <summary>构造段条目，段 ID 默认 0。</summary>
    /// <param name="spec">段规格。</param>
    public SegmentEntry(SegmentSpec spec) : this(0, spec)
    {
    }

    /// <summary>构造段条目。</summary>
    /// <param name="segId">段 ID（须 ≥ 0）。</param>
    /// <param name="spec">段规格。</param>
    /// <exception cref="ArgumentException">segId 为负。</exception>
    public SegmentEntry(int segId, SegmentSpec spec)
    {
        if (segId < 0)
        {
            throw new ArgumentException($"SegmentEntry Segment segId={segId} 非法（须 >= 0）", nameof(segId));
        }
        SegId = segId;
        Spec = spec;
    }

    /// <summary>由规格标量直接构造段条目（包装 <see cref="SegmentSpec"/> 五参构造）。</summary>
    /// <param name="segId">段 ID（须 ≥ 0）。</param>
    /// <param name="minOffset">段的最小偏移。</param>
    /// <param name="growthLimit">段的增长上限。</param>
    /// <param name="maxOffset">段的最大偏移。</param>
    /// <param name="stableState">段的稳定状态。</param>
    public SegmentEntry(int segId, long minOffset, long growthLimit, long maxOffset, StableState stableState)
        : this(segId, new SegmentSpec(minOffset, growthLimit, maxOffset, stableState))
    {
    }

    /// <summary>段 ID。</summary>
    public int SegId { get; }

    /// <summary>段规格。</summary>
    public SegmentSpec Spec { get; }
}