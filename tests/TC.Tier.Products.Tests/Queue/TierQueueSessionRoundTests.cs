using TC.Tier.Contracts.Storage;
using TC.Tier.Runtime.Structures.Metadata;
using TC.Tier.Runtime.Transactions;
using Xunit;

namespace TC.Tier.Products.Tests.Queue;

/// <summary>
/// TierQueue P3b 档二测试（tc-tier-queue-spec §6.3 / §13 验证矩阵 7）——
/// 业务同域事务：组位点与业务效果经 Session 2PC 同生共死。
/// 业务存储替身 = VersionedMetadata（标准 ITransactionParticipant）。
/// </summary>
public sealed class TierQueueSessionRoundTests
{
    private static async Task<VersionedMetadata> NewBizMetaAsync(TestVolume vol, string name)
    {
        var meta = new VersionedMetadata(vol.Fs, new VersionedMetadataSettings(
            new TC.Tier.Runtime.Storage.StorageEngineOptions(name, 4L << 20,
                enableSegmentation: true, preallocateFile: false))
        {
            PayloadSize = 64,
        });
        meta.Initialize();
        await meta.WaitForReadyAsync(default);
        return meta;
    }

    // ══ 矩阵 7：Commit 路径——业务效果与组位点双落地 ══

    [Fact]
    public async Task AckInRound_Commit_BusinessEffectAndCursorLandTogether()
    {
        using var vol = new TestVolume();
        await using var q = await TierQueueTestFactory.StartAsync(vol, o => new TierQueueOptions
        {
            PageSize = 64 << 10, MemorySize = 8 << 20, MaxInFlight = 8, DeadLetter = null,
        });
        var biz = await NewBizMetaAsync(vol, "biz.effect");

        var r = await q.EnqueueAsync(TierQueueTestFactory.Msg(0, 1), default);
        var g = await q.CreateGroupAsync(new GroupOptions { Name = "g" }, default);
        var delivery = await g.DequeueAsync(1, default);
        delivery.Should().HaveCount(1);

        // 域装配：业务存储 + 组位点参与者
        var session = SessionManager.Create(vol.Fs, "queue-domain", HangingResolution.ForwardCommit,
            ("biz", biz), ("queue.g", q.GetGroupParticipant("g")));
        session.Initialize();
        await session.WaitForReadyAsync(default);

        // 回合：业务效果 staged + 确认挂进回合 → 2PC 原子
        using var ts = session.OpenSession("consumer");
        ts.Stage(() => biz.Write([1, 2, 3, 4, 5, 6, 7, 8]));   // 业务效果（处理消息的副作用）
        await g.AckInRoundAsync(ts, [new DeliveryReceipt(delivery[0].Address, delivery[0].Token)], default);
        long seq = await ts.CommitAsync();

        seq.Should().BeGreaterThan(0);
        g.GroupCursor.Should().BeGreaterThan(r.Address, "组位点推进");
        var bizBuf = new byte[8];
        biz.Read(bizBuf).Should().Be(8, "业务效果落地");
        bizBuf[0].Should().Be(1);
        (await g.DequeueAsync(10, default)).Should().BeEmpty("确认后不重投");
    }

    // ══ 矩阵 7：Abort 路径——处理中断 = 组位点与业务效果同回退、消息重投 ══

    [Fact]
    public async Task AckInRound_AboutToCommitButAborted_CursorAndBizRollbackTogether()
    {
        using var vol = new TestVolume();
        await using var q = await TierQueueTestFactory.StartAsync(vol, o => new TierQueueOptions
        {
            PageSize = 64 << 10, MemorySize = 8 << 20, MaxInFlight = 8, DeadLetter = null,
        });
        var biz = await NewBizMetaAsync(vol, "biz.effect");

        var r = await q.EnqueueAsync(TierQueueTestFactory.Msg(0, 2), default);
        var g = await q.CreateGroupAsync(new GroupOptions { Name = "g" }, default);
        var delivery = await g.DequeueAsync(1, default);
        LogicalAddress cursorBefore = g.GroupCursor;

        var session = SessionManager.Create(vol.Fs, "queue-domain", HangingResolution.ForwardCommit,
            ("biz", biz), ("queue.g", q.GetGroupParticipant("g")));
        session.Initialize();
        await session.WaitForReadyAsync(default);

        // 回合 staged（业务 + 确认）但处理中断 → Abort（未提交撤销——结构零触碰）
        using var ts = session.OpenSession("consumer");
        ts.Stage(() => biz.Write([9, 9, 9, 9, 9, 9, 9, 9]));
        await g.AckInRoundAsync(ts, [new DeliveryReceipt(delivery[0].Address, delivery[0].Token)], default);
        ts.Abort();

        g.GroupCursor.Should().Be(cursorBefore, "组位点不动（Abort = 位点与业务同回退）");
        var bizBuf = new byte[8];
        biz.Read(bizBuf);
        bizBuf[0].Should().NotBe(9, "业务效果未落地");

        // 消息重投（at-least-once 兜底——未生效的处理重新可见）
        var redelivered = await g.DequeueAsync(10, default);
        redelivered.Should().HaveCount(1, "未确认 → 重投");
        redelivered[0].Address.Should().Be(r.Address);
    }

    // ══ 档二 fencing：回合内迟交同拒 ══

    [Fact]
    public async Task AckInRound_StaleToken_Rejected()
    {
        using var vol = new TestVolume();
        await using var q = await TierQueueTestFactory.StartAsync(vol, o => new TierQueueOptions
        {
            PageSize = 64 << 10, MemorySize = 8 << 20, MaxInFlight = 8, DeadLetter = null,
        });
        var biz = await NewBizMetaAsync(vol, "biz.effect");

        await q.EnqueueAsync(TierQueueTestFactory.Msg(0, 3), default);
        var g = await q.CreateGroupAsync(new GroupOptions { Name = "g" }, default);
        var a = q.CreateConsumerAsync("$default");
        var b = q.CreateConsumerAsync("$default");
        var delivery = (await a.DequeueAsync(1, default))[0];
        var staleReceipt = new DeliveryReceipt(delivery.Address, delivery.Token);

        var claimed = await b.ClaimAsync(new ClaimFilter(TimeSpan.Zero), default);   // epoch +1
        claimed.Should().HaveCount(1);

        var session = SessionManager.Create(vol.Fs, "queue-domain", HangingResolution.ForwardCommit,
            ("biz", biz), ("queue.g", q.GetGroupParticipant("g")));
        session.Initialize();
        await session.WaitForReadyAsync(default);
        using var ts = session.OpenSession();

        Func<Task> act = async () => await a.AckInRoundAsync(ts, [staleReceipt], default);
        await act.Should().ThrowAsync<StaleDeliveryException>("回合内迟交同样确定性拒绝（fencing 不因档二豁免）");
    }
}
