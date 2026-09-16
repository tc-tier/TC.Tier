using System.Collections.Concurrent;
using TC.Tier.Contracts.Storage;
using TC.Tier.Runtime.Structures.Ring.Contracts;
using Xunit;
using FluentAssertions;

namespace TC.Tier.Products.Tests.Queue;

/// <summary>
/// TierQueue P0 核心流测试（tc-tier-queue-spec §13 验证矩阵 1/3/4/16/17）。
/// <para>介质：mem（TestVolume 默认）——重启语义 = 同卷上 Dispose 实例后 Builder 重建（fs 存活保持数据）。</para>
/// </summary>
public sealed class TierQueueTests
{
    // ══ 矩阵 1：FIFO 保序——多生产者并发入队 × 单消费者：消费序 = 地址序，不丢不重 ══

    [Fact]
    public async Task ConcurrentEnqueue_FifoDelivery_NoLossNoDup()
    {
        using var vol = new TestVolume();
        await using var q = await TierQueueTestFactory.StartAsync(vol);

        const int producers = 4, perProducer = 250;
        var expected = new ConcurrentBag<(int P, int S, LogicalAddress A)>();

        await Task.WhenAll(Enumerable.Range(0, producers).Select(p => Task.Run(async () =>
        {
            for (int i = 0; i < perProducer; i++)
            {
                var r = await q.EnqueueAsync(TierQueueTestFactory.Msg(p, i), default);
                expected.Add((p, i, r.Address));
            }
        })));

        // 单消费者：分批拉空
        var consumed = new List<(LogicalAddress A, int P, int S)>();
        while (true)
        {
            var batch = await q.DequeueAsync(100, default);
            if (batch.Count == 0) break;
            foreach (var d in batch)
            {
                var (p, s) = TierQueueTestFactory.ParseMsg(d.Payload);
                consumed.Add((d.Address, p, s));
            }
        }

        consumed.Should().HaveCount(producers * perProducer, "不丢不重");
        // 消费序 = 地址序（严格递增）
        consumed.Select(c => c.A).Should().BeInAscendingOrder("FIFO：消费序 = 地址序");
        // 内容集合 = 生产集合（不丢不重）
        consumed.Select(c => (c.P, c.S)).ToHashSet().Should().HaveCount(producers * perProducer);
    }

    // ══ 基线：入队→拉取→确认→拉空 ══

    [Fact]
    public async Task EnqueueDequeueAck_BasicCycle()
    {
        using var vol = new TestVolume();
        await using var q = await TierQueueTestFactory.StartAsync(vol);

        var r = await q.EnqueueAsync(TierQueueTestFactory.Msg(0, 42), default);
        var batch = await q.DequeueAsync(10, default);
        batch.Should().HaveCount(1);
        batch[0].Address.Should().Be(r.Address);
        TierQueueTestFactory.ParseMsg(batch[0].Payload).Seq.Should().Be(42);

        await q.AckAsync([r.Address], default);
        q.GroupCursor.Should().BeGreaterThan(r.Address, "前缀推进越过已确认地址");

        (await q.DequeueAsync(10, default)).Should().BeEmpty("已确认区间不再投递");
    }

    [Fact]
    public async Task BatchOptions_HeadAndDurableWatermarks_Work()
    {
        using var vol = new TestVolume();
        await using var q = await TierQueueTestFactory.StartAsync(vol, _ => new TierQueueOptions
        {
            PageSize = 64 << 10,
            MemorySize = 8 << 20,
            Idempotency = new IdempotencyOptions(),
        });

        q.HeadAddress.Should().Be(new LogicalAddress(0, 0), "空队列物理头从 ring 起点开始");
        var payloads = new[]
        {
            (ReadOnlyMemory<byte>)TierQueueTestFactory.Msg(0, 10),
            TierQueueTestFactory.Msg(0, 11),
            TierQueueTestFactory.Msg(0, 12),
        };

        var results = await q.EnqueueBatchAsync(new EnqueueOptions { ProducerId = 42, Seq = 100 }, payloads, default);
        results.Should().HaveCount(3);
        await q.WaitForDurableAsync(results[^1].Address, default);
        q.DurableTail.Should().BeGreaterThan(results[^1].Address, "WaitForDurableAsync 推进落盘水位覆盖目标地址");

        var duplicates = await q.EnqueueBatchAsync(new EnqueueOptions { ProducerId = 42, Seq = 100 }, payloads, default);
        duplicates.Select(x => x.Address).Should().Equal(results.Select(x => x.Address),
            "批量幂等选项按批内序号递增，重复批返回原地址");
    }

