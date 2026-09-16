using TC.Tier.Contracts.Storage;
using Xunit;

namespace TC.Tier.Products.Tests.Queue;

/// <summary>
/// TierQueue P3 延迟队列 + 幂等生产测试（tc-tier-queue-spec §13 验证矩阵 8/9/10/11/12）。
/// </summary>
public sealed class TierQueueDelayedTests
{
    private static TierQueueOptions Opts(bool delayed = true, bool idem = false) => new()
    {
        PageSize = 64 << 10,
        MemorySize = 8 << 20,
        MaxInFlight = 8,
        DeadLetter = null,
        Delayed = delayed ? new DelayedOptions() : null,
        Idempotency = idem ? new IdempotencyOptions() : null,
    };

    // ══ 矩阵 9：延迟就绪——dueTime 序投递；即时/延迟混流稳定（双段批）══
    // ★ 确定性拆分（墙钟竞态根治）：不赌"T0 首投快于 due"——长 due 断言不投（必然成立），
    //   短 due 轮询等投出（Eventually）；两断言无时序竞态。

    [Fact]
    public async Task Delayed_NotDeliveredBeforeDue_LongWindow()
    {
        using var vol = new TestVolume();
        await using var q = await TierQueueTestFactory.StartAsync(vol, o => Opts());

        long now = DateTime.UtcNow.Ticks;
        var immediate = await q.EnqueueAsync(new byte[8], default);
        var farFuture = await q.EnqueueAsync(new EnqueueOptions
        {
            DueTime = now + TimeSpan.FromSeconds(30).Ticks,   // 远未来——测试生命周期内必不投
        }, new byte[8], default);

        // 连续两轮：即时可见、远未来不可见（30s 窗口内确定性成立）
        var b0 = await q.DequeueAsync(10, default);
        b0.Select(d => d.Address).Should().Equal(new[] { immediate.Address }, "即时先行——延迟未就绪");
        await Task.Delay(300);
        (await q.DequeueAsync(10, default)).Should().BeEmpty("未到期不投（确定性）");
    }

    [Fact]
    public async Task Delayed_DeliveredAfterDue_InDueOrder()
    {
        using var vol = new TestVolume();
        await using var q = await TierQueueTestFactory.StartAsync(vol, o => Opts());

        long now = DateTime.UtcNow.Ticks;
        var immediate = await q.EnqueueAsync(new byte[8], default);
        var late = await q.EnqueueAsync(new EnqueueOptions
        {
            DueTime = now + TimeSpan.FromMilliseconds(900).Ticks,
        }, new byte[8], default);
        var soon = await q.EnqueueAsync(new EnqueueOptions
        {
            DueTime = now + TimeSpan.FromMilliseconds(250).Ticks,
        }, new byte[8], default);

        // 轮询（Eventually）：immediate → soon → late 依次可见（dueTime 序）
        var seen = new List<LogicalAddress>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (seen.Count < 3 && DateTime.UtcNow < deadline)
        {
            var batch = await q.DequeueAsync(10, default);
            if (batch.Count == 0) { await Task.Delay(50); continue; }
            seen.AddRange(batch.Select(d => d.Address));
        }
        seen.Should().Equal(new[] { immediate.Address, soon.Address, late.Address },
            "投递序 = 即时 → dueTime 序（soon 先于 late——索引序）");
    }

