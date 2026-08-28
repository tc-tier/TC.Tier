namespace TC.Tier.Runtime.Structures.Ring;

/// <summary>
/// RingBase 的 ITransactionParticipant 实现（跨数据结构 2PC 原子提交）。
/// <para>★ base.md §2.6：Prepare 落盘数据 + meta，ConfirmCommitted 推进 seq，OnCommitted 链式回调。</para>
/// <para>★ Prepare 执行链：FlushUntilAsync(TailAddress) + WriteMetaAsync（数据 + meta 落盘，"悬空"状态——
/// 崩溃在此后 ConfirmCommitted 前恢复时丢弃）。完全对齐 LogBase.Transaction.cs 范式。</para>
/// <para>★ 本 partial 含 await，故不可标 unsafe（CS4004）。RingBase 主 partial 已 unsafe。</para>
/// </summary>
public abstract partial class RingBase<TKey>
{
    // === ITransactionParticipant 状态 ===
    private long _lastCommittedSeq = -1; // -1 = 未参与事务；≥0 = 已提交到该 seq
    private long _lastPreparedSeq = -1;  // -1 = 未参与事务；≥0 = 最近一次 Prepare 的 seq
    /// <summary>★ D2 Abort 回退点：最近一次<b>已确认提交</b>对应的尾（ConfirmCommitted 推进，单调）。
    /// 语义：上一提交点——其后的全部写入属于当前事务窗口（标准 2PC WAL 契约）。Abort 据此
    /// TruncateSuffix 回退；Prepare 随 meta 持久化（CommittedTailAddress 字段）；恢复时从 meta 还原
    /// ——跨崩溃的悬干裁决依据。Empty = 无既有提交边界（首事务 Abort 不截断）。</summary>
    private LogicalAddress _txRollbackTail;
    private readonly SortedList<long, List<Action>> _txCallbacks = new(); // 按 seq 排序的提交回调
    private readonly object _txCallbackLock = new();

    /// <summary>当前已提交序号（恢复/运行时读）。-1 = 未参与事务。</summary>
    public long LastCommittedSeq => Volatile.Read(ref _lastCommittedSeq);

    /// <summary>最近一次 Prepare 的 seq（恢复判定悬空事务）。-1 = 从未 Prepare。</summary>
    public long LastPreparedSeq => Volatile.Read(ref _lastPreparedSeq);

    /// <summary>
    /// ★ Prepare（事务准备）：落盘数据 + meta。
    /// <para>数据 flush 到 TailAddress，meta 落盘（"悬干"状态——崩溃在此后 ConfirmCommitted 前恢复时丢弃）。</para>
    /// <para>★★ meta 策略分路径（fsync 次数优化）：</para>
    /// <para>- <b>Transport 回落 MetaHost（宿主流嵌入，未注入传输）</b>：WriteMeta 先（Commit 写页池纯内存，
    ///   记 dataTail 水位）→ FlushUntil 末尾。meta record 随数据同页 flush 落盘（原子），<b>1 次 fsync</b>。</para>
    /// <para>- <b>Managed/Transport(注入传输)/Disabled</b>：FlushUntil 先 → WriteMeta。Managed meta 在独立文件，
    ///   WriteMeta 必须在 FlushUntil 之后才能记真实的 FlushedUntilAddress（否则崩溃在 Commit 后 FlushUntil 前，
    ///   meta 说刷到 X 但数据没真刷到，恢复丢数据），<b>2 次 fsync</b>。</para>
    /// </summary>
    /// <param name="seq">准备提交的序号。</param>
    public void Prepare(long seq)
    {
        Volatile.Write(ref _lastPreparedSeq, seq);
        // ★ Transport 策略且未注入传输（回落 MetaHost 宿主流嵌入）= 旧 Embedded 语义，走 1-fsync 优化路径
        if (MetaPolicy is TransportMetaPolicy<RingMetaHeader,RingMetaPayload> && _metaTransport is null)
        {
            // ★ 宿主流嵌入优化：1 次 fsync。meta record 随数据同页 flush 原子落盘。
            LogicalAddress dataTail = TailAddress;
            WriteMeta(flushedUntilOverride: dataTail);   // Commit 写页池（纯内存，TailAddress 推进过 meta record）
                                                          // ★ meta 同块持久化当前提交边界（CommittedTailAddress）
            FlushUntil(TailAddress);                      // 1 次 fsync：刷 [oldFlushedUntil, newTail)，含数据+meta
        }
        else
        {
            // Managed/注入传输 Transport/Disabled 原路径：FlushUntil 先（记真实已刷边界）→ WriteMeta 独立 fsync
            FlushUntil(TailAddress);
            WriteMeta();
        }
    }

