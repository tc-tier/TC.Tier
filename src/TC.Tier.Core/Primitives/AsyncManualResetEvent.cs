using System.Runtime.CompilerServices;

namespace TC.Tier.Core.Primitives;

/// <summary>
/// 基于 <see cref="PooledValueTaskSource"/> 的可复用异步手动重置事件。
/// <para>语义对标 <see cref="System.Threading.ManualResetEventSlim"/>，但暴露 <see cref="ValueTask"/>
/// 异步等待入口，常规等待路径零堆分配。</para>
/// <para><b>多 waiter 广播</b>：<see cref="Set"/> 唤醒当前所有等待者；事件保持 set 状态，
/// 后续 <see cref="WaitAsync"/> 立即返回，直到显式 <see cref="Reset"/>。</para>
/// </summary>
/// <remarks>
/// <b>实现要点</b>：<see cref="System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore{TResult}"/> 是单消费者模型，
/// 不能被多个 waiter 共享（多 waiter 同 version 挂起时，先完成的 waiter 调 GetResult 会使后续
/// waiter 的 OnCompleted 抛 InvalidOperationException）。故本实现为<b>每个等待者分配独立的
/// <see cref="PooledValueTaskSource"/></b>（双层池化）。
///
/// <para><b>1:1 快路径（#PERF-002）</b>：首个 waiter CAS 抢占 <c>_singleWaiter</c> 单槽——高频
/// 1:1 唤醒免 <c>WaitNode</c> 分配、免锁；单槽被占时溢出回退 waiter 链表（多 waiter 广播语义不变）。
/// Set 侧先 volatile 写 isSet 再 Exchange 单槽：waiter 抢占槽后的 double-check 读到 true 即安全返回，
/// 无丢信号窗口。</para>
///
/// <para><b>唤醒语义与零分配（#PERF-002）</b>：<see cref="WaitAsync"/> 是非 async 方法（无状态机装箱），
/// source 归还挂在 GetResult 的清理钩子（<c>OnCleanup</c>）上——真实唤醒路径零分配。唤醒调度模式可配：
/// 默认线程池异步（安全）；<c>runContinuationsAsynchronously: false</c> 时 <see cref="Set"/> 在锁外
/// 内联完成等待者续体（对齐 SemaphoreSlim.Release，省一次线程池往返）——⚠️ 仅限 Set 调用点不持锁的场景
/// （持 SpinLock 等不可重入锁时内联续体会自死锁，如 SkipListPriorityQueue 的节点锁，故默认异步）。</para>
///
/// <para><b>source 归还策略</b>：每个 waiter 的 <see cref="PooledValueTaskSource"/> 在
/// <see cref="Set"/> 唤醒后由本类自动归还到池（SetResult 后 core 进入完成态，
/// 归还时的 Reset 只推进 version，不影响已发出的 ValueTask 的 awaiter 读取结果——
/// 因为 awaiter 已通过 OnCompleted 捕获了 continuation，SetResult 已触发其恢复）。
/// 取消的 waiter 的 source 由 <see cref="PooledValueTaskSource.AttachCancellation"/> 的
/// 回调完成，同样会被 Set 时的链表遍历归还（若取消发生在 Set 之前，source 已从链表摘除并归还）。</para>
/// </remarks>
public sealed class AsyncManualResetEvent
{
    // waiter 链表节点：每个等待者一个独立的池化完成源
    private WaitNode? _head;
    // ★ 单 waiter 快路径槽（）：CAS 抢占单槽，1:1 唤醒免 WaitNode 分配与锁；
    //   被占用时溢出回退链表。Set 侧 Exchange 取走。
    private PooledValueTaskSource? _singleWaiter;
    private int _syncWaiters;   // 定时同步等待者计数——Set 仅在 >0 时 Pulse（异步场景免白付 PulseAll）
    private bool _isSet;
    private readonly bool _runContinuationsAsynchronously;
    private readonly object _lock = new();

