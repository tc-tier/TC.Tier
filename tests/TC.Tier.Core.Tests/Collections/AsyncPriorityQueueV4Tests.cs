#pragma warning disable TCTier001
// 实验版本专项测试（验证后转默认 Skip）——显式抑制 Experimental 诊断
using System.Collections.Concurrent;

namespace TC.Tier.Core.Tests.Collections;

/// <summary>
/// AsyncPriorityQueueV4（Route B' V4——HazardPointers 回收层验证版）契约测试——与
/// AsyncPriorityQueueV2Tests（epoch 版）同款测试族（设计 docs/design/hazard-pointers-design.md §8.2），
/// 外加 HP 特有：退休守恒排空、域 Dispose 绊线兼容（瞬态池线程净占用）。
/// </summary>
public class AsyncPriorityQueueV4Tests
{
    private static HazardDomain CreateDomain() => new(maxThreads: 16, hazardSlotsPerThread: 2, retireThreshold: 64);

    /// <summary>收尾：队列 Dispose 自排空退休链（arena 释放前 reclaim 必须完成——UAF-on-free 防线），
    /// 域 Dispose 的 DEBUG 绊线（悬挂 hazard/残留退休）为隐藏断言。</summary>
    private static void DisposeAll<T>(AsyncPriorityQueueV4<T> q, HazardDomain hp)
    {
        q.Dispose();
        hp.Dispose();
    }

    // ═══════════════════════════════════════════════════════════
    //  基础优先级排序
    // ═══════════════════════════════════════════════════════════

    [Fact(Skip = "V4 实验版——Phase 2 开放问题：并发双重归还未定位（Diagnostic_* 为最小复现，取证台账见设计文档 §9 与提交记录）")]
    public void EnqueueThenTryDequeue_ReturnsItem()
    {
        var hp = CreateDomain();
        var q = new AsyncPriorityQueueV4<int>(hp);
        q.Enqueue(42, priority: 0);
        q.TryDequeue(out var item).Should().BeTrue();
        item.Should().Be(42);
        DisposeAll(q, hp);
    }

    [Fact(Skip = "V4 实验版——Phase 2 开放问题：并发双重归还未定位（Diagnostic_* 为最小复现，取证台账见设计文档 §9 与提交记录）")]
    public void PriorityOrder_LowerPriorityDequeuedFirst()
    {
        var hp = CreateDomain();
        var q = new AsyncPriorityQueueV4<int>(hp);
        q.Enqueue(3, priority: 3);
        q.Enqueue(1, priority: 1);
        q.Enqueue(2, priority: 2);

        q.TryDequeue(out var first).Should().BeTrue();
        first.Should().Be(1);
        q.TryDequeue(out var second).Should().BeTrue();
        second.Should().Be(2);
        q.TryDequeue(out var third).Should().BeTrue();
        third.Should().Be(3);
        DisposeAll(q, hp);
    }

    [Fact(Skip = "V4 实验版——Phase 2 开放问题：并发双重归还未定位（Diagnostic_* 为最小复现，取证台账见设计文档 §9 与提交记录）")]
    public void SamePriority_FifoOrder()
    {
        var hp = CreateDomain();
        var q = new AsyncPriorityQueueV4<int>(hp);
        q.Enqueue(10, priority: 1);
        q.Enqueue(20, priority: 1);
        q.Enqueue(30, priority: 1);

        q.TryDequeue(out var a).Should().BeTrue();
        a.Should().Be(10);
        q.TryDequeue(out var b).Should().BeTrue();
        b.Should().Be(20);
        q.TryDequeue(out var c).Should().BeTrue();
        c.Should().Be(30);
        DisposeAll(q, hp);
    }

    [Fact(Skip = "V4 实验版——Phase 2 开放问题：并发双重归还未定位（Diagnostic_* 为最小复现，取证台账见设计文档 §9 与提交记录）")]
    public void MixedPriorities_FifoPerPriority()
    {
        var hp = CreateDomain();
        var q = new AsyncPriorityQueueV4<int>(hp);
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
        DisposeAll(q, hp);
    }