    [Fact]
    public async Task ImmediateBatch_UsesContiguousRingWindows_AndDeliversAllMessages()
    {
        using var vol = new TestVolume();
        await using var q = await TierQueueTestFactory.StartAsync(vol);
        var payloads = Enumerable.Range(0, 64)
            .Select(i => (ReadOnlyMemory<byte>)TierQueueTestFactory.Msg(7, i)).ToArray();

        var enqueued = await q.EnqueueBatchAsync(payloads, default);
        var delivered = await q.DequeueAsync(payloads.Length, default);

        delivered.Should().HaveCount(payloads.Length);
        delivered.Select(d => d.Address).Should().Equal(enqueued.Select(r => r.Address));
        delivered.Select(d => TierQueueTestFactory.ParseMsg(d.Payload).Seq)
            .Should().Equal(Enumerable.Range(0, payloads.Length));
        await q.AckAsync(delivered.Select(d => d.Address).ToArray(), default);
    }

    [Fact]
    public async Task BufferedAcknowledgements_CommitAtThresholdOrExplicitFlush()
    {
        using var vol = new TestVolume();
        await using var q = await TierQueueTestFactory.StartAsync(vol, _ => new TierQueueOptions
        {
            PageSize = 64 << 10,
            MemorySize = 8 << 20,
            BufferedAckBatchSize = 3,
        });
        var addresses = new List<LogicalAddress>();
        for (int i = 0; i < 3; i++)
            addresses.Add((await q.EnqueueAsync(TierQueueTestFactory.Msg(8, i), default)).Address);
        var deliveries = await q.DequeueAsync(3, default);

        await q.AckBufferedAsync([deliveries[0].Address], default);
        q.GroupCursor.Should().NotBe(q.TailAddress, "threshold has not been reached");
        await q.AckBufferedAsync([deliveries[1].Address], default);
        await q.FlushAcknowledgementsAsync(default);
        q.GroupCursor.Should().Be(deliveries[2].Address, "explicit flush commits the buffered prefix");

        await q.AckBufferedAsync([deliveries[2].Address], default);
        await q.FlushAcknowledgementsAsync(default);
        q.GroupCursor.Should().Be(q.TailAddress, "the remaining acknowledgement is explicitly committed");

        for (int i = 0; i < 3; i++)
            await q.EnqueueAsync(TierQueueTestFactory.Msg(9, i), default);
        var thresholdBatch = await q.DequeueAsync(3, default);
        await q.AckBufferedAsync(thresholdBatch.Select(d => d.Address).ToArray(), default);
        q.GroupCursor.Should().Be(q.TailAddress, "the configured batch threshold commits automatically");
    }

    [Fact]
    public async Task BufferedAcknowledgements_CommitAtConfiguredTimeLimit()
    {
        using var vol = new TestVolume();
        await using var q = await TierQueueTestFactory.StartAsync(vol, _ => new TierQueueOptions
        {
            PageSize = 64 << 10,
            MemorySize = 8 << 20,
            BufferedAckBatchSize = 100,
            BufferedAckCommitInterval = TimeSpan.FromMilliseconds(50),
        });
        await q.EnqueueAsync(TierQueueTestFactory.Msg(10, 1), default);
        var delivery = (await q.DequeueAsync(1, default)).Single();

        await q.AckBufferedAsync([delivery.Address], default);
        await Task.Delay(TimeSpan.FromMilliseconds(250));

        q.GroupCursor.Should().Be(q.TailAddress,
            "the bounded buffered acknowledgement must be committed even when no later Ack arrives");
    }

    [Fact]
    public async Task StorageOptionsFactory_AppliesToEveryQueueComponent()
    {
        using var vol = new TestVolume();
        var components = new ConcurrentBag<string>();
        int delayedNodeSize = 0, delayedMinFill = 0, idempotencyCapacity = 0, idempotencyOverflow = 0;
        await using var q = await TierQueueTestFactory.StartAsync(vol, _ => new TierQueueOptions
        {
            PageSize = 64 << 10,
            MemorySize = 8 << 20,
            DeadLetter = null,
            Delayed = new DelayedOptions { NodeSize = 512, MinFillPercent = 60 },
            Idempotency = new IdempotencyOptions { HashTableCapacity = 2048, OverflowPoolCapacity = 1024 },
        }, builder => builder.WithStorageOptionsFactory((component, defaults) =>
        {
            components.Add(component);
            return defaults.WithPreallocateFile(false);
        })
        .WithDelayedIndexFactory((fs, settings, epoch, resolver, comparer) =>
        {
            delayedNodeSize = settings.NodeSize;
            delayedMinFill = settings.MinFillPercent;
            return new TC.Tier.Runtime.Structures.SortedIndex.BTreeOfQueueKey(
                fs, settings, epoch: epoch, keyResolver: resolver, keyComparer: comparer);
        })
        .WithIdempotencyIndexFactory((fs, settings, epoch, resolver, comparer) =>
        {
            idempotencyCapacity = settings.HashTableCapacity;
            idempotencyOverflow = settings.OverflowPoolCapacity;
            return new TC.Tier.Runtime.Structures.ProbingIndex.HashOfQueueKey(
                fs, settings, epoch: epoch, keyResolver: resolver, keyComparer: comparer);
        }));

        components.Should().Contain(["ring", "groups", "group", "delay", "idempotency"]);
        (delayedNodeSize, delayedMinFill).Should().Be((512, 60));
        (idempotencyCapacity, idempotencyOverflow).Should().Be((2048, 1024));
    }

