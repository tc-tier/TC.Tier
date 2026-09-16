using System.Net.Sockets;
using FluentAssertions;
using TC.Tier.Core.Net;
using TC.Tier.Core.Net.Channels;
using TC.Tier.Core.Net.Transport.Uds;
using Xunit;
using Skip = Xunit.Skip;

namespace TC.Tier.Core.Net.Tests.Transport;

/// <summary>
/// UDS 同机控制面介质（二期-E7——DDR-E7 验收清单）：
/// ① 点对点请求回调往返（echo）；② 并发在途 CorrId 配对；
/// ③ 核心区放行 + 公开口注册区校验 + 非承载面拒绝；④ Dispose unlink socket 文件。
/// </summary>
public class UdsTransportTests : IAsyncDisposable
{
    private const byte Domain = 0x74;   // 注册区（0x60-0xAF 使用方自管号——测试域）
    private readonly List<UdsTransport> _transports = new();
    // ★ GUID 截 16 hex：macOS UDS 路径上限 104 字符（/var/folders 长前缀 + 32 位 GUID 必爆——CI macos 实咬）
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tctier-uds-" + Guid.NewGuid().ToString("N")[..16]);

    /// <summary>UDS bind 能力探测（沙箱/平台可能限制 AF_UNIX——受限则跳过验收；路径同上截短口径）。</summary>
    private static bool UdsBindSupported()
    {
        try
        {
            var probe = Path.Combine(Path.GetTempPath(), "tctier-uds-cap-" + Guid.NewGuid().ToString("N")[..16]);
            var l = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            l.Bind(new UnixDomainSocketEndPoint(probe));
            l.Dispose();
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    private sealed class FuncHandler(Func<byte, byte> respond) : IRequestHandler
    {
        public void OnRequest(NodeId from, ReadOnlyMemory<byte> payload, IReplyContext reply)
            => _ = reply.ReplyAsync(new[] { respond(payload.Length > 0 ? payload.Span[0] : (byte)0) }).AsTask();
    }

    private string PathOf(string name) => Path.Combine(_dir, name + ".sock");

    private readonly Dictionary<string, (UdsTransport Transport, NodeId Id)> _servers = new();
    private readonly Dictionary<string, string> _pathsByName = new();

    private UdsTransport CreateServer(string name, Func<byte, byte> respond)
    {
        Directory.CreateDirectory(_dir);
        var path = PathOf(name);
        var t = new UdsTransport(NodeId.NewRandom(), path);
        t.RegisterRequestHandler(Domain, new FuncHandler(respond));
        t.Start();
        _pathsByName[name] = path;
        _transports.Add(t);
        return t;
    }

    private UdsTransport CreateClient(string serverName, NodeId serverId)
    {
        var t = new UdsTransport(NodeId.NewRandom(), null,
            new Dictionary<NodeId, string> { [serverId] = PathOf(serverName) });
        // 占位——PeerPaths 在下述覆盖（对端 ID 于 Server 创建后得知）
        return t;
    }

    /// <summary>探针：UDS bind 建文件 + connect 可达（定位 EADDRNOTAVAIL）。</summary>
    [SkippableFact]
    public async Task Probe_UdsBindConnect()
    {
        Skip.If(!UdsBindSupported(), "沙箱不支持 AF_UNIX bind");
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "probe.sock");
        var l = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        l.Bind(new UnixDomainSocketEndPoint(path));
        l.Listen(1);
        File.Exists(path).Should().BeTrue("bind 创建 socket 文件");
        var c = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await c.ConnectAsync(new UnixDomainSocketEndPoint(path)).WaitAsync(TimeSpan.FromSeconds(2));
        c.Connected.Should().BeTrue("UDS connect 可达");
        c.Dispose(); l.Dispose();
        File.Delete(path);
    }

    /// <summary>验收①：点对点请求回调往返（echo 形态）。</summary>
    [SkippableFact]
    public async Task PointToPoint_RequestRoundTrip()
    {
        Skip.If(!UdsBindSupported(), "沙箱不支持 AF_UNIX bind");
        var server = CreateServer("srv", v => (byte)(v + 1));
        var client = CreateClient("srv", server.Self);

        var resp = await client.SendRequestAsync(server.Self, Domain, new byte[] { 0x2A })
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        resp.Should().Equal(new byte[] { 0x2B }, "echo +1 语义（回程经同 socket）");
    }

    /// <summary>验收①补（#415 回归门）：应答帧恰一次——原始 socket 客户端读完应答帧后再读
    /// 必须超时（无第二帧）。旧实现双重回写（ReplyAsync 与 InboundLoop 各写一次同一帧），
    /// 长连接复用形态下重复帧使 [CorrId][len] 逐帧解析失配。</summary>
    [SkippableFact]
    public async Task ReplyFrame_WrittenExactlyOnce_NoDuplicateFrame()
    {
        Skip.If(!UdsBindSupported(), "沙箱不支持 AF_UNIX bind");
        var server = CreateServer("srv1x", v => v);

        // 原始 socket 客户端（不经 SendRequestAsync——它每请求一连接读一帧即弃，测不出重复帧）
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.ReceiveTimeout = 800;   // 双写回归时立即读到重复帧；修复后此处超时
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(PathOf("srv1x")));

        var corrId = 0x1234UL;
        var frame = new byte[29 + 1];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(frame, corrId);
        server.Self.CopyTo(frame.AsSpan(8, 16));   // From 16B（NodeId.CopyTo——CopyFrom 逆变换）
        frame[24] = Domain;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(25), 1);
        frame[29] = 0x11;
        await socket.SendAsync(frame, SocketFlags.None);

