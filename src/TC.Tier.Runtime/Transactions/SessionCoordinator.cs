namespace TC.Tier.Runtime.Transactions;

/// <summary>
/// ★ Session 域提交协调器（internal）——管线回合对参与者的 2PC 驱动面。
/// <para>两实现（session-manager-design.md §3.1 两 Create 档的内芯）：</para>
/// <para>- <see cref="InProcessCoordinator"/>（默认档）：纯内存序——seq 单调分配 + Prepare-all +
///   Confirm-all，<b>零持久化决策</b>（v2 裁定：持久化真源=参与者各自 meta 水位）；</para>
/// <para>- <see cref="TransactionLogCoordinator"/>（注入档）：包装外部 <see cref="ITransactionLog"/>
///   （测试假件替换协调器 / 需要 record 持久化语义的域）。</para>
/// </summary>
internal interface ICommitCoordinator
{
    /// <summary>当前已提交水位（恢复裁决后的起始值；运行期随 Confirm 推进）。</summary>
    long LastCommittedSeq { get; }

    /// <summary>
    /// ★ TxRound 批提交：分配新 seq → Prepare-all(新 seq) → Confirm-all(新 seq)。
    /// <para>Prepare 阶段失败：自动 Abort 已 Prepare 者（吞次级异常）后重抛原异常（管线回执+续跑）。</para>
    /// </summary>
    /// <returns>本批共享的域 seq。</returns>
    long CommitBatch();

    /// <summary>
    /// ★ ReplicatedRound 分段驱动①：分配候选 seq 并 Prepare-all（fsync-before-replicate——
    /// 参与者 Prepare 落盘语义由结构给出）。失败时自动 Abort 已 Prepare 者后重抛。
    /// </summary>
    /// <returns>候选 seq（决策 true 则随之 Confirm）。</returns>
    long PrepareCandidate();

    /// <summary>ReplicatedRound 分段驱动②：多数派决策 true → Confirm-all（★不可回退点）。</summary>
    void ConfirmCandidate(long seq);

    /// <summary>ReplicatedRound 回滚：Abort 已 Prepare 者（吞次级异常）。</summary>
    void AbortPrepared(long seq);

    /// <summary>
    /// ★ 启动恢复裁决（SessionManager.Initialize 内调用一次）：
    /// 悬干按域声明裁决（默认档 forward-commit 前推 / 显式丢尾；注入档=txn.LoadAndReconcile），
    /// 返回裁决后的起始水位（域 seq 从此继续，严格大于一切已用 seq）。
    /// </summary>
    long ReconcileStartup();
}

/// <summary>悬挂裁决域声明（仅默认档——注入档裁决归注入的协调器）。</summary>
public enum HangingResolution
{
    /// <summary>
    /// 缺省：恢复时把悬干推到各自的 prepared seq（跨参与者一致——同批共享批 seq，各自 Prepare 已知）。
    /// <para>适合 WAL/队列/时序/帧（数据宁可前推不可丢）。零中央决策件。</para>
    /// </summary>
    ForwardCommit,

    /// <summary>
    /// 水位一致档：悬干截断丢弃（退回各自已确认水位）——域要求强确认（确认即持久，
    /// 参与者策略配 Prepare 即落盘）时使用。
    /// </summary>
    DropTail,
}

/// <summary>
/// 默认协调器（SessionManager.Create(fs, …) 档内芯）——纯内存序 2PC 驱动，零持久化决策。
/// </summary>
internal sealed class InProcessCoordinator : ICommitCoordinator
{
    private readonly (string Name, ITransactionParticipant Participant)[] _participants;
    private readonly HangingResolution _resolution;
    private long _seq;

    public long LastCommittedSeq => Volatile.Read(ref _seq);

    public InProcessCoordinator((string, ITransactionParticipant)[] participants, HangingResolution resolution)
    {
        _participants = participants;
        _resolution = resolution;
    }

