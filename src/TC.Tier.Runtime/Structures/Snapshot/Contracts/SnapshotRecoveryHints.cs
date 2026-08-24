namespace TC.Tier.Runtime.Structures.Snapshot.Contracts;

/// <summary>
/// SnapshotBase 恢复 hints（与各结构 hints 完全独立）。
/// </summary>
public readonly struct SnapshotRecoveryHints
{
    /// <summary>已知逻辑写尾（上层注入，优先于扫盘）。</summary>
    public LogicalAddress? WriteAddress { get; init; }

    /// <summary>已知物理写尾（扇区对齐）。</summary>
    public LogicalAddress? PhysicalWriteAddress { get; init; }
}
