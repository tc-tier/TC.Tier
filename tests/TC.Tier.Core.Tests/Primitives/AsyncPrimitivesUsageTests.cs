using TC.Tier.Core.Collections;
using TC.Tier.Core.Epochs;
using TC.Tier.Core.Primitives;

// 测试代码合理地存 ValueTask 到局部以观察 IsCompleted（验证异步原语的完成时机），
// 这是 CA2012 警告的合法例外，故整个测试文件抑制。
#pragma warning disable CA2012

namespace TC.Tier.Core.Tests.Primitives;

/// <summary>
/// docs/async-primitives.md 用法范式验证——测过才算文档成立。
/// 覆盖 AsyncManualResetEvent / AsyncCountDown / AsyncQueue / BucketPriorityQueue / SkipListPriorityQueue / AsyncPriorityQueue。
/// </summary>
public class AsyncPrimitivesUsageTests
{
    // ── AsyncManualResetEvent ──

    [Fact]
    public void AsyncMRE_InitialSet_WaitReturnsImmediately()
    {
        var ev = new AsyncManualResetEvent(initialState: true);
        ev.IsSet.Should().BeTrue();
        ev.WaitAsync().IsCompleted.Should().BeTrue();   // 已 set → 同步完成
    }

    [Fact]
    public async Task AsyncMRE_Set_WakesWaiter()
    {
        var ev = new AsyncManualResetEvent(initialState: false);
        var waitTask = ev.WaitAsync();
        waitTask.IsCompleted.Should().BeFalse();        // 未 set → 异步等

        ev.Set();
        await waitTask;                                  // 被唤醒
        ev.IsSet.Should().BeTrue();
    }

    [Fact]
    public void AsyncMRE_Reset_MakesWaitBlockAgain()
    {
        var ev = new AsyncManualResetEvent(initialState: true);
        ev.Reset();
        ev.IsSet.Should().BeFalse();
        ev.WaitAsync().IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task AsyncMRE_Set_BroadcastsToMultipleWaiters()
    {
        var ev = new AsyncManualResetEvent(initialState: false);
        var t1 = ev.WaitAsync();
        var t2 = ev.WaitAsync();
        var t3 = ev.WaitAsync();

        ev.Set();
        await t1; await t2; await t3;                    // 全部唤醒（Set 后均同步完成）
    }

    // ── AsyncCountDown ──

    [Fact]
    public async Task CountDown_AddRemove_WaitUntilEmpty()
    {
        var cd = new AsyncCountDown();
        cd.Add(); cd.Add(); cd.Add();   // 计数 3

        var wait = cd.WaitUntilEmptyAsync();
        wait.IsCompleted.Should().BeFalse();

        cd.Remove(); cd.Remove();
        wait.IsCompleted.Should().BeFalse();   // 还剩 1

        cd.Remove();                             // 归 0
        await wait;                              // 唤醒
    }

    [Fact]
    public async Task CountDown_ConcurrentRemove_WaitsAll()
    {
        var cd = new AsyncCountDown();
        const int N = 20;
        for (int i = 0; i < N; i++) cd.Add();

        var wait = cd.WaitUntilEmptyAsync();
        await Task.WhenAll(Enumerable.Range(0, N).Select(_ => Task.Run(() => cd.Remove())));
        await wait;                              // 全 Remove 完成后唤醒
    }

    // ── AsyncQueue ──

    [Fact]
    public async Task AsyncQueue_EnqueueDequeue_Roundtrip()
    {
        var q = new AsyncQueue<int>();
        q.Enqueue(1);
        q.Enqueue(2);
        q.Count.Should().Be(2);

        (await q.DequeueAsync()).Should().Be(1);
        (await q.DequeueAsync()).Should().Be(2);
    }

    [Fact]
    public void AsyncQueue_TryDequeue_EmptyReturnsFalse()
    {
        var q = new AsyncQueue<int>();
        q.TryDequeue(out _).Should().BeFalse();
    }

    [Fact]
    public async Task AsyncQueue_DequeueAsync_WaitsForEnqueue()
    {
        var q = new AsyncQueue<string>();
        var take = q.DequeueAsync();

        await Task.Delay(10);
        take.IsCompleted.Should().BeFalse();     // 空时等

        q.Enqueue("hi");
        (await take).Should().Be("hi");           // 入队后唤醒
    }

    // ── BucketPriorityQueue ──

    // ⚠️ BucketPriorityQueue 按优先级值升序扫描——值小的枚举优先出（高优先级 = 数值小）
    private enum Prio { High = 0, Mid = 1, Low = 2 }

    [Fact]
    public async Task BucketPQ_DequeuesByPriority()
    {
        using var pq = new BucketPriorityQueue<Prio, string>();
        pq.Enqueue("low", Prio.Low);
        pq.Enqueue("high", Prio.High);
        pq.Enqueue("mid", Prio.Mid);

        (await pq.DequeueAsync()).Should().Be("high");   // High=0 最先出（升序，小值优先）
        (await pq.DequeueAsync()).Should().Be("mid");
        (await pq.DequeueAsync()).Should().Be("low");
    }

    [Fact]
    public void BucketPQ_TryDequeue_EmptyReturnsFalse()
    {
        using var pq = new BucketPriorityQueue<Prio, int>();
        pq.TryDequeue(out _).Should().BeFalse();
        pq.TryPeek(out _).Should().BeFalse();
    }

    // ── SkipListPriorityQueue ──

    [Fact]
    public async Task SkipListPQ_DequeuesByLongPriority()
    {
        using var pq = new SkipListPriorityQueue<string>();
        pq.Enqueue("c", priority: 30);
        pq.Enqueue("a", priority: 10);
        pq.Enqueue("b", priority: 20);

        (await pq.DequeueAsync()).Should().Be("a");   // 最小 priority 先出
        (await pq.DequeueAsync()).Should().Be("b");
        (await pq.DequeueAsync()).Should().Be("c");
    }

    [Fact]
    public void SkipListPQ_TryPeek_DoesNotRemove()
    {
        using var pq = new SkipListPriorityQueue<int>();
        pq.Enqueue(99, priority: 1);
        pq.TryPeek(out var top).Should().BeTrue();
        top.Should().Be(99);
        pq.Count.Should().Be(1);   // peek 不出队
    }

    // ── AsyncPriorityQueue（需 LightEpoch）──

    [Fact]
    public async Task AsyncPQ_WithEpoch_DequeuesByPriority()
    {
        using var epoch = new LightEpoch();
        using var pq = new AsyncPriorityQueue<string>(epoch);
        pq.Enqueue("mid", priority: 5);
        pq.Enqueue("high", priority: 1);    // 小 = 高优先
        pq.Enqueue("low", priority: 9);

        (await pq.DequeueAsync()).Should().Be("high");
        (await pq.DequeueAsync()).Should().Be("mid");
        (await pq.DequeueAsync()).Should().Be("low");
    }

    [Fact]
    public void AsyncPQ_TryDequeue_EmptyReturnsFalse()
    {
        using var epoch = new LightEpoch();
        using var pq = new AsyncPriorityQueue<int>(epoch);
        pq.TryDequeue(out _).Should().BeFalse();
        pq.TryPeek(out _).Should().BeFalse();
    }
}