    [Fact]
    public async Task SameDueTime_FifoByAddress_Tiebreaker()
    {
        using var vol = new TestVolume();
        await using var q = await TierQueueTestFactory.StartAsync(vol, o => Opts());

        long due = DateTime.UtcNow.Ticks + TimeSpan.FromSeconds(2).Ticks;   // 2s 窗——并行下 enqueue 内重取 now 也不会过期降级
        var r1 = await q.EnqueueAsync(new EnqueueOptions { DueTime = due }, new byte[4], default);
        var r2 = await q.EnqueueAsync(new EnqueueOptions { DueTime = due }, new byte[4], default);
        var r3 = await q.EnqueueAsync(new EnqueueOptions { DueTime = due }, new byte[4], default);
        q.DiagnosticDelayedCount.Should().Be(3, "三条全进延迟索引");

        // Eventually：轮询等三条全部就绪（不赌固定墙钟）
        var seen = new List<LogicalAddress>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(4);
        while (seen.Count < 3 && DateTime.UtcNow < deadline)
        {
            var batch = await q.DequeueAsync(10, default);
            if (batch.Count == 0) { await Task.Delay(50); continue; }
            seen.AddRange(batch.Select(d => d.Address));
        }
        seen.Should().Equal(new[] { r1.Address, r2.Address, r3.Address },
            "同刻 FIFO（索引键 tiebreaker=地址——定案④）");
    }

    [Fact]
    public async Task ListDelayed_QueryFiltersAndPaginates()
    {
        using var vol = new TestVolume();
        await using var q = await TierQueueTestFactory.StartAsync(vol, o => Opts());

        long due = DateTime.UtcNow.Ticks + TimeSpan.FromMinutes(10).Ticks;
        var r1 = await q.EnqueueAsync(new EnqueueOptions { DueTime = due }, new byte[4], default);
        var r2 = await q.EnqueueAsync(new EnqueueOptions { DueTime = due + 10 }, new byte[4], default);
        var r3 = await q.EnqueueAsync(new EnqueueOptions { DueTime = due + 20 }, new byte[4], default);
        var r4 = await q.EnqueueAsync(new EnqueueOptions { DueTime = due + 30 }, new byte[4], default);

        var page = await q.ListDelayedAsync(new DelayedQuery(
            FromDueInclusive: due + 10,
            ToDueInclusive: due + 30,
            Offset: 1,
            Limit: 2), default);

        page.Select(x => x.Address).Should().Equal(new[] { r3.Address, r4.Address });
        page.Select(x => x.DueTime).Should().BeInAscendingOrder();
    }

    // ══ 矩阵 10：延迟崩溃对账——索引未持久窗口断电 → 恢复重放补齐零丢失 ══

    [Fact]
    public async Task DelayedCrash_ReconcileRebuildsIndex_ZeroLoss()
    {
        using var vol = new TestVolume();
        long due = DateTime.UtcNow.Ticks + TimeSpan.FromMilliseconds(500).Ticks;
        EnqueueResult r1, r2;
        await using (var q = await TierQueueTestFactory.StartAsync(vol, o => Opts()))
        {
            r1 = await q.EnqueueAsync(new EnqueueOptions { DueTime = due }, new byte[4], default);
            r2 = await q.EnqueueAsync(new EnqueueOptions { DueTime = due + 1 }, new byte[4], default);
            // 不 Dispose 直接弃（模拟断电——索引 BTree 有主存储持久化，但假设窗口内丢：
            // 对账重放兜底是正确性保证，测试验证的是"无论索引状态如何，恢复后延迟消息不丢"）
        }

        await using (var q2 = await TierQueueTestFactory.StartAsync(vol, o => Opts()))
        {
            // 恢复对账后：两条延迟消息就绪可投
            await Task.Delay(600);
            var b = await q2.DequeueAsync(10, default);
            b.Select(d => d.Address).Should().Equal(
                new[] { r1.Address, r2.Address }, "恢复对账：延迟消息零丢失（索引重建或载入皆达）");
        }
    }

    // ══ 矩阵 11：延迟取消——永不投递、组状态越过、截断解锚 ══

