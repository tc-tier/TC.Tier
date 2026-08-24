namespace TC.Tier.Runtime.AddressSpace;

public readonly struct SegmentEntry
{
    public SegmentEntry(SegmentSpec spec) : this(0, spec)
    {
    }
    public SegmentEntry(int segId, SegmentSpec spec)
    {
        if (segId < 0)
        {
            throw new ArgumentException($"SegmentEntry Segment segId={segId} 非法（须 >= 0）", nameof(segId));
        }
        SegId = segId;
        Spec = spec;
    }
    public SegmentEntry(int segId, long minOffset, long growthLimit, long maxOffset, StableState stableState)
        : this(segId, new SegmentSpec(minOffset, growthLimit, maxOffset, stableState))
    {
    }
    public int SegId { get; }
    public SegmentSpec Spec { get; }
}