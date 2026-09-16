namespace TC.Tier.Runtime.Structures.SortedIndex;

/// <summary>
/// SortedIndexBase 事务 partial——ITransactionParticipant 显式接口实现（Prepare/ConfirmCommitted/Abort 记账与回调注册）。
/// </summary>
public abstract partial class SortedIndexBase<TKey> where TKey : unmanaged, IEquatable<TKey>
{
    private long _lastCommittedSeq = -1;
    private long _lastPreparedSeq = -1;
    private readonly CommitCallbackRegistry _commitCallbacks = new();

    long ITransactionParticipant.LastCommittedSeq => Volatile.Read(ref _lastCommittedSeq);
    long ITransactionParticipant.LastPreparedSeq => Volatile.Read(ref _lastPreparedSeq);

    void ITransactionParticipant.Prepare(long seq)
    {
        Volatile.Write(ref _lastPreparedSeq, seq);
    }

    async ValueTask ITransactionParticipant.PrepareAsync(long seq, CancellationToken ct)
    {
        Volatile.Write(ref _lastPreparedSeq, seq);
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    void ITransactionParticipant.ConfirmCommitted(long seq)
        => _commitCallbacks.AdvanceAndFire(seq, ref _lastCommittedSeq);

    void ITransactionParticipant.OnCommitted(long seq, Action callback)
        => _commitCallbacks.Register(seq, ref _lastCommittedSeq, callback);

    void ITransactionParticipant.Abort(long seq)
    {
        Volatile.Write(ref _lastPreparedSeq, Volatile.Read(ref _lastCommittedSeq));
    }

    async ValueTask ITransactionParticipant.AbortAsync(long seq, CancellationToken ct)
    {
        Volatile.Write(ref _lastPreparedSeq, Volatile.Read(ref _lastCommittedSeq));
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }
}
