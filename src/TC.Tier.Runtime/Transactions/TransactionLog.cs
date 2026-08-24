using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TC.Tier.Runtime.Transactions;

/// <summary>
/// 默认事务日志实现——独立 commit record 文件 + DirectIO + 4K 对齐 + Magic/Crc + Flush 落盘 + 链式触发。
/// <para>★ 设计（见 transaction-design.md §5）：</para>
/// <para>- 物理：独立文件 + DisableFileBuffering(DirectIO) + 4K 对齐固定覆盖写</para>
/// <para>- Commit：seq+1 → 算 Crc → DirectIO 写 + Flush（这一次 Write+Flush = 原子点）→ 链式触发参与者</para>
/// <para>- Load：读固定块 → 校验 Magic/Crc → 返回 seq（空/损坏 = 0）</para>
/// <para>开箱即用，上层 new 即可；或注入自定义 ITransactionLog 替换。</para>
/// </summary>
public sealed class TransactionLog : ITransactionLog
{
    private const int DioAlignment = 4096;
    private const ulong MagicValue = 0x54534143494F4E58;   // ASCII "XNOIAST" 反向 "XNOIASTX"

    private readonly IStorageEngine _engine;
    private readonly AlignedMemoryManager _buffer;
    // ★ 保留插入顺序的命名参与者表（诊断/按名称查找/恢复报告）。键=名称，值=参与者。
    private readonly Dictionary<string, ITransactionParticipant> _participants = new();
    private long _lastCommittedSeq;
    private int _disposed;

    /// <summary>CommitRecord 物理布局（24B，4K 对齐 padding 到 DioAlignment）。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct CommitRecord
    {
        public ulong Magic;
        public long Seq;
        public uint Crc;
    }

    /// <param name="engine">注入的 commit record 引擎（外部管生命周期）。</param>
    public TransactionLog(IStorageEngine engine)
    {
        _engine = engine;
        var blockSize = Unsafe.SizeOf<CommitRecord>().AlignUp(DioAlignment);
        _buffer = new AlignedMemoryManager(blockSize, DioAlignment);
    }

    public long LastCommittedSeq => _lastCommittedSeq;

    public IReadOnlyCollection<string> ParticipantNames => _participants.Keys;

    public void Register(string name, ITransactionParticipant participant)
    {
        ArgumentNullException.ThrowIfNull(participant);
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("参与者名称不能为空", nameof(name));
        // 同名注册：覆盖（子系统重建场景），保留末次插入顺序
        _participants[name] = participant;
    }

    public bool Unregister(string name) => _participants.Remove(name);

    /// <summary>★ 协调者提交事件：每次 Commit 成功（全局 seq 推进 + Flush 落盘）后触发，传新 seq。</summary>
    public event Action<long>? OnCommitted;

    // === Load ===

    public long Load()
    {
        try
        {
            int got = _engine.Read(LogicalAddress.Empty, _buffer.GetSpan());
            if (got <= 0) { _lastCommittedSeq = 0; return 0; }
            return TryLoad();
        }
        catch { _lastCommittedSeq = 0; return 0; }
    }

    public async ValueTask<long> LoadAsync(CancellationToken ct)
    {
        try
        {
            int got = await _engine.ReadAsync(LogicalAddress.Empty, _buffer.Memory, ct).ConfigureAwait(false);
            if (got <= 0) { _lastCommittedSeq = 0; return 0; }
            return TryLoad();
        }
        catch { _lastCommittedSeq = 0; return 0; }
    }

    // === LoadAndReconcile（恢复协调） ===

    /// <summary>
    /// ★ 恢复协调：Load commit record + 推进所有已注册参与者到已提交 seq。
    /// <para>调用方须在调用前 Register 所有参与者（与 Commit 时相同的集合）。</para>
    /// <para>恢复判定：</para>
    /// <para>- committedSeq == 0（空盘/损坏）→ 所有有悬干的参与者 Abort</para>
    /// <para>- LastCommittedSeq < committedSeq → ConfirmCommitted(committedSeq)（正向：未同步推进）</para>
    /// <para>- LastPreparedSeq > committedSeq → Abort(LastPreparedSeq)（反向：超前悬干丢弃）</para>
    /// </summary>
    public long LoadAndReconcile()
    {
        long committedSeq = Load();
        if (committedSeq == 0)
        {
            // 空盘/损坏：所有有悬干数据的参与者 Abort
            foreach (var p in _participants.Values)
                if (p.LastPreparedSeq > p.LastCommittedSeq)
                    p.Abort(p.LastPreparedSeq);
            return 0;
        }
        foreach (var p in _participants.Values)
        {
            if (p.LastCommittedSeq < committedSeq)
                p.ConfirmCommitted(committedSeq);        // 正向：未同步 → 推进
            else if (p.LastPreparedSeq > committedSeq)
                p.Abort(p.LastPreparedSeq);              // ★ 反向：超前悬干 → 丢弃
        }
        _lastCommittedSeq = committedSeq;
        return committedSeq;
    }

