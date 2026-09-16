namespace TC.Tier.Contracts.Lifecycle;

/// <summary>
/// 恢复进度事件参数（进度条订阅）。所有 LifecycleBase 派生类共享。
/// </summary>
public readonly record struct RecoveryProgress
{
    /// <summary>当前恢复阶段（持有者中立，见 <see cref="RecoveryPhase"/>——与 <see cref="RecoveryState.Phase"/> 同语义）。</summary>
    /// <returns>当前恢复阶段（持有者中立，见 <see cref="RecoveryPhase"/>——与 <see cref="RecoveryState.Phase"/> 同语义）。</returns>
    public required RecoveryPhase Phase { get; init; }
    /// <summary>0-100（Recovering 阶段细分进度；Completed=100，Failed=0）。</summary>
    /// <returns>0-100（Recovering 阶段细分进度；Completed=100，Failed=0）。</returns>
    /// <remarks>Recovering 阶段细分进度；Completed=100，Failed=0。</remarks>
    public int Percent { get; init; }
    /// <summary>可选详情（引擎特定，如 "page 1234/5678"、"meta ok"）。</summary>
    /// <remarks>可空，持有者自定义文案，供进度条展示。</remarks>
    /// <returns>可选详情（引擎特定，如 "page 1234/5678"、"meta ok"）。</returns>
    public string? Detail { get; init; }
}
