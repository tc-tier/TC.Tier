namespace TC.Tier.Core.Tests.Primitives;

/// <summary>
/// AsyncOperation 契约测试（docs/sync-async-bridge.md §10）——计数/状态语义、唤醒协议、配对绊线。
/// </summary>
public class AsyncOperationTests
{
    // ════════════════════════════════════════════════════════════
    // === 状态机语义（计数/终态/幂等）===
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void Constructor_StateIsRunning_CompletedFalse()
    {
        var op = new AsyncOperation("test");
        op.Status.Should().Be(AsyncOperationStatus.Running);
        op.IsCompleted.Should().BeFalse();
        op.IsObserved.Should().BeFalse();   // 先断言未观察（Exception 读取本身会标记）
        op.Exception.Should().BeNull();
    }

    [Fact]
    public void ReportSucceeded_TerminalState_CompletesWait()
    {
        var op = new AsyncOperation("test");
        op.ReportSucceeded();
        op.Status.Should().Be(AsyncOperationStatus.Succeeded);
        op.IsCompleted.Should().BeTrue();
        op.Wait(1000).Should().BeTrue();
    }

    [Fact]
    public void ReportFailed_StatusFailed_ExceptionStored()
    {
        var op = new AsyncOperation("test");
        var ex = new InvalidOperationException("boom");
        op.ReportFailed(ex);
        op.Status.Should().Be(AsyncOperationStatus.Failed);
        op.Exception.Should().BeSameAs(ex);
    }

    [Fact]
    public void ReportFailed_FirstTerminalWins_LaterReportsAreNoOp()
    {
        // 首终态 = Succeeded：后续 Failed/Canceled 幂等 no-op
        var a = new AsyncOperation("test");
        a.ReportSucceeded();
        a.ReportFailed(new InvalidOperationException("late"));
        a.ReportCanceled(new OperationCanceledException());
        a.Status.Should().Be(AsyncOperationStatus.Succeeded);
        a.Wait(1000).Should().BeTrue();

        // 首终态 = Failed：后续 Succeeded 不改变（不可逆）
        var b = new AsyncOperation("test");
        var ex = new InvalidOperationException("first");
        b.ReportFailed(ex);
        b.ReportSucceeded();
        b.Status.Should().Be(AsyncOperationStatus.Failed);
        var act = () => b.Wait(1000);
        act.Should().Throw<InvalidOperationException>().Which.Should().BeSameAs(ex);
    }

