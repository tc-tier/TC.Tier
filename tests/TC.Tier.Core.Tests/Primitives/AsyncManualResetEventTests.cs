
namespace TC.Tier.Core.Tests.Primitives;

public class AsyncManualResetEventTests
{
    [Fact]
    public void InitialState_IsUnset()
    {
        var ev = new AsyncManualResetEvent();
        ev.IsSet.Should().BeFalse();
    }

    [Fact]
    public void Constructor_InitialStateTrue_IsSet()
    {
        var ev = new AsyncManualResetEvent(initialState: true);
        ev.IsSet.Should().BeTrue();
    }

    [Fact]
    public async Task WaitAsync_AlreadySet_CompletesSynchronously()
    {
        var ev = new AsyncManualResetEvent(initialState: true);
        await ev.WaitAsync();
    }

    [Fact]
    public async Task Set_ThenWaitAsync_CompletesImmediately()
    {
        var ev = new AsyncManualResetEvent();
        ev.Set();
        ev.IsSet.Should().BeTrue();
        await ev.WaitAsync();
    }

    [Fact]
    public async Task WaitAsync_BeforeSet_BlocksUntilSet()
    {
        var ev = new AsyncManualResetEvent();
        var tcs = new TaskCompletionSource<bool>();

        // 后台等待
        _ = Task.Run(async () =>
        {
            await ev.WaitAsync();
            tcs.SetResult(true);
        });

        // 确认等待者已挂起
        await Task.Delay(50);
        tcs.Task.IsCompleted.Should().BeFalse();

        ev.Set();

        (await tcs.Task).Should().BeTrue();
    }

    [Fact]
    public async Task Set_BroadcastsToMultipleWaiters()
    {
        var ev = new AsyncManualResetEvent();
        int completed = 0;
        var waiters = new Task[4];

        for (int i = 0; i < 4; i++)
        {
            waiters[i] = Task.Run(async () =>
            {
                await ev.WaitAsync();
                Interlocked.Increment(ref completed);
            });
        }

        await Task.Delay(100);
        // Set 前，应无人完成
        Volatile.Read(ref completed).Should().Be(0);

        ev.Set();
        await Task.WhenAll(waiters);

        Volatile.Read(ref completed).Should().Be(4);
    }

    [Fact]
    public void Set_CalledTwice_IsIdempotent()
    {
        var ev = new AsyncManualResetEvent();
        ev.Set();
        var act = () => ev.Set();
        act.Should().NotThrow();
        ev.IsSet.Should().BeTrue();
    }

