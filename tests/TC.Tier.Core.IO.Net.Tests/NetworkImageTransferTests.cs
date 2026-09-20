using System.Net;
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
/// TIN1 网络传送契约测试（tv-medium-and-conversion-design §9）——
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
        _open.Add(tv);
        Populate(src);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();   // 绑定同步完成——端口先于发送方确定（结构性无竞速）
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var receiver = Task.Run(() => NetworkImageTransfer.ReceiveTo(tv, listener,
            new ImageOptions { FrameBytes = 4096 }, cts.Token), cts.Token);
        var result = NetworkImageTransfer.Send(src, "127.0.0.1", port,
            new ImageOptions { FrameBytes = 4096 }, cts.Token);
        result.Verified.Should().BeTrue("回执确认");

        // 等价断言
        var exp = src.EnumerateEntries(recursive: true).Select(e => (e.Name, e.Type)).OrderBy(x => x.Name).ToList();
        var act = tv.EnumerateEntries(recursive: true).Select(e => (e.Name, e.Type)).OrderBy(x => x.Name).ToList();
        act.Should().BeEquivalentTo(exp);
        using (var he = src.Open("a/dense", RO()))
        using (var ha = tv.Open("a/dense", RO()))
        {
            ha.Length.Should().Be(he.Length);
            var be = new byte[he.Length];
            var ba = new byte[ha.Length];
            he.Read(0, be).Should().Be((int)he.Length);
            ha.Read(0, ba).Should().Be((int)ha.Length);
            ba.Should().BeEquivalentTo(be, "跨网络往返内容逐字节等价");
        }
    }

    [Fact]
    public void SendReceive_EmptyVolume_Roundtrips()
    {
        using var src = MemoryFileSystem.New();
        var tv = TierVolumeFs.New(TierVolumeCarrier.File(Path.Combine(_dir, $"e-{Guid.NewGuid():N}.tier")),
            new TierVolumeFormatOptions { QuotaBytes = 8 << 20 });
        _open.Add(tv);
        src.EnsureRoot();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var receiver = Task.Run(() => NetworkImageTransfer.ReceiveTo(tv, listener, null, cts.Token), cts.Token);
        var result = NetworkImageTransfer.Send(src, "127.0.0.1", port, null, cts.Token);
        receiver.Wait(TimeSpan.FromSeconds(15)).Should().BeTrue();
        result.EntryCount.Should().Be(0);
        tv.EnumerateEntries(recursive: true).Should().BeEmpty();
    }

    [Fact]
    public void Handshake_BadMagic_Rejected()
    {
        using var tv = TierVolumeFs.New(TierVolumeCarrier.File(Path.Combine(_dir, $"b-{Guid.NewGuid():N}.tier")),
            new TierVolumeFormatOptions { QuotaBytes = 8 << 20 });
        _open.Add(tv);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var receiver = Task.Run(() =>
        {
            var act = () => NetworkImageTransfer.ReceiveTo(tv, listener, null, cts.Token);
            act.Should().Throw<IOException>("magic 不符拒读");
        }, cts.Token);
        using var client = new TcpClient();
        client.Connect("127.0.0.1", port);
        using var s = client.GetStream();
        s.Write(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x01, 0x00, 0x00 });   // 坏 magic
        receiver.Wait(TimeSpan.FromSeconds(15)).Should().BeTrue();
    }

    // ═══ Raw 档（#492——整卷字节镜像 dd 语义）═══

    [Fact]
    public void SendRaw_TvToTv_Loopback_ByteMirrorRoundtrip()
    {
        var src = TierVolumeFs.New(TierVolumeCarrier.File(Path.Combine(_dir, $"raw-s-{Guid.NewGuid():N}.tier")),
            new TierVolumeFormatOptions { QuotaBytes = 8 << 20 });
        var dst = TierVolumeFs.New(TierVolumeCarrier.File(Path.Combine(_dir, $"raw-d-{Guid.NewGuid():N}.tier")),
            new TierVolumeFormatOptions { QuotaBytes = 8 << 20 });
        _open.Add(src);
        _open.Add(dst);

        // 源卷装数据（经 fs 面——Raw 传送后目标 fs 面应见同一内容：OnMirrorCompleted 重建内存元数据）
        src.EnsureRoot();
        src.CreateDirectory("cfg");
        using (var h = src.Open("keys.bin", RWO()))
        {
            var data = new byte[4096];
            new Random(11).NextBytes(data);
            h.Write(0, data);
            h.Flush();
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var receiver = Task.Run(() => NetworkImageTransfer.ReceiveRawTo(dst, listener, cts.Token), cts.Token);
        var sent = NetworkImageTransfer.SendRaw(src, "127.0.0.1", port, cts.Token);
        receiver.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue("接收端按时完成");
        var received = receiver.Result;

        sent.Verified.Should().BeTrue("对端回执端到端对账（字节数+聚合 CRC）一致");
        received.Verified.Should().BeTrue("逐帧 CRC + 长度对账通过");
        sent.RawBytes.Should().Be(received.RawBytes).And.BePositive("整卷原始字节数");
        sent.EntryCount.Should().Be(0, "Raw 档无条目语义");

        // 字节镜像后目标 fs 面等价（元数据从盘重建）
        dst.EnumerateEntries(recursive: true).Select(e => e.Name).OrderBy(x => x)
            .Should().BeEquivalentTo(["keys.bin", "cfg"], "整卷镜像携带全部文件");
        using (var hs = src.Open("keys.bin", RO()))
        using (var hd = dst.Open("keys.bin", RO()))
        {
            hd.Length.Should().Be(hs.Length);
            var bs = new byte[hs.Length];
            var bd = new byte[hd.Length];
            hs.Read(0, bs);
            hd.Read(0, bd);
            bd.Should().BeEquivalentTo(bs, "镜像内容逐字节等价");
        }
    }

    [Fact]
    public void SendRaw_MemSource_ThrowsUnsupported_AllPlatforms()
    {
        using var mem = MemoryFileSystem.New();
        var act = () => NetworkImageTransfer.SendRaw(mem, "127.0.0.1", 1);
        act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.Unsupported,
            "Raw 档要求连续卷介质——结构化介质走 Structural 档");
    }
}
