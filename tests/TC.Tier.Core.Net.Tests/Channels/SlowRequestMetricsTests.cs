using System.Net;
using FluentAssertions;
using TC.Tier.Core.Metrics;
using TC.Tier.Core.Net;
using TC.Tier.Core.Net.Channels;
using TC.Tier.Core.Observability;
using TC.Tier.Core.Net.Transport;
using Xunit;

namespace TC.Tier.Core.Net.Tests.Channels;

/// <summary>
/// 慢请求/慢 handler 面（二期-I3 NETGAP-008 补全——验证矩阵 I3 行）：
/// 请求回调慢应答（分发至 ReplyAsync 超阈值）、流 acceptor 慢回调——
/// 对照：快路径零事件。
/// </summary>
public class SlowRequestMetricsTests
{
    private const byte FastDomain = 0x70;
    private const byte SlowDomain = 0x71;
    private const byte StreamDomain = 0x72;

    private sealed class EchoHandler : IRequestHandler
    {
        public void OnRequest(NodeId from, ReadOnlyMemory<byte> payload, IReplyContext reply)
            => _ = reply.ReplyAsync(payload.ToArray()).AsTask();
    }

    private sealed class DelayedReplyHandler(TimeSpan delay) : IRequestHandler
    {
        public void OnRequest(NodeId from, ReadOnlyMemory<byte> payload, IReplyContext reply)
            => _ = Task.Run(async () =>   // 延迟应答（deferred reply 形态——分发至应答耗时可测）
            {
                try
                {
                    await Task.Delay(delay);
                    await reply.ReplyAsync(payload.ToArray());
                }
                catch { /* 链路已断——尽力语义 */ }
            });
    }

    private sealed class SlowAcceptor(TimeSpan delay) : IStreamAcceptor
    {
        public void OnStream(NodeId from, IWireStream stream, ReadOnlyMemory<byte> openPayload)
            => Thread.Sleep(delay);   // 快进快出契约的违例形态——I3 计数兜底
    }

    /// <summary>I3：慢请求（延迟应答 300ms > 阈值 100ms）计数、快路径零事件、流慢 acceptor 计数。</summary>
    [Fact]
    public async Task SlowRequest_AndSlowStream_Counted()
    {
        var sink = new TC.Tier.Core.Net.Tests.Fixtures.RecordingMetricsSink();
        var hub = ObservabilityHub.Create(sink, null, new ObservabilityOptions
        {
            Metrics = new MetricsConfig { Enabled = true, EnableNetMetrics = true },
        });

        var port = ReservePort();
        var serverId = NodeId.NewRandom();
        var clientId = NodeId.NewRandom();

        var server = new ClusterTransport(serverId,
            TC.Tier.Core.Net.Transport.TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, port),
                new Dictionary<NodeId, IPEndPoint>())
                .WithSlowDispatchThreshold(TimeSpan.FromMilliseconds(100)),
            logger: null, hub: hub);
        server.Start();
        server.RegisterRequestHandler(FastDomain, new EchoHandler());
        server.RegisterRequestHandler(SlowDomain, new DelayedReplyHandler(TimeSpan.FromMilliseconds(300)));
        server.RegisterStreamAcceptor(StreamDomain, new SlowAcceptor(TimeSpan.FromMilliseconds(300)));

        var client = new ClusterTransport(clientId,
            TC.Tier.Core.Net.Transport.TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, 0),
                new Dictionary<NodeId, IPEndPoint>()));
        client.Start();
        try
        {
            await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port)).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

            // 快路径：立即应答——不产生慢事件
            await client.SendRequestAsync(serverId, FastDomain, new byte[] { 0x01 }).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

            // 慢请求：延迟应答 300ms > 100ms 阈值——计数
            await client.SendRequestAsync(serverId, SlowDomain, new byte[] { 0x02 }).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

            // 流慢 acceptor：300ms > 100ms 阈值——计数
            var stream = await client.OpenStreamAsync(serverId, StreamDomain).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await stream.WriteAsync(new byte[] { 0x01 });
            await stream.CompleteAsync();

            await WaitForAsync(() =>
                sink.CountOf("net.slow_request", "protocol", SlowDomain.ToString("X2")) >= 1
                && sink.CountOf("net.slow_stream_accept", "protocol", StreamDomain.ToString("X2")) >= 1,
                TimeSpan.FromSeconds(5));

            // 快路径零慢事件
            sink.CountOf("net.slow_request", "protocol", FastDomain.ToString("X2")).Should().Be(0, "立即应答——无慢事件");
        }
        finally
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
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
