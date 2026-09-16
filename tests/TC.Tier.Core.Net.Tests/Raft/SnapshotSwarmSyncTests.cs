using System.Collections.Concurrent;
using FluentAssertions;
using Xunit;
using TC.Tier.Core.Net.Raft;
using TC.Tier.Core.Net.Swarm;
using TC.Tier.Core.Net.Transport.InProcess;
using TC.Tier.Core.Net.Tests.Fixtures;

namespace TC.Tier.Core.Net.Tests.Raft;

/// <summary>
/// Snapshot multi-source sync scenarios (spec-12 §6.1 consumer — SwarmSync first consumer):
/// publish → multi-source parallel install reconstructs the snapshot region byte-exact;
/// the acceptance contract = multi-source product byte-identical to the single-source stream
/// product (<see cref="SnapshotStreamTransferTests"/> baseline).
/// </summary>
public class SnapshotSwarmSyncTests
{
    /// <summary>单节点装配（InProcess——SwarmSync 0x03 挂载 + 快照装配 + 可选单源流式传输面）。</summary>
    private sealed class NodeRig(
        InProcessNode node, SwarmSync swarm, InMemoryRaftStore store,
        SnapshotSwarmSync snapshotSwarm, SnapshotStreamTransfer? stream) : IAsyncDisposable
    {
        public InProcessNode Node => node;
        public SwarmSync Swarm => swarm;
        public InMemoryRaftStore Store => store;
        public SnapshotSwarmSync SnapshotSwarm => snapshotSwarm;
        public SnapshotStreamTransfer? Stream => stream;

        public async ValueTask DisposeAsync()
        {
            if (stream is not null) await stream.DisposeAsync();
            await swarm.DisposeAsync();
            await node.DisposeAsync();
        }
    }

    private sealed class ClusterFixture(InProcessTransportHub hub, NodeRig[] rigs) : IAsyncDisposable
    {
        public InProcessTransportHub Hub => hub;
        public NodeRig[] Rigs => rigs;

        public async ValueTask DisposeAsync()
        {
            foreach (var rig in rigs)
            {
                try { await rig.DisposeAsync(); } catch { /* 尽力清理 */ }
            }
            await hub.DisposeAsync();
        }
    }

    private static async Task<NodeRig> CreateRigAsync(InProcessTransportHub hub, bool withStream = false)
    {
        var node = hub.Register(NodeId.NewRandom());
        node.Start();
        var store = new InMemoryRaftStore();
        await store.InitializeAsync();
        var swarm = new SwarmSync(node);
        await swarm.StartAsync();
        var snapshotSwarm = new SnapshotSwarmSync(swarm, store);
        SnapshotStreamTransfer? stream = null;
        if (withStream)
        {
            stream = new SnapshotStreamTransfer(node, store);
            await stream.StartAsync();
        }
        return new NodeRig(node, swarm, store, snapshotSwarm, stream);
    }

    private static async Task<ClusterFixture> CreateClusterAsync(params bool[] withStream)
    {
        var hub = new InProcessTransportHub();
        var rigs = new NodeRig[withStream.Length];
        try
        {
            for (var i = 0; i < withStream.Length; i++)
                rigs[i] = await CreateRigAsync(hub, withStream[i]);
            return new ClusterFixture(hub, rigs);
        }
        catch
        {
            foreach (var r in rigs.Where(x => x is not null)) await r.DisposeAsync();
            await hub.DisposeAsync();
            throw;
        }
    }

    /// <summary>追加 [1..count] 条目（前半 term 1、后半 term 2——非单调但合法的不降任期）。</summary>
    private static async Task AppendAsync(InMemoryRaftStore store, int count)
    {
        var entries = new (long, byte, ReadOnlyMemory<byte>)[count];
        for (var i = 0; i < count; i++)
        {
            var payload = new byte[128];
            payload[0] = (byte)i;
            payload[1] = (byte)(i >> 8);
            entries[i] = (i < count / 2 ? 1 : 2, RaftEntryKind.Command, payload);
        }
        (await store.AppendAsync(0, entries)).Should().Be(count);
        await store.WaitForPersistedAsync(count);
    }