    /// <summary>
    /// ★ PrepareAsync（事务准备）：落盘数据 + meta。对等 <see cref="Prepare"/> 的分路径逻辑。
    /// </summary>
    /// <param name="seq">准备提交的序号。</param>
    /// <param name="ct">取消令牌。</param>
    public async ValueTask PrepareAsync(long seq, CancellationToken ct)
    {
        Volatile.Write(ref _lastPreparedSeq, seq);
        if (MetaPolicy is TransportMetaPolicy<RingMetaHeader,RingMetaPayload> && _metaTransport is null)
        {
            LogicalAddress dataTail = TailAddress;
            await WriteMetaAsync(flushedUntilOverride: dataTail, ct: ct).ConfigureAwait(false);
            await FlushUntilAsync(TailAddress, ct).ConfigureAwait(false);
        }
        else
        {
            await FlushUntilAsync(TailAddress, ct).ConfigureAwait(false);
            await WriteMetaAsync(ct: ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// ★ ConfirmCommitted（事务确认提交）：推进 LastCommittedSeq，触发回调。
    /// <para>★ Abort 支撑：推进成功即更新提交边界 <see cref="_txRollbackTail"/> = 当前尾
    /// （协议下 Confirm 时尾 == 本轮 Prepare 落的尾；恢复正向推进路径下尾 == meta 恢复尾）。</para>
    /// </summary>
    /// <param name="seq">确认提交的序号。</param>
    public void ConfirmCommitted(long seq)
    {
        long current;
        do
        {
            current = Volatile.Read(ref _lastCommittedSeq);
            if (seq <= current) return; // 已提交到更高 seq，忽略更低 seq
        } while (Interlocked.CompareExchange(ref _lastCommittedSeq, seq, current) != current);

        if (TailAddress > _txRollbackTail) _txRollbackTail = TailAddress;   // ★ 新提交边界（单调）
        FireTransactionCallbacks(seq);
    }

    /// <summary>
    /// ★ Abort（2PC 回滚，D2 决策落地）：TruncateSuffix 回退到<b>上一已确认提交边界</b>，丢弃本轮悬干数据。
    /// <para>回退点 = <see cref="_txRollbackTail"/>（ConfirmCommitted 推进的提交尾，随 meta 持久化为
    /// CommittedTailAddress——跨崩溃的恢复裁决同一路）。⚠️ 窗口契约：上一提交点之后的全部写入
    /// 必须都属于被回滚的事务（标准 2PC WAL 契约——TransactionLog 协议天然满足；
    /// 混入非事务写会被一并回退）。</para>
    /// <para>守卫矩阵：seq ≤ LastCommittedSeq（已提交）→ no-op；seq ≠ LastPreparedSeq（陈旧 Abort）→
    /// 仅复位记账；无既有提交边界（Empty，如首事务）/ 无悬干数据 → 仅复位记账。</para>
    /// <para>回退后 WriteMeta 持久化（窗口关 + seq 复位 + 回退水位）；溢出引擎的悬干值不回收
    /// （无引用字节，无害残留）。</para>
    /// <para>⚠️ 调用契约：与并发 Write 串行（事务终态点调用，TransactionLog 协议天然满足）；
    /// 回退点不可落入已驱逐区（TruncateSuffix fail-fast 守卫）。</para>
    /// </summary>
    public void Abort(long seq)
    {
        EnsureReady();
        EnsureNotDisposed();
        if (seq <= LastCommittedSeq) return;   // 已提交到 ≥seq——不可回滚已提交数据

        long preparedSeq = LastPreparedSeq;
        Volatile.Write(ref _lastPreparedSeq, Volatile.Read(ref _lastCommittedSeq));   // 记账复位（各分支一致）

        if (preparedSeq != seq) return;                       // 陈旧 Abort——非本轮窗口
        LogicalAddress rollbackTail = _txRollbackTail;        // 提交边界保留（Abort 不改变边界本身）
        if (rollbackTail == LogicalAddress.Empty) return;     // 无既有提交边界（首事务）——无安全回退点
        if (rollbackTail >= TailAddress) return;              // 无悬干数据（Prepare 后未写入）

        TruncateSuffix(rollbackTail);                          // D2 尾截断（四件套）
        WriteMeta();                                           // meta 重写：持久化回退后状态
    }

    /// <summary>
    /// ★ AbortAsync（2PC 回滚的异步版，D2 决策落地）：TruncateSuffix 回退到上一已确认提交边界，
    /// 丢弃本轮悬干数据，随后异步 WriteMeta 持久化回退后状态。
    /// <para>对等 <see cref="Abort"/> 的守卫矩阵与窗口契约（回退点 = _txRollbackTail，
    /// 上一提交点之后的全部写入必须属于被回滚事务）。</para>
    /// </summary>
    /// <param name="seq">要回滚的 Prepare 序号。</param>
    /// <param name="ct">取消令牌——异步 meta 落盘途中响应取消。</param>
    /// <returns>回滚完成的 ValueTask。</returns>
    public async ValueTask AbortAsync(long seq, CancellationToken ct)
    {
        EnsureReady();
        EnsureNotDisposed();
        if (seq <= LastCommittedSeq) return;

        long preparedSeq = LastPreparedSeq;
        Volatile.Write(ref _lastPreparedSeq, Volatile.Read(ref _lastCommittedSeq));

        if (preparedSeq != seq) return;
        LogicalAddress rollbackTail = _txRollbackTail;
        if (rollbackTail == LogicalAddress.Empty) return;
        if (rollbackTail >= TailAddress) return;

        TruncateSuffix(rollbackTail);
        await WriteMetaAsync(ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// ★ OnCommitted（提交回调注册）：注册 seq 的回调。若已提交到更高 seq，则立即触发。
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
            foreach (var cb in toFire) cb();
    }
}
