namespace TC.Tier.Contracts.Lifecycle;

/// <summary>
/// 恢复进度事件参数（进度条订阅）。所有 LifecycleBase 派生类共享。
/// </summary>
public readonly record struct RecoveryProgress
{
    public required RecoveryPhase Phase { get; init; }
    public int Percent { get; init; }
    public string? Detail { get; init; }
}
