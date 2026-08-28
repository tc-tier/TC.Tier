namespace TC.Tier.Products.Tests.Wal;

/// <summary>
/// TierWAL 元数据 Opaque 槽——raft term/vote/config 原子替换（内容零知识）+ 容量边界 + 跨重启。
/// </summary>
public class TierWalMetaTests
{
    [Fact]
    public async Task WriteMeta_PersistsImmediately_ReadBack()
    {
        using var vol = new TestVolume();
        await using var wal = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit);
        await wal.AppendSingleAsync(WalTestFactory.Entry(1), default);

        var blob = new byte[] { 1, 2, 3, 4 };
        await wal.WriteMetaAsync(blob, default);

        wal.ReadMeta().ToArray().Should().Equal(blob);
        // ★ raft 契约①：投票/任期变更必须持久化后才应答——WriteMetaAsync 内含显式提交
        wal.PersistedIndex.Should().Be(wal.AllocatedIndex);
        wal.IsPersisted(wal.AllocatedIndex).Should().BeTrue();
    }

    [Fact]
    public async Task WriteMeta_ReplacesAtomically()
    {
        using var vol = new TestVolume();
        await using var wal = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit);

        await wal.WriteMetaAsync(new byte[] { 1 }, default);
        await wal.WriteMetaAsync(new byte[] { 9, 9, 9 }, default);
        wal.ReadMeta().ToArray().Should().Equal(9, 9, 9);
    }

    [Fact]
    public void ReadMeta_Unset_Empty()
    {
        using var vol = new TestVolume();
        using var wal = WalTestFactory.StartAsync(vol).GetAwaiter().GetResult();
        wal.ReadMeta().IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task WriteMeta_OverCapacity_Throws()
    {
        using var vol = new TestVolume();
        // opaque 容量 256B——raft 区 = 256 - 56 = 200B
        await using var wal = await WalTestFactory.StartAsync(vol, o => o.WithMetaOpaqueBytes(256));
        var act = () => wal.WriteMetaAsync(new byte[201], default).AsTask();
        // ★ 底层 ManagedMetaPolicy 容量契约：超容量抛 ArgumentException（stage 时）
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*opaque capacity*");
    }

    [Fact]
    public async Task WriteMeta_SurvivesRestart()
    {
        using var vol = new TestVolume();
        var blob = new byte[] { 5, 6, 7 };
        long persisted;
        using (var wal = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit))
        {
            await wal.WriteMetaAsync(blob, default);
            persisted = wal.PersistedIndex;
        }

        await using var wal2 = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit);
        wal2.ReadMeta().ToArray().Should().Equal(blob);
        wal2.PersistedIndex.Should().Be(persisted);
    }

    [Fact]
    public async Task WriteMeta_RaftArea_IndependentOfEntryData()
    {
        using var vol = new TestVolume();
        await using var wal = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit);
        await wal.AppendBatchAsync(Enumerable.Range(1, 20).Select(i => (ReadOnlyMemory<byte>)WalTestFactory.Entry(i)).ToList(), default);
        await wal.WriteMetaAsync(new byte[] { 0xAB }, default);

        // meta 写入不影响 entry 数据/水位推进
        wal.AllocatedIndex.Should().Be(20);
        var entries = await TierWalTests.ReadAll(wal, 1, default);
        entries.Should().HaveCount(20);
        entries[^1].Data.ToArray().Should().Equal(WalTestFactory.Entry(20));
    }
}