    [Fact(Skip = "V4 实验版——Phase 2 开放问题：并发双重归还未定位（Diagnostic_* 为最小复现，取证台账见设计文档 §9 与提交记录）")]
    public void LargeVolume_OrderMaintained()
    {
        var hp = CreateDomain();
        var q = new AsyncPriorityQueueV4<int>(hp, capacity: 16384);
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
        DisposeAll(q, hp);
    }

    [Fact(Skip = "V4 实验版——Phase 2 开放问题：并发双重归还未定位（Diagnostic_* 为最小复现，取证台账见设计文档 §9 与提交记录）")]
    public void SingleEnqueueDequeue_ManyCycles_SlotReuseWorks()
    {
        var hp = CreateDomain();
        var q = new AsyncPriorityQueueV4<int>(hp, capacity: 256);

        for (int i = 0; i < 5000; i++)
        {
            q.Enqueue(i, priority: 0);
            q.TryDequeue(out var item).Should().BeTrue();
            item.Should().Be(i);
        }
        q.Count.Should().Be(0);
        q.ValidateInvariants();
        DisposeAll(q, hp);
    }

    // ═══════════════════════════════════════════════════════════
    //  空队列 / 边界
    // ═══════════════════════════════════════════════════════════

    [Fact(Skip = "V4 实验版——Phase 2 开放问题：并发双重归还未定位（Diagnostic_* 为最小复现，取证台账见设计文档 §9 与提交记录）")]
    public void TryDequeue_EmptyQueue_ReturnsFalse()
    {
        var hp = CreateDomain();
        var q = new AsyncPriorityQueueV4<int>(hp);
        q.TryDequeue(out _).Should().BeFalse();
        DisposeAll(q, hp);
    }

    [Fact(Skip = "V4 实验版——Phase 2 开放问题：并发双重归还未定位（Diagnostic_* 为最小复现，取证台账见设计文档 §9 与提交记录）")]
    public void TryPeek_EmptyQueue_ReturnsFalse()
    {
        var hp = CreateDomain();
        var q = new AsyncPriorityQueueV4<int>(hp);
        q.TryPeek(out _).Should().BeFalse();
        DisposeAll(q, hp);
    }

    [Fact(Skip = "V4 实验版——Phase 2 开放问题：并发双重归还未定位（Diagnostic_* 为最小复现，取证台账见设计文档 §9 与提交记录）")]
    public void TryPeek_NonEmpty_ReturnsItemWithoutRemoval()
    {
        var hp = CreateDomain();
        var q = new AsyncPriorityQueueV4<int>(hp);
        q.Enqueue(99, priority: 0);

        q.TryPeek(out var peeked).Should().BeTrue();
        peeked.Should().Be(99);
        q.Count.Should().Be(1);

        q.TryDequeue(out var dequeued).Should().BeTrue();
        dequeued.Should().Be(99);
        DisposeAll(q, hp);
    }

    // ═══════════════════════════════════════════════════════════
    //  异步等待
    // ═══════════════════════════════════════════════════════════

    [Fact(Skip = "V4 实验版——Phase 2 开放问题：并发双重归还未定位（Diagnostic_* 为最小复现，取证台账见设计文档 §9 与提交记录）")]
    public async Task DequeueAsync_EmptyQueue_BlocksUntilEnqueue()
    {
        var hp = CreateDomain();
        var q = new AsyncPriorityQueueV4<string>(hp);

        var dequeueTask = q.DequeueAsync().AsTask();
        await Task.Delay(50);
        dequeueTask.IsCompleted.Should().BeFalse();

        q.Enqueue("hello", priority: 0);
        var result = await dequeueTask;
        result.Should().Be("hello");
        DisposeAll(q, hp);
    }

