namespace TC.Tier.Runtime.Structures.Ring;

/// <summary>
/// Ring 恢复提示（上层注入的已知水位，加速恢复）。对齐 LogRecoveryHints/RecoveryHints。
/// <para>★ 全 LogicalAddress（base.md §2.3）。</para>
/// <para>四级回退优先级：本 hints → meta(O(1)) → 引擎 AllocatedTail → OpenScanCursor 扫盘。</para>
/// <para>参见 base.md §3 D。</para>
/// </summary>
public readonly struct RingRecoveryHints
{
    /// <summary>已知头截断边界。</summary>
    public LogicalAddress? BeginAddress { get; init; }
    /// <summary>已知驱逐边界。</summary>
    public LogicalAddress? HeadAddress { get; init; }
    /// <summary>已知落盘边界。</summary>
    public LogicalAddress? FlushedUntilAddress { get; init; }
    /// <summary>已知写游标（上层快照场景注入，优先于扫盘）。</summary>
    public LogicalAddress? RecoveredTail { get; init; }
    /// <summary>已知溢出写游标（上层快照场景注入）。</summary>
    public LogicalAddress? OverflowTailAddress { get; init; }
}
