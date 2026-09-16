using TC.Tier.Contracts.Storage;
using Xunit;

namespace TC.Tier.Products.Tests.Queue;

/// <summary>
/// TierQueue P1 消费组全量测试（tc-tier-queue-spec §13 验证矩阵 2/5/6/13/15）。
/// </summary>
public sealed class TierQueueGroupTests
{
    private static readonly int[] ExponentialBackoffExpected = [200, 400, 800];

    // ══ 矩阵 2：组独立——两组不同位点各自消费；删组后截断下界抬升 ══

    [Fact]
    public async Task TwoGroups_IndependentPositions_DeleteRaisesTruncateBound()
    {
        using var vol = new TestVolume();
        await using var q = await TierQueueTestFactory.StartAsync(vol);

        var addrs = new List<LogicalAddress>();
        for (int i = 0; i < 5; i++)
            addrs.Add((await q.EnqueueAsync(TierQueueTestFactory.Msg(0, i), default)).Address);

        // ★ 组先建（未确认 floor 钉住数据），$default 后清（ack 触发的截断被 g1/g2 拦住）
        var g1 = await q.CreateGroupAsync(new GroupOptions { Name = "g1" }, default);          // Earliest——部分确认
        var g2 = await q.CreateGroupAsync(new GroupOptions { Name = "g2" }, default);          // Earliest——全程不确认（被删对象）
        var dflt = await q.DequeueAsync(10, default);
        dflt.Should().HaveCount(5);
        await q.AckAsync(dflt.Select(d => d.Address).ToList(), default);

        var b1 = await g1.DequeueAsync(10, default);
        b1.Should().HaveCount(5, "Earliest 组消费全量");
        await g1.AckAsync(b1.Take(3).Select(d => new DeliveryReceipt(d.Address, d.Token)).ToList(), default);
        var g1Cursor = g1.GroupCursor;
        var b2 = await g2.DequeueAsync(10, default);
        b2.Should().HaveCount(5);

        // g2 未确认（floor=数据头）钉住：头不能抬过 addrs[0]
        var headPinned = await q.TruncateAsync(q.TailAddress, default);
        headPinned.Should().BeLessThan(addrs[3], "g2（未确认）钉住——头不能越过其下界");

        // 删 g2 → 绊脚松开 → 头抬升到 min($default.Cursor=尾, g1.Cursor)
        await q.DeleteGroupAsync("g2", default);
        var headFreed = await q.TruncateAsync(q.TailAddress, default);
        headFreed.Should().Be(g1Cursor, "删组后截断下界抬升 = 剩余最慢组 Cursor");

        // 未确认数据不被截掉：新建组（Earliest）恰见 a3/a4（a0..a2 已截、a3 起存活）
        var g3 = await q.CreateGroupAsync(new GroupOptions { Name = "g3" }, default);
        var rest = await g3.DequeueAsync(10, default);
        rest.Select(d => d.Address).Should().Equal(new[] { addrs[3], addrs[4] }, "未确认数据不被截掉");
    }

    [Fact]
    public async Task GroupsPersist_AcrossRestart_WithConfig()
    {
        using var vol = new TestVolume();
        await using (var q = await TierQueueTestFactory.StartAsync(vol))
        {
            await q.EnqueueAsync(TierQueueTestFactory.Msg(0, 1), default);
            var g = await q.CreateGroupAsync(new GroupOptions
            {
                Name = "orders",
                VisibilityTimeout = TimeSpan.FromMilliseconds(250),
            }, default);
            var batch = await g.DequeueAsync(1, default);
            await g.AckAsync(batch.Select(d => new DeliveryReceipt(d.Address, d.Token)).ToList(), default);
        }

        await using var q2 = await TierQueueTestFactory.StartAsync(vol);
        var names = await q2.ListGroupsAsync(default);
        names.Should().Contain("orders").And.Contain("$default", "组注册表持久");

        var g2 = q2.CreateConsumerAsync("orders");
        (await g2.DequeueAsync(10, default)).Should().BeEmpty("位点持久——ack 过的不重投");
        g2.GroupCursor.Should().Be(q2.TailAddress);
    }

