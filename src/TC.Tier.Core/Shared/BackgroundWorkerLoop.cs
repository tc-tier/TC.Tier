namespace TC.Tier.Core.Shared;

/// <summary>
/// 通用长生命周期后台循环骨架（第一层——循环骨架，无队列）。与业务无关。
/// <para>★ 封装：执行器（注入 <see cref="TaskScheduler"/>（如 IsolatedTaskScheduler.Shared）或 null=公共池 +
///   <see cref="ConsumerCount"/> 多消费者扇出——消费者是协作跑在调度器线程上的循环 Task，不是线程数）+ 幂等启停 +
///   超时等待退出 + CAS 防双 Dispose + 单周期异常隔离。</para>
/// <para>★ 子类实现 <see cref="RunOneCycleAsync"/>（一个工作周期）+ 信号源选择（见 worker-loop-unified-design.md §5）。</para>
/// <para>★ 自身 <see cref="IDisposable"/>——<see cref="LifecycleBase{THints}"/> 内建持有时，
///   Dispose 统一编排 Stop + WaitForExit（见 worker-loop-unified-design.md §4）。</para>
/// <para>★ 多消费者：<see cref="ConsumerCount"/> 个执行体共跑同一循环（各自 <see cref="RunOneCycleAsync"/>），
///   常用于事件驱动队列的多消费者 drain（<see cref="BackgroundWorkerLoop{T}"/> 内建优先级队列 + counting-semaphore
///   逐项公平唤醒，见 <see cref="Collections.BucketPriorityQueue{TPriority,T}"/>）。默认 1 = 向后兼容。</para>
/// </summary>
/// <remarks>
/// ★ 时间驱动 / 信号驱动 worker 直接继承本类（如 EntryLog / Compactor / EngineMeta / CpuSampler）。
/// ★ 事件驱动 + 队列 worker 继承 <see cref="BackgroundWorkerLoop{T}"/>（泛型层，内建队列）。
/// </remarks>
public abstract class BackgroundWorkerLoop : IDisposable, IAsyncDisposable
{
    // === 执行器（由注入的 TaskScheduler 决定；多消费者扇出）===
    private Task[]? _loopTasks;
    /// <summary>注入的调度器：null=公共池（Task.Run）；非 null=如 <see cref="IsolatedTaskScheduler.Shared"/>。
    /// ★ 本类<b>不 own</b> 调度器——生命周期归注入方（Shared 进程级 / 引擎 own 经 Resources 释放）。</summary>
    private readonly TaskScheduler? _scheduler;
    private readonly int _consumerCount;         // 消费者（循环 Task）数——协作跑在调度器线程上，不是线程数！
    private int _exitedConsumers;                // 已退出消费者计数（末位退出者跑 OnLoopExitAsync）
    private int _everStarted;                     // 是否启动过（首次启动跳过重启等待——CORE-19 防护只适用重启）

    // === 生命周期 ===
    private readonly CancellationTokenSource _cts = new();
    private int _running;                         // 0=未启动, 1=运行中（CAS 守护幂等 Start）
    private int _disposed;                        // CAS 防双 Dispose
    private readonly string _name;                // 诊断用（线程名 / 日志标识）
    private readonly TimeSpan _exitTimeout;       // Dispose 等退出超时，默认 5s
    private readonly ILogger? _logger;

    /// <summary>消费者数（构造时定）。1=单消费者（向后兼容）；>1=多执行体共 drain 同一信号源/队列。</summary>
    public int ConsumerCount => _consumerCount;

    /// <summary>
    /// consumerCount 上限 = <c>max(ProcessorCount*4, 16)</c>。
    /// <para>★ 依据：消费者是<b>协作</b>跑在调度器线程（M ≤ ProcessorCount）上的循环 Task、不是线程数——
    ///   N 超过可能并行度的 4 倍只增内存/调度开销、不增吞吐。</para>
    /// <para>★ 超界 ctor 直接 throw（fail-fast）——防"外部传巨大 N → 静默卡死无人知因"的失效模式。
    ///   需要更高并行度应增大调度器线程数（<see cref="IsolatedSchedulerOptions.ThreadCount"/>）而非 consumerCount。</para>
    /// </summary>
    public static int MaxConsumerCount => Math.Max(Environment.ProcessorCount * 4, 16);