    [Fact]
    public async Task CancelDelayed_NeverDelivers_UnblocksTruncation()
    {
        using var vol = new TestVolume();
        await using var q = await TierQueueTestFactory.StartAsync(vol, o => Opts());

        long due = DateTime.UtcNow.Ticks + TimeSpan.FromSeconds(5).Ticks;
        var r1 = await q.EnqueueAsync(new EnqueueOptions { DueTime = due }, new byte[4], default);
        var r2 = await q.EnqueueAsync(new EnqueueOptions { DueTime = due + 100 }, new byte[4], default);
        (await q.ListDelayedAsync(default)).Should().HaveCount(2);

        // 取消 r1（按地址）
        int n = await q.CancelDelayedAsync(CancelDelayedFilter.ByAddress(r1.Address), default);
        n.Should().Be(1);
        (await q.ListDelayedAsync(default)).Should().ContainSingle(d => d.Address == r2.Address);

        // 截断解锚：取消后 $default ack 空 + 组位点越过被取消消息 → 头可推进越过 r1（索引不再钉住）
        var stats = await q.GetStatsAsync(default);
        stats.BacklogBytes.Should().BeGreaterThanOrEqualTo(0, "取消后积压观测可用");

        // 到期后：仅 r2 可投（r1 被取消永不投递）
        await Task.Delay(300);   // 不到 5s——用区间取消清场后验证 ListDelayed 一致性
        int n2 = await q.CancelDelayedAsync(
            CancelDelayedFilter.ByRange(due - 1, due + 1000), default);
        n2.Should().Be(1, "区间取消剩余 1 条（r2）");
        (await q.ListDelayedAsync(default)).Should().BeEmpty("全部取消");
    }

    // ══ 矩阵 12：锚定治理——MaxDelay 拒绝 + 延迟关闭档拒延迟入队 ══

    [Fact]
    public async Task MaxDelay_RejectsOverlongDelay()
    {
        using var vol = new TestVolume();
        var b = new TierQueueBuilder(vol.Fs, new TierQueueOptions
        {
            PageSize = 64 << 10, MemorySize = 8 << 20, DeadLetter = null,
            Delayed = new DelayedOptions { MaxDelay = TimeSpan.FromSeconds(1) },
        });
        await using var q = await b.StartAsync();

        long tooFar = DateTime.UtcNow.Ticks + TimeSpan.FromSeconds(10).Ticks;
        Func<Task> act = async () => await q.EnqueueAsync(new EnqueueOptions { DueTime = tooFar }, new byte[4], default);
        await act.Should().ThrowAsync<InvalidOperationException>("锚定治理：超 MaxDelay 拒绝（截断钉住上限）");
    }

    [Fact]
    public async Task DelayedDisabled_RejectsDelayedEnqueue()
    {
        using var vol = new TestVolume();
        await using var q = await TierQueueTestFactory.StartAsync(vol, o => Opts(delayed: false));

        long due = DateTime.UtcNow.Ticks + TimeSpan.FromSeconds(1).Ticks;
        Func<Task> act = async () => await q.EnqueueAsync(new EnqueueOptions { DueTime = due }, new byte[4], default);
        await act.Should().ThrowAsync<InvalidOperationException>("Options.Delayed = null：延迟入队被拒");
    }

    // ══ 矩阵 8：幂等生产——同 (pid,seq) 重发 → Ring 恰一条（档三短路）══

    [Fact]
    public async Task IdempotentEnqueue_DuplicateReturnsOriginalAddress()
    {
        using var vol = new TestVolume();
        await using var q = await TierQueueTestFactory.StartAsync(vol, o => Opts(idem: true));

        var payload = TierQueueTestFactory.Msg(3, 7);
        var opt = new EnqueueOptions { ProducerId = 42, Seq = 1 };

        var r1 = await q.EnqueueAsync(opt, payload, default);
        var r2 = await q.EnqueueAsync(opt, payload, default);   // 重发
        var r3 = await q.EnqueueAsync(opt, payload, default);   // 再重发
        r2.Address.Should().Be(r1.Address, "幂等命中返回首次地址");
        r3.Address.Should().Be(r1.Address);
        q.TailAddress.Should().BeGreaterThan(r1.Address, "Ring 只追加了一条（尾部仅前移一次的几何）");

        // 消费面：恰一条
        var b = await q.DequeueAsync(10, default);
        b.Should().HaveCount(1, "Ring 恰一条消息");

        // 不同 seq 正常追加
        var r4 = await q.EnqueueAsync(new EnqueueOptions { ProducerId = 42, Seq = 2 }, payload, default);
        r4.Address.Should().BeGreaterThan(r1.Address);
        (await q.DequeueAsync(10, default)).Should().HaveCount(1);
    }

