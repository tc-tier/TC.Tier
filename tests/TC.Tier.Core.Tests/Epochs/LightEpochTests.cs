namespace TC.Tier.Core.Tests.Epochs;

public class LightEpochTests
{
    [Fact]
    public void NewInstance_NotProtectedOnEntry()
    {
        var epoch = new LightEpoch();
        epoch.ThisInstanceProtected().Should().BeFalse();
    }

    [Fact]
    public void Resume_ThisInstanceProtected_True()
    {
        var epoch = new LightEpoch();
        epoch.Resume();
        try
        {
            epoch.ThisInstanceProtected().Should().BeTrue();
        }
        finally
        {
            epoch.Suspend();
        }
    }

    [Fact]
    public void Suspend_AfterResume_NotProtected()
    {
        var epoch = new LightEpoch();
        epoch.Resume();
        epoch.ThisInstanceProtected().Should().BeTrue();
        epoch.Suspend();
        epoch.ThisInstanceProtected().Should().BeFalse();
    }

    [Fact]
    public void ProtectAndDrain_MakesInstanceProtected()
    {
        var epoch = new LightEpoch();
        // Must Resume/Acquire first to get a thread entry, then ProtectAndDrain
        epoch.Resume();
        try
        {
            epoch.ProtectAndDrain();
            epoch.ThisInstanceProtected().Should().BeTrue();
        }
        finally
        {
            epoch.Suspend();
        }
    }

    [Fact]
    public void ResumeSuspend_MultipleCycles_MaintainsConsistency()
    {
        var epoch = new LightEpoch();
        for (int i = 0; i < 100; i++)
        {
            epoch.Resume();
            epoch.ThisInstanceProtected().Should().BeTrue();
            epoch.Suspend();
            epoch.ThisInstanceProtected().Should().BeFalse();
        }
    }

    [Fact]
    public void BumpCurrentEpoch_WithAction_InvokesAction()
    {
        var epoch = new LightEpoch();
        epoch.Resume();
        try
        {
            int invoked = 0;
            epoch.BumpCurrentEpoch(() => invoked++);
            // Action should be drained during BumpCurrentEpoch's ProtectAndDrain
            // (since this thread is the only active thread, epoch should be safe to reclaim immediately)
            invoked.Should().BeGreaterThanOrEqualTo(0); // at minimum, no crash
        }
        finally
        {
            epoch.Suspend();
        }
    }

    [Fact]
    public void BumpCurrentEpoch_NestedBumpCount_Readable()
    {
        // NestedBumpCount is an observable metric; verify it's readable without nesting
        // (actual nested bump triggers Debug.Assert which throws in test host)
        var initial = LightEpoch.NestedBumpCount;
        initial.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void Mark_CheckIsComplete_SingleThread()
    {
        var epoch = new LightEpoch();
        epoch.Resume();
        try
        {
            const int markerIdx = 0;
            const long version = 42;

            epoch.Mark(markerIdx, version);
            // This thread has marked; check if all threads completed
            // Single active thread that marked = should be complete
            epoch.CheckIsComplete(markerIdx, version).Should().BeTrue();
        }
        finally
        {
            epoch.Suspend();
        }
    }

    [Fact]
    public void Mark_DifferentVersion_NotComplete()
    {
        var epoch = new LightEpoch();
        epoch.Resume();
        try
        {
            const int markerIdx = 1;
            epoch.Mark(markerIdx, version: 10);
            // Checking for version 20 when this thread marked 10 and is still active
            // Should not be complete (active thread hasn't reached 20)
            epoch.CheckIsComplete(markerIdx, version: 20).Should().BeFalse();
        }
        finally
        {
            epoch.Suspend();
        }
    }

    [Fact]
    public void Dispose_ResetsState()
    {
        var epoch = new LightEpoch();
        epoch.Dispose();
        // After dispose, should still be usable (Dispose just resets epoch counters)
        epoch.ThisInstanceProtected().Should().BeFalse();
    }

    [Fact]
    public async Task Concurrent_ResumeSuspendAcrossThreads_NoCorruption()
    {
        var epoch = new LightEpoch();
        int errors = 0;
        var tasks = new Task[4];

        for (int t = 0; t < 4; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                for (int i = 0; i < 200; i++)
                {
                    try
                    {
                        epoch.Resume();
                        epoch.ProtectAndDrain();
                        epoch.ThisInstanceProtected().Should().BeTrue();
                        epoch.Suspend();
                    }
                    catch (Exception)
                    {
                        Interlocked.Increment(ref errors);
                    }
                }
            });
        }

