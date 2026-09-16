using FluentAssertions;
using Xunit;
using TC.Tier.Core.Net.Channels;
using TC.Tier.Core.Net.Hosting;
using TC.Tier.Core.Net.Security;
using TC.Tier.Core.Net.Tests.Fixtures;

namespace TC.Tier.Core.Net.Tests.Hosting;

/// <summary>
/// NetClientBuilder assembly gate (spec-12 §11 client preset — address-addressed dial, learn
/// remote identity at handshake, pre-registered handlers serve pushes, optional listen =
/// symmetric node, security fail-closed).
/// </summary>
public class NetClientBuilderTests
{
    private const byte EchoProtocol = 0x64;   // 注册区（0x60-0xAF 使用方自管号——测试域）
    private const byte PushProtocol = 0x65;

    private sealed class EchoRequestHandler : IRequestHandler
    {
        public void OnRequest(NodeId from, ReadOnlyMemory<byte> payload, IReplyContext reply)
            => _ = reply.ReplyAsync(payload.ToArray()).AsTask();   // 回显——直排/读循环上下文立即回程
    }

    private sealed class CapturingDatagramHandler(TaskCompletionSource<byte[]> captured) : IDatagramHandler
    {
        public void OnDatagram(NodeId from, ReadOnlyMemory<byte> payload) => captured.TrySetResult(payload.ToArray());
    }

    /// <summary>地址制直连（§4.1）：有地址者拨号——对端身份握手后才得知（RemoteId）。</summary>
    [Fact]
    public async Task StartAsync_ClientPreset_ConnectsAndLearnsRemoteId()
    {
        var serverId = NodeId.NewRandom();
        var port = TestEndpoints.ReservePort();
        await using var server = await ClusterBuilder.Create(serverId)
            .Listen(TestEndpoints.Loopback(port))
            .StartAsync();

        await using var client = await NetClientBuilder.Create(NodeId.NewRandom())
            .Connect(TestEndpoints.Loopback(port))
            .StartAsync();

        client.RemoteId.Should().Be(serverId);
        client.Self.Should().NotBe(serverId);
    }

    /// <summary>注册面先于拨号（§11 示例）：客户端注册请求回调——服务端收到连接后即可回调服务。</summary>
    [Fact]
    public async Task RegisterRequestHandler_EchoRoundTrip_ServerCallsClient()
    {
        var serverId = NodeId.NewRandom();
        var clientId = NodeId.NewRandom();
        var port = TestEndpoints.ReservePort();
        await using var server = await ClusterBuilder.Create(serverId)
            .Listen(TestEndpoints.Loopback(port))
            .StartAsync();

        // ★ 服务端就绪观测（判例 2026-09-02）：三步握手客户端侧写完 Final 即返回——服务端
        //   注册链路在收到 Final 之后（§5.2 未连快速失败契约：SendRequestAsync 对未注册目标
        //   立即抛 NetIOException）——先订阅再拨号，回调以服务端链路建立为准
        var serverSawClient = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.PeerConnected += id => serverSawClient.TrySetResult();

        await using var client = await NetClientBuilder.Create(clientId)
            .Connect(TestEndpoints.Loopback(port))
            .RegisterRequestHandler(EchoProtocol, new EchoRequestHandler())
            .StartAsync();

        await serverSawClient.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var payload = new byte[] { 0x01, 0x02, 0x03 };
        var echoed = await server.SendRequestAsync(clientId, EchoProtocol, payload);
        echoed.Should().Equal(payload);
    }

