using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;

namespace TC.Tier.Core.Primitives;

/// <summary>
/// 池化的单次完成 <see cref="IValueTaskSource"/>——每次 Rent 一个独立实例，
/// <see cref="SetResult"/> / <see cref="SetException"/> 后归还复用。
/// <para>多 waiter 广播的正确做法：每个 waiter 持有独立的 <see cref="PooledValueTaskSource"/> 实例
/// （而非共享一个 core），由发起方在全部完成时逐个 Set。这与 <see cref="AsyncManualResetEvent"/>
/// 的共享 core 语义互补：本类型面向"一次性、单消费者"的高频 IO 唤醒场景。</para>
/// <para>双层池化（thread-local <see cref="Stack{T}"/> 热路径 + <see cref="ConcurrentStack{T}"/> 全局回退 + 批量搬运），
/// 常规 Rent/Return 往返零争用、零堆分配。</para>
/// </summary>
public sealed class PooledValueTaskSource : IValueTaskSource
{
    private ManualResetValueTaskSourceCore<object?> _core;
    private CancellationTokenRegistration _registration;

    // ★ 完成先于注册协议（#PERF-002）——ManualResetValueTaskSourceCore 在"完成先于 OnCompleted"时
    //   会排队/内联调用 CompletionSentinel，而哨兵在 continuation 从未注册（或 core 已 Reset 归还）时
    //   抛 InvalidOperationException。本类以自管标记协议替代裸 SetResult：
    //   _markState：0=武装；1=发起方已标记完成；2=已归还池（拒绝一切迟到标记）。
    //   _registered：OnCompleted 是否已注册 continuation。
    //   发起方调 MarkOrComplete：标记 0→1 成功且已注册 → 立即 SetResult；未注册 → 留待 OnCompleted 转发处兜底。
    private int _markState;
    private int _registered;
    private Exception? _pendingError;   // MarkOrFault 暂存的异常——注册兜底完成时取出

    /// <summary>
    /// ★ 完成时清理钩子（无 async 包装的归还逻辑用）：awaiter 的 GetResult 完成态时调用一次
    /// （正常/异常路径都调）。静态委托 + 租用者已有对象做 state，零分配。
    /// </summary>
    public object? CleanupState;
    public Action<object?, PooledValueTaskSource>? OnCleanup;

    // ★ 单次完成守卫（2026-08-14 竞态修复）：0=武装（租出待完成）；1=已完成；2=已归还池。
    //   取消回调 / Set 广播遍历 / 归还后的残留信号，任何两个并发撞上都会双触发
    //   ManualResetValueTaskSourceCore.SignalCompletion 抛 InvalidOperationException
    //   （AsyncManualResetEventTests 并行分组变化即暴露）。CAS 守护：只有第一个完成者生效，
    //   迟到/重复完成一律 no-op；归还后（state=2）任何残留完成也被拒绝。
    private int _state;

    private PooledValueTaskSource()
    {
        _core.RunContinuationsAsynchronously = true;
    }

