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
using static TC.Tier.Core.Net.Tests.Fixtures.ClusterTransportRig;

/// <summary>
/// ClusterTransport TCP 介质契约测试（spec-11 §2/§3——互联/尽力送达/注入矩阵/协议违规拒绝）。
/// <para>全 loopback 真套接字；事件等待带超时（失败快速暴露，不悬挂）。</para>
/// </summary>
public class ClusterTransportTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);

    // ══ 装配辅助 ══




    private sealed class CapturingHandler(TaskCompletionSource<(NodeId From, byte[] Payload)> tcs) : IDatagramHandler
    {
        public void OnDatagram(NodeId from, ReadOnlyMemory<byte> payload) => tcs.TrySetResult((from, payload.ToArray()));
    }

    private sealed class SleepingHandler : IDatagramHandler
    {
        public void OnDatagram(NodeId from, ReadOnlyMemory<byte> payload) => Thread.Sleep(30);
    }

    // ══ 互联与数据报 ══

    [Fact]
    public async Task 互联_双向PeerConnected_数据报双向往返()
    {
        var (low, high, lowId, highId) = await SetupPairAsync();
        try
        {
            var received = new TaskCompletionSource<(NodeId From, byte[] Payload)>();
            high.RegisterProtocol(0x64, new CapturingHandler(received));
            var backReceived = new TaskCompletionSource<(NodeId From, byte[] Payload)>();
            low.RegisterProtocol(0x64, new CapturingHandler(backReceived));

            var payload = new byte[] { 1, 2, 3, 4 };
            await low.SendDatagramAsync(highId, 0x64, payload);
            var got = await received.Task.WaitAsync(WaitLimit);
            got.From.Should().Be(lowId);
            got.Payload.Should().Equal(payload);

            var payloadBack = new byte[] { 9, 8 };
            await high.SendDatagramAsync(lowId, 0x64, payloadBack);
            var gotBack = await backReceived.Task.WaitAsync(WaitLimit);
            gotBack.From.Should().Be(highId);
            gotBack.Payload.Should().Equal(payloadBack);
        }
        finally
        {
            await DisposeBothAsync(low, high);
        }
    }

    [Fact]
    public async Task 大载荷_1MB_往返完整()
    {
        var (low, high, _, highId) = await SetupPairAsync();
        try
        {
            var received = new TaskCompletionSource<(NodeId From, byte[] Payload)>();
            high.RegisterCoreProtocol(ProtocolIds.Raft, new CapturingHandler(received));

            var payload = new byte[1024 * 1024];
            Random.Shared.NextBytes(payload);
            await low.SendDatagramAsync(highId, ProtocolIds.Raft, payload);

            var got = await received.Task.WaitAsync(WaitLimit);
            got.Payload.Should().Equal(payload);
        }
        finally
        {
            await DisposeBothAsync(low, high);
        }
    }

    [Fact]
    public async Task 发送_对端未连接_静默丢弃并计数()
    {
        var sink = new RecordingMetricsSink();
        var transport = new ClusterTransport(NodeId.NewRandom(), TransportOptions.Default(null, new Dictionary<NodeId, IPEndPoint>()), hub: sink.ToHub());
        try
        {
            transport.Start();
            await transport.SendDatagramAsync(NodeId.NewRandom(), ProtocolIds.Raft, new byte[] { 1 });
            sink.CountOf("net.datagram_dropped", "reason", "no_link").Should().Be(1);
        }
        finally
        {
            await transport.DisposeAsync();
        }
    }

    [Fact]
    public async Task 未注册协议域_丢弃计数_连接保持可用()
    {
        var highSink = new RecordingMetricsSink();
        var (low, high, lowId, highId) = await SetupPairAsync(highSink: highSink);
        try
        {
            var received = new TaskCompletionSource<(NodeId From, byte[] Payload)>();
            high.RegisterProtocol(0x64, new CapturingHandler(received));

            await low.SendDatagramAsync(highId, 0x90, new byte[] { 7 });    // 0x90 未注册
            await Task.Delay(200);                                          // 错误分发时间窗
            highSink.CountOf("net.datagram_dropped", "reason", "unknown_protocol").Should().BeGreaterThanOrEqualTo(1);

            await low.SendDatagramAsync(highId, 0x64, new byte[] { 7 });    // 连接仍可用
            (await received.Task.WaitAsync(WaitLimit)).Payload.Should().Equal(new byte[] { 7 });
        }
        finally
        {
            await DisposeBothAsync(low, high);
        }
    }

    [Fact]
    public async Task 协议域_重复注册_抛()
    {
        var transport = new ClusterTransport(NodeId.NewRandom(), TransportOptions.Default(null, new Dictionary<NodeId, IPEndPoint>()));
        try
        {
            var tcs = new TaskCompletionSource<(NodeId, byte[])>();
            transport.RegisterProtocol(0x64, new CapturingHandler(tcs));
            var act = () => transport.RegisterProtocol(0x64, new CapturingHandler(tcs));
            act.Should().Throw<InvalidOperationException>();
        }
        finally
        {
            await transport.DisposeAsync();
        }
    }

    // ══ 注入矩阵（spec-09 统一面——TCP 等价复用）══

    [Fact]
    public async Task 分区注入_PeerGone_拒拨_Reset后重连()
    {
        var lowSink = new RecordingMetricsSink();
        var (low, high, lowId, highId) = await SetupPairAsync(lowSink: lowSink);
        try
        {
            var lowGone = new TaskCompletionSource();
            var highGone = new TaskCompletionSource();
            var lowReUp = new TaskCompletionSource();
            low.PeerGone += _ => lowGone.TrySetResult();
            high.PeerGone += _ => highGone.TrySetResult();
            low.PeerConnected += _ => lowReUp.TrySetResult();

            low.Faults.Partition([lowId], [highId]);
            await lowGone.Task.WaitAsync(WaitLimit);
            await highGone.Task.WaitAsync(WaitLimit);

            low.Faults.Reset();
            await lowReUp.Task.WaitAsync(TimeSpan.FromSeconds(10));   // 退避 ≤ 初值 + 拨号 + 握手
            lowSink.CountOf("net.reconnects").Should().BeGreaterThanOrEqualTo(1);
        }
        finally
        {
            await DisposeBothAsync(low, high);
        }
    }

    [Fact]
    public async Task 丢包注入_全丢_Reset后可达()
    {
        var (low, high, lowId, highId) = await SetupPairAsync();
        try
        {
            var received = new TaskCompletionSource<(NodeId From, byte[] Payload)>();
            high.RegisterProtocol(0x64, new CapturingHandler(received));

            low.Faults.Drop(lowId, highId, 1.0);
            await low.SendDatagramAsync(highId, 0x64, new byte[] { 1 });
            await Assert.ThrowsAsync<TimeoutException>(
                () => received.Task.WaitAsync(TimeSpan.FromMilliseconds(400)));

            low.Faults.Reset();
            await low.SendDatagramAsync(highId, 0x64, new byte[] { 2 });
            (await received.Task.WaitAsync(WaitLimit)).Payload.Should().Equal(new byte[] { 2 });
        }
        finally
        {
            await DisposeBothAsync(low, high);
        }
    }

    [Fact]
    public async Task 延迟注入_到达且延迟生效()
    {
        var (low, high, lowId, highId) = await SetupPairAsync();
        try
        {
            var received = new TaskCompletionSource<(NodeId From, byte[] Payload)>();
            high.RegisterProtocol(0x64, new CapturingHandler(received));

            low.Faults.SetLatency(lowId, highId, TimeSpan.FromMilliseconds(150));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await low.SendDatagramAsync(highId, 0x64, new byte[] { 5 });
            await received.Task.WaitAsync(WaitLimit);
            sw.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(120));
        }
        finally
        {
            await DisposeBothAsync(low, high);
        }
    }

    [Fact]
    public async Task 乱序注入_消息全达()
    {
        var (low, high, lowId, highId) = await SetupPairAsync();
        try
        {
            var sink = new ConcurrentBag<byte>();
            var allReceived = new TaskCompletionSource();
            const int count = 8;
            high.RegisterProtocol(0x64, new CollectingHandler(sink, allReceived, count));
            low.Faults.Reorder(lowId, highId, true);

            for (byte i = 0; i < count; i++)
            {
                byte[] one = [i];
                await low.SendDatagramAsync(highId, 0x64, one);
            }

            await allReceived.Task.WaitAsync(WaitLimit);
            sink.Should().HaveCount(count);
            sink.Distinct().Should().HaveCount(count);   // 无丢失
        }
        finally
        {
            await DisposeBothAsync(low, high);
        }
    }

    [Fact]
    public async Task 分发慢回调_计数兜底()
    {
        var highSink = new RecordingMetricsSink();
        var (low, high, lowId, highId) = await SetupPairAsync(highSink: highSink);
        try
        {
            high.RegisterProtocol(0x64, new SleepingHandler());   // 30ms > 10ms 阈值
            await low.SendDatagramAsync(highId, 0x64, new byte[] { 1 });
            await Task.Delay(200);
            highSink.CountOf("net.slow_dispatch").Should().BeGreaterThanOrEqualTo(1);
        }
        finally
        {
            await DisposeBothAsync(low, high);
        }
    }

    // ══ 协议违规拒绝（原始套接字——明文形态的线级行为）══

    private static NodeId NewSmallerIdThan(NodeId other)
    {
        NodeId id;
        do { id = NodeId.NewRandom(); } while (id.CompareTo(other) >= 0);
        return id;
    }

    private static byte[] BuildFrame(byte kind, byte channel, byte protocol, byte[] payload)
    {
        var frame = new byte[FrameCodec.HeaderSize + payload.Length];
        FrameCodec.Encode(kind, channel, protocol, payload, frame);
        return frame;
    }

    private static async Task<(byte Kind, byte[] Payload)> ReadRawFrameAsync(NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[FrameCodec.HeaderSize];
        var read = await stream.ReadAsync(header, ct);
        if (read < FrameCodec.HeaderSize) return (0, []);
        if (!FrameCodec.TryReadHeader(header, out var hdr)) return (0, []);
        var payload = new byte[hdr.PayloadLength];
        var total = 0;
        while (total < payload.Length)
        {
            var n = await stream.ReadAsync(payload.AsMemory(total), ct);
            if (n <= 0) break;
            total += n;
        }
        return (hdr.Kind, payload);
    }

    [Fact]
    public async Task 握手前数据帧_Error断连()
    {
        var (rawId, highId) = NewOrderedIds();
        var known = new Dictionary<NodeId, IPEndPoint> { [rawId] = new(IPAddress.Loopback, 0) };
        var high = new ClusterTransport(highId, TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, 0), known));
        high.Start();
        try
        {
            using var raw = new TcpClient();
            raw.Connect(high.LocalEndPoint!);
            raw.NoDelay = true;
            var stream = raw.GetStream();
            using var cts = new CancellationTokenSource(WaitLimit);

            await stream.WriteAsync(BuildFrame(FrameKind.Datagram, ChannelIds.Datagram, 0x64, [1]), cts.Token);
            var (kind, payload) = await ReadRawFrameAsync(stream, cts.Token);
            kind.Should().Be(FrameKind.Error);
            payload[0].Should().Be(TransportError.HandshakeViolation);

            var eof = await stream.ReadAsync(new byte[1], cts.Token);
            eof.Should().Be(0);   // 断连
        }
        finally
        {
            await high.DisposeAsync();
        }
    }

    [Fact]
    public async Task 版本无交集_Error拒绝断连()
    {
        var (rawId, highId) = NewOrderedIds();
        var known = new Dictionary<NodeId, IPEndPoint> { [rawId] = new(IPAddress.Loopback, 0) };
        var high = new ClusterTransport(highId, TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, 0), known));
        high.Start();
        try
        {
            using var raw = new TcpClient();
            raw.Connect(high.LocalEndPoint!);
            raw.NoDelay = true;
            var stream = raw.GetStream();
            using var cts = new CancellationTokenSource(WaitLimit);

            var initPayload = new byte[HandshakeCodec.InitPayloadSize];
            HandshakeCodec.WriteInit(initPayload, rawId, 5, 7, 0, 0, 0xAB, HandshakeSecurity.Plaintext);
            await stream.WriteAsync(BuildFrame(FrameKind.HandshakeInit, ChannelIds.Management, ProtocolIds.Management, initPayload), cts.Token);

            var (kind, payload) = await ReadRawFrameAsync(stream, cts.Token);
            kind.Should().Be(FrameKind.Error);
            payload[0].Should().Be(TransportError.VersionRejected);
        }
        finally
        {
            await high.DisposeAsync();
        }
    }

    [Fact]
    public async Task Dispose后发送_抛ObjectDisposed()
    {
        var transport = new ClusterTransport(NodeId.NewRandom(), TransportOptions.Default(null, new Dictionary<NodeId, IPEndPoint>()));
        await transport.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await transport.SendDatagramAsync(NodeId.NewRandom(), 0x64, new byte[] { 1 }));
    }

    [Fact]
    public async Task ClusterTagMismatch_HandshakeRejected()
    {
        var (rawId, highId) = NewOrderedIds();
        var known = new Dictionary<NodeId, IPEndPoint> { [rawId] = new(IPAddress.Loopback, 0) };
        var highOptions = TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, 0), known) with { ClusterTag = 0x11111111 };
        var high = new ClusterTransport(highId, highOptions);
        high.Start();
        try
        {
            using var raw = new TcpClient();
            raw.Connect(high.LocalEndPoint!);
            raw.NoDelay = true;
            var stream = raw.GetStream();
            using var cts = new CancellationTokenSource(WaitLimit);

            var initPayload = new byte[HandshakeCodec.InitPayloadSize];
            HandshakeCodec.WriteInit(initPayload, rawId, 1, 1, 0, 0x22222222, 0xAB, HandshakeSecurity.Plaintext);
            await stream.WriteAsync(BuildFrame(FrameKind.HandshakeInit, ChannelIds.Management, ProtocolIds.Management, initPayload), cts.Token);

            var (kind, payload) = await ReadRawFrameAsync(stream, cts.Token);
            kind.Should().Be(FrameKind.Error);
            payload[0].Should().Be(TransportError.HandshakeViolation, "错集群标签——握手期 fail-fast（spec-12 §3.3）");
        }
        finally
        {
            await high.DisposeAsync();
        }
    }

    // ══ 寻址两制与准入分层（spec-12 §4.1/§4.3——连接准入 ≠ 成员准入；地址制直连）══

    /// <summary>装配直连对：A 监听（地址表空——B 不在成员表），B 地址制拨号。B 的 ID 有意取较大侧。</summary>
    private static async Task<(ClusterTransport Server, ClusterTransport Client, NodeId ServerId, NodeId ClientId)> SetupDirectPairAsync(
        bool clientIdLarger, RecordingMetricsSink? clientSink = null)
    {
        var (low, high) = NewOrderedIds();
        var serverId = clientIdLarger ? low : high;
        var clientId = clientIdLarger ? high : low;

        var server = new ClusterTransport(serverId, TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, 0), new Dictionary<NodeId, IPEndPoint>()));
        server.Start();
        var client = new ClusterTransport(clientId, TransportOptions.Default(null, new Dictionary<NodeId, IPEndPoint>()), hub: clientSink?.ToHub());
        client.Start();

        var serverUp = new TaskCompletionSource<NodeId>();
        server.PeerConnected += id => serverUp.TrySetResult(id);
        var connectedId = await client.ConnectAsync(server.LocalEndPoint!);
        connectedId.Should().Be(serverId, "地址制对端身份握手得知（§4.1——此前未知）");
        (await serverUp.Task.WaitAsync(WaitLimit)).Should().Be(clientId);
        return (server, client, serverId, clientId);
    }

    [Theory]
    [InlineData(true)]    // 客户端 ID 较大——地址制不受拨号归属 NodeId 大小约束（§4.3）
    [InlineData(false)]
    public async Task 直连_握手与数据报双向往返(bool clientIdLarger)
    {
        var (server, client, serverId, clientId) = await SetupDirectPairAsync(clientIdLarger);
        try
        {
            var atServer = new TaskCompletionSource<(NodeId From, byte[] Payload)>();
            server.RegisterProtocol(0x64, new CapturingHandler(atServer));
            var atClient = new TaskCompletionSource<(NodeId From, byte[] Payload)>();
            client.RegisterProtocol(0x64, new CapturingHandler(atClient));

            await client.SendDatagramAsync(serverId, 0x64, new byte[] { 3, 1 });
            // ★ 尽力送达有界重试（判例 2026-09-02：链路刚建立的数据报单发可被调度窗口吞没——
            //   混跑实锤 5s 超时；重发至到达或时限）
            var sendDeadline = DateTime.UtcNow + WaitLimit;
            while (!atServer.Task.IsCompleted && DateTime.UtcNow < sendDeadline)
            {
                await client.SendDatagramAsync(serverId, 0x64, new byte[] { 3, 1 });
                await Task.Delay(100);
            }
            var got = await atServer.Task.WaitAsync(WaitLimit);
            got.From.Should().Be(clientId);
            got.Payload.Should().Equal(new byte[] { 3, 1 });

            await server.SendDatagramAsync(clientId, 0x64, new byte[] { 4, 2 });   // 服务端凭链路表定向回发（客户端不在地址表）
            var back = await atClient.Task.WaitAsync(WaitLimit);
            back.From.Should().Be(serverId);
            back.Payload.Should().Equal(new byte[] { 4, 2 });
        }
        finally
        {
            await DisposeBothAsync(server, client);
        }
    }

    [Fact]
    public async Task 直连_域名端点_DnsEndPoint握手()
    {
        var clientSink = new RecordingMetricsSink();
        // ★ localhost 双栈解析（::1 回退 127.0.0.1）满载下可超缺省 3s 握手超时——专用装配放宽到 10s
        var serverId = NodeId.NewRandom();
        var server = new ClusterTransport(serverId,
            TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, 0), new Dictionary<NodeId, IPEndPoint>()));
        server.Start();
        var listenPort = ((IPEndPoint)server.LocalEndPoint!).Port;
        var clientOpts = TransportOptions.Default(null, new Dictionary<NodeId, IPEndPoint>())
            .WithHandshakeTimeout(TimeSpan.FromSeconds(10));
        var client = new ClusterTransport(NodeId.NewRandom(), clientOpts, hub: clientSink?.ToHub());
        client.Start();
        try
        {
            var connected = await client.ConnectAsync(new DnsEndPoint("localhost", listenPort));
            connected.Should().Be(serverId, "域名端点直连（§4.1 寻址 = IP/域名）——同一监听口替换旧链路");
        }
        finally
        {
            await DisposeBothAsync(server, client);
        }
    }

    [Fact]
    public async Task 直连_错ClusterTag_抛NetIOException()
    {
        var (serverId, clientId) = NewOrderedIds();
        var server = new ClusterTransport(serverId,
            TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, 0), new Dictionary<NodeId, IPEndPoint>()) with { ClusterTag = 0x11111111 });
        var client = new ClusterTransport(clientId,
            TransportOptions.Default(null, new Dictionary<NodeId, IPEndPoint>()) with { ClusterTag = 0x22222222 });
        server.Start();
        client.Start();
        try
        {
            var act = () => client.ConnectAsync(server.LocalEndPoint!).AsTask();
            (await act.Should().ThrowAsync<NetIOException>())
                .Which.Message.Should().Contain("集群标签", "错集群直连 fail-fast——对端 Error 细节外泄给调用方（§3.3）");
        }
        finally
        {
            await DisposeBothAsync(server, client);
        }
    }

    [Fact]
    public async Task 成员制_较大方入站_握手期拒绝()
    {
        var (lowId, highId) = NewOrderedIds();
        // 服务端 = 较小方（成员制应由它拨号）；客户端 = 较大方且在服务端地址表——入站违规
        var known = new Dictionary<NodeId, IPEndPoint> { [highId] = new(IPAddress.Loopback, 0) };
        var clientSink = new RecordingMetricsSink();
        var server = new ClusterTransport(lowId, TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, 0), known));
        var client = new ClusterTransport(highId, TransportOptions.Default(null, new Dictionary<NodeId, IPEndPoint>()), hub: clientSink.ToHub());
        server.Start();
        client.Start();
        try
        {
            var act = () => client.ConnectAsync(server.LocalEndPoint!).AsTask();
            (await act.Should().ThrowAsync<NetIOException>())
                .Which.Message.Should().Contain("归属", "成员制较大方入站 = 双向对拨违规——握手期 Error + 断连（§4.3）");
            clientSink.CountOf("net.reconnects").Should().Be(0, "地址制直连失败不进自动重连（重连退避只属成员制拨号循环）");
        }
        finally
        {
            await DisposeBothAsync(server, client);
        }
    }

    [Fact]
    public async Task 直连_断开_不自动重连()
    {
        var clientSink = new RecordingMetricsSink();
        var (server, client, serverId, _) = await SetupDirectPairAsync(clientIdLarger: false, clientSink: clientSink);
        try
        {
            var clientGone = new TaskCompletionSource();
            var reUp = new TaskCompletionSource();
            client.PeerGone += _ => clientGone.TrySetResult();
            client.PeerConnected += _ => reUp.TrySetResult();

            await server.DisposeAsync();   // 服务端下线——链路断开
            await clientGone.Task.WaitAsync(WaitLimit);

            await Task.Delay(300);   // 观察窗：地址制断开无自动重拨
            reUp.Task.IsCompleted.Should().BeFalse("地址制链路断开由调用方自决重拨（§4.1）——传输不自动重连");
            clientSink.CountOf("net.reconnects").Should().Be(0);
        }
        finally
        {
            await DisposeBothAsync(null, client);
        }
    }
}
