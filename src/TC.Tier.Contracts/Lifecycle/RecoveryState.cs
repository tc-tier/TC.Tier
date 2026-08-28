namespace TC.Tier.Contracts.Lifecycle;

/// <summary>
/// 恢复状态快照（原子可读，并发安全）。所有 LifecycleBase 派生类共享。
/// </summary>
public readonly record struct RecoveryState
{
    /// <summary>当前恢复阶段（见 <see cref="RecoveryPhase"/>——严格顺序推进，不可回退）。</summary>
    public required RecoveryPhase Phase { get; init; }
    /// <summary>0-100。Completed=100，Failed=0。</summary>
    public int Percent { get; init; }
    /// <summary>可选详情（引擎特定，如 "page 1234/5678"、"meta ok"、"commit N found"）。</summary>
    public string? Detail { get; init; }
    /// <summary>Failed 时的异常（其他阶段为 null）。</summary>
    public Exception? Error { get; init; }

    /// <summary>便利属性：是否已完成（Phase == Completed）。</summary>
    public bool IsCompleted => Phase == RecoveryPhase.Completed;
}