namespace TC.Tier.Core.Tests.Primitives;

public class PooledValueTaskSourceTests
{
    [Fact]
    public async Task Rent_SetResult_AwaiterCompletes()
    {
        var source = PooledValueTaskSource.Rent();
        var vt = new ValueTask(source, source.Version);

        source.SetResult();

        await vt;
        vt.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task Rent_SetException_AwaiterThrows()
    {
        var source = PooledValueTaskSource.Rent();
        var vt = new ValueTask(source, source.Version);

        source.SetException(new InvalidOperationException("boom"));

        var act = async () => await vt;
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
    }

    [Fact]
    public async Task Rent_BeforeSet_AwaiterBlocks()
    {
        var source = PooledValueTaskSource.Rent();
        var vt = new ValueTask(source, source.Version);

        await Task.Delay(50);
        vt.IsCompleted.Should().BeFalse();

        source.SetResult();
        await vt;
    }

    [Fact]
    public void Return_AfterRent_RetrievesSameInstance()
    {
        // 预热：确保同一线程的 thread-local 栈有实例
        var s1 = PooledValueTaskSource.Rent();
        PooledValueTaskSource.Return(s1);

        // 再 Rent 应拿到刚归还的（LIFO）
        var s2 = PooledValueTaskSource.Rent();
        s2.Should().BeSameAs(s1);

        PooledValueTaskSource.Return(s2);
    }

    [Fact]
    public async Task Return_ResetsCore_ForReuse()
    {
        var s1 = PooledValueTaskSource.Rent();
        s1.SetResult();
        PooledValueTaskSource.Return(s1);

        // 归还后重新 Rent，应能正常使用（core 已 reset）
        var s2 = PooledValueTaskSource.Rent();
        s2.Should().BeSameAs(s1);

        var vt = new ValueTask(s2, s2.Version);
        s2.SetResult();
        await vt; // 不应抛"已被完成"异常
    }

    [Fact]
    public async Task AttachCancellation_TokenFires_ThrowsOCE()
    {
        var source = PooledValueTaskSource.Rent();
        using var cts = new CancellationTokenSource();
        source.AttachCancellation(cts.Token);

        var vt = new ValueTask(source, source.Version);

        cts.Cancel();

        var act = async () => await vt;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task AttachCancellation_SetResultBeforeCancel_CompletesNormally()
    {
        var source = PooledValueTaskSource.Rent();
        using var cts = new CancellationTokenSource();
        source.AttachCancellation(cts.Token);

        var vt = new ValueTask(source, source.Version);
        source.SetResult();

        await vt; // 不应抛异常
        cts.Cancel();
    }

    [Fact]
    public async Task MultipleWaiters_EachIndependentSource_AllComplete()
    {
        // 多 waiter 场景：每个 waiter 用独立的 source（正确的多 waiter 模式）
        var sources = new PooledValueTaskSource[4];
        var tasks = new ValueTask[4];

        for (int i = 0; i < 4; i++)
        {
            sources[i] = PooledValueTaskSource.Rent();
            tasks[i] = new ValueTask(sources[i], sources[i].Version);
        }

        // 逐个完成
        for (int i = 0; i < 4; i++)
        {
            sources[i].SetResult();
            await tasks[i];
        }
    }

    [Fact]
    public void RentMany_DoesNotLeak_HighVolume()
    {
        // 大量 Rent/Return 往返，确保池工作正常不泄漏
        for (int i = 0; i < 10000; i++)
        {
            var s = PooledValueTaskSource.Rent();
            PooledValueTaskSource.Return(s);
        }

        // 能正常 Rent 到实例即可
        var final = PooledValueTaskSource.Rent();
        final.Should().NotBeNull();
        PooledValueTaskSource.Return(final);
    }

    [Fact]
    public async Task Concurrent_RentReturnSet_ThreadSafe()
    {
        int errors = 0;
        var tasks = new Task[8];

        for (int t = 0; t < 8; t++)
        {
            tasks[t] = Task.Run(async () =>
            {
                for (int i = 0; i < 500; i++)
                {
                    try
                    {
                        var s = PooledValueTaskSource.Rent();
                        var vt = new ValueTask(s, s.Version);
                        s.SetResult();
                        await vt;
                        PooledValueTaskSource.Return(s);
                    }
                    catch
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
    public void Version_AfterReturn_ChangesForNewCycle()
    {
        var s = PooledValueTaskSource.Rent();
        var v1 = s.Version;
        PooledValueTaskSource.Return(s);

        // 同一实例归还后 version 应推进（Reset 会推进）
        var s2 = PooledValueTaskSource.Rent();
        if (s2 == s)
        {
            // 若取回同一实例，version 应不同
            s2.Version.Should().NotBe(v1);
        }
        PooledValueTaskSource.Return(s2);
    }
}
