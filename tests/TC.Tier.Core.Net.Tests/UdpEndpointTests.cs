using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using TC.Tier.Core.Net;
using TC.Tier.Core.Net.Channels;
using TC.Tier.Core.Net.Transport;
using TC.Tier.Core.Net.Wire;
using TC.Tier.Core.Primitives;
using Xunit;

namespace TC.Tier.Core.Net.Tests;

using TC.Tier.Core.Net.Tests.Fixtures;

/// <summary>
/// UDP 数据报端点契约测试（spec-11 §1.3/§2.2 bit1/§4 W2——通告协商/承载路由/源准入/畸形丢弃）。
/// <para>全 loopback 真套接字；事件等待带超时（失败快速暴露，不悬挂）。</para>
/// </summary>
public class UdpEndpointTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);

    /// <summary>链路/通告建立时限（判例 2026-09-02：混跑调度毛刺下 loopback 握手与 UDP 端点
    /// 通告可超 5s——传输层退避重试恒会建立——建立等待与消息等待分档）。</summary>
    private static readonly TimeSpan EstablishLimit = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan NoDeliveryWindow = TimeSpan.FromMilliseconds(300);

    // ══ 装配辅助 ══

    private static (NodeId Low, NodeId High) NewOrderedIds()
    {
        var a = NodeId.NewRandom();
        var b = NodeId.NewRandom();
        return a.CompareTo(b) < 0 ? (a, b) : (b, a);
    }

    /// <summary>
    /// 装配一对互联节点（UDP 可选开关）：较大方监听 TCP（port 0）+ 可选 UDP，较小方拨号 + 可选 UDP——
    /// 双向 PeerConnected 且（双端开 UDP 时）UDP 端点互通告后返回。
    /// </summary>
    private static async Task<(ClusterTransport Low, ClusterTransport High, NodeId LowId, NodeId HighId)> SetupPairAsync(
        bool lowUdp = true, bool highUdp = true, int udpMaxDatagramBytes = 1200,
        RecordingMetricsSink? lowSink = null, RecordingMetricsSink? highSink = null)
    {
        var (lowId, highId) = NewOrderedIds();

        var knownOfHigh = new Dictionary<NodeId, IPEndPoint> { [lowId] = new(IPAddress.Loopback, 0) };
        var highOptions = TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, 0), knownOfHigh) with
        {
            UdpListenEndPoint = highUdp ? new IPEndPoint(IPAddress.Loopback, 0) : null,
            UdpMaxDatagramBytes = udpMaxDatagramBytes,
        };
        var high = new ClusterTransport(highId, highOptions, hub: highSink?.ToHub());
        high.Start();
        var listenEndPoint = high.LocalEndPoint!;

        var lowOptions = TransportOptions.Default(null, new Dictionary<NodeId, IPEndPoint> { [highId] = listenEndPoint }) with
        {
            UdpListenEndPoint = lowUdp ? new IPEndPoint(IPAddress.Loopback, 0) : null,
            UdpMaxDatagramBytes = udpMaxDatagramBytes,
        };
        var low = new ClusterTransport(lowId, lowOptions, hub: lowSink?.ToHub());
        low.Start();

        var lowUp = new TaskCompletionSource();
        var highUp = new TaskCompletionSource();
        low.PeerConnected += _ => lowUp.TrySetResult();
        high.PeerConnected += _ => highUp.TrySetResult();
        await lowUp.Task.WaitAsync(EstablishLimit);
        await highUp.Task.WaitAsync(EstablishLimit);

        if (lowUdp && highUdp)
        {
            await WaitUntilAsync(() => low.HasUdpEndpoint(highId) && high.HasUdpEndpoint(lowId), EstablishLimit);
        }

        return (low, high, lowId, highId);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? limit = null)
    {
        var deadline = DateTime.UtcNow + (limit ?? WaitLimit);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("条件未在时限内成立。");
            await Task.Delay(10);
        }
    }

    private static async Task DisposeBothAsync(ClusterTransport? a, ClusterTransport? b)
    {
        if (a is not null) await a.DisposeAsync();
        if (b is not null) await b.DisposeAsync();
    }

    private sealed class CapturingHandler(TaskCompletionSource<(NodeId From, byte[] Payload)> tcs) : IDatagramHandler
    {
        public void OnDatagram(NodeId from, ReadOnlyMemory<byte> payload) => tcs.TrySetResult((from, payload.ToArray()));
    }

    // ══ 契约测试 ══

    [Fact]
    public async Task 双端UDP互联_数据报走UDP往返()
    {
        var lowSink = new RecordingMetricsSink();
        var highSink = new RecordingMetricsSink();
        var (low, high, lowId, highId) = await SetupPairAsync(lowSink: lowSink, highSink: highSink);
        try
        {
            low.UdpLocalEndPoint.Should().NotBeNull();
            high.UdpLocalEndPoint.Should().NotBeNull();

            var received = new TaskCompletionSource<(NodeId From, byte[] Payload)>();
            high.RegisterProtocol(0x64, new CapturingHandler(received), DatagramBearer.Udp);
            var backReceived = new TaskCompletionSource<(NodeId From, byte[] Payload)>();
            low.RegisterProtocol(0x64, new CapturingHandler(backReceived), DatagramBearer.Udp);

            var payload = new byte[] { 1, 2, 3, 4 };
            await low.SendDatagramAsync(highId, 0x64, payload);
            var got = await received.Task.WaitAsync(WaitLimit);
            got.From.Should().Be(lowId);
            got.Payload.Should().Equal(payload);

            var payloadBack = new byte[] { 9, 8, 7 };
            await high.SendDatagramAsync(lowId, 0x64, payloadBack);
            var gotBack = await backReceived.Task.WaitAsync(WaitLimit);
            gotBack.From.Should().Be(highId);
            gotBack.Payload.Should().Equal(payloadBack);

            lowSink.CountOf("net.udp.datagrams_sent").Should().BeGreaterOrEqualTo(1);
            highSink.CountOf("net.udp.datagrams_received").Should().BeGreaterOrEqualTo(1);
            highSink.CountOf("net.udp.datagrams_sent").Should().BeGreaterOrEqualTo(1);
            lowSink.CountOf("net.udp.datagrams_received").Should().BeGreaterOrEqualTo(1);
        }
        finally
        {
            await DisposeBothAsync(low, high);
        }
    }

    [Fact]
    public async Task 单端开UDP_通告不成立_回落TCP承载()
    {
        var lowSink = new RecordingMetricsSink();
        var highSink = new RecordingMetricsSink();
        var (low, high, lowId, highId) = await SetupPairAsync(lowUdp: true, highUdp: false, lowSink: lowSink, highSink: highSink);
        try
        {
            low.UdpLocalEndPoint.Should().NotBeNull();
            high.UdpLocalEndPoint.Should().BeNull();
            low.HasUdpEndpoint(highId).Should().BeFalse("通告协商 = 双方都开才生效（spec-11 §1.4）");

            var received = new TaskCompletionSource<(NodeId From, byte[] Payload)>();
            high.RegisterProtocol(0x64, new CapturingHandler(received), DatagramBearer.Udp);
            var backReceived = new TaskCompletionSource<(NodeId From, byte[] Payload)>();
            low.RegisterProtocol(0x64, new CapturingHandler(backReceived), DatagramBearer.Udp);

            // ★ 尽力送达有界重试（判例 2026-09-02）：数据报投递 = 静默丢弃语义——链路/调度窗口
            //   内单发可能落空（混跑实锤：单发 5s 超时）——重发至到达或时限（回落 TCP 承载不变，
            //   计数断言仍为 0）
            var sendDeadline = DateTime.UtcNow + WaitLimit;
            while (!received.Task.IsCompleted && DateTime.UtcNow < sendDeadline)
            {
                await low.SendDatagramAsync(highId, 0x64, new byte[] { 5, 5 });
                await Task.Delay(100);
            }
            var got = await received.Task.WaitAsync(WaitLimit);
            got.Payload.Should().Equal(new byte[] { 5, 5 });

            var backDeadline = DateTime.UtcNow + WaitLimit;
            while (!backReceived.Task.IsCompleted && DateTime.UtcNow < backDeadline)
            {
                await high.SendDatagramAsync(lowId, 0x64, new byte[] { 6, 6 });
                await Task.Delay(100);
            }
            await backReceived.Task.WaitAsync(WaitLimit);

            lowSink.CountOf("net.udp.datagrams_sent").Should().Be(0, "对端端点未通告——回落 TCP 承载");
            highSink.CountOf("net.udp.datagrams_received").Should().Be(0);
        }
        finally
        {
            await DisposeBothAsync(low, high);
        }
    }

    [Fact]
    public async Task 超单报预算_回落TCP承载()
    {
        var highSink = new RecordingMetricsSink();
        var (low, high, lowId, highId) = await SetupPairAsync(udpMaxDatagramBytes: 64, highSink: highSink);
        try
        {
            var received = new TaskCompletionSource<(NodeId From, byte[] Payload)>();
            high.RegisterProtocol(0x64, new CapturingHandler(received), DatagramBearer.Udp);

            var bigPayload = new byte[200];
            Array.Fill(bigPayload, (byte)7);
            await low.SendDatagramAsync(highId, 0x64, bigPayload);

            var got = await received.Task.WaitAsync(WaitLimit);
            got.Payload.Should().Equal(bigPayload);
            highSink.CountOf("net.udp.datagrams_received").Should().Be(0, "超单报预算——回落 TCP 承载（spec-12 §4.5 不做 IP 分片依赖）");
        }
        finally
        {
            await DisposeBothAsync(low, high);
        }
    }

    [Fact]
    public async Task 未知源UDP报文_丢弃计数不分发()
    {
        var highSink = new RecordingMetricsSink();
        var (low, high, _, highId) = await SetupPairAsync(highSink: highSink);
        try
        {
            var received = new TaskCompletionSource<(NodeId From, byte[] Payload)>();
            high.RegisterProtocol(0x64, new CapturingHandler(received), DatagramBearer.Udp);

            // 陌生端点伪造合法帧（明文模式数据报源不认证——但准入 = 握手通告端点映射，spec-11 §1.3）
            var frame = new byte[FrameCodec.HeaderSize + 4];
            FrameCodec.Encode(FrameKind.Datagram, ChannelIds.Datagram, 0x64, new byte[] { 1, 2, 3, 4 }, frame);
            using var forger = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            await forger.SendAsync(frame, frame.Length, high.UdpLocalEndPoint!);

            await WaitUntilAsync(() => highSink.CountOf("net.datagram_dropped", "reason", "udp_unknown_source") >= 1);
            highSink.CountOf("net.udp.datagrams_received").Should().Be(0);
            received.Task.IsCompleted.Should().BeFalse("未通告端点不分发（防伪造源准入）");
        }
        finally
        {
            await DisposeBothAsync(low, high);
        }
    }

    [Fact]
    public async Task 畸形UDP报文_丢弃计数()
    {
        var highSink = new RecordingMetricsSink();
        var (low, high, _, _) = await SetupPairAsync(highSink: highSink);
        try
        {
            using var noise = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            await noise.SendAsync(new byte[64], 64, high.UdpLocalEndPoint!);   // 全零——头 CRC 必不符

            await WaitUntilAsync(() => highSink.CountOf("net.datagram_dropped", "reason", "udp_malformed") >= 1);
            highSink.CountOf("net.udp.datagrams_received").Should().Be(0);
        }
        finally
        {
            await DisposeBothAsync(low, high);
        }
    }

    [Fact]
    public async Task 分区注入_UDP承载同面拦截()
    {
        var lowSink = new RecordingMetricsSink();
        var (low, high, lowId, highId) = await SetupPairAsync(lowSink: lowSink);
        try
        {
            var received = new TaskCompletionSource<(NodeId From, byte[] Payload)>();
            high.RegisterProtocol(0x64, new CapturingHandler(received), DatagramBearer.Udp);
            low.RegisterProtocol(0x64, new CapturingHandler(new TaskCompletionSource<(NodeId, byte[])>()), DatagramBearer.Udp);

            low.Faults.Partition(new[] { lowId }, new[] { highId });

            await low.SendDatagramAsync(highId, 0x64, new byte[] { 1 });
            await low.SendDatagramAsync(highId, 0x64, new byte[] { 2 });

            await Assert.ThrowsAsync<TimeoutException>(
                async () => await received.Task.WaitAsync(NoDeliveryWindow));
            lowSink.CountOf("net.udp.datagrams_sent").Should().Be(0, "分区拦截在发送路径——注入面与 TCP 同一实例语义");
        }
        finally
        {
            await DisposeBothAsync(low, high);
        }
    }
}
