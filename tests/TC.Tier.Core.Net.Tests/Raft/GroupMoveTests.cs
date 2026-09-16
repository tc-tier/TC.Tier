using FluentAssertions;
using Xunit;
using TC.Tier.Core.Net.Raft;
using TC.Tier.Core.Net.Transport.InProcess;
using TC.Tier.Core.Net.Tests.Fixtures;

namespace TC.Tier.Core.Net.Tests.Raft;

/// <summary>
/// 组搬迁编排（二期-F5——DDR-F5 验收「再平衡测试」）：
/// ① 搬迁 A→B：B 入组追平、A 出组、终态配置正确、集群持续可写；
/// ② 幂等续走：重调已完成搬迁 = 无重复副作用。
/// </summary>
public class GroupMoveTests
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

    /// <summary>验收①：搬迁 B→D（第 4 节点入组、B 出组）——数据追平 + 集群持续可写。</summary>
    [Fact]
    public async Task Move_MemberToNewNode_DataCaughtUp_ClusterWritable()
    {
        var (rig, nodes) = await CreateClusterAsync(3);
        await using var _ = rig;
        await WaitForAsync(() => nodes.Any(n => n.Raft.IsLeader), TimeSpan.FromSeconds(15));
        var leader = nodes.First(n => n.Raft.IsLeader);
        var moving = nodes.First(n => n.Id != leader.Id);   // 搬迁一个 follower

        var dataIndex = await leader.Raft.ReplicateAsync(new byte[] { 0xCD }).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        // 目标节点 D（进程已起、配置未入组——搬迁把 D 拉进组、B 移出）
        var d = await CreateNodeAsync(rig.Hub, NodeId.NewRandom(),
            new ClusterConfig(new[] { new ClusterMember(leader.Id, "") }), 77);
        d.Owned.ToList().ForEach(o => rig.Owned.Add(o));
        rig.Nodes.Add(d);

        var orchestrator = new GroupMoveOrchestrator(leader.Raft, leader.Membership);
        await orchestrator.MoveAsync(d.Id, moving.Id, ct: CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(30));

        // 终态：leader 视角配置 = 原 3 - moving + d（★ 有界收敛等待——配置条目 apply 经管道异步
        //   回写 Config 视图，晚于编排返回一拍；2vCPU runner 上即时断言偶发红 ×2 达台账线）
        await WaitForAsync(() => !leader.Raft.Config.Contains(moving.Id)
            && leader.Raft.Config.Contains(d.Id)
            && leader.Raft.Config.VoterCount == 3, TimeSpan.FromSeconds(10));
        leader.Raft.Config.Contains(moving.Id).Should().BeFalse("源成员出组");
        leader.Raft.Config.Contains(d.Id).Should().BeTrue("目标成员入组");
        leader.Raft.Config.VoterCount.Should().Be(3);
        // 数据追平：D 收到搬迁前数据
        await WaitForAsync(() => d.Machine.Applied.ContainsKey(dataIndex), TimeSpan.FromSeconds(20));
        // 集群持续可写
        var next = await leader.Raft.ReplicateAsync(new byte[] { 0xEF }).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        next.Should().BeGreaterThan(dataIndex, "搬迁后集群持续可写");
    }

    /// <summary>验收②：重调幂等——搬迁完成后重调无重复副作用。</summary>
    [Fact]
    public async Task Move_RerunAfterComplete_Idempotent()
    {
        var (rig, nodes) = await CreateClusterAsync(3);
        await using var _ = rig;
        await WaitForAsync(() => nodes.Any(n => n.Raft.IsLeader), TimeSpan.FromSeconds(15));
        var leader = nodes.First(n => n.Raft.IsLeader);
        var moving = nodes.First(n => n.Id != leader.Id);

        var d = await CreateNodeAsync(rig.Hub, NodeId.NewRandom(),
            new ClusterConfig(new[] { new ClusterMember(leader.Id, "") }), 78);
        d.Owned.ToList().ForEach(o => rig.Owned.Add(o));
        rig.Nodes.Add(d);

        var orchestrator = new GroupMoveOrchestrator(leader.Raft, leader.Membership);
        await orchestrator.MoveAsync(d.Id, moving.Id, ct: CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        var appliedAfterMove = leader.Machine.Applied.Count;

        // 重调：D 已入组、B 已出组——全部步骤幂等跳过
        await orchestrator.MoveAsync(d.Id, moving.Id, ct: CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        leader.Raft.Config.Contains(moving.Id).Should().BeFalse("终态稳定");
        leader.Raft.Config.VoterCount.Should().Be(3, "配置不变（无重复变更）");
        leader.Machine.Applied.Count.Should().Be(appliedAfterMove, "零新增配置条目（幂等短路）");
    }
}