    [Fact]
    public async Task GroupRegistry_LargerThanLegacy4Kb_PersistsAcrossRestart()
    {
        using var vol = new TestVolume();
        var expected = new List<string>();
        await using (var q = await TierQueueTestFactory.StartAsync(vol, _ => new TierQueueOptions
        {
            PageSize = 64 << 10,
            MemorySize = 8 << 20,
            GroupRegistryPayloadSize = 16 << 10,
        }))
        {
            for (int i = 0; i < 40; i++)
            {
                string name = $"g{i:D2}-{new string('x', 116)}";
                expected.Add(name);
                await q.CreateGroupAsync(new GroupOptions { Name = name }, default);
            }
        }

        await using (var q2 = await TierQueueTestFactory.StartAsync(vol, _ => new TierQueueOptions
        {
            PageSize = 64 << 10,
            MemorySize = 8 << 20,
            GroupRegistryPayloadSize = 16 << 10,
        }))
        {
            var names = await q2.ListGroupsAsync(default);
            names.Should().Contain(expected).And.Contain("$default");
        }
    }

    [Fact]
    public async Task GroupRegistry_OverCapacity_FailsWithoutKeepingGroupInMemory()
    {
        using var vol = new TestVolume();
        await using var q = await TierQueueTestFactory.StartAsync(vol, _ => new TierQueueOptions
        {
            PageSize = 64 << 10,
            MemorySize = 8 << 20,
            GroupRegistryPayloadSize = 128,
        });

        string name = $"g-{new string('x', 120)}";
        Func<Task> act = async () => await q.CreateGroupAsync(new GroupOptions { Name = name }, default);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*GroupRegistryPayloadSize*");
        (await q.ListGroupsAsync(default)).Should().NotContain(name, "失败建组必须回滚内存组表");
    }

    // ══ 矩阵 5：崩溃窗口重投有界——pending 不持久，重启重放 ≤ 未确认在途；skip 过滤已确认 ══

    [Fact]
    public async Task CrashWindow_RedeliveryBoundedByUnacked()
    {
        using var vol = new TestVolume();
        var addrs = new List<LogicalAddress>();
        await using (var q = await TierQueueTestFactory.StartAsync(vol))
        {
            for (int i = 0; i < 6; i++)
                addrs.Add((await q.EnqueueAsync(TierQueueTestFactory.Msg(0, i), default)).Address);

            var g = await q.CreateGroupAsync(new GroupOptions { Name = "g" }, default);
            var all = await g.DequeueAsync(10, default);   // 全部在途（pending=6）
            all.Should().HaveCount(6);

            // 确认前 3 条（位点持久）；后 3 条在途未 ack 即"断电"
            await g.AckAsync(all.Take(3).Select(d => new DeliveryReceipt(d.Address, d.Token)).ToList(), default);
        }

        await using (var q2 = await TierQueueTestFactory.StartAsync(vol))
        {
            var g2 = q2.CreateConsumerAsync("g");
            var redelivered = await g2.DequeueAsync(10, default);
            redelivered.Select(d => d.Address).Should().Equal(
                new[] { addrs[3], addrs[4], addrs[5] },
                "重放恰为未确认在途（skip 过滤已确认前缀）——重投有界");
        }
    }

    // ══ 矩阵 6：fencing——抢占后旧持有者 Ack → StaleDeliveryException；不双计 ══

    [Fact]
    public async Task Claim_FencesOldHolder_StaleAckRejected()
    {
        using var vol = new TestVolume();
        await using var q = await TierQueueTestFactory.StartAsync(vol);

        await q.EnqueueAsync(TierQueueTestFactory.Msg(0, 1), default);
        var consumerA = q.CreateConsumerAsync("$default");
        var consumerB = q.CreateConsumerAsync("$default");

        // A 拉取（token epoch e）
        var got = await consumerA.DequeueAsync(1, default);
        got.Should().HaveCount(1);
        var oldReceipt = new DeliveryReceipt(got[0].Address, got[0].Token);

        // B 抢占（minIdle=0 → 立即改派；epoch +1）
        var claimed = await consumerB.ClaimAsync(new ClaimFilter(TimeSpan.Zero), default);
        claimed.Should().HaveCount(1, "minIdle=0 全部在途可抢占");

        // A 迟交（旧 token）→ 确定性拒绝
        Func<Task> staleAct = async () => await consumerA.AckAsync([oldReceipt], default);
        await staleAct.Should().ThrowAsync<StaleDeliveryException>("旧持有者迟交被 fence——不双计");

        // B 凭新 token 确认 → 成功推进
        await consumerB.AckAsync(claimed.Select(d => new DeliveryReceipt(d.Address, d.Token)).ToList(), default);
        consumerB.GroupCursor.Should().BeGreaterThan(got[0].Address, "新持有者确认推进位点");

        // 确认后不再投递
        (await consumerB.DequeueAsync(10, default)).Should().BeEmpty();
    }

