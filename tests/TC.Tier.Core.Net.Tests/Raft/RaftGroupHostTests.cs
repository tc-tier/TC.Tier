using System.Net;
using FluentAssertions;
using TC.Tier.Core.Net.Channels;
using TC.Tier.Core.Net.Raft;
using TC.Tier.Core.Net.Tests.Fixtures;
using Xunit;

namespace TC.Tier.Core.Net.Tests.Raft;

/// <summary>
/// Multi-Raft 组路由语义（二期-C1——验证矩阵 C1 行）：
/// 3 节点 × 2 组同传输共存（组隔离 / 各自选主）、组前缀畸形 / 未知组丢弃 + 计数（不断链）、
/// 单写者纪律（同组重复注册抛）。
/// </summary>
public class RaftGroupHostTests
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

    private sealed class GroupNode : IAsyncDisposable
    {
        public required InProcessNode Transport { get; init; }
        public required RaftGroupHost Host { get; init; }
        public required RaftGroupChannel GroupA { get; init; }
        public required RaftGroupChannel GroupB { get; init; }
        public required RecordingStateMachine MachineA { get; init; }
        public required RecordingStateMachine MachineB { get; init; }
        public required RaftStateMachine RaftA { get; init; }
        public required RaftStateMachine RaftB { get; init; }

        public async ValueTask DisposeAsync()
        {
            await RaftA.DisposeAsync();
            await RaftB.DisposeAsync();
            await Host.DisposeAsync();
            await Transport.DisposeAsync();
        }
    }

    private sealed class Rig(List<GroupNode> nodes, InProcessTransportHub hub) : IAsyncDisposable
    {
        public List<GroupNode> Nodes { get; } = nodes;
        public InProcessTransportHub Hub { get; } = hub;

        public async ValueTask DisposeAsync()
        {
            foreach (var n in Nodes)
            {
                try { await n.DisposeAsync(); } catch { /* 尽力清理 */ }
            }
            await Hub.DisposeAsync();
        }
    }

    private static async Task<Rig> CreateTwoGroupClusterAsync(int count)
    {
        var hub = new InProcessTransportHub();
        var nodes = new List<GroupNode>();
        try
        {
            var members = Enumerable.Range(0, count).Select(_ => NodeId.NewRandom()).ToArray();
            var config = new ClusterConfig(members.Select(id => new ClusterMember(id, "")).ToArray());
            foreach (var id in members)
            {
                var transport = hub.Register(id);
                transport.Start();
                var host = new RaftGroupHost(transport);
                await host.StartAsync();
                var groupA = host.CreateGroup(new RaftGroupId(0x0A0A));
                var groupB = host.CreateGroup(new RaftGroupId(0x0B0B));
                var machineA = new RecordingStateMachine();
                var machineB = new RecordingStateMachine();
                var storeA = new InMemoryRaftStore();
                var storeB = new InMemoryRaftStore();
                var pipelineA = new ApplyPipeline(storeA, machineA, ApplyPipelineOptions.Default);
                var pipelineB = new ApplyPipeline(storeB, machineB, ApplyPipelineOptions.Default);
                var options = RaftOptions.Default
                    .WithElectionTimeout(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(800))
                    .WithRandom(new Random(id.GetHashCode()));
                var raftA = new RaftStateMachine(id, storeA, groupA, pipelineA, options);
                pipelineA.SetConfigCallback(raftA.PostConfigChanged);
                await pipelineA.StartAsync();
                await raftA.StartAsync(config);
                var raftB = new RaftStateMachine(id, storeB, groupB, pipelineB, options);
                pipelineB.SetConfigCallback(raftB.PostConfigChanged);
                await pipelineB.StartAsync();
                await raftB.StartAsync(config);
                nodes.Add(new GroupNode
                {
                    Transport = transport, Host = host, GroupA = groupA, GroupB = groupB,
                    MachineA = machineA, MachineB = machineB, RaftA = raftA, RaftB = raftB,
                });
            }
            return new Rig(nodes, hub);
        }
        catch
        {
            foreach (var n in nodes) { try { await n.DisposeAsync(); } catch { } }
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

    private static async Task<long> ReplicateOnAsync(RaftStateMachine raft, byte[] command)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await raft.ReplicateAsync(command).AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (NotLeaderException) when (attempt < 40) { await Task.Delay(50); }
        }
    }

    /// <summary>C1：3 节点 × 2 组同传输共存——各组独立选主、独立提交、提交互不可见（组隔离）。</summary>
    [Fact]
    public async Task TwoGroups_SameTransports_IsolatedElectionAndCommit()
    {
        await using var fx = await CreateTwoGroupClusterAsync(3);

        // 各组各自收敛单 leader（同一物理拓扑——leader 可能不同节点）
        await WaitForAsync(() => fx.Nodes.Count(n => n.RaftA.IsLeader) == 1, TimeSpan.FromSeconds(15));
        await WaitForAsync(() => fx.Nodes.Count(n => n.RaftB.IsLeader) == 1, TimeSpan.FromSeconds(15));
        var leaderA = fx.Nodes.Single(n => n.RaftA.IsLeader);
        var leaderB = fx.Nodes.Single(n => n.RaftB.IsLeader);

        // 组 A 写两条——仅组 A 状态机可见；组 B 零应用（manifest 不串组的机器面等价断言）
        await ReplicateOnAsync(leaderA.RaftA, new byte[] { 0x11 });
        var idxA = await ReplicateOnAsync(leaderA.RaftA, new byte[] { 0x22 });
        await WaitForAsync(() => fx.Nodes.All(n => n.MachineA.Applied.Count >= 2), TimeSpan.FromSeconds(10));

        fx.Nodes.All(n => n.MachineB.Applied.IsEmpty).Should().BeTrue("组 A 提交对组 B 不可见（组隔离）");

        // 组 B 写一条——独立水位
        var idxB = await ReplicateOnAsync(leaderB.RaftB, new byte[] { 0x33 });
        await WaitForAsync(() => fx.Nodes.All(n => n.MachineB.Applied.ContainsKey(idxB)), TimeSpan.FromSeconds(10));
        idxB.Should().BeGreaterThanOrEqualTo(1, "组 B 独立提交推进");
    }

    /// <summary>C1：组前缀畸形（&lt; 8B）/ 未知组——丢弃 + 计数、不断链（链路照常可用）。</summary>
    [Fact]
    public async Task MalformedAndUnknownGroup_DroppedAndCounted_LinkIntact()
    {
        await using var fx = await CreateTwoGroupClusterAsync(3);
        await WaitForAsync(() => fx.Nodes.Count(n => n.RaftA.IsLeader) == 1, TimeSpan.FromSeconds(15));
        var leader = fx.Nodes.Single(n => n.RaftA.IsLeader);
        var follower = fx.Nodes.First(n => !n.RaftA.IsLeader);

        // 畸形（< 8B 前缀）：直发到 follower 的物理传输——组防线丢弃（对端超时 = 尽力语义）
        var malformed = new byte[] { 0x01, 0x02, 0x03 };   // 3B < 8B 前缀
        await using var sender = fx.Hub.Register(NodeId.NewRandom());
        sender.Start();
        var timeout = RequestOptions.Default with { Timeout = TimeSpan.FromMilliseconds(300) };
        var act = async () => await sender.SendRequestAsync(follower.Transport.Self, ProtocolIds.Raft, malformed, timeout);
        await act.Should().ThrowAsync<Exception>("畸形载荷无应答——调用方超时语义");

        // 未知组：合法编码载荷 + 未注册组前缀——同样丢弃 + 计数
        var encoded = RaftRpcCodec.Encode(new AppendEntriesReq
        {
            Term = 1, LeaderId = leader.Transport.Self, PrevLogIndex = 0, PrevLogTerm = 0, LeaderCommit = 0,
        });
        var unknownGroupId = new RaftGroupId(0xDEAD);
        var unknown = new byte[RaftGroupHost.GroupPrefixBytes + encoded.Length];
        RaftGroupHost.WritePrefix(unknown, unknownGroupId);
        encoded.AsSpan().CopyTo(unknown.AsSpan(RaftGroupHost.GroupPrefixBytes));
        var act2 = async () => await sender.SendRequestAsync(follower.Transport.Self, ProtocolIds.Raft, unknown, timeout);
        await act2.Should().ThrowAsync<Exception>("未知组无应答——调用方超时语义");

        // 防线计数已增（畸形 / 未知组）
        follower.Host.Stats.MalformedDrops.Should().BeGreaterThanOrEqualTo(1);
        follower.Host.Stats.UnknownGroupDrops.Should().BeGreaterThanOrEqualTo(1);

        // 不断链：正常组路由请求照常应答（已知 leader 直发组 A 提案被拒——角色应答可达）
        var knownGroup = new byte[RaftGroupHost.GroupPrefixBytes + encoded.Length];
        RaftGroupHost.WritePrefix(knownGroup, new RaftGroupId(0x0A0A));
        encoded.AsSpan().CopyTo(knownGroup.AsSpan(RaftGroupHost.GroupPrefixBytes));
        var ok = await sender.SendRequestAsync(follower.Transport.Self, ProtocolIds.Raft, knownGroup, timeout);
        ok.Length.Should().BeGreaterThan(0, "合法组请求正常应答——防线只丢非法不伤链路");
    }

    /// <summary>C1：同组同域重复注册抛（单写者纪律）；未知域抛。</summary>
    [Fact]
    public async Task DuplicateRegistration_AndForeignDomain_Throw()
    {
        var hub = new InProcessTransportHub();
        var transport = hub.Register(NodeId.NewRandom());
        transport.Start();
        await using var hostTransport = transport;
        var host = new RaftGroupHost(transport);
        await host.StartAsync();
        await using var __ = host;
        var channel = host.CreateGroup(RaftGroupId.Empty);

        var invoker = () => channel.RegisterCoreRequestHandler(ProtocolIds.Raft, new EchoHandler());
        invoker.Should().NotThrow("首次注册合法");
        invoker.Should().Throw<InvalidOperationException>("同组同域重复注册抛——沿用单组纪律");
        var invoker2 = () => channel.RegisterCoreStreamAcceptor(ProtocolIds.Raft, new NoopAcceptor());
        invoker2.Should().Throw<ArgumentOutOfRangeException>("0x01 非流式域");
        var invoker3 = () => channel.SendDatagramAsync(NodeId.NewRandom(), 0x60, ReadOnlyMemory<byte>.Empty);
        invoker3.Should().Throw<NotSupportedException>("Raft 家族零数据报");
    }

    private sealed class EchoHandler : IRequestHandler
    {
        public void OnRequest(NodeId from, ReadOnlyMemory<byte> payload, IReplyContext reply)
            => _ = reply.ReplyAsync(payload.ToArray()).AsTask();
    }

    private sealed class NoopAcceptor : IStreamAcceptor
    {
        public void OnStream(NodeId from, IWireStream stream, ReadOnlyMemory<byte> openPayload) { }
    }
}