    /// <summary>
    /// 构造。
    /// </summary>
    /// <param name="scheduler">执行器调度器：null=<see cref="Task.Run"/>（公共池——低频 worker，如 CpuSampler/meta flusher）；
    ///   非 null=注入（如 <see cref="IsolatedTaskScheduler.Shared"/>——高频/关键 worker，隔离公共池）。
    ///   ★ 线程数 M 由调度器决定，与 <paramref name="consumerCount"/> 彻底解耦；本类不 own 调度器（注入方管生命周期）。</param>
    /// <param name="consumerCount">消费者（循环 Task）数——<b>协作</b>跑在调度器线程上，<b>不是线程数</b>。默认 1。
    ///   范围 [1, <see cref="MaxConsumerCount"/>]，超界 throw。建议 N ≈ 调度器线程数 M（N ≫ M 只增开销不增吞吐，Start 时 WARN）。</param>
    /// <param name="name">诊断标识（线程名 / 日志）。默认用 GetType().Name。</param>
    /// <param name="exitTimeout">Dispose 等退出的超时。默认 5s（超时仅 LogWarning 不抛，对齐现状所有 worker）。须 &gt; 0。</param>
    /// <param name="logger">日志（可选）。</param>
    protected BackgroundWorkerLoop(
        TaskScheduler? scheduler = null,
        int consumerCount = 1,
        string? name = null,
        TimeSpan? exitTimeout = null,
        ILogger? logger = null)
    {
        if (consumerCount < 1)
            throw new ArgumentOutOfRangeException(nameof(consumerCount), consumerCount, "consumerCount 必须 >= 1");
        if (consumerCount > MaxConsumerCount)
            throw new ArgumentOutOfRangeException(nameof(consumerCount), consumerCount,
                $"consumerCount={consumerCount} 超过上限 {MaxConsumerCount}（=max(ProcessorCount*4,16)）。"
                + "消费者是协作跑在调度器线程上的循环 Task、不是线程数；N 超过并行度 4 倍只增内存/调度开销不增吞吐。"
                + "需要更高并行度应增大调度器线程数（IsolatedSchedulerOptions.ThreadCount）而非 consumerCount");
        if (exitTimeout is { } et && et <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(exitTimeout), et, "exitTimeout 必须 > 0");
        _scheduler = scheduler;
        _consumerCount = consumerCount;
        _name = name ?? GetType().Name;
        _exitTimeout = exitTimeout ?? TimeSpan.FromSeconds(5);
        _logger = logger;
    }

    // ════════════════════════════════════════════════════════════
    // === 子类钩子 ===
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 一个工作周期（子类核心实现）。
    /// <para>★ 典型实现：取一批任务（或等信号源）→ 处理 → 返回。</para>
    /// <para>★ 返回 false 则停止循环（如 EOF / 显式终止条件）。多消费者下：单个消费者返回 false 仅自身退出，
    ///   不影响其他消费者（如需全停，子类自行 <see cref="Stop"/>）。</para>
    /// <para>★ 抛异常不杀 worker——异常走 <see cref="OnCycleError"/> 钩子，循环继续下一周期。</para>
    /// <para>★ 收到 <see cref="OperationCanceledException"/>（Stop 触发的 cts.Cancel）视为正常退出，不进 OnCycleError。</para>
    /// </summary>
    protected abstract ValueTask<bool> RunOneCycleAsync(CancellationToken ct);

    /// <summary>循环启动前钩子（可选，初始化资源/状态）。★ 多消费者下仅 <see cref="Start"/> 调用一次。</summary>
    protected virtual void OnLoopStart() { }

    /// <summary>
    /// 循环退出后 drain/flush 钩子（可选）。
    /// <para>★ 用于退出前处理残留（如 Lifecycle 等 in-flight 建段、Compactor 最后 flush）。</para>
    /// <para>★ 多消费者下仅末位退出的消费者执行一次（Interlocked 守护）。</para>
    /// <para>★ ct 传 <see cref="CancellationToken.None"/>——退出清理不应被再次取消。</para>
    /// </summary>
    protected virtual ValueTask OnLoopExitAsync(CancellationToken ct) => ValueTask.CompletedTask;

    /// <summary>
    /// 单周期异常钩子（默认 LogWarning，子类可 override 做重试/告警/计数）。
    /// <para>★ 对齐 Compactor 现状：单 lease 失败 NotifyFailed 不杀 worker。</para>
    /// <para>★ <see cref="OperationCanceledException"/> 不进此钩子（视为正常退出）。</para>
    /// </summary>
    protected virtual void OnCycleError(Exception ex)
        => _logger?.LogWarning(ex, "{WorkerName} 单周期异常（worker 继续运行）", _name);

