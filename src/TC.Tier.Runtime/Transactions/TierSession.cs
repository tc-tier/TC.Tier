namespace TC.Tier.Runtime.Transactions;

/// <summary>
/// ★ 会话（sealed，单线程会话契约——staged 缓冲与覆盖层无锁；SessionManager 与管线自身完全线程安全）。
/// <para>读写统一句柄（session-manager-design.md §3.2）：写=Stage 暂存物化委托 → Commit 入管线单飞；
/// 读=EnterReadScope（聚合 epoch）/ 地址直达（无会话，一等公民零税）。</para>
/// <para>★ 单飞使用门闩：同一时刻仅一个线程使用本会话（FASTER 同款契约）——staged 快照/覆盖层
/// 无锁数据结构的并发腐蚀防线，违规立即抛；Commit 入队后即释放门闩（await 回执不占）。</para>
/// <para>会话状态机：Active →（回合物化/Prepare/决策失败回执）Faulted →（Dispose）Disposed。
/// Faulted 后 Stage/Commit 抛（fault 原因内联）；重开=OpenSession。</para>
/// </summary>
public sealed class TierSession : IDisposable
{
    private readonly SessionManager _manager;
    private readonly string? _name;

    // staged 物化委托缓冲（会话单线程——无锁；Commit 快照移交管线）
    private readonly List<(Action Materialize, object? Tag)> _staged = new();

    // 单飞使用门闩（0=空闲，1=使用中）
    private int _gate;

    private int _state;                 // 0=Active, 1=Faulted, 2=Disposed（SessionState）
    private Exception? _fault;

    // 开放事务登记位（首个 Stage 置位；事务终态复位）——规则 W（OpenTxCount）数据源
    private bool _txRegistered;

    /// <summary>覆盖层挂点（组合层自管：staged 命令表/批号映射等——Runtime 只定协议不定内容）。</summary>
    public object? State { get; set; }

    /// <summary>会话名（诊断用，可空）。</summary>
    public string? Name => _name;

    /// <summary>当前会话状态。</summary>
    public SessionState SessionState => (SessionState)Volatile.Read(ref _state);

    /// <summary>Faulted 原因（仅 Faulted 态非空；诊断/重开决策用）。</summary>
    public Exception? Fault => _fault;

    /// <summary>本会话当前 staged 物化委托数（诊断/测试观测）。</summary>
    public int StagedCount => _staged.Count;

    internal TierSession(SessionManager manager, string? name)
    {
        _manager = manager;
        _name = name;
    }

    // ══════════ 单飞使用门闩 ══════════

    private void EnterGate()
    {
        ThrowIfNotActive();
        if (Interlocked.CompareExchange(ref _gate, 1, 0) != 0)
            throw new InvalidOperationException(
                $"会话 '{_name ?? "(anonymous)"}' 同一时刻被多于一个线程使用——单线程会话契约" +
                "（staged 缓冲与覆盖层无锁，FASTER 同款）");
    }

    private void ExitGate() => Volatile.Write(ref _gate, 0);

    private void ThrowIfNotActive()
    {
        var s = (SessionState)Volatile.Read(ref _state);
        ObjectDisposedException.ThrowIf(s == SessionState.Disposed, this);
        if (s == SessionState.Faulted)
            throw new InvalidOperationException(
                $"会话 '{_name ?? "(anonymous)"}' 已 Faulted（回合失败）——重开 OpenSession", _fault);
    }

    // ══════════ 读路径（会话读——聚合 epoch 协议件）══════════

    /// <summary>
    /// ★ 会话读 scope：聚合域内全部 epoch 读保护参与者（一次进/出），暴露覆盖层（RYW 挂点）。
    /// <para>需要协调的读（RYW/scope 批量/未来路由）经此入口；地址直达读（自缓冲句柄）无会话零税
    /// ——一等公民永远保留（设计稿 §3.2 两档）。保护区纪律见 <see cref="SessionReadScope"/>。</para>
    /// </summary>
    public SessionReadScope EnterReadScope()
    {
        ThrowIfNotActive();
        return new SessionReadScope(this, _manager.ReadProtectionHolders());
    }

    // ══════════ 写路径（档 B 协调）══════════

    /// <summary>
    /// ★ 暂存物化委托——结构零触碰（staged 仅存委托；管线回合内按 FIFO 序统一执行）。
    /// <para>物化委托在管线线程执行：①应为纯缓冲写（Append/Set 类）尽力不抛——抛=管线 Faulted
    /// （域报废重建，§6 物化失败模型：悬干无法安全清除，续跑会洗白）；②业务校验放 Stage 前
    /// （此时抛无副作用）。</para>
    /// </summary>
    /// <param name="materialize">物化委托（写结构缓冲——管线回合内执行）。</param>
    /// <param name="tag">调用方关联物（诊断/覆盖层映射——组合层自定）。</param>
    public void Stage(Action materialize, object? tag = null)
    {
        ArgumentNullException.ThrowIfNull(materialize);
        EnterGate();
        try
        {
            _staged.Add((materialize, tag));
            if (!_txRegistered)
            {
                _txRegistered = true;
                _manager.OnSessionTxOpened();
            }
        }
        finally { ExitGate(); }
    }