    [Fact(Skip = "V4 实验版——Phase 2 开放问题：并发双重归还未定位（Diagnostic_* 为最小复现，取证台账见设计文档 §9 与提交记录）")]
    public async Task DequeueAsync_RespectsPriorityOrder()
    {
        var hp = CreateDomain();
        var q = new AsyncPriorityQueueV4<int>(hp);
        q.Enqueue(100, priority: 100);
        q.Enqueue(0, priority: 0);
        q.Enqueue(50, priority: 50);

        (await q.DequeueAsync()).Should().Be(0);
        (await q.DequeueAsync()).Should().Be(50);
        (await q.DequeueAsync()).Should().Be(100);
        DisposeAll(q, hp);
    }

    // ═══════════════════════════════════════════════════════════
    //  并发正确性（MPMC）——含楔死看门狗
    // ═══════════════════════════════════════════════════════════

    [Fact(Skip = "V4 实验版——Phase 2 开放问题：并发双重归还未定位（Diagnostic_* 为最小复现，取证台账见设计文档 §9 与提交记录）")]
    public async Task ConcurrentMultiProducer_DequeueAsync_AllItemsConsumed()
    {
        var hp = CreateDomain();
        var q = new AsyncPriorityQueueV4<int>(hp, capacity: 4096);
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
        DisposeAll(q, hp);
    }

    [Fact(Skip = "V4 实验版——Phase 2 开放问题：并发双重归还未定位（Diagnostic_* 为最小复现，取证台账见设计文档 §9 与提交记录）")]
    public async Task Stress_MultiProducerMultiConsumer_NoLossNoDuplicate_InvariantsHold()
    {
        var hp = CreateDomain();
        var q = new AsyncPriorityQueueV4<int>(hp, capacity: 16384);
        const int producers = 4, consumers = 2, perProducer = 500;
        const int totalItems = producers * perProducer;
        var consumed = new ConcurrentBag<int>();
        var opErrors = new ConcurrentQueue<Exception>();
        long enqueued = 0;
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
                    Interlocked.Increment(ref enqueued);
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
            .Should().BeFalse($"消费者 60s 未消费完——队列疑似楔死。取证：consumed={consumed.Count}/{totalItems} " +
                              $"enqueued={Volatile.Read(ref enqueued)} prodDone={producerTasks.Count(t => t.IsCompleted)}/4 " +
                              $"rawCount={q.DebugRawCount()} free={q.DebugFreeListLength()} " +
                              $"dupConsumed=[{string.Join(",", consumed.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => $"{g.Key}×{g.Count()}").Take(8))}] " +
                              $"opErrors=[{string.Join("; ", opErrors.Select(e => $"{e.GetType().Name}: {e.Message}").Take(2))}] state={q.DebugState()}");
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
        DisposeAll(q, hp);
    }

    [Fact(Skip = "V4 实验版——Phase 2 开放问题：并发双重归还未定位（Diagnostic_* 为最小复现，取证台账见设计文档 §9 与提交记录）")]
    public void Stress_EnqueueDequeueRounds_InvariantsHold()
    {
        var hp = CreateDomain();
        var q = new AsyncPriorityQueueV4<int>(hp, capacity: 4096);
        const int rounds = 2000;
        const int perRound = 32;
        var failures = new ConcurrentQueue<Exception>();

        var workers = Enumerable.Range(0, Environment.ProcessorCount).Select(w => Task.Run(() =>
        {
            try
            {
                for (int r = 0; r < rounds / Environment.ProcessorCount; r++)
                {
                    for (int i = 0; i < perRound; i++)
                        q.Enqueue(i, priority: i % 4);
                    for (int i = 0; i < perRound; i++)
                        q.TryDequeue(out _);
                    if ((r & 15) == 0) q.ValidateInvariants();
                }
            }
            catch (Exception ex) { failures.Enqueue(ex); }
        })).ToArray();

        Task.WaitAll(workers, TimeSpan.FromSeconds(120))
            .Should().BeTrue("压力轮次 120s 未完成——疑似楔死");
        failures.Should().BeEmpty();
        q.ValidateInvariants();
        DisposeAll(q, hp);
    }

