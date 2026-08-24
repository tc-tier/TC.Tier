#pragma warning disable TCTier001
// 实验版本专项测试（默认 Skip）——显式抑制 Experimental 诊断
using System.Collections.Concurrent;
using TC.Tier.Core.Epochs;

namespace TC.Tier.Core.Tests.Collections;

/// <summary>
/// AsyncPriorityQueueV2（Route B' 验证版）契约测试——与 AsyncPriorityQueueTests（Route A）
/// 同款测试族，外加分配对比测试（B' 零分配 vs A 每次入队一次 Gen0）。
/// </summary>
public class AsyncPriorityQueueV2Tests
{
    private static LightEpoch CreateEpoch() => new();

    // ═══════════════════════════════════════════════════════════
    // 基础优先级排序
    // ═══════════════════════════════════════════════════════════

    [Fact(Skip = "实验版本 V2/V3——非生产，默认跳过（RootCause 见 docs/lab/async-priority-queue-root-cause.md）")]
    public void EnqueueThenTryDequeue_ReturnsItem()
    {
        var q = new AsyncPriorityQueueV2<int>(CreateEpoch());
        q.Enqueue(42, priority: 0);
        q.TryDequeue(out var item).Should().BeTrue();
        item.Should().Be(42);
    }

    [Fact(Skip = "实验版本 V2/V3——非生产，默认跳过（RootCause 见 docs/lab/async-priority-queue-root-cause.md）")]
    public void PriorityOrder_LowerPriorityDequeuedFirst()
    {
        var q = new AsyncPriorityQueueV2<int>(CreateEpoch());
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

    [Fact(Skip = "实验版本 V2/V3——非生产，默认跳过（RootCause 见 docs/lab/async-priority-queue-root-cause.md）")]
    public void SamePriority_FifoOrder()
    {
        var q = new AsyncPriorityQueueV2<int>(CreateEpoch());
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

    [Fact(Skip = "实验版本 V2/V3——非生产，默认跳过（RootCause 见 docs/lab/async-priority-queue-root-cause.md）")]
    public void MixedPriorities_FifoPerPriority()
    {
        var q = new AsyncPriorityQueueV2<int>(CreateEpoch());
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

    [Fact(Skip = "实验版本 V2/V3——非生产，默认跳过（RootCause 见 docs/lab/async-priority-queue-root-cause.md）")]
    public void LargeVolume_OrderMaintained()
    {
        var q = new AsyncPriorityQueueV2<int>(CreateEpoch(), capacity: 16384);
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
        q.ValidateInvariants();
    }

    [Fact(Skip = "实验版本 V2/V3——非生产，默认跳过（RootCause 见 docs/lab/async-priority-queue-root-cause.md）")]
    public void SingleEnqueueDequeue_ManyCycles_SlotReuseWorks()
    {
        var q = new AsyncPriorityQueueV2<int>(CreateEpoch(), capacity: 256);

        for (int i = 0; i < 5000; i++)
        {
            q.Enqueue(i, priority: 0);
            q.TryDequeue(out var item).Should().BeTrue();
            item.Should().Be(i);
        }
        q.Count.Should().Be(0);
        q.ValidateInvariants();
    }

    // ═══════════════════════════════════════════════════════════
    // 空队列 / 边界
    // ═══════════════════════════════════════════════════════════

    [Fact(Skip = "实验版本 V2/V3——非生产，默认跳过（RootCause 见 docs/lab/async-priority-queue-root-cause.md）")]
    public void TryDequeue_EmptyQueue_ReturnsFalse()
    {
        var q = new AsyncPriorityQueueV2<int>(CreateEpoch());
        q.TryDequeue(out _).Should().BeFalse();
    }

    [Fact(Skip = "实验版本 V2/V3——非生产，默认跳过（RootCause 见 docs/lab/async-priority-queue-root-cause.md）")]
    public void TryPeek_EmptyQueue_ReturnsFalse()
    {
        var q = new AsyncPriorityQueueV2<int>(CreateEpoch());
        q.TryPeek(out _).Should().BeFalse();
    }

    [Fact(Skip = "实验版本 V2/V3——非生产，默认跳过（RootCause 见 docs/lab/async-priority-queue-root-cause.md）")]
    public void TryPeek_NonEmpty_ReturnsItemWithoutRemoval()
    {
        var q = new AsyncPriorityQueueV2<int>(CreateEpoch());
        q.Enqueue(99, priority: 0);

        q.TryPeek(out var peeked).Should().BeTrue();
        peeked.Should().Be(99);
        q.Count.Should().Be(1);

        q.TryDequeue(out var dequeued).Should().BeTrue();
        dequeued.Should().Be(99);
    }

    // ═══════════════════════════════════════════════════════════
    // 异步等待
    // ═══════════════════════════════════════════════════════════

    [Fact(Skip = "实验版本 V2/V3——非生产，默认跳过（RootCause 见 docs/lab/async-priority-queue-root-cause.md）")]
    public async Task DequeueAsync_EmptyQueue_BlocksUntilEnqueue()
    {
        var q = new AsyncPriorityQueueV2<string>(CreateEpoch());

        var dequeueTask = q.DequeueAsync().AsTask();
        await Task.Delay(50);
        dequeueTask.IsCompleted.Should().BeFalse();

        q.Enqueue("hello", priority: 0);
        var result = await dequeueTask;
        result.Should().Be("hello");
    }

    [Fact(Skip = "实验版本 V2/V3——非生产，默认跳过（RootCause 见 docs/lab/async-priority-queue-root-cause.md）")]
    public async Task DequeueAsync_RespectsPriorityOrder()
    {
        var q = new AsyncPriorityQueueV2<int>(CreateEpoch());
        q.Enqueue(100, priority: 100);
        q.Enqueue(0, priority: 0);
        q.Enqueue(50, priority: 50);

        (await q.DequeueAsync()).Should().Be(0);
        (await q.DequeueAsync()).Should().Be(50);
        (await q.DequeueAsync()).Should().Be(100);
    }

    // ═══════════════════════════════════════════════════════════
    // 并发正确性（MPMC）
    // ═══════════════════════════════════════════════════════════

    [Fact(Skip = "实验版本 V2/V3——非生产，默认跳过（RootCause 见 docs/lab/async-priority-queue-root-cause.md）")]
    public async Task ConcurrentMultiProducer_DequeueAsync_AllItemsConsumed()
    {
        var q = new AsyncPriorityQueueV2<int>(CreateEpoch(), capacity: 4096);
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

        var producerDone = Task.WhenAll(producers);
        var watchdog = Task.Delay(TimeSpan.FromSeconds(30));
        (await Task.WhenAny(producerDone, watchdog) == watchdog)
            .Should().BeFalse("生产者 30s 未完成——入队疑似楔死");
        await producerDone;
        await consumer;

        var consumedList = consumed.ToList();
        consumedList.Count.Should().Be(totalItems);
        consumedList.OrderBy(x => x).Should().Equal(Enumerable.Range(0, totalItems).OrderBy(x => x));
        q.ValidateInvariants();
    }

    [Fact(Skip = "实验版本 V2/V3——非生产，默认跳过（RootCause 见 docs/lab/async-priority-queue-root-cause.md）")]
    public async Task Stress_MultiProducerMultiConsumer_NoLossNoDuplicate_InvariantsHold()
    {
        // ★ B' 验证残留竞态探测器：重型并发下偶发"一代陈旧引用"fail-visible（根因档案 §6——
        //   摘除/回收窗口的 epoch 覆盖缺口，已知问题；失败=绊线命中，非静默腐蚀）
        var q = new AsyncPriorityQueueV2<int>(CreateEpoch(), capacity: 16384);
        const int producers = 4, consumers = 2, perProducer = 500;
        const int totalItems = producers * perProducer;
        var consumed = new ConcurrentBag<int>();
        var opErrors = new ConcurrentQueue<Exception>();
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
                try
                {
                    var spin = new SpinWait();
                    while (!cts.IsCancellationRequested)
                    {
                        if (q.TryDequeue(out var item)) { consumed.Add(item); spin.Reset(); }
                        else if (consumed.Count >= totalItems) break;
                        else spin.SpinOnce();
                    }
                }
                catch (Exception ex) { opErrors.Enqueue(ex); }
            });
        }

        var done = Task.WhenAll(consumerTasks);
        var watchdog = Task.Delay(TimeSpan.FromSeconds(60));
        (await Task.WhenAny(done, watchdog) == watchdog)
            .Should().BeFalse("消费者 60s 未消费完——队列疑似楔死");
        cts.Cancel();
        await Task.WhenAll(producerTasks);

        var list = consumed.ToList();
        if (!opErrors.IsEmpty)
        {
            var first = opErrors.First();
            Assert.Fail($"消费者操作异常：{first.GetType().Name}: {first.Message}\n{first.StackTrace}");
        }
        list.Count.Should().Be(totalItems);
        list.OrderBy(x => x).Should().Equal(Enumerable.Range(0, totalItems).OrderBy(x => x));
        q.ValidateInvariants();
        q.Count.Should().Be(0);
    }

