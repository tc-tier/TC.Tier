using System.Collections.Concurrent;
using FluentAssertions;
using TC.Tier.Core.Net.Coordination;
using TC.Tier.Core.Net.Raft;
using TC.Tier.Core.Net.Transport.InProcess;
using TC.Tier.Core.Net.Tests.Fixtures;
using Xunit;

namespace TC.Tier.Core.Net.Tests.Coordination;

/// <summary>
/// 分布式锁/租约原语（二期-G4 NETGAP-027——验证矩阵 G4 行）：
/// 锁互斥（他人持有时拒绝）、租约过期（停止续约后他人可获得）、fencing 单调（新授予 token 递增）、
/// keepalive 续约（持有者保持）。
/// </summary>
public class DistributedLockTests
{
    private const byte Domain = 0x72;   // 注册区（0x60-0xAF 使用方自管号——测试域）

    private sealed class Rig : IAsyncDisposable
    {
        public required InProcessTransportHub Hub { get; init; }
        public required InProcessNode RaftNodeTransport { get; init; }
        public required RaftStateMachine Raft { get; init; }
        public required LockStateMachine Machine { get; init; }
        public required DistributedLockService Service { get; init; }
        public required InProcessNode ClientTransport { get; init; }

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
        var machine = new LockStateMachine();
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

        return new Rig
        {
            Hub = hub,
            RaftNodeTransport = raftTransport,
            Raft = raft,
            Machine = machine,
            Service = service,
            ClientTransport = clientTransport,
        };
    }

    /// <summary>G4：锁互斥——A 授予后 B 同锁被拒（denied 且提示实际持有者）。</summary>
    [Fact]
    public async Task Acquire_Mutex_SecondClientDenied()
    {
        await using var fx = await CreateRigAsync();
        var a = new DistributedLockClient(fx.ClientTransport, fx.RaftNodeTransport.Self, Domain, "lock-1", "client-A", leaseMs: 5000);
        var b = new DistributedLockClient(fx.ClientTransport, fx.RaftNodeTransport.Self, Domain, "lock-1", "client-B", leaseMs: 5000);

        (await a.AcquireAsync()).Should().BeTrue("A 首次授予");
        a.FencingToken.Should().BeGreaterThan(0, "fencing token = apply index");

        (await b.AcquireAsync()).Should().BeFalse("B 在租期内被拒——互斥");

        // 续约：A 同 owner 续约 token 不变
        var tokenBefore = a.FencingToken;
        (await a.RenewAsync()).Should().BeTrue("A 续约成功");
        a.FencingToken.Should().Be(tokenBefore, "续约不改 token");
    }

    /// <summary>G4：租约过期——持有者停止续约后，他人可获得新授予且 fencing token 递增。</summary>
    [Fact]
    public async Task Lease_Expiry_NewGrantWithHigherFencingToken()
    {
        await using var fx = await CreateRigAsync();
        var a = new DistributedLockClient(fx.ClientTransport, fx.RaftNodeTransport.Self, Domain, "lock-2", "client-A", leaseMs: 400);
        var b = new DistributedLockClient(fx.ClientTransport, fx.RaftNodeTransport.Self, Domain, "lock-2", "client-B", leaseMs: 5000);

        (await a.AcquireAsync()).Should().BeTrue();
        var tokenA = a.FencingToken;

        await Task.Delay(700);   // 不续约——租约过期

        (await b.AcquireAsync()).Should().BeTrue("过期后新授予");
        b.FencingToken.Should().BeGreaterThan(tokenA, "fencing token 单调递增（apply index）");
    }

    /// <summary>G4：释放——Owner + token 匹配才释放；释放后他人可获得；错 token 释放被拒。</summary>
    [Fact]
    public async Task Release_TokenMatch_Required()
    {
        await using var fx = await CreateRigAsync();
        var a = new DistributedLockClient(fx.ClientTransport, fx.RaftNodeTransport.Self, Domain, "lock-3", "client-A", leaseMs: 5000);
        var wrongToken = new DistributedLockClient(fx.ClientTransport, fx.RaftNodeTransport.Self, Domain, "lock-3", "client-A", leaseMs: 5000);
        var b = new DistributedLockClient(fx.ClientTransport, fx.RaftNodeTransport.Self, Domain, "lock-3", "client-B", leaseMs: 5000);

        (await a.AcquireAsync()).Should().BeTrue();

        // 错 token 释放——不生效（Owner+token 匹配才释放）
        (await wrongToken.ReleaseWithTokenAsync(a.FencingToken + 999)).Should().BeFalse("token 不匹配——释放拒绝");
        a.Holds.Should().BeTrue("A 仍持有");

        (await a.ReleaseAsync()).Should().BeTrue("正确 token 释放");
        (await b.AcquireAsync()).Should().BeTrue("释放后他人可获得");
    }

    /// <summary>G4：keepalive 续约——持有者在多个续约周期后仍保持锁（租约不过期）。</summary>
    [Fact]
    public async Task Keepalive_Renewal_HoldsAcrossCycles()
    {
        await using var fx = await CreateRigAsync();
        var a = new DistributedLockClient(fx.ClientTransport, fx.RaftNodeTransport.Self, Domain, "lock-4", "client-A", leaseMs: 500);
        var b = new DistributedLockClient(fx.ClientTransport, fx.RaftNodeTransport.Self, Domain, "lock-4", "client-B", leaseMs: 5000);

        (await a.AcquireAsync()).Should().BeTrue();
        var token = a.FencingToken;

        using var cts = new CancellationTokenSource();
        var lost = new TaskCompletionSource();
        var keepalive = a.RunKeepaliveAsync(TimeSpan.FromMilliseconds(150), onLost: () => lost.TrySetResult(), cts.Token);

        await Task.Delay(1200);   // 跨多个租约周期（500ms 租约）
        a.Holds.Should().BeTrue("keepalive 续约——持有保持");
        lost.Task.IsCompleted.Should().BeFalse("未失锁");

        (await b.AcquireAsync()).Should().BeFalse("租期内 B 被拒");
        cts.Cancel();
        await keepalive;
    }

}