    // ══ 矩阵 3：乱序 ack——空洞进 Skip 表持久，重启重放跳过已确认空洞 ══

    [Fact]
    public async Task OutOfOrderAck_SkipHolesPersist_AcrossRestart()
    {
        using var vol = new TestVolume();
        var addrs = new List<LogicalAddress>();
        await using (var q = await TierQueueTestFactory.StartAsync(vol))
        {
            for (int i = 0; i < 5; i++)
                addrs.Add((await q.EnqueueAsync(TierQueueTestFactory.Msg(0, i), default)).Address);

            // 乱序：只确认 a1/a3（非前缀 → 空洞 Skip）
            await q.AckAsync([addrs[1], addrs[3]], default);
        }

        // 重启：重放从 Cursor（= 数据头）起，Skip 过滤 a1/a3 → 恰投 a0/a2/a4
        await using (var q2 = await TierQueueTestFactory.StartAsync(vol))
        {
            var batch = await q2.DequeueAsync(10, default);
            batch.Select(d => d.Address).Should().Equal(
                new[] { addrs[0], addrs[2], addrs[4] }, "Skip 表持久——已确认空洞不重投");

            // 全部确认 → Cursor 推到尾
            await q2.AckAsync(batch.Select(d => d.Address).ToList(), default);
        }

        // 再重启：空
        await using (var q3 = await TierQueueTestFactory.StartAsync(vol))
        {
            (await q3.DequeueAsync(10, default)).Should().BeEmpty("全确认后无重投");
            q3.GroupCursor.Should().Be(q3.TailAddress, "连续前缀推到尾");
        }
    }

    // ══ 矩阵 4：ack 后断电不重投——AckAsync 返回 = 位点已持久（契约①②）══

    [Fact]
    public async Task AckPersisted_NoRedeliveryAfterRestart()
    {
        using var vol = new TestVolume();
        var tailBefore = LogicalAddress.Invalid;
        await using (var q = await TierQueueTestFactory.StartAsync(vol))
        {
            for (int i = 0; i < 3; i++)
                await q.EnqueueAsync(TierQueueTestFactory.Msg(0, i), default);
            tailBefore = q.TailAddress;

            var batch = await q.DequeueAsync(10, default);
            batch.Should().HaveCount(3);
            await q.AckAsync(batch.Select(d => d.Address).ToList(), default);
            // AckAsync 返回即位点已持久——此后 Dispose（模拟断电，无额外 Flush）
        }

        await using (var q2 = await TierQueueTestFactory.StartAsync(vol))
        {
            (await q2.DequeueAsync(10, default)).Should().BeEmpty("ack 已持久——不重投");
            q2.GroupCursor.Should().Be(tailBefore, "位点恢复到尾");
        }
    }

    // ══ 未 ack 崩溃 → at-least-once 重投 ══

    [Fact]
    public async Task UnackedCrash_RedeliveryAtLeastOnce()
    {
        using var vol = new TestVolume();
        await using (var q = await TierQueueTestFactory.StartAsync(vol))
        {
            for (int i = 0; i < 3; i++)
                await q.EnqueueAsync(TierQueueTestFactory.Msg(0, i), default);
            var batch = await q.DequeueAsync(10, default);
            batch.Should().HaveCount(3);
            // 拉取未 ack 即 Dispose——重启重投（at-least-once 基线）
        }

        await using (var q2 = await TierQueueTestFactory.StartAsync(vol))
        {
            var batch = await q2.DequeueAsync(10, default);
            batch.Should().HaveCount(3, "未确认 → at-least-once 重投");
        }
    }

    // ══ 矩阵 16：截断守卫——bound 计算含 Cursor 分量，越界目标夹取不截未 ack 数据 ══

