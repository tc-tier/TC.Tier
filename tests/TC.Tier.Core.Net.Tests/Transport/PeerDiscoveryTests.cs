using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using TC.Tier.Core.IO;
using TC.Tier.Core.Net;
using TC.Tier.Core.Net.Transport;
using Xunit;

namespace TC.Tier.Core.Net.Tests.Transport;

/// <summary>
/// 服务发现集成（二期-F8 NETGAP-041——验证矩阵 F8 行）：文件发现源（hexNodeId=host:port 行格式）
/// → PeerDiscoveryService 汇聚入 IPeerRegistry（新增/端点更新/add-only 不移除）；
/// DnsPeerDiscoverySource 主机名解析跟随；单源失败隔离。
/// </summary>
public class PeerDiscoveryTests
{
    private static NodeId IdFromHex(string hex8) => NodeId.Parse(hex8.PadRight(32, '0'));

    /// <summary>绝对路径 → 文件发现源（TierFs 挂载所在目录——源构造走 IFileSystem，对齐零 BCL IO 纪律）。</summary>
    private static FilePeerDiscoverySource Source(string absPath)
        => new(TierFs.OpenOrCreate("local:///" + Path.GetDirectoryName(absPath)!.Replace('\\', '/')),
               Path.GetFileName(absPath));

    private static async Task<string> WriteSourceFileAsync(params (string NodeHex, string Host, int Port)[] entries)
    {
        var path = Path.Combine(Path.GetTempPath(), "tctier-disc-" + Guid.NewGuid().ToString("N") + ".conf");
        var lines = new List<string> { "# tctier discovery source" };
        foreach (var (hex, host, port) in entries)
            lines.Add($"{hex}={host}:{port}");
        await File.WriteAllLinesAsync(path, lines);
        return path;
    }

