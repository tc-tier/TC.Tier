using TC.Tier.Core.Primitives;

namespace TC.Tier.Core.Tests.Primitives;

/// <summary>PooledValueTaskSource&lt;T&gt; 契约（带载荷池化完成源）：载荷直达/异常直达/先完成后注册兜底/
/// 归还重租无残留/取消传播/清理钩子单次。</summary>
public class PooledValueTaskSourceOfTTests
{
    private static async Task<T> AwaitAsync<T>(PooledValueTaskSource<T> s)
    {
        var vt = new ValueTask<T>(s, s.Version);
        return await vt;
    }

    [Fact]
    public async Task SetResult_PayloadDeliveredToAwaiter()
    {
        var s = PooledValueTaskSource<int>.Rent();
        var awaiter = AwaitAsync(s);
        s.SetResult(42);
        (await awaiter).Should().Be(42);
        PooledValueTaskSource<int>.Return(s);
    }

    [Fact]
    public async Task SetException_ExceptionSurfacedToAwaiter()
    {
        var s = PooledValueTaskSource<string>.Rent();
        var awaiter = AwaitAsync(s);
        s.SetException(new InvalidOperationException("boom"));
        var act = async () => await awaiter;
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("boom");
        PooledValueTaskSource<string>.Return(s);
    }

    [Fact]
    public async Task MarkOrComplete_BeforeRegistration_FallbackDeliversPayload()
    {
        // ★ 完成先于注册（#PERF-002 载荷版）：Mark 落暂存 → OnCompleted 兜底补完成 → 载荷不丢
        var s = PooledValueTaskSource<long>.Rent();
        s.MarkOrComplete(77L);
        (await AwaitAsync(s)).Should().Be(77L);
        PooledValueTaskSource<long>.Return(s);
    }

    [Fact]
    public async Task Return_Rent_Reset_NoStalePayloadOrState()
    {
        var s = PooledValueTaskSource<int>.Rent();
        var awaiter = AwaitAsync(s);
        s.SetResult(1);
        (await awaiter).Should().Be(1);
        PooledValueTaskSource<int>.Return(s);

        var s2 = PooledValueTaskSource<int>.Rent();
        if (s2 != s) PooledValueTaskSource<int>.Return(s2);   // 池空同实例直还
        var awaiter2 = AwaitAsync(s);
        s.SetResult(2);   // 重租后再完成——不得被旧载荷污染
        (await awaiter2).Should().Be(2);
        PooledValueTaskSource<int>.Return(s);
    }

    [Fact]
    public async Task AttachCancellation_TokenFires_OceToAwaiter()
    {
        var s = PooledValueTaskSource<int>.Rent();
        using var cts = new CancellationTokenSource(50);
        s.AttachCancellation(cts.Token);
        var awaiter = AwaitAsync(s);
        var act = async () => await awaiter;
        await act.Should().ThrowAsync<OperationCanceledException>();
        PooledValueTaskSource<int>.Return(s);
    }

    [Fact]
    public async Task CleanupHook_InvokedOnce_AfterCompletion()
    {
        var s = PooledValueTaskSource<int>.Rent();
        var cleanupCount = 0;
        s.CleanupState = null;
        s.OnCleanup = (_, _) => Interlocked.Increment(ref cleanupCount);
        var awaiter = AwaitAsync(s);
        s.SetResult(9);
        (await awaiter).Should().Be(9);
        cleanupCount.Should().Be(1, "GetResult 完成态清理钩子恰一次");
        s.OnCleanup = null;
        PooledValueTaskSource<int>.Return(s);
    }

    [Fact]
    public async Task ConcurrentStress_CompleteAndAwait_PairsHold()
    {
        // 并发租借/完成/等待/归还守恒——多线程租借互不串扰（版本号代际正确性）
        const int total = 2000;
        var results = new int[total];
        await Task.WhenAll(Enumerable.Range(0, total).Select(async i =>
        {
            var s = PooledValueTaskSource<int>.Rent();
            var awaiter = AwaitAsync(s);
            s.SetResult(i);
            results[i] = await awaiter;
            PooledValueTaskSource<int>.Return(s);
        }));
        results.Should().BeEquivalentTo(Enumerable.Range(0, total));
    }
}
