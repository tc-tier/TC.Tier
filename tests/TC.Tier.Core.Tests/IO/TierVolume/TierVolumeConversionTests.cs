using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Disk;
using TC.Tier.Core.IO.Image;
using TC.Tier.Core.IO.Mem;
using TC.Tier.Core.IO.TierVolume;
using TC.Tier.Core.IO.Remote;
using TC.Tier.Core.IO.Testing;

namespace TC.Tier.Core.Tests.IO.TierVolume;

/// <summary>
/// TierVolume 介质转换矩阵测试（§1.2/§7）——四态闭环的最后 7 格：
/// TierVolume ↔ Mem/Disk/Remote 结构化互转 + TierVolume↔TierVolume 真快道（字节镜像，IContiguousVolume）。
/// </summary>
public sealed class TierVolumeConversionTests : IDisposable
{
    private readonly string _dir = TestTempDir.Create("core-io-tv-conv");

    public void Dispose() => TestTempDir.TryCleanup(_dir);

    private TierVolumeFs NewTv()
        => TierVolumeFs.New(TierVolumeCarrier.File(Path.Combine(_dir, $"v-{Guid.NewGuid():N}.tier")),
            new TierVolumeFormatOptions { QuotaBytes = 32L << 20 });

    private static void Populate(IFileSystem fs)
    {
        fs.EnsureRoot();
        fs.CreateDirectory("a/b");
        fs.CreateFile("empty");
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

    private static void AssertEquivalent(IFileSystem expected, IFileSystem actual)
    {
        var exp = expected.EnumerateEntries(recursive: true).Select(e => (e.Name, e.Type)).OrderBy(x => x.Name).ToList();
        var act = actual.EnumerateEntries(recursive: true).Select(e => (e.Name, e.Type)).OrderBy(x => x.Name).ToList();
        act.Should().BeEquivalentTo(exp, "条目集等价");
        foreach (var (path, type) in exp.Where(x => x.Type == FsEntryType.File))
        {
            using var he = expected.Open(path, RO());
            using var ha = actual.Open(path, RO());
            ha.Length.Should().Be(he.Length, $"[{path}] 逻辑长度");
            ha.FileExtra.ToArray().Should().BeEquivalentTo(he.FileExtra.ToArray(), $"[{path}] FileExtra");
            var be = new byte[he.Length];
            var ba = new byte[ha.Length];
            if (he.Length > 0)
            {
                he.Read(0, be).Should().Be((int)he.Length);
                ha.Read(0, ba).Should().Be((int)ha.Length);
            }
            ba.Should().BeEquivalentTo(be, $"[{path}] 内容");
        }
    }

    public delegate IFileSystem FsFactory();

    private static RemoteFileSystem NewRemote() => RemoteFileSystem.OpenOrCreate(new MemoryObjectStore());

    // ── 三个源 → TierVolume（制档）═══════════════

    public static IEnumerable<object[]> Sources
        => new FsFactory[]
        {
            () => MemoryFileSystem.New(),
            () => RemoteFileSystem.OpenOrCreate(new MemoryObjectStore()),
        }.Select(f => new object[] { f });

    [Theory]
    [MemberData(nameof(Sources))]
    public void Structural_CaptureToTv(FsFactory srcFactory)
    {
        using var src = srcFactory();
        using var tv = NewTv();
        Populate(src);
        using var s = new MemoryStream();
        RootSpaceImage.Capture(src, s, new ImageOptions { FrameBytes = 4096 });
        s.Position = 0;
        RootSpaceImage.Restore(s, tv);
        AssertEquivalent(src, tv);
    }

    [Fact]
    public void Structural_DiskToTv()
    {
        var diskDir = TestTempDir.Create("core-io-tv-conv-disk");
        try
        {
            using var src = DiskFileSystem.OpenOrCreate(diskDir);
            using var tv = NewTv();
            Populate(src);
            using var s = new MemoryStream();
            RootSpaceImage.Capture(src, s, new ImageOptions { FrameBytes = 4096 });
            s.Position = 0;
            RootSpaceImage.Restore(s, tv);
            AssertEquivalent(src, tv);
        }
        finally { TestTempDir.TryCleanup(diskDir); }
    }

    // ── TierVolume → 三个目标（解档）═══════════════

    [Fact]
    public void Structural_TvToMem()
    {
        using var tv = NewTv();
        using var dst = MemoryFileSystem.New();
        Populate(tv);
        using var s = new MemoryStream();
        RootSpaceImage.Capture(tv, s, new ImageOptions { FrameBytes = 4096 });
        s.Position = 0;
        RootSpaceImage.Restore(s, dst);
        AssertEquivalent(tv, dst);
    }

    [Fact]
    public void Structural_TvToDisk()
    {
        using var tv = NewTv();
        var diskDir = TestTempDir.Create("core-io-tv-conv-d2");
        try
        {
            using var dst = DiskFileSystem.OpenOrCreate(diskDir);
            Populate(tv);
            using var s = new MemoryStream();
            RootSpaceImage.Capture(tv, s, new ImageOptions { FrameBytes = 4096 });
            s.Position = 0;
            RootSpaceImage.Restore(s, dst);
            AssertEquivalent(tv, dst);
        }
        finally { TestTempDir.TryCleanup(diskDir); }
    }

    [Fact]
    public void Structural_TvToRemote()
    {
        using var tv = NewTv();
        using var dst = RemoteFileSystem.OpenOrCreate(new MemoryObjectStore());
        Populate(tv);
        using var s = new MemoryStream();
        RootSpaceImage.Capture(tv, s, new ImageOptions { FrameBytes = 4096 });
        s.Position = 0;
        RootSpaceImage.Restore(s, dst);
        AssertEquivalent(tv, dst);
    }

    // ── TierVolume ↔ TierVolume 真快道（字节镜像）═══════════════

    [Fact]
    public void FastPath_TvToTv_ByteMirror()
    {
        using var src = NewTv();
        using var dst = NewTv();
        Populate(src);
        var summary = RootSpaceImage.Transfer(src, dst);
        summary.EntryCount.Should().Be(0, "快道产物 = 整卷字节镜像（条目数无意义，§6.2）");
        summary.RawBytes.Should().Be(src.Volume.TotalSpace, "整卷长度对账");
        AssertEquivalent(src, dst);   // 字节镜像必等价
    }

    [Fact]
    public void FastPath_StructuralFallback_WhenTargetNotContiguous()
    {
        using var src = NewTv();
        using var dst = MemoryFileSystem.New();
        Populate(src);
        var summary = RootSpaceImage.Transfer(src, dst);
        summary.EntryCount.Should().BeGreaterThan(0, "Mem 未置 ContiguousCapture → 结构化回退");
        dst.Exists("a/dense").Should().BeTrue();
    }

    [Fact]
    public void Transfer_CrossCell_TvSourcePersists()
    {
        using var src = NewTv();
        Populate(src);
        using var s = new MemoryStream();
        RootSpaceImage.Capture(src, s);   // 经 QuietSource 门闩（TierVolume 置位 MaintenanceGate）
        s.Position = 0;
        using var dst = NewTv();
        RootSpaceImage.Restore(s, dst);
        AssertEquivalent(src, dst);
    }
}
