namespace TC.Tier.Contracts.Storage;

/// <summary>
/// Compact 结果——新水位线 + 地址翻译对照表。
/// </summary>
public readonly struct CompactResult
{
    /// <summary>Compact 后的新低水位线。</summary>
    public LogicalAddress NewLowWaterMark { get; init; }

    /// <summary>Compact 后的新高水位线。</summary>
    public LogicalAddress NewHighWaterMark { get; init; }

    /// <summary>
    /// 旧地址 → 新地址 对照表。
    /// <para>RangeCompact 为每个不同的请求地址保留一项；hole、不存在或区间外地址映射到 null。</para>
    /// </summary>
    public IReadOnlyDictionary<LogicalAddress, LogicalAddress?> MigrationMap { get; init; }
}