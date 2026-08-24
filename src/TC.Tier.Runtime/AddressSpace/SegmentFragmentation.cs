namespace TC.Tier.Runtime.AddressSpace;

/// <summary>
/// 段碎片化统计——区间状态分布、空洞率、可合并建议。
/// </summary>
public sealed record SegmentFragmentation
{
    /// <summary>段 ID。</summary>
    public int SegId { get; set; }
    /// <summary>总区间数（含所有状态）。</summary>
    public int TotalExtents { get; set; }
    /// <summary>Committed 区间数（有效数据）。</summary>
    public int CommittedCount { get; set; }
    /// <summary>Wasted 区间数（可覆写空洞）。</summary>
    public int WastedCount { get; set; }
    /// <summary>Aborted 区间数（永久洞，只 Compact 修）。</summary>
    public int AbortedCount { get; set; }
    /// <summary>在途区间数（lease 占住未提交）。</summary>
    public int InFlightCount { get; set; }
    /// <summary>Committed 字节数（有效数据量）。</summary>
    public long CommittedBytes { get; set; }
    /// <summary>Wasted 字节数（可覆写空洞量）。</summary>
    public long WastedBytes { get; set; }
    /// <summary>Aborted 字节数（永久洞量）。</summary>
    public long AbortedBytes { get; set; }
    /// <summary>在途字节数。</summary>
    public long InFlightBytes { get; set; }
    /// <summary>总有效空间（Committed + Wasted + Aborted + InFlight）。</summary>
    public long TotalBytes => CommittedBytes + WastedBytes + AbortedBytes + InFlightBytes;
    /// <summary>
    /// ★ 碎片率——空洞占比 = (Wasted + Aborted) / Total。
    /// <para>> 30% 建议触发 CompactIntervals 整理。</para>
    /// </summary>
    public double FragmentationRatio => TotalBytes > 0
        ? (double)(WastedBytes + AbortedBytes) / TotalBytes : 0;
    /// <summary>可整理性——Aborted 占比（只 Compact 能修的永久洞）。</summary>
    public double AbortedRatio => TotalBytes > 0 ? (double)AbortedBytes / TotalBytes : 0;
}