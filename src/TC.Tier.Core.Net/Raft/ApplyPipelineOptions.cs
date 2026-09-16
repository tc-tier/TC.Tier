namespace TC.Tier.Core.Net.Raft;

/// <summary>ApplyPipeline 参数（spec-05 §3/§6——appliedIndex 落盘节流 + 队列背压）。</summary>
public sealed record ApplyPipelineOptions
{
    /// <summary>默认配置（spec-05 §3 定案默认：1024 条 / 100ms——断电损失 ≤ 节流窗口的重放成本，重放幂等）。</summary>
    public static ApplyPipelineOptions Default { get; } = new();

    /// <summary>appliedIndex 落盘条数节流（每 N 条一次 meta 写）。</summary>
    public int PersistEvery { get; private init; } = 1024;

    /// <summary>appliedIndex 落盘时间节流（距上次落盘超过该间隔 → 落盘）。</summary>
    public TimeSpan PersistInterval { get; private init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>提交水位队列容量（默认 4096——apply 慢时提交推进自然减速，spec-05 §6 背压）。</summary>
    public int QueueCapacity { get; private init; } = 4096;

    /// <summary>With 链——条数节流（测试确定性：1 = 每条约盘）。</summary>
    /// <param name="every">每 N 条 appliedIndex 落盘一次（1 = 每条落盘——测试确定性用）。</param>
    /// <returns>新的 ApplyPipelineOptions 实例（副本语义）。</returns>
    public ApplyPipelineOptions WithPersistEvery(int every) => this with { PersistEvery = every };

    /// <summary>With 链——时间节流。</summary>
    /// <param name="interval">距上次落盘超过该间隔 → 落盘（断电损失 ≤ 节流窗口的重放成本）。</param>
    /// <returns>新的 ApplyPipelineOptions 实例（副本语义）。</returns>
    public ApplyPipelineOptions WithPersistInterval(TimeSpan interval) => this with { PersistInterval = interval };
}
