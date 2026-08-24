using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Disk;
using TC.Tier.Core.IO.Image;
using TC.Tier.Core.IO.Mem;
using TC.Tier.Core.IO.Remote;
using TC.Tier.Core.IO.Testing;

namespace TC.Tier.Core.Tests.IO.Image;

/// <summary>
/// 采集/还原管线契约测试（raw-medium-and-conversion-design §5/§7）——
/// 3×3 介质转换矩阵全格往返（Mem/Disk/Remote ↔ Mem/Disk/Remote）+ 格式健壮性（坏流拒读）+
/// 稀疏/FileExtra/多帧保真。等价断言 = 条目集/内容/逻辑长度/FileExtra（AllocatedSize 介质语义不比）。
/// </summary>
public sealed class RootSpaceImageTests
{
    // ═══════════════ 介质工厂（矩阵两轴）═══════════════

    private static MemoryFileSystem NewMem() => MemoryFileSystem.New();

    private static DiskFileSystem NewDisk()
    {
        var dir = TestTempDir.Create("core-io-image");
        var fs = DiskFileSystem.OpenOrCreate(dir);
        return fs;
    }

    private static RemoteFileSystem NewRemote() => RemoteFileSystem.OpenOrCreate(new MemoryObjectStore());

    public static IEnumerable<object[]> Matrix
        => from src in (Func<IFileSystem>[])[NewMem, NewDisk, NewRemote]
           from dst in (Func<IFileSystem>[])[NewMem, NewDisk, NewRemote]
           select new object[] { src, dst };

    // ═══════════════ 内容构造 + 等价断言 ═══════════════

    /// <summary>代表性根空间：嵌套目录 / 空文件 / 稠密 / 稀疏（高偏移写）/ FileExtra / 多帧大文件。</summary>
    private static void Populate(IFileSystem fs)
    {
        fs.CreateDirectory("a");
        fs.CreateDirectory("a/b");

        fs.CreateFile("empty");   // 空文件（显式创建——三介质持久化语义一致；Remote 句柄无脏 Flush 是 no-op）

        using (var h = fs.Open("a/dense", RWOpts()))
        {
            var data = new byte[10_000];
            new Random(7).NextBytes(data);
            h.Write(0, data);
            h.Flush();
        }

        using (var h = fs.Open("sparse", RWOpts()))
        {
            h.Write(65536, new byte[] { 1, 2, 3 });   // 头部大洞
            h.Write(0, new byte[] { 9 });
            h.Flush();
        }

        fs.CreateFile("a/b/extra", extra: new byte[] { 0xCA, 0xFE });
        using (var h = fs.Open("a/b/extra", RWOpts()))
        {
            h.Write(0, new byte[100]);
            h.Flush();
        }
    }

    private static FileOpenOptions RWOpts() => new()
    { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate, Sharing = FileSharing.ReadWrite };

    private static FileOpenOptions ROpts() => new()
    { Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting, Sharing = FileSharing.ReadWrite };

    private static void AssertEquivalent(IFileSystem expected, IFileSystem actual)
    {
        var expEntries = expected.EnumerateEntries(recursive: true).Select(e => (e.Name, e.Type)).OrderBy(x => x.Name).ToList();
        var actEntries = actual.EnumerateEntries(recursive: true).Select(e => (e.Name, e.Type)).OrderBy(x => x.Name).ToList();
        actEntries.Should().BeEquivalentTo(expEntries, "条目集（路径+类型）往返等价");

        foreach (var (path, type) in expEntries.Where(x => x.Type == FsEntryType.File))
        {
            using var he = expected.Open(path, ROpts());
            using var ha = actual.Open(path, ROpts());
            ha.Length.Should().Be(he.Length, $"[{path}] 逻辑长度等价");
            ha.FileExtra.ToArray().Should().BeEquivalentTo(he.FileExtra.ToArray(), $"[{path}] FileExtra 等价");
            var be = new byte[he.Length];
            var ba = new byte[ha.Length];
            if (he.Length > 0)
            {
                he.Read(0, be).Should().Be((int)he.Length);
                ha.Read(0, ba).Should().Be((int)ha.Length);
            }
            ba.Should().BeEquivalentTo(be, $"[{path}] 内容逐字节等价");
        }
    }