    private static async Task<List<(long, long, byte, byte[])>> CollectAsync(IRaftStore store, long snapshotIndex)
    {
        var list = new List<(long, long, byte, byte[])>();
        await foreach (var e in store.ReadSnapshotEntriesAsync(snapshotIndex)) list.Add((e.Index, e.Term, e.Kind, e.Content.ToArray()));
        return list;
    }

    private static async Task AssertRegionsEqualAsync(IRaftStore expected, IRaftStore actual, long snapshotIndex, string because)
    {
        var e = await CollectAsync(expected, snapshotIndex);
        var a = await CollectAsync(actual, snapshotIndex);
        a.Should().HaveCount(e.Count, because);
        for (var i = 0; i < e.Count; i++)
        {
            a[i].Item1.Should().Be(e[i].Item1, $"{because}——index 逐条一致");
            a[i].Item2.Should().Be(e[i].Item2, $"{because}——term 逐条一致");
            a[i].Item3.Should().Be(e[i].Item3, $"{because}——kind 逐条一致");
            a[i].Item4.Should().Equal(e[i].Item4, $"{because}——content 逐字节一致");
        }
    }

    /// <summary>单 holder（两步走单源基线经 Swarm 形态）：发布 → 安装——快照区逐字节一致。</summary>
    [Fact]
    public async Task Install_SingleHolder_ByteExact()
    {
        await using var fx = await CreateClusterAsync(false, false);
        await AppendAsync(fx.Rigs[0].Store, 20);

        var manifest = await fx.Rigs[0].SnapshotSwarm.PublishSnapshotAsync(20, blockSize: 64);
        await fx.Rigs[1].SnapshotSwarm.InstallSnapshotAsync(manifest, [fx.Rigs[0].Node.Self]);

        fx.Rigs[1].Store.SnapshotIndex.Should().Be(20);
        fx.Rigs[1].Store.LastLogIndex.Should().Be(20);
        await AssertRegionsEqualAsync(fx.Rigs[0].Store, fx.Rigs[1].Store, 20, "单 holder 安装");
    }

    /// <summary>多 holder（§6.1 并行度 K）：3 源安装——逐字节一致 + 多源利用率 &gt; 1。</summary>
    [Fact]
    public async Task Install_MultiHolder_UtilizesMultipleSources_ByteExact()
    {
        await using var fx = await CreateClusterAsync(false, false, false, false);
        for (var i = 0; i < 3; i++) await AppendAsync(fx.Rigs[i].Store, 30);

        var manifest = await fx.Rigs[0].SnapshotSwarm.PublishSnapshotAsync(30, blockSize: 64);
        await fx.Rigs[1].SnapshotSwarm.PublishSnapshotAsync(30, blockSize: 64);
        await fx.Rigs[2].SnapshotSwarm.PublishSnapshotAsync(30, blockSize: 64);

        var sourcesUsed = new ConcurrentDictionary<NodeId, byte>();
        var holders = fx.Rigs.Take(3).Select(r => r.Node.Self).ToList();
        await fx.Rigs[3].SnapshotSwarm.InstallSnapshotAsync(manifest, holders,
            onBlockCompleted: (_, from) => sourcesUsed.TryAdd(from, 0));

        fx.Rigs[3].Store.SnapshotIndex.Should().Be(30);
        await AssertRegionsEqualAsync(fx.Rigs[0].Store, fx.Rigs[3].Store, 30, "多 holder 安装");
        sourcesUsed.Count.Should().BeGreaterThan(1, "K 并行多源拉取——多个 holder 被利用（块级换源轮转）");
    }

