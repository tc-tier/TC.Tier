using TC.Tier.Contracts.Transactions;
using TC.Tier.Runtime.Tests;

namespace TC.Tier.Products.Tests.Blob;

/// <summary>
/// TierBlob Session 2PC 测试（tierblob-spec §8 验证矩阵 11——参与者 Prepare/Abort 悬干回滚，
/// "业务写 + 对象登记"原子——裁定⑤）。
/// </summary>
public sealed class TierBlobSession2pcTests
{
    private const long SessionSeq = 1000;   // 会话协调者 seq 域（大于内部注册序）

    // ══ 矩阵 11a：Prepare 后登记 + Abort → 登记回滚（内存表 + 表帧尾截断），已提交对象零影响 ══

    [Fact]
    public async Task ParticipantAbort_RollsBackDeferredRegistrations()
    {
        using var vol = new TestVolume();
        await using var blob = await TierBlobTestFactory.StartAsync(vol);

        var committed = await blob.PutAsync(TierBlobTestFactory.MakeData(1000, 0x01));

        var participant = blob.GetParticipant();
        participant.Prepare(SessionSeq);

        // 会话域内登记：PutAsync 自动延迟提交（挂 undo）
        var deferred = await blob.PutAsync(TierBlobTestFactory.MakeData(2000, 0x02));
        deferred.ObjectId.CompareTo(committed.ObjectId).Should().BeGreaterThan(0);

        // 会话域内删除亦延迟（undo 可回退）
        await blob.DeleteAsync(committed.ObjectId);

        // 域内可见（读路径零锁——内存镜像已推进）
        (await blob.GetInfoAsync(deferred.ObjectId)).State.Should().Be(BlobState.Active);

        participant.Abort(SessionSeq);

        // 登记回滚：延迟对象消失；延迟删除回退（对象复活）
        await FluentActions.Awaiting(async () => await blob.GetInfoAsync(deferred.ObjectId))
            .Should().ThrowAsync<KeyNotFoundException>("延迟登记随事务回滚");
        (await blob.GetInfoAsync(committed.ObjectId)).State.Should().Be(BlobState.Active, "延迟删除回退——对象复活");

        var all = await TierBlobTests.CollectAsync(blob.ListAsync());
        all.Should().ContainSingle("表镜像回到会话前状态");
        all[0].ObjectId.Should().Be(committed.ObjectId);
    }

    // ══ 矩阵 11b：Prepare 后登记 + ConfirmCommitted → 登记持久化，重启后存活 ══

    [Fact]
    public async Task ParticipantConfirm_PersistsRegistrations_AcrossRestart()
    {
        using var vol = new TestVolume();
        LogicalAddress confirmedHandle;
        var confirmedData = TierBlobTestFactory.MakeData(1200, 0x0B);

        await using (var blob = await TierBlobTestFactory.StartAsync(vol))
        {
            var participant = blob.GetParticipant();
            participant.Prepare(SessionSeq);

            var put = await blob.PutAsync(confirmedData);
            confirmedHandle = put.ObjectId;

            participant.ConfirmCommitted(SessionSeq);
        }

        await using (var blob = await TierBlobTestFactory.StartAsync(vol))
        {
            var dst = new byte[confirmedData.Length];
            (await blob.GetAsync(confirmedHandle, dst)).Should().Be(confirmedData.Length);
            dst.Should().Equal(confirmedData, "Confirm 后登记持久——重启存活");
        }
    }

    // ══ 矩阵 11c：Prepare 后崩溃（未 Confirm 未 Abort）→ 悬干登记重启不可见（表 meta 悬干裁决尾截断）══

    [Fact]
    public async Task DanglingPreparedRegistrations_DiscardedOnRecovery()
    {
        using var vol = new TestVolume();
        var keptData = TierBlobTestFactory.MakeData(800, 0x0C);
        LogicalAddress keptHandle, danglingHandle;

        await using (var blob = await TierBlobTestFactory.StartAsync(vol))
        {
            keptHandle = (await blob.PutAsync(keptData)).ObjectId;

            var participant = blob.GetParticipant();
            participant.Prepare(SessionSeq);

            var dangling = await blob.PutAsync(TierBlobTestFactory.MakeData(1500, 0x0D));
            danglingHandle = dangling.ObjectId;

            // 崩溃形态：不 Confirm 不 Abort，直接 Dispose
        }

        await using (var blob = await TierBlobTestFactory.StartAsync(vol))
        {
            var dst = new byte[keptData.Length];
            (await blob.GetAsync(keptHandle, dst)).Should().Be(keptData.Length);
            dst.Should().Equal(keptData, "已提交对象零影响");

            await FluentActions.Awaiting(async () => await blob.GetInfoAsync(danglingHandle))
                .Should().ThrowAsync<KeyNotFoundException>("悬干 Prepare 的登记重启即弃（表引擎悬干裁决）");

            // 数据面：悬干登记的数据帧 = 孤儿——空间保留（at-least-once 诚实形态）
            var all = await TierBlobTests.CollectAsync(blob.ListAsync(new BlobListFilter(default, default, IncludeTombstones: true)));
            all.Select(b => b.ObjectId).Should().Equal(keptHandle);
        }
    }

    // ══ 参与者 seq 观测面：LastPreparedSeq/LastCommittedSeq 透传表引擎 ══

    [Fact]
    public async Task Participant_SeqObservability()
    {
        using var vol = new TestVolume();
        await using var blob = await TierBlobTestFactory.StartAsync(vol);

        await blob.PutAsync(TierBlobTestFactory.MakeData(100, 0x01));   // 内部注册 seq=1 → committed
        var participant = blob.GetParticipant();
        participant.LastCommittedSeq.Should().BeGreaterThan(0, "内部注册已经表引擎 Confirm 收口");

        participant.Prepare(SessionSeq);
        participant.LastPreparedSeq.Should().Be(SessionSeq);
        participant.ConfirmCommitted(SessionSeq);
        participant.LastCommittedSeq.Should().Be(SessionSeq);

        // Abort 幂等（重复 Abort 同 seq no-op）
        participant.Prepare(SessionSeq + 1);
        participant.Abort(SessionSeq + 1);
        participant.Abort(SessionSeq + 1);
        participant.LastCommittedSeq.Should().Be(SessionSeq);
    }
}
