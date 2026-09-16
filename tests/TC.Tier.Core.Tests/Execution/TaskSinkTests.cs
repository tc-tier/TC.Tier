using TC.Tier.Core.Execution;

namespace TC.Tier.Core.Tests.Shared;

/// <summary>
/// TaskSink 契约（fire-and-forget 统一原语，设计稿 docs/design/core-task-sink-design.md §6）：
/// 跟踪配对/异常兜底/取消传播/drain 有界/fail-fast/幂等/并发压力/OCE 归一
/// （SubmitFast 内联 / Submit 池上——两条提交路径共用）+ 各路径专项 +
/// 审查修复契约（onFaulted 回调保护/提交释放并发封堵）。
/// </summary>
public class TaskSinkTests
{
    // ══ 两形态统一适配（共用契约 Theory 数据源）══

    public interface IGroupRunner : IAsyncDisposable
    {
        void Run(Func<CancellationToken, Task> body);
        int InFlightCount { get; }
        long SubmittedCount { get; }
        long FaultedCount { get; }
    }

    public sealed class ValueRunner : IGroupRunner
    {
        private readonly TaskSink _g;
        public ValueRunner(Action<Exception>? onFaulted = null, TimeSpan? drain = null)
            => _g = new TaskSink(onFaulted: onFaulted, drainTimeout: drain);
        public void Run(Func<CancellationToken, Task> body) => _g.SubmitFast(ct => new ValueTask(body(ct)));
        public int InFlightCount => _g.InFlightCount;
        public long SubmittedCount => _g.SubmittedCount;
        public long FaultedCount => _g.FaultedCount;
        public ValueTask DisposeAsync() => _g.DisposeAsync();
    }

    public sealed class TaskRunner : IGroupRunner
    {
        private readonly TaskSink _g;
        public TaskRunner(Action<Exception>? onFaulted = null, TimeSpan? drain = null)
            => _g = new TaskSink(onFaulted: onFaulted, drainTimeout: drain);
        public void Run(Func<CancellationToken, Task> body) => _g.Submit(body);   // 池上路径
        public int InFlightCount => _g.InFlightCount;
        public long SubmittedCount => _g.SubmittedCount;
        public long FaultedCount => _g.FaultedCount;
        public ValueTask DisposeAsync() => _g.DisposeAsync();
    }

    public static TheoryData<Func<Action<Exception>?, TimeSpan?, IGroupRunner>> Runners =>
    [
        (onFaulted, drain) => new ValueRunner(onFaulted, drain),
        (onFaulted, drain) => new TaskRunner(onFaulted, drain),
    ];

    private static Task WaitForInFlightZeroAsync(IGroupRunner g, TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        return Task.Run(async () =>
        {
            while (g.InFlightCount > 0)
            {
                Assert.True(Environment.TickCount64 < deadline, "在途未归零（跟踪配对破坏——计数泄漏）");
                await Task.Delay(10);
            }
        });
    }

    // ══ 共用契约（两形态各跑一遍）══

    [Theory, MemberData(nameof(Runners))]
    public async Task TrackPairing_AllComplete_InFlightZero_SubmittedCountAccurate(
        Func<Action<Exception>?, TimeSpan?, IGroupRunner> factory)
    {
        await using var g = factory(null, null);
        for (var i = 0; i < 100; i++)
            g.Run(_ => Task.CompletedTask);

        await WaitForInFlightZeroAsync(g, TimeSpan.FromSeconds(10));
        g.SubmittedCount.Should().Be(100);
        g.FaultedCount.Should().Be(0);
    }

    [Theory, MemberData(nameof(Runners))]
    public async Task FaultBubbling_OnFaultedOnce_Contained_SubsequentUnaffected(
        Func<Action<Exception>?, TimeSpan?, IGroupRunner> factory)
    {
        var faults = new List<Exception>();
        await using var g = factory(ex => { lock (faults) faults.Add(ex!); }, null);

        g.Run(_ => throw new InvalidOperationException("boom"));
        g.Run(_ => Task.CompletedTask);

        await WaitForInFlightZeroAsync(g, TimeSpan.FromSeconds(10));
        g.FaultedCount.Should().Be(1);
        lock (faults) faults.Count.Should().Be(1);
        g.SubmittedCount.Should().Be(2);
    }

