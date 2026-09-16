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

}