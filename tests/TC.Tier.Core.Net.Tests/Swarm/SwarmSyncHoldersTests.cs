using FluentAssertions;
using Xunit;
using TC.Tier.Core.Net.Swarm;
using TC.Tier.Core.Net.Transport.InProcess;

namespace TC.Tier.Core.Net.Tests.Swarm;

/// <summary>
/// SwarmSync 持有表语义测试（spec-12 §6.1 增量 S2——manifest 代际持有关系：登记幂等/
/// 空表兜底 [本端]/换代清表/换届清表/Announce 往返/纯消费者配置线）。
/// </summary>
public class SwarmSyncHoldersTests
{
    private static Opaque16 Id(byte fill)
    {
        Span<byte> bytes = stackalloc byte[16];
        bytes.Fill(fill);
        return new Opaque16(bytes);
    }

    [Fact]
    public async Task RegisterSource_Idempotent_AggregatesHolders()
    {
        await using var hub = new InProcessTransportHub();
        var node = hub.Register(NodeId.NewRandom());
        node.Start();
        var sync = new SwarmSync(node);
        var id = Id(0x01);
        var holderA = NodeId.NewRandom();
        var holderB = NodeId.NewRandom();

        sync.RegisterSource(id, holderA);
        sync.RegisterSource(id, holderB);
        sync.RegisterSource(id, holderA);   // 重复登记

        sync.GetHolders(id).Should().HaveCount(2, "重复 Announce 幂等");
    }

    [Fact]
    public async Task GetHolders_EmptyTable_FallsBackToSelf()
    {
        await using var hub = new InProcessTransportHub();
        var node = hub.Register(NodeId.NewRandom());
        node.Start();
        var sync = new SwarmSync(node);

        sync.GetHolders(Id(0x02)).Should().Equal([node.Self],
            "漏报退化：空表兜底 [本端]——无上报时单源，永不比现在差");
    }

    [Fact]
    public async Task ResetHolders_ClearsGeneration_RegistersSelf()
    {
        await using var hub = new InProcessTransportHub();
        var node = hub.Register(NodeId.NewRandom());
        node.Start();
        var sync = new SwarmSync(node);
        var oldGen = Id(0x03);
        var newGen = Id(0x04);
        sync.RegisterSource(oldGen, NodeId.NewRandom());
        sync.RegisterSource(newGen, NodeId.NewRandom());

        sync.ResetHolders(newGen);

        sync.GetHolders(newGen).Should().Equal([node.Self], "换代清表——旧 Announce 不污染新代，本端必持有刚发布内容");
        sync.GetHolders(oldGen).Should().NotContain(node.Self, "其它代不受影响");
    }

    [Fact]
    public async Task OnLeaderLost_ClearsAllGenerations()
    {
        await using var hub = new InProcessTransportHub();
        var node = hub.Register(NodeId.NewRandom());
        node.Start();
        var sync = new SwarmSync(node);
        sync.RegisterSource(Id(0x05), NodeId.NewRandom());
        sync.RegisterSource(Id(0x06), NodeId.NewRandom());

        sync.OnLeaderLost();

        sync.GetHolders(Id(0x05)).Should().Equal([node.Self], "换届丢表——兜底 [本端] 语义");
        sync.GetHolders(Id(0x06)).Should().Equal([node.Self]);
    }

    /// <summary>Announce 往返（S2 发送面 + handler 登记）：follower 上报 → leader 持有表登记。</summary>
    [Fact]
    public async Task AnnounceSourceAsync_RegistersOnLeader()
    {
        await using var hub = new InProcessTransportHub();
        var leaderTransport = hub.Register(NodeId.NewRandom());
        leaderTransport.Start();
        var leader = new SwarmSync(leaderTransport);
        await leader.StartAsync();
        var followerTransport = hub.Register(NodeId.NewRandom());
        followerTransport.Start();
        var follower = new SwarmSync(followerTransport);
        await follower.StartAsync();
        var id = Id(0x07);

        await follower.AnnounceSourceAsync(leaderTransport.Self, id);

        leader.GetHolders(id).Should().Contain(followerTransport.Self, "Announce 到达即登记");
    }

    /// <summary>ServeBlocks=false（纯消费者）：不广播（ShouldAnnounce=false）——即使误达也无处登记
    /// （handler 未注册）；GetHolders 恒兜底 [本端]。</summary>
    [Fact]
    public async Task ServeBlocksFalse_NoAnnounceNoRegistration()
    {
        await using var hub = new InProcessTransportHub();
        var leaderTransport = hub.Register(NodeId.NewRandom());
        leaderTransport.Start();
        var leader = new SwarmSync(leaderTransport);
        await leader.StartAsync();
        var consumerTransport = hub.Register(NodeId.NewRandom());
        consumerTransport.Start();
        var consumer = new SwarmSync(consumerTransport, SwarmOptions.Default.WithServeBlocks(false));
        await consumer.StartAsync();
        consumer.ShouldAnnounce.Should().BeFalse("纯消费者不广播");
        var id = Id(0x08);

        await consumer.AnnounceSourceAsync(leaderTransport.Self, id);   // 静默空操作

        leader.GetHolders(id).Should().Equal([leaderTransport.Self], "无上报——兜底 [本端]（单源退化）");
    }

    /// <summary>AnnounceOnStart=false：退出广播面但不影响 ServeBlocks 面的 handler 注册。</summary>
    [Fact]
    public void AnnounceOnStartFalse_ExitsBroadcastFace_KeepsHandler()
    {
        var options = SwarmOptions.Default.WithAnnounceOnStart(false);
        options.ServeBlocks.Should().BeTrue("handler 照常注册——静态 holders 指名拉取仍可用");
        options.AnnounceOnStart.Should().BeFalse();
    }
}
