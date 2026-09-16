using TC.Tier.Contracts.Storage;
using TC.Tier.Runtime.Structures.Ring.Contracts;
using Xunit;

namespace TC.Tier.Products.Tests.Queue;

/// <summary>
/// TierQueue P2 重试与死信测试（tc-tier-queue-spec §13 验证矩阵 14 + 计数持久化 + 关闭档 + 回放）。
/// </summary>
public sealed class TierQueueDeadLetterTests
{
    private static readonly int[] SeqOneTwo = [1, 2];

    private static TierQueueOptions Opts(int maxRedeliveries, bool deadLetter = true) => new()
    {
        PageSize = 64 << 10,
        MemorySize = 8 << 20,
        DeadLetter = deadLetter ? new DeadLetterOptions() : null,
        MaxInFlight = 8,
    };

    // ══ 矩阵 14：达上限 → DLQ 恰好一条；溯源字段齐；原组终结不重投 ══

    [Fact]
    public async Task NackBeyondMax_RoutesToDlq_WithEnvelopeSource()
    {
        using var vol = new TestVolume();
        await using var q = await TierQueueTestFactory.StartAsync(vol, o => Opts(1));

        var r = await q.EnqueueAsync(TierQueueTestFactory.Msg(7, 1), default);
        var g = await q.CreateGroupAsync(new GroupOptions { Name = "g", MaxRedeliveries = 1 }, default);

        // nack 1 次：count=1 ≤ max → 重投
        var d1 = await g.DequeueAsync(1, default);
        d1.Should().HaveCount(1);
        await g.NackAsync([r.Address], default);
        var d2 = await g.DequeueAsync(1, default);
        d2.Should().HaveCount(1);
        d2[0].RedeliveryCount.Should().Be(1, "首次 nack 后计数 1（≤ max 重投）");

        // nack 第 2 次：count=2 > max=1 → 死信（DLQ envelope + 原位终结）
        await g.NackAsync([r.Address], default);
        (await g.DequeueAsync(1, default)).Should().BeEmpty("死信后原组不再重投");

        // DLQ 恰好一条 + 溯源齐
        q.DeadLetterQueue.Should().NotBeNull("默认开启");
        var dlqBatch = await q.DeadLetterQueue!.DequeueAsync(10, default);
        dlqBatch.Should().HaveCount(1, "DLQ 恰好一条");
        DeadLetterEnvelope.TryUnwrap(dlqBatch[0].Payload, out var src, out var grp, out var finalCount, out var payload)
            .Should().BeTrue("DLQ 条目是死信 envelope");
        src.Should().Be(r.Address, "溯源：原地址");
        grp.Should().Be("g", "溯源：源组");
        finalCount.Should().Be(2, "溯源：最终计数");
        payload.Should().Equal(TierQueueTestFactory.Msg(7, 1), "原 payload 完整");
    }

    [Fact]
    public async Task VisibilityExpiryBeyondMax_RoutesToDlq()
    {
        using var vol = new TestVolume();
        await using var q = await TierQueueTestFactory.StartAsync(vol, o => Opts(1));

        var r = await q.EnqueueAsync(TierQueueTestFactory.Msg(0, 5), default);
        var g = await q.CreateGroupAsync(new GroupOptions
        {
            Name = "g",
            MaxRedeliveries = 1,
            VisibilityTimeout = TimeSpan.FromMilliseconds(250),
        }, default);

        // 第一轮在途→超时→重投（count=1）
        var d1 = await g.DequeueAsync(1, default);
        d1.Should().HaveCount(1);
        await Task.Delay(400);
        var d2 = await g.DequeueAsync(1, default);
        d2.Should().HaveCount(1);
        d2[0].RedeliveryCount.Should().Be(1);

        // 第二轮在途→超时 → count=2 > max → 死信（第三轮拉取的到期处理内路由）
        await Task.Delay(400);
        var d3 = await g.DequeueAsync(1, default);
        d3.Should().BeEmpty("达上限死信——不再重投");

        var dlqBatch = await q.DeadLetterQueue!.DequeueAsync(10, default);
        dlqBatch.Should().HaveCount(1);
        DeadLetterEnvelope.TryUnwrap(dlqBatch[0].Payload, out var src, out _, out var finalCount, out _).Should().BeTrue();
        src.Should().Be(r.Address);
        finalCount.Should().Be(2);
    }

    // ══ 关闭档：无 DLQ，达上限仅终结 + 队列继续工作 ══

