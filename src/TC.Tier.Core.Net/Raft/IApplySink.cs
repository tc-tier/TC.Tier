namespace TC.Tier.Core.Net.Raft;

/// <summary>
/// 提交 → 应用管道接口（spec-05 契约——共识循环与 apply 解耦）。
/// <para>状态机（共识循环单 worker）commitIndex 推进 → <see cref="Submit"/> 投递 committed 批次；
///   apply 管道（独立单 worker）按序 apply → 经 <see cref="AppliedTo"/> 通知状态机
///   （完成 ReplicateAsync 的 pending——spec-05 契约②：返回 = committed 且 applied）。</para>
/// </summary>
public interface IApplySink
{
    /// <summary>提交新批次（[lastSubmitted+1, commitIndex]——管道内部去重推进）。</summary>
    /// <param name="commitIndex">新提交水位（单调不减——管道内部去重推进）。</param>
    void Submit(long commitIndex);

    /// <summary>已应用推进（appliedIndex ≥ index——状态机完成 pending 用；apply worker 线程回调，只入队不阻塞）。</summary>
    event Action<long>? AppliedTo;
}