    // ══ 矩阵 13：可见性超时重投——超时未 ack → 计数 +1 重投 ══

    [Fact]
    public async Task VisibilityTimeout_RedeliversWithCountIncrement()
    {
        using var vol = new TestVolume();
        await using var q = await TierQueueTestFactory.StartAsync(vol);

        await q.EnqueueAsync(TierQueueTestFactory.Msg(0, 1), default);
        var g = await q.CreateGroupAsync(new GroupOptions
        {
            Name = "vis",
            VisibilityTimeout = TimeSpan.FromMilliseconds(300),
        }, default);

        var first = await g.DequeueAsync(1, default);
        first.Should().HaveCount(1);
        first[0].RedeliveryCount.Should().Be(0);

        // 在途窗口内再拉 → 空（deliver-once 窗口——pending 过滤）
        (await g.DequeueAsync(1, default)).Should().BeEmpty("在途不重投（deliver-once 窗口）");

        // 超时后 → 重投 + 计数 1
        await Task.Delay(450);
        var second = await g.DequeueAsync(1, default);
        second.Should().HaveCount(1, "可见性超时回队重投");
        second[0].Address.Should().Be(first[0].Address);
        second[0].RedeliveryCount.Should().Be(1, "重投计数 +1");

        // pending 窥视：在途 1 条
        var pend = await g.PendingAsync(default);
        pend.Should().HaveCount(1);
    }

    [Fact]
    public async Task Nack_ImmediateRedelivery_CountIncrements()
    {
        using var vol = new TestVolume();
        await using var q = await TierQueueTestFactory.StartAsync(vol);

        await q.EnqueueAsync(TierQueueTestFactory.Msg(0, 1), default);
        var g = q.CreateConsumerAsync("$default");
        var got = await g.DequeueAsync(1, default);

        await g.NackAsync([got[0].Address], default);   // 显式否认 → 立即回队

        var again = await g.DequeueAsync(1, default);
        again.Should().HaveCount(1, "Nack 立即重投");
        again[0].RedeliveryCount.Should().Be(1);
    }

    // ══ 矩阵 15：治理三模式——Block 有界等待 / Reject 抛 / EvictOldest fence + 显式 Reset ══

    [Fact]
    public async Task Governance_Reject_ThrowsWhenBacklogExceeds()
    {
        using var vol = new TestVolume();
        var b = new TierQueueBuilder(vol.Fs, new TierQueueOptions
        {
            PageSize = 64 << 10, MemorySize = 8 << 20,
            MaxBacklogBytes = 512, Backlog = BacklogPolicy.Reject,   // envelope 头 ~29B：64B 消息占 ~304B 槽
        });
        await using var q = await b.StartAsync();

        // 积压未超 → 正常（预检在写前：64B 槽 < 256）
        await q.EnqueueAsync(new byte[64], default);

        // 首写跨限（250B payload → 320B 对齐槽），下一笔预检即超限 → 拒绝
        await q.EnqueueAsync(new byte[250], default);
        Func<Task> act = async () => await q.EnqueueAsync(new byte[8], default);
        await act.Should().ThrowAsync<QueueFullException>("Reject 模式超限即抛");
    }

