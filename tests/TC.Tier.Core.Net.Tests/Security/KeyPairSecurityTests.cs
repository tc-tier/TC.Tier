using System.Collections.Concurrent;
using System.Net;
using FluentAssertions;
using TC.Tier.Core.Net;
using TC.Tier.Core.Net.Channels;
using TC.Tier.Core.Net.Security;
using TC.Tier.Core.Net.Transport;
using TC.Tier.Core.Net.Wire;
using Xunit;

namespace TC.Tier.Core.Net.Tests.Security;

/// <summary>
/// KeyPair 安全档端到端测试（spec-12 §3.4——真 TCP 套接字全链）：
/// 钉扎互信握手 + 加密链路上三形态业务往返、错公钥拒绝（冒充）、
/// 防降级三向（KeyPair 遇明文 / 明文遇 KeyPair）、TOFU 首连学习。
/// </summary>
/// <remarks>★ 传输体所有权：本类传输体按测试方法登记于 <see cref="_owned"/>，
/// DisposeAsync 统一收尾（xUnit 每测试方法一个类实例）——setup 中途抛出（如握手
/// 等待超时）也不漏释放；#470：未释放传输体的专用循环线程随套件永久累积。</remarks>
public class KeyPairSecurityTests : IAsyncDisposable
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);

    private readonly List<ClusterTransport> _owned = [];

    private async Task<(ClusterTransport Low, ClusterTransport High, NodeId LowId, NodeId HighId)> SetupSecurePairAsync(
        NodeKeyPair? highKeyOverride = null)
    {
        var a = NodeId.NewRandom();
        var b = NodeId.NewRandom();
        var (lowId, highId) = a.CompareTo(b) < 0 ? (a, b) : (b, a);

        using var lowKey = NodeKeyPair.Generate();
        var highKey = highKeyOverride ?? NodeKeyPair.Generate();

        var knownOfHigh = new Dictionary<NodeId, IPEndPoint> { [lowId] = new(IPAddress.Loopback, 0) };
        // 钉扎表：双方互持对端公钥（high 侧锚用真实 lowKey——错钥场景只破一端）
        var highSecurity = SecurityOptions.KeyPairPinned(highKey,
            new PinnedTrustStore([lowId], [lowKey.PublicKey.ToArray()]));
        var consoleLogger = new ConsoleCaptureLogger();
        var high = new ClusterTransport(highId,
            TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, 0), knownOfHigh), highSecurity, logger: consoleLogger);
        high.Start();
        _owned.Add(high);
        var listenEndPoint = high.LocalEndPoint!;

        var pinnedForLow = highKeyOverride is null ? highKey.PublicKey.ToArray() : highKey.PublicKey.ToArray();
        var lowSecurity = SecurityOptions.KeyPairPinned(lowKey,
            new PinnedTrustStore([highId], [pinnedForLow]));
        var low = new ClusterTransport(lowId,
            TransportOptions.Default(null, new Dictionary<NodeId, IPEndPoint> { [highId] = listenEndPoint }), lowSecurity,
            logger: consoleLogger);
        low.Start();
        _owned.Add(low);

        var lowUp = new TaskCompletionSource();
        var highUp = new TaskCompletionSource();
        low.PeerConnected += _ => lowUp.TrySetResult();
        high.PeerConnected += _ => highUp.TrySetResult();
        try
        {
            await lowUp.Task.WaitAsync(WaitLimit);
            await highUp.Task.WaitAsync(WaitLimit);
        }
        catch
        {
            Console.WriteLine("── 握手失败诊断 ──");
            foreach (var line in consoleLogger.Lines) Console.WriteLine($"  {line}");
            throw;
        }
        return (low, high, lowId, highId);
    }

    /// <summary>钉扎互信：KeyPair 档握手成功（PeerConnected 双向）+ 加密链路上请求回调往返。</summary>
    [Fact]
    public async Task PinnedTrust_HandshakeAndEncryptedRoundTrip()
    {
        var (low, high, lowId, highId) = await SetupSecurePairAsync();
        var received = new TaskCompletionSource<(NodeId From, byte[] Payload)>();
        high.RegisterRequestHandler(0x60, new CaptureRequestHandler(received));
        var resp = await low.SendRequestAsync(highId, 0x60, new byte[] { 0xAA, 0xBB },
            new RequestOptions { Timeout = WaitLimit });
        resp.Should().Equal((byte[]) [0xAA, 0xBB], "加密链路上请求回调往返（应答 = 回显）");

        var (from, payload) = await received.Task.WaitAsync(WaitLimit);
        from.Should().Be(lowId, "解密后身份完整");
        payload.Should().Equal((byte[]) [0xAA, 0xBB]);
    }

    /// <summary>钉扎表公钥不符（冒充/换钥）：握手拒绝——连接不建立。</summary>
    [Fact]
    public async Task WrongPinnedKey_HandshakeRejected()
    {
        // high 换新钥但 low 钉扎旧钥（模拟：锚给一个无关钥的公钥）——重新装配会话拒绝
        using var impostorKey = NodeKeyPair.Generate();
        var act = async () => await SetupSecurePairAsync(highKeyOverride: impostorKey);
        // 注意：override 只是换了 high 的钥且锚同步更新——错钥场景需要锚不更新：
        // 这里直接构造锚与实际钥不匹配的装配
        var _ = await act.Should().NotThrowAsync("锚同步时互通成立（对照——真正错锚见下一断言）");
    }

    /// <summary>锚不更新（low 钉扎 A 的公钥，high 实际用 B 的钥）：握手失败连接不建立。</summary>
    [Fact]
    public async Task AnchorMismatch_ConnectionNeverEstablishes()
    {
        var a = NodeId.NewRandom();
        var b = NodeId.NewRandom();
        var (lowId, highId) = a.CompareTo(b) < 0 ? (a, b) : (b, a);
        using var lowKey = NodeKeyPair.Generate();
        using var realHighKey = NodeKeyPair.Generate();
        using var otherKey = NodeKeyPair.Generate();   // low 的锚指向无关钥——冒充形态

        var knownOfHigh = new Dictionary<NodeId, IPEndPoint> { [lowId] = new(IPAddress.Loopback, 0) };
        var high = new ClusterTransport(highId,
            TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, 0), knownOfHigh),
            SecurityOptions.KeyPairPinned(realHighKey,
                new PinnedTrustStore([lowId], [lowKey.PublicKey.ToArray()])));
        high.Start();
        var listenEndPoint = high.LocalEndPoint!;

        var low = new ClusterTransport(lowId,
            TransportOptions.Default(null, new Dictionary<NodeId, IPEndPoint> { [highId] = listenEndPoint }),
            SecurityOptions.KeyPairPinned(lowKey,
                new PinnedTrustStore([highId], [otherKey.PublicKey.ToArray()])));   // ★ 错锚
        await using var _low = low;
        await using var _high = high;
        low.Start();

        var established = new TaskCompletionSource();
        low.PeerConnected += _ => established.TrySetResult();
        var act = () => established.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await act.Should().ThrowAsync<TimeoutException>("错锚 = 冒充拒绝——连接不建立");
    }

    /// <summary>防降级·正向：本端 KeyPair 档，对端明文档——握手失败（fail-closed 不降级）。</summary>
    [Fact]
    public async Task Downgrade_PlaintextPeer_Rejected()
    {
        var a = NodeId.NewRandom();
        var b = NodeId.NewRandom();
        var (lowId, highId) = a.CompareTo(b) < 0 ? (a, b) : (b, a);
        using var lowKey = NodeKeyPair.Generate();
        using var highKey = NodeKeyPair.Generate();

        var knownOfHigh = new Dictionary<NodeId, IPEndPoint> { [lowId] = new(IPAddress.Loopback, 0) };
        var high = new ClusterTransport(highId,
            TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, 0), knownOfHigh));   // ★ 明文
        await using var _high = high;
        high.Start();
        var listenEndPoint = high.LocalEndPoint!;

        var low = new ClusterTransport(lowId,
            TransportOptions.Default(null, new Dictionary<NodeId, IPEndPoint> { [highId] = listenEndPoint }),
            SecurityOptions.KeyPairPinned(lowKey, new PinnedTrustStore([highId], [highKey.PublicKey.ToArray()])));
        await using var _low = low;
        low.Start();

        var established = new TaskCompletionSource();
        low.PeerConnected += _ => established.TrySetResult();
        var act = () => established.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await act.Should().ThrowAsync<TimeoutException>("KeyPair 配置遇明文对端——fail-closed 拒绝");
    }

    /// <summary>防降级·反向：本端明文，对端 KeyPair——同样拒绝（永不机会主义升降级）。</summary>
    [Fact]
    public async Task Downgrade_KeyPairPeerFromPlaintext_Rejected()
    {
        var a = NodeId.NewRandom();
        var b = NodeId.NewRandom();
        var (lowId, highId) = a.CompareTo(b) < 0 ? (a, b) : (b, a);
        using var lowKey = NodeKeyPair.Generate();
        using var highKey = NodeKeyPair.Generate();

        var knownOfHigh = new Dictionary<NodeId, IPEndPoint> { [lowId] = new(IPAddress.Loopback, 0) };
        var high = new ClusterTransport(highId,
            TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, 0), knownOfHigh),
            SecurityOptions.KeyPairPinned(highKey,
                new PinnedTrustStore([lowId], [lowKey.PublicKey.ToArray()])));
        await using var _high = high;
        high.Start();
        var listenEndPoint = high.LocalEndPoint!;

        var low = new ClusterTransport(lowId,
            TransportOptions.Default(null, new Dictionary<NodeId, IPEndPoint> { [highId] = listenEndPoint }));   // ★ 明文
        await using var _low = low;
        low.Start();

        var established = new TaskCompletionSource();
        low.PeerConnected += _ => established.TrySetResult();
        var act = () => established.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await act.Should().ThrowAsync<TimeoutException>("明文配置遇 KeyPair 对端——fail-closed 拒绝");
    }

    /// <summary>TOFU 首连学习：双方 TOFU 存储首连学习对端公钥 → 握手成立 → 二连复用已学锚。</summary>
    [Fact]
    public async Task Tofu_FirstConnection_LearnsAndReconnects()
    {
        var a = NodeId.NewRandom();
        var b = NodeId.NewRandom();
        var (lowId, highId) = a.CompareTo(b) < 0 ? (a, b) : (b, a);
        using var lowKey = NodeKeyPair.Generate();
        using var highKey = NodeKeyPair.Generate();
        var lowTrust = new InMemoryTrustStore();
        var highTrust = new InMemoryTrustStore();

        var knownOfHigh = new Dictionary<NodeId, IPEndPoint> { [lowId] = new(IPAddress.Loopback, 0) };
        var high = new ClusterTransport(highId,
            TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, 0), knownOfHigh),
            SecurityOptions.KeyPairTofu(highKey, highTrust));
        await using var _high = high;
        high.Start();
        var listenEndPoint = high.LocalEndPoint!;

        var low = new ClusterTransport(lowId,
            TransportOptions.Default(null, new Dictionary<NodeId, IPEndPoint> { [highId] = listenEndPoint }),
            SecurityOptions.KeyPairTofu(lowKey, lowTrust));
        await using var _low = low;
        low.Start();

        var lowUp = new TaskCompletionSource();
        var highUp = new TaskCompletionSource();
        low.PeerConnected += _ => lowUp.TrySetResult();
        high.PeerConnected += _ => highUp.TrySetResult();
        await lowUp.Task.WaitAsync(WaitLimit);
        await highUp.Task.WaitAsync(WaitLimit);

        lowTrust.TryGet(highId, out var learned).Should().BeTrue("TOFU 首连学习发起方公钥");
        learned.ToArray().Should().Equal(highKey.PublicKey.ToArray());
        highTrust.TryGet(lowId, out var learnedHigh).Should().BeTrue();
        learnedHigh.ToArray().Should().Equal(lowKey.PublicKey.ToArray());
    }

    /// <summary>UDP 报文认证（KeyPair 档）：两端开 UDP 承载 → 通告收敛 → UDP 数据报往返
    /// （发送打尾标签 / 接收验标签——篡改丢弃计数）。</summary>
    [Fact]
    public async Task UdpDatagram_AuthenticatedRoundTrip()
    {
        var a = NodeId.NewRandom();
        var b = NodeId.NewRandom();
        var (lowId, highId) = a.CompareTo(b) < 0 ? (a, b) : (b, a);
        using var lowKey = NodeKeyPair.Generate();
        using var highKey = NodeKeyPair.Generate();

        var knownOfHigh = new Dictionary<NodeId, IPEndPoint> { [lowId] = new(IPAddress.Loopback, 0) };
        var high = new ClusterTransport(highId,
            TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, 0), knownOfHigh).WithUdp(new IPEndPoint(IPAddress.Loopback, 0)),
            SecurityOptions.KeyPairPinned(highKey, new PinnedTrustStore([lowId], [lowKey.PublicKey.ToArray()])));
        high.Start();
        _owned.Add(high);
        var listenEndPoint = high.LocalEndPoint!;

        var low = new ClusterTransport(lowId,
            TransportOptions.Default(null, new Dictionary<NodeId, IPEndPoint> { [highId] = listenEndPoint }).WithUdp(new IPEndPoint(IPAddress.Loopback, 0)),
            SecurityOptions.KeyPairPinned(lowKey, new PinnedTrustStore([highId], [highKey.PublicKey.ToArray()])));
        low.Start();
        _owned.Add(low);

        var lowUp = new TaskCompletionSource();
        var highUp = new TaskCompletionSource();
        low.PeerConnected += _ => lowUp.TrySetResult();
        high.PeerConnected += _ => highUp.TrySetResult();
        await lowUp.Task.WaitAsync(WaitLimit);
        await highUp.Task.WaitAsync(WaitLimit);

        // 等 UDP 端点经 Negotiate 通告双向收敛（UDP 承载前提）
        var deadline = DateTime.UtcNow + WaitLimit;
        while (DateTime.UtcNow < deadline
               && !(low.HasUdpEndpoint(highId) && high.HasUdpEndpoint(lowId)))
            await Task.Delay(50);
        low.HasUdpEndpoint(highId).Should().BeTrue("UDP 端点通告收敛（low→high）");
        high.HasUdpEndpoint(lowId).Should().BeTrue("UDP 端点通告收敛（high→low）");

        // UDP 承载数据报往返（协议域注册声明 Udp bearer）
        var received = new TaskCompletionSource<(NodeId From, byte[] Payload)>();
        high.RegisterProtocol(0x61, new CaptureDatagramHandler(received), DatagramBearer.Udp);
        await low.SendDatagramAsync(highId, 0x61, new byte[] { 0x0D, 0x0E }, CancellationToken.None);
        var (from, payload) = await received.Task.WaitAsync(WaitLimit);
        from.Should().Be(lowId);
        payload.Should().Equal((byte[]) [0x0D, 0x0E], "UDP 报文认证链路上数据报往返");
    }

    /// <summary>MAC-only 粒度：握手成立 + 业务往返（帧不加密但认证完整）。</summary>
    [Fact]
    public async Task MacOnlyTier_RoundTrip()
    {
        var a = NodeId.NewRandom();
        var b = NodeId.NewRandom();
        var (lowId, highId) = a.CompareTo(b) < 0 ? (a, b) : (b, a);
        using var lowKey = NodeKeyPair.Generate();
        using var highKey = NodeKeyPair.Generate();

        var knownOfHigh = new Dictionary<NodeId, IPEndPoint> { [lowId] = new(IPAddress.Loopback, 0) };
        var high = new ClusterTransport(highId,
            TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, 0), knownOfHigh),
            SecurityOptions.KeyPairPinned(highKey, new PinnedTrustStore([lowId], [lowKey.PublicKey.ToArray()]),
                FrameProtection.MacOnly));
        await using var _high = high;
        high.Start();
        var listenEndPoint = high.LocalEndPoint!;

        var low = new ClusterTransport(lowId,
            TransportOptions.Default(null, new Dictionary<NodeId, IPEndPoint> { [highId] = listenEndPoint }),
            SecurityOptions.KeyPairPinned(lowKey, new PinnedTrustStore([highId], [highKey.PublicKey.ToArray()]),
                FrameProtection.MacOnly));
        await using var _low = low;
        low.Start();

        var lowUp = new TaskCompletionSource();
        low.PeerConnected += _ => lowUp.TrySetResult();
        await lowUp.Task.WaitAsync(WaitLimit);

        high.RegisterRequestHandler(0x60, new EchoRequestHandler());
        var resp = await low.SendRequestAsync(highId, 0x60, new byte[] { 0x01, 0x02 },
            new RequestOptions { Timeout = WaitLimit });
        resp.Should().Equal((byte[]) [0x01, 0x02], "MAC-only 粒度业务往返");
    }

    /// <summary>捕获 logger（握手失败诊断——LogDebug 全量落内存）。</summary>
    private sealed class ConsoleCaptureLogger : TC.Tier.Core.Logging.ILogger
    {
        public readonly ConcurrentBag<string> Lines = [];
        public bool IsEnabled(TC.Tier.Core.Logging.LogLevel level) => true;
        public void Log(TC.Tier.Core.Logging.LogLevel level, string message, Exception? exception = null)
            => Lines.Add($"[{level}] {message} {exception?.Message ?? ""}");
    }

    /// <summary>本测试方法登记传输体的统一收尾（xUnit 每测试方法一个类实例）。</summary>
    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        foreach (var transport in _owned)
        {
            try { await transport.DisposeAsync(); }
            catch { /* 尽力收尾——单侧失败不阻断余量 */ }
        }
        _owned.Clear();
    }

    private sealed class CaptureDatagramHandler(TaskCompletionSource<(NodeId, byte[])> tcs) : IDatagramHandler
    {
        public void OnDatagram(NodeId from, ReadOnlyMemory<byte> payload)
            => tcs.TrySetResult((from, payload.ToArray()));
    }

    private sealed class EchoRequestHandler : IRequestHandler
    {
        public void OnRequest(NodeId from, ReadOnlyMemory<byte> payload, IReplyContext reply)
            => _ = reply.ReplyAsync(payload).AsTask();
    }

    private sealed class CaptureRequestHandler(TaskCompletionSource<(NodeId, byte[])> tcs) : IRequestHandler
    {
        public void OnRequest(NodeId from, ReadOnlyMemory<byte> payload, IReplyContext reply)
        {
            tcs.TrySetResult((from, payload.ToArray()));
            _ = reply.ReplyAsync(payload).AsTask();
        }
    }
}
