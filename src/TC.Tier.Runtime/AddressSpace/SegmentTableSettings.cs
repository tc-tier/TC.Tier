namespace TC.Tier.Runtime.AddressSpace;

/// <summary>
/// 段表配置参数——用于初始化 <see cref="SegmentTable"/>。
/// </summary>
/// <param name="GrowthLimit">段增长上限（字节，&gt;0；默认 <see cref="AlignmentConst.Alignment32M"/> = 32MB，启动后不变）。</param>
/// <param name="MinSegId">段表最小有效段号（恢复路径首段 segId，默认 0）。</param>
/// <param name="IndexCapacity">_segIndex 初始容量（= maxSegId + 1，恢复路径用扫盘最大段号；默认 8）。</param>
/// <param name="SpinMilliseconds">自旋等待时间（毫秒，默认 30*1000 = 30秒）。</param>
/// <param name="WarnEvery">警告间隔（每多少次尝试记录一次警告，默认 32）。</param>
/// <param name="EnableSingleSegment">单段模式（默认 false=多段）。true=仅 seg0，分配超容量直接抛
///   容量不足（不 spin、不建 seg1）——旧单段语义收口到此。</param>
public readonly record struct SegmentTableSettings(
    long GrowthLimit = AlignmentConst.Alignment32M,
    int MinSegId = 0,
    int IndexCapacity = 8,
    long SpinMilliseconds = 30 * 1000,
    int WarnEvery = 32,
    bool EnableSingleSegment = false)
{
    /// <summary>
    /// 默认配置实例。
    /// </summary>
    public static SegmentTableSettings Default => new SegmentTableSettings();
};