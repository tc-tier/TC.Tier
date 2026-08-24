using System.Threading.Channels;
using TC.Tier.Contracts.Storage;
using TC.Tier.Contracts.Transactions;
using TC.Tier.Core.Shared;

namespace TC.Tier.Runtime.Transactions;

/// <summary>
/// ★ Session 管理器——组合域统一协调协议层（session-manager-design.md v2，Runtime/Transactions 收官件）。
/// <para>每组合域一个；读写检查点三 op 的唯一进出：写=staged 物化委托经单飞提交管线
/// （纯内存序+排空批合并，FIFO 全序）；检查点=管线内串行回合；读=会话 scope/地址直达（协议面另件）。</para>
/// <para>★ 零自有存储、零持久化决策（v2 决策）：持久化真源=参与者各自 meta 水位
/// （2PC 六件套本就持久），时机归参与者自身策略；悬挂裁决按域声明（默认 forward-commit 前推）。</para>
/// <para>线程安全（管线/计数/通道）；会话（TierSession）单线程使用契约。</para>
/// </summary>
public sealed class SessionManager : LifecycleBase
{
    private readonly ICommitCoordinator _coordinator;
    private readonly (string Name, ITransactionParticipant Participant)[] _participants;
    private readonly IFileSystem? _fileSystem;
    private readonly string? _name;
    private readonly ITransactionLog? _injectedTxn;
    // 域内 epoch 读保护持有者（参与者中 IEpochProtected 子集——SessionReadScope 聚合对象，构造期缓存）
    private readonly TC.Tier.Contracts.Transactions.IEpochProtected[] _readHolders;

    private Channel<object>? _channel;                   // Initialize 时建（管线回合通道）
    private Task _pipelineTask = Task.CompletedTask;
    private int _faulted;                               // 0=运行, 1=Faulted
    private Exception? _faultReason;

    private int _sessionCount;
    private int _openTxCount;

    /// <summary>Dispose 排水有界期限（管线在飞回合+排队排水的上限等待）。</summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(15);

    private SessionManager(ICommitCoordinator coordinator,
        (string, ITransactionParticipant)[] participants,
        IFileSystem? fileSystem, string? name, ITransactionLog? injectedTxn)
    {
        _coordinator = coordinator;
        _participants = participants;
        _fileSystem = fileSystem;
        _name = name;
        _injectedTxn = injectedTxn;
        _readHolders = participants
            .Where(p => p.Item2 is TC.Tier.Contracts.Transactions.IEpochProtected)
            .Select(p => (TC.Tier.Contracts.Transactions.IEpochProtected)p.Item2)
            .ToArray();
    }

    // ══════════ 工厂（域装配根：fs+名字+参与者即全部）══════════

    /// <summary>
    /// 默认档：域装配根——fs（与结构共享同一实例；Session 不持有存储，仅域身份关联）+
    /// 参与者全集。内芯=纯内存序协调器（Prepare-all/Confirm-all，零持久化决策）。
    /// </summary>
    /// <param name="fs">组合域文件系统（与参与者结构同源；Session 零自有存储——此参数为域装配身份）。</param>
    /// <param name="name">域名（诊断）。</param>
    /// <param name="resolution">悬挂裁决域声明（恢复时悬干前推 vs 丢尾；缺省 forward-commit）。</param>
    /// <param name="participants">参与者全集（名称+实例；同结构写必经同一域——§2 多域规则）。</param>
    public static SessionManager Create(IFileSystem fs, string? name = null,
        HangingResolution resolution = HangingResolution.ForwardCommit,
        params (string Name, ITransactionParticipant Participant)[] participants)
    {
        ArgumentNullException.ThrowIfNull(fs);
        return new SessionManager(new InProcessCoordinator(participants, resolution),
            participants, fs, name, injectedTxn: null);
    }

