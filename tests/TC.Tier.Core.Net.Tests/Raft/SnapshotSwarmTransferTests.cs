using FluentAssertions;
using Xunit;
using TC.Tier.Core.Net.Raft;
using TC.Tier.Core.Net.Swarm;
using TC.Tier.Core.Net.Transport.InProcess;
using TC.Tier.Core.Net.Tests.Fixtures;

namespace TC.Tier.Core.Net.Tests.Raft;

/// <summary>
/// SnapshotSwarmTransfer contract tests (spec-12 §6.1 ISnapshotTransfer swarm data plane):
/// export publishes the blockized snapshot and returns handshake coordination
/// (manifest + holders); import driven by that coordination reconstructs the snapshot
/// region byte-exact; null coordination = mixed-assembly guard.
/// </summary>
public class SnapshotSwarmTransferTests
{
    private sealed class Rig : IAsyncDisposable
    {
        public required InProcessNode Node { get; init; }
        public required InMemoryRaftStore Store { get; init; }
        public required SnapshotSwarmTransfer Transfer { get; init; }
        public required SwarmSync Swarm { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Swarm.DisposeAsync().ConfigureAwait(false);
            await Node.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<Rig> CreateRigAsync(InProcessTransportHub hub)
    {
        var node = hub.Register(NodeId.NewRandom());
        node.Start();
        var store = new InMemoryRaftStore();
        await store.InitializeAsync();
        var swarm = new SwarmSync(node);
        await swarm.StartAsync();
        var sync = new SnapshotSwarmSync(swarm, store);
        var transfer = new SnapshotSwarmTransfer(node, sync, store, blockSize: 64);
        return new Rig { Node = node, Store = store, Transfer = transfer, Swarm = swarm };
    }

    private static async Task AppendAsync(InMemoryRaftStore store, int count, long prevIndex = 0)
    {
        var entries = new (long, byte, ReadOnlyMemory<byte>)[count];
        for (var i = 0; i < count; i++)
        {
            var payload = new byte[32];
            payload[0] = (byte)i;
            entries[i] = (1, RaftEntryKind.Command, payload);
        }
        (await store.AppendAsync(prevIndex, entries)).Should().Be(prevIndex + count);
        await store.WaitForPersistedAsync(prevIndex + count);
    }

    /// <summary>Export = snapshot creation strategy (advance to persisted tail) + coordination:
    /// manifest carries the blockized content identity, holders = [self].</summary>
    [Fact]
    public async Task Export_ReturnsCoordinationWithManifestAndSelfHolder()
    {
        await using var hub = new InProcessTransportHub();
        await using var rig = await CreateRigAsync(hub);
        await AppendAsync(rig.Store, 100);

        var coordination = await rig.Transfer.ExportSnapshotAsync(NodeId.NewRandom());

        coordination.Should().NotBeNull();
        rig.Store.SnapshotIndex.Should().Be(100, "export advances the snapshot point to the persisted tail");
        coordination!.BlockSize.Should().Be(64);
        coordination.TotalBytes.Should().BePositive();
        coordination.Checksums.Should().NotBeEmpty();
        coordination.ManifestId.Should().NotBe(default);
        coordination.Holders.Should().Equal(rig.Node.Self);
    }

    /// <summary>Import driven by the exported coordination: follower reconstructs the snapshot
    /// region byte-exact from the leader's blocks.</summary>
    [Fact]
    public async Task Install_ExportedCoordination_ReconstructsRegionByteExact()
    {
        await using var hub = new InProcessTransportHub();
        await using var source = await CreateRigAsync(hub);
        await using var target = await CreateRigAsync(hub);
        await AppendAsync(source.Store, 40);

        var coordination = await source.Transfer.ExportSnapshotAsync(target.Node.Self);
        await target.Transfer.ImportSnapshotAsync(source.Node.Self, coordination);

        target.Store.SnapshotIndex.Should().Be(40);
        var expected = await CollectAsync(source.Store, 40);
        var actual = await CollectAsync(target.Store, 40);
        actual.Should().HaveCount(expected.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            actual[i].Item1.Should().Be(expected[i].Item1);
            actual[i].Item2.Should().Be(expected[i].Item2);
            actual[i].Item3.Should().Be(expected[i].Item3);
            actual[i].Item4.Should().Equal(expected[i].Item4);
        }
    }

    /// <summary>多源成立（spec-12 §6.1 增量 S3——设计稿测试计划 #1 核心）：follower 安装完成后
    /// 自动上报持有——leader 端持有表从 [本端] 扩为含 follower（多源从字面成立）。</summary>
    [Fact]
    public async Task Install_FollowerAnnounces_LeaderHoldersTableGainsFollower()
    {
        await using var hub = new InProcessTransportHub();
        await using var source = await CreateRigAsync(hub);
        await using var target = await CreateRigAsync(hub);
        await AppendAsync(source.Store, 40);

        var coordination = await source.Transfer.ExportSnapshotAsync(target.Node.Self);
        await target.Transfer.ImportSnapshotAsync(source.Node.Self, coordination);

        source.Swarm.GetHolders(coordination!.ManifestId)
            .Should().Contain(target.Node.Self, "安装完成上报——持有表登记 follower（下次安装即可多源）");
    }

    /// <summary>换代清表（设计稿测试计划 #2 场景级）：快照换代（新 N₀ = 新 manifest Id）——
    /// 旧代 Announce 不污染新代表（ResetHolders 生效），follower 重报后回归新代。</summary>
    [Fact]
    public async Task Regeneration_NewExport_ClearsOldGeneration()
    {
        await using var hub = new InProcessTransportHub();
        await using var source = await CreateRigAsync(hub);
        await using var target = await CreateRigAsync(hub);
        await AppendAsync(source.Store, 40);

        var oldGen = await source.Transfer.ExportSnapshotAsync(target.Node.Self);
        await target.Transfer.ImportSnapshotAsync(source.Node.Self, oldGen);
        source.Swarm.GetHolders(oldGen!.ManifestId).Should().Contain(target.Node.Self);

        // 换代：更多日志（从快照尾续接）→ 新导出（新 N₀ = 新 manifest Id）
        await AppendAsync(source.Store, 60, prevIndex: 40);
        var newGen = await source.Transfer.ExportSnapshotAsync(target.Node.Self);
        newGen!.ManifestId.Should().NotBe(oldGen.ManifestId, "换代 = 新内容标识");

        source.Swarm.GetHolders(newGen.ManifestId).Should().Equal([source.Node.Self],
            "旧代 Announce 不污染新代表——新代表表 = [本端]");

        // follower 重报（换装新基线后——模拟触发③路径）后回归新代
        await target.Transfer.ImportSnapshotAsync(source.Node.Self, newGen);
        source.Swarm.GetHolders(newGen.ManifestId).Should().Contain(target.Node.Self, "重报后回归");
    }

    /// <summary>Mixed-assembly guard: swarm import with null coordination fails fast (stream-mode
    /// handshake cannot drive a swarm data plane — cluster assembly must be consistent).</summary>
    [Fact]
    public async Task Import_NullCoordination_ThrowsInvalidOperation()
    {
        await using var hub = new InProcessTransportHub();
        await using var rig = await CreateRigAsync(hub);

        var act = async () => await rig.Transfer.ImportSnapshotAsync(NodeId.NewRandom(), null);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    /// <summary>Empty snapshot (no entries): zero blocks — coordination still carries a valid
    /// (empty) manifest and the import anchors the empty snapshot region.</summary>
    [Fact]
    public async Task Export_EmptySnapshot_ZeroBlocks_ImportAnchors()
    {
        await using var hub = new InProcessTransportHub();
        await using var source = await CreateRigAsync(hub);
        await using var target = await CreateRigAsync(hub);
        await AppendAsync(target.Store, 5);   // target 已有内容——空快照导入清空重锚

        var coordination = await source.Transfer.ExportSnapshotAsync(target.Node.Self);
        coordination!.TotalBytes.Should().Be(0);
        coordination.Checksums.Should().BeEmpty();
        coordination.ToManifest().BlockCount.Should().Be(0);

        await target.Transfer.ImportSnapshotAsync(source.Node.Self, coordination);
        target.Store.SnapshotIndex.Should().Be(0);
        target.Store.LastLogIndex.Should().Be(0);
    }

    private static async Task<List<(long, long, byte, byte[])>> CollectAsync(InMemoryRaftStore store, long snapshotIndex)   // CA1859：具体形
    {
        var list = new List<(long, long, byte, byte[])>();
        await foreach (var e in store.ReadSnapshotEntriesAsync(snapshotIndex))
            list.Add((e.Index, e.Term, e.Kind, e.Content.ToArray()));
        return list;
    }
}