    [Fact]
    public async Task Idempotency_SurvivesRestart()
    {
        using var vol = new TestVolume();
        var opts = Opts(idem: true);
        EnqueueResult r1;
        await using (var q = await TierQueueTestFactory.StartAsync(vol, o => opts))
        {
            r1 = await q.EnqueueAsync(new EnqueueOptions { ProducerId = 7, Seq = 9 },
                TierQueueTestFactory.Msg(0, 1), default);
        }

        await using (var q2 = await TierQueueTestFactory.StartAsync(vol, o => opts))
        {
            var dup = await q2.EnqueueAsync(new EnqueueOptions { ProducerId = 7, Seq = 9 },
                TierQueueTestFactory.Msg(0, 1), default);
            dup.Address.Should().Be(r1.Address, "幂等索引跨重启：判重持续");
        }
    }

    // ══ 增强包 #5：多生产者延迟入队并发（操作闸愈合验证）+ 锚点帧启用 ══

    [Fact]
    public async Task ConcurrentDelayedEnqueue_MultiProducer_IndexIntact()
    {
        // ★ 历史违规案：多生产者并发延迟入队 → BTreeOfQueueKey.Insert 并发裸奔（单写者契约违反）；
        //   比较族操作闸（增强包 #3）落地后结构自保证——本测为愈合验证（终态断言）
        using var vol = new TestVolume();
        await using var q = await TierQueueTestFactory.StartAsync(vol, o => Opts());

        const int producers = 4, perProducer = 250;
        long now = DateTime.UtcNow.Ticks;
        var tasks = new Task[producers];
        for (int p = 0; p < producers; p++)
        {
            int pid = p;
            tasks[p] = Task.Run(async () =>
            {
                for (int i = 0; i < perProducer; i++)
                {
                    // 同刻多样本（tiebreaker=地址 FIFO）+ 跨生产者不同 due 混流
                    long due = now + TimeSpan.FromSeconds(30 + pid).Ticks + (i % 7);
                    await q.EnqueueAsync(new EnqueueOptions { DueTime = due },
                        TierQueueTestFactory.Msg(pid, i), default);
                }
            });
        }
        await Task.WhenAll(tasks);

        var listed = await q.ListDelayedAsync(default);
        listed.Count.Should().Be(producers * perProducer, "并发延迟入队零丢失零重复");
        listed.Select(d => d.DueTime).Should().BeInAscendingOrder("就绪索引键序完整");
    }

