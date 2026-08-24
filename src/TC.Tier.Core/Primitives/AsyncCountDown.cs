namespace TC.Tier.Core.Primitives;

/// <summary>
/// 异步倒计时器：当计数器从非零降到零时，唤醒所有等待者。
/// <para>底层基于 <see cref="AsyncManualResetEvent"/>（<see cref="ManualResetValueTaskSourceCore{TResult}"/>），
/// 常规等待路径零堆分配（传统 <see cref="TaskCompletionSource{TResult}"/> 方案每次等待有一次堆分配，
/// 且 <c>nextTcs</c> 赋值非原子存在竞态；本实现消除分配并修复竞态）。</para>
/// <para>语义：初始/计数归零时事件 set（等待立即返回）；<see cref="Add"/> 使计数 &gt; 0，
/// 事件 reset（等待阻塞）。</para>
/// </summary>
public sealed class AsyncCountDown
{
    private int _counter;
    private readonly AsyncManualResetEvent _event;

    /// <summary>创建倒计时器（唤醒默认线程池异步调度——安全）。</summary>
    public AsyncCountDown() => _event = new AsyncManualResetEvent(initialState: true);

    /// <summary>创建倒计时器并指定唤醒调度模式（false = Remove 内联唤醒等待者，仅限调用点不持锁场景）。</summary>
    public AsyncCountDown(bool runContinuationsAsynchronously)
        => _event = new AsyncManualResetEvent(initialState: true, runContinuationsAsynchronously);

    /// <summary>计数加 1。计数从 0 → 1 时事件 reset。</summary>
    public void Add()
    {
        // 先 Add 再判断：若之前是 0（已 set），Add 后变为 >0，需 reset
        if (Interlocked.Increment(ref _counter) == 1)
            _event.Reset();
    }

    /// <summary>计数减 1。计数降到 0 时事件 set，唤醒所有等待者。</summary>
    public void Remove()
    {
        // 先 Remove 再判断：若降到 0，set 唤醒
        if (Interlocked.Decrement(ref _counter) == 0)
            _event.Set();
    }

    /// <summary>计数器是否为零。</summary>
    public bool IsEmpty => Volatile.Read(ref _counter) == 0;

    /// <summary>
    /// 当计数为 0 时同步返回（可复用等待：计数再次 &gt;0 后会重新阻塞）。
    /// </summary>
    public ValueTask WaitUntilEmptyAsync(CancellationToken cancellationToken = default)
        => _event.WaitAsync(cancellationToken);
}
