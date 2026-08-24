using System.Runtime.CompilerServices;

namespace TC.Tier.Runtime.Structures.Mirror;

/// <summary>核心机制 partial：checkpoint 会话 + 读 + N=2 截断 + 2PC + Dispose。</summary>
public abstract partial class MirrorBase
{
    // ════════════════════════════════════════════════════════════
    // === checkpoint 会话（子类写门面的公共支撑）===
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// checkpoint 会话开启（子类首个写入调用）：分配会话版本号。会话内所有 record 共用同号，
    /// 恢复时按版本号识别"最后一批未裁决 record"（meta prepared&gt;committed 时尾截断）。
    /// </summary>
    private protected void BeginCheckpointSession()
    {
        EnsureNotDisposed();
        EnsureReady();
        if (_sessionActive)
            throw new InvalidOperationException("checkpoint 会话已激活——先 ConfirmCommitted/Abort 当前会话");
        _sessionActive = true;
        _sessionVersion = _currentVersion + 1;
    }

    /// <summary>记录追加后更新链尾水位（子类每次 Allocate+Write 后调用）。</summary>
    private protected void OnRecordAppended(LogicalAddress start, long recordSize)
    {
        _lastRecordEnd = _engine.CalculationAddress(start, recordSize);
    }

    // ════════════════════════════════════════════════════════════
    // === 截断（N=2 轮替）===
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 头截断（N=2 立即回收最老 checkpoint 方向）。ConfirmCommitted 自动调；也可业务显式调。
    /// <para>保留窗口：当前 + 上一个；<see cref="ComputeRetainFloor"/> 返回保留窗口最老版本地址
    /// （PagedMirror = 所有页 keepAddr 最小值——引擎 MinAddress 是全局水位，只能推进到所有页都同意回收的地址）。</para>
    /// </summary>
    public void ReclaimOldVersions()
    {
        EnsureNotDisposed();
        if (!_hasCommittedVersion) return;
        var keepAddr = ComputeRetainFloor();
        if (keepAddr == LogicalAddress.Empty) return;
        if (keepAddr.CompareTo(_engine.MinAddress) <= 0) return; // 无可回收
        // 只回收链尾（最老）方向；不碰链头（当前）方向
        _engine.ReclaimHead(keepAddr);
        _lowestVersionAddress = keepAddr;
        PruneFrameBook(keepAddr);   // 被回收帧的几何账面条目清理
    }

    /// <summary>
    /// ★ 计算保留窗口最老版本地址（N=2：第二新 record 的起始地址；不足两版回退链头自身=不回收）。
    /// WholeMirror 单链取全局第二新；PagedMirror 取所有页的第二新最小值。
    /// </summary>
    private protected abstract LogicalAddress ComputeRetainFloor();

    // ════════════════════════════════════════════════════════════
    // === 2PC（ITransactionParticipant，独立协议，与数据写正交）===
    // ════════════════════════════════════════════════════════════

    long ITransactionParticipant.LastCommittedSeq => Volatile.Read(ref _lastCommittedSeq);
    long ITransactionParticipant.LastPreparedSeq => Volatile.Read(ref _lastPreparedSeq);

    /// <summary>
    /// Prepare：flush 已写数据 + meta.Commit（记录 LastPreparedSeq + 悬干水位）。
    /// <para>★ checkpoint 数据由子类写门面（Begin/Chunk/End 或 WritePage）先行写盘——Prepare 是 2PC 持久化点
    /// （数据 fsync 先于 meta fsync，断电时 meta 绝不标记 data 未落盘的 commit 点）。</para>
    /// </para>崩溃在 Prepare 后 Commit 前：seq 未推进 → 恢复按 meta 裁决尾截断悬干（一致）。</para>
    /// </summary>
    public void Prepare(long seq)
    {
        EnsureNotDisposed();
        EnsureReady();
        _engine.Flush();
        Volatile.Write(ref _lastPreparedSeq, seq);
        WriteMeta();
    }