    [Fact]
    public async Task Reset_AfterSet_AllowsReblocking()
    {
        var ev = new AsyncManualResetEvent(initialState: true);

        // 第一次等待立即完成
        await ev.WaitAsync();

        // Reset 后应阻塞
        ev.Reset();
        ev.IsSet.Should().BeFalse();

        var waitTask = ev.WaitAsync().AsTask();
        await Task.Delay(50);
        waitTask.IsCompleted.Should().BeFalse();

        // Set 后应完成
        ev.Set();
        await waitTask;
        waitTask.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public void Reset_WhenUnset_IsNoOp()
    {
        var ev = new AsyncManualResetEvent();
        var act = () => ev.Reset();
        act.Should().NotThrow();
        ev.IsSet.Should().BeFalse();
    }

    [Fact]
    public void Wait_Synchronous_ReturnsAfterSet()
    {
        var ev = new AsyncManualResetEvent();

        // 后台延迟后 set
        _ = Task.Run(() =>
        {
            Thread.Sleep(50);
            ev.Set();
        });

        // 阻塞等待
        ev.Wait();
        ev.IsSet.Should().BeTrue();
    }

    [Fact]
    public async Task SetResetReuse_MultipleCycles_AllWork()
    {
        var ev = new AsyncManualResetEvent();

        for (int cycle = 0; cycle < 5; cycle++)
        {
            ev.Reset();
            ev.IsSet.Should().BeFalse();

            var waitTask = ev.WaitAsync().AsTask();
            await Task.Delay(20);
            waitTask.IsCompleted.Should().BeFalse();

            ev.Set();
            await waitTask;
        }
    }

    [Fact]
    public async Task WaitAsync_WithCancellation_ThrowsOCE()
    {
        var ev = new AsyncManualResetEvent();
        using var cts = new CancellationTokenSource();

        var waitTask = ev.WaitAsync(cts.Token).AsTask();
        await Task.Delay(50);
        waitTask.IsCompleted.Should().BeFalse();

        cts.Cancel();

        var act = async () => await waitTask;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task WaitAsync_AlreadyCanceled_ThrowsImmediately()
    {
        var ev = new AsyncManualResetEvent();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await ev.WaitAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task CancelledWait_DoesNotAffectEventState()
    {
        var ev = new AsyncManualResetEvent();
        using var cts = new CancellationTokenSource();

        // 第一个等待者取消
        _ = Task.Run(async () =>
        {
            try { await ev.WaitAsync(cts.Token); }
            catch (OperationCanceledException) { }
        });

        await Task.Delay(50);
        cts.Cancel();
        await Task.Delay(50);

        // 事件仍 unset，其他等待者仍可被正常唤醒
        ev.IsSet.Should().BeFalse();

        var waiter2 = ev.WaitAsync().AsTask();
        ev.Set();
        await waiter2;
    }

    [Fact]
    public async Task Concurrent_SetAndWait_NoLostSignal()
    {
        for (int iter = 0; iter < 100; iter++)
        {
            var ev = new AsyncManualResetEvent();
            int woken = 0;

            var waiters = new Task[8];
            for (int i = 0; i < 8; i++)
            {
                waiters[i] = Task.Run(async () =>
                {
                    await ev.WaitAsync();
                    Interlocked.Increment(ref woken);
                });
            }

            // 随机延迟后 Set
            await Task.Delay(Random.Shared.Next(1, 10));
            ev.Set();

            await Task.WhenAll(waiters);
            Volatile.Read(ref woken).Should().Be(8);

            ev.Reset();
        }
    }

    /// <summary>
    /// PERF-002 回归：Set 与 waiter 注册的竞态（"完成先于注册"）曾在 ManualResetValueTaskSourceCore
    /// 的 CompletionSentinel 上抛 InvalidOperationException（进程级崩溃）。
    /// 修复后 Set 一律走 MarkOrComplete 标记协议——两种唤醒模式 × 多 waiter 广播高频循环不得抛异常。
    /// </summary>
    [Theory]
    [InlineData(true)]    // 默认：线程池异步调度
    [InlineData(false)]   // 内联模式：Set 调用者线程内联续体
    public async Task SetVsRegistrationRace_NoCompletionSentinelCrash(bool runContinuationsAsynchronously)
    {
        for (int n = 1; n <= 8; n++)
        {
            for (int round = 0; round < 200; round++)
            {
                var ev = new AsyncManualResetEvent(initialState: false, runContinuationsAsynchronously);
                var waiters = new Task[n];
                for (int i = 0; i < n; i++)
                    waiters[i] = Task.Run(async () => await ev.WaitAsync());

                await Task.Yield();   // 让 waiter 与 Set 在注册/完成时序上真实竞态
                ev.Set();
                await Task.WhenAll(waiters);
            }
        }
    }

    /// <summary>
    /// PERF-002 回归：内联模式（无 OnCompleted 时 Set 仅标记）下取消与 Set 并发——
    /// 单次完成守卫应保证只走一个完成路径，不抛异常、不悬挂。
    /// </summary>
    [Fact]
    public async Task InlineMode_CancelAndSetRace_NoThrowNoHang()
    {
        for (int round = 0; round < 500; round++)
        {
            var ev = new AsyncManualResetEvent(initialState: false, runContinuationsAsynchronously: false);
            using var cts = new CancellationTokenSource();
            var t = Task.Run(async () =>
            {
                try { await ev.WaitAsync(cts.Token); }
                catch (OperationCanceledException) { }
            });

            await Task.Yield();
            ev.Set();
            cts.Cancel();
            await t.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