    [Fact]
    public async Task Governance_Block_WaitsUntilAckFrees()
    {
        using var vol = new TestVolume();
        var b = new TierQueueBuilder(vol.Fs, new TierQueueOptions
        {
            PageSize = 64 << 10, MemorySize = 8 << 20,
            MaxBacklogBytes = 512, Backlog = BacklogPolicy.Block,   // envelope 头推高槽位——数字按 512 校准
            BacklogBlockTimeout = TimeSpan.FromSeconds(5),
        });
        await using var q = await b.StartAsync();

        var r1 = await q.EnqueueAsync(new byte[400], default);      // 写后 ~640 > 512
        var enqueueTask = q.EnqueueAsync(new byte[200], default);   // 将阻塞（积压 > 512）
        await Task.Delay(150);
        enqueueTask.IsCompleted.Should().BeFalse("Block 模式等待回收");

        // 消费者确认 → 截断脉冲 → 阻塞写入放行
        var got = await q.DequeueAsync(10, default);
        await q.AckAsync(got.Select(d => d.Address).ToList(), default);

        var ok = await enqueueTask;   // 不抛 = 放行
        ok.Address.Should().BeGreaterThan(r1.Address);

        // 超时路径：先填满超限，再无消费者等待 → 超时抛
        var filler = await q.EnqueueAsync(new byte[400], default);   // ~640B 槽：backlog 跨限
        var slowTask = q.EnqueueAsync(new byte[8], default).AsTask();
        Func<Task> timeoutAct = () => slowTask;
        await timeoutAct.Should().ThrowAsync<QueueFullException>("Block 有界等待超时即抛");
    }

    [Fact]
    public async Task Governance_EvictOldest_FencesSlowest_ResetResumes()
    {
        using var vol = new TestVolume();
        var b = new TierQueueBuilder(vol.Fs, new TierQueueOptions
        {
            PageSize = 64 << 10, MemorySize = 8 << 20,
            MaxBacklogBytes = 512, Backlog = BacklogPolicy.EvictOldest,   // envelope 头推高槽位——按 512 校准
        });
        await using var q = await b.StartAsync();

        var slow = await q.CreateGroupAsync(new GroupOptions { Name = "slow" }, default);   // 消费后不 ack = 钉住
        var fast = await q.CreateGroupAsync(new GroupOptions { Name = "fast" }, default);

        // 首写跨限（400B → ~640B 槽 > 512）：$default 与 fast 先消费并确认（floor 抬离数据头）
        var r0 = await q.EnqueueAsync(new byte[400], default);
        var dflt = await q.DequeueAsync(10, default);
        await q.AckAsync(dflt.Select(d => d.Address).ToList(), default);
        var fastGot0 = await fast.DequeueAsync(10, default);
        await fast.AckAsync(fastGot0.Select(d => new DeliveryReceipt(d.Address, d.Token)).ToList(), default);

        // slow 拉走不 ack → floor=数据头（唯一最慢——Evict 必中）
        var got = await slow.DequeueAsync(10, default);
        got.Should().HaveCount(1);

        // 超限写入 → EvictOldest fence 最慢组（slow）→ 写入放行
        var r = await q.EnqueueAsync(new byte[200], default);
        r.Address.Should().BeGreaterThan(r0.Address);

        // fence 生效：slow 消费抛 DataLostFencedException
        Func<Task> fencedAct = async () => await slow.DequeueAsync(10, default);
        await fencedAct.Should().ThrowAsync<DataLostFencedException>("fence 组消费被拒——破坏显式化");

        // fast 组不受影响（r0 已确认——只见新写入的 1 条）
        var fastGot = await fast.DequeueAsync(10, default);
        fastGot.Should().HaveCount(1, "其余组不受 EvictOldest 影响");
        fastGot[0].Address.Should().Be(r.Address);

        // 显式 Reset（Latest）→ slow 复活续消费
        await q.ResetGroupAsync("slow", GroupStartAt.Latest, LogicalAddress.Invalid, default);
        var resumed = await slow.DequeueAsync(10, default);
        resumed.Should().BeEmpty("Reset Latest → 从当前尾续（旧数据已承认丢失）");
        (await q.GetStatsAsync(default)).Groups.Should().ContainSingle(gs => gs.Name == "slow" && !gs.Fenced);
    }