    // ═══════════════ 3×3 矩阵全格往返 ═══════════════

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Matrix_Roundtrip_AllNineCells(Func<IFileSystem> srcFactory, Func<IFileSystem> dstFactory)
    {
        using var source = srcFactory();
        using var target = dstFactory();
        source.EnsureRoot();
        Populate(source);

        using var stream = new MemoryStream();
        var capture = RootSpaceImage.Capture(source, stream, new ImageOptions { FrameBytes = 4096 });
        capture.EntryCount.Should().Be(6, "2 目录 + 4 文件");
        capture.FrameCount.Should().BeGreaterThan(3, "多帧路径覆盖（dense 拆 ≥3 帧）");

        stream.Position = 0;
        var restore = RootSpaceImage.Restore(stream, target);
        restore.EntryCount.Should().Be(capture.EntryCount);
        restore.RawBytes.Should().Be(capture.RawBytes, "尾对账两端一致");

        AssertEquivalent(source, target);
    }

    // ═══════════════ 格式与语义 ═══════════════

    [Fact]
    public void EmptyRoot_Roundtrips()
    {
        using var src = NewMem();
        using var dst = NewMem();
        src.EnsureRoot();
        using var s = new MemoryStream();
        var cap = RootSpaceImage.Capture(src, s);
        cap.EntryCount.Should().Be(0);
        s.Position = 0;
        RootSpaceImage.Restore(s, dst).EntryCount.Should().Be(0);
    }

    [Fact]
    public void SparseFidelity_HoleNotInFrames_ReadsZero()
    {
        using var src = NewMem();
        src.EnsureRoot();
        using (var hSrc = src.Open("big-hole", RWOpts()))
        {
            hSrc.Write(1 << 20, new byte[] { 5 });   // 1MB 头洞 + 1 字节
            hSrc.Flush();
        }
        using var s = new MemoryStream();
        var cap = RootSpaceImage.Capture(src, s);
        cap.RawBytes.Should().BeLessThanOrEqualTo((1 << 20) + 1, "洞不占帧（稀疏保真）");

        using var dst = NewMem();
        s.Position = 0;
        RootSpaceImage.Restore(s, dst);
        using var hDst = dst.Open("big-hole", ROpts());
        hDst.Length.Should().Be((1 << 20) + 1);
        var probe = new byte[16];
        hDst.Read(0, probe).Should().Be(16);
        probe.Should().OnlyContain(b => b == 0, "还原端洞读零");
    }

