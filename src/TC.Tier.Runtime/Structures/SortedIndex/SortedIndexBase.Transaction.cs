namespace TC.Tier.Runtime.Structures.SortedIndex;

public abstract partial class SortedIndexBase<TKey> where TKey : unmanaged, IEquatable<TKey>
{
    private long _lastCommittedSeq = -1;
    private long _lastPreparedSeq = -1;

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
    {
        long current;
        do
        {
            current = Volatile.Read(ref _lastCommittedSeq);
            if (seq <= current) return;
        } while (Interlocked.CompareExchange(ref _lastCommittedSeq, seq, current) != current);
    }

    void ITransactionParticipant.OnCommitted(long seq, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (seq <= Volatile.Read(ref _lastCommittedSeq))
        {
            callback();
            return;
        }
    }

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