    /// <summary>
    /// 注入档：外部 <see cref="ITransactionLog"/> 作协调器（测试假件替换 / record 持久化语义域）。
    /// <para>参与者同时 Register 到注入 txn（同名覆盖语义）；恢复裁决=txn.LoadAndReconcile()。
    /// ★ 不支持 ReplicatedRound（seq 真源在 txn 内部无法分段预订）——复制域用默认档。</para>
    /// </summary>
    public static SessionManager Create(ITransactionLog txn,
        params (string Name, ITransactionParticipant Participant)[] participants)
    {
        ArgumentNullException.ThrowIfNull(txn);
        foreach (var (pname, p) in participants)
            txn.Register(pname, p);
        return new SessionManager(new TransactionLogCoordinator(txn),
            participants, fileSystem: null, name: null, injectedTxn: txn);
    }

    // ══════════ 生命周期（与结构层 Lifecycle 对齐）══════════

    /// <summary>
    /// 启动序收口：悬挂裁决（协调器 ReconcileStartup——域声明路径）+ 域起始水位 + 管线启动。
    /// <para>调用前参与者结构须已 Initialize+WaitForReady（悬干以"已恢复尾"形态在各自结构里）。</para>
    /// </summary>
    public void Initialize() => Initialize(default(EmptyHints));

    protected override void OnInitializeComplete()
    {
        _coordinator.ReconcileStartup();   // 悬挂裁决（forward-commit 前推 / 丢尾 / 注入 txn 裁决）
        StartPipeline();
    }

    private void StartPipeline()
    {
        _channel = Channel.CreateUnbounded<object>(new UnboundedChannelOptions
        {
            SingleReader = true,
            // ★ false：读方续体恒经调度器——管线循环永在管线自身执行流（单飞提交线程语义）。
            //   true 时 TryWrite 会在入队方线程内联跑整圈管线（2PC 寄生调用方线程；
            //   时序控制场景（Prepare 阻塞）= 调用方死等自己——SessionPipelineTests
            //   卡死现场 dotnet-stack 实锤）。
            AllowSynchronousContinuations = false,
        });
        _pipelineTask = Task.Factory.StartNew(static m => ((SessionManager)m!).PipelineLoop(),
                this, CancellationToken.None, TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default)
            .Unwrap();
    }

    // ══════════ 公开观测面 ══════════

    /// <summary>域名（诊断）。</summary>
    public string? Name => _name;

    /// <summary>域文件系统（与参与者结构共享同一实例；Session 零自有存储）。</summary>
    public IFileSystem? FileSystem => _fileSystem;

    /// <summary>参与者名称集合（诊断/恢复报告）。</summary>
    public IReadOnlyCollection<string> ParticipantNames => _participants.Select(p => p.Name).ToArray();

    /// <summary>当前全局已提交水位（协调器真源）。</summary>
    public long LastCommittedSeq => _coordinator.LastCommittedSeq;

    /// <summary>★ 开放事务会话计数（窗口契约 W 检查——存在开放事务期间该域结构档 A 直写 fail-fast）。</summary>
    public int OpenTxCount => Volatile.Read(ref _openTxCount);

    /// <summary>开放会话总数（诊断）。</summary>
    public int SessionCount => Volatile.Read(ref _sessionCount);

    /// <summary>管线是否 Faulted（物化失败/管线线程死亡——域报废重建，恢复=进程重启）。</summary>
    public bool IsFaulted => Volatile.Read(ref _faulted) == 1;

    /// <summary>管线 Fault 原因（仅 IsFaulted 时非空）。</summary>
    public Exception? FaultReason => _faultReason;

    // ══════════ 会话 ══════════

    /// <summary>
    /// 开会话（会话=运行期概念，无持久身份）。单线程会话契约（TierSession）。
    /// </summary>
    public TierSession OpenSession(string? name = null)
    {
        ThrowIfDisposed();
        if (IsFaulted)
            throw new InvalidOperationException($"Session 管线已 Faulted——域报废重建。原因：{_faultReason?.Message}");
        Interlocked.Increment(ref _sessionCount);
        return new TierSession(this, name);
    }

