using FluentAssertions;
using Xunit;
using TC.Tier.Core.Net.Hosting;
using TC.Tier.Core.Net.Security;
using TC.Tier.Core.Net.P2P;
using TC.Tier.Core.Net.Ports;
using TC.Tier.Core.Net.Tests.Fixtures;

namespace TC.Tier.Core.Net.Tests.Hosting;

/// <summary>
/// ClusterBuilder assembly gate (spec-12 §11 — the builder example as tests, §12 装配门):
/// member preset (listen + peers + identity forms) / ClusterTag fail-fast / mechanism opt-in
/// mounting (WithP2P — zero-privilege uniform path) / security-mode fail-closed.
/// </summary>
public class ClusterBuilderTests
{
    /// <summary>成员制端点（监听）能接受地址制直连（§4.1 准入分层——成员准入与连接准入分离）。</summary>
    [Fact]
    public async Task StartAsync_MemberPreset_AcceptsClientDial()
    {
        var serverId = NodeId.NewRandom();
        var clientId = NodeId.NewRandom();
        var port = TestEndpoints.ReservePort();

        await using var server = await ClusterBuilder.Create(serverId)
            .Listen(TestEndpoints.Loopback(port))
            .StartAsync();
        var serverUp = new TaskCompletionSource();
        server.PeerConnected += _ => serverUp.TrySetResult();

        await using var client = await NetClientBuilder.Create(clientId)
            .Connect(TestEndpoints.Loopback(port))
            .StartAsync();

        server.Self.Should().Be(serverId);
        client.RemoteId.Should().Be(serverId, "地址制直连——对端身份握手后才得知（§4.1）");
        await serverUp.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>ClusterTag 错集群（§3.3）：握手指纹拒绝——拨号方拿到判别文本（IOException 携由）。</summary>
    [Fact]
    public async Task StartAsync_ClusterTagMismatch_ClientDialRejected()
    {
        var port = TestEndpoints.ReservePort();
        await using var server = await ClusterBuilder.Create(NodeId.NewRandom())
            .ClusterTag(0x5443)
            .Listen(TestEndpoints.Loopback(port))
            .StartAsync();

        var act = async () => await NetClientBuilder.Create(NodeId.NewRandom())
            .Connect(TestEndpoints.Loopback(port))
            .StartAsync();
        await act.Should().ThrowAsync<NetIOException>("错集群拒绝路径——握手期 fail-fast");
    }

    /// <summary>身份供给端口（§8.3）：首次生成保存、此后加载——NodeId 跨"重启"稳定
    /// （成员表/votedFor/密钥绑定前提）。</summary>
    [Fact]
    public async Task StartAsync_IdentitySource_StableAcrossRestart()
    {
        var source = new InMemoryIdentitySource();
        var port = TestEndpoints.ReservePort();

        NodeId firstId;
        await using (var first = await ClusterBuilder.Create(source).Listen(TestEndpoints.Loopback(port)).StartAsync())
        {
            firstId = first.Self;
        }

        // "重启"：同源再装配——身份必须一致
        await using var second = await ClusterBuilder.Create(source).Listen(TestEndpoints.Loopback(TestEndpoints.ReservePort())).StartAsync();
        second.Self.Should().Be(firstId);
    }

    /// <summary>配置注入（§8.3）：WithIdentity 优先于 Create(nodeId)。</summary>
    [Fact]
    public async Task StartAsync_WithIdentity_TakesPrecedenceOverCreate()
    {
        var injected = new NodeIdentity { Id = NodeId.NewRandom() };
        await using var endpoint = await ClusterBuilder.Create(NodeId.NewRandom())
            .WithIdentity(injected)
            .Listen(TestEndpoints.Loopback(TestEndpoints.ReservePort()))
            .StartAsync();
        endpoint.Self.Should().Be(injected.Id);
    }

    /// <summary>安全档装配（§3.4——KeyPair 档已落地）：档位不符配置 = 参数拒绝（fail-fast）。</summary>
    [Fact]
    public void WithKeyPair_WrongModeOptions_Rejected()
    {
        var act = () => ClusterBuilder.Create(NodeId.NewRandom()).WithKeyPair(SecurityOptions.Plain);
        act.Should().Throw<ArgumentException>("WithKeyPair 须 KeyPair 档配置");
    }

    /// <summary>安全档装配（MutualTls 已落地）：null 证书 fail-fast。</summary>
    [Fact]
    public void WithMutualTls_NullCertificate_Rejected()
    {
        var act = () => ClusterBuilder.Create(NodeId.NewRandom()).WithMutualTls(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    /// <summary>机制显式 opt-in（§6/§11）：WithP2P 经统一挂载口（INodeMechanism——内建与第三方
    /// 同路径零特权）；JOIN 从 seed 扩散——装配返回时主动视图已含种子。</summary>
    [Fact]
    public async Task StartAsync_WithP2P_SeedJoin_ActiveViewContainsSeed()
    {
        var seedId = NodeId.NewRandom();
        var nodeId = NodeId.NewRandom();
        var seedPort = TestEndpoints.ReservePort();
        var nodePort = TestEndpoints.ReservePort();
        var peers = new Dictionary<NodeId, System.Net.IPEndPoint>
        {
            [seedId] = TestEndpoints.Loopback(seedPort),
            [nodeId] = TestEndpoints.Loopback(nodePort),
        };

        // 部署种子自身：只启动不 JOIN；节点 2：JOIN 从种子扩散
        await using var seed = await ClusterBuilder.Create(seedId)
            .Listen(TestEndpoints.Loopback(seedPort)).Peers(peers)
            .WithP2P()
            .StartAsync();
        await using var node = await ClusterBuilder.Create(nodeId)
            .Listen(TestEndpoints.Loopback(nodePort)).Peers(peers)
            .WithP2P(seedId)
            .StartAsync();

        var mechanism = node.Mechanisms.OfType<PeerMechanism>().Single();
        mechanism.ActiveView.Should().Contain(seedId, "JOIN 完成（MountAsync 等待 JoinAsync 返回）——主动视图含种子");
        seed.Mechanisms.OfType<PeerMechanism>().Single().ActiveCount.Should().BeGreaterThanOrEqualTo(0);
    }

    // ═══ 传输配置面（spec-12 §4.5 全旋钮可调——注入/管道/优先级三段式）═══

    /// <summary>整份注入：显式方法未触达的旋钮以注入值为准（NodeEndpoint.Options 装配自证）。</summary>
    [Fact]
    public async Task WithTransport_Injection_FillsUnsetKnobs()
    {
        var injected = TransportOptions.Default(null, new Dictionary<NodeId, System.Net.IPEndPoint>())
            .WithHandshakeTimeout(TimeSpan.FromSeconds(5));
        await using var endpoint = await ClusterBuilder.Create(NodeId.NewRandom())
            .WithTransport(injected)
            .StartAsync();

        endpoint.Options.HandshakeTimeout.Should().Be(TimeSpan.FromSeconds(5), "未显式触达的旋钮以注入值为准");
        endpoint.Options.ListenEndPoint.Should().BeNull("未显式 Listen——不监听");
    }

    /// <summary>优先级三段式：显式方法 > 注入 > Default（显式触达的位归方法裁决）。</summary>
    [Fact]
    public async Task WithTransport_ExplicitMethods_WinOverInjection()
    {
        var port = TestEndpoints.ReservePort();
        var injected = TransportOptions.Default(TestEndpoints.Loopback(TestEndpoints.ReservePort()),
                new Dictionary<NodeId, System.Net.IPEndPoint>())
            .WithClusterTag(7)
            .WithHandshakeTimeout(TimeSpan.FromSeconds(5));

        await using var endpoint = await ClusterBuilder.Create(NodeId.NewRandom())
            .WithTransport(injected)
            .ClusterTag(3)
            .Listen(TestEndpoints.Loopback(port))
            .StartAsync();

        endpoint.Options.ClusterTag.Should().Be(3u, "显式 ClusterTag 胜注入");
        endpoint.Options.ListenEndPoint.Should().Be(TestEndpoints.Loopback(port), "显式 Listen 胜注入");
        endpoint.Options.HandshakeTimeout.Should().Be(TimeSpan.FromSeconds(5), "未触达旋钮保持注入值");
    }

    /// <summary>管道链尾依序执行、可组合——默认起步零配置 + 管道调旋钮。</summary>
    [Fact]
    public async Task WithTransport_Pipes_ComposeInOrder()
    {
        await using var endpoint = await ClusterBuilder.Create(NodeId.NewRandom())
            .WithTransport(o => o.WithHandshakeTimeout(TimeSpan.FromSeconds(5)))
            .WithTransport(o => o.WithKeepalive(true, TimeSpan.FromSeconds(2), 4))
            .StartAsync();

        endpoint.Options.HandshakeTimeout.Should().Be(TimeSpan.FromSeconds(5));
        endpoint.Options.EnableKeepalive.Should().BeTrue();
        endpoint.Options.KeepaliveMaxUnanswered.Should().Be(4);
    }

    /// <summary>显式 Listen(null) 语义 = 不监听——覆盖注入的监听端点（null 与"未调用"判别）。</summary>
    [Fact]
    public async Task WithTransport_ExplicitListenNull_ClearsInjectedListen()
    {
        var injected = TransportOptions.Default(TestEndpoints.Loopback(TestEndpoints.ReservePort()),
            new Dictionary<NodeId, System.Net.IPEndPoint>());
        await using var endpoint = await ClusterBuilder.Create(NodeId.NewRandom())
            .WithTransport(injected)
            .Listen(null)
            .StartAsync();

        endpoint.Options.ListenEndPoint.Should().BeNull("显式 Listen(null) 裁决为不监听");
    }
}
