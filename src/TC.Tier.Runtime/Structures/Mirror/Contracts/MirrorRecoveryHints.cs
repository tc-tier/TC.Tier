namespace TC.Tier.Runtime.Structures.Mirror.Contracts;

/// <summary>
/// MirrorBase 恢复 hints（与 Log/Ring/Metadata 的 hints 完全独立，各结构各自定义）。
/// </summary>
public readonly struct MirrorRecoveryHints
{
    /// <summary>已知链头地址（上层注入，优先于扫盘）。</summary>
    public LogicalAddress? HighestVersionAddress { get; init; }

    /// <summary>已知提交点 seq。</summary>
    public long? LastCommittedSeq { get; init; }
}
