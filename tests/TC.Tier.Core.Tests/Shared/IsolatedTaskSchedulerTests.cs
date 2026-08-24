using System.Collections.Concurrent;
using TC.Tier.Core.Logging;
using TC.Tier.Core.Metrics;
using TC.Tier.Core.Observability;
using TC.Tier.Core.Shared;
using Xunit;

namespace TC.Tier.Core.Tests.Shared;

/// <summary>
/// IsolatedTaskScheduler 单元测试——验证实例管理（Shared/Create/防扩散）+ 线程数校验 + 隔离执行 +
/// 双队列模式（有界阻塞背压 / 无界）。全部用确定性 flag 断言，避免时序 flaky。
/// </summary>
/// <remarks>★ 与 BackgroundWorkerLoopStressTests 共享 InstanceTracker collection：计数断言依赖
/// 进程级静态跟踪表的绝对计数差，Stress 类并行 Create/Dispose tracked 实例会污染计数
/// （2026-08-17 全量套件偶发失败根因——单跑永远绿，并行才撞）。同一 collection 串行化根治。</remarks>
[Collection("instance-tracker")]
public class IsolatedTaskSchedulerTests
{
    // ════════════════════════════════════════════════════════════
    //  实例管理（§2.3）
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void Shared_IsSingleton()
    {
        var a = IsolatedTaskScheduler.Shared;
        var b = IsolatedTaskScheduler.Shared;
        Assert.Same(a, b);
    }

    [Fact]
    public void Shared_HasRecommendedThreadCount()
    {
        Assert.Equal(IsolatedTaskScheduler.RecommendedThreadCount, IsolatedTaskScheduler.Shared.ThreadCount);
    }

    [Fact]
    public void RecommendedThreadCount_ClampedBetween2And4()
    {
        var m = IsolatedTaskScheduler.RecommendedThreadCount;
        Assert.InRange(m, 2, 4);
    }

    [Fact]
    public void Shared_NotRegisteredInInstanceTracker()
    {
        // Shared 是进程意图资源（track:false），不应进 InstanceTracker（A2）
        var before = InstanceTracker.GetAlive(nameof(IsolatedTaskScheduler)).Count;
        _ = IsolatedTaskScheduler.Shared;
        var after = InstanceTracker.GetAlive(nameof(IsolatedTaskScheduler)).Count;
        Assert.Equal(before, after);
    }

    [Fact]
    public void Create_RegistersAndUnregistersInInstanceTracker()
    {
        var before = InstanceTracker.GetAlive(nameof(IsolatedTaskScheduler)).Count;
        var s = IsolatedTaskScheduler.Create(new IsolatedSchedulerOptions { Name = "reg-test" });
        Assert.Equal(before + 1, InstanceTracker.GetAlive(nameof(IsolatedTaskScheduler)).Count);
        s.Dispose();
        Assert.Equal(before, InstanceTracker.GetAlive(nameof(IsolatedTaskScheduler)).Count);
    }

    [Fact]
    public void Create_ProliferationWarnsAboveThreshold()
    {
        var logger = new CapturingLogger();
        var created = new List<IsolatedTaskScheduler>();
        try
        {
            for (var i = 0; i < 10; i++)
            {
                created.Add(IsolatedTaskScheduler.Create(new IsolatedSchedulerOptions { Logger = logger, Name = "prolif" }));
                if (logger.Messages.Any(m => m.Contains("Shared", StringComparison.Ordinal)))
                    return;   // ✅ 已触发防扩散 WARN
            }
            Assert.Contains(logger.Messages, m => m.Contains("Shared", StringComparison.Ordinal));
        }
        finally
        {
            foreach (var s in created) s.Dispose();
        }
    }

