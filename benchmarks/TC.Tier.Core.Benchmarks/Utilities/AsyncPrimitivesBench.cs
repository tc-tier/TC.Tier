using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace TC.Tier.Core.Benchmarks.Utilities;

/// <summary>
/// 异步同步原语性能基准：对比 SemaphoreSlim 基线与基于 ManualResetValueTaskSourceCore 的新实现。
/// 覆盖：等待分配量、吞吐、广播延迟、池化 Rent/Return 往返。
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 3, iterationCount: 5)]
public class AsyncPrimitivesBench : IDisposable
{
    // ===== 1. 无竞争等待：已 set 事件的快速路径分配量对比 =====

    private AsyncManualResetEvent _presetEvent = new(initialState: true)!;
    private SemaphoreSlim _presetSemaphore = new(1, 1)!;

    [Benchmark(Description = "AsyncMRE.WaitAsync (already set) - zero alloc fast path", Baseline = true)]
    public async ValueTask AsyncMRE_WaitAlreadySet()
    {
        await _presetEvent.WaitAsync();
    }

    [Benchmark(Description = "SemaphoreSlim.WaitAsync (count=1) - fast path")]
    public async Task SemaphoreSlim_WaitAvailable()
    {
        await _presetSemaphore.WaitAsync();
        _presetSemaphore.Release();
    }

    // ===== 2. 单 waiter Set/Wait 往返吞吐 =====

    private AsyncManualResetEvent _cycleEvent = new()!;
    private AsyncManualResetEvent _wakeEvent = new()!;   // 真实唤醒用（unset 起步，默认异步调度）
    private AsyncManualResetEvent _wakeEventInline = new(initialState: false, runContinuationsAsynchronously: false)!;   // 内联唤醒模式
    private SemaphoreSlim _cycleSemaphore = new(0, int.MaxValue)!;

    [Benchmark(Description = "AsyncMRE Set+Wait cycle (already-set fast path)")]
    public async Task AsyncMRE_SetWaitCycle()
    {
        _cycleEvent.Set();
        await _cycleEvent.WaitAsync();
        _cycleEvent.Reset();
    }

    [Benchmark(Description = "AsyncMRE real wake 1:1 (suspended waiter, default async scheduling)")]
    public async Task AsyncMRE_RealWakeSuspended()
    {
        var t = _wakeEvent.WaitAsync().AsTask();   // AsTask 立即挂接 OnCompleted——waiter 真实挂起
        _wakeEvent.Set();                          // 真实唤醒（continuation 经调度/内联）
        await t;
        _wakeEvent.Reset();
    }

    [Benchmark(Description = "AsyncMRE real wake 1:1 (suspended waiter, inline continuation mode)")]
    public async Task AsyncMRE_RealWakeSuspendedInline()
    {
        var t = _wakeEventInline.WaitAsync().AsTask();
        _wakeEventInline.Set();
        await t;
        _wakeEventInline.Reset();
    }

    [Benchmark(Description = "SemaphoreSlim Release+Wait cycle (1 waiter)")]
    public async Task SemaphoreSlim_ReleaseWaitCycle()
    {
        _cycleSemaphore.Release();
        await _cycleSemaphore.WaitAsync();
    }

    // ===== 3. PooledValueTaskSource Rent/Return 往返 =====

    [Benchmark(Description = "PooledVTS Rent+SetResult+Return")]
    public async ValueTask PooledVTS_RentCompleteReturn()
    {
        var s = PooledValueTaskSource.Rent();
        var vt = new ValueTask(s, s.Version);
        s.SetResult();
        await vt;
        PooledValueTaskSource.Return(s);
    }

    [Benchmark(Description = "SemaphoreSlim(0) Release+WaitAsync (1:1)")]
    public async Task SemaphoreSlim_OneShotWait()
    {
        var sem = new SemaphoreSlim(0);
        sem.Release();
        await sem.WaitAsync();
    }

    // ===== 4. 多 waiter 广播延迟 =====

    [Params(1, 4, 16)]
    public int WaiterCount { get; set; }

    [Benchmark(Description = "AsyncMRE broadcast to N waiters")]
    public async Task AsyncMRE_BroadcastN()
    {
        var ev = new AsyncManualResetEvent();
        var waiters = new Task[WaiterCount];

        for (int i = 0; i < WaiterCount; i++)
        {
            waiters[i] = Task.Run(async () => await ev.WaitAsync());
        }

        // 短暂等待确保 waiter 入队
        await Task.Yield();
        ev.Set();
        await Task.WhenAll(waiters);
    }

    [Benchmark(Description = "SemaphoreSlim broadcast to N waiters")]
    public async Task SemaphoreSlim_BroadcastN()
    {
        var sem = new SemaphoreSlim(0);
        var waiters = new Task[WaiterCount];

        for (int i = 0; i < WaiterCount; i++)
        {
            waiters[i] = Task.Run(async () => await sem.WaitAsync());
        }

        await Task.Yield();
        sem.Release(WaiterCount);
        await Task.WhenAll(waiters);
    }

    // ===== 5. AsyncCountDown 吞吐 =====

    private AsyncCountDown _steadyCountDown = new();
    private AsyncCountDown _steadyCountDownInline = new(runContinuationsAsynchronously: false);

    /// <summary>稳态循环：实例预建、ValueTask 直等——纯 Add/Remove + 事件唤醒成本（默认异步调度）。</summary>
    [Benchmark(Description = "AsyncCountDown Add+Wait+Remove steady cycle (default async scheduling)")]
    public async ValueTask AsyncCountDown_AddRemoveSteady()
    {
        _steadyCountDown.Add();
        var wait = _steadyCountDown.WaitUntilEmptyAsync();
        _steadyCountDown.Remove();
        await wait;
    }

    /// <summary>内联唤醒模式稳态循环（Set 不持锁场景 opt-in）。</summary>
    [Benchmark(Description = "AsyncCountDown Add+Wait+Remove steady cycle (inline mode)")]
    public async ValueTask AsyncCountDown_AddRemoveSteadyInline()
    {
        _steadyCountDownInline.Add();
        var wait = _steadyCountDownInline.WaitUntilEmptyAsync();
        _steadyCountDownInline.Remove();
        await wait;
    }

    /// <summary>含构造 + AsTask 包装的全额成本（旧基准保留——展示构造与 Task 分配主导，勿按此选型）。</summary>
    [Benchmark(Description = "AsyncCountDown Add+Remove incl. construct+AsTask")]
    public async Task AsyncCountDown_AddRemoveWithConstruct()
    {
        var cd = new AsyncCountDown();
        cd.Add();
        var waitTask = cd.WaitUntilEmptyAsync().AsTask();
        cd.Remove();
        await waitTask;
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _presetSemaphore?.Dispose();
        _cycleSemaphore?.Dispose();
    }
}