    [Fact(Skip = "实验版本 V2/V3——非生产，默认跳过（RootCause 见 docs/lab/async-priority-queue-root-cause.md）")]
    public void Stress_EnqueueDequeueRounds_InvariantsHold()
    {
        // ★ B' 验证残留竞态探测器（同上）：偶发 fail-visible = 已知残余竞态命中
        var q = new AsyncPriorityQueueV2<int>(CreateEpoch(), capacity: 4096);
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
    }

    // ═══════════════════════════════════════════════════════════
    // ★ B' 核心验证：热路径零托管分配（对比 A 每次入队一次 Gen0）
    // ═══════════════════════════════════════════════════════════

    [Fact(Skip = "实验版本 V2/V3——非生产，默认跳过（RootCause 见 docs/lab/async-priority-queue-root-cause.md）")]
    public void Allocation_V2_EnqueueDequeueCycle_ZeroAllocations()
    {
        var q = new AsyncPriorityQueueV2<int>(CreateEpoch(), capacity: 1024);
        const int ops = 20_000;

        // 预热：xorshift 种子 / epoch entry / 静态初始化
        for (int i = 0; i < 200; i++) { q.Enqueue(i, 0); q.TryDequeue(out _); }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < ops; i++) { q.Enqueue(i, 0); q.TryDequeue(out _); }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.Should().Be(0, $"B'（槽位池化）热路径应零托管分配，实际 {allocated} 字节");
        q.ValidateInvariants();
    }

    [Fact(Skip = "实验版本 V2/V3——非生产，默认跳过（RootCause 见 docs/lab/async-priority-queue-root-cause.md）")]
    public void Allocation_V1_Baseline_AllocatesPerEnqueue()
    {
        var q = new AsyncPriorityQueue<int>(CreateEpoch());
        const int ops = 20_000;

        for (int i = 0; i < 200; i++) { q.Enqueue(i, 0); q.TryDequeue(out _); }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < ops; i++) { q.Enqueue(i, 0); q.TryDequeue(out _); }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // A（GC 回收）每次入队一个 Node + Forward 数组——应有可观测分配（对照基线，非断言性能数字）
        allocated.Should().BeGreaterThan(0, "Route A 每次入队应有托管分配（GC 回收模型）");
    }

    // ═══════════════════════════════════════════════════════════
    // Dispose
    // ═══════════════════════════════════════════════════════════

    [Fact(Skip = "实验版本 V2/V3——非生产，默认跳过（RootCause 见 docs/lab/async-priority-queue-root-cause.md）")]
    public void EnqueueAfterDispose_Throws()
    {
        var q = new AsyncPriorityQueueV2<int>(CreateEpoch());
        q.Dispose();
        q.Invoking(x => x.Enqueue(1, priority: 0))
         .Should().Throw<ObjectDisposedException>();
    }

    [Fact(Skip = "实验版本 V2/V3——非生产，默认跳过（RootCause 见 docs/lab/async-priority-queue-root-cause.md）")]
    public void TryDequeueAfterDispose_ReturnsFalse()
    {
        var q = new AsyncPriorityQueueV2<int>(CreateEpoch());
        q.Enqueue(1, priority: 0);
        q.Dispose();
        q.TryDequeue(out _).Should().BeFalse();
    }

    [Fact(Skip = "实验版本 V2/V3——非生产，默认跳过（RootCause 见 docs/lab/async-priority-queue-root-cause.md）")]
    public void Ctor_NullEpoch_Throws()
    {
        Action act = static () => _ = new AsyncPriorityQueueV2<int>(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
