using FluentAssertions;
using TC.Tier.Core.Net.Coordination;
using TC.Tier.Core.Net.Raft;
using TC.Tier.Core.Net.Transport.InProcess;
using Xunit;

namespace TC.Tier.Core.Net.Tests.Coordination;

/// <summary>
/// 全局 ID 段分配（二期-G2 NETGAP-025——验证矩阵 G2 行）：
/// 段授予经 raft 全序——两客户端段区间不重叠严格递增；分发器本地发号（段耗尽自动补段）；
/// 分配尾跨"重启"（同存储重建）单调保持。
/// </summary>
public class GlobalIdServiceTests
{
    private const byte Domain = 0x75;   // 注册区（0x60-0xAF 使用方自管号——测试域）

    private sealed class Rig : IAsyncDisposable
    {
        public required InProcessTransportHub Hub { get; init; }
        public required InProcessNode RaftNodeTransport { get; init; }
        public required InProcessNode ClientTransport { get; init; }
        public required RaftStateMachine Raft { get; init; }
        public required IdSegmentStateMachine Machine { get; init; }
        public required Fixtures.InMemoryRaftStore Store { get; init; }
        public required GlobalIdService Service { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Raft.DisposeAsync();
            await RaftNodeTransport.DisposeAsync();
            await ClientTransport.DisposeAsync();
            await Hub.DisposeAsync();
        }
    }

    private static async Task<Rig> CreateRigAsync()
    {
        var hub = new InProcessTransportHub();
        var raftId = NodeId.NewRandom();
        var raftTransport = hub.Register(raftId);
        raftTransport.Start();
        var store = new Fixtures.InMemoryRaftStore();
        await store.InitializeAsync();
        var machine = new IdSegmentStateMachine();
        var pipeline = new ApplyPipeline(store, machine, ApplyPipelineOptions.Default);
        var options = RaftOptions.Default
            .WithElectionTimeout(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(800));
        var raft = new RaftStateMachine(raftId, store, raftTransport, pipeline, options);
        pipeline.SetConfigCallback(raft.PostConfigChanged);
        await pipeline.StartAsync();
        await raft.StartAsync(new ClusterConfig([new ClusterMember(raftId, "")]));
        var service = new GlobalIdService(raftTransport, Domain, raft, machine);

        var clientTransport = hub.Register(NodeId.NewRandom());
        clientTransport.Start();

        return new Rig
        {
            Hub = hub,
            RaftNodeTransport = raftTransport,
            ClientTransport = clientTransport,
            Raft = raft,
            Machine = machine,
            Store = store,
            Service = service,
        };
    }

    /// <summary>G2：段授予经 raft 全序——两客户端请求区间不重叠且严格递增。</summary>
    [Fact]
    public async Task Allocate_TwoClients_DisjointIncreasing()
    {
        await using var fx = await CreateRigAsync();
        await WaitForLeaderAsync(fx);

        var s1 = await fx.Service.AllocateAsync("order", 100);
        var s2 = await fx.Service.AllocateAsync("order", 100);

        s1.Should().Be((1, 100), "首段从 1 起");
        s2.Start.Should().Be(101, "段衔接不重叠");
        s2.End.Should().Be(200);
        s2.End.Should().BeGreaterThan(s1.End, "严格递增");
    }

    /// <summary>G2：分发器本地发号——段耗尽自动补段；ID 全局单调无重复。</summary>
    [Fact]
    public async Task Dispenser_LocalHandout_AutoRefetch()
    {
        await using var fx = await CreateRigAsync();
        await WaitForLeaderAsync(fx);
        var dispenser = new IdSegmentDispenser(fx.ClientTransport, fx.RaftNodeTransport.Self, Domain, "order", segmentSize: 10);

        var ids = new List<long>();
        for (var i = 0; i < 25; i++)
            ids.Add(await dispenser.NextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));

        ids.Should().HaveCount(25);
        ids.Distinct().Should().HaveCount(25, "全局无重复");
        ids.Should().BeInAscendingOrder("段内连续 + 段间递增");
        ids[0].Should().Be(1);
        // 25 个 ID = 3 段（10+10+5）——机器分配尾 = 30
        fx.Machine.AllocatedTail("order").Should().Be(30, "段耗尽自动补段");
    }

    /// <summary>G2：分配尾跨"重启"（同存储重建状态机/服务）单调保持——新段从旧尾之后开始。</summary>
    [Fact]
    public async Task Allocate_SurvivesRestart_Monotonic()
    {
        var hub = new InProcessTransportHub();
        var raftId = NodeId.NewRandom();
        var raftTransport = hub.Register(raftId);
        raftTransport.Start();
        var store = new Fixtures.InMemoryRaftStore();
        await store.InitializeAsync();
        var machine = new IdSegmentStateMachine();
        var pipeline = new ApplyPipeline(store, machine, ApplyPipelineOptions.Default);
        var options = RaftOptions.Default
            .WithElectionTimeout(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(800));
        var raft = new RaftStateMachine(raftId, store, raftTransport, pipeline, options);
        pipeline.SetConfigCallback(raft.PostConfigChanged);
        await pipeline.StartAsync();
        await raft.StartAsync(new ClusterConfig([new ClusterMember(raftId, "")]));
        var service1 = new GlobalIdService(raftTransport, Domain, raft, machine);

        var (start1, end1) = await service1.AllocateAsync("order", 50);
        end1.Should().Be(50);
        service1.Dispose();
        await raft.DisposeAsync();
        await raftTransport.DisposeAsync();   // 旧传输退役（其注册表随实例消亡）
        await hub.DisposeAsync();

        // "重启"：全新 hub/传输（同 raftId、同 store）——分配尾从 50 之后继续
        var machine2 = new IdSegmentStateMachine();
        var pipeline2 = new ApplyPipeline(store, machine2, ApplyPipelineOptions.Default);
        var hub2 = new InProcessTransportHub();
        var raftTransport2 = hub2.Register(raftId);
        raftTransport2.Start();
        var raft2 = new RaftStateMachine(raftId, store, raftTransport2, pipeline2, options);
        // （同一 raftTransport 复用——raft1 已 Dispose；注册表随实例新建）
        pipeline2.SetConfigCallback(raft2.PostConfigChanged);
        await pipeline2.StartAsync();
        await raft2.StartAsync(new ClusterConfig([new ClusterMember(raftId, "")]));
        var service2 = new GlobalIdService(raftTransport2, Domain, raft2, machine2);
        var (start2, end2) = await service2.AllocateAsync("order", 25);
        service2.Dispose();
        await raft2.DisposeAsync();
        await raftTransport2.DisposeAsync();
        await hub2.DisposeAsync();

        start2.Should().Be(51, "重启后分配尾单调保持——不回发已发段");
        end2.Should().Be(75);
    }

    private static async Task WaitForLeaderAsync(Rig fx)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!fx.Raft.IsLeader)
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("leader 未收敛。");
            await Task.Delay(50);
        }
    }
}