    [Fact]
    public async Task TruncateGuard_ClampsToGroupCursor()
    {
        using var vol = new TestVolume();
        await using var q = await TierQueueTestFactory.StartAsync(vol);

        var addrs = new List<LogicalAddress>();
        for (int i = 0; i < 5; i++)
            addrs.Add((await q.EnqueueAsync(TierQueueTestFactory.Msg(0, i), default)).Address);

        // 确认前缀 a0/a1 → Cursor 越过 a1
        await q.AckAsync([addrs[0], addrs[1]], default);
        var cursor = q.GroupCursor;

        // 恶意截断到尾：必须被夹取到 Cursor（a2/a3/a4 未 ack 不可截）
        var head = await q.TruncateAsync(q.TailAddress, default);
        head.Should().Be(cursor, "截断下界守卫：目标夹取到组 Cursor");

        // 未确认数据仍可消费
        var batch = await q.DequeueAsync(10, default);
        batch.Select(d => d.Address).Should().Equal(
            new[] { addrs[2], addrs[3], addrs[4] }, "未 ack 数据不被截掉");
    }

    // ══ 恢复自动截断（契约③）：崩溃前"已 ack 未截断"窗口即刻回收 ══

    [Fact]
    public async Task RecoveryAutoTruncate_ReclaimsAckedWindow()
    {
        using var vol = new TestVolume();
        await using (var q = await TierQueueTestFactory.StartAsync(vol))
        {
            for (int i = 0; i < 3; i++)
                await q.EnqueueAsync(TierQueueTestFactory.Msg(0, i), default);
            var batch = await q.DequeueAsync(10, default);
            await q.AckAsync(batch.Select(d => d.Address).ToList(), default);
        }

        await using (var q2 = await TierQueueTestFactory.StartAsync(vol))
        {
            var head = await q2.TruncateAsync(LogicalAddress.Invalid, default);   // 无效目标 = 查询当前头
            head.Should().Be(q2.GroupCursor, "恢复自动截断：头 = 组 Cursor");
        }
    }

    // ══ 矩阵 17：溢出 payload——超阈值消息经溢出引擎，入队/消费往返一致 ══

    [Fact]
    public async Task OverflowPayload_RoundtripConsistent()
    {
        using var vol = new TestVolume();
        // ★ 单实例契约：同卷队列先 Dispose 再重开（mem 卷 ExclusiveLock——重叠打开第二次拿隔离空卷，
        //   meta 空 → 假尾爬扫）。q1 块作用域先于 q2 关闭。
        var payload = new byte[1 << 20];   // 1MB（>> 1KB 阈值 → 溢出引擎）
        new Random(42).NextBytes(payload);
        EnqueueResult r;
        await using (var q = await new TierQueueBuilder(vol.Fs, new TierQueueOptions
        {
            OverflowPolicy = OverflowPolicy.Enabled,
            MinOverflowSize = 1024,
        }).StartAsync())
        {
            r = await q.EnqueueAsync(payload, default);
            var batch = await q.DequeueAsync(10, default);
            batch.Should().HaveCount(1);
            batch[0].Payload.Should().Equal(payload, "溢出值经溢出引擎往返一致");

            // ack + 重启：不重投（溢出数据先于位点持久）
            await q.AckAsync([r.Address], default);
        }

        await using var q2 = await new TierQueueBuilder(vol.Fs, new TierQueueOptions
        {
            OverflowPolicy = OverflowPolicy.Enabled,
            MinOverflowSize = 1024,
        }).StartAsync();
        (await q2.DequeueAsync(10, default)).Should().BeEmpty();
    }

    // ══ Skip 表在途窗口上限 = 消费侧背压 ══

    [Fact]
    public async Task SkipWindowOverflow_ThrowsBackpressure()
    {
        using var vol = new TestVolume();
        var b = new TierQueueBuilder(vol.Fs, new TierQueueOptions { PageSize = 64 << 10, MemorySize = 8 << 20, MaxInFlight = 2 });
        await using var q = await b.StartAsync();

        var addrs = new List<LogicalAddress>();
        for (int i = 0; i < 5; i++)
            addrs.Add((await q.EnqueueAsync(TierQueueTestFactory.Msg(0, i), default)).Address);

        // 全是非前缀空洞：3 条 > MaxInFlight=2 → 拒绝
        Func<Task> act = async () => await q.AckAsync([addrs[1], addrs[2], addrs[3]], default);
        await act.Should().ThrowAsync<InvalidOperationException>("在途窗口背压（PendingWindowOverflow）");
    }
}
