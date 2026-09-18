using FluentAssertions;
using Xunit;
using TC.Tier.Core.Net.Raft;
using TC.Tier.Core.Net.Transport.InProcess;
using TC.Tier.Core.Net.Tests.Fixtures;

namespace TC.Tier.Core.Net.Tests.Raft;

/// <summary>
/// 多成员原子变更（二期-F3——DDR-F3 验收，编排式变更链）：
/// ① 3→5→4 一条 API 完成增删混合变更——终态配置 = 目标集、新成员追平数据；
/// ② 重调幂等——目标已达成零副作用（无新配置条目）；③ 链步进可见（每步 applied）。
/// </summary>
public class MembershipChangeTests
{
    private sealed class RecordingStateMachine : IStateMachine
    {
        public readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte[]> Applied = new();
        public ValueTask ApplyAsync(long index, ReadOnlyMemory<byte> command, CancellationToken ct = default)
        {
            Applied[index] = command.ToArray();
            return ValueTask.CompletedTask;
        }
    }

    private sealed record TestNode(NodeId Id, RaftStateMachine Raft, RecordingStateMachine Machine, Membership Membership, IAsyncDisposable[] Owned);

    private sealed record Rig(InProcessTransportHub Hub, List<TestNode> Nodes, List<IAsyncDisposable> Owned) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            foreach (var d in Owned)
            {
                try { await d.DisposeAsync(); } catch { /* 尽力清理 */ }
            }
            await Hub.DisposeAsync();
        }
    }

    private static async Task<TestNode> CreateNodeAsync(InProcessTransportHub hub, NodeId id, ClusterConfig config, int seed)
    {
        var store = new InMemoryRaftStore();
        await store.InitializeAsync();
        var transport = hub.Register(id);
        transport.Start();
        var machine = new RecordingStateMachine();
        var pipeline = new ApplyPipeline(store, machine, ApplyPipelineOptions.Default);
        var options = RaftOptions.Default
            .WithElectionTimeout(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(800))
            .WithRandom(new Random(seed));
        var raft = new RaftStateMachine(id, store, transport, pipeline, options);
        pipeline.SetConfigCallback(raft.PostConfigChanged);
        await pipeline.StartAsync();
        await raft.StartAsync(config);
        return new TestNode(id, raft, machine, new Membership(raft), [raft, pipeline, transport]);
    }

    private static async Task<(Rig Rig, TestNode[] Nodes)> CreateClusterAsync(int count)
    {
        var hub = new InProcessTransportHub();
        var owned = new List<IAsyncDisposable>();
        var nodes = new List<TestNode>();
        try
        {
            var ids = Enumerable.Range(0, count).Select(_ => NodeId.NewRandom()).ToArray();
            var initial = new ClusterConfig(ids.Select(id => new ClusterMember(id, "")).ToArray());
            for (var i = 0; i < count; i++)
            {
                var n = await CreateNodeAsync(hub, ids[i], initial, 42 + i);
                nodes.Add(n);
                owned.AddRange(n.Owned);
            }
            return (new Rig(hub, nodes, owned), nodes.ToArray());
        }
        catch
        {
            foreach (var d in owned) { try { await d.DisposeAsync(); } catch { } }
            await hub.DisposeAsync();
            throw;
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("条件未满足（超时）。");
            await Task.Delay(50);
        }
    }

    private static async Task<TestNode> WaitLeaderAsync(Rig rig)
    {
        await WaitForAsync(() => rig.Nodes.Any(n => n.Raft.IsLeader), TimeSpan.FromSeconds(15));
        return rig.Nodes.First(n => n.Raft.IsLeader);
    }

    private static bool ConfigEquals(ClusterConfig config, IEnumerable<(NodeId Id, string Ep)> target)
        => config.Count == target.Count()
           && target.All(t => config.Contains(t.Id));

    /// <summary>验收①：3→5→4 一条 API——增删混合、新成员追平、终态 = 目标。</summary>
    [Fact]
    public async Task ChangeMembers_GrowThenShrink_ReachesTargetAtomically()
    {
        var (rig, nodes) = await CreateClusterAsync(3);
        await using var _ = rig;
        await WaitLeaderAsync(rig);
        var leader = rig.Nodes.First(n => n.Raft.IsLeader);

        // 先写一条业务数据（变更后新成员必须追平）
        var dataIndex = await leader.Raft.ReplicateAsync(new byte[] { 0xAB }).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        // 3→5：一次调用加 2 个活跃节点
        var joiners = Enumerable.Range(0, 2).Select(_ => NodeId.NewRandom()).ToArray();
        for (var i = 0; i < joiners.Length; i++)
        {
            var n = await CreateNodeAsync(rig.Hub, joiners[i],
                new ClusterConfig(new[] { new ClusterMember(leader.Id, "") }), 50 + i);
            n.Owned.ToList().ForEach(o => rig.Owned.Add(o));
            rig.Nodes.Add(n);
        }
        var target5 = rig.Nodes.Select(n => (n.Id, "")).ToArray();
        await leader.Membership.ChangeMembersAsync(target5).WaitAsync(TimeSpan.FromSeconds(30));
        // 收敛窗与变更预算对齐（编排 30s）——多节点 apply 链在 runner 负载下有秒级突发延迟
        await WaitForAsync(() => rig.Nodes.All(n => ConfigEquals(n.Raft.Config, target5)), TimeSpan.FromSeconds(30));
        rig.Nodes.Select(n => n.Raft.Config.VoterCount).Should().OnlyContain(v => v == 5, "终态 5 voter");

        // 新成员追平变更前数据
        await WaitForAsync(() => rig.Nodes.Where(n => joiners.Contains(n.Id))
            .All(n => n.Machine.Applied.ContainsKey(dataIndex)), TimeSpan.FromSeconds(30));

        // 5→4：同一条 API 删 1（被移除者必须是 joiner——原 3 活跃节点保持多数派）
        var victim = rig.Nodes.First(n => joiners.Contains(n.Id));
        var target4 = rig.Nodes.Where(n => n.Id != victim.Id).Select(n => (n.Id, "")).ToArray();
        await leader.Membership.ChangeMembersAsync(target4).WaitAsync(TimeSpan.FromSeconds(30));
        await WaitForAsync(() => rig.Nodes.Where(n => n.Id != victim.Id)
            .All(n => ConfigEquals(n.Raft.Config, target4)), TimeSpan.FromSeconds(30));
        rig.Nodes.Where(n => n.Id != victim.Id).Select(n => n.Raft.Config.VoterCount)
            .Should().OnlyContain(v => v == 4, "终态 4 voter");
    }

    /// <summary>验收②：目标已达成 = 零提案（幂等短路，无新配置条目）。</summary>
    [Fact]
    public async Task ChangeMembers_TargetReached_NoOpShortCircuits()
    {
        var (r, nodes) = await CreateClusterAsync(3);
        await using var rig = r;
        var leader = await WaitLeaderAsync(rig);

        var appliedCount = leader.Machine.Applied.Count;
        await leader.Membership.ChangeMembersAsync(nodes.Select(n => (n.Id, "")))
            .WaitAsync(TimeSpan.FromSeconds(10));

        leader.Raft.Config.Count.Should().Be(3, "配置不变");
        leader.Machine.Applied.Count.Should().Be(appliedCount, "目标已达成零提案（幂等短路）");
    }
}
