namespace TC.Tier.Runtime.Structures.Log;

/// <summary>
/// LogBase 的 ITransactionParticipant 实现（跨数据结构 2PC 原子提交）。
/// <para>★ base.md §2.6：LogBase 作为事务参与者，Prepare 落盘数据 + meta，ConfirmCommitted 推进 seq，OnCommitted 链式回调。</para>
/// <para>★ Prepare 执行链：FlushUntilAsync(seq 地址) + MetaPolicy.Commit（数据 + meta 落盘，"悬空"状态——
/// 崩溃在此后 ConfirmCommitted 前恢复时丢弃）。参考 2PC 通用模式（CAS 单调 seq + SortedList 回调 + 锁外触发），
/// 按 Log 的 Prepare 语义（FlushUntil + meta.Commit）全新实现。</para>
/// </summary>
public abstract partial class LogBase
{
    // === ITransactionParticipant 状态 ===
    private long _lastCommittedSeq = -1; // -1 = 未参与事务；≥0 = 已提交到该 seq
    private long _lastPreparedSeq = -1;  // -1 = 未参与事务；≥0 = 最近一次 Prepare 的 seq
    private readonly SortedList<long, List<Action>> _txCallbacks = new(); // 按 seq 排序的提交回调
    private readonly object _txCallbackLock = new();

    /// <summary>当前已提交序号（恢复/运行时读）。-1 = 未参与事务。</summary>
    public long LastCommittedSeq => Volatile.Read(ref _lastCommittedSeq);

    /// <summary>最近一次 Prepare 的 seq（恢复判定悬空事务）。-1 = 从未 Prepare。</summary>
    public long LastPreparedSeq => Volatile.Read(ref _lastPreparedSeq);

    /// <summary>
    /// ★ Prepare（事务准备）：本结构准备提交到 seq。落盘数据 + meta。
    /// <para>★ 新模型：FlushUntil(TailAddress) 数据落盘后，用 TailAddress 作为 committedOffset 写 meta。</para>
    /// <para>★ Abort 支撑：meta 同块持久化当前提交边界（<see cref="_txRollbackTail"/> →
    /// PreparedTailAddress 字段）——本轮 Prepare 窗口的回退点跨崩溃可用。</para>
    /// </summary>
    /// <param name="seq">准备提交的序号。</param>
    public void Prepare(long seq)
    {
        Volatile.Write(ref _lastPreparedSeq, seq);
        FlushUntil(TailAddress);
        AppendMeta(TailAddress);  // opaque 由 SetOpaqueMetaPayload 预设值自动带入
    }

    public async ValueTask PrepareAsync(long seq, CancellationToken ct)
    {
        Volatile.Write(ref _lastPreparedSeq, seq);
        await FlushUntilAsync(TailAddress, ct).ConfigureAwait(false);
        await AppendMetaAsync(TailAddress, ct).ConfigureAwait(false);  // opaque 由 SetOpaqueMeta stage 自动带入
    }

    /// <summary>
    /// ★ ConfirmCommitted（事务确认提交）：本结构已提交到 seq。推进 LastCommittedSeq，触发 OnCommitted 回调。
    /// <para>★ Abort 支撑：推进成功即更新提交边界 <see cref="_txRollbackTail"/> = 当前尾
    /// （协议下 Confirm 时尾 == 本轮 Prepare 落的尾——Prepare 与终态之间无追加；
    /// 恢复正向推进路径下尾 == meta 恢复尾，同为该 seq 对应的尾）。</para>
    /// </summary>
    /// <param name="seq">确认提交的序号。</param>
    public void ConfirmCommitted(long seq)
    {
        long current;
        do
        {
            current = Volatile.Read(ref _lastCommittedSeq);
            if (seq <= current) return; // 已提交到更高 seq，忽略旧的
        } while (Interlocked.CompareExchange(ref _lastCommittedSeq, seq, current) != current);

        if (TailAddress > _txRollbackTail) _txRollbackTail = TailAddress;   // ★ 新提交边界（单调）
        FireTransactionCallbacks(seq);
    }