    /// <summary>单周期完成耗时钩子（默认 >10ms 时 LogDebug；子类可 override 上报到 ObservabilityHub Histogram）。
    /// <para>★ 慢循环是后台 worker 最常见的性能问题——override 本方法接 <c>ObservabilityHub.Metrics.Histogram</c> 即默认可观测。</para></summary>
    /// <param name="elapsedMicros">本周期耗时（微秒，含 await 等待时间）。</param>
    protected virtual void OnCycleCompleted(long elapsedMicros)
    {
        if (elapsedMicros > 10_000)   // >10ms 才记，避免日志洪水
            _logger?.LogDebug("{WorkerName} 周期耗时 {elapsedUs}μs", _name, elapsedMicros);
    }

    // ════════════════════════════════════════════════════════════
    // === 生命周期（final 模板方法，子类不 override）===
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 启动 worker（幂等——重复调只首次生效）。
    /// <para>★ 按 <see cref="_scheduler"/> 分流执行器（null=公共池 Task.Run / 非 null=注入调度器）；
    ///   <see cref="_consumerCount"/> 个执行体共跑同一循环。</para>
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(!_cts.TryReset(), _name);  // 复用 worker 时，Stop 后 TryReset 失败 = 已 Dispose
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;  // ★ 已启动幂等返回
        if (_cts.IsCancellationRequested)
        {
            // ★ CORE-19：TryReset 与 CAS 之间插入 Stop——"启动即死"防护（幂等重试收敛）
            Interlocked.Exchange(ref _running, 0);
            return;
        }

        // ★ CORE-19：旧消费者必须全部退出才重启——否则旧退出者的 Increment 命中新计数 →
        //   OnLoopExitAsync 在新一轮运行期间重复执行（drain/flush 钩子双跑）。
        //   有界等待（5s）+ 警告（异常场景不阻塞调用方）。
        // ★ 仅重启路径等待：首次启动无旧消费者（_exitedConsumers 恒 0 ≠ N），等待 = 每引擎白等 5s。
        if (Volatile.Read(ref _everStarted) != 0)
        {
            var deadline = Environment.TickCount64 + 5000;
            while (Volatile.Read(ref _exitedConsumers) != _consumerCount)
            {
                if (Environment.TickCount64 > deadline)
                {
                    _logger?.LogWarning("{WorkerName} 重启时旧消费者未在 5s 内全部退出（{Exited}/{N}）——OnLoopExitAsync 可能重复执行",
                        _name, Volatile.Read(ref _exitedConsumers), _consumerCount);
                    break;
                }
                Thread.Yield();
            }

            Volatile.Write(ref _exitedConsumers, 0);
        }
        Volatile.Write(ref _everStarted, 1);
        OnLoopStart();   // ★ 启动一次（多消费者下不重复；在调用方线程执行）

        // ★ 配置可见（治"卡死无人知因"）：启动时打一行有效配置；N≫M 给出调参指引
        if (_scheduler is IsolatedTaskScheduler its)
        {
            if (_consumerCount > its.ThreadCount)
                _logger?.LogWarning("{WorkerName} consumerCount={N} 超过调度器线程数 M={M}——N≫M 只增内存/调度开销不增吞吐（建议 N≈M）",
                    _name, _consumerCount, its.ThreadCount);
            _logger?.LogInformation("{WorkerName} 启动：scheduler={Scheduler}(M={M}) consumerCount={N}",
                _name, its.Name, its.ThreadCount, _consumerCount);
        }
        else
        {
            _logger?.LogInformation("{WorkerName} 启动：scheduler=公共池(Task.Run) consumerCount={N}", _name, _consumerCount);
        }

