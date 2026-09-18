using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Xunit;
using TC.Tier.Core.IO;
using TC.Tier.Core.Net;
using TC.Tier.Core.Net.Raft;
using TC.Tier.Core.Net.Transport.InProcess;
using TC.Tier.Products.Net.Node;
using TC.Tier.Products.Net.Host;
using TC.Tier.Products.Net.Admin;
using TC.Tier.Products.Net.RaftStore;

namespace TC.Tier.Products.Tests.Net;

/// <summary>
/// TierRaftNodeBuilder 装配门（D6 收口——传输二选一形态）：①嵌入式 InProcess 注入 /
/// ②内建 TCP 组装（真部署形态——传输生命周期随节点）/ 双供给·零供给 fail-fast。
/// </summary>
public class TierRaftNodeBuilderTests
{
    private sealed class CountingMachine : IStateMachine
    {
        public long Applied;
        public ValueTask ApplyAsync(long index, ReadOnlyMemory<byte> command, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Applied);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>测试加速（raft 选举窗放宽——WAL 提交冷路径期间不被心跳超时打掉）。</summary>
    private static TierRaftNodeOptions FastOptions() => TierRaftNodeOptions.Default
        .WithWal(TierWalOptions.Default.WithCommitInterval(TimeSpan.FromMilliseconds(-1)))
        .WithRaft(RaftOptions.Default.WithElectionTimeout(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)));

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("条件未满足（超时）。");
            await Task.Delay(50);
        }
    }

    /// <summary>预留 loopback 端口（bind :0 取实际 → 释放——TCP 组装测试用）。</summary>
    private static int ReservePort()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        socket.Close();
        return port;
    }

    [Fact]
    public async Task InProcessForm_SingleNode_ElectsItselfLeader()
    {
        await using var hub = new InProcessTransportHub();
        var id = NodeId.NewRandom();
        var config = new ClusterConfig([new ClusterMember(id, "")]);
        var machine = new CountingMachine();

        var node = await TierRaftNodeBuilder.Create(id, TierFs.New("memory:"), config, machine)
            .WithTransport(hub.Register(id))          // 形态①——嵌入式注入
            .WithOptions(FastOptions())
            .StartAsync();
        try
        {
            await WaitForAsync(() => node.Raft.IsLeader);
            node.Raft.LeaderId.Should().Be(id, "单节点集群——自举为 leader");
        }
        finally
        {
            await node.DisposeAsync();
        }
    }

    [Fact]
    public async Task ClusterTransportForm_TwoNodes_RealTcp_ElectsLeader()
    {
        var idA = NodeId.NewRandom();
        var idB = NodeId.NewRandom();
        var portA = ReservePort();
        var portB = ReservePort();
        var config = new ClusterConfig([new ClusterMember(idA, ""), new ClusterMember(idB, "")]);
        var machine = new CountingMachine();

        // 形态②——内建 TCP 组装（peers 表互指；传输生命周期随节点）
        var nodeA = await TierRaftNodeBuilder.Create(idA, TierFs.New("memory:"), config, machine)
            .WithClusterTransport(new IPEndPoint(IPAddress.Loopback, portA),
                new Dictionary<NodeId, IPEndPoint> { [idB] = new(IPAddress.Loopback, portB) })
            .WithOptions(FastOptions())
            .StartAsync();
        var nodeB = await TierRaftNodeBuilder.Create(idB, TierFs.New("memory:"), config, machine)
            .WithClusterTransport(new IPEndPoint(IPAddress.Loopback, portB),
                new Dictionary<NodeId, IPEndPoint> { [idA] = new(IPAddress.Loopback, portA) })
            .WithOptions(FastOptions())
            .StartAsync();
        try
        {
            await WaitForAsync(() => nodeA.Raft.IsLeader || nodeB.Raft.IsLeader, TimeSpan.FromSeconds(15));
            (nodeA.Raft.LeaderId ?? nodeB.Raft.LeaderId).Should().NotBeNull("双节点集群选出 leader");
        }
        finally
        {
            await nodeA.DisposeAsync();
            await nodeB.DisposeAsync();
        }
    }

    [Fact]
    public async Task DoubleSupply_FailsFast()
    {
        await using var hub = new InProcessTransportHub();
        var id = NodeId.NewRandom();
        var config = new ClusterConfig([new ClusterMember(id, "")]);
        var act = async () => await TierRaftNodeBuilder.Create(id, TierFs.New("memory:"), config, new CountingMachine())
            .WithTransport(hub.Register(id))
            .WithClusterTransport(null, new Dictionary<NodeId, IPEndPoint>())
            .StartAsync();
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*二选一*");
    }

    [Fact]
    public async Task ZeroSupply_FailsFast_WithGuidance()
    {
        var id = NodeId.NewRandom();
        var config = new ClusterConfig([new ClusterMember(id, "")]);
        var act = async () => await TierRaftNodeBuilder.Create(id, TierFs.New("memory:"), config, new CountingMachine())
            .StartAsync();
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*WithTransport*WithClusterTransport*");
    }

    [Fact]
    public async Task WithJoin_StandbyBootstrap_PromotesToVoter()    {
        // 两节点集群（builder 形态①注入）+ 加入方（形态① + WithJoin）——learner 入组追平晋级
        await using var hub = new InProcessTransportHub();
        var idA = NodeId.NewRandom();
        var idB = NodeId.NewRandom();
        var idC = NodeId.NewRandom();
        var options = TierRaftNodeOptions.Default
            .WithWal(TierWalOptions.Default.WithCommitInterval(TimeSpan.FromMilliseconds(-1)))
            .WithRaft(RaftOptions.Default.WithElectionTimeout(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)));

        var nodeA = await TierRaftNodeBuilder.Create(idA, TierFs.New("memory:"),
                new ClusterConfig([new ClusterMember(idA, ""), new ClusterMember(idB, "")]), new CountingMachine())
            .WithTransport(hub.Register(idA)).WithOptions(options).StartAsync();
        var nodeB = await TierRaftNodeBuilder.Create(idB, TierFs.New("memory:"),
                new ClusterConfig([new ClusterMember(idA, ""), new ClusterMember(idB, "")]), new CountingMachine())
            .WithTransport(hub.Register(idB)).WithOptions(options).StartAsync();
        try
        {
            await WaitForAsync(() => nodeA.Raft.IsLeader || nodeB.Raft.IsLeader);

            var joinerConfig = new ClusterConfig([new ClusterMember(idC, "")]);
            var nodeC = await TierRaftNodeBuilder.Create(idC, TierFs.New("memory:"), joinerConfig, new CountingMachine())
                .WithTransport(hub.Register(idC)).WithOptions(options)
                .WithJoin(idA, idB)
                .StartAsync();
            try
            {
                await WaitForAsync(() => nodeC.Raft.IsVoter, TimeSpan.FromSeconds(10));
                nodeC.Raft.IsVoter.Should().BeTrue("Standby 引导完成——learner 追平晋级 voter");
                nodeA.Raft.Config.IsVoter(idC).Should().BeTrue("leader 侧活动配置已切换");
            }
            finally
            {
                await nodeC.DisposeAsync();
            }
        }
        finally
        {
            await nodeA.DisposeAsync();
            await nodeB.DisposeAsync();
        }
    }

    /// <summary>两 voter 真 TCP 集群（内建组装形态）——join 系测试基座；返回已选主的节点表。</summary>
    private static async Task<(TierRaftNode A, TierRaftNode B, NodeId IdA, NodeId IdB, Dictionary<NodeId, IPEndPoint> Peers)>
        StartTwoVoterTcpClusterAsync(TierRaftNodeOptions options)
    {
        var idA = NodeId.NewRandom();
        var idB = NodeId.NewRandom();
        var portA = ReservePort();
        var portB = ReservePort();
        var config = new ClusterConfig([new ClusterMember(idA, ""), new ClusterMember(idB, "")]);
        var peersA = new Dictionary<NodeId, IPEndPoint> { [idB] = new(IPAddress.Loopback, portB) };
        var peersB = new Dictionary<NodeId, IPEndPoint> { [idA] = new(IPAddress.Loopback, portA) };

        var nodeA = await TierRaftNodeBuilder.Create(idA, TierFs.New("memory:"), config, new CountingMachine())
            .WithClusterTransport(new IPEndPoint(IPAddress.Loopback, portA), peersA)
            .WithOptions(options).StartAsync();
        var nodeB = await TierRaftNodeBuilder.Create(idB, TierFs.New("memory:"), config, new CountingMachine())
            .WithClusterTransport(new IPEndPoint(IPAddress.Loopback, portB), peersB)
            .WithOptions(options).StartAsync();
        await WaitForAsync(() => nodeA.Raft.IsLeader || nodeB.Raft.IsLeader, TimeSpan.FromSeconds(15));
        return (nodeA, nodeB, idA, idB, new Dictionary<NodeId, IPEndPoint>
        {
            [idA] = new(IPAddress.Loopback, portA),
            [idB] = new(IPAddress.Loopback, portB),
        });
    }

    [Fact]
    public async Task WithJoin_EndpointDial_LearnerRealTcp_JoinsAndReplicates()
    {
        // #480 验收：内建 TCP 组装（NodeEndpoint）下端点拨号引导——介质判定放开前恒抛 NotSupportedException
        var options = FastOptions();
        var (nodeA, nodeB, idA, idB, peers) = await StartTwoVoterTcpClusterAsync(options);
        try
        {
            var idC = NodeId.NewRandom();
            var nodeC = await TierRaftNodeBuilder.Create(idC, TierFs.New("memory:"),
                    new ClusterConfig([new ClusterMember(idC, "")]), new CountingMachine())
                .WithClusterTransport(new IPEndPoint(IPAddress.Loopback, ReservePort()),
                    new Dictionary<NodeId, IPEndPoint>())   // 空拨号表——bootstrap 靠地址制直连
                .WithOptions(options)
                .WithJoin(peers[idA], peers[idB])
                .StartAsync();
            try
            {
                await WaitForAsync(() => nodeC.Raft.IsVoter, TimeSpan.FromSeconds(15));
                nodeC.Raft.IsVoter.Should().BeTrue("端点拨号引导完成——learner 追平晋级 voter");
                var leader = nodeA.Raft.IsLeader ? nodeA : nodeB;
                leader.Raft.Config.IsVoter(idC).Should().BeTrue("leader 侧活动配置已晋级");
            }
            finally
            {
                await nodeC.DisposeAsync();
            }
        }
        finally
        {
            await nodeA.DisposeAsync();
            await nodeB.DisposeAsync();
        }
    }

    [Fact]
    public async Task WithJoinByNodeIds_MemberTableDial_LearnerRealTcp_Joins()
    {
        // #480 诉求 2 验收：成员表拨号形态（NodeId 档）——拨号表含成员地址 + JoinReq 通告本端
        // 监听地址，leader 受理后动态注册拨号表回连复制（通告端点缺失时此链路必超时）
        var options = FastOptions();
        var (nodeA, nodeB, idA, idB, peers) = await StartTwoVoterTcpClusterAsync(options);
        try
        {
            var idC = NodeId.NewRandom();
            var nodeC = await TierRaftNodeBuilder.Create(idC, TierFs.New("memory:"),
                    new ClusterConfig([new ClusterMember(idC, "")]), new CountingMachine())
                .WithClusterTransport(new IPEndPoint(IPAddress.Loopback, ReservePort()), peers)
                .WithOptions(options)
                .WithJoinAsLearner(idA, idB)
                .StartAsync();
            try
            {
                await WaitForAsync(() => nodeC.Raft.Config.Contains(idC) && nodeC.Raft.Config.Count > 1,
                    TimeSpan.FromSeconds(15));
                nodeC.Raft.IsVoter.Should().BeFalse("learner 永久只读档");
                var leader = nodeA.Raft.IsLeader ? nodeA : nodeB;
                leader.Raft.Config.Contains(idC).Should().BeTrue("leader 侧活动配置已登记 learner");
                leader.Raft.Config.GetEndPoint(idC).Should().NotBeEmpty("通告端点随配置条目登记（回连复制依据）");
            }
            finally
            {
                await nodeC.DisposeAsync();
            }
        }
        finally
        {
            await nodeA.DisposeAsync();
            await nodeB.DisposeAsync();
        }
    }

    [Fact]
    public async Task WithJoinAsWitness_EndpointDial_RealTcp_JoinsAndWitnesses()
    {
        // #481 验收：witness 装配/引导公开面——builder WithJoinAsWitness + StartWitnessAsync
        // （真 TCP 端点拨号），投票计多数派、断言流推进、无日志体
        var options = FastOptions();
        var (nodeA, nodeB, idA, idB, peers) = await StartTwoVoterTcpClusterAsync(options);
        try
        {
            var idW = NodeId.NewRandom();
            var witness = await TierRaftNodeBuilder.Create(idW, TierFs.New("memory:"),
                    new ClusterConfig([new ClusterMember(idW, "")]), new CountingMachine())
                .WithClusterTransport(new IPEndPoint(IPAddress.Loopback, ReservePort()),
                    new Dictionary<NodeId, IPEndPoint>())
                .WithOptions(options)
                .WithJoinAsWitness(peers[idA], peers[idB])
                .StartWitnessAsync();
            try
            {
                var leader = nodeA.Raft.IsLeader ? nodeA : nodeB;
                leader.Raft.Config.IsWitness(idW).Should().BeTrue("leader 侧活动配置已登记 witness 角色");
                leader.Raft.Config.VoterCount.Should().Be(3, "witness 计入选主/提交多数派");
                witness.Raft.IsLeader.Should().BeFalse("witness 永不自荐");

                // 断言流抵达：写数据 → witness 高水位推进（内容丢弃）
                var index = await leader.Raft.ReplicateAsync(new byte[] { 0xEE });
                await WaitForAsync(() => witness.Store.LastLogIndex >= index, TimeSpan.FromSeconds(15));

                // 投票权实证：leader 宕机 → 幸存 voter + witness（2/3）重选举并继续提交
                var survivor = leader == nodeA ? nodeB : nodeA;
                await leader.DisposeAsync();
                await WaitForAsync(() => survivor.Raft.IsLeader, TimeSpan.FromSeconds(15));
                var seq = await survivor.Raft.ReplicateAsync(new byte[] { 0x12 });
                seq.Should().BeGreaterThan(0, "witness 在多数派域——单 voter 拓扑继续提交");
            }
            finally
            {
                await witness.DisposeAsync();
            }
        }
        finally
        {
            await nodeA.DisposeAsync();
            await nodeB.DisposeAsync();
        }
    }

    [Fact]
    public async Task StartAsync_WithJoinAsWitness_FailsFast_WithGuidance()
    {
        await using var hub = new InProcessTransportHub();
        var id = NodeId.NewRandom();
        var config = new ClusterConfig([new ClusterMember(id, "")]);
        var act = async () => await TierRaftNodeBuilder.Create(id, TierFs.New("memory:"), config, new CountingMachine())
            .WithTransport(hub.Register(id))
            .WithJoinAsWitness(NodeId.NewRandom())
            .StartAsync();
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*StartWitnessAsync*");
    }

}