        // 第一帧：应答帧头 + payload（修复后 ReplyAsync 唯一写——必到）
        var header = new byte[12];
        await ReadExact(socket, header);
        var len = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
        len.Should().Be(1, "应答帧 [CorrId 8B][len 4B] 头解析");
        var payload = new byte[len];
        await ReadExact(socket, payload);
        payload.Should().Equal(new byte[] { 0x11 });

        // 第二读：必须无重复帧——双写回归时 ReceiveAsync 立即返回 1 字节重复帧；
        // 修复后应答唯一写，第二读只可能超时（无数据）或读到 0（对端关闭）——两者均证明无重复帧。
        var probe = new byte[1];
        try
        {
            var second = await socket.ReceiveAsync(probe, SocketFlags.None)
                .WaitAsync(TimeSpan.FromMilliseconds(800));
            second.Should().BeLessThanOrEqualTo(0,
                $"读到 {second} 字节 = 重复帧存在（双写回归）");
        }
        catch (TimeoutException)
        {
            // 预期路径：800ms 无第二帧——应答恰一次
        }
    }

    private static async Task ReadExact(Socket socket, byte[] buffer)
    {
        var got = 0;
        while (got < buffer.Length)
        {
            var n = await socket.ReceiveAsync(buffer.AsMemory(got), SocketFlags.None);
            if (n == 0) throw new InvalidOperationException("对端关闭（期望更多字节）");
            got += n;
        }
    }

    /// <summary>验收②：并发 3 在途——CorrId 配对正确（互不串扰）。</summary>
    [SkippableFact]
    public async Task ConcurrentInFlight_CorrelationPaired()
    {
        Skip.If(!UdsBindSupported(), "沙箱不支持 AF_UNIX bind");
        var server = CreateServer("srv2", v => v);
        var client = CreateClient("srv2", server.Self);

        var tasks = new List<Task<byte[]>>();
        for (var i = 0; i < 3; i++)
        {
            var v = (byte)(i + 1);
            tasks.Add(client.SendRequestAsync(server.Self, Domain, new byte[] { v })
                .AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        }
        var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));

        results.Should().HaveCount(3);
        for (var i = 0; i < 3; i++)
            results[i].Should().Equal(new byte[] { (byte)(i + 1) }, $"CorrId 配对不串扰（item {i}）");
    }

    /// <summary>验收③：核心区放行 + 公开口注册区校验 + 非承载面拒绝。</summary>
    [SkippableFact]
    public void Registration_Faces_CorePortAndUserPort()
    {
        Skip.If(!UdsBindSupported(), "沙箱不支持 AF_UNIX bind");
        var server = CreateServer("reg", v => v);

        var actCore = () => server.RegisterCoreRequestHandler(0x01, new EchoHandler());
        actCore.Should().NotThrow("核心区 0x01 放行（ICoreProtocolPort 内部口）");

        var actUser = () => server.RegisterCoreRequestHandler(0x70, new EchoHandler());
        actUser.Should().Throw<ArgumentOutOfRangeException>("核心口只放行 0x00-0x4F");

        var actData = () => server.SendDatagramAsync(NodeId.NewRandom(), 0x70, ReadOnlyMemory<byte>.Empty)
            .AsTask();
        actData.Should().ThrowAsync<NotSupportedException>("UDS 定位 = 请求回调控制面");
    }

    /// <summary>验收④：Dispose unlink socket 文件。</summary>
    [SkippableFact]
    public async Task Dispose_UnlinksSocketFile()
    {
        Skip.If(!UdsBindSupported(), "沙箱不支持 AF_UNIX bind");
        var server = CreateServer("unlink", v => v);
        var path = Path.Combine(_dir, "unlink.sock");
        File.Exists(path).Should().BeTrue("Start 绑定后 socket 文件存在");
        await server.DisposeAsync();
        File.Exists(path).Should().BeFalse("Dispose unlink socket 文件");
    }

    private sealed class EchoHandler : IRequestHandler
    {
        public void OnRequest(NodeId from, ReadOnlyMemory<byte> payload, IReplyContext reply)
            => _ = reply.ReplyAsync(payload.ToArray()).AsTask();
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        foreach (var t in _transports) { try { await t.DisposeAsync(); } catch { } }
        try { Directory.Delete(_dir, true); } catch { }
    }
}