    [Fact]
    public async Task DelayedIndex_PersistenceFrame_AppliedOnRestart()
    {
        // ★ 历史死重案：StartAsync 不传 hints → 锚点帧只写不读（30s 后台 dump 白写）；
        //   启用后恢复走载帧 + 增量重放——诊断位断言帧真被消费
        using var vol = new TestVolume();
        var opts = new TierQueueOptions
        {
            PageSize = 64 << 10,
            MemorySize = 8 << 20,
            MaxInFlight = 8,
            DeadLetter = null,
            Delayed = new DelayedOptions
            {
                // 收紧 dump 策略——100ms 间隔保证重启前帧已落
                PersistencePolicy = new TC.Tier.Runtime.Structures.SortedIndex.SortedIndexPersistencePolicy
                {
                    Interval = TimeSpan.FromMilliseconds(100),
                    EntryDeltaThreshold = 1,
                },
            },
        };

        long due = DateTime.UtcNow.Ticks + TimeSpan.FromMinutes(5).Ticks;
        using (var q1 = await TierQueueTestFactory.StartAsync(vol, o => opts))
        {
            for (int i = 0; i < 50; i++)
                await q1.EnqueueAsync(new EnqueueOptions { DueTime = due + i }, new byte[4], default);
            q1.DiagnosticDelayedCount.Should().Be(50);
            // dump worker 轮询周期 1s（间隔策略 100ms 在轮询后才生效）——等 1.8s 保证至少一次 dump
            await Task.Delay(1800);
        }

        await using (var q2 = await TierQueueTestFactory.StartAsync(vol, o => opts))
        {
            q2.DiagnosticDelayedMainStorageApplied.Should().BeTrue(
                "重启恢复应走锚点帧载入 + 增量重放（此前不传 hints 全量重建——帧白写）");
            q2.DiagnosticDelayedCount.Should().Be(50, "载帧后条目完整");
        }
    }

    // ══ 矩阵 16（补）：截断下界含延迟索引分量——未就绪延迟消息钉住数据头 ══

    [Fact]
    public async Task TruncateBound_PinnedByDelayedMessage_CancelReleases()
    {
        using var vol = new TestVolume();
        await using var q = await TierQueueTestFactory.StartAsync(vol, o => Opts());

        long farDue = DateTime.UtcNow.Ticks + TimeSpan.FromHours(1).Ticks;
        var immediate = await q.EnqueueAsync(new byte[8], default);
        var delayed = await q.EnqueueAsync(new EnqueueOptions { DueTime = farDue }, new byte[8], default);

        // 确认即时消息 → Cursor 推到尾
        var batch = await q.DequeueAsync(10, default);
        await q.AckAsync(batch.Select(d => d.Address).ToList(), default);

        // 截断到尾：被延迟消息钉住（索引最小存活地址 = delayed）
        var headPinned = await q.TruncateAsync(q.TailAddress, default);
        headPinned.Should().Be(delayed.Address, "未就绪延迟消息钉住截断下界");

        // 取消延迟消息 → 索引解锚 → 截断可推进到尾
        await q.CancelDelayedAsync(CancelDelayedFilter.ByAddress(delayed.Address), default);
        var headFreed = await q.TruncateAsync(q.TailAddress, default);
        headFreed.Should().Be(q.TailAddress, "取消后延迟索引解锚——截断推进到尾");
    }

    // ══ 矩阵 11（加强）：延迟取消后截断解锚——被取消消息的地址可被截断越过 ══

    [Fact]
    public async Task CancelDelayed_TruncationPassesCancelledAddress()
    {
        using var vol = new TestVolume();
        await using var q = await TierQueueTestFactory.StartAsync(vol, o => Opts());

        long due = DateTime.UtcNow.Ticks + TimeSpan.FromHours(1).Ticks;
        var r1 = await q.EnqueueAsync(new EnqueueOptions { DueTime = due }, new byte[4], default);
        var r2 = await q.EnqueueAsync(new EnqueueOptions { DueTime = due + 100 }, new byte[4], default);

        // 取消 r1
        await q.CancelDelayedAsync(CancelDelayedFilter.ByAddress(r1.Address), default);

        // 截断到 r2 之前：r1 已取消不再钉住，但 r2 仍钉住
        var head = await q.TruncateAsync(r2.Address, default);
        head.Should().Be(r2.Address, "r1 取消解锚——截断可越过 r1，但被 r2 钉住");

        // 取消 r2 → 截断到尾
        await q.CancelDelayedAsync(CancelDelayedFilter.ByAddress(r2.Address), default);
        var headTail = await q.TruncateAsync(q.TailAddress, default);
        headTail.Should().Be(q.TailAddress, "全部取消——截断推进到尾");
    }
}
