using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;

namespace TC.Tier.Core.Primitives;

/// <summary>
/// 池化的单次完成 <see cref="IValueTaskSource{TResult}"/>（带载荷版——<see cref="PooledValueTaskSource"/> 的泛化）：
/// 每次 <see cref="Rent"/> 一个独立实例，<see cref="SetResult(T)"/> / <see cref="SetException"/> 后归还复用。
/// <para>语义与非泛型版逐条同源（完成先于注册协议 #PERF-002 / 单次完成守卫 / 双层池化 /
/// 归还拒绝迟到完成 / CORE-03 注销），差异仅载荷经 <see cref="ManualResetValueTaskSourceCore{TResult}"/>
/// 的结果通道传递（GetResult 原样返回）。</para>
/// <para>典型消费面：请求回调 pending 表——每请求 Rent 一个代替 new TaskCompletionSource（TCS 一次性
/// 语义不可池化，本类型 Reset 后可复用）；await 终态经 OnCleanup 或显式 <see cref="Return"/> 归还。</para>
/// </summary>
public sealed class PooledValueTaskSource<T> : IValueTaskSource<T>
{
    private ManualResetValueTaskSourceCore<T> _core;
    private CancellationTokenRegistration _registration;

    // ★ 完成先于注册协议（#PERF-002——与非泛型版同源）：_markState 0=武装 1=发起方已标记 2=已归还；
    //   _registered：OnCompleted 是否已注册；MarkOrComplete 先标记，注册后即补完成。
    private int _markState;
    private int _registered;
    private Exception? _pendingError;
    private T? _pendingResult; // MarkOrComplete 暂存的载荷——注册兜底完成（SetResult()）时取用

    /// <summary>★ 完成时清理钩子：GetResult 完成态时以 <c>(CleanupState, this)</c> 调用一次（正常/异常都调）。</summary>
    public object? CleanupState { get; set; }

    private Action<object?, PooledValueTaskSource<T>>? _onCleanup;

    /// <summary>清理回调（与 <see cref="CleanupState"/> 配对——GetResult 后清空）。</summary>
    public Action<object?, PooledValueTaskSource<T>>? OnCleanup
    {
        get => _onCleanup;
        set => _onCleanup = value;
    }

    // ★ 单次完成守卫：0=武装 1=已完成 2=已归还（迟到/重复完成一律 no-op——防双触发 SignalCompletion）。
    private int _state;

    private PooledValueTaskSource()
    {
        _core.RunContinuationsAsynchronously = true;
    }