    /// <summary>
    /// ★ 检查点回合入队（管线串行——与事务回合天然全序；时机归协议，内容归组合层）。
    /// plan 收当前已提交水位 seq；回执=该水位。
    /// </summary>
    public async ValueTask<long> EnqueueCheckpoint(Action<long> plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ThrowIfFaultedOrClosed();
        var round = new CheckpointRound(plan);
        await _channel!.Writer.WriteAsync(round, CancellationToken.None).ConfigureAwait(false);
        try
        {
            return await round.Completion.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            round.TryCancel();
            throw;
        }
    }

    // ══════════ 会话回调（internal——TierSession 调）══════════

    internal void OnSessionTxOpened() => Interlocked.Increment(ref _openTxCount);

    internal void OnSessionTxClosed() => Interlocked.Decrement(ref _openTxCount);

    /// <summary>域读保护持有者快照（TierSession.EnterReadScope 聚合用——构造期缓存，恒定）。</summary>
    internal TC.Tier.Contracts.Transactions.IEpochProtected[] ReadProtectionHolders() => _readHolders;

    /// <summary>
    /// ★ 窗口契约 W 统一检查（session-manager-design.md §5.2）——产品面（TierKv/Queue/TS/Blob/Meta）
    /// 档 A 直写入口必调，防各面漏检各自为政：存在开放事务会话期间（<see cref="OpenTxCount"/> &gt; 0），
    /// 本域参与者结构的档 A 直写 fail-fast（与在途协调回合竞态；裸调结构公开面=专家模式自担）。
    /// <para>会话协调写（staged→管线）不经此检查（档 B 本身即协调路径）。</para>
    /// </summary>
    /// <param name="operation">调用方操作名（诊断定位用，自动捕获调用方成员名）。</param>
    public void EnsureNoOpenTransaction(
        [System.Runtime.CompilerServices.CallerMemberName] string? operation = null)
    {
        if (Volatile.Read(ref _openTxCount) > 0)
            throw new InvalidOperationException(
                $"窗口契约 W：域 '{_name ?? "(anonymous)"}' 存在开放事务会话（OpenTxCount={_openTxCount}）——" +
                $"参与者结构的档 A 直写被禁止（操作：{operation ?? "?"}）；等事务终态或改走会话协调写");
    }

    internal void OnSessionClosed(TierSession session) => Interlocked.Decrement(ref _sessionCount);

    internal void EnqueueRound(TxRound round)
    {
        ThrowIfFaultedOrClosed();
        _channel!.Writer.TryWrite(round);
    }

