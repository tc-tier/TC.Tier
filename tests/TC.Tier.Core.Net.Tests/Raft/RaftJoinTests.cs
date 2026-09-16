using FluentAssertions;
using Xunit;
using TC.Tier.Core.Net.Raft;
using TC.Tier.Core.Net.Transport.InProcess;
using TC.Tier.Core.Net.Tests.Fixtures;

namespace TC.Tier.Core.Net.Tests.Raft;

/// <summary>
/// Standby 引导流（JoinReq learner 入组 + 追平自动晋级 + 观察副本保持 learner）：
/// 加入方 IsVoter 翻真 / 复制抵达加入方 / 非投票观察副本不计多数派。
/// </summary>
public class RaftJoinTests
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
            .WithElectionTimeout(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(600))
            .WithRandom(new Random(seed));
        var raft = new RaftStateMachine(id, store, transport, pipeline, options);
        pipeline.SetConfigCallback(raft.PostConfigChanged);
        await pipeline.StartAsync();
        await raft.StartAsync(config);
        return new TestNode(id, raft, machine, new Membership(raft), [raft, pipeline, transport]);
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("条件未满足（超时）。");
            await Task.Delay(30);
        }
    }

    [Fact]
    public async Task Join_LearnerAnnounce_AutoPromotes_Replicates()
    {
        await using var fx = new Rig(new InProcessTransportHub(), [], []);
        var idA = NodeId.NewRandom();
        var idB = NodeId.NewRandom();
        var voterConfig = new ClusterConfig([new ClusterMember(idA, ""), new ClusterMember(idB, "")]);
        var a = await CreateNodeAsync(fx.Hub, idA, voterConfig, seed: 1);
        var b = await CreateNodeAsync(fx.Hub, idB, voterConfig, seed: 2);
        fx.Nodes.AddRange([a, b]);
        fx.Owned.AddRange([.. a.Owned, .. b.Owned]);
        await WaitForAsync(() => a.Raft.IsLeader || b.Raft.IsLeader);
        var leader = a.Raft.IsLeader ? a : b;

        // 加入方：learner 自配置启动 + 引导宣告（轮转递 JoinReq）→ 追平晋级 voter
        var idC = NodeId.NewRandom();
        var c = await CreateNodeAsync(fx.Hub, idC,
            new ClusterConfig([new ClusterMember(idC, "", ClusterMemberRole.Learner)]), seed: 3);
        fx.Nodes.Add(c);
        fx.Owned.AddRange(c.Owned);

        await c.Raft.JoinAsync([idA, idB], TimeSpan.FromSeconds(10));

        c.Raft.IsVoter.Should().BeTrue("追平晋级完成——加入方就绪信号");
        leader.Raft.Config.IsVoter(idC).Should().BeTrue("leader 侧活动配置已晋级");

        // 复制抵达加入方（晋级后的日志复制计入多数派域）
        var index = await leader.Raft.ReplicateAsync(new byte[] { 0xAB });
        await WaitForAsync(() => c.Machine.Applied.ContainsKey(index));
        c.Machine.Applied[index].Should().Equal(new byte[] { 0xAB });
    }

    [Fact]
    public async Task Join_AsLearner_StaysLearner_NeverPromoted_Replicates()
    {
        await using var fx = new Rig(new InProcessTransportHub(), [], []);
        var idA = NodeId.NewRandom();
        var idB = NodeId.NewRandom();
        var voterConfig = new ClusterConfig([new ClusterMember(idA, ""), new ClusterMember(idB, "")]);
        var a = await CreateNodeAsync(fx.Hub, idA, voterConfig, seed: 1);
        var b = await CreateNodeAsync(fx.Hub, idB, voterConfig, seed: 2);
        fx.Nodes.AddRange([a, b]);
        fx.Owned.AddRange([.. a.Owned, .. b.Owned]);
        await WaitForAsync(() => a.Raft.IsLeader || b.Raft.IsLeader);
        var leader = a.Raft.IsLeader ? a : b;

        // DP 形态（#441）：learner 永久只读引导——入组完成 = 配置收敛含自身，永不晋级
        var idDp = NodeId.NewRandom();
        var dp = await CreateNodeAsync(fx.Hub, idDp,
            new ClusterConfig([new ClusterMember(idDp, "", ClusterMemberRole.Learner)]), seed: 3);
        fx.Nodes.Add(dp);
        fx.Owned.AddRange(dp.Owned);

        await dp.Raft.JoinAsync([idA, idB], TimeSpan.FromSeconds(10), autoPromote: false);

        // 就绪信号：活动配置收敛含自身（learner 角色）
        dp.Raft.Config.Contains(idDp).Should().BeTrue("加入完成信号——活动配置收敛含自身");
        dp.Raft.IsVoter.Should().BeFalse("learner 永久只读——不晋级 voter");
        leader.Raft.Config.Contains(idDp).Should().BeTrue("leader 侧活动配置已登记 learner");

        // 追平后持续保持 learner（追平晋级判定不触发——AutoPromote 未登记）
        var index = await leader.Raft.ReplicateAsync(new byte[] { 0xCD });
        await WaitForAsync(() => dp.Machine.Applied.ContainsKey(index), TimeSpan.FromSeconds(10));
        dp.Machine.Applied[index].Should().Equal(new byte[] { 0xCD }, "learner 日志/快照复制全路径");
        await Task.Delay(500);   // 越过追平晋级判定窗
        dp.Raft.IsVoter.Should().BeFalse("永不晋级——DP 扩容不影响选举面");
        leader.Raft.Config.IsVoter(idDp).Should().BeFalse();
    }

    [Fact]
    public async Task AddLearner_Observer_ReceivesReplication_StaysNonVoter()
    {
        await using var fx = new Rig(new InProcessTransportHub(), [], []);
        var idA = NodeId.NewRandom();
        var idB = NodeId.NewRandom();
        var voterConfig = new ClusterConfig([new ClusterMember(idA, ""), new ClusterMember(idB, "")]);
        var a = await CreateNodeAsync(fx.Hub, idA, voterConfig, seed: 1);
        var b = await CreateNodeAsync(fx.Hub, idB, voterConfig, seed: 2);
        fx.Nodes.AddRange([a, b]);
        fx.Owned.AddRange([.. a.Owned, .. b.Owned]);
        await WaitForAsync(() => a.Raft.IsLeader || b.Raft.IsLeader);
        var leader = a.Raft.IsLeader ? a : b;

        // 观察副本：以 learner 入组、无晋级登记（AutoPromote=false 的等价手工形态）
        var idD = NodeId.NewRandom();
        var d = await CreateNodeAsync(fx.Hub, idD,
            new ClusterConfig([new ClusterMember(idD, "", ClusterMemberRole.Learner)]), seed: 3);
        fx.Nodes.Add(d);
        fx.Owned.AddRange(d.Owned);
        await leader.Membership.AddLearnerAsync(idD);

        var index = await leader.Raft.ReplicateAsync(new byte[] { 0xCD });
        await WaitForAsync(() => d.Machine.Applied.ContainsKey(index));
        d.Raft.IsVoter.Should().BeFalse("观察副本保持 learner——不投票不计多数派");
        leader.Raft.Config.IsVoter(idD).Should().BeFalse();
    }

    [Fact]
    public async Task Join_ByEndpoints_OnInProcessTransport_FailsFast()
    {
        await using var fx = new Rig(new InProcessTransportHub(), [], []);
        var idC = NodeId.NewRandom();
        var c = await CreateNodeAsync(fx.Hub, idC,
            new ClusterConfig([new ClusterMember(idC, "", ClusterMemberRole.Learner)]), seed: 3);
        fx.Nodes.Add(c);
        fx.Owned.AddRange(c.Owned);

        var act = () => c.Raft.JoinAsync([new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 9400)]);
        await act.Should().ThrowAsync<NotSupportedException>("端点拨号需 ClusterTransport——进程内形态用同伴 ID 重载");
    }
}