        _loopTasks = new Task[_consumerCount];
        if (_scheduler is { } sched)
        {
            // ★ 注入调度器（如 IsolatedTaskScheduler.Shared）：消费者 Task 跑其私有线程，await continuation 回流该调度器。
            //   线程数 M 由调度器定（§7 校验），与 consumerCount 解耦——不再随 N 开线程（治"巨大 N 线程爆炸卡死"）。
            for (var i = 0; i < _consumerCount; i++)
            {
                _loopTasks[i] = Task.Factory.StartNew(
                        RunLoopCore,
                        CancellationToken.None,
                        TaskCreationOptions.DenyChildAttach,
                        sched)
                    .Unwrap();
            }
        }
        else
        {
            // 公共线程池（低频 worker，如 CpuSampler/MetaFlusher）
            for (var i = 0; i < _consumerCount; i++)
                _loopTasks[i] = Task.Run(RunLoopCore);
        }
    }

    /// <summary>
    /// 停止 worker（设标志 + cts.Cancel 唤醒在 ct 上等待的循环）。
    /// <para>★ 不等待退出——调用 <see cref="WaitForExit"/> / <see cref="Dispose"/> 才等。</para>
    /// <para>★ 幂等：重复调无副作用。</para>
    /// </summary>
    public void Stop()
    {
        Volatile.Write(ref _running, 0);
        try { _cts.Cancel(); } catch (ObjectDisposedException) { /* Dispose 竞态，忽略 */ }
    }

    /// <summary>
    /// 同步等待所有消费者退出（带 <see cref="_exitTimeout"/> 超时/消费者，超时仅 LogWarning 不抛）。
    /// <para>⚠️ 禁止在异步上下文调用（同步阻塞 task 在同步上下文死锁）。异步用 <see cref="WaitForExitAsync"/>。</para>
    /// </summary>
    public void WaitForExit()
    {
        var tasks = _loopTasks;
        if (tasks is null) return;
        foreach (var t in tasks)
        {
            if (t is null) continue;
            try
            {
#pragma warning disable TCSG031 // 设计必需：Stop/等待退出必须同步（Dispose 路径，有界超时）
                if (!t.Wait(_exitTimeout)) _logger?.LogWarning("{WorkerName} 等待退出超时 {timeout}", _name, _exitTimeout);
#pragma warning restore TCSG031
            }
            catch { /* 吞 worker 内异常（异常已在 OnCycleError 处理）+ OCE */ }
        }
    }

    /// <summary>异步等待所有消费者退出（带超时，超时仅 LogWarning 不抛）。</summary>
    public async Task WaitForExitAsync()
    {
        var tasks = _loopTasks;
        if (tasks is null) return;
        var live = tasks.Where(static t => t is not null).ToArray();
        if (live.Length == 0) return;
        try { await Task.WhenAll(live).WaitAsync(_exitTimeout).ConfigureAwait(false); }
        catch (TimeoutException) { _logger?.LogWarning("{WorkerName} 等待退出超时 {timeout}", _name, _exitTimeout); }
        catch { /* 吞 worker 内异常 + OCE */ }
    }

    /// <summary>
    /// 释放——CAS 防双 Dispose + Stop + WaitForExit + cts.Dispose。
    /// <para>★ <see cref="LifecycleBase{THints}"/> 内建持有时由基类编排调用顺序（见 worker-loop-unified-design.md §4）。</para>
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;
        Stop();
        WaitForExit();
        // ★ 不释放注入的调度器——归注入方管（Shared 进程级 / 引擎 own 经 Resources 释放）
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>异步释放——同 <see cref="Dispose"/> 但用 <see cref="WaitForExitAsync"/>（不阻塞调用线程）。</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;
        Stop();
        await WaitForExitAsync().ConfigureAwait(false);
        // ★ 不释放注入的调度器——归注入方管（Shared 进程级 / 引擎 own 经 Resources 释放）
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }

    // ════════════════════════════════════════════════════════════
    // === 循环主体（模板，异常隔离）===
    // ════════════════════════════════════════════════════════════

    private async Task RunLoopCore()
    {
        var ct = _cts.Token;
        try
        {
            while (Volatile.Read(ref _running) != 0 && !ct.IsCancellationRequested)
            {
                try
                {
                    var timer = MicroTimer.Start(active: _logger is not null);   // 有 logger 才计时（零分配 struct，无 logger 时 JIT 消除）
                    var cont = await RunOneCycleAsync(ct).ConfigureAwait(false);
                    if (timer.IsActive)
                        OnCycleCompleted(timer.ElapsedMicros());
                    if (!cont) break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }  // Stop 触发的取消=正常退出
                catch (Exception ex) { OnCycleError(ex); }  // ★ 单周期异常不杀 worker
            }
        }
        finally
        {
            // ★ 末位退出的消费者跑 OnLoopExitAsync（多消费者下只执行一次）
            if (Interlocked.Increment(ref _exitedConsumers) == _consumerCount)
            {
                try { await OnLoopExitAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { _logger?.LogWarning(ex, "{WorkerName} OnLoopExitAsync 异常", _name); }
            }
        }
    }
}