    [Fact]
    public async Task DlqDisabled_SkipsAndContinues()
    {
        using var vol = new TestVolume();
        await using var q = await TierQueueTestFactory.StartAsync(vol, o => Opts(1, deadLetter: false));

        var r = await q.EnqueueAsync(TierQueueTestFactory.Msg(0, 1), default);
        var g = await q.CreateGroupAsync(new GroupOptions { Name = "g", MaxRedeliveries = 1 }, default);

        var d1 = await g.DequeueAsync(1, default);
        await g.NackAsync([r.Address], default);
        (await g.DequeueAsync(1, default)).Should().HaveCount(1, "首次重投");
        await g.NackAsync([r.Address], default);
        (await g.DequeueAsync(1, default)).Should().BeEmpty("关闭档：达上限终结（无 DLQ）");

        q.DeadLetterQueue.Should().BeNull("Options.DeadLetter = null 关闭");

        // 队列继续工作
        var r2 = await q.EnqueueAsync(TierQueueTestFactory.Msg(0, 2), default);
        var d2 = await g.DequeueAsync(1, default);
        d2.Should().HaveCount(1);
        d2[0].Address.Should().Be(r2.Address);
    }

    // ══ 计数持久化：nack 后重启，计数延续直达死信 ══

    [Fact]
    public async Task RetryCount_PersistsAcrossRestart()
    {
        using var vol = new TestVolume();
        var opts = Opts(2);
        await using (var q = await TierQueueTestFactory.StartAsync(vol, o => opts))
        {
            var r = await q.EnqueueAsync(TierQueueTestFactory.Msg(0, 1), default);
            var g = await q.CreateGroupAsync(new GroupOptions { Name = "g", MaxRedeliveries = 2 }, default);
            var d = await g.DequeueAsync(1, default);
            await g.NackAsync([r.Address], default);   // count=1——随组状态持久
        }

        await using (var q2 = await TierQueueTestFactory.StartAsync(vol, o => opts))
        {
            var g2 = q2.CreateConsumerAsync("g");
            var d = await g2.DequeueAsync(1, default);
            d.Should().HaveCount(1);
            d[0].RedeliveryCount.Should().Be(1, "重试计数跨重启持久——续数非清零");

            // 再 nack×2：count 2 → 3 > max=2 → 死信
            await g2.NackAsync([d[0].Address], default);
            (await g2.DequeueAsync(1, default)).Should().HaveCount(1).And.ContainSingle(x => x.RedeliveryCount == 2);
            await g2.NackAsync([d[0].Address], default);
            (await g2.DequeueAsync(1, default)).Should().BeEmpty("持久计数延续 → 恰在 max+1 死信");

            (await q2.DeadLetterQueue!.DequeueAsync(10, default)).Should().HaveCount(1);
        }
    }

    // ══ 矩阵 14 后半：ReplayTo 往返——死信还原原 payload 入目标队列，DLQ 排空 ══

    [Fact]
    public async Task ReplayAll_RestoresOriginalPayload_TargetSeesIt_DlqDrained()
    {
        using var vol = new TestVolume();
        await using var q = await TierQueueTestFactory.StartAsync(vol, o => Opts(1));
        await using var target = await new TierQueueBuilder(vol.Fs, new TierQueueOptions
        {
            QueueName = "target",
            PageSize = 64 << 10,
            MemorySize = 8 << 20,
            DeadLetter = null,
        }).StartAsync();

        // 两条消息死信
        var r1 = await q.EnqueueAsync(TierQueueTestFactory.Msg(1, 1), default);
        var r2 = await q.EnqueueAsync(TierQueueTestFactory.Msg(1, 2), default);
        var g = await q.CreateGroupAsync(new GroupOptions { Name = "g", MaxRedeliveries = 1 }, default);
        foreach (var r in new[] { r1, r2 })
        {
            await g.DequeueAsync(2, default);
            await g.NackAsync([r.Address], default);
            await g.DequeueAsync(2, default);
            await g.NackAsync([r.Address], default);   // 两次 → 死信
        }
        (await q.DeadLetterQueue!.GetStatsAsync(default)).Groups.Should().NotBeEmpty();

        // 回放全部 → 目标队列见原 payload（地址序），DLQ 排空
        int n = await q.DeadLetterQueue!.ReplayAllAsync(target, default);
        n.Should().Be(2, "两条死信全回放");

        var seen = await target.DequeueAsync(10, default);
        seen.Should().HaveCount(2);
        seen.Select(d => TierQueueTestFactory.ParseMsg(d.Payload).Seq)
            .Should().Equal(SeqOneTwo, "原 payload 还原（解 envelope）");

        (await q.DeadLetterQueue!.DequeueAsync(10, default)).Should().BeEmpty("DLQ 排空");
    }