    private sealed class WaitNode
    {
        public PooledValueTaskSource Source = null!;
        public WaitNode? Next;
        public AsyncManualResetEvent? Owner;   // 清理钩子 state（链表路径）
    }

    // ★ 清理钩子（static 零分配）：GetResult 完成态时清槽/摘链表 + 归还 source
    private static readonly Action<object?, PooledValueTaskSource> s_slotCleanup =
        static (state, source) =>
        {
            var ev = (AsyncManualResetEvent)state!;
            Interlocked.CompareExchange(ref ev._singleWaiter, null, source);
            PooledValueTaskSource.Return(source);
        };
    private static readonly Action<object?, PooledValueTaskSource> s_nodeCleanup =
        static (state, source) =>
        {
            var node = (WaitNode)state!;
            node.Owner!.RemoveNode(node);
            PooledValueTaskSource.Return(source);
        };

    /// <summary>创建初始状态为 unset 的事件（唤醒默认线程池异步调度——安全）。</summary>
    public AsyncManualResetEvent() => _runContinuationsAsynchronously = true;

    /// <summary>创建并指定初始状态（唤醒默认线程池异步调度——安全）。</summary>
    /// <param name="initialState">true = 已 set（首个 WaitAsync 立即返回）。</param>
    public AsyncManualResetEvent(bool initialState)
    {
        _isSet = initialState;
        _runContinuationsAsynchronously = true;
    }

    /// <summary>创建并指定初始状态与唤醒调度模式。</summary>
    /// <param name="initialState">true = 已 set（首个 WaitAsync 立即返回）。</param>
    /// <param name="runContinuationsAsynchronously">
    /// false = Set 内联执行等待者续体（对齐 SemaphoreSlim.Release，真实唤醒 ~130ns 省线程池往返）；
    /// true = 线程池异步调度（默认，安全——⚠️ 若 Set 在持锁（如 SpinLock 节点锁）内调用，
    /// 内联续体会在同线程重入同锁自死锁，故默认异步）。</param>
    public AsyncManualResetEvent(bool initialState, bool runContinuationsAsynchronously)
    {
        _isSet = initialState;
        _runContinuationsAsynchronously = runContinuationsAsynchronously;
    }

    /// <summary>当前是否处于 set 状态。</summary>
    public bool IsSet => Volatile.Read(ref _isSet);