    private void ThrowIfFaultedOrClosed()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (_channel is null)
            throw new InvalidOperationException("SessionManager 未 Initialize——先 Initialize() 再使用");
        if (IsFaulted)
            throw new InvalidOperationException(
                $"Session 管线已 Faulted——域报废重建（排水已在故障时完成）。原因：{_faultReason?.Message}");
    }

    // ══════════ 提交管线（单飞线程；纯内存序+排空批合并）══════════

    private async Task PipelineLoop()
    {
        var reader = _channel!.Reader;
        try
        {
            while (true)
            {
                object item = await reader.ReadAsync().ConfigureAwait(false);
                if (item is TxRound tx)
                    await DispatchTxAsync(reader, tx).ConfigureAwait(false);
                else
                    await DispatchCheckpointAsync((CheckpointRound)item).ConfigureAwait(false);
            }
        }
        catch (ChannelClosedException)
        {
            // 正常关闭（Dispose 排水）或 FaultPipeline 关闭——余量排水见 finally
        }
        catch (Exception ex)
        {
            FaultPipeline(ex);   // 管线自身异常（回合处理之外的不可预期）——排水
        }
        finally
        {
            DrainRemaining();
        }
    }

    /// <summary>TxRound 分派：Replicated 独立回合；普通回合排空积压为一批（批合并）。</summary>
    private async Task DispatchTxAsync(ChannelReader<object> reader, TxRound first)
    {
        // 取走检查（排队撤销——出队丢弃：结构零触碰、seq 零消耗）
        if (!first.TryTake())
        {
            first.CompleteCancelled();
            return;
        }

        if (first.AwaitDecision != null)
        {
            await ProcessReplicatedAsync(first).ConfigureAwait(false);
            return;
        }

        // 批合并：排空连续的普通 TxRound（Replicated/Checkpoint 停止吸收，FIFO 全序不变）
        var batch = new List<TxRound> { first };
        object? stashed = null;
        while (reader.TryRead(out var next))
        {
            if (next is TxRound ntx && ntx.AwaitDecision == null)
            {
                if (ntx.TryTake()) batch.Add(ntx);
                else ntx.CompleteCancelled();
            }
            else
            {
                stashed = next;
                break;
            }
        }

        await ProcessTxBatchAsync(batch).ConfigureAwait(false);

        if (stashed != null && Volatile.Read(ref _faulted) == 0)
        {
            if (stashed is TxRound stx) await DispatchTxAsync(reader, stx).ConfigureAwait(false);
            else await DispatchCheckpointAsync((CheckpointRound)stashed).ConfigureAwait(false);
        }
    }

    /// <summary>TxRound 批：物化×N（入队序）→ 整批一次 Prepare-all+Confirm-all（coordinator）→ 逐个回执。</summary>
    private async Task ProcessTxBatchAsync(List<TxRound> batch)
    {
        await Task.CompletedTask;   // ★ CS1998 收口（async 签名保持——管线统一 await 面）
        // 物化（FIFO 序）——物化抛=管线 Faulted（悬干无法安全清除，续跑会洗白——域报废）
        foreach (var round in batch)
        {
            foreach (var (materialize, _) in round.Materializers)
            {
                try { materialize(); }
                catch (Exception ex)
                {
                    HandleMaterializeFailure(batch, round, ex);
                    return;
                }
            }
        }

        // 2PC（整批共享 seq）——Prepare 抛=Abort 已 Prepare 者（coordinator 内）→ 回执异常 → 管线续跑
        long seq;
        try
        {
            seq = _coordinator.CommitBatch();
        }
        catch (Exception ex)
        {
            foreach (var round in batch) round.CompleteFault(ex);
            return;
        }

        foreach (var round in batch) round.Complete(seq);
    }

    /// <summary>ReplicatedRound：物化 → Prepare-all → await 决策 → Confirm（不可回退）/Abort（回滚回执）。</summary>
    private async Task ProcessReplicatedAsync(TxRound round)
    {
        foreach (var (materialize, _) in round.Materializers)
        {
            try { materialize(); }
            catch (Exception ex)
            {
                HandleMaterializeFailure(new List<TxRound> { round }, round, ex);
                return;
            }
        }

        long seq;
        try
        {
            seq = _coordinator.PrepareCandidate();
        }
        catch (Exception ex)
        {
            round.CompleteFault(ex);   // coordinator 已 Abort 已 Prepare 者——续跑
            return;
        }

        bool decision;
        try
        {
            decision = await round.AwaitDecision!(seq, round.Ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _coordinator.AbortPrepared(seq);
            round.CompleteFault(new RollbackException(seq,
                $"复制决策异常，已回滚到上一提交边界（候选 seq={seq}）", ex));
            return;
        }

        if (!decision)
        {
            _coordinator.AbortPrepared(seq);
            round.CompleteFault(new RollbackException(seq,
                $"复制决策否决，已回滚到上一提交边界（候选 seq={seq}）"));
            return;
        }

        try
        {
            _coordinator.ConfirmCandidate(seq);
        }
        catch (Exception ex)
        {
            // ★不可回退点之后异常——参与者水位可能分裂，域不可续用
            HandlePipelineFatal(new InvalidOperationException(
                $"Confirm-all 在多数派决策后失败（seq={seq}，不可回退点）——域报废", ex), round);
            return;
        }
        round.Complete(seq);
    }

    /// <summary>CheckpointRound：执行 plan（当前已提交水位）——与事务回合天然串行。</summary>
    private async Task DispatchCheckpointAsync(CheckpointRound round)
    {
        if (!round.TryTake())
        {
            round.CompleteCancelled();
            return;
        }
        long watermark = _coordinator.LastCommittedSeq;
        await Task.CompletedTask;   // ★ CS1998 收口（async 签名保持——管线统一 await 面）
        try
        {
            round.Plan(watermark);
        }
        catch (Exception ex)
        {
            round.CompleteFault(ex);   // plan 抛=回执原异常，管线续跑（检查点无结构悬干）
            return;
        }
        round.Complete(watermark);
    }

    // ══════════ 故障排水 ══════════

    /// <summary>物化失败处理：失败回合回执原异常，同批其余回执批中止——管线 Faulted（防悬干洗白）。</summary>
    private void HandleMaterializeFailure(List<TxRound> batch, TxRound failed, Exception ex)
    {
        foreach (var round in batch)
        {
            round.CompleteFault(ReferenceEquals(round, failed)
                ? ex
                : new InvalidOperationException("同批回合物化失败，批中止（管线 Faulted）", ex));
        }
        FaultPipeline(ex);
    }

    private void HandlePipelineFatal(Exception fatal, params TxRound[] pending)
    {
        foreach (var round in pending) round.CompleteFault(fatal);
        FaultPipeline(fatal);
    }

    /// <summary>管线 Faulted：标记+关通道（主循环退出时排水全部未决回执）。恢复=进程重启/域重建。</summary>
    private void FaultPipeline(Exception reason)
    {
        if (Interlocked.Exchange(ref _faulted, 1) == 1) return;
        _faultReason = reason;
        _channel!.Writer.TryComplete(reason);   // Initialize 必先于管线启动（_channel 非空不变量）
    }

    /// <summary>排水通道余量（主循环退出路径：Faulted 关闭/Dispose 关闭）——未决回执 fault。</summary>
    private void DrainRemaining()
    {
        var fault = _faultReason;
        var drainEx = new InvalidOperationException(
            fault == null ? "Session 管线已关闭（Dispose）" : $"Session 管线已 Faulted：{fault.Message}", fault);
        while (_channel is { } ch && ch.Reader.TryRead(out var item))
        {
            try
            {
                if (item is TxRound tx) tx.CompleteFault(drainEx);
                else ((CheckpointRound)item).CompleteFault(drainEx);
            }
            catch { /* 排水不因单个回执失败中断 */ }
        }
    }

    // ══════════ Dispose（有界排水）══════════

    protected override async ValueTask DisposeOverrideAsync(bool disposing)
    {
        if (!disposing) return;
        if (_channel is { } ch && ch.Writer.TryComplete())
        {
            // 等管线排空已入队回合（有界——超时强制排水）
            try
            {
                await _pipelineTask.WaitAsync(DrainTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                FaultPipeline(new TimeoutException(
                    $"Session 管线 Dispose 排水超时（{DrainTimeout.TotalSeconds:0}s）——强制排水"));
                try { await _pipelineTask.ConfigureAwait(false); } catch { /* 已强制排水 */ }
            }
        }
        _injectedTxn?.Dispose();
    }

    protected override void DisposeOverride(bool disposing)
    {
        if (!disposing) return;
        if (_channel is { } ch && ch.Writer.TryComplete())
        {
#pragma warning disable TCSG031 // 设计必需：Dispose 排水必须同步完成（IDisposable 契约，有界超时）
            if (!_pipelineTask.Wait(DrainTimeout))
            {
                FaultPipeline(new TimeoutException(
                    $"Session 管线 Dispose 排水超时（{DrainTimeout.TotalSeconds:0}s）——强制排水"));
                try { _pipelineTask.Wait(DrainTimeout); } catch { /* 已强制排水 */ }
            }
#pragma warning restore TCSG031
        }
        _injectedTxn?.Dispose();
    }
}

// ══════════ 管线回合类型（internal——管线协议）══════════

/// <summary>
/// 事务回合（TxRound/ReplicatedRound 同体——AwaitDecision 区分）。
/// 回合状态原子机：0=排队 → 1=管线取走（不可打断）/ 2=排队撤销（出队丢弃）。
/// </summary>
internal sealed class TxRound
{
    private const int Queued = 0, Taken = 1, Cancelled = 2;

    private int _roundState;   // Queued/Taken/Cancelled

    public TierSession Session { get; }
    public (Action Materialize, object? Tag)[] Materializers { get; }
    public object? Context { get; }
    public Func<long, CancellationToken, ValueTask<bool>>? AwaitDecision { get; }
    public CancellationToken Ct { get; }
    public TaskCompletionSource<long> Completion { get; }
        = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TxRound(TierSession session, (Action, object?)[] materializers, object? context,
        Func<long, CancellationToken, ValueTask<bool>>? awaitDecision, CancellationToken ct = default)
    {
        Session = session;
        Materializers = materializers;
        Context = context;
        AwaitDecision = awaitDecision;
        Ct = ct;
    }

    /// <summary>管线取走（排队撤销竞争败者=false=丢弃）。</summary>
    public bool TryTake() => Interlocked.CompareExchange(ref _roundState, Taken, Queued) == Queued;

    /// <summary>排队撤销标记（仅排队中生效；已取走=false——在途不可打断）。</summary>
    public bool TryCancel() => Interlocked.CompareExchange(ref _roundState, Cancelled, Queued) == Queued;

    /// <summary>登记到会话在途回合位（快照入队时——Abort 二分判定用）。</summary>
    public void SetPendingOn(TierSession session) => session.SetPending(this);

    /// <summary>Abort 视角：排队→标记撤销（管线稍后回执取消）；在途→等终态（不可打断）。</summary>
    public void ResolvePendingForAbort()
    {
        if (!TryCancel())
        {
            // 已被管线取走——回合中不可打断，等终态（异常吞：Abort 只关心"已终态"）
#pragma warning disable TCSG031 // 设计必需：Abort 同步 API 契约——等回合终态
            try { Completion.Task.GetAwaiter().GetResult(); }
#pragma warning restore TCSG031
            catch { /* 终态即返回 */ }
        }
    }

    public void Complete(long seq)
    {
        Completion.TrySetResult(seq);
        Session.ClearPending(this);
    }

    public void CompleteFault(Exception ex)
    {
        Completion.TrySetException(ex);
        Session.ClearPending(this);
    }

    public void CompleteCancelled()
    {
        Completion.TrySetCanceled();
        Session.ClearPending(this);
    }
}

/// <summary>检查点回合（plan 委托 + 回执水位；同 TxRound 状态机）。</summary>
internal sealed class CheckpointRound
{
    private const int Queued = 0, Taken = 1, Cancelled = 2;
    private int _roundState;

    public Action<long> Plan { get; }
    public TaskCompletionSource<long> Completion { get; }
        = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CheckpointRound(Action<long> plan) => Plan = plan;

    public bool TryTake() => Interlocked.CompareExchange(ref _roundState, Taken, Queued) == Queued;
    public bool TryCancel() => Interlocked.CompareExchange(ref _roundState, Cancelled, Queued) == Queued;

    public void Complete(long seq) => Completion.TrySetResult(seq);
    public void CompleteFault(Exception ex) => Completion.TrySetException(ex);
    public void CompleteCancelled() => Completion.TrySetCanceled();
}