    /// <summary>对称节点（§4.2）：客户端只开监听不拨号（无固定客户端/服务器模式）——服务端地址表
    /// 启动即拨号（★成员制拨号归属 §4.3：服务端须为较小方才拨号——纯监听客户端的链路前提；
    /// id 序确定性构造）→ 链路建立后推送数据报（尽力送达：链路未建发送静默丢弃——消费方有界重试）。</summary>
    [Fact]
    public async Task Listen_Optional_ServerPushesToListeningClient()
    {
        // 成员制拨号归属：服务端（较小方）拨号——id 首字节控序（NodeId 字节字典序；
        // 全零 = Empty 哨兵禁用，从 0x01 起）
        var serverId = new NodeId(Enumerable.Repeat((byte)0x01, NodeId.Size).ToArray());
        var clientId = new NodeId(Enumerable.Repeat((byte)0x02, NodeId.Size).ToArray());
        var serverPort = TestEndpoints.ReservePort();
        var clientPort = TestEndpoints.ReservePort();
        var captured = new TaskCompletionSource<byte[]>();

        await using var client = await NetClientBuilder.Create(clientId)
            .Listen(clientPort)
            .RegisterProtocol(PushProtocol, new CapturingDatagramHandler(captured))
            .StartAsync();

        var server = await ClusterBuilder.Create(serverId)
            .Listen(TestEndpoints.Loopback(serverPort))
            .Peers(new Dictionary<NodeId, System.Net.IPEndPoint> { [clientId] = TestEndpoints.Loopback(clientPort) })
            .StartAsync();

        try
        {
            // 拨号由退避驱动（StartAsync 内即起）——事件订阅竞态不依赖：尽力语义下消费方有界重试
            var payload = new byte[] { 0xAA, 0xBB, 0xCC };
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!captured.Task.IsCompleted && DateTime.UtcNow < deadline)
            {
                await server.SendDatagramAsync(clientId, PushProtocol, payload);
                await Task.Delay(100);
            }
            var received = await captured.Task.WaitAsync(TimeSpan.FromSeconds(5));
            received.Should().Equal(payload, "服务端直接推数据——客户端/服务器无固定模式");
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    /// <summary>直连不可达（§5.2 目标未连 = IOException 快速失败）——装配失败不残留半启动端点。</summary>
    [Fact]
    public async Task StartAsync_ConnectUnreachable_ThrowsIOException()
    {
        var port = TestEndpoints.ReservePort();   // 预留后无监听——直连必败
        var act = async () => await NetClientBuilder.Create(NodeId.NewRandom())
            .Connect(TestEndpoints.Loopback(port))
            .StartAsync();
        await act.Should().ThrowAsync<NetIOException>();
    }

    /// <summary>身份供给端口（§8.3）——客户端预设同款（NodeId 跨重启稳定）。</summary>
    [Fact]
    public async Task StartAsync_IdentitySource_StableAcrossRestart()
    {
        var source = new InMemoryIdentitySource();
        var port = TestEndpoints.ReservePort();
        await using var server = await ClusterBuilder.Create(NodeId.NewRandom())
            .Listen(TestEndpoints.Loopback(port))
            .StartAsync();

        // ★ 建立容差（T8 判例——混跑负载下 loopback 握手可超 3s 握手窗：超时取消形态）：
        //   服务端已保证在听，NetIOException = 环境瞬态——有界重试（拒绝型快速失败不受影响）
        static async Task<NodeEndpoint> EstablishAsync(NetClientBuilder builder)
        {
            for (var attempt = 1; ; attempt++)
            {
                try { return await builder.StartAsync(); }
                catch (NetIOException) when (attempt < 5) { await Task.Delay(100); }
            }
        }

        NodeId firstId;
        await using (var first = await EstablishAsync(NetClientBuilder.Create(source).Connect(TestEndpoints.Loopback(port))))
        {
            firstId = first.Self;
        }
        await using var second = await EstablishAsync(NetClientBuilder.Create(source).Connect(TestEndpoints.Loopback(port)));
        second.Self.Should().Be(firstId);
    }

    /// <summary>安全档装配校验（二期-A §7.1——错档 fail-fast，绝不静默降级为明文）。</summary>
    [Fact]
    public void WithKeyPair_WrongMode_FailsFast()
    {
        var act = () => NetClientBuilder.Create(NodeId.NewRandom()).WithKeyPair(SecurityOptions.Plain);
        act.Should().Throw<ArgumentException>("WithKeyPair 须 KeyPair 档——错档装配期拒绝（§3.4 防降级）");
    }

    /// <summary>安全档空配置拒绝。</summary>
    [Fact]
    public void WithKeyPair_Null_Throws()
    {
        var act = () => NetClientBuilder.Create(NodeId.NewRandom()).WithKeyPair(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    /// <summary>ClusterTag 拨入匹配集群（§3.3——错集群 fail-fast 的正路径）：tag 一致握手通过。</summary>
    [Fact]
    public async Task ClusterTag_Match_JoinsTaggedCluster()
    {
        var port = TestEndpoints.ReservePort();
        await using var server = await ClusterBuilder.Create(NodeId.NewRandom())
            .ClusterTag(0x5443)
            .Listen(TestEndpoints.Loopback(port))
            .StartAsync();
        var serverUp = new TaskCompletionSource();
        server.PeerConnected += _ => serverUp.TrySetResult();

        await using var client = await NetClientBuilder.Create(NodeId.NewRandom())
            .ClusterTag(0x5443)
            .Connect(TestEndpoints.Loopback(port))
            .StartAsync();

        client.Options.ClusterTag.Should().Be(0x5443u, "生效配置装配自证");
        await serverUp.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>UDP 端点 + 管道旋钮经生效配置可观测（§4.5 客户端预设同权）。</summary>
    [Fact]
    public async Task WithUdp_AndPipe_ReflectInEffectiveOptions()
    {
        await using var client = await NetClientBuilder.Create(NodeId.NewRandom())
            .WithUdp(0)   // 动态端口——装配期取实际
            .WithTransport(o => o.WithKeepalive(true))
            .StartAsync();

        client.Options.UdpListenEndPoint.Should().NotBeNull("UDP 数据报端点已绑定");
        client.Options.EnableKeepalive.Should().BeTrue("管道改写生效");
    }
}