    /// <summary>Prepare 异步轨（flush 原生仅同步，实质等价）。</summary>
    public async ValueTask PrepareAsync(long seq, CancellationToken ct)
    {
        Prepare(seq);
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// ConfirmCommitted：CAS 推进 LastCommittedSeq + 推进链头（子类 <see cref="OnConfirmSession"/>）+
    /// N=2 立即头截断回收最老 + 刷新 meta + 触发回调。
    /// </summary>
    public void ConfirmCommitted(long seq)
    {
        long current;
        do
        {
            current = Volatile.Read(ref _lastCommittedSeq);
            if (seq <= current) return;
        } while (Interlocked.CompareExchange(ref _lastCommittedSeq, seq, current) != current);

        // 会话提交：链头推进 + 链尾水位推进
        OnConfirmSession();
        _currentVersion = _sessionVersion;
        _committedChainEnd = _lastRecordEnd;
        _hasCommittedVersion = true;
        _sessionActive = false;
        _sessionVersion = 0;

        ReclaimOldVersions(); // N=2 立即回收最老（spec §2.7）
        if (_settings.MetaPolicyKind != MetaPolicyKind.Disabled)
            WriteMeta();
        FireTransactionCallbacks(seq);
    }

    /// <summary>Abort：尾截断回退悬干新 checkpoint（物理丢弃 [_committedChainEnd, AllocatedTail)）+ 回退会话链头状态 + meta。</summary>
    public void Abort(long seq)
    {
        EnsureNotDisposed();
        if (seq <= Volatile.Read(ref _lastAbortedSeq)) return; // 幂等

        if (_sessionActive)
        {
            OnAbortSession();
            if (_lastRecordEnd.CompareTo(_committedChainEnd) > 0)
            {
                _engine.ReclaimTail(_committedChainEnd); // 物理丢弃悬干（引擎退化 AllocatedTail）
                _lastRecordEnd = _committedChainEnd;
            }
            _sessionActive = false;
            _sessionVersion = 0;
        }

        Volatile.Write(ref _lastPreparedSeq, _lastCommittedSeq);
        Volatile.Write(ref _lastAbortedSeq, seq);
        if (_settings.MetaPolicyKind != MetaPolicyKind.Disabled)
            WriteMeta();
    }

    /// <summary>Abort 异步轨（引擎截断原生同步，实质等价）。</summary>
    public async ValueTask AbortAsync(long seq, CancellationToken ct)
    {
        Abort(seq);
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>注册提交回调（链式触发）。</summary>
    public void OnCommitted(long seq, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_txCallbackLock)
        {
            if (seq <= Volatile.Read(ref _lastCommittedSeq))
            {
                callback();
                return;
            }

            if (!_txCallbacks.TryGetValue(seq, out var list))
            {
                list = new();
                _txCallbacks[seq] = list;
            }

            list.Add(callback);
        }
    }

    private void FireTransactionCallbacks(long committedSeq)
    {
        List<Action>? toFire = null;
        lock (_txCallbackLock)
        {
            foreach (var kvp in _txCallbacks)
            {
                if (kvp.Key <= committedSeq)
                {
                    (toFire ??= new()).AddRange(kvp.Value);
                }
            }

            while (_txCallbacks.Count > 0 && _txCallbacks.Keys[0] <= committedSeq)
                _txCallbacks.RemoveAt(0);
        }

        if (toFire is not null)
            foreach (var cb in toFire)
                cb(); // 锁外触发避免死锁
    }

    // ════════════════════════════════════════════════════════════
    // === 会话链头推进/回退钩子（子类实现各自几何）===
    // ════════════════════════════════════════════════════════════

    /// <summary>ConfirmCommitted 时推进链头（WholeMirror：pending→committed 单链头；PagedMirror：逐页推进）。</summary>
    private protected abstract void OnConfirmSession();

    /// <summary>Abort 时回退会话链头状态（committed 链头未被会话触碰，只需丢弃 pending）。</summary>
    private protected abstract void OnAbortSession();

    // ════════════════════════════════════════════════════════════
    // === Dispose ===
    // ════════════════════════════════════════════════════════════

    /// <summary>已释放则抛——委托 LifecycleBase.ThrowIfDisposed。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected void EnsureNotDisposed() => ThrowIfDisposed();
}
