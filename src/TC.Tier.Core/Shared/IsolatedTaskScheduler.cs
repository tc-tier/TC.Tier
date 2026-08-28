using System.Collections.Concurrent;

namespace TC.Tier.Core.Shared;

/// <summary>
/// 隔离线程 TaskScheduler——在 M 个<b>私有</b>线程上执行任务，与公共线程池（<see cref="TaskScheduler.Default"/>）完全隔离。
/// <para>★ 用途：高频 / 关键路径的 worker（如引擎建段 worker）需要线程隔离——其异步 continuation 不与公共池上的
///   其它任务（写者、其它池工作）争抢池线程，避免饥饿/抖动。</para>
/// <para>★ <b>真·异步友好</b>：任务在私有线程上执行时 <c>TaskScheduler.Current</c> = 本调度器，任务内 <c>await</c> 的
///   continuation 自动回到本调度器的私有线程（不经公共池）——既得「专用线程」隔离，又无 <c>GetAwaiter().GetResult()</c>
///   sync-over-async（高频路径禁区）。</para>
/// <para>★ <b>阻塞等待模型</b>：私有线程在 <see cref="BlockingCollection{T}"/> 上阻塞等任务（空闲不占 CPU）。</para>
/// <para>★ <b>稀缺资源·受控创建</b>：每实例开 M 个真实 OS 线程（栈 ~1MB/线程）。<b>禁止</b>直接 <c>new</c>——ctor 为
///   <c>internal</c>，经 <see cref="Shared"/>（进程级全局单例）或 <see cref="Create"/>（工厂 + 防扩散护栏）获取，
///   使用指南见 §2（使用指南：docs/dedicated-task-scheduler.md，文档站）（选型/旋钮/注意事项/故障排查）。</para>
/// <para>★ 释放：调用方须在所有任务结束后（worker 的 WaitForExit 之后）调 <see cref="Dispose"/>——
///   <see cref="BlockingCollection{T}.CompleteAdding"/> + Join 线程。</para>
/// </summary>
public sealed class IsolatedTaskScheduler : TaskScheduler, IDisposable
{
    // ════════════════════════════════════════════════════════════
    //  实例管理（§2.3）：防扩散护栏
    // ════════════════════════════════════════════════════════════

    /// <summary>进程内独立实例数超过此阈值 → WARN（防滥用，引导用 <see cref="Shared"/>）。</summary>
    private const int ProliferationWarnThreshold = 4;

