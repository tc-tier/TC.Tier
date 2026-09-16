using FluentAssertions;
using TC.Tier.Contracts.Storage;
using TC.Tier.Core.Net;
using TC.Tier.Core.Net.Raft;
using TC.Tier.Core.Net.Transport.InProcess;
using TC.Tier.Products.Net.Routing;
using Xunit;

namespace TC.Tier.Products.Tests.Routing;

/// <summary>
/// GroupReplicaRouter 分发面契约测试（replica-prerequisites-design.md §2/§4 矩阵 #2）：
/// 未装配组 → 确定性 Rejected；NotLeaderHint → NotLeaderException 携 hint；Ok 封套（地址 + index）
/// 全线往返无损；GetOrCreate 幂等（同传输同域一实例——异域分实例）。
/// <para>★ 线格式为迁移回归锚（TierQueueReplicaTests 1~12）——本测只钉路由器通用面行为。</para>
/// </summary>
public class GroupReplicaRouterTests
{
    /// <summary>路由器测试协议域（避开 0x60/0x61 产品域号——域号分配表见 Products.Net/COORDINATION.md）。</summary>
    private const byte TestDomain = 0x7E;

    /// <summary>桩受理面（返回预置结果——计数入站调用）。</summary>
    private sealed class StubHandler(Func<ReadOnlyMemory<byte>, GroupApplyResult>? onSelect = null)
        : IGroupProposalHandler
    {
        public int Calls;

        public ValueTask<GroupApplyResult> ProposeAsync(ReadOnlyMemory<byte> command, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return ValueTask.FromResult(onSelect is { } f ? f(command) : GroupApplyResult.FromOk());
        }
    }

    [Fact]
    public async Task Forward_UnregisteredGroup_DeterministicallyRejected()
    {
        await using var hub = new InProcessTransportHub();
        var nodeA = hub.Register(NodeId.NewRandom());
        var nodeB = hub.Register(NodeId.NewRandom());
        nodeA.Start();
        nodeB.Start();
        using var routerA = GroupReplicaRouter.GetOrCreate(nodeA, TestDomain);
        using var routerB = GroupReplicaRouter.GetOrCreate(nodeB, TestDomain);   // B 不装配任何组

        var result = await routerA.ForwardAsync(nodeB.Self, new RaftGroupId(0x999),
            new byte[] { 1, 2, 3 }, default);

        result.Status.Should().Be(GroupApplyStatus.Rejected, "未装配组 = 确定性拒绝（非超时静默）");
        result.Error.Should().Contain("未装配");
    }

    [Fact]
    public async Task Forward_NotLeaderHint_ThrowsNotLeaderExceptionWithHint()
    {
        await using var hub = new InProcessTransportHub();
        var nodeA = hub.Register(NodeId.NewRandom());
        var nodeB = hub.Register(NodeId.NewRandom());
        nodeA.Start();
        nodeB.Start();
        using var routerA = GroupReplicaRouter.GetOrCreate(nodeA, TestDomain);
        using var routerB = GroupReplicaRouter.GetOrCreate(nodeB, TestDomain);
        var leader = NodeId.NewRandom();
        routerB.Register(new RaftGroupId(0x42), new StubHandler(_ =>
            new GroupApplyResult(GroupApplyStatus.NotLeaderHint, default, 0, leader.ToString())));

        var act = () => routerA.ForwardAsync(nodeB.Self, new RaftGroupId(0x42),
            new byte[] { 1 }, default).AsTask();

        var ex = (await act.Should().ThrowAsync<NotLeaderException>()).Which;
        ex.Leader.Should().Be(leader, "NotLeaderHint 封套转 NotLeader 惯例异常——携 leader 供重路由");
    }

    [Fact]
    public async Task Forward_OkEnvelope_AddressAndIndexRoundTripLossless()
    {
        await using var hub = new InProcessTransportHub();
        var nodeA = hub.Register(NodeId.NewRandom());
        var nodeB = hub.Register(NodeId.NewRandom());
        nodeA.Start();
        nodeB.Start();
        using var routerA = GroupReplicaRouter.GetOrCreate(nodeA, TestDomain);
        using var routerB = GroupReplicaRouter.GetOrCreate(nodeB, TestDomain);

        var produced = new LogicalAddress(7, 3, 42);
        byte[]? receivedCommand = null;
        var handler = new StubHandler(cmd =>
        {
            receivedCommand = cmd.ToArray();
            return GroupApplyResult.FromOk(produced, 1234);
        });
        routerB.Register(new RaftGroupId(0x42), handler);

        var command = new byte[] { 9, 8, 7, 6 };
        var result = await routerA.ForwardAsync(nodeB.Self, new RaftGroupId(0x42), command, default);

        result.Status.Should().Be(GroupApplyStatus.Ok);
        result.Address.Should().Be(produced, "地址（D1 版本/fencing token）无损往返");
        result.AppliedIndex.Should().Be(1234, "共识进度无损往返");
        receivedCommand.Should().BeEquivalentTo(command, "命令字节原样透传（GroupId 头剥除）");
    }

    [Fact]
    public async Task Forward_OkWithInvalidAddress_EncodesBareOkFrame()
    {
        await using var hub = new InProcessTransportHub();
        var nodeA = hub.Register(NodeId.NewRandom());
        var nodeB = hub.Register(NodeId.NewRandom());
        nodeA.Start();
        nodeB.Start();
        using var routerA = GroupReplicaRouter.GetOrCreate(nodeA, TestDomain);
        using var routerB = GroupReplicaRouter.GetOrCreate(nodeB, TestDomain);
        // Address=Invalid（"无地址产物"哨兵）→ 线格式裸帧 [HasAddr=0]
        // （FromOk() 缺省 Address=Empty = 合法地址 seg0@0，走全帧——Empty 非"无值"哨兵）
        routerB.Register(new RaftGroupId(0x42), new StubHandler(_ =>
            new GroupApplyResult(GroupApplyStatus.Ok, LogicalAddress.Invalid, 0, null)));

        var result = await routerA.ForwardAsync(nodeB.Self, new RaftGroupId(0x42), Array.Empty<byte>(), default);

        result.Status.Should().Be(GroupApplyStatus.Ok);
        result.Address.Should().Be(LogicalAddress.Empty, "裸帧 [0,0] 解码 = FromOk() 缺省地址（与旧实现逐字节同行为）");
        result.AppliedIndex.Should().Be(0);
    }

    [Fact]
    public void GetOrCreate_IdempotentPerTransportAndDomain()
    {
        var hub = new InProcessTransportHub();
        var node = hub.Register(NodeId.NewRandom());
        node.Start();

        var r1 = GroupReplicaRouter.GetOrCreate(node, TestDomain);
        var r2 = GroupReplicaRouter.GetOrCreate(node, TestDomain);
        var r3 = GroupReplicaRouter.GetOrCreate(node, (byte)(TestDomain + 1));

        r2.Should().BeSameAs(r1, "同传输同域幂等");
        r3.Should().NotBeSameAs(r1, "异域 = 独立路由器（域号分配表）");
        r3.Dispose();
        r1.Dispose();
    }
}