    [Fact]
    public void Restore_NonEmptyDestination_Rejected()
    {
        using var src = NewMem();
        using var dst = NewMem();
        src.EnsureRoot();
        Populate(src);
        dst.EnsureRoot();
        using (var h = dst.Open("occupied", RWOpts())) h.Write(0, new byte[4]);

        using var s = new MemoryStream();
        RootSpaceImage.Capture(src, s);
        s.Position = 0;
        var act = () => RootSpaceImage.Restore(s, dst);
        act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.AlreadyExists,
            "还原目标必须为空——显式失败优于静默合并（设计 §5.3）");
    }

    [Fact]
    public void BadMagic_Rejected()
    {
        using var src = NewMem();
        src.EnsureRoot();
        using (var h = src.Open("f", RWOpts())) h.Write(0, new byte[8]);
        using var s = new MemoryStream();
        RootSpaceImage.Capture(src, s);
        s.Position = 0;
        s.WriteByte(0xFF);   // 破坏 magic 首字节
        s.Position = 0;
        using var dst = NewMem();
        var act = () => RootSpaceImage.Restore(s, dst, new ImageOptions { VerifyChecksums = false });
        act.Should().Throw<FileIOException>().WithMessage("*magic*");
    }

    [Fact]
    public void CorruptedFrame_CrcRejected()
    {
        using var src = NewMem();
        src.EnsureRoot();
        using (var h = src.Open("f", RWOpts()))
        {
            var data = new byte[1000];
            new Random(3).NextBytes(data);
            h.Write(0, data);
            h.Flush();
        }
        using var s = new MemoryStream();
        RootSpaceImage.Capture(src, s, new ImageOptions { Compression = ImageCompression.None });

        // 定位数据帧 payload（清单：路径"f"=1+1、type 1、len 8、extra 2、ticks 8、rangecount 4、range 17 = 头 4+2+2+4 =12 + ~41 ≈ 53）——直接在流中后段翻转一字节
        var bytes = s.ToArray();
        bytes[^40] ^= 0xFF;
        using var corrupted = new MemoryStream(bytes);
        using var dst = NewMem();
        var act = () => RootSpaceImage.Restore(corrupted, dst);
        act.Should().Throw<FileIOException>("帧 CRC 校验失败或尾对账失败——损坏被检出");
    }

    [Fact]
    public void Capture_QuietsSource_WhenGateAvailable()
    {
        using var src = NewMem();
        src.EnsureRoot();
        using (var h = src.Open("f", RWOpts())) h.Write(0, new byte[8]);

        using var s = new MemoryStream();
        RootSpaceImage.Capture(src, s, new ImageOptions { QuietSource = true });
        // 门闩租约应已随采集结束释放——写恢复
        var act = () => src.Open("f", RWOpts());
        act.Should().NotThrow("QuietSource 租约 RAII 释放后恢复可写");
    }

    [Theory]
    [InlineData(ImageCompression.None)]
    [InlineData(ImageCompression.ZLib)]
    public void Compression_BothCodecs_Roundtrip(ImageCompression codec)
    {
        using var src = NewMem();
        using var dst = NewMem();
        src.EnsureRoot();
        using (var h = src.Open("f", RWOpts()))
        {
            var data = new byte[50_000];   // 可压模式（伪随机）+ 帧拆分
            new Random(11).NextBytes(data);
            h.Write(0, data);
            h.Flush();
        }
        using var s = new MemoryStream();
        RootSpaceImage.Capture(src, s, new ImageOptions { Compression = codec, FrameBytes = 8192 });
        s.Position = 0;
        RootSpaceImage.Restore(s, dst);
        AssertEquivalent(src, dst);
    }

    // ═══ RM-13：zstd 帧编码（native 探测两分支——可用跑真往返 / 不可用断言显式拒绝）═══

    [Fact]
    public void ZstdCodec_Roundtrip_OrHonestRejection()
    {
        if (TC.Tier.Core.NativeInterop.ZstdCodec.IsAvailable)
        {
            // 帧级往返（可压数据 + 随机数据两形态）
            var compressible = new byte[100_000];
            new Random(41).NextBytes(compressible.AsSpan(0, 1000));
            for (var i = 1000; i < compressible.Length; i++) compressible[i] = (byte)(i % 7);
            var packed = TC.Tier.Core.NativeInterop.ZstdCodec.CompressFrame(compressible);
            packed.Length.Should().BeLessThan(compressible.Length, "模式数据可压");
            TC.Tier.Core.NativeInterop.ZstdCodec.DecompressFrame(packed, compressible.Length)
                .Should().BeEquivalentTo(compressible, "zstd 帧往返无损");

            var random = new byte[64_000];
            new Random(42).NextBytes(random);
            var packedR = TC.Tier.Core.NativeInterop.ZstdCodec.CompressFrame(random);
            TC.Tier.Core.NativeInterop.ZstdCodec.DecompressFrame(packedR, random.Length)
                .Should().BeEquivalentTo(random, "随机数据往返无损（膨胀由 WriteFrame 帧级回退处理）");
        }
        else
        {
            var act = () => new ImageOptions { Compression = ImageCompression.Zstd }.Validate();
            act.Should().Throw<ArgumentException>("运行库缺失——显式拒绝（不静默回退）");
        }
    }

    [Fact]
    public void Transfer_WithZstd_ContentEndToEnd()
    {
        if (!TC.Tier.Core.NativeInterop.ZstdCodec.IsAvailable)
        {
            // 环境无 zstd：端到端请求同样显式拒绝（与 Validate 同源——诚实降级贯穿）
            var act = () => RootSpaceImage.Transfer(
                TC.Tier.Core.IO.Mem.MemoryFileSystem.New(),
                TC.Tier.Core.IO.Mem.MemoryFileSystem.New(),
                new ImageOptions { Compression = ImageCompression.Zstd });
            act.Should().Throw<ArgumentException>("Zstd 端到端在缺失环境显式拒绝");
            return;
        }

        using var src = TC.Tier.Core.IO.Mem.MemoryFileSystem.New(
            new TC.Tier.Core.IO.Mem.MemoryFileSystemOptions { QuotaBytes = 1L << 28 });
        var data = new byte[2_000_000];
        for (var i = 0; i < data.Length; i++) data[i] = (byte)(i % 5);   // 高度可压
        using (var h = src.Open("big", new FileOpenOptions
        { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate, Sharing = FileSharing.ReadWrite }))
            h.Write(0, data);
        using var dst = TC.Tier.Core.IO.Mem.MemoryFileSystem.New(
            new TC.Tier.Core.IO.Mem.MemoryFileSystemOptions { QuotaBytes = 1L << 28 });
        var summary = RootSpaceImage.Transfer(src, dst, new ImageOptions { Compression = ImageCompression.Zstd });
        summary.RawBytes.Should().Be(2_000_000);
        using (var h = dst.Open("big", new FileOpenOptions
        { Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting, Sharing = FileSharing.ReadWrite }))
        {
            var buf = new byte[2_000_000];
            h.Read(0, buf).Should().Be(2_000_000);
            buf.Should().BeEquivalentTo(data, "zstd 端到端内容无损");
        }
    }
}