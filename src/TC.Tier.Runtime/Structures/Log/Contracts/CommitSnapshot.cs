namespace TC.Tier.Runtime.Structures.Log.Contracts;

/// <summary>
/// 提交判定快照（EntryLog 喂给 <see cref="ICommitPolicy"/> 的当前状态）。
/// <para>group 提交引擎按此快照的三维度（数据量/时间/记录数）判定是否触发提交。参见 EntryLog.md §3.5。</para>
/// </summary>
public readonly struct CommitSnapshot
{
    /// <summary>未提交字节数（= TailAddress - CommittedOffset）。</summary>
    public required long UnflushedBytes { get; init; }

    /// <summary>未提交 entry 数（自上次 commit 以来的 Append 次数）。</summary>
    public required int UnflushedCount { get; init; }

    /// <summary>距上次提交的时间。</summary>
    public required TimeSpan SinceLastCommit { get; init; }
}