    [Fact]
    public async Task Stats_ReflectsGroupsAndBacklog()
    {
        using var vol = new TestVolume();
        await using var q = await TierQueueTestFactory.StartAsync(vol);

        await q.EnqueueAsync(new byte[100], default);
        var g = await q.CreateGroupAsync(new GroupOptions { Name = "s" }, default);
        var got = await g.DequeueAsync(1, default);
        got.Should().HaveCount(1);

        var stats = await q.GetStatsAsync(default);
        stats.Groups.Should().HaveCount(2);   // $default + s
        var s = stats.Groups.Single(x => x.Name == "s");
        s.Pending.Should().Be(1, "在途 1 条");
        s.LagBytes.Should().BeGreaterThan(0, "滞后 = [Cursor, Tail) 字节");
        stats.BacklogBytes.Should().BeGreaterThan(0);
    }

    // ══ 矩阵 13（补）：重试退避——nack/超时后不立即重投，按策略延迟后重投（复用 pending 可见性窗口）══

    [Fact]
    public async Task NackWithBackoff_NotRedeliveredUntilDelayExpires()
    {
        using var vol = new TestVolume();
        await using var q = await TierQueueTestFactory.StartAsync(vol);

        await q.EnqueueAsync(TierQueueTestFactory.Msg(0, 1), default);
        var g = await q.CreateGroupAsync(new GroupOptions
        {
            Name = "backoff",
            RetryBackoff = _ => TimeSpan.FromMilliseconds(600),   // 固定 600ms 退避
        }, default);

        var first = await g.DequeueAsync(1, default);
        first.Should().HaveCount(1);

        // nack → 退避窗口内不可投
        await g.NackAsync([first[0].Address], default);
        (await g.DequeueAsync(1, default)).Should().BeEmpty("退避窗口内不重投");
        await Task.Delay(200);
        (await g.DequeueAsync(1, default)).Should().BeEmpty("退避未到期仍不重投");

        // 到期后重投
        await Task.Delay(500);
        var second = await g.DequeueAsync(1, default);
        second.Should().HaveCount(1, "退避到期重投");
        second[0].Address.Should().Be(first[0].Address);
        second[0].RedeliveryCount.Should().Be(1, "重投计数 +1");
    }

    [Fact]
    public async Task VisibilityTimeoutWithBackoff_DelayedRedelivery()
    {
        using var vol = new TestVolume();
        await using var q = await TierQueueTestFactory.StartAsync(vol);

        await q.EnqueueAsync(TierQueueTestFactory.Msg(0, 2), default);
        var g = await q.CreateGroupAsync(new GroupOptions
        {
            Name = "vis-backoff",
            VisibilityTimeout = TimeSpan.FromMilliseconds(200),
            RetryBackoff = _ => TimeSpan.FromMilliseconds(500),
        }, default);

        var first = await g.DequeueAsync(1, default);
        first.Should().HaveCount(1);

        // 可见性超时（200ms）→ 进入退避（500ms）→ 合计约 700ms 后重投
        await Task.Delay(350);   // 过了 visibility，但退避未到期
        (await g.DequeueAsync(1, default)).Should().BeEmpty("退避窗口内不重投");

        await Task.Delay(500);   // 退避到期
        var second = await g.DequeueAsync(1, default);
        second.Should().HaveCount(1);
        second[0].RedeliveryCount.Should().Be(1);
    }

    [Fact]
    public async Task ExponentialBackoff_DelayGrowsWithRetryCount()
    {
        using var vol = new TestVolume();
        await using var q = await TierQueueTestFactory.StartAsync(vol);

        await q.EnqueueAsync(TierQueueTestFactory.Msg(0, 3), default);
        var delays = new List<int>();
        var g = await q.CreateGroupAsync(new GroupOptions
        {
            Name = "exp",
            MaxRedeliveries = 5,
            RetryBackoff = n => { var d = 100 * (int)Math.Pow(2, n); delays.Add(d); return TimeSpan.FromMilliseconds(d); },
        }, default);

        // 连续 nack 3 次：退避 200ms, 400ms, 800ms（每次需等退避到期才能重投）
        var expectedDelays = new[] { 200, 400, 800 };
        for (int i = 0; i < 3; i++)
        {
            var got = await g.DequeueAsync(1, default);
            got.Should().HaveCount(1);
            await g.NackAsync([got[0].Address], default);
            await Task.Delay(expectedDelays[i] + 50);   // 等退避到期
        }

        delays.Should().Equal(ExponentialBackoffExpected, "指数退避随重投计数增长");
    }
}