    /// <summary>
    /// ★ 提交（TxRound 入口）：staged 快照入管线 → await 自己的 TCS 回执。
    /// <para>批合并：管线排空当前积压为一批——同批回合共享批 seq、整批一次 Prepare-all+Confirm-all
    /// （FIFO 全序不变）。空 staged 合法（纯 seq 推进回合）。ct 取消=排队撤销（出队丢弃）。</para>
    /// </summary>
    /// <param name="context">调用方关联上下文（随回执域诊断面可读——协议不解释内容）。</param>
    /// <returns>域 seq（本回合所在批的共享 seq）。</returns>
    public async ValueTask<long> CommitAsync(object? context = null, CancellationToken ct = default)
    {
        TxRound round = SnapshotAndEnqueue(context, awaitDecision: null);
        try
        {
            long seq = await round.Completion.Task.WaitAsync(ct).ConfigureAwait(false);
            CloseTxIfIdle();
            return seq;
        }
        catch (OperationCanceledException)
        {
            // 排队撤销（TryCancel 对已取走回合无效——在途不可打断，等终态防登记泄漏）
            if (!round.TryCancel())
            {
                try { await round.Completion.Task.ConfigureAwait(false); }
                catch { /* 只等终态，结果不再传播 */ }
            }
            CloseTxIfIdle();
            throw;
        }
        catch (Exception ex)
        {
            FaultSession(ex);
            throw;
        }
    }

    /// <summary>
    /// ★ 复制回合入口（ReplicatedRound——Raft WAL 域形态）：staged 物化 → Prepare-all
    /// （fsync-before-replicate，参与者落盘语义）→ <b>await awaitDecision(候选 seq)</b>
    /// （决策注入：多数派共识——Phase 4 自研协议对接位）→ true: Confirm-all（★不可回退点）；
    /// false/超时/异常: Abort 已 Prepare 者（D2 截断）→ <see cref="RollbackException"/>。
    /// </summary>
    public async ValueTask<long> CommitReplicatedAsync(
        Func<long, CancellationToken, ValueTask<bool>> awaitDecision,
        object? context = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(awaitDecision);
        TxRound round = SnapshotAndEnqueue(context, awaitDecision);
        try
        {
            long seq = await round.Completion.Task.WaitAsync(ct).ConfigureAwait(false);
            CloseTxIfIdle();
            return seq;
        }
        catch (OperationCanceledException)
        {
            if (!round.TryCancel())
            {
                try { await round.Completion.Task.ConfigureAwait(false); }
                catch { /* 只等终态，结果不再传播 */ }
            }
            CloseTxIfIdle();
            throw;
        }
        catch (Exception ex)
        {
            FaultSession(ex);
            throw;
        }
    }

    private TxRound SnapshotAndEnqueue(object? context,
        Func<long, CancellationToken, ValueTask<bool>>? awaitDecision)
    {
        TxRound round;
        EnterGate();
        try
        {
            round = new TxRound(this,
                _staged.Count == 0 ? Array.Empty<(Action, object?)>() : _staged.ToArray(),
                context, awaitDecision);
            _staged.Clear();
            round.SetPendingOn(this);
        }
        finally { ExitGate(); }
        _manager.EnqueueRound(round);
        return round;
    }

    /// <summary>成功回执后事务登记复位（会话线程续体——staged/_txRegistered 同线程序贯读写安全）。</summary>
    private void CloseTxIfIdle()
    {
        if (_txRegistered && _staged.Count == 0)
        {
            _txRegistered = false;
            _manager.OnSessionTxClosed();
        }
    }

    /// <summary>失败回执：会话 Faulted + 事务登记复位（Faulted 会话不可再 Stage）。</summary>
    private void FaultSession(Exception ex)
    {
        Interlocked.CompareExchange(ref _fault, ex, null);
        Interlocked.CompareExchange(ref _state, (int)SessionState.Faulted, (int)SessionState.Active);
        if (_txRegistered)
        {
            _txRegistered = false;
            _manager.OnSessionTxClosed();
        }
    }

    /// <summary>
    /// ★ 未决撤销：staged 缓冲清空 + 排队中回合标记出队丢弃（结构零触碰、seq 零消耗）。
    /// <para>回合中不可打断：在途回合（管线已取走）等终态后返回。</para>
    /// <para>Abort 后会话保持 Active（可继续 Stage）。</para>
    /// </summary>
    public void Abort()
    {
        if ((SessionState)Volatile.Read(ref _state) == SessionState.Disposed) return;   // Dispose 已隐式 Abort

        EnterGate();
        try
        {
            _staged.Clear();
            if (_txRegistered)
            {
                _txRegistered = false;
                _manager.OnSessionTxClosed();
            }
        }
        finally { ExitGate(); }

        // 未决回合二分（与管线取走原子竞争）：排队→标记丢弃；在途→等终态（不可打断）
        Volatile.Read(ref _pendingRound)?.ResolvePendingForAbort();
    }

    /// <summary>开放状态=隐式 Abort；幂等。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _state, (int)SessionState.Disposed) == (int)SessionState.Disposed) return;
        Abort();
        _manager.OnSessionClosed(this);
    }

    // ══════════ 在途回合引用（Abort 判定用）══════════

    private TxRound? _pendingRound;

    internal void SetPending(TxRound round) => Volatile.Write(ref _pendingRound, round);

    /// <summary>回执终态后由 TxRound 清引用（管线线程——CAS 匹配才清，防误清后继回合）。</summary>
    internal void ClearPending(TxRound round)
        => Interlocked.CompareExchange(ref _pendingRound, null, round);
}

/// <summary>会话状态机：Active（可写可提交）→ Faulted（回合失败回执，重开）→ Disposed（终态）。</summary>
public enum SessionState
{
    Active = 0,
    Faulted = 1,
    Disposed = 2,
}