    // === 全局共享单例（§2.3）——进程级，不注册 InstanceTracker（A2：进程意图资源，非泄漏）===
    private static readonly Lazy<IsolatedTaskScheduler> SharedLazy = new(
        static () => new IsolatedTaskScheduler(
            new IsolatedSchedulerOptions { Name = "isolated-shared" }, track: false),
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// 进程级全局共享单例（§2.3）。<see cref="RecommendedThreadCount"/> 线程，全默认 options。
    /// <para>★ 常见 / 单引擎场景直接用——所有走专用模式的 worker 共用这一个，进程级线程数固定。</para>
    /// <para>★ 调用方<b>不 Dispose</b>（进程生命期；私有线程 <see cref="Thread.IsBackground"/>=true 不挡进程退出）。</para>
    /// </summary>
    public static IsolatedTaskScheduler Shared => SharedLazy.Value;

    /// <summary>推荐线程数 = <c>Clamp(ProcessorCount, 2, 4)</c>（§7：保守，为业务/写者留核）。</summary>
    public static int RecommendedThreadCount => Math.Clamp(Environment.ProcessorCount, 2, 4);

    /// <summary>
    /// 创建独立实例（需<b>分区隔离</b>时用，§2.3）——多引擎互不干扰时每引擎 own 一个，随 <c>Resources</c> 释放。
    /// <para>★ 注册 <see cref="InstanceTracker"/>（泄漏/扩散可见）+ 防扩散 WARN（实例数超阈值）。</para>
    /// <para>★ 校验 <see cref="IsolatedSchedulerOptions.ThreadCount"/>（超核 throw、过半核 WARN，§7）。</para>
    /// </summary>
    public static IsolatedTaskScheduler Create(IsolatedSchedulerOptions? options = null)
    {
        var opts = options ?? new IsolatedSchedulerOptions();
        var scheduler = new IsolatedTaskScheduler(opts, track: true);   // ctor 内注册 InstanceTracker
        var alive = InstanceTracker.GetAlive(nameof(IsolatedTaskScheduler)).Count;
        if (alive > ProliferationWarnThreshold)
            opts.Logger?.LogWarning(
                "IsolatedTaskScheduler 实例数={Alive}（>{Threshold}），疑似滥用——考虑用 Shared",
                alive, ProliferationWarnThreshold);
        return scheduler;
    }

    // ════════════════════════════════════════════════════════════
    //  实例状态
    // ════════════════════════════════════════════════════════════

    private readonly IsolatedSchedulerOptions _options;
    private readonly string _name;
    private readonly ILogger? _logger;
    private readonly ObservabilityHub _hub;          // 指标 sink（默认 Disabled，零开销）
    private readonly KeyValuePair<string, string>[] _nameTag;  // 预构造指标 tag（避免每次入队分配）
    private readonly BlockingCollection<Task> _tasks;
    private readonly Thread[] _threads;
    private readonly bool _bounded;        // true=有界（满则阻塞背压）；false=无界
    private readonly int _capacity;        // 有界容量；无界时为 -1
    private readonly bool _tracked;        // true=注册了 InstanceTracker（Create），Dispose 时注销

    // === per-thread 状态（watchdog 用，§5）：私有线程写、watchdog 读，无锁（Volatile） ===
    private readonly long[] _lastDequeueTicks;   // 最近取任务时间（Environment.TickCount64）
    private readonly long[] _taskStartTicks;     // 当前任务开始时间
    private readonly int[] _threadState;          // StateIdle / StateExecuting / StateDead
    // === watchdog（§5）===
    private readonly Timer? _watchdog;            // 公共池 Timer（不占 M 私有线程）；interval<=0 时不建
    private readonly long[] _watchPrevDequeue;   // 上次 tick 的 lastDequeue 快照（判推进）
    private int _noProgressStreak;                // 连续无推进 tick 数（判死锁）
    private readonly long _taskTimeoutMs;
    private readonly int _deadlockConfirmTicks;
    // === 死亡重启（§4 / L4）===
    private readonly SchedulerRestartPolicy _restartPolicy;
    private readonly int[] _restartCount;         // 每槽位重启次数（RestartOnce 用）
    private int _disposed;

    /// <summary>测试专用：设为某 idx 使该线程下次取任务时退出（finally→Dead），验证死亡重启路径。生产恒为 -1。</summary>
    internal int _testForceExitIdx = -1;

    /// <summary>
    /// 私有线程状态（§5-B1/B2）：Idle=空闲、Executing=执行任务、Dead=异常死亡。
    /// </summary>
    private const int StateIdle = 0;
    private const int StateExecuting = 1;
    private const int StateDead = 2;

    /// <summary>
    /// 主构造（internal——经 <see cref="Shared"/>/<see cref="Create"/> 进入，禁止外部裸 new）。
    /// </summary>
    /// <param name="options">构造选项（线程数 / 队列策略 / 诊断名 / 日志）。</param>
    /// <param name="track">是否注册 InstanceTracker（Shared=false 进程意图资源；Create=true）。</param>
    internal IsolatedTaskScheduler(IsolatedSchedulerOptions options, bool track = true)
    {
        ArgumentNullException.ThrowIfNull(options);
        var m = options.ThreadCount;
        // §7 线程数校验
        if (m < 1)
            throw new ArgumentOutOfRangeException(nameof(options) + "." + nameof(options.ThreadCount), m, "ThreadCount 必须 >= 1");
        if (m > Environment.ProcessorCount)
            throw new ArgumentOutOfRangeException(nameof(options) + "." + nameof(options.ThreadCount), m,
                $"ThreadCount={m} 超过 ProcessorCount={Environment.ProcessorCount}（cooperative async 下超核纯增上下文切换，无益）");
        if (m > Environment.ProcessorCount / 2)
            options.Logger?.LogWarning(
                "IsolatedTaskScheduler ThreadCount={M} 超过 ProcessorCount/2={Half}，专用线程占核过半，与写者叠加大概率超订致不稳定",
                m, Environment.ProcessorCount / 2);

        _options = options;
        _name = string.IsNullOrEmpty(options.Name) ? nameof(IsolatedTaskScheduler) : options.Name;
        _logger = options.Logger;
        _hub = options.Hub ?? ObservabilityHub.Disabled;
        _nameTag = [new KeyValuePair<string, string>("name", _name)];
        _tracked = track;

        // §3.3 队列策略：QueueCapacity 0=自动有界(max(M*4,16))；>0=指定有界；<0=无界
        var cap = options.QueueCapacity;
        if (cap < 0)
        {
            _bounded = false;
            _capacity = -1;
            _tasks = new BlockingCollection<Task>();
        }
        else
        {
            _bounded = true;
            _capacity = cap == 0 ? Math.Max(m * 4, 16) : cap;   // cap ≫ M：缓解私有线程给自己续体排队时的自死锁（§3.3）
            _tasks = new BlockingCollection<Task>(_capacity);
        }

        _threads = new Thread[m];
        _lastDequeueTicks = new long[m];
        _taskStartTicks = new long[m];
        _threadState = new int[m];
        _watchPrevDequeue = new long[m];
        _restartCount = new int[m];
        for (var i = 0; i < m; i++)
        {
            var idx = i;   // ★ 闭包捕获副本
            _threads[i] = new Thread(() => ThreadLoop(idx))
            {
                IsBackground = true,
                Name = m == 1 ? _name : $"{_name}-{i}"
            };
        }
        foreach (var t in _threads)
            t.Start();

        // watchdog（§5）：interval>0 才起；跑在公共池 Timer 上（不占 M 私有线程，保证独立——被看的不能在看病的人身上）
        _taskTimeoutMs = (long)options.TaskTimeout.TotalMilliseconds;
        _deadlockConfirmTicks = options.DeadlockConfirmTicks;
        _restartPolicy = options.RestartPolicy;
        var intervalMs = (long)options.WatchdogInterval.TotalMilliseconds;
        if (intervalMs > 0)
            _watchdog = new Timer(WatchdogTick, null, intervalMs, intervalMs);

        if (track)
            InstanceTracker.Register(this, nameof(IsolatedTaskScheduler));
    }

    /// <summary>
    /// 便捷构造（internal）——向后兼容 <see cref="BackgroundWorkerLoop"/> 现有调用 + 测试便利。
    /// 默认自动有界队列（<c>max(M*4,16)</c>）、注册 InstanceTracker。
    /// </summary>
    /// <param name="threadCount">专用线程数 M（并发度上限）。</param>
    /// <param name="name">诊断名前缀（线程名 / 日志 / 指标 tag）。默认 "isolated"。</param>
    internal IsolatedTaskScheduler(int threadCount, string? name = null)
        : this(new IsolatedSchedulerOptions { ThreadCount = threadCount, Name = name ?? "isolated" }) { }

    // ════════════════════════════════════════════════════════════
    //  诊断属性
    // ════════════════════════════════════════════════════════════

    /// <summary>专用线程数 M（构造时定）。</summary>
    public int ThreadCount => _threads.Length;
    /// <summary>当前任务队列深度（并发下近似，诊断用）。</summary>
    public int QueueDepth => _tasks.Count;
    /// <summary>是否有界队列（true=满则阻塞背压；false=无界）。</summary>
    public bool IsBounded => _bounded;
    /// <summary>队列容量（<see cref="IsBounded"/>=false 时为 -1）。</summary>
    public int QueueCapacity => _capacity;
    /// <summary>诊断名（线程名前缀 / 日志标识）。</summary>
    public string Name => _name;

    // ════════════════════════════════════════════════════════════
    //  TaskScheduler 实现
    // ════════════════════════════════════════════════════════════

    /// <summary>私有线程主循环——阻塞取任务 → 本线程执行（TaskScheduler.Current=本调度器，await continuation 回流）。</summary>
    /// <param name="idx">线程槽位索引（0~M-1）。</param>
    private void ThreadLoop(int idx)
    {
        var metrics = _hub.Metrics;   // ★ 闭包提升到循环外：IsEnabled 一次读
        try
        {
            foreach (var task in _tasks.GetConsumingEnumerable())
            {
                if (idx == Volatile.Read(ref _testForceExitIdx))
                {
                    Volatile.Write(ref _testForceExitIdx, -1);   // 清除，使重启后的替换线程不再退出
                    return;   // 测试专用：模拟线程死亡 → finally 标 Dead → watchdog 重启
                }

                var nowTicks = Environment.TickCount64;
                Volatile.Write(ref _lastDequeueTicks[idx], nowTicks);   // watchdog 判推进用
                Volatile.Write(ref _taskStartTicks[idx], nowTicks);
                Volatile.Write(ref _threadState[idx], StateExecuting);

                // ★ TryExecuteTask 在本私有线程上跑任务——await continuation 经 QueueTask 回到本调度器。
                //   任务抛异常存于 Task（调用方观察），不杀私有线程。
                var timing = metrics.IsEnabled ? MicroTimer.Start() : default;   // 指标关→default，零开销
                try
                {
                    TryExecuteTask(task);
                }
                finally
                {
                    Volatile.Write(ref _threadState[idx], StateIdle);
                }

                if (!timing.IsActive) continue;
                metrics.Counter("scheduler.task.executed", _nameTag);
                metrics.Histogram("scheduler.task.exec_us", timing.ElapsedMicros(), _nameTag);
                metrics.Gauge("scheduler.queue.depth", _tasks.Count, _nameTag);
            }
        }
        finally
        {
            Volatile.Write(ref _threadState[idx], StateDead);   // ★ 线程退出（含异常死亡）→ 标 Dead，watchdog 捕获
        }
    }

    /// <summary>
    /// 入队任务到私有线程消费。
    /// <para>★ 有界时 <see cref="BlockingCollection{T}.Add(T)"/> 在队列满时<b>阻塞调用线程</b>（真生产者背压，§3.3）；
    ///   无界时永不阻塞。</para>
    /// <para>★ <see cref="TaskScheduler.QueueTask"/> 无法"拒绝/转交"（Task 执行体内部），故有界语义只能是阻塞背压。</para>
    /// <para>★ <see cref="BlockingCollection{T}.CompleteAdding"/> 后入队（Dispose 竞态/超时遗留 continuation）：吞
    ///   <see cref="InvalidOperationException"/>，避免抛到入队线程（此时 worker 已停，任务为孤儿）。</para>
    /// </summary>
    /// <param name="task">入队任务。</param>
    protected override void QueueTask(Task task)
    {
        var metrics = _hub.Metrics;
        var timing = metrics.IsEnabled ? MicroTimer.Start() : default;   // 指标关→default，零开销
        try
        {
            _tasks.Add(task);
        }
        catch (InvalidOperationException) { return; }   // CompleteAdding 后丢弃孤儿 Task
        if (!timing.IsActive) return;
        metrics.Counter("scheduler.task.enqueued", _nameTag);
        var blockedMicros = timing.ElapsedMicros();   // Add 阻塞耗时（有界满时 = 背压等待）
        if (blockedMicros > 1_000)   // >1ms 视为被背压挡住
        {
            metrics.Counter("scheduler.queue.full", _nameTag);
            _logger?.LogWarning("IsolatedTaskScheduler {Name} 队列满，生产者被阻塞 {BlockedMicros}μs", _name, blockedMicros);
        }
    }

    /// <summary>不支持出队——任务一旦入队即在私有线程执行（cancellation 由任务内部 ct 处理）。</summary>
    /// <param name="task">任务。</param>
    protected override bool TryDequeue(Task task) => false;

    /// <summary>不内联——所有任务经队列到私有线程执行（保证线程隔离 + 不在调用线程跑 worker 逻辑）。</summary>
    /// <param name="task">任务。</param>
    /// <param name="taskWasPreviouslyQueued">是否已入队（无用）。</param>
    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

    /// <summary>诊断快照（调试器用）。</summary>
    /// <returns>当前队列任务快照（并发下近似）。</returns>
    protected override IEnumerable<Task> GetScheduledTasks() => _tasks;

    // ════════════════════════════════════════════════════════════
    //  watchdog（§5）：慢任务 / 疑似死锁检测（跑在公共池 Timer 上，独立于 M 私有线程）
    // ════════════════════════════════════════════════════════════

    private void WatchdogTick(object? _)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var metricsEnabled = _hub.Metrics.IsEnabled;
        var now = Environment.TickCount64;
        var slow = 0;
        var anyProgress = false;

        for (var i = 0; i < _threadState.Length; i++)
        {
            var st = Volatile.Read(ref _threadState[i]);
            if (st == StateDead)
            {
                // ★ 异常死亡（Dispose 时 watchdog 已先停，此处见 Dead 必为运行期死亡）→ 按策略重启（§4/L4）
                // CAS Dead→Idle：保证仅一个 tick 执行重启（防 Timer 回调重叠双重启）
                if (Interlocked.CompareExchange(ref _threadState[i], StateIdle, StateDead) != StateDead) continue;
                RestartThread(i, metricsEnabled);
                continue;
            }

            // 慢任务（§5-B2）：Executing 且超过 taskTimeout
            if (st == StateExecuting)
            {
                var age = now - Volatile.Read(ref _taskStartTicks[i]);
                if (age > _taskTimeoutMs)
                {
                    slow++;
                    if (metricsEnabled) _hub.Metrics.Counter("scheduler.task.slow", _nameTag);
                    _logger?.LogWarning("IsolatedTaskScheduler {Name} 线程 {Thread} 任务已执行 {AgeMs}ms 超过 taskTimeout={TimeoutMs}ms（疑似慢任务/霸占）",
                        _name, _threads[i].Name, age, _taskTimeoutMs);
                }
            }

            // 推进检测（§5-B1）：lastDequeue 变化即有前进
            var cur = Volatile.Read(ref _lastDequeueTicks[i]);
            if (cur != Volatile.Read(ref _watchPrevDequeue[i]))
            {
                anyProgress = true;
                Volatile.Write(ref _watchPrevDequeue[i], cur);
            }
        }

        // 疑似死锁（§5-B1，保守启发式·可能误报 A4）：≥2 线程慢 + 连续 confirmTicks 无推进
        if (slow >= 2)
        {
            if (anyProgress)
            {
                Interlocked.Exchange(ref _noProgressStreak, 0);
            }
            else if (Interlocked.Increment(ref _noProgressStreak) >= _deadlockConfirmTicks)
            {
                if (metricsEnabled) _hub.Metrics.Counter("scheduler.deadlock.suspected", _nameTag);
                _logger?.LogError("IsolatedTaskScheduler {Name} 疑似死锁：{Slow} 线程慢 + 连续 {Streak} tick 无推进（保守启发式·无法自动解开，需人工介入）",
                    _name, slow, _deadlockConfirmTicks);
                DumpDiagnostics("deadlock-suspected");
                Interlocked.Exchange(ref _noProgressStreak, 0);   // 一次告警后重置，避免每 tick 重复 ERROR
            }
        }
        else
        {
            Interlocked.Exchange(ref _noProgressStreak, 0);
        }
    }

