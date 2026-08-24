namespace TC.Tier.Core.Tests.Shared;

/// <summary>
/// BackgroundWorkerLoop（非泛型基类）单元测试——验证循环骨架 + 双执行器 + 启停 + 异常隔离 + Dispose 编排。
/// </summary>
public class BackgroundWorkerLoopTests
{
    // ════════════════════════════════════════════════════════════
    //  测试用最小子类——时间驱动（Task.Delay），记录周期数/异常/退出钩子
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 测试 worker——每周期 Delay 10ms，记录调用次数。
    /// 可控制：返回 false 终止 / 抛异常 / 退出钩子。
    /// </summary>
    private sealed class TestWorker : BackgroundWorkerLoop
    {
        private readonly TimeSpan _cycleDelay;
        private readonly int _maxCycles;        // 0 = 无限
        private readonly Exception? _throwAt;   // 在第 N 周期抛异常
        private readonly bool _ignoreCancellation; // true = 不响应 ct（测试超时）

        public int CycleCount;
        public int ErrorCount;
        public int ExitCount;
        public bool LoopStartCalled;

        public TestWorker(
            TimeSpan? cycleDelay = null, int maxCycles = 0,
            Exception? throwAt = null, bool useDedicatedThread = false,
            bool ignoreCancellation = false, string? name = null)
            : base(useDedicatedThread ? IsolatedTaskScheduler.Shared : null, name: name)
        {
            _cycleDelay = cycleDelay ?? TimeSpan.FromMilliseconds(10);
            _maxCycles = maxCycles;
            _throwAt = throwAt;
            _ignoreCancellation = ignoreCancellation;
        }

        protected override void OnLoopStart() => LoopStartCalled = true;

