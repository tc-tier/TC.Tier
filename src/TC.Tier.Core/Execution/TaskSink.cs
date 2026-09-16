using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace TC.Tier.Core.Execution;

/// <summary>
/// 任务沉池（fire-and-forget 派发原语——执行载体 taxonomy 终态：短频繁·可丢失自愈）。
/// <para>★ 契约一句话：提交即放手（void 不返回，调用方零仪式）。<b>保证</b>——异常收口不外泄
///   （计数 + 日志 + onFaulted 回调）、组生命周期（Dispose = 取消传播 +
///   有界 drain）；<b>不保证</b>——执行（已释放即拒/池拒绝/进程终）、完成通知（丢弃静默，
///   仅计数器事后可观测）、顺序（提交序 ≠ 执行序）。</para>
/// <para>★ 越界即错：按身份去重/单任务取消/等待指定任务完成不是本原语的语义——协议层自持状态；
///   长稳定异步循环走专用线程 + <see cref="AsyncPump"/> 泵域；时间驱动唤醒走
///   <see cref="DeadlineRegistry"/>。</para>
/// <para>★ 在途跟踪 = 原子计数 + drain 信号（提交热路径零堆分配——<see cref="DisposeAsync"/> 等计数归零）。</para>
/// </summary>
public sealed class TaskSink : IAsyncDisposable, IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _drainedSignal = new(0, 1);   // drain 唤醒（在途归零时 Release）
    private readonly string _name;
    private readonly ILogger? _logger;
    private readonly Action<Exception>? _onFaulted;
    private readonly TimeSpan _drainTimeout;
    private long _nextId; // 任务 ID（Interlocked 递增——比 Guid 高频便宜一个量级；诊断标识）
    private long _submitted;
    private long _faulted;
    private long _inFlight;   // 在途原子计数（O(1) 读——drain 等归零；唯一在途真源）
    private int _drainArmed;  // drain 等待中（完成方归零时按需 Release——无 drain 时不碰信号量）
    private int _disposed;

    /// <summary>在途任务数（并发下非精确快照——诊断用；O(1) 原子读）。</summary>
    public int InFlightCount => (int)Interlocked.Read(ref _inFlight);

    /// <summary>累计提交数（洪水率量化：提交速率可观测）。</summary>
    public long SubmittedCount => Interlocked.Read(ref _submitted);

    /// <summary>累计故障数（未捕获异常兜底计数——黑洞率量化：Faulted/Submitted）。</summary>
    public long FaultedCount => Interlocked.Read(ref _faulted);

    /// <summary>
    /// 构造。
    /// </summary>
    /// <param name="name">诊断标识（日志区分多实例）。</param>
    /// <param name="logger">日志（未捕获异常兜底记录；null = 静默）。</param>
    /// <param name="onFaulted">未捕获异常回调（logger 之后调用——测试断言/高级消费方）。</param>
    /// <param name="drainTimeout">Dispose 等在途完成的超时（超时 LogWarning 不抛）。默认 5s（对齐 WorkerLoop）。须 &gt; 0。</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="drainTimeout"/> ≤ 0。</exception>
    public TaskSink(
        string? name = null,
        Action<Exception>? onFaulted = null,
        TimeSpan? drainTimeout = null,
        ILogger? logger = null)
    {
        _drainTimeout = drainTimeout ?? TimeSpan.FromSeconds(5);
        if (_drainTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(drainTimeout), drainTimeout, "drainTimeout 必须 > 0。");
        _name = name ?? nameof(TaskSink);
        _logger = logger;
        _onFaulted = onFaulted;
    }

    /// <summary>
    /// 提交后台任务（fire-and-forget——void 不返回，调用方零仪式）。全程线程池执行。
    /// <para>★ 适用：第一段未知/不可控（第三方回调、闭包路径多）——契约无第一段假设。</para>
    /// </summary>
    /// <param name="taskBody">任务体（拿组级 ct——Dispose 取消传播）。</param>
    /// <exception cref="ObjectDisposedException">池已 Dispose（fail-fast——静默收进已释放池 = 黑洞无迹）。</exception>
    /// <remarks>池上启动：任务体全程（含第一段）在线程池执行——提交线程零占用；代价是每次提交固定一次池调度跳（无快路径）</remarks>
    public void Submit(Func<CancellationToken, Task> taskBody)
    {
        var ct = PrepareSubmit(out var id);
        var observer = Observer.Rent(this, id, valueBody: null, taskBody, ExecutionContext.Capture());
        var queued = false;
        try
        {
            RegisterInFlight();
            queued = true;
            // ★ 外层无组级取消（防泄漏雷）：排队项若被池级取消会跳过观察者收尾 → finally 不跑 →
            //   在途计数永不归零 → drain 挂死。取消只经任务体协作路径出来（OCE 归一），收尾必经。
            ThreadPool.UnsafeQueueUserWorkItem(observer, preferLocal: false);
        }
        finally
        {
            if (!queued) Observer.Return(observer);   // 登记回滚（ODE）——观察者归还，任务体未起
        }
    }

    /// <summary>
    /// 提交后台任务（fire-and-forget——void 不返回，调用方零仪式）。
    /// 同步完成走 0 分配快路径；未同步完成续段自动在线程池执行。
    /// </summary>
    /// <param name="taskBody">任务体（拿组级 ct——Dispose 取消传播）。</param>
    /// <exception cref="ObjectDisposedException">池已 Dispose（fail-fast——静默收进已释放池 = 黑洞无迹）。</exception>
    public void SubmitFast(Func<CancellationToken, ValueTask> taskBody)
    {
        var ct = PrepareSubmit(out var id);
        var observer = Observer.Rent(this, id, valueBody: taskBody, taskBody: null, ExecutionContext.Capture());
        RunInline(observer);
    }

    /// <summary>
    /// 提交后台任务（Task 便捷重载）——观察者直接承载 <see cref="Task"/> 体
    /// （零闭包适配），同步完成走 0 分配快路径，未同步完成续段自动在线程池执行。
    /// </summary>
    /// <param name="taskBody">任务体（拿组级 ct——Dispose 取消传播）。</param>
    public void SubmitFast(Func<CancellationToken, Task> taskBody)
    {
        var ct = PrepareSubmit(out var id);
        var observer = Observer.Rent(this, id, valueBody: null, taskBody, ExecutionContext.Capture());
        RunInline(observer);
    }

    /// <summary>内联执行（SubmitFast 两重载共用）：在途登记（ODE 回滚时观察者归还池、任务体未起）
    /// → 观察者首段在提交线程执行（同步完成即收尾归还；真挂起续体自动池上——计数在途，无注册动作）。</summary>
    private void RunInline(Observer observer)
    {
        var started = false;
        try
        {
            RegisterInFlight();
            started = true;
            observer.RunCore();
        }
        finally
        {
            if (!started) Observer.Return(observer);
        }
    }

    /// <summary>提交前奏：fail-fast 检查 + 任务 ID 分配 + 提交计数。</summary>
    /// <param name="id">输出：本次任务 ID（诊断标识）。</param>
    /// <returns>任务体应使用的组级取消 token（Dispose 时触发）。</returns>
    /// <exception cref="ObjectDisposedException">池已 Dispose（防悬挂提交）。</exception>
    private CancellationToken PrepareSubmit(out long id)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        id = Interlocked.Increment(ref _nextId);
        Interlocked.Increment(ref _submitted);
        return _cts.Token;
    }

    /// <summary>
    /// 在途登记（提交同步段调用——先于任何执行/完成，消灭"完成后才登记"竞态窗口）。
    /// <para>★ 二次校验：封堵提交/释放竞态窗口（PrepareSubmit 的 disposed 检查与本次递增
    /// 非原子——Dispose 的 drain 可能已按旧计数归零通过。发现已释放立即回滚并抛——
    /// 从源头拦截漏网任务；回滚使计数归位，drain 不受影响，任务体未起（无悬挂））。</para>
    /// </summary>
    /// <exception cref="ObjectDisposedException">池已 Dispose。</exception>
    private void RegisterInFlight()
    {
        Interlocked.Increment(ref _inFlight);
        if (Volatile.Read(ref _disposed) != 0)
        {
            RetireInFlight();
            throw new ObjectDisposedException(nameof(TaskSink));
        }
    }

    /// <summary>在途退役（WrapAsync finally / 登记回滚共用）：递减计数，归零且 drain 等待中时唤醒 drain。</summary>
    private void RetireInFlight()
    {
        if (Interlocked.Decrement(ref _inFlight) == 0 && Volatile.Read(ref _drainArmed) != 0)
            _drainedSignal.Release();
    }

    /// <summary>故障收口（双包装共用——单一真源：改日志格式/埋点只动此处）：
    /// 计数 + 告警 + onFaulted 回调保护（回调自身抛异常会逃出外层 catch → 包装任务 faulted →
    /// unobserved 黑洞回流——兜底记日志，任务状态保持恒成功）。</summary>
    /// <param name="ex">任务体未捕获异常。</param>
    /// <param name="id">任务 ID（诊断标识）。</param>
    private void HandleFault(Exception ex, long id)
    {
        Interlocked.Increment(ref _faulted);
        _logger?.LogWarning(ex, "任务池未捕获异常：name={Name} id={Id}", _name, id);
        try { _onFaulted?.Invoke(ex); }
        catch (Exception callbackEx)
        {
            _logger?.LogError(callbackEx, "任务池 onFaulted 回调异常：name={Name} id={Id}", _name, id);
        }
    }

    /// <summary>
    /// 提交观察者（池化——去 async 包装状态机箱）：字段携带 (sink, id, body, ct, ec)，
    /// 续体委托构造期缓存一份；两段式执行——首段内联（SubmitFast 提交线程 / Submit
    /// 经 <see cref="IThreadPoolWorkItem"/> 池直排），真挂起经 UnsafeOnCompleted 池上续体收尾。
    /// 异常收口（取消归一/故障计数/在途退役）与租还复用（双层池：thread-local 栈 +
    /// 全局并发栈，<see cref="PooledValueTaskSource"/> 同款）单点收口。
    /// <para>★ 完成先于注册：完成态下 UnsafeOnCompleted 同步内联续体（实例即归还池）——
    ///   注册恒为所在段最后一语句，此后禁再触碰任何字段。</para>
    /// <para>★ ExecutionContext 保持：提交点捕获、首段（池直排时）与续体收尾段重放——
    ///   AsyncLocal 父子链（ITracer span/日志作用域）对齐 async 包装语义。</para>
    /// </summary>
    private sealed class Observer : IThreadPoolWorkItem
    {
        private const int LocalCap = 16;       // thread-local 栈容量上限
        private const int GlobalCap = 256;     // 全局池容量上限（溢出丢弃交 GC）

        [ThreadStatic]
        private static Stack<Observer>? t_local;

        private static readonly ConcurrentStack<Observer> s_global = new();
        private static readonly ContextCallback s_executeCallback = static o => ((Observer)o!).RunCore();
        private static readonly ContextCallback s_completeCallback = static o => ((Observer)o!).OnCompletedCore();

        private TaskSink _sink = null!;
        private long _id;
        private CancellationToken _ct;
        private Func<CancellationToken, ValueTask>? _valueBody;   // ValueTask 体（与 _taskBody 互斥）
        private Func<CancellationToken, Task>? _taskBody;
        private ExecutionContext? _ec;                             // 提交点上下文（null = 默认上下文零开销）
        private ValueTaskAwaiter _awaiter;
        private readonly Action _onCompleted;

        private Observer() => _onCompleted = OnCompleted;

        /// <summary>租用：优先 thread-local 栈（无锁），其次全局并发栈，最后 new；租出即写入本轮载荷。</summary>
        /// <param name="sink">目标 TaskSink（载荷与取消 token 来源），非空。</param>
        /// <param name="id">本轮任务的 sink 内单调 id。</param>
        /// <param name="valueBody">ValueTask 任务体（与 <paramref name="taskBody"/> 互斥，可为 null）。</param>
        /// <param name="taskBody">Task 任务体（与 <paramref name="valueBody"/> 互斥，可为 null）。</param>
        /// <param name="ec">提交点捕获的 <see cref="ExecutionContext"/>（null = 默认上下文零开销）。</param>
        /// <returns>已写入本轮载荷的池化 Observer 实例。</returns>
        public static Observer Rent(TaskSink sink, long id,
            Func<CancellationToken, ValueTask>? valueBody,
            Func<CancellationToken, Task>? taskBody,
            ExecutionContext? ec)
        {
            var local = t_local;
            var o = local is not null && local.TryPop(out var localHit) ? localHit
                : s_global.TryPop(out var globalHit) ? globalHit
                : new Observer();
            o._sink = sink;
            o._id = id;
            o._ct = sink._cts.Token;
            o._valueBody = valueBody;
            o._taskBody = taskBody;
            o._ec = ec;
            return o;
        }

        /// <summary>归还：载荷字段清零（防跨轮串味）后入 thread-local 栈，满则溢出全局栈（超上限丢弃）。</summary>
        /// <param name="o">要归还的 Observer 实例，非空。</param>
        public static void Return(Observer o)
        {
            o._sink = null!;
            o._valueBody = null;
            o._taskBody = null;
            o._ec = null;
            o._awaiter = default;
            var local = t_local ??= new Stack<Observer>(LocalCap);
            if (local.Count < LocalCap)
            {
                local.Push(o);
                return;
            }
            if (s_global.Count < GlobalCap)
                s_global.Push(o);
        }

        /// <summary>池直排入口（<see cref="TaskSink.Submit"/>——提交点捕获的上下文下执行首段）。</summary>
        void IThreadPoolWorkItem.Execute()
        {
            var ec = _ec;
            if (ec is not null) ExecutionContext.Run(ec, s_executeCallback, this);
            else RunCore();
        }

        /// <summary>首段执行（内联或池直排共用）：任务体调用（同步抛即收尾）→ 完成态直接收尾；
        /// 真挂起注册缓存续体（UnsafeOnCompleted 无上下文捕获——收尾段由本类显式重放提交点上下文）。</summary>
        internal void RunCore()
        {
            ValueTask vt;
            try
            {
                // ★ 观察者本地暂存（单次消费于下段 GetAwaiter）——CA2012 不识别该形态，豁免
#pragma warning disable CA2012
                if (_valueBody is { } valueBody) vt = valueBody(_ct);
                else vt = new ValueTask(_taskBody!(_ct));
#pragma warning restore CA2012
            }
            catch (Exception ex)
            {
                FinishFault(ex);
                return;
            }
            try
            {
#pragma warning disable CA2012 // 设计必需：GetAwaiter 仅取等待器不消费——IsCompleted 检查后同步收尾或 UnsafeOnCompleted 注册一次（ValueTask 单次消费契约由观察者租还生命周期保证）
                var awaiter = vt.GetAwaiter();
#pragma warning restore CA2012
                if (awaiter.IsCompleted)
                {
                    FinishWith(awaiter);
                    return;
                }
                _awaiter = awaiter;
                // 慢路径观测（注册前取快照——注册即移交，此后禁碰字段）
                var sink = _sink;
                sink._logger?.LogTrace("任务池提交慢路径：name={Name} id={Id}", sink._name, _id);
                // ★ 完成先于注册时回调同步内联（实例即归还池）——注册必须收尾本方法
                awaiter.UnsafeOnCompleted(_onCompleted);
            }
            catch (Exception ex)
            {
                FinishFault(ex);
            }
        }

        /// <summary>挂起续体（完成线程执行——池上）：提交点上下文下收尾。</summary>
        private void OnCompleted()
        {
            var ec = _ec;
            if (ec is not null) ExecutionContext.Run(ec, s_completeCallback, this);
            else OnCompletedCore();
        }

        private void OnCompletedCore() => FinishWith(_awaiter);

        /// <summary>完成收尾（同步完成/续体完成共用）：GetResult 观察终态——取消归一（不进故障计数）/
        /// 故障收口；finally 退役在途计数（与 RegisterInFlight 配对）后归还池。</summary>
        private void FinishWith(ValueTaskAwaiter awaiter)
        {
            var sink = _sink;
            var id = _id;
            try
            {
#pragma warning disable TCSG137 // 设计必需：完成态消费（IsCompleted/完成续体已判——取结果零阻塞），不取结果则底层箱/源不归还
                awaiter.GetResult();
#pragma warning restore TCSG137
            }
            catch (OperationCanceledException)
            {
                // 取消归一（组取消 Dispose 收尾 / 任务体业务取消——取消即完成，不进故障计数）
            }
            catch (Exception ex)
            {
                sink.HandleFault(ex, id);   // 计数+告警+onFaulted 回调保护（单一真源）
            }
            finally
            {
                sink.RetireInFlight();
                Return(this);
            }
        }

        /// <summary>首段同步抛/注册抛收尾（同 <see cref="FinishWith"/> 的故障面——任务体未产出可等待终态）。</summary>
        private void FinishFault(Exception ex)
        {
            var sink = _sink;
            sink.HandleFault(ex, _id);
            sink.RetireInFlight();
            Return(this);
        }
    }

    /// <summary>
    /// 异步释放：取消传播（在途任务协作收尾）→ 有界等在途计数归零（超时 LogWarning 剩余数，不抛）
    /// → 释放资源（drain 后再释放——在途任务还读 token/信号量）。
    /// </summary>
    /// <returns>任务在取消传播、在途计数归零（或超时放弃等待）并完成资源释放后完成。</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return; // CAS 幂等
        await _cts.CancelAsync().ConfigureAwait(false);
        var completed = await WaitDrainedAsync().ConfigureAwait(false);
        FinishDrain(completed);
    }

    /// <summary>
    /// 同步释放——同 <see cref="DisposeAsync"/> 但阻塞等待（同步 Dispose 契约须返回前收尾，
    /// 对齐 <see cref="BackgroundWorkerLoop.Dispose"/> 同步等待先例）。
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return; // CAS 幂等
        _cts.Cancel();
        var completed = Interlocked.Read(ref _inFlight) == 0;
        if (!completed)
        {
#pragma warning disable TCSG137 // 设计必需：同步 Dispose 契约——返回前有界收尾（WorkerLoop.Dispose 同步等待同款）
            Volatile.Write(ref _drainArmed, 1);
            completed = Interlocked.Read(ref _inFlight) == 0   // 武装竞态补判（归零先于武装）
                || _drainedSignal.Wait(_drainTimeout);
#pragma warning restore TCSG137
        }
        FinishDrain(completed);
    }

    /// <summary>drain 等待（异步形态）：武装信号 → 有界等归零唤醒；false = 超时。</summary>
    private async Task<bool> WaitDrainedAsync()
    {
        if (Interlocked.Read(ref _inFlight) == 0) return true;
        Volatile.Write(ref _drainArmed, 1);
        if (Interlocked.Read(ref _inFlight) == 0) return true;   // 武装竞态补判（归零先于武装——RetireInFlight 未 Release 的窗口）
        return await _drainedSignal.WaitAsync(_drainTimeout).ConfigureAwait(false);
    }

    /// <summary>drain 收尾（双 Dispose 公共段）：未全完成则告警；释放资源（drain 后——在途任务还读 token）。</summary>
    /// <param name="completed">在途是否全部完成（false = 超时——LogWarning 剩余数，不抛）。</param>
    private void FinishDrain(bool completed)
    {
        if (!completed)
        {
            // ★ 超时 = 在途未收尾——cts/信号量滞留换安全：在途任务还会读 token、
            //   ct.Register（已释放 CTS 上抛 ODE）、归零 Release（已释放信号量抛 ODE）。
            //   资源由进程收尾（组级生命周期已终，量级=每滞留组一个 CTS+一个信号量）。
            _logger?.LogWarning("任务池 drain 超时：name={Name} 剩余={Remaining}", _name, InFlightCount);
            return;
        }
        _cts.Dispose();
        _drainedSignal.Dispose();
    }
}
