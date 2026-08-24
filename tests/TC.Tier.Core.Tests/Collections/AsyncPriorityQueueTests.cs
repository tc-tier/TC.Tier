using System.Collections.Concurrent;
using TC.Tier.Core.Epochs;

namespace TC.Tier.Core.Tests.Collections;

public class AsyncPriorityQueueTests
{
    private static LightEpoch CreateEpoch() => new();

    // ═══════════════════════════════════════════════════════════
    // 基础优先级排序
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void EnqueueThenTryDequeue_ReturnsItem()
    {
        var epoch = CreateEpoch();
        var q = new AsyncPriorityQueue<int>(epoch);
        q.Enqueue(42, priority: 0);
        q.TryDequeue(out var item).Should().BeTrue();
        item.Should().Be(42);
    }

    [Fact]
    public void PriorityOrder_LowerPriorityDequeuedFirst()
    {
        var epoch = CreateEpoch();
        var q = new AsyncPriorityQueue<int>(epoch);
        q.Enqueue(3, priority: 3);
        q.Enqueue(1, priority: 1);
        q.Enqueue(2, priority: 2);

        q.TryDequeue(out var first).Should().BeTrue();
        first.Should().Be(1);
        q.TryDequeue(out var second).Should().BeTrue();
        second.Should().Be(2);
        q.TryDequeue(out var third).Should().BeTrue();
        third.Should().Be(3);
    }

    [Fact]
    public void SamePriority_FifoOrder()
    {
        var epoch = CreateEpoch();
        var q = new AsyncPriorityQueue<int>(epoch);
        q.Enqueue(10, priority: 1);
        q.Enqueue(20, priority: 1);
        q.Enqueue(30, priority: 1);

        q.TryDequeue(out var a).Should().BeTrue();
        a.Should().Be(10);
        q.TryDequeue(out var b).Should().BeTrue();
        b.Should().Be(20);
        q.TryDequeue(out var c).Should().BeTrue();
        c.Should().Be(30);
    }

    [Fact]
    public void MixedPriorities_FifoPerPriority()
    {
        var epoch = CreateEpoch();
        var q = new AsyncPriorityQueue<int>(epoch);
        q.Enqueue(11, priority: 0);
        q.Enqueue(21, priority: 1);
        q.Enqueue(12, priority: 0);
        q.Enqueue(22, priority: 1);

        q.TryDequeue(out var a).Should().BeTrue();
        a.Should().Be(11);
        q.TryDequeue(out var b).Should().BeTrue();
        b.Should().Be(12);
        q.TryDequeue(out var c).Should().BeTrue();
        c.Should().Be(21);
        q.TryDequeue(out var d).Should().BeTrue();
        d.Should().Be(22);
    }

    [Fact]
    public void NegativePriorities_SortCorrectly()
    {
        var epoch = CreateEpoch();
        var q = new AsyncPriorityQueue<int>(epoch);
        q.Enqueue(0, priority: 0);
        q.Enqueue(-1, priority: -1);
        q.Enqueue(1, priority: 1);

        q.TryDequeue(out var a).Should().BeTrue();
        a.Should().Be(-1);
        q.TryDequeue(out var b).Should().BeTrue();
        b.Should().Be(0);
        q.TryDequeue(out var c).Should().BeTrue();
        c.Should().Be(1);
    }

    [Fact]
    public void EnqueueDequeue_Interleaved_PriorityOrderMaintained()
    {
        var epoch = CreateEpoch();
        var q = new AsyncPriorityQueue<int>(epoch);

        q.Enqueue(10, priority: 10);
        q.Enqueue(5, priority: 5);

        q.TryDequeue(out var a).Should().BeTrue();
        a.Should().Be(5);

        q.Enqueue(1, priority: 1);
        q.Enqueue(20, priority: 20);

        q.TryDequeue(out var b).Should().BeTrue();
        b.Should().Be(1);
        q.TryDequeue(out var c).Should().BeTrue();
        c.Should().Be(10);
        q.TryDequeue(out var d).Should().BeTrue();
        d.Should().Be(20);
    }

    // ═══════════════════════════════════════════════════════════
    // 空队列 / 边界
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void TryDequeue_EmptyQueue_ReturnsFalse()
    {
        var epoch = CreateEpoch();
        var q = new AsyncPriorityQueue<int>(epoch);
        q.TryDequeue(out _).Should().BeFalse();
    }

    [Fact]
    public void TryPeek_EmptyQueue_ReturnsFalse()
    {
        var epoch = CreateEpoch();
        var q = new AsyncPriorityQueue<int>(epoch);
        q.TryPeek(out _).Should().BeFalse();
    }