    /// <summary>当前 core 的版本号，供构造 <see cref="ValueTask{TResult}"/>。</summary>
    public short Version
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _core.Version;
    }

    /// <summary>★ 完成先于注册的安全完成入口（带载荷）：载荷暂存 _pendingResult（不直触 core——
    /// 未注册时 core.SetResult 会走 CompletionSentinel 提前完成路径，即 #PERF-002 要防的形态），
    /// 注册即经 <see cref="SetResult(T)"/> 单参重载补完成。</summary>
    /// <param name="result">完成载荷，暂存至 <see cref="_pendingResult"/>；等待者 GetResult 时原样返回。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void MarkOrComplete(T result)
    {
        // 普通写即可：后随 _markState CAS（full fence）——观察者见到标记时必已见载荷
        _pendingResult = result;
        if (Interlocked.CompareExchange(ref _markState, 1, 0) != 0) return;
        if (Volatile.Read(ref _registered) != 0)
            SetResult();
    }

    /// <summary>★ 异常版 MarkOrComplete（取消路径）：暂存异常，注册（OnCompleted 转发处）即补完。</summary>
    /// <param name="error">完成源携带的异常（取消路径传 <see cref="OperationCanceledException"/>）；等待者 GetResult 时重抛。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void MarkOrFault(Exception error)
    {
        if (Interlocked.CompareExchange(ref _markState, 1, 0) != 0) return;
        Volatile.Write(ref _pendingError, error);
        if (Volatile.Read(ref _registered) != 0)
            SetException(error);
    }

    /// <summary>完成源（成功，带载荷）。单次完成：已完成后/已归还后再调为 no-op。
    /// 无参补完成路径（OnCompleted 兜底）读 <see cref="_pendingResult"/>。</summary>
    /// <param name="result">完成载荷；等待者 GetResult 时原样返回。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetResult(T result)
    {
        _pendingResult = result; // 后随 _state CAS（full fence）——次序同上
        SetResult();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetResult()
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) return;
        DetachCancellation();
        _core.SetResult(_pendingResult!);
    }

    /// <summary>完成源（异常）。单次完成：已完成后/已归还后再调为 no-op。</summary>
    /// <param name="exception">完成源携带的异常；等待者 GetResult 时重抛。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetException(Exception exception)
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) return;
        DetachCancellation();
        _core.SetException(exception);
    }

    /// <summary>附加取消令牌：token 触发时以 <see cref="OperationCanceledException"/> 完成源。须在构造 ValueTask 前调用（每实例一次）。</summary>
    /// <param name="cancellationToken">要附加的取消令牌；不可取消的令牌为 no-op。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AttachCancellation(CancellationToken cancellationToken)
    {
        if (cancellationToken.CanBeCanceled)
        {
            _registration = cancellationToken.Register(
                static s => ((PooledValueTaskSource<T>)s!).MarkOrFault(new OperationCanceledException()),
                this);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DetachCancellation()
    {
        _registration.Dispose();
        _registration = default;
    }

    // ===== IValueTaskSource<T> 转发 =====

    T IValueTaskSource<T>.GetResult(short token)
    {
        try
        {
            return _core.GetResult(token);
        }
        finally
        {
            if (_state == 1)
            {
                var cleanup = Interlocked.Exchange(ref _onCleanup, null);
                cleanup?.Invoke(CleanupState, this);
            }
        }
    }

    ValueTaskSourceStatus IValueTaskSource<T>.GetStatus(short token) => _core.GetStatus(token);

    void IValueTaskSource<T>.OnCompleted(
        Action<object?> continuation, object? state,
        short token, ValueTaskSourceOnCompletedFlags flags)
    {
        if (token != _core.Version)
            throw new InvalidOperationException(
                $"[探针] token={token} version={_core.Version} state={_state} markState={_markState} registered={_registered}");
        _core.OnCompleted(continuation, state, token, flags);
        Volatile.Write(ref _registered, 1);
        if (Interlocked.CompareExchange(ref _markState, 0, 1) == 1)
        {
            var error = Volatile.Read(ref _pendingError);
            if (error is not null) SetException(error);
            else SetResult();
        }
    }

    /// <summary>
    /// 未完成放弃（超时/取消/发送失败路径）：仅当仍武装（无任何完成发生）时归还池。
    /// <para>false = 已完成（等待方 GetResult 消费后显式 <see cref="Return"/>) 或已归还——调用方不做任何事。
    /// 晚到的迟到完成由归还态（state=2）守卫拒绝。</para>
    /// </summary>
    /// <returns>true = 抢占成功并已归还池；false = 实例已完成或已归还，未做任何处理。</returns>
    public bool TryRelease()
    {
        if (Interlocked.CompareExchange(ref _state, 2, 0) != 0) return false;
        Volatile.Write(ref _markState, 2);
        _pendingError = null;
        _core.Reset();
        _registration.Dispose();
        _registration = default;
        Return(this); // 归还态重置幂等——单一入池
        return true;
    }

    // ===== 双层池化（按闭合泛型类型独立成池）=====

    private const int LocalCap = 16;
    private const int GlobalCap = 256;
    private const int TransferBatch = 8;

    [ThreadStatic] private static Stack<PooledValueTaskSource<T>>? t_localStack;

    private static readonly ConcurrentStack<PooledValueTaskSource<T>> SGlobalPool = new();

    /// <summary>租用一个实例：thread-local 栈 → 全局并发栈 → new。</summary>
    /// <param name="runContinuationsAsynchronously">完成时是否异步调度 continuation（默认 true；false=完成方栈内联执行等待方续体）。</param>
    /// <returns>重置为武装态（待完成）的实例，用 <see cref="Version"/> 构造 <see cref="ValueTask{TResult}"/> 使用。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    [SuppressMessage("Design", "CA1000:不要在泛型类型中声明静态成员")]
    public static PooledValueTaskSource<T> Rent(bool runContinuationsAsynchronously = true)
    {
        var local = t_localStack;
        if (local is not null && local.TryPop(out var s))
        {
            Volatile.Write(ref s._state, 0);
            Volatile.Write(ref s._markState, 0);
            Volatile.Write(ref s._registered, 0);
            Volatile.Write(ref s._pendingError, null);
            s._core.RunContinuationsAsynchronously = runContinuationsAsynchronously;
            return s;
        }
        if (!SGlobalPool.TryPop(out var s2))
            return new PooledValueTaskSource<T>
                { _core = { RunContinuationsAsynchronously = runContinuationsAsynchronously } };
        Volatile.Write(ref s2._state, 0);
        Volatile.Write(ref s2._markState, 0);
        Volatile.Write(ref s2._registered, 0);
        Volatile.Write(ref s2._pendingError, null);
        s2._core.RunContinuationsAsynchronously = runContinuationsAsynchronously;
        return s2;

    }

    /// <summary>归还实例：先置归还态拒绝迟到完成/标记，Reset 恢复可复用，注销取消注册，入池（双层同非泛型版）。</summary>
    /// <param name="source">要归还的实例（此前必须已完成消费或经 <see cref="TryRelease"/> 抢占）。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    [SuppressMessage("Design", "CA1000:不要在泛型类型中声明静态成员")]
    public static void Return(PooledValueTaskSource<T> source)
    {
        Interlocked.Exchange(ref source._state, 2);
        Interlocked.Exchange(ref source._markState, 2);
        Volatile.Write(ref source._registered, 0);
        Volatile.Write(ref source._pendingError, null);
        source._pendingResult = default;
        source._onCleanup = null;
        source.CleanupState = null;
        source._core.Reset();
        source._registration.Dispose();
        source._registration = default;

        var local = t_localStack ??= new Stack<PooledValueTaskSource<T>>(LocalCap);
        if (local.Count < LocalCap)
        {
            local.Push(source);
            return;
        }

        var batch = new PooledValueTaskSource<T>[TransferBatch + 1];
        var taken = 0;
        while (taken < TransferBatch && local.Count > 0)
            batch[taken++] = local.Pop();
        batch[taken++] = source;
        if (SGlobalPool.Count + taken <= GlobalCap)
            SGlobalPool.PushRange([.. batch.AsSpan(0, taken)]);
    }
}