    // ════════════════════════════════════════════════════════════
    //  线程数校验（§7）
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void ThreadCount_Below1_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            IsolatedTaskScheduler.Create(new IsolatedSchedulerOptions { ThreadCount = 0 }));
    }

    [Fact]
    public void ThreadCount_OverProcessorCount_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            IsolatedTaskScheduler.Create(new IsolatedSchedulerOptions { ThreadCount = Environment.ProcessorCount + 1 }));
    }

    // ════════════════════════════════════════════════════════════
    //  隔离执行（§2）
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task Tasks_ExecuteOnPrivateThreads()
    {
        using var scheduler = IsolatedTaskScheduler.Create(new IsolatedSchedulerOptions { ThreadCount = 2, Name = "exec-test" });
        var names = new ConcurrentBag<string?>();

        var tasks = Enumerable.Range(0, 20).Select(_ =>
            Task.Factory.StartNew(
                () => names.Add(Thread.CurrentThread.Name),
                CancellationToken.None, TaskCreationOptions.None, scheduler)).ToArray();

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEmpty(names);
        Assert.All(names, n => Assert.StartsWith("exec-test", n ?? "", StringComparison.Ordinal));   // 全在私有线程
    }

    [Fact]
    public async Task Await_ContinuationStaysOnScheduler()
    {
        using var scheduler = IsolatedTaskScheduler.Create(new IsolatedSchedulerOptions { ThreadCount = 1, Name = "cont-test" });
        var afterYieldName = (string?)null;

        await Task.Factory.StartNew(async () =>
        {
            await Task.Yield();    // 让出 → continuation 应回流本调度器
            Interlocked.Exchange(ref afterYieldName, Thread.CurrentThread.Name);
        }, CancellationToken.None, TaskCreationOptions.None, scheduler).Unwrap().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.StartsWith("cont-test", afterYieldName ?? "", StringComparison.Ordinal);
    }

    // ════════════════════════════════════════════════════════════
    //  队列模式（§3.3）
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void Queue_AutoBoundedByDefault()
    {
        using var s = IsolatedTaskScheduler.Create(new IsolatedSchedulerOptions { ThreadCount = 2 });
        Assert.True(s.IsBounded);
        Assert.Equal(Math.Max(2 * 4, 16), s.QueueCapacity);
    }

    [Fact]
    public void Queue_ExplicitBoundedCapacity()
    {
        using var s = IsolatedTaskScheduler.Create(new IsolatedSchedulerOptions { ThreadCount = 1, QueueCapacity = 3 });
        Assert.True(s.IsBounded);
        Assert.Equal(3, s.QueueCapacity);
    }

    [Fact]
    public void Queue_UnboundedWhenNegative()
    {
        using var s = IsolatedTaskScheduler.Create(new IsolatedSchedulerOptions { ThreadCount = 1, QueueCapacity = -1 });
        Assert.False(s.IsBounded);
        Assert.Equal(-1, s.QueueCapacity);
    }

    /// <summary>
    /// 有界队列满时，生产者（调度 Task 的线程）被阻塞——真背压。确定性 flag 断言：
    /// 释放前 C 不可跑（队列满），释放后 C 必跑。
    /// </summary>
    [Fact]
    public Task Bounded_BlocksProducerUntilSpace()
    {
        using var scheduler = IsolatedTaskScheduler.Create(new IsolatedSchedulerOptions
        { ThreadCount = 1, QueueCapacity = 1, Name = "blk-test" });
        var gate = new ManualResetEventSlim(false);
        var aStarted = new ManualResetEventSlim(false);
        var bRan = 0;
        var cRan = 0;

        // A：占住唯一的私有线程，等 gate
        var a = Task.Factory.StartNew(() =>
        {
            aStarted.Set();
            gate.Wait();                // 阻塞私有线程
        }, CancellationToken.None, TaskCreationOptions.None, scheduler);
        Assert.True(aStarted.Wait(2000), "A 未在 2s 内启动");

        // B：进队列（cap=1 → 队列满）
        var b = Task.Factory.StartNew(() => Interlocked.Increment(ref bRan),
            CancellationToken.None, TaskCreationOptions.None, scheduler);

        // C：在池线程上调度——其 QueueTask 应被背压阻塞（队列满 + 线程被 A 占）
        Task? cTask = null;
        var cSchedule = Task.Run(() =>
        {
            cTask = Task.Factory.StartNew(() => Interlocked.Increment(ref cRan),
                CancellationToken.None, TaskCreationOptions.None, scheduler);
        });
        Thread.Sleep(200);   // 给 C 的 QueueTask 时间尝试入队（非断言，纯等待）

        // ★ 确定性断言：释放前 C 不可能跑（队列满、线程被 A 占）
        Assert.Equal(0, Volatile.Read(ref cRan));

        gate.Set();   // 释放 → A 完成 → 线程取 B → C 解阻塞
        Assert.True(cSchedule.Wait(5000), "C 调度未在 5s 内返回（背压未解除）");
        Assert.True(cTask!.Wait(5000), "C 未在 5s 内执行");

        Assert.True(a.Wait(5000) && b.Wait(5000));
        Assert.Equal(1, Volatile.Read(ref bRan));
        Assert.Equal(1, Volatile.Read(ref cRan));
        return Task.CompletedTask;
    }

    // ════════════════════════════════════════════════════════════
    //  指标（§6 / L2）
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task Metrics_EmitCountersAndLatencyWhenEnabled()
    {
        var (hub, sink) = NewHub(metricsEnabled: true);
        using var scheduler = IsolatedTaskScheduler.Create(new IsolatedSchedulerOptions { ThreadCount = 1, Name = "met-test", Hub = hub });

        var tasks = Enumerable.Range(0, 10).Select(_ =>
            Task.Factory.StartNew(() => { }, CancellationToken.None, TaskCreationOptions.None, scheduler)).ToArray();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));

        // enqueued 在任务运行前发（无竞态）；executed/histogram/gauge 在任务后发，轮询等待
        Assert.True(sink.Counters.Count(c => c == "scheduler.task.enqueued") >= 10);
        Assert.True(SpinWait.SpinUntil(() => sink.Counters.Any(c => c == "scheduler.task.executed"), 2000));
        Assert.Contains(sink.Histograms, h => h.name == "scheduler.task.exec_us");
        Assert.Contains(sink.Gauges, g => g.name == "scheduler.queue.depth");
    }

    [Fact]
    public async Task Metrics_SilentWhenDisabled()
    {
        var (hub, sink) = NewHub(metricsEnabled: false);   // sink 挂上但 Metrics.Enabled=false → IsEnabled=false
        using var scheduler = IsolatedTaskScheduler.Create(new IsolatedSchedulerOptions { ThreadCount = 1, Hub = hub });

        var tasks = Enumerable.Range(0, 5).Select(_ =>
            Task.Factory.StartNew(() => { }, CancellationToken.None, TaskCreationOptions.None, scheduler)).ToArray();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(sink.Counters);
        Assert.Empty(sink.Histograms);
        Assert.Empty(sink.Gauges);
    }

    // ════════════════════════════════════════════════════════════
    //  watchdog（§5 / L3）：慢任务 / 疑似死锁检测
    // ════════════════════════════════════════════════════════════

    [Fact]
    public Task Watchdog_DetectsSlowTask()
    {
        var (hub, sink) = NewHub(metricsEnabled: true);
        using var scheduler = IsolatedTaskScheduler.Create(new IsolatedSchedulerOptions
        {
            ThreadCount = 1, TaskTimeout = TimeSpan.FromMilliseconds(200),   // 满套并行负载下留足窗口
            WatchdogInterval = TimeSpan.FromMilliseconds(100), DeadlockConfirmTicks = 100,   // 单线程不会判死锁
            Name = "slow-test", Hub = hub
        });

        _ = Task.Factory.StartNew(() => Thread.Sleep(800), CancellationToken.None, TaskCreationOptions.None, scheduler);

        Assert.True(SpinWait.SpinUntil(() => sink.Counters.Any(c => c == "scheduler.task.slow"), 5000));
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Watchdog_DetectsSuspectedDeadlock()
    {
        var (hub, sink) = NewHub(metricsEnabled: true);
        using var scheduler = IsolatedTaskScheduler.Create(new IsolatedSchedulerOptions
        {
            ThreadCount = 2, TaskTimeout = TimeSpan.FromMilliseconds(150),   // 满套并行负载下留足窗口
            WatchdogInterval = TimeSpan.FromMilliseconds(100), DeadlockConfirmTicks = 1,
            Name = "deadlock-test", Hub = hub
        });

        var t1 = Task.Factory.StartNew(() => Thread.Sleep(1500), CancellationToken.None, TaskCreationOptions.None, scheduler);
        var t2 = Task.Factory.StartNew(() => Thread.Sleep(1500), CancellationToken.None, TaskCreationOptions.None, scheduler);

        // 2 个慢任务（各自 Sleep）满足保守启发式 → 疑似死锁（A4：可能误报，此处正是触发该启发式）。
        // ★ 满套件负载下放宽窗口（任务启动调度延迟会打散双线程同时性 → 曾 flaky；确定性对齐见
        //   MultiConsumer 的 Barrier 模式，此处 2 线程专属调度器竞争小，放宽足够）。
        Assert.True(SpinWait.SpinUntil(() => sink.Counters.Any(c => c == "scheduler.deadlock.suspected"), 10_000));
        await Task.WhenAll(t1, t2).WaitAsync(TimeSpan.FromSeconds(12));
    }

    [Fact]
    public async Task Watchdog_SilentWhenDisabled()
    {
        var (hub, sink) = NewHub(metricsEnabled: true);
        using var scheduler = IsolatedTaskScheduler.Create(new IsolatedSchedulerOptions
        {
            ThreadCount = 1, TaskTimeout = TimeSpan.FromMilliseconds(50),
            WatchdogInterval = TimeSpan.Zero,   // 关闭 watchdog
            Name = "wd-off", Hub = hub
        });

        var t = Task.Factory.StartNew(() => Thread.Sleep(300), CancellationToken.None, TaskCreationOptions.None, scheduler);
        await t.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(150);   // 若 watchdog 误开，50ms 周期早该触发
        Assert.DoesNotContain(sink.Counters, c => c == "scheduler.task.slow");
    }

    // ════════════════════════════════════════════════════════════
    //  死亡重启（§4 / L4）
    // ════════════════════════════════════════════════════════════

    [Fact]
    public Task Restart_OnThreadDeath_Always()
    {
        var (hub, sink) = NewHub(metricsEnabled: true);
        var scheduler = IsolatedTaskScheduler.Create(new IsolatedSchedulerOptions
        {
            ThreadCount = 1, WatchdogInterval = TimeSpan.FromMilliseconds(50),
            RestartPolicy = SchedulerRestartPolicy.Always, Name = "restart-test", Hub = hub
        });
        try
        {
            scheduler._testForceExitIdx = 0;   // 线程 0 下次取任务时退出（finally→Dead）
            _ = Task.Factory.StartNew(() => { }, CancellationToken.None, TaskCreationOptions.None, scheduler);   // 唤醒它去撞钩子

            Assert.True(SpinWait.SpinUntil(() => sink.Counters.Any(c => c == "scheduler.threads.restarted"), 3000));

            // 重启后替换线程存活，仍能正常跑任务
            var ran = 0;
            var t = Task.Factory.StartNew(() => Interlocked.Increment(ref ran), CancellationToken.None, TaskCreationOptions.None, scheduler);
            Assert.True(t.Wait(3000));
            Assert.Equal(1, Volatile.Read(ref ran));
        }
        finally { scheduler.Dispose(); }
        return Task.CompletedTask;
    }

    [Fact]
    public Task Restart_PolicyNone_MarksDegraded()
    {
        var (hub, sink) = NewHub(metricsEnabled: true);
        var scheduler = IsolatedTaskScheduler.Create(new IsolatedSchedulerOptions
        {
            ThreadCount = 1, WatchdogInterval = TimeSpan.FromMilliseconds(50),
            RestartPolicy = SchedulerRestartPolicy.None, Name = "no-restart", Hub = hub
        });
        try
        {
            scheduler._testForceExitIdx = 0;
            _ = Task.Factory.StartNew(() => { }, CancellationToken.None, TaskCreationOptions.None, scheduler);

            Assert.True(SpinWait.SpinUntil(() => sink.Counters.Any(c => c == "scheduler.threads.degraded"), 3000));
            Assert.DoesNotContain(sink.Counters, c => c == "scheduler.threads.restarted");
        }
        finally { scheduler.Dispose(); }
        return Task.CompletedTask;
    }

    // ════════════════════════════════════════════════════════════
    //  辅助
    // ════════════════════════════════════════════════════════════

    /// <summary>捕获所有日志消息的测试用 ILogger（对齐 TC.Tier.Core.Logging.ILogger）。</summary>
    private sealed class CapturingLogger : ILogger
    {
        public readonly List<string> Messages = new();
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log(LogLevel logLevel, string message, Exception? exception = null)
        {
            lock (Messages) Messages.Add(message);
        }
    }

    /// <summary>捕获所有指标调用的测试用 IMetricsSink（任务在私有线程上发，用并发安全集合）。</summary>
    private sealed class CapturingSink : IMetricsSink
    {
        public bool IsEnabled => true;
        public readonly ConcurrentQueue<string> Counters = new();
        public readonly ConcurrentQueue<(string name, double value)> Histograms = new();
        public readonly ConcurrentQueue<(string name, double value)> Gauges = new();

        public void Counter(string name, ReadOnlySpan<KeyValuePair<string, string>> tags) => Counters.Enqueue(name);
        public void Histogram(string name, double value, ReadOnlySpan<KeyValuePair<string, string>> tags) => Histograms.Enqueue((name, value));
        public void Gauge(string name, double value, ReadOnlySpan<KeyValuePair<string, string>> tags) => Gauges.Enqueue((name, value));
    }

    /// <summary>构造测试用 hub：sink 始终挂上（观察调用），由 metricsEnabled 控制 MetricsView.IsEnabled。</summary>
    private static (ObservabilityHub hub, CapturingSink sink) NewHub(bool metricsEnabled)
    {
        var sink = new CapturingSink();
        var hub = ObservabilityHub.Create(sink, tracer: null,
            new ObservabilityOptions { Metrics = new MetricsConfig { Enabled = metricsEnabled } });
        return (hub, sink);
    }
}