    public long CommitBatch()
    {
        long seq = Interlocked.Increment(ref _seq);
        var prepared = new List<ITransactionParticipant>();
        try
        {
            foreach (var (_, p) in _participants)
            {
                p.Prepare(seq);
                prepared.Add(p);
            }
        }
        catch
        {
            AbortSilently(prepared, seq);
            throw;
        }
        foreach (var (_, p) in _participants)
            p.ConfirmCommitted(seq);
        return seq;
    }

    public long PrepareCandidate()
    {
        long seq = Interlocked.Increment(ref _seq);
        var prepared = new List<ITransactionParticipant>();
        try
        {
            foreach (var (_, p) in _participants)
            {
                p.Prepare(seq);
                prepared.Add(p);
            }
        }
        catch
        {
            AbortSilently(prepared, seq);
            throw;
        }
        return seq;
    }

    public void ConfirmCandidate(long seq)
    {
        foreach (var (_, p) in _participants)
            p.ConfirmCommitted(seq);
    }

    public void AbortPrepared(long seq)
    {
        // PrepareCandidate 全员已 Prepare（成功返回才有决策阶段）——全量 Abort
        foreach (var (_, p) in _participants)
        {
            try { p.Abort(seq); }
            catch { /* 吞次级异常（§6：协调器自动 Abort 吞次级） */ }
        }
    }

    public long ReconcileStartup()
    {
        foreach (var (_, p) in _participants)
        {
            if (p.LastPreparedSeq <= p.LastCommittedSeq) continue;   // 无悬干
            if (_resolution == HangingResolution.ForwardCommit)
                p.ConfirmCommitted(p.LastPreparedSeq);   // 前推：悬干推到 prepared seq
            else
                p.Abort(p.LastPreparedSeq);              // 丢尾：截断回已确认水位
        }

        // 起始水位=裁决后的参与者已提交 max（前推会抬升、丢尾会回落——一律以裁决终态为准；
        // 参与者 -1（未参与事务）折算 0——新域首次 CommitBatch 从 seq=1 起）
        long watermark = 0;
        foreach (var (_, p) in _participants)
            watermark = Math.Max(watermark, p.LastCommittedSeq);

        Volatile.Write(ref _seq, watermark);
        return watermark;
    }

    private static void AbortSilently(List<ITransactionParticipant> prepared, long seq)
    {
        foreach (var p in prepared)
        {
            try { p.Abort(seq); }
            catch { /* 吞次级异常，继续 Abort 其余 */ }
        }
    }
}

/// <summary>
/// 注入协调器（SessionManager.Create(txn, …) 档内芯）——包装外部 ITransactionLog。
/// <para>注入的 txn 须已（或经 Create 转交）Register 全部参与者；恢复裁决=txn.LoadAndReconcile()
/// （record/假件语义）。★ 不支持 ReplicatedRound（seq 真源在 txn 内部，无法分段预订）——
/// 该档域用 TxRound/Checkpoint（注入档主要为测试假件与强确认域）。</para>
/// </summary>
internal sealed class TransactionLogCoordinator : ICommitCoordinator
{
    private readonly ITransactionLog _txn;

    public TransactionLogCoordinator(ITransactionLog txn) => _txn = txn;

    public long LastCommittedSeq => _txn.LastCommittedSeq;

    public long CommitBatch() => _txn.Commit();

    public long PrepareCandidate()
        => throw new NotSupportedException(
            "注入档（ITransactionLog 协调器）不支持 ReplicatedRound——seq 真源在注入协调器内部，无法分段预订；" +
            "复制回合域请用默认档 SessionManager.Create(fs, …)。");

    public void ConfirmCandidate(long seq)
        => throw new NotSupportedException("注入档不支持 ReplicatedRound（见 PrepareCandidate）。");

    public void AbortPrepared(long seq) => _txn.Abort();

    public long ReconcileStartup() => _txn.LoadAndReconcile();
}
