namespace TC.Tier.Contracts.Transactions;

/// <summary>
/// 事务协调者——跨数据结构原子提交的全局提交点。底层提供接口 + 默认实现（TransactionLog）。
/// <para>设计见 transaction-design.md。</para>
/// <para>★ 职责边界：底层提供原子提交原语 + 默认实现，上层编排 2PC（哪些结构参与、顺序、回滚）。</para>
/// </summary>
public interface ITransactionLog : IDisposable, IAsyncDisposable
{
    /// <summary>★ 注册参与者（带名称标识，用于诊断/按名称查找）。</summary>
    /// <para>名称用于：诊断（"Ring-A Prepare 失败，Abort Ring-A + Log-B"）、按名称查找、恢复报告。</para>
    void Register(string name, ITransactionParticipant participant);

    /// <summary>按名称取消注册（可选，子系统销毁时调）。返回是否找到并移除。</summary>
    bool Unregister(string name);

    /// <summary>当前注册的参与者名称集合（诊断用）。</summary>
    IReadOnlyCollection<string> ParticipantNames { get; }

    /// <summary>★ 协调者提交事件：每次 Commit 成功（全局 seq 推进 + Flush 落盘）后触发，传新 seq。</summary>
    event Action<long>? OnCommitted;

    /// <summary>
    /// ★ 真正两阶段提交（本次改造）：Phase 1 foreach Prepare → 任一失败 → foreach Abort；
    /// 全部成功 → persist commit record（原子点）→ Phase 2 foreach ConfirmCommitted。
    /// <para>落盘分层：参与者 Prepare 各自 flush 数据+meta；协调者只持久化 commit record（原子点）。</para>
    /// <para>返回新 seq。Phase 1 失败抛异常（已 Prepare 的全部 Abort）。</para>
    /// </summary>
    ValueTask<long> CommitAsync(CancellationToken ct);
    long Commit();

    /// <summary>
    /// ★ 显式 Abort 当前轮次（可选，通常 Commit 内部失败自动调）。
    /// <para>对所有 LastPreparedSeq > LastCommittedSeq 的参与者调 Abort。</para>
    /// </summary>
    void Abort();

    /// <summary>当前全局已提交序号（恢复/运行时读）。</summary>
    long LastCommittedSeq { get; }

    /// <summary>启动时从磁盘加载 commit record（读 lastCommittedSeq）。返回 lastCommittedSeq（无 record = 0）。</summary>
    ValueTask<long> LoadAsync(CancellationToken ct);
    long Load();

    /// <summary>
    /// ★ 恢复协调（本次改造为双向分支）：Load commit record + 对每个参与者裁决。
    /// <para>- committedSeq == 0（空盘/损坏）：所有有悬干数据的参与者 Abort</para>
    /// <para>- LastCommittedSeq &lt; committedSeq → ConfirmCommitted(committedSeq)（正向：未同步推进）</para>
    /// <para>- LastPreparedSeq &gt; committedSeq → Abort(LastPreparedSeq)（反向：超前悬干丢弃）</para>
    /// <para>调用方须在调用本方法前 Register 所有参与者（按依赖顺序：底层先，上层后）。</para>
    /// </summary>
    long LoadAndReconcile();
    ValueTask<long> LoadAndReconcileAsync(CancellationToken ct);
}