    /// <summary>
    /// ★ Abort（2PC 回滚）：TruncateSuffix 回退到<b>上一已确认提交边界</b>，丢弃本轮悬干数据。
    /// <para>回退点 = <see cref="_txRollbackTail"/>（ConfirmCommitted 推进的提交尾，随 meta 持久化为
    /// PreparedTailAddress——跨崩溃的恢复裁决同一路）。⚠️ 窗口契约：上一提交点之后的全部追加
    /// 必须都属于被回滚的事务（标准 2PC WAL 契约——TransactionLog 协议天然满足；
    /// 混入非事务写会被一并回退）。</para>
    /// <para>守卫矩阵：seq ≤ LastCommittedSeq（已提交）→ no-op；seq ≠ LastPreparedSeq（陈旧 Abort）→
    /// 仅复位记账；无既有提交边界（Empty，如首事务）/ 边界已被头截断回收 / 无悬干数据 → 仅复位记账。</para>
    /// <para>⚠️ 调用契约：与 Append/Flush 单写者串行（事务终态点调用，TransactionLog 协议天然满足）。</para>
    /// </summary>
    public void Abort(long seq)
    {
        EnsureNotDisposed();
        if (seq <= LastCommittedSeq) return;   // 已提交到 ≥seq——不可回滚已提交数据

        long preparedSeq = LastPreparedSeq;
        Volatile.Write(ref _lastPreparedSeq, Volatile.Read(ref _lastCommittedSeq));   // 记账复位（各分支一致）

        if (preparedSeq != seq) return;                       // 陈旧 Abort——非本轮窗口
        LogicalAddress rollbackTail = _txRollbackTail;        // 提交边界保留（Abort 不改变边界本身）
        if (rollbackTail == LogicalAddress.Empty) return;     // 无既有提交边界（首事务）——无安全回退点
        if (rollbackTail < BeginAddress) return;              // 边界已被头截断物理回收——回退无意义
        if (rollbackTail >= TailAddress) return;              // 无悬干数据（Prepare 后未追加）

        if (!TruncateSuffix(rollbackTail)) return;            // 复用成熟尾截断（引擎 ReclaimTail + 页缓冲复位）
        OnAborted(rollbackTail);                              // 子类夹自管水位（EntryLog 夹 CommittedOffset）
        AppendMeta(rollbackTail);                             // meta 重写：持久化回退后状态（窗口关、seq 复位）
    }

    public async ValueTask AbortAsync(long seq, CancellationToken ct)
    {
        EnsureNotDisposed();
        if (seq <= LastCommittedSeq) return;

        long preparedSeq = LastPreparedSeq;
        Volatile.Write(ref _lastPreparedSeq, Volatile.Read(ref _lastCommittedSeq));

        if (preparedSeq != seq) return;
        LogicalAddress rollbackTail = _txRollbackTail;
        if (rollbackTail == LogicalAddress.Empty) return;
        if (rollbackTail < BeginAddress) return;
        if (rollbackTail >= TailAddress) return;

        if (!TruncateSuffix(rollbackTail)) return;
        OnAborted(rollbackTail);
        await AppendMetaAsync(rollbackTail, ct).ConfigureAwait(false);
    }

    /// <summary>★ Abort 尾截断完成后的子类钩子——夹自管水位到回退点（默认空；EntryLog 夹 CommittedOffset）。</summary>
    /// <param name="rollbackTail">回退点（截断后的新尾）。</param>
    protected virtual void OnAborted(LogicalAddress rollbackTail) { }

    /// <summary>
    /// ★ OnCommitted（事务提交回调注册）：注册 seq 的提交回调。若已提交到更高 seq，则立即触发。
    /// </summary>
    /// <param name="seq">注册回调的序号。</param>
    /// <param name="callback">提交回调。</param>
    /// <exception cref="ArgumentNullException">当 callback 为 null 时抛出。</exception>
    public void OnCommitted(long seq, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_txCallbackLock)
        {
            // 已提交到更高 seq → 立即同步触发
            if (seq <= Volatile.Read(ref _lastCommittedSeq))
            {
                callback();
                return;
            }

            if (!_txCallbacks.TryGetValue(seq, out var list))
            {
                list = new List<Action>();
                _txCallbacks[seq] = list;
            }

            list.Add(callback);
        }
    }

    /// <summary>触发所有 seq ≤ committedSeq 的回调（锁外触发，避免回调里再注册回调死锁）。</summary>
    private void FireTransactionCallbacks(long committedSeq)
    {
        List<Action>? toFire = null;
        List<long>? toRemove = null;
        lock (_txCallbackLock)
        {
            foreach (var kvp in _txCallbacks)
            {
                if (kvp.Key > committedSeq) break; // SortedList 升序，超出则停
                (toFire ??= new List<Action>()).AddRange(kvp.Value);
                (toRemove ??= new List<long>()).Add(kvp.Key);
            }

            if (toRemove != null)
            {
                foreach (var key in toRemove) _txCallbacks.Remove(key);
            }
        }

        // 锁外触发（避免回调里再注册回调死锁）
        if (toFire != null)
            foreach (var cb in toFire)
                cb();
    }
}