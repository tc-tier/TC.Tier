namespace TC.Tier.Core.Tests.Shared;

/// <summary>
/// SyncAsyncBridge 契约测试（docs/sync-async-bridge.md §10）——基本三轨、可见性原则、
/// 再入防护、池饿死回归、超时纪律、并发压测。
/// </summary>
public class SyncAsyncBridgeTests
{
    // ════════════════════════════════════════════════════════════
    // === 基本三轨（成功/失败/取消）===
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void Run_ReturnsResult()
    {
        var result = SyncAsyncBridge.Run(async ct =>
        {
            await Task.Yield();
            return 42;
        });
        result.Should().Be(42);
    }

    [Fact]
    public void Run_Failure_RethrowsOriginal()
    {
        var ex = new InvalidOperationException("boom");
        var act = () => SyncAsyncBridge.Run(async ct =>
        {
            await Task.Yield();
            throw ex;
        });
        act.Should().Throw<InvalidOperationException>().Which.Should().BeSameAs(ex);
    }

    [Fact]
    public void Run_Canceled_RethrowsOce()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var act = () => SyncAsyncBridge.Run(async ct =>
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
        }, cancellationToken: cts.Token);
        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void Run_VoidWork_Completes()
    {
        var done = 0;
        SyncAsyncBridge.Run(async ct =>
        {
            await Task.Yield();
            Volatile.Write(ref done, 1);
        });
        Volatile.Read(ref done).Should().Be(1);
    }

    // ════════════════════════════════════════════════════════════
    // === 可见性原则（Start 返回即 Running）+ 延迟等待 ===
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void Start_ReturnsRunningImmediately_BeforeWorkExecutes()
    {
        using var gate = new ManualResetEventSlim(false);
        var op = SyncAsyncBridge.Start(async ct => { gate.Wait(CancellationToken.None); await Task.CompletedTask; });   // work 挂在门上
        try
        {
            op.Status.Should().Be(AsyncOperationStatus.Running);   // ★ 可见性原则：不依赖被调度
            op.IsCompleted.Should().BeFalse();
        }
        finally
        {
            gate.Set();
        }
        op.Wait(5_000).Should().BeTrue();
        op.Status.Should().Be(AsyncOperationStatus.Succeeded);
    }

    [Fact]
    public void Start_DeferredWait_CallerContinuesOtherWork()
    {
        // "Start 早、Wait 晚"：发起后调用线程继续本地逻辑，最后时刻才等
        using var gate = new ManualResetEventSlim(false);
        long localWork = 0;
        var op = SyncAsyncBridge.Start(async ct => { gate.Wait(CancellationToken.None); await Task.CompletedTask; });
        for (var i = 0; i < 1_000_000; i++) localWork += i;   // 本地逻辑不阻塞
        op.IsCompleted.Should().BeFalse();
        gate.Set();
        op.Wait(5_000).Should().BeTrue();
        localWork.Should().BeGreaterThan(0);
    }

    // ════════════════════════════════════════════════════════════
    // === 再入防护（池自锁防御）===
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void NestedRun_SamePool_ThrowsFast()
    {
        // 桥 work 体内再经同一池同步等待 → 池自锁 → Start 必须快速失败（不能挂死等超时）
        var act = () => SyncAsyncBridge.Run(async ct =>
        {
            await Task.Yield();
            SyncAsyncBridge.Run(async ct2 => await Task.Yield(), cancellationToken: CancellationToken.None);   // 同默认池——禁止
        });
        act.Should().Throw<InvalidOperationException>()
            .Where(e => e.Message.Contains("同一桥池") || e.Message.Contains("再入"));
    }

    [Fact]
    public void NestedRun_DifferentScheduler_Allowed()
    {
        // 分池豁免：内层注入 own 单线程实例（对齐"compact 同步 worker 与异步建段分池"教训）
        using var own = IsolatedTaskScheduler.Create(new IsolatedSchedulerOptions
        {
            ThreadCount = 1,
            Name = "bridge-nested-test",
            WatchdogInterval = TimeSpan.Zero,
        });
        var act = () => SyncAsyncBridge.Run(async ct =>
        {
            await Task.Yield();
            SyncAsyncBridge.Run(async ct2 => await Task.Yield(),
                new SyncBridgeOptions { Name = "bridge-nested-inner", Scheduler = own, TimeoutMs = 5_000 },
                CancellationToken.None);
        });
        act.Should().NotThrow();
    }

    // ════════════════════════════════════════════════════════════
    // === 独立池隔离（推进不依赖公共池）===
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void Work_ContinuationRunsOnBridgePoolThread()
    {
        // await 之后仍在桥池私有线程上（continuation 回流，不落公共池）——隔离性的机器验证
        var threadName = SyncAsyncBridge.Run<string>(async ct =>
        {
            await Task.Yield();
            await Task.Yield();
            return Thread.CurrentThread.Name ?? "<unnamed>";
        });
        threadName.Should().StartWith("sync-bridge");
    }

    [Fact]
    public void PoolStarvation_Regression_BridgeStillCompletes()
    {
        // ★ 池饿死回归：打满公共池（阻塞 Task.Run）后桥操作仍在超时内完成——独立池价值的验收。
        //   若桥被改回公共池 + 同步等待，本测试将超时失败（回归绊线）。
        var blockers = Math.Min(Environment.ProcessorCount * 4, 32);
        using var release = new ManualResetEventSlim(false);
        var held = new Task[blockers];
        for (var i = 0; i < blockers; i++)
            held[i] = Task.Run(() => release.Wait(15_000));   // 占住公共池工作线程
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var act = () => SyncAsyncBridge.Run(async ct =>
            {
                await Task.Yield();
                return 7;
            }, new SyncBridgeOptions { TimeoutMs = 10_000 });
            act().Should().Be(7);
            sw.ElapsedMilliseconds.Should().BeLessThan(10_000);
        }
        finally
        {
            release.Set();
            Task.WaitAll(held, 15_000);
        }
    }

    // ════════════════════════════════════════════════════════════
    // === 超时纪律（有界 + 现场）===
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void Run_Timeout_ThrowsWithDiagnostics()
    {
        using var gate = new ManualResetEventSlim(false);
        try
        {
            var act = () => SyncAsyncBridge.Run(async ct => { gate.Wait(20_000, CancellationToken.None); await Task.CompletedTask; },
                new SyncBridgeOptions { Name = "timeout-probe", TimeoutMs = 100 });
            act.Should().Throw<TimeoutException>()
                .Where(e => e.Message.Contains("timeout-probe") && e.Message.Contains("Running"));
        }
        finally
        {
            gate.Set();   // 放走 work，别占着桥池线程
        }
    }

    [Fact]
    public void Run_CustomTimeout_OverridesDefault()
    {
        using var gate = new ManualResetEventSlim(false);
        try
        {
            var act = () => SyncAsyncBridge.Run(async ct => { gate.Wait(20_000, CancellationToken.None); await Task.CompletedTask; },
                new SyncBridgeOptions { TimeoutMs = 80 });
            var sw = System.Diagnostics.Stopwatch.StartNew();
            act.Should().Throw<TimeoutException>();
            sw.ElapsedMilliseconds.Should().BeLessThan(5_000);   // 用的是自定义 80ms 而非默认 15s
        }
        finally
        {
            gate.Set();
        }
    }

    // ════════════════════════════════════════════════════════════
    // === 默认池与并发压测 ===
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void DefaultScheduler_IsProcessSingleton()
    {
        SyncAsyncBridge.DefaultScheduler.Should().BeSameAs(SyncAsyncBridge.DefaultScheduler);
        SyncAsyncBridge.DefaultScheduler.Should().BeAssignableTo<IsolatedTaskScheduler>();
    }

    [Fact]
    public void ConcurrentRun_Stress_AllComplete()
    {
        const int threads = 4, opsEach = 25;
        var counter = 0;
        var tasks = new Task[threads];
        for (var t = 0; t < threads; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                for (var i = 0; i < opsEach; i++)
                    SyncAsyncBridge.Run(async ct =>
                    {
                        Interlocked.Increment(ref counter);
                        await Task.Yield();
                    });
            });
        }
        Task.WaitAll(tasks, TimeSpan.FromMinutes(2));
        Volatile.Read(ref counter).Should().Be(threads * opsEach);   // 无丢失、无状态机错乱
    }
}