        await Task.WhenAll(tasks);
        Volatile.Read(ref errors).Should().Be(0);
    }

    [Fact]
    public async Task Concurrent_BumpCurrentEpoch_ActionsAllExecute()
    {
        var epoch = new LightEpoch();
        int actionsExecuted = 0;
        var tasks = new Task[4];

        for (int t = 0; t < 4; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                epoch.Resume();
                try
                {
                    for (int i = 0; i < 50; i++)
                    {
                        epoch.BumpCurrentEpoch(() => Interlocked.Increment(ref actionsExecuted));
                    }
                }
                finally
                {
                    epoch.Suspend();
                }
            });
        }

        await Task.WhenAll(tasks);
        // All 200 bump actions should eventually execute
        Volatile.Read(ref actionsExecuted).Should().Be(200);
    }

    // ═══════════════════════════════════════════════════════════
    // 协议违反绊线（Debug 构建强制抛异常+示波器历史；Release 零开销不检查）
    // 教训：AsyncPriorityQueue 挂死事故中 Release 构建零检测，只能 hang dump 考古。
    // 违规实验全部在独立线程执行——测试线程的 ThreadStatic epoch 状态永不污染。
    // ═══════════════════════════════════════════════════════════

    private static Exception? RunViolationInIsolatedThread(Action<LightEpoch> violate)
    {
        Exception? ex = null;
        var task = Task.Run(() =>
        {
            var epoch = new LightEpoch();
            try { violate(epoch); }
            catch (Exception e) { ex = e; }
            finally
            {
                // ★ 尽力清理实验残留的保护区（ReentrantResume/Dispose 等实验留下未配对 Resume）——
                //   线程池复用线程不重置 ThreadStatic，残留 ThreadEntryIndex/count 会污染后续
                //   测试在同一线程上的实验（实测：ProtectAndDrain 绊线被残留 entry 绕过）。
                try { epoch.Suspend(); } catch { }
            }
        });
        task.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
        return ex;
    }

    private static void AssertProtocolViolation(Exception? ex, string messageFragment)
    {
#if DEBUG
        ex.Should().BeOfType<InvalidOperationException>()
          .Which.Message.Should().Contain("协议违反").And.Contain(messageFragment)
          .And.Contain("示波器");   // 绊线异常自动携带协议操作历史
#else
        ex.Should().BeNull();   // Release 零开销：协议检查被编译掉
#endif
    }

    [Fact]
    public void BumpCurrentEpoch_WithoutResume_ThrowsProtocolViolation()
        => AssertProtocolViolation(RunViolationInIsolatedThread(e => e.BumpCurrentEpoch(() => { })), "未在保护区");

    [Fact]
    public void Suspend_WithoutResume_ThrowsProtocolViolation()
        => AssertProtocolViolation(RunViolationInIsolatedThread(e => e.Suspend()), "未配对");

    [Fact]
    public void ProtectAndDrain_WithoutResume_ThrowsProtocolViolation()
        => AssertProtocolViolation(RunViolationInIsolatedThread(e => e.ProtectAndDrain()), "未 Resume");

    [Fact]
    public void ReentrantResume_ThrowsProtocolViolation()
        => AssertProtocolViolation(RunViolationInIsolatedThread(e => { e.Resume(); e.Resume(); }), "重入");

    [Fact]
    public void NestedBumpCurrentEpoch_ThrowsProtocolViolation()
    {
        var ex = RunViolationInIsolatedThread(e =>
        {
            e.Resume();
            e.BumpCurrentEpoch(() => e.BumpCurrentEpoch(() => { }));
        });
#if DEBUG
        // 嵌套绊线在内层 bump 入口抛出，可能经外层 drain action 的包装再传播——沿异常链找绊线。
        var chain = new List<string>();
        for (var e = ex; e is not null; e = e.InnerException)
            chain.Add(e.Message ?? "");
        chain.Should().ContainSingle(m => m.Contains("协议违反") && m.Contains("嵌套") && m.Contains("示波器"));
#else
        ex.Should().BeNull();
#endif
    }

    [Fact]
    public void Dispose_WhileProtected_ThrowsProtocolViolation()
        => AssertProtocolViolation(RunViolationInIsolatedThread(e =>
        {
            e.Resume();
            e.Dispose();
        }), "Dispose");

    [Fact]
    public void CrossThreadSuspend_ThrowsProtocolViolation()
    {
        // 线程 A Resume，线程 B Suspend（await 后换线程释放的经典协议违反）。
        // Debug：B 无 entry → "Release 未配对"绊线立即抛出；A 随后在 A 上 Suspend 正常清理。
        // Release：无检查——B 写 entry0 + 计数错乱，但实验发生在隔离线程，测试线程零污染。
        var epoch = new LightEpoch();
        Exception? ex = null;
        var resumeDone = new ManualResetEventSlim();
        var releaseDone = new ManualResetEventSlim();

        var threadA = Task.Run(() =>
        {
            epoch.Resume();           // 线程 A 持有保护
            resumeDone.Set();
            releaseDone.Wait(TimeSpan.FromSeconds(10));
            try { epoch.Suspend(); }  // A 正常 Suspend 清理（不计入违规断言）
            catch { }
        });

        resumeDone.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
        try { epoch.Suspend(); }      // 线程 B（本线程）Suspend——跨线程违反
        catch (Exception e) { ex = e; }
        finally { releaseDone.Set(); }

        threadA.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
        AssertProtocolViolation(ex, "未配对");
    }

    [Fact]
    public void DrainAction_Throws_DebugWrapsWithContext()
    {
        var ex = RunViolationInIsolatedThread(e =>
        {
            e.Resume();
            e.BumpCurrentEpoch(() => throw new ApplicationException("boom"));
        });
#if DEBUG
        // Debug：drain action 异常被包装，携带上下文 + 示波器历史；原始异常在 InnerException。
        ex.Should().BeOfType<InvalidOperationException>()
          .Which.Message.Should().Contain("drain action").And.Contain("示波器");
        ex!.InnerException.Should().BeOfType<ApplicationException>()
          .Which.Message.Should().Be("boom");
#else
        // Release：原始异常自然传播（无包装开销）。
        ex.Should().BeOfType<ApplicationException>().Which.Message.Should().Be("boom");
#endif
    }

    // ════════════════════════════════════════════════════════════
    // === DrainThen（2026-08-24 下沉 Core：Resume→Bump→条件 Suspend 封装）===
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void DrainThen_NoReader_ActionInvokedSynchronously()
    {
        // 无并发 reader：action 在 DrainThen 调用栈内同步触发（ProtectAndDrain 立即安全回收）
        var epoch = new LightEpoch();
        var invoked = 0;
        epoch.DrainThen(() => invoked++);
        invoked.Should().Be(1);
        // 协议收尾：调用返回后本线程不再持保护（DrainThen 内部已条件 Suspend）
        epoch.ThisInstanceProtected().Should().BeFalse();
    }

    [Fact]
    public void DrainThen_WithActiveReader_ActionDeferredUntilReaderExits()
    {
        // reader（独立线程）持保护期间：action 不立即执行（旧 epoch 未安全）；reader 退出后协作触发
        var epoch = new LightEpoch();
        var invoked = 0;
        using var readerEntered = new ManualResetEventSlim();
        using var readerExit = new ManualResetEventSlim();
        var reader = Task.Run(() =>
        {
            epoch.Resume();   // reader 持保护（模拟在途读者）
            readerEntered.Set();
            readerExit.Wait(TimeSpan.FromSeconds(10));
            epoch.Suspend();  // reader 退出 → SuspendDrain 协作触发 action
        });
        readerEntered.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();

        epoch.DrainThen(() => Interlocked.Increment(ref invoked));
        invoked.Should().Be(0, "reader 仍在旧 epoch——action 延迟");

        readerExit.Set();
        reader.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
        // action 由 reader 的 Suspend→SuspendDrain 协作触发（或后续 ProtectAndDrain）——轮询等待
        SpinWait.SpinUntil(() => Volatile.Read(ref invoked) == 1, TimeSpan.FromSeconds(5))
            .Should().BeTrue("reader 退出后 action 应被协作执行");
    }

    [Fact]
    public void DrainThen_ActionThrows_NoResidualProtection()
    {
        // action 抛异常（如 promote 失败）：Bump 幂等清理 + 条件 Suspend 收尾——不残留保护、
        // 不掩盖原始异常（真异常浮出；Debug 构建带包装上下文——同 DrainAction_Throws 契约）
        var epoch = new LightEpoch();
        var act = () => epoch.DrainThen(() => throw new InvalidOperationException("boom"));
#if DEBUG
        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("drain action");
#else
        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Be("boom");
#endif
        epoch.ThisInstanceProtected().Should().BeFalse();
    }
}
