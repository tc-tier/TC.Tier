using System.Net;
using FluentAssertions;
using TC.Tier.Core.Net;
using TC.Tier.Core.Net.Hosting;
using TC.Tier.Core.Net.Channels;
using Xunit;

namespace TC.Tier.Core.Net.Tests.Hosting;

/// <summary>
/// 地址制客户端自动重连（二期-D7 NETGAP-015——验证矩阵 D7 行）：
/// 断链（故障注入分区断开既有链路）→ PeerGone → 代理按退避自动重拨 → 链路重建；
/// 既有语义对照：未启用自动重连 = 调用方自决（无自动重拨）。
/// </summary>
public class NetClientAutoReconnectTests
{
    private sealed class EchoHandler : IRequestHandler
    {
        public void OnRequest(NodeId from, ReadOnlyMemory<byte> payload, IReplyContext reply)
            => _ = reply.ReplyAsync(payload.ToArray()).AsTask();
    }

    private sealed class Counter
    {
        private int _count;
        public void Bump() => Interlocked.Increment(ref _count);
        public long Value => Volatile.Read(ref _count);
    }

    /// <summary>D7：分区断链 → 自动重拨 → 链路重建（PeerConnected 再触发）+ 业务恢复。</summary>
    [Fact]
    public async Task AutoReconnect_LinkDeath_ClientRedialsAndResumes()
    {
        var port = ReservePort();
        var serverId = NodeId.NewRandom();
        var clientId = NodeId.NewRandom();

        var server = new ClusterTransport(serverId,
            TC.Tier.Core.Net.Transport.TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, port),
                new Dictionary<NodeId, IPEndPoint>()));
        server.Start();
        server.RegisterRequestHandler(0x70, new EchoHandler());

        await using var client = await NetClientBuilder.Create(clientId)
            .WithAutoReconnect(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2))
            .Connect(new IPEndPoint(IPAddress.Loopback, port))
            .StartAsync();

        try
        {
            client.RemoteId.Should().Be(serverId, "初始连接建立");

            var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.PeerConnected += _ => reconnected.TrySetResult();

            var goneFired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.PeerGone += _ => goneFired.TrySetResult();

            // 分区断开既有链路（服务端注入——既有链路立即断开）
            server.Faults.Partition([serverId], [clientId]);
            await goneFired.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // 自动重拨：握手帧不注入——分区下链路即可重建
            await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            client.RemoteId.Should().Be(serverId, "重连后对端身份不变");

            // 撤销分区——业务恢复（请求回调照常往返）
            server.Faults.Reset();
            var echoed = await client.SendRequestAsync(serverId, 0x70, new byte[] { 0xD7 })
                .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            echoed.Should().Equal(new byte[] { 0xD7 }, "重连后业务往返恢复");
        }
        finally
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    /// <summary>D7 对照：未启用自动重连——断链后由调用方自决（无自动重拨，PeerConnected 不再触发）。</summary>
    [Fact]
    public async Task NoAutoReconnect_LinkDeath_NoAutoRedial()
    {
        var port = ReservePort();
        var serverId = NodeId.NewRandom();
        var clientId = NodeId.NewRandom();

        var server = new ClusterTransport(serverId,
            TC.Tier.Core.Net.Transport.TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, port),
                new Dictionary<NodeId, IPEndPoint>()));
        server.Start();

        await using var client = await NetClientBuilder.Create(clientId)
            .Connect(new IPEndPoint(IPAddress.Loopback, port))
            .StartAsync();

        try
        {
            client.RemoteId.Should().Be(serverId);

            var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.PeerConnected += _ => reconnected.TrySetResult();

            server.Faults.Partition([serverId], [clientId]);
            await Task.Delay(500);   // 无代理——无自动重拨

            reconnected.Task.IsCompleted.Should().BeFalse("缺省关闭——调用方自决语义保持");
        }
        finally
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    private static int ReservePort()
    {
        var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        socket.Close();
        return port;
    }
}