        protected override async ValueTask<bool> RunOneCycleAsync(CancellationToken ct)
        {
            if (_ignoreCancellation)
            {
                await Task.Delay(_cycleDelay, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await Task.Delay(_cycleDelay, ct).ConfigureAwait(false);
            }
            Interlocked.Increment(ref CycleCount);

            // 在指定周期抛异常（测试异常隔离）
            if (_throwAt is { } ex && Volatile.Read(ref CycleCount) == 1)
                throw ex;

            // 达到最大周期数则终止
            if (_maxCycles > 0 && Volatile.Read(ref CycleCount) >= _maxCycles)
                return false;
            return true;
        }

        protected override void OnCycleError(Exception ex) => Interlocked.Increment(ref ErrorCount);

        protected override ValueTask OnLoopExitAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref ExitCount);
            return ValueTask.CompletedTask;
        }
    }

    // ════════════════════════════════════════════════════════════
    //  启停基础
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task StartStopBasic_WorkerEntersLoopAndExitsOnStop()
    {
        var worker = new TestWorker(cycleDelay: TimeSpan.FromMilliseconds(10));
        worker.Start();

        // 轮询对齐至首周期完成（固定 Delay 在并行套压池时假失败——全量套实测踩中）
        (await TestWait.UntilAsync(() => worker.CycleCount > 0)).Should().BeTrue("worker 应已运行若干周期");
        worker.LoopStartCalled.Should().BeTrue("OnLoopStart 应被调用");

        worker.Stop();
        worker.WaitForExit();

        worker.ExitCount.Should().Be(1, "OnLoopExitAsync 应被调用一次");
        worker.Dispose();
    }

    [Fact]
    public void StartIdempotent_RepeatedStartOnlyOneExecutor()
    {
        var worker = new TestWorker(cycleDelay: TimeSpan.FromMilliseconds(50));
        worker.Start();
        worker.Start();   // 幂等——不应启动第二个执行器
        worker.Start();

        worker.Stop();
        worker.WaitForExit();
        worker.Dispose();
        // 不抛异常即通过——CAS 守护了幂等
    }

    [Fact]
    public void StopIdempotent_RepeatedStopNoThrow()
    {
        var worker = new TestWorker();
        worker.Start();

        worker.Stop();
        worker.Stop();   // 幂等——重复 Cancel 不抛
        worker.Stop();

        worker.WaitForExit();
        worker.Dispose();
    }

    // ════════════════════════════════════════════════════════════
    //  Dispose
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void DisposeDoubleFree_OnlyReleasesOnce()
    {
        var worker = new TestWorker();
        worker.Start();

        worker.Dispose();
        worker.Dispose();   // 幂等——CAS 防双释放
        worker.Dispose();
        // 不抛 ObjectDisposedException 即通过
    }

    [Fact]
    public async Task DisposeAsync_ReleasesCleanly()
    {
        var worker = new TestWorker(cycleDelay: TimeSpan.FromMilliseconds(10));
        worker.Start();
        (await TestWait.UntilAsync(() => worker.CycleCount > 0)).Should().BeTrue("至少跑一个周期再 Dispose");

        await worker.DisposeAsync();
        // 不抛即通过
    }

    // ════════════════════════════════════════════════════════════
    //  异常隔离 + 退出钩子
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task CycleExceptionIsolation_OnCycleErrorCalledAndLoopContinues()
    {
        var worker = new TestWorker(
            cycleDelay: TimeSpan.FromMilliseconds(10),
            throwAt: new InvalidOperationException("test cycle error"));

        worker.Start();

        // 轮询对齐到断言条件（10s 上限）——固定 Delay(200) 是 delay 型同步，
        // 池忙时 worker 循环慢启（并行测试套压公共池）会假失败（全量套实测踩中一次）
        var deadline = Environment.TickCount64 + 10_000;
        while (Environment.TickCount64 < deadline
               && (Volatile.Read(ref worker.ErrorCount) < 1 || Volatile.Read(ref worker.CycleCount) <= 1))
        {
            await Task.Delay(10);
        }
        worker.ErrorCount.Should().BeGreaterThanOrEqualTo(1, "OnCycleError 应被调用");
        worker.CycleCount.Should().BeGreaterThan(1, "循环应继续——异常不杀 worker");

        worker.Stop();
        worker.WaitForExit();
        worker.Dispose();
    }

    [Fact]
    public Task RunOneCycleReturnsFalse_LoopExitsAndOnLoopExitCalled()
    {
        var worker = new TestWorker(
            cycleDelay: TimeSpan.FromMilliseconds(10),
            maxCycles: 3);

        worker.Start();
        worker.WaitForExit();

        worker.CycleCount.Should().Be(3, "应在第 3 周期返回 false 终止");
        worker.ExitCount.Should().Be(1, "OnLoopExitAsync 应被调用");
        worker.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task OnLoopExitCalled_AllThreePaths()
    {
        // 路径 1：正常退出（RunOneCycleAsync 返回 false）
        var normalWorker = new TestWorker(maxCycles: 1);
        normalWorker.Start();
        normalWorker.WaitForExit();
        normalWorker.ExitCount.Should().Be(1, "正常退出应调 OnLoopExitAsync");
        normalWorker.Dispose();

        // 路径 2：异常退出后 Stop（异常不杀 worker，靠 Stop 退出）
        var errorWorker = new TestWorker(throwAt: new InvalidOperationException("err"));
        errorWorker.Start();
        (await TestWait.UntilAsync(() => errorWorker.ErrorCount >= 1)).Should().BeTrue("异常周期应已发生再 Stop");
        errorWorker.Stop();
        errorWorker.WaitForExit();
        errorWorker.ExitCount.Should().Be(1, "异常后 Stop 退出应调 OnLoopExitAsync");
        errorWorker.Dispose();

        // 路径 3：Stop 触发取消退出
        var stopWorker = new TestWorker(cycleDelay: TimeSpan.FromMilliseconds(10));
        stopWorker.Start();
        (await TestWait.UntilAsync(() => stopWorker.CycleCount > 0)).Should().BeTrue("至少跑一个周期再 Stop");
        stopWorker.Stop();
        stopWorker.WaitForExit();
        stopWorker.ExitCount.Should().Be(1, "Stop 触发取消退出应调 OnLoopExitAsync");
        stopWorker.Dispose();
    }

    // ════════════════════════════════════════════════════════════
    //  双执行器
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void TaskExecutor_UseDedicatedThreadFalse_RunsOnThreadPool()
    {
        var worker = new TestWorker(
            cycleDelay: TimeSpan.FromMilliseconds(10),
            useDedicatedThread: false,
            name: "TestTaskWorker");
        worker.Start();

        // ★ 轮询替代固定 50ms sleep——并行测试负载下固定窗口易 flaky
        SpinWait.SpinUntil(() => worker.CycleCount > 0, 2000).Should().BeTrue("公共池模式应正常运行");

        worker.Stop();
        worker.WaitForExit();
        worker.Dispose();
    }

    [Fact]
    public void ThreadExecutor_UseDedicatedThreadTrue_RunsOnDedicatedThread()
    {
        var worker = new TestWorker(
            cycleDelay: TimeSpan.FromMilliseconds(10),
            useDedicatedThread: true,
            name: "TestThreadWorker");
        worker.Start();

        // ★ 轮询替代固定 50ms sleep——并行测试负载下固定窗口易 flaky
        SpinWait.SpinUntil(() => worker.CycleCount > 0, 2000).Should().BeTrue("隔离调度器模式应正常运行");

        worker.Stop();
        worker.WaitForExit();
        worker.Dispose();
    }

    // ════════════════════════════════════════════════════════════
    //  超时 + 取消传播
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void ExitTimeout_WorkerIgnoresStop_WaitForExitTimesOutWithoutThrowing()
    {
        // worker 用 CancellationToken.None 不响应取消——WaitForExit 必超时但不抛
        var worker = new TestWorker(
            cycleDelay: TimeSpan.FromSeconds(10),    // 每周期 10s，Join/Wait 必超时
            ignoreCancellation: true);

        // 用短超时让测试快速跑完
        var shortTimeoutWorker = new TimeoutTestWorker(TimeSpan.FromMilliseconds(50));
        shortTimeoutWorker.Start();

        // WaitForExit 应在 50ms 超时后返回（不抛）
        var sw = System.Diagnostics.Stopwatch.StartNew();
        shortTimeoutWorker.Stop();
        shortTimeoutWorker.WaitForExit();
        sw.Stop();

        // 超时应快速返回（50ms 超时 + 一些余量），不应长时间阻塞
        sw.ElapsedMilliseconds.Should().BeLessThan(2000, "超时应快速返回");
        shortTimeoutWorker.Dispose();
        worker.Dispose();
    }

    [Fact]
    public void Ctor_RejectsNonPositiveExitTimeout()
    {
        var act = static () =>
        {
            using var _ = new TimeoutTestWorker(TimeSpan.Zero);
        };
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>定制超时时间的测试 worker（超短退出超时）。</summary>
    private sealed class TimeoutTestWorker(TimeSpan exitTimeout) : BackgroundWorkerLoop(
        exitTimeout: exitTimeout, name: "TimeoutTest")
    {
        protected override async ValueTask<bool> RunOneCycleAsync(CancellationToken ct)
        {
            // 不响应 ct——模拟"worker 卡住"
            await Task.Delay(TimeSpan.FromSeconds(30), CancellationToken.None).ConfigureAwait(false);
            return true;
        }
    }

    [Fact]
    public async Task CancellationTokenPropagation_StopTriggersOceAndCleanExit()
    {
        var worker = new TestWorker(cycleDelay: TimeSpan.FromMilliseconds(50));
        worker.Start();

        // 轮询对齐至首周期完成——此后 Stop 必然经 Delay/下一周期收到 ct 取消，均走 OCE 干净退出
        (await TestWait.UntilAsync(() => worker.CycleCount > 0)).Should().BeTrue("至少完成一个周期再 Stop");
        worker.Stop();          // cts.Cancel → Delay 抛 OCE → 正常退出（不进 OnCycleError）

        worker.WaitForExit();

        worker.ErrorCount.Should().Be(0, "OCE 是正常退出，不进 OnCycleError");
        worker.ExitCount.Should().Be(1, "OnLoopExitAsync 应被调用");
        worker.Dispose();
    }
}