    /// <summary>
    /// 异步等待事件被 set。若已 set 则同步完成（零分配）。
    /// <para>未 set 时等待者入队；被 <see cref="Set"/> 唤醒或被取消时出队。</para>
    /// <para>★ 两种唤醒模式 + 完成先于注册安全协议（#PERF-002）：
    /// <see cref="Set"/> 一律经 <see cref="PooledValueTaskSource.MarkOrComplete"/> 完成 source——
    /// continuation 未注册时仅标记，注册（OnCompleted 转发处）即补完；从结构上消除
    /// ManualResetValueTaskSourceCore 的 CompletionSentinel 崩溃类（裸 SetResult 完成未注册 source 必抛）。
    /// <list type="bullet">
    /// <item><b>默认（线程池异步调度，Set 可持锁）</b>：async 方法 + 锁内入队（V1 同形）——真实唤醒
    /// 付一次状态机装箱 + 线程池往返（~800ns）。</item>
    /// <item><b>内联模式（runContinuationsAsynchronously: false）</b>：非 async 实现 + 单槽快路径 +
    /// GetResult 清理钩子（零装箱、Set 调用者线程内联续体、真实唤醒 ~80ns）。
    /// ⚠️ 仅限 Set 调用点不持锁场景（持 SpinLock 等不可重入锁时内联续体会自死锁）。</item>
    /// </list></para>
    /// </summary>
    public ValueTask WaitAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // ★ 快路径直接返回（不进 async 状态机——已 set 高频场景零装箱零开销）
        if (Volatile.Read(ref _isSet))
            return default;
        return _runContinuationsAsynchronously ? WaitAsyncDefault(cancellationToken) : WaitAsyncInline(cancellationToken);
    }

    /// <summary>★ 默认模式（线程池异步调度）——async 实现 + 锁内入队（V1 同形）：
    /// 状态机在返回调用方前同步注册 OnCompleted；完成侧走 MarkOrComplete 安全协议，无哨兵崩溃类风险。</summary>
    private async ValueTask WaitAsyncDefault(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 快速路径：已 set，立即返回（零分配）
        if (Volatile.Read(ref _isSet))
            return;

        var source = PooledValueTaskSource.Rent(runContinuationsAsynchronously: true);
        if (cancellationToken.CanBeCanceled)
            source.AttachCancellation(cancellationToken);

        var node = new WaitNode { Source = source, Owner = this };
        lock (_lock)
        {
            // double-check：拿到锁后可能已被 Set
            if (Volatile.Read(ref _isSet))
            {
                PooledValueTaskSource.Return(source);
                return;
            }
            // 无锁入队（LIFO 栈式）
            node.Next = _head;
            _head = node;
        }

        try
        {
            await new ValueTask(source, source.Version).ConfigureAwait(false);
        }
        finally
        {
            // await 完成（正常/取消）后，归还 source 到池 + 从链表摘除
            RemoveNode(node);
            PooledValueTaskSource.Return(source);
        }
    }

    /// <summary>★ 内联模式（runContinuationsAsynchronously: false）——非 async 实现，零装箱；
    /// source 归还经 GetResult 清理钩子（内联模式无队列哨兵，提前归还安全）。</summary>
    private ValueTask WaitAsyncInline(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 快速路径：已 set，立即返回（零分配）
        if (Volatile.Read(ref _isSet))
            return default;

        var source = PooledValueTaskSource.Rent(runContinuationsAsynchronously: false);
        if (cancellationToken.CanBeCanceled)
            source.AttachCancellation(cancellationToken);

        // ★ 单 waiter 快路径：CAS 抢占单槽——1:1 唤醒免 WaitNode 分配与双锁
        if (Interlocked.CompareExchange(ref _singleWaiter, source, null) == null)
        {
            // double-check：抢占槽位后可能已被 Set（Set 先写 isSet 再 Exchange 槽，读到 true 即已完成）
            if (Volatile.Read(ref _isSet))
            {
                Interlocked.CompareExchange(ref _singleWaiter, null, source);   // 清槽（可能已被 Set 取走）
                PooledValueTaskSource.Return(source);
                return default;
            }
            source.CleanupState = this;
            source.OnCleanup = s_slotCleanup;
            return new ValueTask(source, source.Version);
        }

        // 慢速路径（单槽被占 → 多 waiter）：入链表
        var node = new WaitNode { Source = source, Owner = this };
        lock (_lock)
        {
            // double-check：拿到锁后可能已被 Set
            if (Volatile.Read(ref _isSet))
            {
                PooledValueTaskSource.Return(source);
                return default;
            }
            // 无锁入队（LIFO 栈式）
            node.Next = _head;
            _head = node;
        }
        source.CleanupState = node;
        source.OnCleanup = s_nodeCleanup;
        return new ValueTask(source, source.Version);
    }

    /// <summary>
    /// 同步等待事件被 set，阻塞调用线程。
    /// <para>基于自旋等待实现，不走 sync-over-async。</para>
    /// </summary>
    public void Wait(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var spin = new SpinWait();
        while (!Volatile.Read(ref _isSet))
        {
            cancellationToken.ThrowIfCancellationRequested();
            spin.SpinOnce();
        }
    }

    /// <summary>
    /// ★ 定时同步等待（段表物理门用）——自旋 → park 分片，不走 sync-over-async。
    /// <para>先自旋（SpinOnce 自带退避），进入 yield 阶段后持 _syncWait park；
    /// <see cref="Set"/> 侧 pulse 同步等待者；另设 50ms 自醒分片——脉冲即使丢失也有界重查（双保险）。</para>
    /// </summary>
    /// <param name="timeoutMs">最长等待毫秒数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>true = 事件已 set；false = 超时。</returns>
    public bool Wait(int timeoutMs, CancellationToken cancellationToken = default)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        var spin = new SpinWait();
        while (!Volatile.Read(ref _isSet))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (spin.NextSpinWillYield)
            {
                var remaining = deadline - Environment.TickCount64;
                if (remaining <= 0) return false;
                // ★ 先登记同步等待者计数再 double-check（#PERF-002）：
                //   Set 侧先写 isSet 再读计数——若 Set 读到 0 跳过 Pulse，本线程的锁内 double-check
                //   必读到 isSet=true（volatile 序 + Interlocked 全屏障），无丢脉冲窗口。
                Interlocked.Increment(ref _syncWaiters);
                try
                {
                    lock (_syncWait)
                    {
                        // double-check：拿锁后可能已被 Set
                        if (Volatile.Read(ref _isSet)) return true;
                        Monitor.Wait(_syncWait, (int)Math.Min(remaining, SyncWaitSliceMs));
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref _syncWaiters);
                }
                spin.Reset();   // 被唤醒/自醒后短暂重自旋再进 park
            }
            else
            {
                spin.SpinOnce();
            }
        }
        return true;
    }

    /// <summary>同步等待者的 park 对象——Set 侧 pulse。</summary>
    private readonly object _syncWait = new();
    /// <summary>同步等待自醒分片——脉冲丢失的有界兜底。</summary>
    private const int SyncWaitSliceMs = 50;

    /// <summary>
    /// 将事件设置为 set 状态，唤醒所有当前等待者。事件保持 set 直到 <see cref="Reset"/>。
    /// <para>重复 Set（已 set 再 Set）为 no-op（幂等）。</para>
    /// </summary>
    public void Set()
    {
        // 快速路径：已 set，幂等返回
        if (Volatile.Read(ref _isSet))
            return;

        WaitNode? toWake;
        PooledValueTaskSource? single;
        lock (_lock)
        {
            if (Volatile.Read(ref _isSet)) return;
            Volatile.Write(ref _isSet, true);
            // 原子取出整个 waiter 链表 + 单 waiter 快路径槽
            toWake = _head;
            _head = null;
            single = Interlocked.Exchange(ref _singleWaiter, null);
        }

        // 锁外唤醒单槽 waiter（MarkOrComplete：完成先于注册安全协议——未注册时留待 OnCompleted 兜底）
        single?.MarkOrComplete();

        // 锁外遍历唤醒所有链表 waiter（逐个完成独立 source）
        while (toWake is not null)
        {
            var next = toWake.Next;
            toWake.Source.MarkOrComplete();
            // source 的归还由 waiter 的 WaitAsync finally/清理钩子负责（await 完成后归还）
            toWake = next;
        }

        // ★ 仅在有定时同步等待者时 Pulse（#PERF-002）——异步场景免每 Set 白付 Monitor 往返
        if (Volatile.Read(ref _syncWaiters) > 0)
        {
            lock (_syncWait)
                Monitor.PulseAll(_syncWait);
        }
    }

    /// <summary>
    /// 重置事件为 unset 状态，使后续 <see cref="WaitAsync"/> 重新阻塞。
    /// </summary>
    public void Reset() => Volatile.Write(ref _isSet, false);

    /// <summary>从 waiter 链表中摘除指定节点（O(n)，但 waiter 数通常很少）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RemoveNode(WaitNode target)
    {
        lock (_lock)
        {
            // 链表可能已被 Set 清空（_head == null），此时节点已在 Set 中被处理，无需摘除
            if (_head == null) return;

            if (ReferenceEquals(_head, target))
            {
                _head = target.Next;
                return;
            }

            var prev = _head;
            while (prev!.Next is not null)
            {
                if (ReferenceEquals(prev.Next, target))
                {
                    prev.Next = target.Next;
                    return;
                }
                prev = prev.Next;
            }
        }
    }
}
