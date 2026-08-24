namespace TC.Tier.Core.Tests.Collections;

/// <summary>
/// 测试用枚举优先级——4 级，值非连续（验证稀疏枚举映射）。
/// Low=0, Normal=5, High=10, Critical=15。
/// </summary>
public enum TestPriority
{
    Low = 0,
    Normal = 5,
    High = 10,
    Critical = 15,
}

/// <summary>
/// BucketPriorityQueue 单元测试。
/// 覆盖：枚举优先级顺序、同优先级 FIFO、空队列、异步等待/取消、MPMC 并发正确性。
/// </summary>
public class BucketPriorityQueueTests
{
    private static BucketPriorityQueue<TestPriority, T> NewQueue<T>() => new();

    // ════════════════════════════════════════════════════════════
    //  基础正确性
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void EnqueueDequeue_SingleItem()
    {
        var q = NewQueue<int>();
        q.Count.Should().Be(0);

        q.Enqueue(42, TestPriority.Normal);
        q.Count.Should().Be(1);

        q.TryDequeue(out var item).Should().BeTrue();
        item.Should().Be(42);
        q.Count.Should().Be(0);
    }

    [Fact]
    public void TryDequeue_EmptyQueue_ReturnsFalse()
    {
        var q = NewQueue<string>();
        q.TryDequeue(out var item).Should().BeFalse();
        item.Should().BeNull();
    }

    [Fact]
    public void Count_TracksSize()
    {
        var q = NewQueue<int>();
        q.Enqueue(1, TestPriority.Low);
        q.Enqueue(2, TestPriority.High);
        q.Enqueue(3, TestPriority.Critical);
        q.Count.Should().Be(3);

        q.TryDequeue(out _);
        q.Count.Should().Be(2);
    }

    // ════════════════════════════════════════════════════════════
    //  优先级顺序（严格——值小的先出）
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void Dequeue_StrictPriorityOrder()
    {
        // 乱序入队不同优先级——出队必须按枚举值升序（Low→Normal→High→Critical）
        var q = NewQueue<string>();
        q.Enqueue("critical", TestPriority.Critical);   // 15
        q.Enqueue("low", TestPriority.Low);             // 0
        q.Enqueue("high", TestPriority.High);           // 10
        q.Enqueue("normal", TestPriority.Normal);       // 5

        q.TryDequeue(out var a).Should().BeTrue();
        q.TryDequeue(out var b).Should().BeTrue();
        q.TryDequeue(out var c).Should().BeTrue();
        q.TryDequeue(out var d).Should().BeTrue();
        q.TryDequeue(out _).Should().BeFalse();

        a.Should().Be("low");       // 0
        b.Should().Be("normal");    // 5
        c.Should().Be("high");      // 10
        d.Should().Be("critical");  // 15
    }

    [Fact]
    public void Dequeue_SamePriority_FifoOrdering()
    {
        // 同优先级按入队顺序 FIFO
        var q = NewQueue<int>();
        for (var i = 0; i < 100; i++)
            q.Enqueue(i, TestPriority.Normal);

        for (var i = 0; i < 100; i++)
        {
            q.TryDequeue(out var item).Should().BeTrue();
            item.Should().Be(i, $"同优先级 FIFO：期望 {i}");
        }
    }

    [Fact]
    public void Dequeue_InterleavedPriorities_StrictOrdering()
    {
        // 交叉入队不同优先级——出队仍严格按优先级
        var q = NewQueue<int>();
        q.Enqueue(1, TestPriority.High);
        q.Enqueue(2, TestPriority.Low);     // Low 优先级更高（值小），应先出
        q.Enqueue(3, TestPriority.High);
        q.Enqueue(4, TestPriority.Low);

        q.TryDequeue(out var a).Should().BeTrue();
        q.TryDequeue(out var b).Should().BeTrue();
        q.TryDequeue(out var c).Should().BeTrue();
        q.TryDequeue(out var d).Should().BeTrue();

        a.Should().Be(2);   // Low 先入
        b.Should().Be(4);   // Low 后入
        c.Should().Be(1);   // High 先入
        d.Should().Be(3);   // High 后入
    }

    [Fact]
    public void TryPeek_DoesNotRemove()
    {
        var q = NewQueue<int>();
        q.Enqueue(10, TestPriority.High);
        q.Enqueue(5, TestPriority.Low);    // Low 值小，应被 Peek

        q.TryPeek(out var peek).Should().BeTrue();
        peek.Should().Be(5);
        q.Count.Should().Be(2);   // Peek 不删

        q.TryDequeue(out var first).Should().BeTrue();
        first.Should().Be(5);     // 确认 Low 确实先出
    }

    // ════════════════════════════════════════════════════════════
    //  异步等待 / 取消
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task DequeueAsync_EmptyQueue_BlocksUntilEnqueue()
    {
        var q = NewQueue<string>();
        var dequeueTask = q.DequeueAsync().AsTask();

        await Task.Delay(50);
        dequeueTask.IsCompleted.Should().BeFalse();

        q.Enqueue("hello", TestPriority.Normal);
        var result = await dequeueTask;
        result.Should().Be("hello");
    }

