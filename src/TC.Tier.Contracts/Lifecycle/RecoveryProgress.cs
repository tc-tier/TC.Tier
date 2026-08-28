namespace TC.Tier.Contracts.Lifecycle;

/// <summary>
/// 恢复进度事件参数（进度条订阅）。所有 LifecycleBase 派生类共享。
/// </summary>
public readonly record struct RecoveryProgress
{
    /// <summary>当前恢复阶段（持有者中立，见 <see cref="RecoveryPhase"/>——与 <see cref="RecoveryState.Phase"/> 同语义）。</summary>
    public required RecoveryPhase Phase { get; init; }
    /// <summary>0-100（Recovering 阶段细分进度；Completed=100，Failed=0）。</summary>
    public int Percent { get; init; }
    /// <summary>可选详情（引擎特定，如 "page 1234/5678"、"meta ok"）。</summary>
    public string? Detail { get; init; }
}