    [Fact]
    public void ReportFailed_NullArgument_Throws()
    {
        var op = new AsyncOperation("test");
        var act = () => op.ReportFailed(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ConcurrentReport_OnlyOneTerminalWins()
    {
        // 并发完成侧：N 线程混报三种终态——最终状态必为三者之一且不再变化，Wait 语义与状态一致
        for (var round = 0; round < 20; round++)
        {
            var op = new AsyncOperation("test");
            var barrier = new Barrier(3);
            var tasks = new Task[3];
            tasks[0] = Task.Run(() => { barrier.SignalAndWait(); op.ReportSucceeded(); });
            tasks[1] = Task.Run(() => { barrier.SignalAndWait(); op.ReportFailed(new InvalidOperationException("f")); });
            tasks[2] = Task.Run(() => { barrier.SignalAndWait(); op.ReportCanceled(new OperationCanceledException()); });
            Task.WaitAll(tasks);

            op.IsCompleted.Should().BeTrue();
            var st = op.Status;
            st.Should().BeOneOf(AsyncOperationStatus.Succeeded, AsyncOperationStatus.Failed, AsyncOperationStatus.Canceled);
            // 状态与等待语义一致（多次读不漂移——不可逆）
            for (var i = 0; i < 3; i++)
                op.Status.Should().Be(st);
        }
    }

    // ════════════════════════════════════════════════════════════
    // === 同步兜底等待（分层/有界/取消）===
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void Wait_TimeoutWhenNeverCompleted_ReturnsFalse()
    {
        var op = new AsyncOperation("test");   // 永不完成
        var sw = System.Diagnostics.Stopwatch.StartNew();
        op.Wait(50).Should().BeFalse();
        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(40);   // 真等了，不是立即返回
        op.IsObserved.Should().BeTrue();   // 超时也视为已观察（调用方拿到 false 即取得现场责任）
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Wait_NonPositiveTimeout_Throws(int timeoutMs)
    {
        var op = new AsyncOperation("test");
        var act = () => op.Wait(timeoutMs);
        act.Should().Throw<ArgumentOutOfRangeException>();   // 有界纪律：同步等待必须超时 > 0
    }

    [Fact]
    public async Task Wait_CancellationDuringWait_ThrowsOce()
    {
        var op = new AsyncOperation("test");   // 永不完成
        using var cts = new CancellationTokenSource(50);
        await Task.Yield();   // 确保不在同步上下文里
        var act = () => op.Wait(10_000, cts.Token);
        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void Wait_AfterFailure_RethrowsOriginalException()
    {
        var op = new AsyncOperation("test");
        var ex = new InvalidOperationException("boom");
        op.ReportFailed(ex);
        var act = () => op.Wait(1000);
        act.Should().Throw<InvalidOperationException>().Which.Should().BeSameAs(ex);
    }

    [Fact]
    public void Wait_AfterCancel_RethrowsOce()
    {
        var op = new AsyncOperation("test");
        var oce = new OperationCanceledException();
        op.ReportCanceled(oce);
        var act = () => op.Wait(1000);
        act.Should().Throw<OperationCanceledException>().Which.Should().BeSameAs(oce);
    }

    // ════════════════════════════════════════════════════════════
    // === 异步等待（一等公民）===
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task WaitAsync_AlreadySucceeded_CompletesSynchronously()
    {
        var op = new AsyncOperation("test");
        op.ReportSucceeded();
        var vt = op.WaitAsync();
        vt.IsCompletedSuccessfully.Should().BeTrue();
        await vt;
    }

    [Fact]
    public async Task WaitAsync_AlreadyFailed_Rethrows()
    {
        var op = new AsyncOperation("test");
        var ex = new InvalidOperationException("boom");
        op.ReportFailed(ex);
        var act = async () => await op.WaitAsync();
        await act.Should().ThrowAsync<InvalidOperationException>().Where(e => ReferenceEquals(e, ex));
    }

    [Fact]
    public async Task WaitAsync_BeforeCompletion_CompletesOnReport()
    {
        var op = new AsyncOperation("test");
        var waiter = Task.Run(async () => await op.WaitAsync());
        await Task.Delay(50);   // 确认等待者已挂起
        waiter.IsCompleted.Should().BeFalse();
        op.ReportSucceeded();
        await waiter;
    }

    // ════════════════════════════════════════════════════════════
    // === 唤醒协议（广播 / 完成先于等待零丢失）===
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task ReportSucceeded_BroadcastsToMultipleSyncWaiters()
    {
        var op = new AsyncOperation("test");
        var results = new int[4];   // 1=true
        var waiters = new Task[4];
        for (var i = 0; i < 4; i++)
        {
            var idx = i;
            waiters[i] = Task.Run(() => results[idx] = op.Wait(10_000) ? 1 : 0);
        }
        await Task.Delay(100);   // 等待者全部挂起（park）
        op.ReportSucceeded();
        await Task.WhenAll(waiters);
        results.Should().AllBeEquivalentTo(1);   // 广播全醒、无一超时
    }

    [Fact]
    public async Task ReportSucceeded_BroadcastsToMultipleAsyncWaiters()
    {
        var op = new AsyncOperation("test");
        var completed = 0;
        var waiters = new Task[4];
        for (var i = 0; i < 4; i++)
            waiters[i] = Task.Run(async () =>
            {
                await op.WaitAsync();
                Interlocked.Increment(ref completed);
            });
        await Task.Delay(100);
        Volatile.Read(ref completed).Should().Be(0);
        op.ReportSucceeded();
        await Task.WhenAll(waiters);
        Volatile.Read(ref completed).Should().Be(4);
    }

    [Fact]
    public void CompletionBeforeWait_NoLostSignal()
    {
        // 完成先于等待：Wait 必须立即返回 true（快路径 volatile 读，零丢失窗口）
        var op = new AsyncOperation("test");
        op.ReportSucceeded();
        for (var i = 0; i < 100; i++)
            op.Wait(1000).Should().BeTrue();   // 多消费者重复等（MRES 语义：保持 set）
    }

    // ════════════════════════════════════════════════════════════
    // === ThrowIfFailed / 观察标记 / 诊断 ===
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void ThrowIfFailed_Succeeded_NoThrow()
    {
        var op = new AsyncOperation("test");
        op.ReportSucceeded();
        var act = () => op.ThrowIfFailed();
        act.Should().NotThrow();
    }

    [Fact]
    public void ThrowIfFailed_Failed_RethrowsStored()
    {
        var op = new AsyncOperation("test");
        var ex = new InvalidOperationException("boom");
        op.ReportFailed(ex);
        var act = () => op.ThrowIfFailed();
        act.Should().Throw<InvalidOperationException>().Which.Should().BeSameAs(ex);
    }

    [Fact]
    public void ThrowIfFailed_NotCompleted_Throws()
    {
        var op = new AsyncOperation("test");
        var act = () => op.ThrowIfFailed();
        act.Should().Throw<InvalidOperationException>();   // 防御：仅终态后可调
    }

    [Fact]
    public async Task ConsumptionModes_MarkObserved()
    {
        // Wait 超时 / WaitAsync / ThrowIfFailed 三种消费模式都标记已观察（泄漏绊线契约）
        var a = new AsyncOperation("a");
        a.Wait(10);
        a.IsObserved.Should().BeTrue();

        var b = new AsyncOperation("b");
        var bTask = b.WaitAsync().AsTask();   // AsTask 消费 ValueTask（挂起中，不 await）
        await Task.Yield();
        b.IsObserved.Should().BeTrue();

        var c = new AsyncOperation("c");
        c.ReportSucceeded();
        c.ThrowIfFailed();
        c.IsObserved.Should().BeTrue();

        var d = new AsyncOperation("d");
        d.IsObserved.Should().BeFalse();   // 仅轮询 Status/IsCompleted 不标记（不算消费）
    }

    [Fact]
    public void Describe_ContainsNameAndStatus()
    {
        var op = new AsyncOperation("my-op");
        op.ReportSucceeded();
        var text = op.Describe();
        text.Should().Contain("my-op").And.Contain("Succeeded");
        op.ToString().Should().Contain("my-op").And.Contain("Succeeded");   // ToString=Describe（ageMs 随时间变，不做全等）
    }

    // ════════════════════════════════════════════════════════════
    // === 通用后台操作契约（2026-08-24 升级）：Progress/Failed 事件 ===
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void ReportProgress_RaisesProgressEvent_SubscriberExceptionIsolated()
    {
        var op = new AsyncOperation("progress");
        var seen = new List<double>();
        op.Progress += (_, p) =>
        {
            seen.Add(p);
            if (p > 0.5) throw new InvalidOperationException("subscriber boom");   // 订阅者异常不得外泄
        };

        op.ReportProgress(0.25);
        op.ReportProgress(0.75);
        op.ReportProgress(1.0);   // 前次订阅者异常不影响后续触发

        seen.Should().Equal(0.25, 0.75, 1.0);
    }

    [Fact]
    public void ReportFailed_RaisesFailedEvent()
    {
        var op = new AsyncOperation("fail");
        Exception? seen = null;
        op.Failed += (_, ex) => seen = ex;
        var ex = new InvalidOperationException("boom");
        op.ReportFailed(ex);
        seen.Should().BeSameAs(ex);
    }

    [Fact]
    public void ReportCanceled_RaisesFailedEvent_WithOce()
    {
        // ★ Failed 语义沿用 Storage 契约：取消（回滚完成）也触发 Failed 事件（参数为 OCE）
        var op = new AsyncOperation("cancel");
        Exception? seen = null;
        op.Failed += (_, ex) => seen = ex;
        var oce = new OperationCanceledException("canceled");
        op.ReportCanceled(oce);
        seen.Should().BeSameAs(oce);
    }

    [Fact]
    public void ReportSucceeded_DoesNotRaiseFailedEvent()
    {
        var op = new AsyncOperation("ok");
        var raised = false;
        op.Failed += (_, _) => raised = true;
        op.ReportSucceeded();
        raised.Should().BeFalse();
    }

    [Fact]
    public async Task TerminalEvent_BeforeWaitAsyncWakeup_EventAlreadyDelivered()
    {
        // ★ 事件先于信号时序契约（Storage 历史 flaky 根因区）：等待者（WaitAsync 唤醒）苏醒时
        //   终态事件必已投递——订阅事件后等待，完成后断言事件已到（无"等待返回但事件未发"窗口）
        for (var round = 0; round < 50; round++)
        {
            var op = new AsyncOperation("seq");
            var failed = 0;
            op.Failed += (_, _) => Interlocked.Increment(ref failed);
            var waiter = Task.Run(async () =>
            {
                try { await op.WaitAsync(); }
                catch { /* 预期失败 */ }
            });
            await Task.Delay(10);
            op.ReportFailed(new InvalidOperationException("boom"));
            await waiter;
            Volatile.Read(ref failed).Should().Be(1);   // 苏醒时事件必已投递
        }
    }

    [Fact]
    public async Task FailedEvent_SubscriberException_DoesNotBreakCompletion()
    {
        var op = new AsyncOperation("throw");
        op.Failed += (_, _) => throw new InvalidOperationException("subscriber boom");
        op.ReportFailed(new InvalidOperationException("real"));
        op.IsCompleted.Should().BeTrue();
        // ★ 订阅者异常不外泄——等待者重抛的是操作的真实异常（非订阅者异常）
        var act = async () => await op.WaitAsync();
        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Be("real");
    }

    // ════════════════════════════════════════════════════════════
    // === 通用后台操作契约：Cancel + CancellationToken ===
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void Cancel_TriggersToken_WorkerCheckpointObservable()
    {
        var op = new AsyncOperation("cancel");
        op.CancellationToken.IsCancellationRequested.Should().BeFalse();
        op.Cancel();
        op.CancellationToken.IsCancellationRequested.Should().BeTrue();
        op.Cancel();   // 幂等
        op.CancellationToken.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task Cancel_WorkerRespondsWithReportCanceled_CompletesWait()
    {
        // ★ 后台 worker 模式：Cancel 触发令牌 → worker 检查点抛 OCE → ReportCanceled（回滚完成）
        var op = new AsyncOperation("worker");
        var worker = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    op.CancellationToken.ThrowIfCancellationRequested();
                    await Task.Delay(10);
                }
            }
            catch (OperationCanceledException oce)
            {
                op.ReportCanceled(oce);
            }
        });

        await Task.Delay(50);
        op.Cancel();
        await worker;
        op.Status.Should().Be(AsyncOperationStatus.Canceled);
        var act = async () => await op.WaitAsync();
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void ExternalCancellation_Linked_CancelsOperation()
    {
        // ★ 外部令牌链接：引擎 Dispose 令牌触发 → 在途操作自动取消（Cancel 同源）
        using var external = new CancellationTokenSource();
        var op = new AsyncOperation("linked", externalCancellation: external.Token);
        op.CancellationToken.IsCancellationRequested.Should().BeFalse();
        external.Cancel();
        op.CancellationToken.IsCancellationRequested.Should().BeTrue();
    }

    // ════════════════════════════════════════════════════════════
    // === 泛型变体 AsyncOperation{TResult} ===
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task Generic_ReportSucceeded_WaitAsyncReturnsResult()
    {
        var op = new AsyncOperation<int>("gen");
        op.ReportSucceeded(42);
        (await op.WaitAsync()).Should().Be(42);
        op.Result.Should().Be(42);
    }

    [Fact]
    public async Task Generic_WaitAsync_BeforeCompletion_ReturnsResultOnReport()
    {
        var op = new AsyncOperation<string>("gen");
        var waiter = Task.Run(async () => await op.WaitAsync());
        await Task.Delay(50);
        waiter.IsCompleted.Should().BeFalse();
        op.ReportSucceeded("done");
        (await waiter).Should().Be("done");
    }

    [Fact]
    public async Task Generic_ReportFailed_RethrowsOriginal_ResultThrows()
    {
        var op = new AsyncOperation<int>("gen");
        var ex = new InvalidOperationException("boom");
        op.ReportFailed(ex);
        var act = async () => await op.WaitAsync();
        await act.Should().ThrowAsync<InvalidOperationException>().Where(e => ReferenceEquals(e, ex));
        var act2 = () => op.Result;
        act2.Should().Throw<InvalidOperationException>().Which.Should().BeSameAs(ex);
    }

    [Fact]
    public void Generic_Result_NotCompleted_Throws()
    {
        var op = new AsyncOperation<int>("gen");
        var act = () => op.Result;
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Generic_ReportSucceededWithoutResult_DefensiveThrow()
    {
        // ★ 防御误用：无参 ReportSucceeded 被隐藏——调用编译错误；反射/基类引用触发防御异常
        var op = new AsyncOperation<int>("gen");
        Action act = () => ((AsyncOperationBase)op).ReportSucceeded();
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Generic_CompletedEvent_CarriesResult_BeforeWaitWakeup()
    {
        var op = new AsyncOperation<int>("gen");
        var seen = 0;
        op.Completed += (_, r) => seen = r;
        op.ReportSucceeded(7);
        seen.Should().Be(7);
        op.Wait(1000).Should().BeTrue();
    }

    [Fact]
    public void Generic_FirstTerminalWins_LaterReportsNoOp()
    {
        var op = new AsyncOperation<int>("gen");
        op.ReportSucceeded(1);
        op.ReportSucceeded(2);   // 幂等 no-op
        op.Result.Should().Be(1);
    }

    // ════════════════════════════════════════════════════════════
    // === 接口消费面契约（IAsyncOperation——公开面 = 类消费面全集）===
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void Interface_ExposesFullConsumptionSurface()
    {
        // ★ 接口 = 类消费面全集：轮询（Status/IsCompleted/Exception）+ 同步兜底（Wait）
        //   + 终态取异常（ThrowIfFailed）+ 事件 + 取消——调用方只看接口即可，无需"查类 API"
        var impl = new AsyncOperation("iface");
        AssertInterfaceConsumptionSurface(impl);

        var ex = new InvalidOperationException("boom");
        impl.ReportFailed(ex);   // 完成侧经实现类——接口面只读消费侧
        AssertInterfaceFailedSurface(impl, ex);
    }

    private static void AssertInterfaceConsumptionSurface<T>(T op) where T : IAsyncOperation
    {
        op.Status.Should().Be(AsyncOperationStatus.Running);
        op.IsCompleted.Should().BeFalse();
        op.Exception.Should().BeNull();
    }

    private static void AssertInterfaceFailedSurface<T>(T op, Exception expected) where T : IAsyncOperation
    {
        op.Status.Should().Be(AsyncOperationStatus.Failed);
        op.IsCompleted.Should().BeTrue();
        op.Exception.Should().BeSameAs(expected);
        var waitAct = () => op.Wait(1000);   // 同步兜底：失败终态重抛原异常（对齐 Task 语义）
        waitAct.Should().Throw<InvalidOperationException>().Which.Should().BeSameAs(expected);
        var act = () => op.ThrowIfFailed();
        act.Should().Throw<InvalidOperationException>().Which.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task GenericInterface_WaitAsyncReturnsResult()
    {
        var impl = new AsyncOperation<int>("gen-iface");
        impl.ReportSucceeded(42);
        await AssertGenericInterfaceResult(impl);   // 经接口参数 await 拿结果（接口面验证）
    }

    private static async Task AssertGenericInterfaceResult<T>(T op) where T : IAsyncOperation<int>
    {
        (await op.WaitAsync()).Should().Be(42);
        op.Exception.Should().BeNull();
    }
}
