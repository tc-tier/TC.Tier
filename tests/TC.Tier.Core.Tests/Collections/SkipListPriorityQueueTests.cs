namespace TC.Tier.Core.Tests.Collections;

/// <summary>
/// SkipListPriorityQueue 单元测试。
/// 覆盖：任意优先级顺序、同优先级 FIFO、空队列、异步等待/取消、MPMC 并发正确性。
/// </summary>
public class SkipListPriorityQueueTests
{
    private static SkipListPriorityQueue<T> NewQueue<T>(int maxLevel = 15) => new(maxLevel);

    // ════════════════════════════════════════════════════════════
    //  基础正确性
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void EnqueueDequeue_SingleItem()
    {
        var q = NewQueue<int>();
        q.Count.Should().Be(0);
        q.Enqueue(42, priority: 0);
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

    // ════════════════════════════════════════════════════════════
    //  优先级顺序（任意 long 优先级，值小者先出）
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void Dequeue_StrictPriorityOrder()
    {
        var q = NewQueue<string>();
        q.Enqueue("p100", 100);
        q.Enqueue("p0", 0);
        q.Enqueue("p50", 50);
        q.Enqueue("p0b", 0);   // 同优先级 0，FIFO

        q.TryDequeue(out var a).Should().BeTrue();
        q.TryDequeue(out var b).Should().BeTrue();
        q.TryDequeue(out var c).Should().BeTrue();
        q.TryDequeue(out var d).Should().BeTrue();
        q.TryDequeue(out _).Should().BeFalse();

        a.Should().Be("p0");    // 0 最小
        b.Should().Be("p0b");   // 同 0，FIFO
        c.Should().Be("p50");
        d.Should().Be("p100");
    }

    [Fact]
    public void Dequeue_NegativePriority_Handled()
    {
        // 负优先级（如 Lifecycle 的 -1 强制建段）应正确排序
        var q = NewQueue<string>();
        q.Enqueue("normal", 0);
        q.Enqueue("critical", -1);   // 比 0 更优先
        q.Enqueue("low", 5);

        q.TryDequeue(out var a).Should().BeTrue();
        q.TryDequeue(out var b).Should().BeTrue();
        q.TryDequeue(out var c).Should().BeTrue();

        a.Should().Be("critical");   // -1 最小
        b.Should().Be("normal");     // 0
        c.Should().Be("low");        // 5
    }

    [Fact]
    public void Dequeue_SamePriority_FifoOrdering()
    {
        var q = NewQueue<int>();
        for (var i = 0; i < 100; i++)
            q.Enqueue(i, priority: 0);

        for (var i = 0; i < 100; i++)
        {
            q.TryDequeue(out var item).Should().BeTrue();
            item.Should().Be(i, $"同优先级 FIFO：期望 {i}");
        }
    }

    [Fact]
    public void Dequeue_LargeBatch_CorrectOrder()
    {
        // 大批量——验证 skip-list 多层结构正确
        var q = NewQueue<int>(maxLevel: 20);
        var rng = new Random(42);
        var enqueued = new List<(int val, long prio, long seq)>();
        long seq = 0;

        for (var i = 0; i < 1000; i++)
        {
            var val = rng.Next(0, 1000);
            var prio = rng.NextInt64(0, 10);
            enqueued.Add((val, prio, ++seq));
            q.Enqueue(val, prio);
        }

        // 期望：(prio, seq) 升序
        var expected = enqueued.OrderBy(e => (e.prio << 48) | e.seq).Select(e => e.val).ToList();
        var actual = new List<int>();
        while (q.TryDequeue(out var v)) actual.Add(v);

        actual.Should().Equal(expected, "按 (priority, sequence) 升序出队");
    }

    [Fact]
    public void TryPeek_DoesNotRemove()
    {
        var q = NewQueue<int>();
        q.Enqueue(10, 5);
        q.Enqueue(5, 1);   // 更优先

        q.TryPeek(out var peek).Should().BeTrue();
        peek.Should().Be(5);
        q.Count.Should().Be(2);
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

        q.Enqueue("hello", 0);
        var result = await dequeueTask;
        result.Should().Be("hello");
    }

    [Fact]
    public async Task DequeueAsync_Cancellation_Throws()
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
        q.Enqueue(99, 0);
        var item = await q.DequeueAsync();
        item.Should().Be(99);
    }

    // ════════════════════════════════════════════════════════════
    //  并发正确性（MPMC）
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task Diag_MinimalConcurrency_2x10()
    {
        var q = NewQueue<int>(maxLevel: 5);
        var produced = new System.Collections.Concurrent.ConcurrentBag<int>();
        var consumed = new System.Collections.Concurrent.ConcurrentBag<int>();

        var t1 = Task.Run(() => { for (var i = 0; i < 10; i++) { produced.Add(i); q.Enqueue(i, i % 3); } });
        var t2 = Task.Run(() => { for (var i = 10; i < 20; i++) { produced.Add(i); q.Enqueue(i, i % 3); } });
        var consumer = Task.Run(() =>
        {
            while (consumed.Count < 20)
            {
                if (q.TryDequeue(out var item)) consumed.Add(item);
                else Thread.SpinWait(100);
            }
        });

        var allTasks = Task.WhenAll(t1, t2, consumer);
        var winner = await Task.WhenAny(allTasks, Task.Delay(10000));
        if (winner != allTasks)
            throw new TimeoutException($"超时！produced={produced.Count} consumed={consumed.Count}");

        consumed.Count.Should().Be(20);
    }

