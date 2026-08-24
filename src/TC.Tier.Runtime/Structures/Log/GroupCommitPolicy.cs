namespace TC.Tier.Runtime.Structures.Log;

/// <summary>
/// ★ 可选提前提交策略（非底层兜底）——在页未满时按三维度阈值提前触发提交，降延迟。
/// <para>★ 定位（修正后）：本策略 <b>只负责"提前提交"优化</b>，<b>不承担</b>底层持久化兜底。
/// 底层页提交契约由 <see cref="EntryLog.OnPageFlushed"/> 恒成立保证（页满换页即 commit）。
/// 即使完全不注入本策略，最坏情况 = 写满一页才提交。</para>
/// <para>三维度阈值（页未满时提前触发，任一满足即提交）：</para>
/// <para>- 数据量（未提交字节 ≥ MaxUnflushedBytes）</para>
/// <para>- 时间（距上次提交 ≥ Interval）</para>
/// <para>- 记录数（未提交条数 ≥ MaxUnflushedCount）</para>
/// <para>★ 0 值语义（修正后，与文档一致）：</para>
/// <para>- <c>MaxUnflushedBytes = 0</c> = 字节维度立即满足（每次 Append 后即触发提前提交）</para>
/// <para>- <c>MaxUnflushedCount = 0</c> = 条数维度立即满足</para>
/// <para>- <c>Interval = 0</c> = 时间维度立即满足</para>
/// <para>三个全 0 = 每次 Append 立即提前提交（等同单条强制）。</para>
/// <para>★ 禁用某维度用 <c>Interval = -1ms</c>（时间）/ 设很大值（字节、条数）。注入 <c>null</c> 策略
/// 则完全不提前提交，仅靠底层页契约。</para>
/// </summary>
internal sealed class GroupCommitPolicy : ICommitPolicy
{
    /// <summary>
    /// 数据量阈值（未提交字节 ≥ 此值触发提前提交）。
    /// <para>0 = 字节维度立即满足（每次 Append 触发）。要禁用字节维度请设 <see cref="long.MaxValue"/>。</para>
    /// </summary>
    public long MaxUnflushedBytes { get; init; }

    /// <summary>
    /// 时间阈值（距上次提交 ≥ 此间隔触发提前提交）。
    /// <para>0 = 时间维度立即满足。-1ms（<c>TimeSpan.FromMilliseconds(-1)</c>）= 禁用时间维度。</para>
    /// </summary>
    public TimeSpan Interval { get; init; }

    /// <summary>
    /// 记录数阈值（未提交 entry 数 ≥ 此值触发提前提交）。
    /// <para>0 = 条数维度立即满足（每次 Append 触发）。要禁用条数维度请设 <see cref="int.MaxValue"/>。</para>
    /// </summary>
    public int MaxUnflushedCount { get; init; }

    /// <summary>
    /// 判定是否触发提前提交：三维度任一满足即 true。
    /// <para>★ 0 值 = 该维度立即满足（与文档一致）；-1ms / 大值 = 禁用该维度。</para>
    /// </summary>
    public bool ShouldCommit(in CommitSnapshot s) =>
        s.UnflushedBytes >= MaxUnflushedBytes ||
        (Interval != TimeSpan.FromMilliseconds(-1) && s.SinceLastCommit >= Interval) ||
        s.UnflushedCount >= MaxUnflushedCount;
}