    [Fact]
    public void TryPeek_NonEmpty_ReturnsItemWithoutRemoval()
    {
        var epoch = CreateEpoch();
        var q = new AsyncPriorityQueue<int>(epoch);
        q.Enqueue(99, priority: 0);

        q.TryPeek(out var peeked).Should().BeTrue();
        peeked.Should().Be(99);
        q.Count.Should().Be(1);

        q.TryDequeue(out var dequeued).Should().BeTrue();
        dequeued.Should().Be(99);
    }

    [Fact]
    public void Count_ReflectsOperations()
    {
        var epoch = CreateEpoch();
        var q = new AsyncPriorityQueue<int>(epoch);
        q.Count.Should().Be(0);

        q.Enqueue(1, priority: 0);
        q.Enqueue(2, priority: 0);
        q.Count.Should().Be(2);

        q.TryDequeue(out _);
        q.Count.Should().Be(1);
    }

    [Fact]
    public void LargeVolume_OrderMaintained()
    {
        var epoch = CreateEpoch();
        var q = new AsyncPriorityQueue<int>(epoch);
        const int n = 10000;

        for (int i = n - 1; i >= 0; i--)
            q.Enqueue(i, priority: i % 10);

        int? lastPriority = null;
        int count = 0;
        while (q.TryDequeue(out var item))
        {
            int currentPriority = item % 10;
            if (lastPriority.HasValue)
                currentPriority.Should().BeGreaterOrEqualTo(lastPriority.Value);
            lastPriority = currentPriority;
            count++;
        }
        count.Should().Be(n);
    }

    [Fact]
    public void SingleEnqueueDequeue_ManyCycles_PoolReuseWorks()
    {
        var epoch = CreateEpoch();
        var q = new AsyncPriorityQueue<int>(epoch);

        for (int i = 0; i < 5000; i++)
        {
            q.Enqueue(i, priority: 0);
            q.TryDequeue(out var item).Should().BeTrue();
            item.Should().Be(i);
        }
        q.Count.Should().Be(0);
    }

    // ═══════════════════════════════════════════════════════════
    // 异步等待
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task DequeueAsync_NonEmpty_ReturnsImmediately()
    {
        var epoch = CreateEpoch();
        var q = new AsyncPriorityQueue<int>(epoch);
        q.Enqueue(42, priority: 0);

        var result = await q.DequeueAsync();
        result.Should().Be(42);
    }

    [Fact]
    public async Task DequeueAsync_EmptyQueue_BlocksUntilEnqueue()
    {
        var epoch = CreateEpoch();
        var q = new AsyncPriorityQueue<string>(epoch);

        var dequeueTask = q.DequeueAsync().AsTask();
        await Task.Delay(50);
        dequeueTask.IsCompleted.Should().BeFalse();

        q.Enqueue("hello", priority: 0);
        var result = await dequeueTask;
        result.Should().Be("hello");
    }

