using FluentAssertions;
using TC.Tier.Core.Net.Coordination;
using TC.Tier.Core.Net.Raft;
using TC.Tier.Core.Net.Transport.InProcess;
using TC.Tier.Core.Net.Tests.Fixtures;
using TC.Tier.Core.Testing;
using Xunit;

namespace TC.Tier.Core.Net.Tests.Coordination;

/// <summary>
/// 分布式锁时钟缝（故障注入面补全设计 件一 + §1.3 矩阵）——假钟驱动的租约语义：
/// 租约到期 = 快进确定性判除（零真实睡等）；墙钟跳变不误判（租约判定走单调戳）。
/// </summary>
public sealed class DistributedLockFakeClockTests
{
    private const byte Domain = 0x72;   // 注册区（0x60-0xAF 使用方自管号——测试域）

    [Fact]
    public async Task Lease_Expiry_AdvanceDrivenDeterministic()
    {
        var clock = new FakeTimeProvider();
        var hub = new InProcessTransportHub();
        var raftId = NodeId.NewRandom();
        var raftTransport = hub.Register(raftId);
        raftTransport.Start();
        var store = new Fixtures.InMemoryRaftStore();
        await store.InitializeAsync();
        var machine = new LockStateMachine(clock);
        var pipeline = new ApplyPipeline(store, machine, ApplyPipelineOptions.Default);
        var options = RaftOptions.Default
            .WithElectionTimeout(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(800));
        var raft = new RaftStateMachine(raftId, store, raftTransport, pipeline, options);
        pipeline.SetConfigCallback(raft.PostConfigChanged);
        await pipeline.StartAsync();
        await raft.StartAsync(new ClusterConfig([new ClusterMember(raftId, "")]));
        var service = new DistributedLockService(raftTransport, Domain, raft, machine);
        var clientTransport = hub.Register(NodeId.NewRandom());
        clientTransport.Start();
        try
        {
            var a = new DistributedLockClient(clientTransport, raftTransport.Self, Domain, "fk-lock", "client-A", leaseMs: 400, clock: clock);
            var b = new DistributedLockClient(clientTransport, raftTransport.Self, Domain, "fk-lock", "client-B", leaseMs: 5000, clock: clock);

            (await a.AcquireAsync()).Should().BeTrue("A 首次授予");
            (await b.AcquireAsync()).Should().BeFalse("租期内 B 被拒——互斥");
            machine.Query("fk-lock").Exists.Should().BeTrue("租约在期内（快进前）");

            clock.Advance(TimeSpan.FromMilliseconds(500));   // 快进过租约点（§1.3 前跳行——零真实睡等）

            machine.Query("fk-lock").Exists.Should().BeFalse("租约到期判除（假钟快进驱动）");
            (await b.AcquireAsync()).Should().BeTrue("停止续约后他人可获得新授予");

            // 墙钟跳变不误判：租约判定走单调戳（§1.3 单调消费点列——不受墙钟影响）
            clock.SetWallClock(FakeTimeProvider.DefaultStart.AddHours(-5));
            machine.Query("fk-lock").Exists.Should().BeTrue("墙钟倒流不回收未到期租约（单调戳判定）");
        }
        finally
        {
            await clientTransport.DisposeAsync();
            service.Dispose();
            await raft.DisposeAsync();
            await raftTransport.DisposeAsync();
            await hub.DisposeAsync();
        }
    }
}
