using System.Net;
using System.Runtime.Versioning;
using FluentAssertions;
using TC.Tier.Core.Net;
using TC.Tier.Core.Net.Channels;
using TC.Tier.Core.Net.Transport;
using TC.Tier.Core.Net.Transport.Quic;
using Xunit;

namespace TC.Tier.Core.Net.Tests.Transport;

/// <summary>
/// QUIC 介质专项测试（spec-11 W5——三形态映射/对称握手/尽力语义/错集群 fail-fast/流式收尾）。
/// ★ 环境门控：MsQuic 运行时依赖（Windows 11 内置；Linux 须 libmsquic）——不可用时静默通过
/// （环境依赖介质测试；QUIC 消费面语义不依赖介质的部分由介质无关测试覆盖）。
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public class QuicTransportTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    /// <summary>MsQuic 可用（不可用 = 本类全部静默通过）。</summary>
    public static bool MsQuicAvailable =>
        System.Net.Quic.QuicListener.IsSupported;

    private sealed class Rig : IAsyncDisposable
    {
        public required QuicTransport Low { get; init; }
        public required QuicTransport High { get; init; }
        public required NodeId LowId { get; init; }
        public required NodeId HighId { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Low.DisposeAsync();
            await High.DisposeAsync();
        }
    }

    /// <summary>两节点装配（High 监听 0 端口回读实际值；Low 拨号表就绪——连接由首次发送惰性触发）。</summary>
    private sealed class ConsoleLogger : TC.Tier.Core.Logging.ILogger
    {
        public void Log(TC.Tier.Core.Logging.LogLevel logLevel, string message, Exception? exception = null)
            => Console.WriteLine($"[{logLevel}] {message}{(exception is null ? "" : $" :: {exception.GetType().Name}: {exception.Message}")}");
        public bool IsEnabled(TC.Tier.Core.Logging.LogLevel logLevel) => true;
    }

    private static Rig CreateRig(uint clusterTag = 0)
    {
        var a = NodeId.NewRandom();
        var b = NodeId.NewRandom();
        var (low, high) = a.CompareTo(b) < 0 ? (a, b) : (b, a);

        var highTransport = new QuicTransport(high,
            TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, 0), new Dictionary<NodeId, IPEndPoint>()) with { ClusterTag = clusterTag });
        highTransport.Start();
        var lowTransport = new QuicTransport(low, TransportOptions.Default(null,
            new Dictionary<NodeId, IPEndPoint> { [high] = highTransport.LocalEndPoint! }) with { ClusterTag = clusterTag });
        lowTransport.Start();
        return new Rig { Low = lowTransport, High = highTransport, LowId = low, HighId = high };
    }

    /// <summary>挂接 PeerConnected 等待（先注册事件——拨号触发前调用）。</summary>
    private static Task WaitPeerAsync(QuicTransport transport, NodeId peer)
    {
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.PeerConnected += id => { if (id == peer) connected.TrySetResult(); };
        return connected.Task;
    }

    /// <summary>对称握手 + PeerConnected 双向事件（连接面语义——首次请求触发惰性拨号）。</summary>
    [Fact]
    public async Task Handshake_PeerConnectedBothSides()
    {
        if (!MsQuicAvailable) return;
        await using var fx = CreateRig();

        var lowUp = WaitPeerAsync(fx.Low, fx.HighId);
        var highUp = WaitPeerAsync(fx.High, fx.LowId);
        _ = fx.Low.SendDatagramAsync(fx.HighId, 0x60, new byte[] { 0x00 }).AsTask();   // 触发惰性拨号（尽力——失败不炸测试）

        await Task.WhenAll(lowUp, highUp).WaitAsync(WaitLimit);
    }

    /// <summary>请求回调：注册 handler → 往返（非空回显 + 空应答两形态）。</summary>
    [Fact]
    public async Task RequestReply_RoundTrip()
    {
        if (!MsQuicAvailable) return;
        await using var fx = CreateRig();

        fx.High.RegisterRequestHandler(0x60, new LambdaRequest((NodeId from, ReadOnlyMemory<byte> payload, IReplyContext reply) =>
        {
            var echoed = new byte[payload.Length];
            payload.CopyTo(echoed);
            _ = reply.ReplyAsync(echoed).AsTask();
        }));
        fx.High.RegisterRequestHandler(0x61, new LambdaRequest((NodeId from, ReadOnlyMemory<byte> payload, IReplyContext reply) =>
        {
            var vt = reply.ReplyAsync(ReadOnlyMemory<byte>.Empty);
            if (!vt.IsCompleted) vt.AsTask().Wait();   // 空应答（写缓冲同步完成常态——有界等待防裸丢弃）
        }));

        var request1 = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var reply1 = await fx.Low.SendRequestAsync(fx.HighId, 0x60, request1).AsTask().WaitAsync(WaitLimit);
        reply1.Should().Equal(request1, "请求载荷逐字节回显");

        var reply2 = await fx.Low.SendRequestAsync(fx.HighId, 0x61, new byte[] { 0x01 }).AsTask().WaitAsync(WaitLimit);
        reply2.Should().BeEmpty("空应答合法");
    }

    /// <summary>数据报：尽力送达——载荷完整到达 handler。</summary>
    [Fact]
    public async Task Datagram_ReachesHandler()
    {
        if (!MsQuicAvailable) return;
        await using var fx = CreateRig();

        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        fx.High.RegisterProtocol(0x62, new LambdaDatagram(payload => received.TrySetResult(payload)));

        await fx.Low.SendDatagramAsync(fx.HighId, 0x62, new byte[] { 0x11, 0x22 });
        var payload = await received.Task.WaitAsync(WaitLimit);
        payload.Should().Equal(new byte[] { 0x11, 0x22 });
    }

    /// <summary>尽力语义：地址表内目标不可达 → SendDatagram 静默（不抛）。</summary>
    [Fact]
    public async Task Datagram_UnreachableTarget_Silent()
    {
        if (!MsQuicAvailable) return;
        var ghost = NodeId.NewRandom();
        var dead = new QuicTransport(NodeId.NewRandom(), TransportOptions.Default(null,
            new Dictionary<NodeId, IPEndPoint> { [ghost] = new IPEndPoint(IPAddress.Loopback, 1) }));   // 端口 1 = 无监听
        dead.Start();
        await using var _ = dead;

        await dead.Invoking(t => t.SendDatagramAsync(ghost, 0x60, new byte[] { 0x01 }).AsTask())
            .Should().NotThrowAsync("尽力送达——对端不可达 = 静默丢弃");
    }

    /// <summary>错集群 fail-fast：ClusterTag 不匹配 → 拨号握手拒绝。</summary>
    [Fact]
    public async Task Handshake_WrongClusterTag_FailsFast()
    {
        if (!MsQuicAvailable) return;
        var a = NodeId.NewRandom();
        var b = NodeId.NewRandom();
        var (low, high) = a.CompareTo(b) < 0 ? (a, b) : (b, a);

        var highTransport = new QuicTransport(high,
            TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, 0), new Dictionary<NodeId, IPEndPoint>()) with { ClusterTag = 7 });
        highTransport.Start();
        await using var __ = highTransport;
        var lowTransport = new QuicTransport(low, TransportOptions.Default(null,
            new Dictionary<NodeId, IPEndPoint> { [high] = highTransport.LocalEndPoint! }) with { ClusterTag = 9 });
        await using var _ = lowTransport;
        lowTransport.Start();

        await lowTransport.Invoking(async t => await t.SendRequestAsync(high, 0x60, new byte[] { 0x01 }))
            .Should().ThrowAsync<NetIOException>("错集群握手 fail-fast");
    }

    /// <summary>流式会话：开流 ↔ acceptor——帧边界保持 + Complete 收尾（枚举自然结束）。</summary>
    [Fact]
    public async Task Stream_FramesRoundTrip_AndComplete()
    {
        if (!MsQuicAvailable) return;
        await using var fx = CreateRig();

        var accepted = new TaskCompletionSource<IWireStream>(TaskCreationOptions.RunContinuationsAsynchronously);
        fx.High.RegisterStreamAcceptor(0x63, new LambdaAcceptor((_, stream) => accepted.TrySetResult(stream)));

        var outbound = await fx.Low.OpenStreamAsync(fx.HighId, 0x63).AsTask().WaitAsync(WaitLimit);
        var inbound = await accepted.Task.WaitAsync(WaitLimit);

        var frames = new List<byte[]> { new byte[] { 0x01 }, new byte[1000], new byte[] { 0xFF } };
        foreach (var frame in frames)
            await outbound.WriteAsync(frame).AsTask().WaitAsync(WaitLimit);
        await outbound.CompleteAsync().AsTask().WaitAsync(WaitLimit);

        var received = new List<byte[]>();
        using var readCts = new CancellationTokenSource(WaitLimit);
        await foreach (var frame in inbound.ReadAllAsync(readCts.Token))
            received.Add(frame.ToArray());
        received.Should().HaveCount(3, "帧边界保持");
        received[0].Should().Equal(frames[0]);
        received[1].Should().HaveCount(1000);
        received[2].Should().Equal(frames[2]);
    }

    private sealed class LambdaDatagram(Action<byte[]> onPayload) : IDatagramHandler
    {
        public void OnDatagram(NodeId from, ReadOnlyMemory<byte> payload) => onPayload(payload.ToArray());
    }

    private sealed class LambdaRequest(Action<NodeId, ReadOnlyMemory<byte>, IReplyContext> on) : IRequestHandler
    {
        public void OnRequest(NodeId from, ReadOnlyMemory<byte> payload, IReplyContext reply) => on(from, payload, reply);
    }

    private sealed class LambdaAcceptor(Action<NodeId, IWireStream> onStream) : IStreamAcceptor
    {
        public void OnStream(NodeId from, IWireStream stream, ReadOnlyMemory<byte> openPayload) => onStream(from, stream);
    }
}