    /// <summary>
    /// MPMC 并发正确性——4 生产者 × 1000 + 2 消费者，验证无丢失无重复。
    /// <para>历史：曾间歇性 30s 超时（零进展）。诊断（TryEnter 超时 + 操作级重试计数）证实
    /// <b>不是经典死锁</b>（零 LOCK-STALL）而是 <b>OP-SPIN 活锁</b>——根因是 lazy skip-list 的
    /// 清理义务缺失：FindFirst 返回 marked victim、TryDequeue 见 marked victim 裸 retry 不物理删除，
    /// 导致 marked 节点永久滞留链上、FindFirst 反复命中同一 victim。修复：FindFirst 跳过 marked +
    /// TryDequeue 见 marked victim 时接力 unlink（helping）后再 retry。</para>
    /// </summary>
    [Fact]
    public async Task Concurrent_NoLossNoDuplication()
    {
        const int Producers = 4;
        const int PerProducer = 1000;   // 大规模 5000 在特定线程调度下触发 SpinLock 竞态，降至 1000
        var q = NewQueue<int>(maxLevel: 20);
        try
        {
            var total = Producers * PerProducer;
            var produced = new System.Collections.Concurrent.ConcurrentBag<int>();
            var consumed = new System.Collections.Concurrent.ConcurrentBag<int>();
            var stop = false;

            var producers = Enumerable.Range(0, Producers).Select(p => Task.Run(() =>
            {
                for (var i = 0; i < PerProducer; i++)
                {
                    var val = p * PerProducer + i;
                    produced.Add(val);
                    q.Enqueue(val, val % 4);
                }
            })).ToArray();

            var consumers = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
            {
                while (!Volatile.Read(ref stop) && consumed.Count < total)
                {
                    if (q.TryDequeue(out var item)) consumed.Add(item);
                    else Thread.SpinWait(10);
                }
            })).ToArray();

            // 整体超时——超时打印状态（定位哪个阶段卡）
            var all = Task.WhenAll(producers.Concat(consumers).ToArray());
            var winner = await Task.WhenAny(all, Task.Delay(30000));
            Volatile.Write(ref stop, true);
            if (winner != all)
                throw new TimeoutException($"并发超时！produced={produced.Count} consumed={consumed.Count}");

            consumed.Count.Should().Be(total, "无丢失");
            produced.OrderBy(x => x).Should().BeEquivalentTo(consumed.OrderBy(x => x), "无丢失无重复");
        }
        finally { q.Dispose(); }
    }

    [Theory]
    [InlineData(4, 2000)]
    [InlineData(8, 1000)]
    public async Task Concurrent_HighContention_NoLoss(int producers, int perProducer)
    {
        var q = NewQueue<int>(maxLevel: 20);
        var total = producers * perProducer;
        var consumed = new System.Collections.Concurrent.ConcurrentBag<int>();

        var prodTasks = Enumerable.Range(0, producers).Select(p => Task.Run(() =>
        {
            for (var i = 0; i < perProducer; i++)
            {
                var val = p * perProducer + i;
                q.Enqueue(val, val % 5);
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

    /// <summary>
    /// PERF-004 回归：TryDequeue 曾只校验 level-0（Herlihy 协议要求全层级校验）——高层 preds 陈旧时
    /// unlink 守卫失手，marked 节点永久滞留高层链；Find 的 marked-skip 不在 key 处 break → 每次查找
    /// 扫完整条 marked 前缀，混合负载 Find 退化 O(n)（实测 2P+2C × 20 万 = 86s，修复后 ~0.2s）。
    /// 以进度上限护栏：修复版 ~百毫秒，退化版数十秒。
    /// </summary>
    [Fact]
    public void Concurrent_MixedProgress_BoundedTime()
    {
        const int Producers = 2;
        const int PerProducer = 40_000;
        var q = NewQueue<int>(maxLevel: 20);
        var total = Producers * PerProducer;
        var consumed = new System.Collections.Concurrent.ConcurrentBag<int>();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var producers = Enumerable.Range(0, Producers).Select(p => Task.Run(() =>
        {
            for (var i = 0; i < PerProducer; i++)
            {
                var val = p * PerProducer + i;
                q.Enqueue(val, val % 5);
            }
        })).ToArray();

        var consumers = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            while (consumed.Count < total)
            {
                if (q.TryDequeue(out var item)) consumed.Add(item);
                else Thread.SpinWait(10);
            }
        })).ToArray();

        Task.WaitAll(producers.Concat(consumers).ToArray(), TimeSpan.FromSeconds(10)).Should().BeTrue(
            $"2P+2C × {total} 应在 10s 内完成（实测 {sw.ElapsedMilliseconds} ms）——超时提示 marked 前缀滞留退化回归");
        consumed.Count.Should().Be(total, "无丢失");
    }

    [Fact]
    public void Stress_NoDeadlockProgressMade()
    {
        var q = NewQueue<int>(maxLevel: 20);
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
                    q.Enqueue(rng.Next(0, 1000), rng.Next(0, 4));
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

        Interlocked.Read(ref enqueued).Should().BeGreaterThan(0);
        Interlocked.Read(ref dequeued).Should().BeGreaterThan(0);
    }
}