    [Theory, MemberData(nameof(Runners))]
    public async Task OnFaultedThrows_Protected_CountNotPolluted(
        Func<Action<Exception>?, TimeSpan?, IGroupRunner> factory)
    {
        // ★ 审查修复契约：回调自身抛异常不逃出 catch——任务恒成功（不产生 unobserved）、计数正常
        await using var g = factory(_ => throw new NotSupportedException("callback boom"), null);

        g.Run(_ => throw new InvalidOperationException("body boom"));

        await WaitForInFlightZeroAsync(g, TimeSpan.FromSeconds(10));
        g.FaultedCount.Should().Be(1);   // 任务体异常只计一次（回调异常不污染）
    }

    [Theory, MemberData(nameof(Runners))]
    public async Task CancelPropagation_DisposeSignalsInFlightBody(
        Func<Action<Exception>?, TimeSpan?, IGroupRunner> factory)
    {
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var g = factory(null, null);

        g.Run(async ct =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { observed.TrySetResult(); throw; }
        });

        await Task.Run(async () =>
        {
            while (g.InFlightCount == 0) await Task.Delay(5);   // 确认已注册在途
        });
        await g.DisposeAsync();

        // 不抛（5s 内）即通过——取消已被任务体观察到
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory, MemberData(nameof(Runners))]
    public async Task DrainBounded_StuckTask_TimeoutNoThrow(
        Func<Action<Exception>?, TimeSpan?, IGroupRunner> factory)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var g = factory(null, TimeSpan.FromMilliseconds(200));

        g.Run(_ => gate.Task);   // 挂住且不吃组 ct（不协作取消）——drain 必超时

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await g.DisposeAsync();   // 超时 LogWarning 不抛
        sw.Stop();
        sw.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(150));   // 确实等了
        // 上限放宽到 30s（drain 超时本身 200ms——50× 余量）：契约=不抛+确实等，弱化墙钟敏感性
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));
    }

    [Theory, MemberData(nameof(Runners))]
    public async Task FailFast_RunAfterDispose_Throws(
        Func<Action<Exception>?, TimeSpan?, IGroupRunner> factory)
    {
        var g = factory(null, null);
        await g.DisposeAsync();

        var act = () => g.Run(_ => Task.CompletedTask);
        act.Should().Throw<ObjectDisposedException>();
    }

    [Theory, MemberData(nameof(Runners))]
    public async Task DisposeIdempotent_DoubleDisposeSafe(
        Func<Action<Exception>?, TimeSpan?, IGroupRunner> factory)
    {
        var g = factory(null, null);
        await g.DisposeAsync();
        await g.DisposeAsync();   // 第二次立即返回——不抛
    }

    [Theory, MemberData(nameof(Runners))]
    public async Task ConcurrentStress_MultiProducer_CountingAccurate(
        Func<Action<Exception>?, TimeSpan?, IGroupRunner> factory)
    {
        const int producers = 4, perProducer = 200;
        await using var g = factory(null, null);

        await Task.WhenAll(Enumerable.Range(0, producers).Select(p => Task.Run(async () =>
        {
            for (var i = 0; i < perProducer; i++)
            {
                g.Run(_ => Task.CompletedTask);
                if (i % 50 == 0) await Task.Yield();
            }
        })));

        await WaitForInFlightZeroAsync(g, TimeSpan.FromSeconds(10));
        g.SubmittedCount.Should().Be(producers * perProducer);
        g.FaultedCount.Should().Be(0);
    }

    [Theory, MemberData(nameof(Runners))]
    public async Task OceNormalized_NonGroupCancellation_NotFaulted(
        Func<Action<Exception>?, TimeSpan?, IGroupRunner> factory)
    {
        await using var g = factory(null, null);
        using var bizCts = new CancellationTokenSource();

        g.Run(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            bizCts.Cancel();
            await Task.Delay(10, bizCts.Token);   // 业务取消（非组取消）
        });
        bizCts.CancelAfter(TimeSpan.FromMilliseconds(100));

        await g.DisposeAsync();
        g.FaultedCount.Should().Be(0);   // OCE（含业务取消）归正常——不进故障计数
    }

    // ══ SubmitFast（内联首段）专项 ══

    [Fact]
    public async Task Value_FastPath_SyncComplete_ZeroTrackingTraffic()
    {
        await using var g = new TaskSink();
        g.SubmitFast(_ => ValueTask.CompletedTask);
        // 同步完成：提交返回即在途已退役（计数归零——零滞留）
        g.InFlightCount.Should().Be(0);
        g.SubmittedCount.Should().Be(1);
    }

    [Fact]
    public async Task Value_FirstSegmentInline_RunsOnCallerThread()
    {
        await using var g = new TaskSink();
        var callerThread = Environment.CurrentManagedThreadId;
        int? bodyThread = null;

        g.SubmitFast(_ => { bodyThread = Environment.CurrentManagedThreadId; return ValueTask.CompletedTask; });

        bodyThread.Should().Be(callerThread);   // 第一段在提交线程执行（内联契约）
    }

    [Fact]
    public async Task Value_SyncThrow_ContainedNotBurstCaller()
    {
        await using var g = new TaskSink();
        // 同步抛不炸提交线程（WrapAsync catch-all 接住）+ 计数兜底
        Func<CancellationToken, ValueTask> body = _ => throw new ArgumentException("sync throw");
        g.SubmitFast(body);

        g.FaultedCount.Should().Be(1);
        g.InFlightCount.Should().Be(0);
    }

    [Fact]
    public async Task Value_TaskLambdaCompat_ImplicitConversion()
    {
        await using var g = new TaskSink();
        // 返回 Task 的 lambda 隐式转换照传（API 零摩擦）
        g.SubmitFast(_ => Task.CompletedTask);
        g.SubmittedCount.Should().Be(1);
    }

    // ══ Submit（池上）专项 ══

    [Fact]
    public async Task Task_FirstSegmentOffCaller_RunReturnsImmediately()
    {
        await using var g = new TaskSink();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        g.Submit(async ct =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        });

        // Run 已返回而任务体挂住——提交线程零占用（第一段在池上）
        started.Task.IsCompleted.Should().BeFalse();
        g.InFlightCount.Should().Be(1);
    }

    [Fact]
    public async Task Task_PoolLeakDefense_DisposeWithQueuedNotStarted_InFlightDrains()
    {
        var g = new TaskSink(drainTimeout: null);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        g.Submit(async ct =>
        {
            reached.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        });
        // 已入池（或已启动挂住）即 Dispose——外层 CancellationToken.None 保证 WrapAsync
        // 的 finally 必经（泄漏雷防御），取消传播收尾
        await g.DisposeAsync();

        g.InFlightCount.Should().Be(0);   // 计数归零（若外层传组 ct：任务被池直接取消跳过 finally → 永挂）
    }

    // ══ 提交/释放并发（审查修复契约——RegisterInFlight 二次校验封堵漏入）══

    [Fact]
    public async Task SubmitDisposeRace_NoLeakNoStrayException()
    {
        // 提交洪流与 Dispose 并发：漏入窗口的任务被二次校验拦截（抛 ODE 由提交方吞掉）
        // ——收尾后计数归零（无泄漏）、除 ODE 外无异常外泄。
        var g = new TaskSink("race", drainTimeout: TimeSpan.FromMilliseconds(100));
        var stray = new List<Exception>();
        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var producers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            var i = 0;
            while (!stop.Task.IsCompleted)
            {
                try
                {
                    g.SubmitFast(_ => new ValueTask(Task.CompletedTask));
                    g.Submit(_ => Task.CompletedTask);
                }
                catch (ObjectDisposedException) { return; }   // 二次校验拦截——预期
                catch (Exception ex) { lock (stray) stray.Add(ex); return; }   // 其他异常 = 违约
                if (++i % 100 == 0) await Task.Yield();
            }
        })).ToArray();
        await Task.Delay(200);
        await g.DisposeAsync();
        stop.TrySetResult();
        await Task.WhenAll(producers);

        lock (stray) stray.Should().BeEmpty();
        g.InFlightCount.Should().Be(0);   // 漏入任务已回滚/在途全部收尾
    }

    // ══ 审查修复契约（drain 超时资源滞留）══

    [Fact]
    public async Task DrainTimeout_InFlightBodyCanStillRegisterOnCt()
    {
        // ★ 超时分支资源滞留换安全：在途任务收尾段在组 ct 上 Register——
        //   旧形态（超时仍 Dispose cts）此处 ODE 被 WrapAsync 收口成意外 faulted；新形态 Register 成功。
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var g = new TaskSink(drainTimeout: TimeSpan.FromMilliseconds(100));
        g.Submit(async ct =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException)
            {
                // 收尾段（取消唤醒后）——在组 ct 上挂清理回调
                using var r = ct.Register(() => registered.TrySetResult());
                await Task.Delay(50, CancellationToken.None);   // 滞后于 drain 超时（100ms 窗口内 Dispose 已返回）——不接组 ct（有意滞后）
            }
        });
        await g.DisposeAsync();   // 超时告警不抛——资源滞留
        await registered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        g.FaultedCount.Should().Be(0, "收尾段 Register 不因 CTS 已释放而 ODE（资源滞留契约）");
    }

    // ══ 池化观察者契约（去 async 包装——租还复用/上下文保持）══

    [Fact]
    public async Task ObserverReuse_MixedOutcomes_SequentialCountsExact()
    {
        // 同一观察者实例轮转复用（同步成功/同步抛/慢路径故障/慢路径成功）——计数精确无跨轮串味
        await using var g = new TaskSink();
        const int rounds = 50;
        for (var i = 0; i < rounds; i++)
        {
            g.SubmitFast(_ => ValueTask.CompletedTask);
            g.SubmitFast((Func<CancellationToken, ValueTask>)(_ => throw new InvalidOperationException("sync throw")));
            g.SubmitFast((Func<CancellationToken, ValueTask>)(async _ =>
            {
                await Task.Yield();
                throw new InvalidOperationException("slow fault");
            }));
            g.SubmitFast((Func<CancellationToken, ValueTask>)(async _ => await Task.Yield()));
        }

        var deadline = Environment.TickCount64 + 10_000;
        while (g.InFlightCount > 0)
        {
            Assert.True(Environment.TickCount64 < deadline, "在途未归零（跟踪配对破坏——计数泄漏）");
            await Task.Delay(10);
        }
        g.SubmittedCount.Should().Be(rounds * 4);
        g.FaultedCount.Should().Be(rounds * 2);
    }

    [Theory, MemberData(nameof(Runners))]
    public async Task ExecutionContextFlows_AmbientAsyncLocal_BodyAndFaultCallbackSeeIt(
        Func<Action<Exception>?, TimeSpan?, IGroupRunner> factory)
    {
        // ★ 上下文保持契约：提交点 ambient AsyncLocal 流入任务体首段与故障回调
        //   （Submit 池直排 / SubmitFast 慢路径续体——对齐 async 包装语义）
        var ambient = new AsyncLocal<string?>();
        var bodySeen = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var faultSeen = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var g = factory(_ => faultSeen.TrySetResult(ambient.Value), null);

        ambient.Value = "ctx";
        g.Run(ct =>
        {
            bodySeen.TrySetResult(ambient.Value);
            return Task.Delay(10, CancellationToken.None);   // 真挂起——慢路径续体
        });
        g.Run(async ct =>
        {
            await Task.Delay(10, CancellationToken.None);
            throw new InvalidOperationException("slow fault");
        });

        (await bodySeen.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be("ctx");
        (await faultSeen.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be("ctx",
            "故障回调在提交点上下文重放下执行（AsyncLocal 父子链保持）");
        await WaitForInFlightZeroAsync(g, TimeSpan.FromSeconds(10));
        g.FaultedCount.Should().Be(1);
    }
}
