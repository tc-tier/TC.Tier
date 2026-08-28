using System.Net.Sockets;
using FluentAssertions;
using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Image;
using TC.Tier.Core.IO.Mem;
using TC.Tier.Core.IO.Net;
using TC.Tier.Core.IO.TierVolume;
using Xunit;

namespace TC.Tier.Core.IO.Net.Tests;

/// <summary>
/// TIN1 网络传送契约测试（raw-medium-and-conversion-design §9）——
/// 回环 TCP：Mem→TierVolume 跨介质流式收发 + 回执对账 + 握手违约拒读 + 空卷往返。
/// </summary>
public sealed class NetworkImageTransferTests : IDisposable
{
    private readonly string _dir = TestTempDir.Create("core-io-net");
    private readonly List<IDisposable> _open = [];

    public void Dispose()
    {
        foreach (var d in _open) d.Dispose();
        TestTempDir.TryCleanup(_dir);
    }

    private static void Populate(IFileSystem fs)
    {
        fs.EnsureRoot();
        fs.CreateDirectory("a/b");
        using (var h = fs.Open("a/dense", RWO()))
        {
            var data = new byte[10_000];
            new Random(7).NextBytes(data);
            h.Write(0, data);
            h.Flush();
        }
        using (var h = fs.Open("sparse", RWO()))
        {
            h.Write(65536, new byte[] { 1, 2, 3 });
            h.Write(0, new byte[] { 9 });
            h.Flush();
        }
        fs.CreateFile("a/b/extra", extra: new byte[] { 0xCA, 0xFE });
        using (var h = fs.Open("a/b/extra", RWO()))
        {
            h.Write(0, new byte[100]);
            h.Flush();
        }
    }

    private static FileOpenOptions RWO() => new()
    { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate, Sharing = FileSharing.ReadWrite };

    private static FileOpenOptions RO() => new()
    { Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting, Sharing = FileSharing.ReadWrite };

    [Fact]
    public void SendReceive_MemToTv_Loopback_Roundtrip()
    {
        using var src = MemoryFileSystem.New();
        var tv = TierVolumeFs.New(TierVolumeCarrier.File(Path.Combine(_dir, $"v-{Guid.NewGuid():N}.tier")),
            new TierVolumeFormatOptions { QuotaBytes = 32L << 20 });
        _open.Add(raw);
        Populate(src);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var receiver = Task.Run(() => NetworkImageTransfer.ReceiveTo(raw, port: 0,
            new ImageOptions { FrameBytes = 4096 }, cts.Token), cts.Token);
        // 等监听就绪后取端口（0=系统分配——短暂轮询 listener 就绪信号不可得，改用固定端口重试法之外的
        // 简化：接收端先用固定高端口）
        var result = NetworkTransferResultOnFixedPort(src, raw, 18721, cts.Token);
        result.Verified.Should().BeTrue("回执确认");

        // 等价断言
        var exp = src.EnumerateEntries(recursive: true).Select(e => (e.Name, e.Type)).OrderBy(x => x.Name).ToList();
        var act = raw.EnumerateEntries(recursive: true).Select(e => (e.Name, e.Type)).OrderBy(x => x.Name).ToList();
        act.Should().BeEquivalentTo(exp);
        using (var he = src.Open("a/dense", RO()))
        using (var ha = raw.Open("a/dense", RO()))
        {
            ha.Length.Should().Be(he.Length);
            var be = new byte[he.Length];
            var ba = new byte[ha.Length];
            he.Read(0, be).Should().Be((int)he.Length);
            ha.Read(0, ba).Should().Be((int)ha.Length);
            ba.Should().BeEquivalentTo(be, "跨网络往返内容逐字节等价");
        }
    }

    private static NetworkTransferResult NetworkTransferResultOnFixedPort(IFileSystem src, TierVolumeFs tv,
        int port, CancellationToken ct)
    {
        // 接收端监听重试（固定端口可能短暂占用——3 次退避）
        for (var attempt = 0; ; attempt++)
        {
            var receiver = Task.Run(() => NetworkImageTransfer.ReceiveTo(raw, port,
                new ImageOptions { FrameBytes = 4096 }, ct), ct);
            try
            {
                Thread.Sleep(200);   // 监听就绪
                return NetworkImageTransfer.Send(src, "127.0.0.1", port, new ImageOptions { FrameBytes = 4096 }, ct);
            }
            catch (SocketException) when (attempt < 2)
            {
                receiver.ContinueWith(_ => { });
                port++;
                Thread.Sleep(100);
            }
        }
    }

    [Fact]
    public void SendReceive_EmptyVolume_Roundtrips()
    {
        using var src = MemoryFileSystem.New();
        var tv = TierVolumeFs.New(TierVolumeCarrier.File(Path.Combine(_dir, $"e-{Guid.NewGuid():N}.tier")),
            new TierVolumeFormatOptions { QuotaBytes = 8 << 20 });
        _open.Add(raw);
        src.EnsureRoot();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var receiver = Task.Run(() => NetworkImageTransfer.ReceiveTo(raw, 18731, null, cts.Token), cts.Token);
        Thread.Sleep(200);
        var result = NetworkImageTransfer.Send(src, "127.0.0.1", 18731, null, cts.Token);
        receiver.Wait(TimeSpan.FromSeconds(15)).Should().BeTrue();
        result.EntryCount.Should().Be(0);
        raw.EnumerateEntries(recursive: true).Should().BeEmpty();
    }

    [Fact]
    public void Handshake_BadMagic_Rejected()
    {
        using var tv = TierVolumeFs.New(TierVolumeCarrier.File(Path.Combine(_dir, $"b-{Guid.NewGuid():N}.tier")),
            new TierVolumeFormatOptions { QuotaBytes = 8 << 20 });
        _open.Add(raw);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var receiver = Task.Run(() =>
        {
            var act = () => NetworkImageTransfer.ReceiveTo(raw, 18741, null, cts.Token);
            act.Should().Throw<IOException>("magic 不符拒读");
        }, cts.Token);
        Thread.Sleep(200);
        using var client = new TcpClient();
        client.Connect("127.0.0.1", 18741);
        using var s = client.GetStream();
        s.Write(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x01, 0x00, 0x00 });   // 坏 magic
        receiver.Wait(TimeSpan.FromSeconds(15)).Should().BeTrue();
    }
}
