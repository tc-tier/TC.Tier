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

    private sealed record WitnessTestNode(NodeId Id, RaftStateMachine Raft, WitnessHighWaterStore Store, IAsyncDisposable[] Owned);

    private static async Task<WitnessTestNode> CreateWitnessNodeAsync(InProcessTransportHub hub, NodeId id, int seed)
    {
        var store = new WitnessHighWaterStore();
        await store.InitializeAsync();
        var transport = hub.Register(id);
        transport.Start();
        var options = RaftOptions.Default
            .WithElectionTimeout(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(600))
            .WithRandom(new Random(seed));
        var raft = new RaftStateMachine(id, store, transport, new WitnessApplySink(), options);
        await raft.StartAsync(new ClusterConfig([new ClusterMember(id, "", ClusterMemberRole.Witness)]));
        return new WitnessTestNode(id, raft, store, [raft, transport]);
    }

    /// <summary>witness 空应用槽（witness 分支不推进 commit——Submit 永不触发）。</summary>
    private sealed class WitnessApplySink : IApplySink
    {
        public void Submit(long commitIndex) { }
        public event Action<long>? AppliedTo { add { } remove { } }
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

    // ═══ #507c 批量成员变更（learner 无票——一条配置条目携多节点，quorum 交集平凡成立）═══

    [Fact]
    public void AddLearners_BatchConfig_SerializationRoundtrip_AndWireCap()
    {
        var idA = NodeId.NewRandom();
        var idB = NodeId.NewRandom();
        var config = new ClusterConfig([new ClusterMember(idA, ""), new ClusterMember(idB, "")]);

        var learners = Enumerable.Range(0, 150).Select(i => (NodeId.NewRandom(), $"ep-{i}"));
        var batched = config.AddLearners(learners);
        batched.Count.Should().Be(152, "批量 learner 全部入配置");
        batched.VoterCount.Should().Be(2, "learner 不改变投票成员集——quorum 交集论证平凡成立");
        batched.MajorityThreshold.Should().Be(config.MajorityThreshold, "多数派阈值不变");

        // wire 往返（配置条目 payload——150 成员远超单成员形态）
        var restored = ClusterConfig.Deserialize(batched.Serialize());
        restored.Count.Should().Be(152);
        restored.VoterCount.Should().Be(2);
        restored.Members.Count(m => m.Role == ClusterMemberRole.Learner).Should().Be(150);

        // 幂等：已存在全跳过 = 原样返回（零提案前提）
        config.AddLearners([(idA, "")]).Should().BeSameAs(config, "voter 不被批量 learner 误改/已存在跳过");

        // wire 上限：单条 >200 拒绝（配置条目 Count 1B ≤255 留余量）
        var over = Enumerable.Range(0, 201).Select(i => (NodeId.NewRandom(), ""));
        var act = () => config.AddLearners(over);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task AddLearners_BatchBootstrap_OneRound_AllLearnersReceive()
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

        // 批量引导：3 个 learner 节点对象 + 一次 AddLearnersAsync（O(1) 轮协议——非 3 轮串行）
        var learners = new List<(NodeId Id, TestNode Node)>();
        for (var i = 0; i < 3; i++)
        {
            var id = NodeId.NewRandom();
            var n = await CreateNodeAsync(fx.Hub, id,
                new ClusterConfig([new ClusterMember(id, "", ClusterMemberRole.Learner)]), seed: 10 + i);
            fx.Nodes.Add(n);
            fx.Owned.AddRange(n.Owned);
            learners.Add((id, n));
        }

        await leader.Membership.AddLearnersAsync(learners.Select(l => (l.Id, "")));

        // 单轮生效：一次调用后 leader 配置同时含全部 learner（同一条配置条目 apply 产物）
        leader.Raft.Config.Count.Should().Be(5, "2 voter + 3 learner");
        leader.Raft.Config.VoterCount.Should().Be(2, "批量加入不改变投票集——多数派阈值不变");
        foreach (var (id, node) in learners)
        {
            leader.Raft.Config.IsVoter(id).Should().BeFalse("learner 永久只读");
            node.Raft.Config.Contains(id).Should().BeTrue("learner 侧活动配置已收敛（同条目 apply）");
        }

        // 稳态复制：后续条目全 learner 可达
        var index = await leader.Raft.ReplicateAsync(new byte[] { 0x50, 0x51 });
        foreach (var (_, node) in learners)
        {
            await WaitForAsync(() => node.Machine.Applied.ContainsKey(index), TimeSpan.FromSeconds(10));
            node.Machine.Applied[index].Should().Equal(new byte[] { 0x50, 0x51 }, "批量 learner 全路径复制");
        }
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
        await act.Should().ThrowAsync<NotSupportedException>("端点拨号需 TCP 介质——进程内形态用同伴 ID 重载");
    }

    [Fact]
    public async Task Join_AsWitness_VotesInQuorum_NeverPromotes_NoLogBody()
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
        var follower = leader == a ? b : a;

        // witness 档引导：[self witness] 本地配置 + 高水位存储——入组完成 = leader 受理
        var idW = NodeId.NewRandom();
        var w = await CreateWitnessNodeAsync(fx.Hub, idW, seed: 3);
        fx.Owned.AddRange(w.Owned);

        await w.Raft.JoinAsync([idA, idB], TimeSpan.FromSeconds(10), autoPromote: false, asWitness: true);

        leader.Raft.Config.IsWitness(idW).Should().BeTrue("leader 侧活动配置已登记 witness 角色");
        leader.Raft.Config.VoterCount.Should().Be(3, "witness 计入选主/提交多数派");
        leader.Raft.Config.IsFullVoter(idW).Should().BeFalse("witness 不自荐/不可承接 leader");
        w.Raft.Config.IsWitness(idW).Should().BeTrue("本地引导配置 witness 门——内容不落盘的依据");
        w.Store.Should().BeOfType<WitnessHighWaterStore>("witness 高水位存储（无日志体）");

        // 断言流抵达：写数据后 witness 高水位推进（内容丢弃、位置推进）
        var index = await leader.Raft.ReplicateAsync(new byte[] { 0xEF });
        await WaitForAsync(() => w.Store.LastLogIndex >= index, TimeSpan.FromSeconds(10));
        var readEntry = () => w.Store.TryGetEntry(index, out _, out _, out _);
        readEntry.Should().Throw<NotSupportedException>("witness 无日志体可读");

        // 投票权实证：leader 宕机 → 幸存 voter + witness（2/3 多数派）仍能选出新 leader 并继续提交
        await leader.Raft.DisposeAsync();
        var survivor = follower;
        await WaitForAsync(() => survivor.Raft.IsLeader, TimeSpan.FromSeconds(15));
        var seq = await survivor.Raft.ReplicateAsync(new byte[] { 0x11 });
        seq.Should().BeGreaterThan(0, "witness 在多数派域——单 voter 拓扑继续提交");
    }

    [Fact]
    public async Task Join_AsWitness_WithAutoPromote_FailsFast()
    {
        await using var fx = new Rig(new InProcessTransportHub(), [], []);
        var idC = NodeId.NewRandom();
        var c = await CreateNodeAsync(fx.Hub, idC,
            new ClusterConfig([new ClusterMember(idC, "", ClusterMemberRole.Learner)]), seed: 3);
        fx.Nodes.Add(c);
        fx.Owned.AddRange(c.Owned);

        var act = () => c.Raft.JoinAsync([NodeId.NewRandom()], asWitness: true);
        await act.Should().ThrowAsync<ArgumentException>("witness 永不晋级——与 autoPromote 组合非法");
    }

    [Fact]
    public async Task Join_Learner_AnnouncesEndPoint_LeaderRegisters()
    {
        await using var fx = new Rig(new InProcessTransportHub(), [], []);
        var idA = NodeId.NewRandom();
        var voterConfig = new ClusterConfig([new ClusterMember(idA, "")]);
        var a = await CreateNodeAsync(fx.Hub, idA, voterConfig, seed: 1);
        fx.Nodes.Add(a);
        fx.Owned.AddRange(a.Owned);
        await WaitForAsync(() => a.Raft.IsLeader);

        // 成员表拨号形态（#480）：NodeId 档 + 显式通告监听地址——leader 配置条目承载端点
        var idC = NodeId.NewRandom();
        var c = await CreateNodeAsync(fx.Hub, idC,
            new ClusterConfig([new ClusterMember(idC, "", ClusterMemberRole.Learner)]), seed: 2);
        fx.Nodes.Add(c);
        fx.Owned.AddRange(c.Owned);

        await c.Raft.JoinAsync([idA], TimeSpan.FromSeconds(10), autoPromote: false, listenEndPoint: "10.0.0.9:7001");

        a.Raft.Config.Contains(idC).Should().BeTrue();
        a.Raft.Config.GetEndPoint(idC).Should().Be("10.0.0.9:7001", "加入方通告端点随配置条目登记（leader 回连复制依据）");
    }

    [Fact]
    public async Task AddWitness_SingleOperation_RegistersWitnessRole()
    {
        await using var fx = new Rig(new InProcessTransportHub(), [], []);
        var idA = NodeId.NewRandom();
        var voterConfig = new ClusterConfig([new ClusterMember(idA, "")]);
        var a = await CreateNodeAsync(fx.Hub, idA, voterConfig, seed: 1);
        fx.Nodes.Add(a);
        fx.Owned.AddRange(a.Owned);
        await WaitForAsync(() => a.Raft.IsLeader);

        var idW = NodeId.NewRandom();
        await a.Membership.AddWitnessAsync(idW, "10.0.0.8:7002");

        a.Raft.Config.IsWitness(idW).Should().BeTrue("AddWitness 便捷面——witness 角色登记");
        a.Raft.Config.VoterCount.Should().Be(2, "witness 计入多数派");
        a.Raft.Config.GetEndPoint(idW).Should().Be("10.0.0.8:7002");
    }
}
