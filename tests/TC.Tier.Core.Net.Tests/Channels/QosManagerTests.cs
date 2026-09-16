using System.Collections.Concurrent;
using System.Net;
using FluentAssertions;
using TC.Tier.Core.Net;
using TC.Tier.Core.Net.Channels;
using TC.Tier.Core.Net.Tests.Fixtures;
using TC.Tier.Core.Metrics;
using TC.Tier.Core.Observability;
using TC.Tier.Core.Net.Transport;
using Xunit;

namespace TC.Tier.Core.Net.Tests.Channels;

/// <summary>
/// QoS 限流（二期-E4 NETGAP-032——验证矩阵 E4 行）：令牌桶语义（突发准入→超限拒绝→回填补给）、
/// 域级/来源级两级限流、数据报域限流（超限静默丢弃 + 计数）。
/// </summary>
public class QosManagerTests
{
    /// <summary>E4：令牌桶——突发准入、耗尽拒绝、时间回填。</summary>
    [Fact]
    public async Task TokenBucket_BurstThenRefill()
    {
        var bucket = new TokenBucket(perSecond: 10, burst: 5);
        for (var i = 0; i < 5; i++)
            bucket.TryConsume().Should().BeTrue("突发额度内准入");
        bucket.TryConsume().Should().BeFalse("桶空——拒绝");

        await Task.Delay(150);   // 10/s × 150ms = 1.5 个令牌回填
        bucket.TryConsume().Should().BeTrue("时间回填——可再消费");
        bucket.TryConsume().Should().BeFalse("回填不足两次消费");
    }

    /// <summary>E4：QosManager 两级判定——域级聚合 + 来源级独立桶。</summary>
    [Fact]
    public async Task QosManager_DomainAndSourceLevels()
    {
        var qos = new QosManager();
        var a = NodeId.NewRandom();
        var b = NodeId.NewRandom();
        qos.SetDomainLimit(0x70, perSecond: 2, burst: 2);          // 域聚合：2 突发
        qos.SetSourceLimit(0x70, a, perSecond: 1, burst: 1);       // 来源 A：1 突发

        qos.TryAdmit(0x70, a).Should().BeTrue("A 来源桶 + 域桶均足");
        qos.TryAdmit(0x70, a).Should().BeFalse("A 来源桶耗尽（来源级先拒）");
        qos.TryAdmit(0x70, b).Should().BeTrue("B 走域桶（来源未限）");

        qos.ClearSourceLimit(0x70, a);
        qos.ClearDomainLimit(0x70);
        qos.TryAdmit(0x70, a).Should().BeTrue("清除后不限流");
        await Task.CompletedTask;
    }

    /// <summary>E4 集成：请求域限流——突发内成功、超限请求超时（无应答）、窗口回填后恢复。</summary>
    [Fact]
    public async Task RequestRateLimit_Integration_BurstAndRefill()
    {
        var port = ReservePort();
        var serverId = NodeId.NewRandom();
        var server = new ClusterTransport(serverId,
            TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, port), new Dictionary<NodeId, IPEndPoint>()));
        server.Start();
        server.SetRequestRateLimit(0x70, perSecond: 5, burst: 3);
        server.RegisterRequestHandler(0x70, new EchoHandler());

        var client = new ClusterTransport(NodeId.NewRandom(),
            TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, 0), new Dictionary<NodeId, IPEndPoint>()));
        client.Start();
        try
        {
            await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port)).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

            var ok = 0;
            var dropped = 0;
            for (var i = 0; i < 8; i++)
            {
                try
                {
                    await client.SendRequestAsync(serverId, 0x70, new byte[] { (byte)i },
                        new TC.Tier.Core.Net.Channels.RequestOptions { Timeout = TimeSpan.FromMilliseconds(250) })
                        .AsTask().WaitAsync(TimeSpan.FromSeconds(2));
                    ok++;
                }
                catch (TimeoutException) { dropped++; }
            }

            ok.Should().BeGreaterThanOrEqualTo(1, "突发额度内部分成功");
            dropped.Should().BeGreaterThanOrEqualTo(1, "超限请求无应答超时——限流生效");
            (ok + dropped).Should().Be(8);
        }
        finally
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    /// <summary>E4 集成：数据报域限流——超限静默丢弃 + qos 计数（尽力语义）。</summary>
    [Fact]
    public async Task DatagramRateLimit_Drops_Counted()
    {
        var sink = new RecordingMetricsSink();
        var hub = ObservabilityHub.Create(sink, null, new ObservabilityOptions
        {
            Metrics = new MetricsConfig { Enabled = true, EnableNetMetrics = true },
        });
        var port = ReservePort();
        var serverId = NodeId.NewRandom();
        var server = new ClusterTransport(serverId,
            TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, port), new Dictionary<NodeId, IPEndPoint>()),
            hub: hub);
        server.Start();
        server.SetDatagramRateLimit(0x70, perSecond: 2, burst: 2);

        var client = new ClusterTransport(NodeId.NewRandom(),
            TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, 0), new Dictionary<NodeId, IPEndPoint>()));
        client.Start();
        try
        {
            await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port)).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            for (var i = 0; i < 8; i++)
                await client.SendDatagramAsync(serverId, 0x70, new byte[] { (byte)i }).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(300);   // 丢弃计数异步落

            sink.CountOf("net.datagram_dropped", "reason", "qos").Should()
                .BeGreaterThanOrEqualTo(1, "超限数据报静默丢弃 + qos 计数");
        }
        finally { await client.DisposeAsync(); await server.DisposeAsync(); }
    }

    private sealed class EchoHandler : IRequestHandler
    {
        public void OnRequest(NodeId from, ReadOnlyMemory<byte> payload, IReplyContext reply)
            => _ = reply.ReplyAsync(payload.ToArray()).AsTask();
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