    /// <summary>验收契约：多源产物与单源流式产物逐字节对拍（spec-12 §6/§6.1——两种数据面
    /// 结果同一快照区）。</summary>
    [Fact]
    public async Task Install_MultiVsSingle_ParityByteExact()
    {
        await using var fx = await CreateClusterAsync(true, true, false, false, false);
        // rig0 = 源（单源导出 + 多源发布）、rig1 = 单源目标、rig2/rig3 = 多源 holder、rig4 = 多源目标
        await AppendAsync(fx.Rigs[0].Store, 25);
        await AppendAsync(fx.Rigs[2].Store, 25);
        await AppendAsync(fx.Rigs[3].Store, 25);

        // 单源：源 → rig1（快照点由导出侧推进到已持久化尾 = 25）
        await fx.Rigs[0].Stream!.ExportSnapshotAsync(fx.Rigs[1].Node.Self);
        await fx.Rigs[1].Stream!.ImportSnapshotAsync(fx.Rigs[0].Node.Self);

        // 多源：源 + rig2 + rig3 发布 → rig4 安装
        var manifest = await fx.Rigs[0].SnapshotSwarm.PublishSnapshotAsync(25, blockSize: 64);
        await fx.Rigs[2].SnapshotSwarm.PublishSnapshotAsync(25, blockSize: 64);
        await fx.Rigs[3].SnapshotSwarm.PublishSnapshotAsync(25, blockSize: 64);
        await fx.Rigs[4].SnapshotSwarm.InstallSnapshotAsync(manifest, [fx.Rigs[0].Node.Self, fx.Rigs[2].Node.Self, fx.Rigs[3].Node.Self]);

        fx.Rigs[4].Store.SnapshotIndex.Should().Be(25, "多源安装快照点 = 覆盖点");
        // 对拍：多源目标（rig4）与单源目标（rig1）快照区逐字节一致
        await AssertRegionsEqualAsync(fx.Rigs[1].Store, fx.Rigs[4].Store, 25, "多源与单源产物");
    }

    /// <summary>空快照经 Swarm 形态：发布零块 manifest → 安装空快照区。</summary>
    [Fact]
    public async Task Install_EmptySnapshot_ZeroBlocks()
    {
        await using var fx = await CreateClusterAsync(false, false);
        var manifest = await fx.Rigs[0].SnapshotSwarm.PublishSnapshotAsync(0, blockSize: 64);

        manifest.BlockCount.Should().Be(0);
        await fx.Rigs[1].SnapshotSwarm.InstallSnapshotAsync(manifest, [fx.Rigs[0].Node.Self]);

        fx.Rigs[1].Store.SnapshotIndex.Should().Be(0);
        fx.Rigs[1].Store.LastLogIndex.Should().Be(0);
        (await CollectAsync(fx.Rigs[1].Store, 0)).Should().BeEmpty();
    }

    /// <summary>基线上报（spec-12 §6.1 增量 S4——设计稿测试计划 #4 启动恢复路径）：节点恢复出
    /// 快照基线（SnapshotIndex &gt; 0）→ AnnounceBaselineAsync → leader 端持有表登记
    /// （manifest Id = 覆盖点确定性映射，与发布侧同代）。</summary>
    [Fact]
    public async Task AnnounceBaseline_ReportsHeldGeneration_ToLeader()
    {
        await using var fx = await CreateClusterAsync(false, false);
        var holder = fx.Rigs[0];
        var leaderNode = fx.Hub.Register(NodeId.NewRandom());
        leaderNode.Start();
        var leaderSwarm = new SwarmSync(leaderNode);
        await leaderSwarm.StartAsync();

        // holder 恢复出基线：追加 + 快照点推进到持久化尾（导出同款策略）
        await AppendAsync(holder.Store, 40);
        await holder.Store.TruncatePrefixToAsync(40);

        await holder.SnapshotSwarm.AnnounceBaselineAsync(leaderNode.Self);

        leaderSwarm.GetHolders(SnapshotBlockizer.ManifestIdFor(40))
            .Should().Contain(holder.Node.Self, "启动恢复上报——leader 持有表登记基线持有者");
    }

    [Fact]
    public async Task AnnounceBaseline_NoBaseline_NoOp()
    {
        await using var fx = await CreateClusterAsync(false, false);
        var holder = fx.Rigs[0];
        var leaderNode = fx.Hub.Register(NodeId.NewRandom());
        leaderNode.Start();
        var leaderSwarm = new SwarmSync(leaderNode);
        await leaderSwarm.StartAsync();

        await holder.SnapshotSwarm.AnnounceBaselineAsync(leaderNode.Self);

        leaderSwarm.GetHolders(SnapshotBlockizer.ManifestIdFor(0)).Should().Equal([leaderNode.Self],
            "无基线不上报——leader 侧兜底 [本端]");
    }
}