    /// <summary>
    /// 重启死亡线程（§4 / L4）：按 <see cref="_restartPolicy"/> 决定起新线程替换或标记降级。
    /// <para>★ 仅 watchdog 调用，已 CAS 占槽位（Dead→Idle）。</para>
    /// <para>⚠️ 重启只服务<b>未来</b>任务——死亡时在飞的 Task continuation 链已断，救不回（A3，靠 worker lease 兜底）。</para>
    /// </summary>
    private void RestartThread(int idx, bool metricsEnabled)
    {
        var policy = _restartPolicy;
        var canRestart = policy == SchedulerRestartPolicy.Always
            || (policy == SchedulerRestartPolicy.RestartOnce && Volatile.Read(ref _restartCount[idx]) == 0);
        var oldName = _threads[idx].Name;
        if (!canRestart)
        {
            if (metricsEnabled) _hub.Metrics.Counter("scheduler.threads.degraded", _nameTag);
            _logger?.LogError("IsolatedTaskScheduler {Name} 线程 {Thread} 死亡且不再重启（policy={Policy}），调度器降级", _name, oldName, policy);
            return;
        }
        var repl = new Thread(() => ThreadLoop(idx))
        {
            IsBackground = true,
            Name = _threads.Length == 1 ? _name : $"{_name}-{idx}"
        };
        _threads[idx] = repl;
        Interlocked.Increment(ref _restartCount[idx]);
        if (metricsEnabled) _hub.Metrics.Counter("scheduler.threads.restarted", _nameTag);
        _logger?.LogWarning("IsolatedTaskScheduler {Name} 线程 {Old} 死亡，已重启为 {New}", _name, oldName, repl.Name);
        repl.Start();
    }