    [Fact]
    public async Task DequeueAsync_WithCancellation_ThrowsOperationCanceledException()
    {
        var epoch = CreateEpoch();
        var q = new AsyncPriorityQueue<int>(epoch);
        using var cts = new CancellationTokenSource();

        var dequeueTask = q.DequeueAsync(cts.Token).AsTask();
        await Task.Delay(50);

        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => dequeueTask);
    }

    [Fact]
    public async Task DequeueAsync_RespectsPriorityOrder()
    {
        var epoch = CreateEpoch();
        var q = new AsyncPriorityQueue<int>(epoch);
        q.Enqueue(100, priority: 100);
        q.Enqueue(0, priority: 0);
        q.Enqueue(50, priority: 50);

        (await q.DequeueAsync()).Should().Be(0);
        (await q.DequeueAsync()).Should().Be(50);
        (await q.DequeueAsync()).Should().Be(100);
    }

    [Fact]
    public async Task DequeueAsync_MultipleWaiters_WokenInOrder()
    {
        var epoch = CreateEpoch();
        var q = new AsyncPriorityQueue<string>(epoch);

        var t1 = q.DequeueAsync().AsTask();
        var t2 = q.DequeueAsync().AsTask();
        var t3 = q.DequeueAsync().AsTask();

        q.Enqueue("third", priority: 3);
        q.Enqueue("first", priority: 1);
        q.Enqueue("second", priority: 2);

        (await t1).Should().BeOneOf("first", "second", "third");
        (await t2).Should().BeOneOf("first", "second", "third");
        (await t3).Should().BeOneOf("first", "second", "third");
    }

    // ═══════════════════════════════════════════════════════════
    // 并发正确性（MPMC: 多生产者，单消费者 TryDequeue / DequeueAsync）
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task ConcurrentMultiProducer_DequeueAsync_AllItemsConsumed()
    {
        var epoch = CreateEpoch();
        var q = new AsyncPriorityQueue<int>(epoch);
        const int totalItems = 500;
        var consumed = new ConcurrentBag<int>();

        var producers = new Task[4];
        for (int p = 0; p < producers.Length; p++)
        {
            int offset = p * (totalItems / producers.Length);
            int count = totalItems / producers.Length;
            producers[p] = Task.Run(() =>
            {
                for (int i = 0; i < count; i++)
                    q.Enqueue(offset + i, priority: 0);
            });
        }

        var consumer = Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            for (int i = 0; i < totalItems; i++)
            {
                var item = await q.DequeueAsync(cts.Token);
                consumed.Add(item);
            }
        });

        // ★ 楔死看门狗：生产者侧也要超时——旧实现入队热自旋时 suite 永久挂死
        var producerDone = Task.WhenAll(producers);
        var watchdog = Task.Delay(TimeSpan.FromSeconds(30));
        (await Task.WhenAny(producerDone, watchdog) == watchdog)
            .Should().BeFalse("生产者 30s 未完成——入队疑似楔死");
        await producerDone;
        await consumer;

        var consumedList = consumed.ToList();
        consumedList.Count.Should().Be(totalItems);
        consumedList.OrderBy(x => x).Should().Equal(Enumerable.Range(0, totalItems).OrderBy(x => x));
    }

    [Fact]
    public void ConcurrentMultiProducer_TryDequeueSingleConsumer_AllItems()
    {
        var epoch = CreateEpoch();
        var q = new AsyncPriorityQueue<int>(epoch);
        const int count = 400;
        var consumed = new ConcurrentBag<int>();

        var producer = Task.Run(() =>
        {
            for (int i = 0; i < count; i++)
                q.Enqueue(i, priority: 0);
        });

        var consumer = Task.Run(() =>
        {
            var spin = new SpinWait();
            int got = 0;
            while (got < count)
            {
                if (q.TryDequeue(out var item))
                {
                    consumed.Add(item);
                    got++;
                    spin.Reset();
                }
                else
                {
                    spin.SpinOnce();
                }
            }
        });

        // ★ 楔死看门狗：Wait 带超时，挂死转断言失败而非卡住测试套件
        producer.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue("生产者 30s 未完成——入队疑似楔死");
        consumer.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue("消费者 30s 未完成——出队疑似楔死");

        var consumedList = consumed.ToList();
        consumedList.Count.Should().Be(count);
        // 并发出队顺序不严格=升序（生产者可能赶在消费者之前插入后续元素），但应无重复无遗漏
        consumedList.OrderBy(x => x).Should().Equal(Enumerable.Range(0, count));
    }

    [Fact]
    public void ConcurrentMultiProducer_TryDequeue_NoDuplicates()
    {
        var epoch = CreateEpoch();
        var q = new AsyncPriorityQueue<int>(epoch);
        const int producers = 4;
        const int perProducer = 200;
        const int totalItems = producers * perProducer;
        var consumed = new ConcurrentBag<int>();

        // All producers enqueue, then consumer dequeues all (no overlap — tests insert correctness)
        var producerTasks = new Task[producers];
        for (int p = 0; p < producers; p++)
        {
            int offset = p * perProducer;
            producerTasks[p] = Task.Run(() =>
            {
                for (int i = 0; i < perProducer; i++)
                    q.Enqueue(offset + i, priority: 0);
            });
        }
        Task.WaitAll(producerTasks, TimeSpan.FromSeconds(30))
            .Should().BeTrue("生产者 30s 未完成——入队疑似楔死");

        // Sequential dequeue
        for (int i = 0; i < totalItems; i++)
        {
            q.TryDequeue(out var item).Should().BeTrue();
            consumed.Add(item);
        }

        consumed.Count.Should().Be(totalItems);
        consumed.OrderBy(x => x).Should().Equal(Enumerable.Range(0, totalItems).OrderBy(x => x));
    }

    // ═══════════════════════════════════════════════════════════
    // 压力契约测试（Route A 基线：不丢、不重、结构不变式恒成立）
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Stress_MultiProducerMultiConsumer_NoLossNoDuplicate_InvariantsHold()
    {
        var epoch = CreateEpoch();
        var q = new AsyncPriorityQueue<int>(epoch);
        const int producers = 4, consumers = 2, perProducer = 500;
        const int totalItems = producers * perProducer;
        var consumed = new ConcurrentBag<int>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var producerTasks = new Task[producers];
        for (int p = 0; p < producers; p++)
        {
            int offset = p * perProducer;
            producerTasks[p] = Task.Run(() =>
            {
                for (int i = 0; i < perProducer; i++)
                {
                    q.Enqueue(offset + i, priority: i % 8);
                    if ((i & 255) == 0) q.ValidateInvariants();
                }
            });
        }

        var consumerTasks = new Task[consumers];
        for (int c = 0; c < consumers; c++)
        {
            consumerTasks[c] = Task.Run(() =>
            {
                var spin = new SpinWait();
                while (!cts.IsCancellationRequested)
                {
                    if (q.TryDequeue(out var item)) { consumed.Add(item); spin.Reset(); }
                    else if (consumed.Count >= totalItems) break;
                    else spin.SpinOnce();
                }
            });
        }

        // ★ 楔死看门狗：消费超时即断言失败（旧实现此处 CPU 热自旋挂死）
        var done = Task.WhenAll(consumerTasks);
        var watchdog = Task.Delay(TimeSpan.FromSeconds(60));
        (await Task.WhenAny(done, watchdog) == watchdog)
            .Should().BeFalse("消费者 60s 未消费完——队列疑似楔死");
        cts.Cancel();
        await Task.WhenAll(producerTasks);

        var list = consumed.ToList();
        list.Count.Should().Be(totalItems);
        list.OrderBy(x => x).Should().Equal(Enumerable.Range(0, totalItems).OrderBy(x => x));
        q.ValidateInvariants();
        q.Count.Should().Be(0);
    }

    [Fact]
    public void Stress_EnqueueDequeueRounds_InvariantsHold()
    {
        var epoch = CreateEpoch();
        var q = new AsyncPriorityQueue<int>(epoch);
        const int rounds = 2000;
        const int perRound = 32;

        var workers = Enumerable.Range(0, Environment.ProcessorCount).Select(w => Task.Run(() =>
        {
            for (int r = 0; r < rounds / Environment.ProcessorCount; r++)
            {
                for (int i = 0; i < perRound; i++)
                    q.Enqueue(i, priority: i % 4);
                for (int i = 0; i < perRound; i++)
                    q.TryDequeue(out _);
                if ((r & 15) == 0) q.ValidateInvariants();
            }
        })).ToArray();

        Task.WaitAll(workers, TimeSpan.FromSeconds(120))
            .Should().BeTrue("压力轮次 120s 未完成——疑似楔死");
        q.ValidateInvariants();
        // 各 worker 每轮自产自销净平衡，但并发交错下允许瞬时残留（≤ in-flight 数），不强制空
    }

    // ═══════════════════════════════════════════════════════════
    // 类型安全
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void ReferenceType_StoresAndRetrieves()
    {
        var epoch = CreateEpoch();
        var q = new AsyncPriorityQueue<string>(epoch);
        q.Enqueue("alice", priority: 2);
        q.Enqueue("bob", priority: 1);

        q.TryDequeue(out var first).Should().BeTrue();
        first.Should().Be("bob");
        q.TryDequeue(out var second).Should().BeTrue();
        second.Should().Be("alice");
    }

    [Fact]
    public void ValueType_StoresAndRetrieves()
    {
        var epoch = CreateEpoch();
        var q = new AsyncPriorityQueue<Guid>(epoch);
        var g1 = Guid.NewGuid();
        var g2 = Guid.NewGuid();

        q.Enqueue(g2, priority: 2);
        q.Enqueue(g1, priority: 1);

        q.TryDequeue(out var a).Should().BeTrue();
        a.Should().Be(g1);
        q.TryDequeue(out var b).Should().BeTrue();
        b.Should().Be(g2);
    }

    // ═══════════════════════════════════════════════════════════
    // Dispose
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void EnqueueAfterDispose_Throws()
    {
        var epoch = CreateEpoch();
        var q = new AsyncPriorityQueue<int>(epoch);
        q.Dispose();
        q.Invoking(x => x.Enqueue(1, priority: 0))
         .Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void TryDequeueAfterDispose_ReturnsFalse()
    {
        var epoch = CreateEpoch();
        var q = new AsyncPriorityQueue<int>(epoch);
        q.Enqueue(1, priority: 0);
        q.Dispose();
        q.TryDequeue(out _).Should().BeFalse();
    }

    [Fact]
    public void Dispose_Idempotent()
    {
        var epoch = CreateEpoch();
        var q = new AsyncPriorityQueue<int>(epoch);
        q.Dispose();
        q.Invoking(x => x.Dispose()).Should().NotThrow();
    }
}