    [Fact]
    public async Task ReplaySingle_ByAddress_Works()
    {
        using var vol = new TestVolume();
        await using var q = await TierQueueTestFactory.StartAsync(vol, o => Opts(1));
        await using var target = await new TierQueueBuilder(vol.Fs, new TierQueueOptions
        {
            QueueName = "target", PageSize = 64 << 10, MemorySize = 8 << 20, DeadLetter = null,
        }).StartAsync();

        var r = await q.EnqueueAsync(TierQueueTestFactory.Msg(9, 9), default);
        var g = await q.CreateGroupAsync(new GroupOptions { Name = "g", MaxRedeliveries = 1 }, default);
        await g.DequeueAsync(1, default);
        await g.NackAsync([r.Address], default);
        await g.DequeueAsync(1, default);
        await g.NackAsync([r.Address], default);

        // 从 DLQ 投递面拿死信地址 → 单条回放
        var dlqBatch = await q.DeadLetterQueue!.DequeueAsync(1, default);
        dlqBatch.Should().HaveCount(1);
        var replayed = await q.DeadLetterQueue!.ReplayAsync(dlqBatch[0].Address, target, default);

        var seen = await target.DequeueAsync(10, default);
        seen.Should().HaveCount(1);
        seen[0].Address.Should().Be(replayed.Address);
        TierQueueTestFactory.ParseMsg(seen[0].Payload).Seq.Should().Be(9, "原 payload 还原");
        (await q.DeadLetterQueue!.DequeueAsync(10, default)).Should().BeEmpty("回放后 DLQ 该条已确认");
    }

    // ══ DLQ 随主队列重启存活（独立实例同 fs——注册表/位点持久）══

    [Fact]
    public async Task DlqSurvivesRestart_WithEntries()
    {
        using var vol = new TestVolume();
        var opts = Opts(1);
        LogicalAddress deadAddr;
        await using (var q = await TierQueueTestFactory.StartAsync(vol, o => opts))
        {
            var r = await q.EnqueueAsync(TierQueueTestFactory.Msg(0, 3), default);
            var g = await q.CreateGroupAsync(new GroupOptions { Name = "g", MaxRedeliveries = 1 }, default);
            await g.DequeueAsync(1, default);
            await g.NackAsync([r.Address], default);
            await g.DequeueAsync(1, default);
            await g.NackAsync([r.Address], default);
            var b = await q.DeadLetterQueue!.DequeueAsync(1, default);
            b.Should().HaveCount(1);
            deadAddr = b[0].Address;   // 拉取未 ack——重启后 DLQ 位点重放
        }

        await using (var q2 = await TierQueueTestFactory.StartAsync(vol, o => opts))
        {
            q2.DeadLetterQueue.Should().NotBeNull();
            var b = await q2.DeadLetterQueue!.DequeueAsync(10, default);
            b.Should().HaveCount(1, "DLQ 独立实例持久——未确认死信重投");
            b[0].Address.Should().Be(deadAddr);
        }
    }

    // ══ 矩阵 17（补）：溢出 payload → 死信往返一致（溢出值经 DLQ envelope + Replay 还原）══

    [Fact]
    public async Task OverflowPayload_DeadLetterRoundtrip_Consistent()
    {
        using var vol = new TestVolume();
        var opts = new TierQueueOptions
        {
            PageSize = 64 << 10, MemorySize = 8 << 20,
            OverflowPolicy = OverflowPolicy.Enabled,
            MinOverflowSize = 1024,   // 1KB 阈值
            DeadLetter = new DeadLetterOptions(),
        };
        var payload = new byte[1 << 20];   // 1MB（>> 阈值 → 溢出引擎）
        new Random(7).NextBytes(payload);

        await using var q = await TierQueueTestFactory.StartAsync(vol, _ => opts);
        var r = await q.EnqueueAsync(payload, default);
        var g = await q.CreateGroupAsync(new GroupOptions { Name = "g", MaxRedeliveries = 1 }, default);

        // nack×2 → 死信
        await g.DequeueAsync(1, default);
        await g.NackAsync([r.Address], default);
        await g.DequeueAsync(1, default);
        await g.NackAsync([r.Address], default);

        // DLQ 恰好一条 + 溢出 payload 完整
        var dlqBatch = await q.DeadLetterQueue!.DequeueAsync(10, default);
        dlqBatch.Should().HaveCount(1);
        DeadLetterEnvelope.TryUnwrap(dlqBatch[0].Payload, out var src, out _, out _, out var dlqPayload)
            .Should().BeTrue();
        src.Should().Be(r.Address);
        dlqPayload.Should().Equal(payload, "溢出 payload 经死信 envelope 完整保留");

        // Replay 到目标队列 → 原 payload 还原（target 也需开溢出承载 1MB payload）
        await using var target = await new TierQueueBuilder(vol.Fs, new TierQueueOptions
        {
            QueueName = "target", PageSize = 64 << 10, MemorySize = 8 << 20, DeadLetter = null,
            OverflowPolicy = OverflowPolicy.Enabled, MinOverflowSize = 1024,
        }).StartAsync();
        await q.DeadLetterQueue!.ReplayAsync(dlqBatch[0].Address, target, default);
        var seen = await target.DequeueAsync(10, default);
        seen.Should().HaveCount(1);
        seen[0].Payload.Should().Equal(payload, "Replay 还原溢出 payload");
    }
}