    [Fact]
    public async Task DequeueAsync_WithCancellation_Throws()
    {
        var q = NewQueue<int>();
        using var cts = new CancellationTokenSource(100);

        var act = async () => await q.DequeueAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task DequeueAsync_FastPath_NoBlock()
    {
        var q = NewQueue<int>();
        q.Enqueue(99, TestPriority.Critical);

        var item = await q.DequeueAsync();
        item.Should().Be(99);
    }

    [Fact]
    public async Task DequeueAsync_MultipleWaiters_AllServed()
    {
        // 多个消费者等待——入队后逐一唤醒
        var q = NewQueue<int>();
        var consumers = new Task<int>[3];
        for (var i = 0; i < 3; i++)
            consumers[i] = q.DequeueAsync().AsTask();

        await Task.Delay(50);
        foreach (var c in consumers) c.IsCompleted.Should().BeFalse();

        q.Enqueue(11, TestPriority.Normal);
        q.Enqueue(22, TestPriority.Normal);
        q.Enqueue(33, TestPriority.Normal);

        var results = await Task.WhenAll(consumers);
        results.Should().BeEquivalentTo(s_expectedMultipleWaiterResults);
    }

    private static readonly int[] s_expectedMultipleWaiterResults = { 11, 22, 33 };

    // ════════════════════════════════════════════════════════════
    //  并发正确性（MPMC）
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task Concurrent_NoLossNoDuplication()
    {
        const int Producers = 4;
        const int PerProducer = 5000;
        var q = NewQueue<int>();
        var total = Producers * PerProducer;
        var produced = new System.Collections.Concurrent.ConcurrentBag<int>();
        var consumed = new System.Collections.Concurrent.ConcurrentBag<int>();

        var producers = Enumerable.Range(0, Producers).Select(p => Task.Run(() =>
        {
            for (var i = 0; i < PerProducer; i++)
            {
                var val = p * PerProducer + i;
                produced.Add(val);
                // 轮转优先级
                var prio = (TestPriority)(i % 4 * 5);   // 0,5,10,15
                q.Enqueue(val, prio);
            }
        })).ToArray();

        var consumers = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            while (consumed.Count < total)
            {
                if (q.TryDequeue(out var item))
                    consumed.Add(item);
                else
                    Thread.SpinWait(10);
            }
        })).ToArray();

        await Task.WhenAll(producers);
        await Task.WhenAll(consumers);

        consumed.Count.Should().Be(total, "无丢失");
        produced.OrderBy(x => x).Should().BeEquivalentTo(consumed.OrderBy(x => x), "出队集合与入队集合完全一致（无丢失无重复）");
    }

    [Theory]
    [InlineData(8, 2000)]
    [InlineData(16, 1000)]
    public async Task Concurrent_HighContention_NoLoss(int producers, int perProducer)
    {
        var q = NewQueue<int>();
        var total = producers * perProducer;
        var consumed = new System.Collections.Concurrent.ConcurrentBag<int>();

        var prodTasks = Enumerable.Range(0, producers).Select(p => Task.Run(() =>
        {
            for (var i = 0; i < perProducer; i++)
            {
                var val = p * perProducer + i;
                q.Enqueue(val, (TestPriority)((val % 4) * 5));
            }
        })).ToArray();

        var consTask = Task.Run(() =>
        {
            while (consumed.Count < total)
            {
                if (q.TryDequeue(out var item)) consumed.Add(item);
                else Thread.SpinWait(10);
            }
        });

        await Task.WhenAll(prodTasks);
        await consTask;

        consumed.Count.Should().Be(total);
        consumed.Distinct().Count().Should().Be(total, "无重复");
    }

    [Fact]
    public void Stress_NoDeadlockProgressMade()
    {
        // 混合压测——验证无死锁、无活锁（进度推进）、无崩溃
        var q = NewQueue<int>();
        var stop = false;
        long enqueued = 0, dequeued = 0;

        var threads = new Thread[6];
        for (var t = 0; t < 4; t++)
        {
            threads[t] = new Thread(() =>
            {
                var rng = new Random(t);
                while (!Volatile.Read(ref stop))
                {
                    q.Enqueue(rng.Next(0, 1000), (TestPriority)(rng.Next(4) * 5));
                    Interlocked.Increment(ref enqueued);
                }
            }) { IsBackground = true };
        }
        for (var t = 4; t < 6; t++)
        {
            threads[t] = new Thread(() =>
            {
                while (!Volatile.Read(ref stop) || Interlocked.Read(ref dequeued) < Interlocked.Read(ref enqueued))
                {
                    if (q.TryDequeue(out _)) Interlocked.Increment(ref dequeued);
                    else Thread.SpinWait(10);
                }
            }) { IsBackground = true };
        }

        foreach (var th in threads) th.Start();
        Thread.Sleep(2000);
        Volatile.Write(ref stop, true);
        foreach (var th in threads) th.Join(5000);

        Interlocked.Read(ref enqueued).Should().BeGreaterThan(0, "入队有进度");
        Interlocked.Read(ref dequeued).Should().BeGreaterThan(0, "出队有进度");
    }
}