    /// <summary>诊断转储（死锁触发）——快照队列深度 + 各线程状态，写 ERROR 日志（§6）。</summary>
    private void DumpDiagnostics(string reason)
    {
        if (_logger is null) return;
        var parts = new string[_threadState.Length];
        for (var i = 0; i < _threadState.Length; i++)
        {
            var st = Volatile.Read(ref _threadState[i]);
            parts[i] = $"{_threads[i].Name}={(st == StateIdle ? "idle" : st == StateExecuting ? "exec" : "dead")}";
        }
        _logger.LogError("IsolatedTaskScheduler {Name} 诊断[{Reason}] queueDepth={Depth} threads: {Threads}",
            _name, reason, _tasks.Count, string.Join(" ", parts));
    }

    // ════════════════════════════════════════════════════════════
    //  释放
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 释放——通知线程退出（<see cref="BlockingCollection{T}.CompleteAdding"/>）+ Join。
    /// <para>★ 调用方须先保证所有任务已结束（worker 经 Stop+WaitForExit 后再 Dispose 本调度器）。</para>
    /// <para>★ <see cref="Shared"/> 不应被 Dispose（进程意图资源）；仅 <see cref="Create"/> 的实例随 owner 释放。</para>
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;
        _watchdog?.Dispose();   // 先停 watchdog（不再有新 tick），再 CompleteAdding/Join
        _tasks.CompleteAdding();
        foreach (var t in _threads)
        {
            try { t.Join(); }
            catch { /* 吞——Dispose 不抛 */ }
        }
        _tasks.Dispose();
        if (_tracked)
            InstanceTracker.Unregister(this);
    }
}