    public async ValueTask<long> LoadAndReconcileAsync(CancellationToken ct)
    {
        long committedSeq = await LoadAsync(ct).ConfigureAwait(false);
        if (committedSeq == 0)
        {
            foreach (var p in _participants.Values)
                if (p.LastPreparedSeq > p.LastCommittedSeq)
                    p.Abort(p.LastPreparedSeq);
            return 0;
        }
        foreach (var p in _participants.Values)
        {
            if (p.LastCommittedSeq < committedSeq)
                p.ConfirmCommitted(committedSeq);
            else if (p.LastPreparedSeq > committedSeq)
                p.Abort(p.LastPreparedSeq);
        }
        _lastCommittedSeq = committedSeq;
        return committedSeq;
    }

    private long TryLoad()
    {
        ref var rec = ref GetRecordRef();
        if (rec.Magic == 0 && rec.Seq == 0) { _lastCommittedSeq = 0; return 0; }   // 空文件
        if (!ValidateCrc(ref rec)) { _lastCommittedSeq = 0; return 0; }             // 损坏
        _lastCommittedSeq = rec.Seq;
        return _lastCommittedSeq;
    }

    // === Abort（显式回滚当前轮次）===

    /// <summary>★ Abort：对所有 LastPreparedSeq > LastCommittedSeq 的参与者调 Abort（丢弃悬干）。</summary>
    public void Abort()
    {
        foreach (var p in _participants.Values)
            if (p.LastPreparedSeq > p.LastCommittedSeq)
                p.Abort(p.LastPreparedSeq);
    }

    // === Commit（真正两阶段：foreach Prepare → persist commit record → foreach ConfirmCommitted）===

    public long Commit()
    {
        long newSeq = _lastCommittedSeq + 1;
        var prepared = new List<ITransactionParticipant>();
        // Phase 1: foreach Prepare（任一失败 → catch Abort 已 prepare 的，抛原异常）
        try
        {
            foreach (var p in _participants.Values)
            {
                p.Prepare(newSeq);
                prepared.Add(p);
            }
            // Phase 1b: 全部 Prepare 成功 → 持久化 commit record（原子点）
            PopulateRecord(newSeq);
            _engine.Write(LogicalAddress.Empty, _buffer.GetSpan());
            _engine.Flush();   // ★ 这一次 Write+Flush = 整组事务提交的原子点
            _lastCommittedSeq = newSeq;
        }
        catch
        {
            // Phase 1 失败：Abort 所有已 Prepare 的（吞掉 Abort 异常，继续 Abort 其余）
            foreach (var p in prepared)
            {
                try { p.Abort(newSeq); } catch { /* 吞掉，继续 Abort 其余 */ }
            }
            throw;
        }
        // Phase 2: foreach ConfirmCommitted
        TriggerParticipants(newSeq);
        return newSeq;
    }

    public async ValueTask<long> CommitAsync(CancellationToken ct)
    {
        long newSeq = _lastCommittedSeq + 1;
        var prepared = new List<ITransactionParticipant>();
        try
        {
            foreach (var p in _participants.Values)
            {
                await p.PrepareAsync(newSeq, ct).ConfigureAwait(false);
                prepared.Add(p);
            }
            PopulateRecord(newSeq);
            await _engine.WriteAsync(LogicalAddress.Empty, _buffer.Memory, ct).ConfigureAwait(false);
            _engine.Flush();
            _lastCommittedSeq = newSeq;
        }
        catch
        {
            foreach (var p in prepared)
            {
                try { p.Abort(newSeq); } catch { }
            }
            throw;
        }
        TriggerParticipants(newSeq);
        return newSeq;
    }

    private void PopulateRecord(long seq)
    {
        ref var rec = ref GetRecordRef();
        rec.Magic = MagicValue;
        rec.Seq = seq;
        ComputeCrc(ref rec);
    }

    /// <summary>★ 链式触发：所有参与者 ConfirmCommitted（各自推进 seq + 触发参与者的 OnCommitted 回调）+ 协调者自身 OnCommitted 事件。</summary>
    private void TriggerParticipants(long committedSeq)
    {
        // 1. 各参与者推进 seq（参与者的 OnCommitted 回调在各自 ConfirmCommitted 内触发）
        foreach (var p in _participants.Values)
            p.ConfirmCommitted(committedSeq);
        // 2. 协调者自身的提交事件（外部观察全局提交）
        OnCommitted?.Invoke(committedSeq);
    }

    // === Crc ===

    private ref CommitRecord GetRecordRef()
        => ref Unsafe.As<byte, CommitRecord>(ref MemoryMarshal.GetReference(_buffer.GetSpan()));

    private static void ComputeCrc(ref CommitRecord rec)
    {
        rec.Crc = 0;
        unsafe
        {
            fixed (CommitRecord* p = &rec)
                rec.Crc = Crc32.HashToUInt32(new ReadOnlySpan<byte>(p, sizeof(CommitRecord)));
        }
    }

    private static bool ValidateCrc(ref CommitRecord rec)
    {
        if (rec.Magic != MagicValue) return false;
        uint stored = rec.Crc;
        ComputeCrc(ref rec);
        return stored == rec.Crc;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _buffer.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        _buffer.Dispose();
        return ValueTask.CompletedTask;
    }
}
