namespace TC.Tier.Contracts.Transactions;

/// <summary>
/// 事务参与者接口——数据结构参与 2PC 原子提交的统一契约。
/// <para>4 基类（Ring/Blob/Log/Index）各自实现（基类自带），实现类零额外代码即可参与 2PC。</para>
/// <para>设计见 transaction-design.md。</para>
/// </summary>
public interface ITransactionParticipant
{
    /// <summary>
    /// ★ Prepare（预备）：把数据写到设备并 Flush（真正持久化），附 seq，但 lastCommittedSeq 不推进。
    /// <para>数据持久化但"悬空"——未被 commit record 确认。崩溃在此后 Commit 前恢复时丢弃。</para>
    /// <para>★ 必须包含 Flush——WriteAsync 不保证落盘。</para>
    /// </summary>
    ValueTask PrepareAsync(long seq, CancellationToken ct);
    void Prepare(long seq);

    /// <summary>
    /// ★ CommitPoint（提交点确认）：本结构确认已提交到 seq。推进自身 lastCommittedSeq。
    /// <para>由 TransactionLog.Commit 触发（链式）或上层显式调。</para>
    /// </summary>
    void ConfirmCommitted(long seq);

    /// <summary>
    /// ★ 新增（2PC 完整回滚改造）：回滚未提交的 Prepare。
    /// <para>语义由各参与者 IO 模型决定：</para>
    /// <para>- Log/Ring/Snapshot追加部分：截断到 prepare 前水位（ReclaimTail）。</para>
    /// <para>- MetadataBase：内存窗口回退到上一已提交版本（零 IO）+ 尾截断。</para>
    /// <para>- MirrorBase：尾截断回退到上一 checkpoint。</para>
    /// <para>- IndexBase：no-op（纯内存参与者，回退 seq）。</para>
    /// <para>实现必须是幂等的——恢复时可能对同一 seq 多次调用。</para>
    /// </summary>
    void Abort(long seq);
    ValueTask AbortAsync(long seq, CancellationToken ct);

    /// <summary>当前已提交序号（恢复/运行时读）。</summary>
    long LastCommittedSeq { get; }

    /// <summary>
    /// ★ 最近一次 Prepare 的 seq。恢复时判定悬空事务用。
    /// <para> -1 = 从未 Prepare/未参与事务。</para>
    /// <para>恢复判定：LastPreparedSeq > 协调者 committedSeq → 悬空事务，丢弃；</para>
    /// <para>LastCommittedSeq 小于 committedSeq → 未同步，ConfirmCommitted 推进。</para>
    /// </summary>
    long LastPreparedSeq { get; }

    /// <summary>
    /// ★ 注册提交回调（链式触发）：当本结构提交到 seq 时触发 callback。
    /// <para>用于"A 提交 → 自动触发 B"链式编排。</para>
    /// </summary>
    void OnCommitted(long seq, Action callback);
}