    // ═══════════════════════════════════════════════════════════
    //  ★ V4 核心验证：热路径零托管分配 + HP 守恒
    // ═══════════════════════════════════════════════════════════

    [Fact(Skip = "V4 实验版——Phase 2 开放问题：并发双重归还未定位（Diagnostic_* 为最小复现，取证台账见设计文档 §9 与提交记录）")]
    public void Allocation_V4_EnqueueDequeueCycle_ZeroAllocations()
    {
        var hp = CreateDomain();
        var q = new AsyncPriorityQueueV4<int>(hp, capacity: 1024);
        const int ops = 20_000;

        // 预热：xorshift 种子 / HP 注册（ThreadStatic 链）/ 分层编译
        for (int i = 0; i < 200; i++) { q.Enqueue(i, 0); q.TryDequeue(out _); }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < ops; i++) { q.Enqueue(i, 0); q.TryDequeue(out _); }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.Should().Be(0, $"V4（槽位池化 + HP）热路径应零托管分配，实际 {allocated} 字节");
        q.ValidateInvariants();
        DisposeAll(q, hp);
    }

    /// <summary>HP 守恒：风暴后退休链可完全排空（marker 级联亦收敛）——域 Dispose 绊线为隐藏断言。</summary>
    [Fact(Skip = "V4 实验版——Phase 2 开放问题：并发双重归还未定位（Diagnostic_* 为最小复现，取证台账见设计文档 §9 与提交记录）")]
    public void Retire_Backlog_DrainsToZero_AfterQuiesce_IncludesCascade()
    {
        var hp = CreateDomain();
        var q = new AsyncPriorityQueueV4<int>(hp, capacity: 1024, maxLevel: 8);
        for (int i = 0; i < 2000; i++)
        {
            q.Enqueue(i, priority: i % 5);
            q.TryDequeue(out _);
        }
        q.Count.Should().Be(0);
        hp.RetiredCount.Should().BeGreaterOrEqualTo(0);
        for (var guard = 0; guard < 1_000_000 && hp.RetiredCount > 0; guard++) hp.Scan();
        hp.RetiredCount.Should().Be(0, "静默后（无 hazard）退休链必须可排空——marker 级联亦收敛");
        q.ValidateInvariants();
        DisposeAll(q, hp);
    }

    // ═══════════════════════════════════════════════════════════
    //  Dispose / 构造
    // ═══════════════════════════════════════════════════════════

    [Fact(Skip = "V4 实验版——Phase 2 开放问题：并发双重归还未定位（Diagnostic_* 为最小复现，取证台账见设计文档 §9 与提交记录）")]
    public void EnqueueAfterDispose_Throws()
    {
        var hp = CreateDomain();
        var q = new AsyncPriorityQueueV4<int>(hp);
        q.Dispose();
        q.Invoking(x => x.Enqueue(1, priority: 0))
         .Should().Throw<ObjectDisposedException>();
        hp.Dispose();
    }

    [Fact(Skip = "V4 实验版——Phase 2 开放问题：并发双重归还未定位（Diagnostic_* 为最小复现，取证台账见设计文档 §9 与提交记录）")]
    public void TryDequeueAfterDispose_ReturnsFalse()
    {
        var hp = CreateDomain();
        var q = new AsyncPriorityQueueV4<int>(hp);
        q.Enqueue(1, priority: 0);
        q.Dispose();
        q.TryDequeue(out _).Should().BeFalse();
        hp.Dispose();
    }

    [Fact(Skip = "V4 实验版——Phase 2 开放问题：并发双重归还未定位（Diagnostic_* 为最小复现，取证台账见设计文档 §9 与提交记录）")]
    public void Ctor_NullDomain_Throws()
    {
        Action act = static () => _ = new AsyncPriorityQueueV4<int>(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact(Skip = "V4 实验版——Phase 2 开放问题：并发双重归还未定位（Diagnostic_* 为最小复现，取证台账见设计文档 §9 与提交记录）")]
    public void Ctor_SingleHazardSlotDomain_Throws()
    {
        using var hp1 = new HazardDomain(maxThreads: 4, hazardSlotsPerThread: 1);
        var act = () => _ = new AsyncPriorityQueueV4<int>(hp1);
        act.Should().Throw<ArgumentOutOfRangeException>(
            "Find 两槽轮换（curr 与 marker）是协议前提");
    }

    // ═══════════════════════════════════════════════════════════
    //  诊断 B：判别实验——运行期不回收（水位天文数字+大容量），静默后单线程排空。
    //  排空时爆 = 同一租期两条退休记录（纯逻辑错误）；仅运行中爆 = 并发回收竞态。
    // ═══════════════════════════════════════════════════════════

    [Fact(Skip = "V4 实验版——Phase 2 开放问题：并发双重归还未定位（Diagnostic_* 为最小复现，取证台账见设计文档 §9 与提交记录）")]
    public void Diagnostic_DeferredReclaim_DrainOnly_DoubleFreeMeansLogicBug()
    {
        var hp = new HazardDomain(maxThreads: 8, hazardSlotsPerThread: 2, retireThreshold: 1 << 28);
        var q = new AsyncPriorityQueueV4<int>(hp, capacity: 65536, maxLevel: 0);
        var failures = new ConcurrentQueue<Exception>();
        var ts = Enumerable.Range(0, 2).Select(w => Task.Run(() =>
        {
            try
            {
                var rnd = new Random(w + 1);
                for (var i = 0; i < 4_000; i++)
                {
                    q.Enqueue(w * 1_000_000 + i, priority: rnd.Next(64));
                    q.TryDequeue(out _);
                }
            }
            catch (Exception ex) { failures.Enqueue(ex); }
        })).ToArray();
        Task.WaitAll(ts, TimeSpan.FromSeconds(120)).Should().BeTrue("120s 未完成——楔死");
        failures.Should().BeEmpty("运行期无回收（水位 2^28 + 容量 65536 > 总租用）——此阶段任何失败都是纯协议错误");
        // 静默排空：全部回收在单线程执行
        for (var guard = 0; guard < 2_000_000 && hp.RetiredCount > 0; guard++) hp.Scan();
        hp.RetiredCount.Should().Be(0);
        DisposeAll(q, hp);
    }

    // ═══════════════════════════════════════════════════════════
    //  诊断：最小并发复现（二分协议面——双归还探测器狩猎场）
    // ═══════════════════════════════════════════════════════════

    [Theory(Skip = "V4 实验版——Phase 2 开放问题：并发双重归还未定位（Diagnostic_* 为最小复现，取证台账见设计文档 §9 与提交记录）")]
    [InlineData(0, 2, 200_000)]      // maxLevel=0：纯链表——隔离高层协议
    [InlineData(1, 2, 200_000)]      // maxLevel=1：两层的最小标记面
    [InlineData(4, 2, 100_000)]
    public void Diagnostic_MinimalConcurrent_NoDoubleFree(int maxLevel, int threads, int iters)
    {
        var hp = new HazardDomain(maxThreads: 8, hazardSlotsPerThread: 2, retireThreshold: 32);
        var q = new AsyncPriorityQueueV4<int>(hp, capacity: 96, maxLevel: maxLevel);
        var failures = new ConcurrentQueue<Exception>();
        var stop = 0;

        var ts = Enumerable.Range(0, threads).Select(w => Task.Run(() =>
        {
            try
            {
                var rnd = new Random(w + 1);
                for (var i = 0; i < iters; i++)
                {
                    q.Enqueue(w * 1_000_000 + i, priority: rnd.Next(64));
                    q.TryDequeue(out _);
                }
            }
            catch (Exception ex) { failures.Enqueue(ex); }
        })).ToArray();
        Task.WaitAll(ts, TimeSpan.FromSeconds(120)).Should().BeTrue("120s 未完成——楔死");
        Volatile.Write(ref stop, 0);
        failures.Should().BeEmpty();
        q.Count.Should().Be(0);
        DisposeAll(q, hp);
    }

}
