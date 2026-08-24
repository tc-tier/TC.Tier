namespace TC.Tier.Runtime.Structures.Log.Contracts;

/// <summary>
/// 提交策略接口——EntryLog group 提交引擎按策略判定"何时"触发提交（不决定"怎么"提交）。
/// <para>★ 策略只决定"何时"，提交执行链（FlushUntilAsync + MetaPolicy.Commit + 推进 CommittedOffset）由 EntryLog 内部闭环。
/// 参见 EntryLog.md §3.5。</para>
/// <para>内置策略：</para>
/// <para>- <see cref="GroupCommitPolicy"/>：三维度阈值兜底（数据量/时间/记录数任一满足）</para>
/// <para>- <see cref="SynchronousCommitPolicy"/>：恒 true（每次 Append 立即提交，单条强制）</para>
/// <para>- <see cref="ExternalCommitPolicy"/>：恒 false（不自动提交，靠手动 CommitAsync 或 2PC）</para>
/// </summary>
public interface ICommitPolicy
{
    /// <summary>
    /// 判定是否触发提交（group 按三维度阈值；Synchronous 恒 true；External 恒 false）。
    /// <para>EntryLog 在 Append 后 + 后台循环定期调用此方法。</para>
    /// </summary>
    bool ShouldCommit(in CommitSnapshot snapshot);
}