    /// <summary>当前 core 的版本号，供构造 <see cref="ValueTask"/>。</summary>
    public short Version
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _core.Version;
    }

    /// <summary>
    /// ★ 完成先于注册的安全完成入口（#PERF-002）：标记"发起方已完成"；若 continuation 已注册则立即
    /// SetResult，否则留待 <see cref="IValueTaskSource.OnCompleted"/> 转发处兜底完成。
    /// <para>替代裸 SetResult——裸 SetResult 在未注册时触发 BCL 的 CompletionSentinel，后者在
    /// continuation 缺席/core 已 Reset 归还后抛 InvalidOperationException（进程级崩溃）。</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void MarkOrComplete()
    {
        if (Interlocked.CompareExchange(ref _markState, 1, 0) != 0) return;   // 已标记/已归还——拒绝
        if (Volatile.Read(ref _registered) != 0)
            SetResult();   // 已注册——立即完成
        // 未注册——OnCompleted 转发处兜底
    }

    /// <summary>
    /// ★ 异常版的 MarkOrComplete（取消路径）：先标记再按注册状态完成——未注册时暂存异常，
    /// 注册（OnCompleted 转发处）即补完。与 MarkOrComplete 竞争同一标记，先到者胜（单次完成语义）。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void MarkOrFault(Exception error)
    {
        if (Interlocked.CompareExchange(ref _markState, 1, 0) != 0) return;   // 已被成功/异常标记——拒绝
        Volatile.Write(ref _pendingError, error);
        if (Volatile.Read(ref _registered) != 0)
            SetException(error);
    }

    /// <summary>
    /// 完成源（成功）。单次完成：已完成后/已归还后再调为 no-op（防取消/Set 竞态双触发）。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetResult()
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) return;   // ★ 守卫：仅第一个完成者生效
        DetachCancellation();
        _core.SetResult(null);
    }

    /// <summary>
    /// 完成源（异常）。单次完成：已完成后/已归还后再调为 no-op（防取消/Set 竞态双触发）。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetException(Exception exception)
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) return;   // ★ 守卫：仅第一个完成者生效
        DetachCancellation();
        _core.SetException(exception);
    }

    /// <summary>
    /// 附加 <see cref="CancellationToken"/>：token 触发时以 <see cref="OperationCanceledException"/> 完成源。
    /// <para>必须在构造 <see cref="ValueTask"/> 之前调用。每个实例只能附加一次。</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AttachCancellation(CancellationToken cancellationToken)
    {
        if (cancellationToken.CanBeCanceled)
        {
            _registration = cancellationToken.Register(
                static s => ((PooledValueTaskSource)s!).MarkOrFault(new OperationCanceledException()),
                this);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DetachCancellation()
    {
        _registration.Dispose();
        _registration = default;
    }

    // ===== IValueTaskSource 转发 =====

    void IValueTaskSource.GetResult(short token)
    {
        try
        {
            _core.GetResult(token);
        }
        finally
        {
            // ★ 完成态（正常/异常）后触发一次清理钩子——无 async 包装的租用者在此归还
            //   （GetResult 是 awaiter 的最后一次接触，之后可安全 Return/Reset）。
            if (_state == 1)
            {
                var cleanup = Interlocked.Exchange(ref OnCleanup, null);
                cleanup?.Invoke(CleanupState, this);
            }
        }
    }

    ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _core.GetStatus(token);

    void IValueTaskSource.OnCompleted(
        Action<object?> continuation, object? state,
        short token, ValueTaskSourceOnCompletedFlags flags)
    {
        _core.OnCompleted(continuation, state, token, flags);
        // ★ 完成先于注册兜底（#PERF-002）：注册后才知 MarkOrComplete/MarkOrFault 曾先行——注册即补完。
        Volatile.Write(ref _registered, 1);
        if (Interlocked.CompareExchange(ref _markState, 0, 1) == 1)
        {
            var error = Volatile.Read(ref _pendingError);
            if (error is not null) SetException(error);
            else SetResult();
        }
    }

    // ===== 双层池化 =====

    private const int LocalCap = 16;       // thread-local 栈容量上限
    private const int GlobalCap = 256;     // 全局池容量上限
    private const int TransferBatch = 8;   // 批量搬运数量

    [ThreadStatic]
    private static Stack<PooledValueTaskSource>? t_localStack;

    private static readonly ConcurrentStack<PooledValueTaskSource> s_globalPool = new();

    /// <summary>
    /// 从池中租用一个实例：优先 thread-local 栈（无锁），其次全局并发栈，最后 new。
    /// </summary>
    /// <param name="runContinuationsAsynchronously">
    /// 完成时是否异步调度 continuation（默认 true）。false = 内联执行（Set/SetException 调用者线程）
    /// ——对齐 SemaphoreSlim 唤醒语义，省一次线程池往返；代价是完成方栈上执行等待方续体（重入语义自负）。
    /// </param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PooledValueTaskSource Rent(bool runContinuationsAsynchronously = true)
    {
        // ① thread-local 热路径（无锁、无争用）
        var local = t_localStack;
        if (local is not null && local.TryPop(out var s))
        {
            Volatile.Write(ref s._state, 0);   // ★ 重新武装（归还态→武装态；此刻租用者独占）
            Volatile.Write(ref s._markState, 0);
            Volatile.Write(ref s._registered, 0);
            Volatile.Write(ref s._pendingError, null);
            s._core.RunContinuationsAsynchronously = runContinuationsAsynchronously;
            return s;
        }

        // ② 全局并发栈回退
        if (s_globalPool.TryPop(out var s2))
        {
            Volatile.Write(ref s2._state, 0);
            Volatile.Write(ref s2._markState, 0);
            Volatile.Write(ref s2._registered, 0);
            Volatile.Write(ref s2._pendingError, null);
            s2._core.RunContinuationsAsynchronously = runContinuationsAsynchronously;
            return s2;
        }

        // ③ 池空，新建（_state 默认 0=武装）
        return new PooledValueTaskSource { _core = { RunContinuationsAsynchronously = runContinuationsAsynchronously } };
    }

    /// <summary>
    /// 归还实例到池中：先尝试 thread-local 栈，满了则批量搬运一半到全局栈。
    /// <para>归还前自动 <see cref="ManualResetValueTaskSourceCore{TResult}.Reset"/>，恢复为可复用状态。
    /// ★ 先置归还态（state=2）再 Reset——之后任何残留的迟到完成（如 Set 广播遍历晚到）被守卫拒绝。</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Return(PooledValueTaskSource source)
    {
        Interlocked.Exchange(ref source._state, 2);   // ★ 归还态：拒绝一切迟到完成
        Interlocked.Exchange(ref source._markState, 2);   // ★ 拒绝一切迟到标记（防 Set 侧跨归还的陈旧标记）
        Volatile.Write(ref source._registered, 0);
        Volatile.Write(ref source._pendingError, null);
        source.OnCleanup = null;
        source.CleanupState = null;
        // 归还前必须 Reset（让 core 进入新 generation，可被下次 Rent 后 Set）
        source._core.Reset();
        // ★ CORE-03：注册必须注销（原仅置 default——回调仍挂在旧 CTS 上：早退路径（AsyncManualResetEvent
        //   双检命中已 set）把 AttachCancellation 后未完成的 source 还池 → 重租重新武装后旧 token 触发 →
        //   新等待者被伪取消（实测复现 spuriousCompletion）。迟到回调由 _markState=2 拒绝——但注销是正解）
        source._registration.Dispose();
        source._registration = default;

        // ① 入 thread-local 栈
        var local = t_localStack ??= new Stack<PooledValueTaskSource>(LocalCap);
        if (local.Count < LocalCap)
        {
            local.Push(source);
            return;
        }

        // ② local 已满：批量搬运 TransferBatch 个到 global，腾出空间后再入 local
        // 数组容量 TransferBatch + 1：额外容纳本次归还的 source
        var batch = new PooledValueTaskSource[TransferBatch + 1];
        var taken = 0;
        while (taken < TransferBatch && local.Count > 0)
            batch[taken++] = local.Pop();
        batch[taken++] = source;

        // 全局池有容量上限，超出丢弃（交给 GC）
        if (s_globalPool.Count + taken <= GlobalCap)
            s_globalPool.PushRange(batch.AsSpan(0, taken).ToArray());
    }
}