/// <summary>
/// 事件驱动 + 队列的后台循环骨架（第二层——内建 5 档优先级队列）。
/// <para>★ 继承 <see cref="BackgroundWorkerLoop"/>（循环骨架 + 启停 Dispose + <see cref="BackgroundWorkerLoop.ConsumerCount">多消费者</see>），追加：
///   内建 <see cref="BucketPriorityQueue{WorkerPriority, T}"/> + 统一 <see cref="Enqueue"/> 入队 +
///   abstract <see cref="ProcessItemAsync"/> + virtual <see cref="RunOneCycleAsync(CancellationToken)"/>（出队分发）。</para>
/// <para>★ <b>开箱即用</b>（标准后台任务模式）：子类只实现 <see cref="ProcessItemAsync"/>，
///   基类默认 <see cref="RunOneCycleAsync(CancellationToken)"/> 从队列出队分发。入队统一 <see cref="Enqueue"/>。</para>
/// <para>★ <b>多消费者</b>：构造传 <c>consumerCount &gt; 1</c>，N 个消费者共 drain 同一优先级队列（counting-semaphore
///   逐项公平唤醒，见 <see cref="BucketPriorityQueue{TPriority,T}.DequeueAsync"/>）。</para>
/// <para>★ <b>需要重写</b>：子类 override <see cref="RunOneCycleAsync(CancellationToken)"/> 自定义循环逻辑
///   （如 Lifecycle 的 fire-and-forget 并发内核）。内建队列仍在。</para>
/// </summary>
/// <typeparam name="T">队列元素类型。</typeparam>
public abstract class BackgroundWorkerLoop<T> : BackgroundWorkerLoop
{
    // === 内建队列（5 档优先级，开箱即用）===
    private readonly BucketPriorityQueue<WorkerPriority, T> _queue = new();

    /// <summary>构造——转发基类（执行器/启停/Dispose/多消费者全继承）。</summary>
    protected BackgroundWorkerLoop(
        TaskScheduler? scheduler = null,
        int consumerCount = 1,
        string? name = null,
        TimeSpan? exitTimeout = null,
        ILogger? logger = null)
        : base(scheduler, consumerCount, name, exitTimeout, logger) { }

    // ════════════════════════════════════════════════════════════
    // === 入队（统一入口，生产者调）===
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 入队——统一入口，所有生产者经此方法。
    /// <para>★ 内建 <see cref="BucketPriorityQueue{WorkerPriority, T}"/> 无锁入队 + 异步唤醒等待的消费者。</para>
    /// <para>★ 默认优先级 <see cref="WorkerPriority.Normal"/>（多数场景不关心优先级 = FIFO）。</para>
    /// </summary>
    public void Enqueue(T item, WorkerPriority priority = WorkerPriority.Normal)
        => _queue.Enqueue(item, priority);

    /// <summary>队列近似元素数（并发下非精确，诊断用）。</summary>
    public int QueueCount => _queue.Count;

    /// <summary>内建队列（子类 override RunOneCycleAsync 时可直接访问）。</summary>
    protected BucketPriorityQueue<WorkerPriority, T> Queue => _queue;

    // ════════════════════════════════════════════════════════════
    // === 子类钩子 ===
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// ★ 处理一个出队元素（子类核心实现——标准后台任务模式只需实现此方法）。
    /// <para>★ 基类默认 <see cref="RunOneCycleAsync(CancellationToken)"/> 从内建队列出队后调此方法。</para>
    /// <para>★ 抛异常不杀 worker——异常走 <see cref="OnCycleError"/> 钩子。</para>
    /// </summary>
    protected abstract ValueTask ProcessItemAsync(T item, CancellationToken ct);

    /// <summary>
    /// ★ 一个工作周期——override 基类 abstract，提供 virtual 默认实现：从内建队列出队 → 调 <see cref="ProcessItemAsync"/>。
    /// <para>★ <b>开箱即用</b>：子类不 override 时，自动从队列取任务分发。标准后台任务模式。</para>
    /// <para>★ <b>多消费者</b>：<c>consumerCount &gt; 1</c> 时多个执行体并发调本方法，各自出队分发（<see cref="BucketPriorityQueue{TPriority,T}"/> counting-semaphore 保证逐项公平）。</para>
    /// <para>★ <b>需要重写</b>：子类 override 自定义循环逻辑（如 Lifecycle 的 fire-and-forget
    ///   并发内核 + in-flight 限流）。内建队列仍在，仍可用 <see cref="Enqueue"/>。</para>
    /// <para>★ 返回 false 则停止循环（如 EOF / 显式终止条件）。</para>
    /// </summary>
    protected override async ValueTask<bool> RunOneCycleAsync(CancellationToken ct)
    {
        // ★ 默认实现：从内建队列异步出队 → 分发给 ProcessItemAsync
        var item = await _queue.DequeueAsync(ct).ConfigureAwait(false);
        await ProcessItemAsync(item, ct).ConfigureAwait(false);
        return true;   // 继续循环
    }
}