    /// <summary>F8：文件源发现入表 + 端点更新 + add-only（源缺失项不移除）。</summary>
    [Fact]
    public async Task FileSource_Discovery_AddsAndUpdates_NeverRemoves()
    {
        var port = ReservePort();
        var serverId = NodeId.NewRandom();
        var server = new ClusterTransport(serverId,
            TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, port), new Dictionary<NodeId, IPEndPoint>()));
        server.Start();
        try
        {
            var idA = NodeId.NewRandom();
            var idB = NodeId.NewRandom();
            var sourceFile = await WriteSourceFileAsync(
                (idA.ToString(), "127.0.0.1", 4001),
                (idB.ToString(), "127.0.0.2", 4002),
                ("# comment", "", 0),
                ("not-a-node-id", "127.0.0.1", 1));

            var service = new PeerDiscoveryService(server, new[]
            {
                Source(sourceFile),
            }, interval: TimeSpan.FromMilliseconds(200));
            await service.PollOnceAsync();

            server.Peers.Should().ContainKey(idA, "发现项 AddPeer 入表");
            server.Peers[idA].Port.Should().Be(4001);
            server.Peers[idB].Port.Should().Be(4002);

            // 端点更新：源文件改端点 → 汇聚更新
            await File.WriteAllLinesAsync(sourceFile, new[]
            {
                $"{idA.ToString()}=127.0.0.1:5001",
                $"{idB.ToString()}=127.0.0.2:4002",
            });
            await service.PollOnceAsync();
            server.Peers[idA].Port.Should().Be(5001, "同 ID 换端点 = 更新");

            // add-only：源文件移除 idB → registry 保留（移除归 D8/C2 治理面）
            await File.WriteAllLinesAsync(sourceFile, new[] { $"{idA.ToString()}=127.0.0.1:5001" });
            await service.PollOnceAsync();
            server.Peers.Should().ContainKey(idB, "发现汇聚 add-only——不移除");

            var (polls, errors) = service.Diagnostics;
            polls.Should().BeGreaterThanOrEqualTo(3);
            errors.Should().Be(0);

            await service.DisposeAsync();
        }
        finally { await server.DisposeAsync(); }
    }

    /// <summary>F8：DnsPeerDiscoverySource——主机名解析 + 端口装配（loopback localhost）。</summary>
    [Fact]
    public async Task DnsSource_ResolvesHost()
    {
        var id = NodeId.NewRandom();
        var source = new DnsPeerDiscoverySource(id, "localhost", 9100);
        var discovered = await source.DiscoverAsync();
        discovered.Should().ContainKey(id, "localhost 可解析——A 记录 + 配置端口");
        discovered[id].Port.Should().Be(9100);
    }

    /// <summary>F8：单源失败隔离——一个源抛异常不阻断其他源与后续轮询。</summary>
    [Fact]
    public async Task SourceFailure_Isolated()
    {
        var port = ReservePort();
        var server = new ClusterTransport(NodeId.NewRandom(),
            TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, port), new Dictionary<NodeId, IPEndPoint>()));
        server.Start();
        try
        {
            var goodId = NodeId.NewRandom();
            var failing = new FailingSource();
            var good = Source(await WriteSourceFileAsync(
                (goodId.ToString(), "127.0.0.1", 7001)));

            var service = new PeerDiscoveryService(server, new IPeerDiscoverySource[] { failing, good },
                interval: TimeSpan.FromMilliseconds(100));
            await service.PollOnceAsync();

            server.Peers.Should().ContainKey(goodId, "单源失败不阻断其他源");
            var (polls, errors) = service.Diagnostics;
            polls.Should().Be(1);
            errors.Should().Be(1, "失败源计数隔离");
        }
        finally { await server.DisposeAsync(); }
    }

    private sealed class FailingSource : IPeerDiscoverySource
    {
        public string Name => "failing";
        public Task<IReadOnlyDictionary<NodeId, IPEndPoint>> DiscoverAsync(CancellationToken ct = default)
            => throw new InvalidOperationException("源故障注入");
    }

    private static int ReservePort()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        socket.Close();
        return port;
    }
    /// <summary>F9：DNS/文件源端点漂移跟随——服务迁移端口后，发现源更新 + 拨号即达新端点。</summary>
    [Fact]
    public async Task Drift_EndpointMoved_DiscoveryFollows_DialReaches()
    {
        var port1 = ReservePort();
        var serverId = NodeId.NewRandom();
        var server1 = new ClusterTransport(serverId,
            TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, port1), new Dictionary<NodeId, IPEndPoint>()));
        server1.Start();

        var client = new ClusterTransport(NodeId.NewRandom(),
            TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, 0), new Dictionary<NodeId, IPEndPoint>()));
        client.Start();
        var sourceFile = Path.Combine(Path.GetTempPath(), "tctier-drift-" + Guid.NewGuid().ToString("N") + ".conf");
        var service = new PeerDiscoveryService(client, new[] { Source(sourceFile) },
            interval: TimeSpan.FromMilliseconds(100));
        try
        {
            await File.WriteAllLinesAsync(sourceFile, new[] { $"{serverId}=127.0.0.1:{port1}" });

            // 初始发现 + 拨号到 port1
            await service.PollOnceAsync();
            client.Peers[serverId].Port.Should().Be(port1);
            var remote1 = await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port1)).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
            remote1.Should().Be(serverId);

            // 漂移：服务迁移到 port2 → 源文件更新 → 发现跟随
            await server1.DisposeAsync();
            var port2 = ReservePort();
            var server2 = new ClusterTransport(serverId,
                TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, port2), new Dictionary<NodeId, IPEndPoint>()));
            server2.Start();
            await File.WriteAllLinesAsync(sourceFile, new[] { $"{serverId}=127.0.0.1:{port2}" });
            await service.PollOnceAsync();
            await service.PollOnceAsync();

            client.Peers[serverId].Port.Should().Be(port2, "漂移跟随——发现源端点已更新");
            var remote2 = await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port2)).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
            remote2.Should().Be(serverId, "新端点可拨");
        }
        finally
        {
            await service.DisposeAsync();
            await client.DisposeAsync();
            await server1.DisposeAsync();
            if (File.Exists(sourceFile)) File.Delete(sourceFile);
        }
    }
}
