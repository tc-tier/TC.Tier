using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using TC.Tier.Core.Net;
using TC.Tier.Core.Net.Transport;
using Xunit;

namespace TC.Tier.Core.Net.Tests.Transport;

/// <summary>
/// 连接治理（二期-D4 NETGAP-012/013——验证矩阵 D4 行）：入站链路上限（超限拒绝、释放后可入）、
/// 握手失败冷却（同源失败 → 冷却窗内快速拒绝 + 计数、窗满恢复）。
/// </summary>
public class ConnectionGovernanceTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    private static int ReservePort()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        socket.Close();
        return port;
    }

    /// <summary>D4：入站链路上限——占满后新入站被拒（对端 ConnectAsync 快速失败）；
    /// 既有链路释放后可再入。</summary>
    [Fact]
    public async Task MaxInboundLinks_OverCap_Rejected()
    {
        var port = ReservePort();
        var serverId = NodeId.NewRandom();
        var server = new ClusterTransport(serverId,
            TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, port), new Dictionary<NodeId, IPEndPoint>())
                .WithConnectionGovernance(maxInboundLinks: 1, handshakeFailCooldown: TimeSpan.Zero));
        server.Start();
        try
        {
            // 占满唯一入站名额
            var firstId = NodeId.NewRandom();
            var first = new ClusterTransport(firstId,
                TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, 0), new Dictionary<NodeId, IPEndPoint>()));
            first.Start();
            await using (first.AsDisposable())
            {
                var remoteId = await first.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port))
                    .AsTask().WaitAsync(WaitLimit);
                remoteId.Should().Be(serverId, "入站链路建立——占满唯一名额");

                // 第二个入站——超上限拒绝（对端 ConnectAsync 快速失败 NetIOException）
                var second = new ClusterTransport(NodeId.NewRandom(),
                    TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, 0), new Dictionary<NodeId, IPEndPoint>()));
                second.Start();
                try
                {
                    var act = async () => await second.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port))
                        .AsTask().WaitAsync(WaitLimit);
                    await act.Should().ThrowAsync<NetIOException>("入站链路上限——超限拒绝");
                }
                finally { await second.DisposeAsync(); }
            }

            // 既有链路释放——名额回收，可再入
            var third = new ClusterTransport(NodeId.NewRandom(),
                TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, 0), new Dictionary<NodeId, IPEndPoint>()));
            third.Start();
            try
            {
                var remote = await third.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port)).AsTask().WaitAsync(WaitLimit);
                remote.Should().NotBe(NodeId.Empty, "名额回收后可再入");
            }
            finally { await third.DisposeAsync(); }
        }
        finally { await server.DisposeAsync(); }
    }

    /// <summary>D4：握手失败冷却——同源失败后冷却窗内快速拒绝（服务端不再完整握手，冷却拒绝计数）。</summary>
    [Fact]
    public async Task HandshakeFailCooldown_BlocksSameSource()
    {
        var port = ReservePort();
        var serverId = NodeId.NewRandom();
        var server = new ClusterTransport(serverId,
            TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, port), new Dictionary<NodeId, IPEndPoint>())
                .WithConnectionGovernance(maxInboundLinks: 0, handshakeFailCooldown: TimeSpan.FromSeconds(30)));
        server.Start();
        try
        {
            // 第一次：错 ClusterTag 握手——被拒（完整握手失败）
            var clientBad = new ClusterTransport(NodeId.NewRandom(),
                TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, 0), new Dictionary<NodeId, IPEndPoint>())
                    .WithClusterTag(0xBAD0));
            clientBad.Start();
            var act1 = async () => await clientBad.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port)).AsTask().WaitAsync(WaitLimit);
            await act1.Should().ThrowAsync<NetIOException>("错集群——握手拒绝");

            // 冷却窗内：同源重试——预检快速拒绝（不再完整握手——冷却计数递增）
            server.CooldownRejects.Should().BeGreaterThanOrEqualTo(0);
            var before = server.CooldownRejects;
            var clientRetry = new ClusterTransport(NodeId.NewRandom(),
                TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, 0), new Dictionary<NodeId, IPEndPoint>()));
            clientRetry.Start();
            try
            {
                var act = async () => await clientRetry.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port)).AsTask().WaitAsync(WaitLimit);
                await act.Should().ThrowAsync<NetIOException>("冷却窗内同源入站拒绝");
                server.CooldownRejects.Should().BeGreaterThanOrEqualTo(before + 1, "冷却拒绝计数递增");
            }
            finally { await clientRetry.DisposeAsync(); }
        }
        finally { await server.DisposeAsync(); }
    }
}

/// <summary>ClusterTransport → IAsyncDisposable 桥（await using 形态）。</summary>
internal static class ClusterTransportDisposableExtensions
{
    public static IAsyncDisposable AsDisposable(this ClusterTransport transport) => transport;
}
