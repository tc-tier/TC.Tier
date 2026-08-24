namespace TC.Tier.Runtime.AddressSpace.Leases;

/// <summary>lease 信息（诊断/查询用）。</summary>
public sealed class LeaseInfo
{
    /// <summary>
    /// lease 的唯一标识符。
    /// </summary>
    public Guid Id { get; init; }
    /// <summary>
    /// lease 的起始设备地址（包含）。
    /// </summary>
    public LogicalAddress Start { get; init; }
    /// <summary>
    /// lease 的结束设备地址（不包含）。
    /// </summary>
    public LogicalAddress End { get; init; }
    /// <summary>
    /// lease 的当前状态。
    /// </summary>
    public LeaseState LeaseState { get; init; }
    /// <summary>
    /// lease 创建的时间戳（毫秒）。
    /// </summary>
    public long CreatedTimestampMs { get; init; }
    /// <summary>
    /// lease 涉及的段 ID 列表。
    /// </summary>
    public int[] SegIds { get; init; } = Array.Empty<int>();
}