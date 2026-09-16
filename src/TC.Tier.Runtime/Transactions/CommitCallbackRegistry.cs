namespace TC.Tier.Runtime.Transactions;

internal sealed class CommitCallbackRegistry
{
    private readonly SortedList<long, List<Action>> _callbacks = new();
    private readonly object _gate = new();

    /// <summary>注册提交回调——seq 已提交（≤ lastCommittedSeq）时立即同步执行，否则挂到该 seq 待提交后触发。</summary>
    /// <param name="seq">回调关联的提交序号（单调递增）。</param>
    /// <param name="lastCommittedSeq">调用方持有的已提交序号（按引用读取，须跨线程可见）。</param>
    /// <param name="callback">提交完成时执行的回调，非空。</param>
    public void Register(long seq, ref long lastCommittedSeq, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        lock (_gate)
        {
            if (seq > Volatile.Read(ref lastCommittedSeq))
            {
                if (!_callbacks.TryGetValue(seq, out var callbacks))
                {
                    callbacks = new List<Action>();
                    _callbacks[seq] = callbacks;
                }
                callbacks.Add(callback);
                return;
            }
        }

        callback();
    }

    /// <summary>推进已提交序号并触发所有 ≤ seq 的挂起回调（CAS 单调推进；回调异常聚合为 <see cref="AggregateException"/> 重抛）。</summary>
    /// <param name="seq">新的已提交序号（≤ 当前值时为 no-op）。</param>
    /// <param name="lastCommittedSeq">调用方持有的已提交序号（CAS 单调推进）。</param>
    public void AdvanceAndFire(long seq, ref long lastCommittedSeq)
    {
        long current;
        do
        {
            current = Volatile.Read(ref lastCommittedSeq);
            if (seq <= current) return;
        } while (Interlocked.CompareExchange(ref lastCommittedSeq, seq, current) != current);

        List<Action>? toFire = null;
        lock (_gate)
        {
            while (_callbacks.Count > 0 && _callbacks.Keys[0] <= seq)
            {
                toFire ??= new List<Action>();
                toFire.AddRange(_callbacks.Values[0]);
                _callbacks.RemoveAt(0);
            }
        }

        if (toFire is null) return;

        List<Exception>? errors = null;
        foreach (var callback in toFire)
        {
            try
            {
                callback();
            }
            catch (Exception ex)
            {
                (errors ??= []).Add(ex);
            }
        }

        if (errors is not null)
            throw new AggregateException("One or more commit callbacks failed.", errors);
    }
}
