using TC.Tier.Core.IO;
using TC.Tier.Core.IO.TierVolume;

namespace TC.Tier.Core.Tests.IO.TierVolume;

/// <summary>
/// TierVolumeFileHandle 数据面契约测试（§3.2 三态语义 / §3.1 无台阶）——
/// 稀疏（洞读零）/ 预分配（unwritten）/ PunchHole 真回收 / 100G 逻辑小卷 / 越界零扩展 /
/// CopyRange / 向量 IO / Collapse/Insert 全平台 / 范围锁 / 追加原子。
/// </summary>
public sealed class TierVolumeFileHandleTests : IDisposable
{
    private readonly string _dir = TestTempDir.Create("core-io-tv-handle");
    private readonly TierVolumeFs _fs;

    public TierVolumeFileHandleTests()
    {
        _fs = TierVolumeFs.New(TierVolumeCarrier.File(Path.Combine(_dir, "vol.tier")),
            new TierVolumeFormatOptions { QuotaBytes = 32L << 20 });
    }

    public void Dispose()
    {
        _fs.Dispose();
        TestTempDir.TryCleanup(_dir);
    }

    private static FileOpenOptions RWOpts() => new()
    { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate, Sharing = FileSharing.ReadWrite };

    [Fact]
    public void WriteRead_Roundtrip_ZeroExtend_HoleReadsZero()
    {
        using var h = _fs.Open("f", RWOpts());
        h.Write(16384, new byte[] { 7 });
        h.Length.Should().Be(16385, "越过 EOF 零洞扩展（pwrite 平权）");
        var buf = new byte[64];
        h.Read(0, buf).Should().Be(64);
        buf.Should().OnlyContain(b => b == 0, "洞读零（§3.2）");
        h.Read(16384, buf).Should().Be(1);
        buf[0].Should().Be(7);
    }

    [Fact]
    public void SparseFidelity_HoleNotAllocated()
    {
        using var h = _fs.Open("sparse", RWOpts());
        h.Write(1 << 20, new byte[] { 5 });
        h.Length.Should().Be((1 << 20) + 1);
        h.AllocatedSize.Should().BeLessThanOrEqualTo(8192, "1MB 洞不占物理（稀疏保真——位图=可达集）");
        var ranges = h.EnumerateAllocatedRanges();
        ranges.Should().HaveCount(1, "单 allocated 区间");
    }

    [Fact]
    public void SetLength_Extend_PureLogical_NoAllocation()
    {
        using var h = _fs.Open("logic", RWOpts());
        h.SetLength(1 << 24);   // 16MB 纯逻辑
        h.Length.Should().Be(1 << 24);
        h.AllocatedSize.Should().Be(0, "扩展 = 纯逻辑零物理（§3.2）");
        var buf = new byte[16];
        h.Read(0, buf).Should().Be(16);
        buf.Should().OnlyContain(b => b == 0);
    }

    [Fact]
    public void Preallocate_Unwritten_PhysicalReserved_ReadsZero_WritesConvert()
    {
        using var h = _fs.Open("pre", new FileOpenOptions
        { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate, Sharing = FileSharing.ReadWrite, PreallocateSize = 1 << 20 });
        h.AllocatedSize.Should().Be(1 << 20, "unwritten 物理预留（fallocate 语义）");
        var buf = new byte[64];
        h.Read(0, buf).Should().Be(64);
        buf.Should().OnlyContain(b => b == 0, "unwritten 读零（§3.2）");
        h.Write(4096, new byte[] { 9 });
        h.Read(4096, buf).Should().Be(64, "1MB 逻辑内读满");
        buf[0].Should().Be(9, "写时转 written");
        buf[1..].Should().OnlyContain(b => b == 0, "unwritten 基底零填充（非物理垃圾）");
    }

    [Fact]
    public void PunchHole_ReleasesPhysical_LengthUnchanged()
    {
        using var h = _fs.Open("punch", RWOpts());
        h.Write(0, new byte[65536]);
        var before = h.AllocatedSize;
        before.Should().BeGreaterThanOrEqualTo(65536);
        h.PunchHole(0, 65536);
        h.Length.Should().Be(65536, "逻辑长度不动（§3.2）");
        h.AllocatedSize.Should().BeLessThan(before, "物理真回收（Sparse 位——不是 memset 模拟）");
        var buf = new byte[64];
        h.Read(0, buf).Should().Be(64);
        buf.Should().OnlyContain(b => b == 0, "打洞区间读零");
    }

    // ═══════════════ PunchHole 字节粒度契约（与 Disk/Mem 平权——A4 任意 offset/length）═══════════════

    [Fact]
    public void PunchHole_UnalignedOffsetAndLength_EdgesZeroed_InteriorReclaimed()
    {
        using var h = _fs.Open("pu", RWOpts());
        const int fileLen = 16384;
        var data = new byte[fileLen];
        Array.Fill(data, (byte)0xCD);
        h.Write(0, data);
        // [100, 8292)：头边缘 [100,4096) 零写（块0保留），内块 [4096,8192) 物理回收，尾边缘 [8192,8292) 零写（块2保留）
        h.PunchHole(100, 8192);
        h.Length.Should().Be(fileLen, "逻辑长度不动");

        var buf = new byte[fileLen];
        h.Read(0, buf).Should().Be(fileLen);
        buf[..100].Should().OnlyContain(b => b == 0xCD, "打洞起点前数据完好");
        buf[100..8292].Should().OnlyContain(b => b == 0, "打洞区间（含非对齐边缘）读零");
        buf[8292..].Should().OnlyContain(b => b == 0xCD, "打洞终点后数据完好");

        var ranges = h.EnumerateAllocatedRanges();
        ranges.Should().NotContain(r => r.Start < 8192 && r.End > 4096, "整块 [4096,8192) 已物理回收");
        ranges.Should().Contain(r => r.Start == 0, "头边缘所在块保留（零写不回收）");
    }

    [Fact]
    public void PunchHole_EntirelyWithinOneBlock_Zeroed_NoAlignmentThrow()
    {
        using var h = _fs.Open("pw", RWOpts());
        var data = new byte[4096];
        Array.Fill(data, (byte)0xAB);
        h.Write(0, data);
        h.PunchHole(10, 100);   // 全在块 0 内——无整块回收，纯边缘零写
        var buf = new byte[4096];
        h.Read(0, buf).Should().Be(4096);
        buf[..10].Should().OnlyContain(b => b == 0xAB);
        buf[10..110].Should().OnlyContain(b => b == 0, "块内非对齐区间读零");
        buf[110..].Should().OnlyContain(b => b == 0xAB, "区间外完好");
    }

    [Fact]
    public void PunchHole_BeyondEOF_Throws_IOFailure()
    {
        using var h = _fs.Open("px", RWOpts());
        h.Write(0, new byte[4096]);
        var act = () => h.PunchHole(4096, 4096);   // 紧接 EOF 起打洞 = 越界
        act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.IOFailure,
            "越出文件长度与 Disk/Mem 平权抛 IOFailure");
    }

    [Fact]
    public void PunchHole_DirectHandle_Unaligned_Succeeds_AndReadsZeroAfterFlush()
    {
        using var h = _fs.Open("pd", new FileOpenOptions
        {
            Access = AccessMode.ReadWrite,
            Mode = FileOpenMode.OpenOrCreate,
            Sharing = FileSharing.ReadWrite,
            Hints = FileOpenHints.NoBuffering,
        });
        var data = new byte[8192];
        Array.Fill(data, (byte)0xEF);
        h.Write(0, data);   // DIO 对齐写
        h.PunchHole(100, 4096);   // 非对齐——边缘内部走缓冲零写，不抛 AlignmentError
        h.Flush();   // 边缘零页落载体（DIO 读绕自管页缓存——flush 后载体/缓存一致）
        var buf = new byte[8192];
        h.Read(0, buf).Should().Be(8192);
        buf[..100].Should().OnlyContain(b => b == 0xEF);
        buf[100..4196].Should().OnlyContain(b => b == 0, "DIO 句柄非对齐打洞读零");
        buf[4196..].Should().OnlyContain(b => b == 0xEF, "尾后完好");
    }

    [Fact]
    public void PunchHole_Unaligned_PersistsAcrossReopen()
    {
        var volPath = Path.Combine(_dir, $"reopen-{Guid.NewGuid():N}.tier");
        using (var fs = TierVolumeFs.New(TierVolumeCarrier.File(volPath),
                   new TierVolumeFormatOptions { QuotaBytes = 32L << 20 }))
        {
            using var h = fs.Open("rp", new FileOpenOptions
            { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate, Sharing = FileSharing.ReadWrite });
            var data = new byte[8192];
            Array.Fill(data, (byte)0x77);
            h.Write(0, data);
            h.PunchHole(50, 5000);   // [50,5050)：跨块非对齐
            h.Flush();
        }
        using (var fs = TierVolumeFs.Open(TierVolumeCarrier.File(volPath)))
        using (var h = fs.Open("rp", new FileOpenOptions
               { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenExisting, Sharing = FileSharing.ReadWrite }))
        {
            var buf = new byte[8192];
            h.Read(0, buf).Should().Be(8192);
            buf[..50].Should().OnlyContain(b => b == 0x77, "重开后区间前完好");
            buf[50..5050].Should().OnlyContain(b => b == 0, "重开后打洞区间（journal 元数据 + 载体零页）持续读零");
            buf[5050..].Should().OnlyContain(b => b == 0x77, "重开后区间后完好");
        }
    }

    [Fact]
    public void PunchHole_Aligned_Idempotent()
    {
        using var h = _fs.Open("pi", RWOpts());
        h.Write(0, new byte[8192]);
        h.PunchHole(0, 4096);
        h.PunchHole(0, 4096);   // 二次打洞（已回收区间）——幂等不抛
        var buf = new byte[8192];
        h.Read(0, buf).Should().Be(8192);
        buf[..4096].Should().OnlyContain(b => b == 0);
        buf[4096..].Should().OnlyContain(b => b == 0, "写入即零——全零文件");
    }

    [Fact]
    public void HundredGLogical_OnSmallVolume_Succeeds_PhysicalCriterion()
    {
        using var h = _fs.Open("huge", RWOpts());
        h.SetLength(100L << 30);   // 100G 逻辑（§3.2 容量物理判据）
        h.Length.Should().Be(100L << 30);
        h.Write((99L << 30), new byte[] { 1 });   // 尾部 1 字节
        h.AllocatedSize.Should().BeLessThanOrEqualTo(8192, "物理占用 = 尾块（容量判据 = 物理块数）");
    }

    [Fact]
    public void NoMetadataCliff_EntriesUntilDiskFull()
    {
        // ★ 无台阶契约（§3.1）：条目数增长只允许因"空间耗尽"失败——不因条目数上限
        _fs.CreateDirectory("many");
        var created = 0;
        try
        {
            for (var i = 0; i < 100_000; i++)
            {
                _fs.CreateFile($"many/f{i}");
                created++;
            }
        }
        catch (FileIOException ex)
        {
            ex.Error.Should().Be(IOError.DiskFull,
                $"唯一允许的失败类型 = 空间耗尽（创建 {created} 条后失败——无条目数台阶）");
        }
        _fs.EnumerateEntries(recursive: true).Count().Should().Be(created + 1, "文件 + many 目录");
    }

    [Fact]
    public void Append_AtomicReserved_NoOverwrite()
    {
        using var h = _fs.Open("app", RWOpts());
        var r1 = h.Append(new byte[100]);
        var r2 = h.Append(new byte[50]);
        r2.Should().Be(r1 + 100, "追加预留原子推进（AppendCursor 接线）");
        h.Length.Should().Be(150);
    }

    [Fact]
    public void CopyRange_SameVolume()
    {
        using var src = _fs.Open("cs", RWOpts());
        using var dst = _fs.Open("cd", RWOpts());
        var data = new byte[10000];
        new Random(4).NextBytes(data);
        src.Write(0, data);
        var copied = src.CopyRange(dst, 0, 0, data.Length);
        copied.Should().Be(data.Length);
        var buf = new byte[data.Length];
        dst.Read(0, buf).Should().Be(data.Length);
        buf.Should().BeEquivalentTo(data);
    }

    [Fact]
    public void VectorIO_EquivalentToSerial()
    {
        using var h = _fs.Open("vec", RWOpts());
        var a = new byte[100]; new Random(1).NextBytes(a);
        var b = new byte[200]; new Random(2).NextBytes(b);
        h.WriteVector(0, new[] { new ReadOnlyMemory<byte>(a), new ReadOnlyMemory<byte>(b) });
        var got = new byte[300];
        h.Read(0, got).Should().Be(300);
        got[..100].Should().BeEquivalentTo(a);
        got[100..].Should().BeEquivalentTo(b);
    }

    [Fact]
    public void CollapseInsertRange_AllPlatform()
    {
        using var h = _fs.Open("shift", RWOpts());
        h.Write(0, new byte[3 * 4096]);   // 3 块
        h.Write(0, new byte[] { 1, 2, 3 });
        h.Write(8192, new byte[] { 7, 8, 9 });
        h.CollapseRange(4096, 4096);   // 塌中间块
        h.Length.Should().Be(2 * 4096, "塌缩后缩短");
        var buf = new byte[3];
        h.Read(4096, buf).Should().Be(3);
        buf.Should().BeEquivalentTo(new byte[] { 7, 8, 9 }, "后续数据前移");
        h.InsertRange(0, 4096);   // 头部插入零块
        h.Length.Should().Be(3 * 4096);
        h.Read(4096, buf).Should().Be(3);
        buf.Should().BeEquivalentTo(new byte[] { 1, 2, 3 }, "原数据后移");
        h.Read(0, buf).Should().Be(3);
        buf.Should().OnlyContain(b => b == 0, "插入区读零");
    }

    [Fact]
    public void RangeLock_ExclusiveConflict()
    {
        using var h1 = _fs.Open("lock", RWOpts());
        using var h2 = _fs.Open("lock", RWOpts());
        h1.TryLock(0, 100, FileLockMode.Exclusive).Should().BeTrue();
        h2.TryLock(50, 100, FileLockMode.Shared).Should().BeFalse("他句柄重叠排他冲突（进程内真实生效）");
        h1.Unlock(0, 100);
        h2.TryLock(50, 100, FileLockMode.Shared).Should().BeTrue("释放后可获取");
    }

    [Fact]
    public void FileExtra_Plane()
    {
        using var h = _fs.Open("extra", RWOpts());
        h.SetFileExtra(new byte[] { 1, 2, 3 });
        h.FileExtra.ToArray().Should().BeEquivalentTo(new byte[] { 1, 2, 3 });
        h.WriteFileExtra(1, new byte[] { 9 });
        h.ReadFileExtra(0, new byte[3]).Should().Be(3);
        h.FileExtra.ToArray().Should().BeEquivalentTo(new byte[] { 1, 9, 3 });
    }

    [Fact]
    public void DirectModeHint_BypassesPageCache()
    {
        using var h = _fs.Open("direct", new FileOpenOptions
        {
            Access = AccessMode.ReadWrite,
            Mode = FileOpenMode.OpenOrCreate,
            Sharing = FileSharing.ReadWrite,
            Hints = FileOpenHints.NoBuffering,
        });
        h.UnbufferedSupport.Should().Be(UnbufferedIoSupport.Supported, "直达档报告 Supported（§3.4 两档）");
        h.RequiredAlignment.Should().Be(4096);
        var data = new byte[4096];
        new Random(8).NextBytes(data);
        h.Write(0, data);   // 整块对齐直达写
        var buf = new byte[4096];
        h.Read(0, buf).Should().Be(4096);
        buf.Should().BeEquivalentTo(data, "直达档数据正确（绕过自管页缓存）");
    }
    // ═══════════════ RM-32：CopyRange 块级快道 ═══════════════

    private TierVolumeFileHandle OpenRW(string path)
        => (TierVolumeFileHandle)_fs.Open(path, new FileOpenOptions
        { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate, Sharing = FileSharing.ReadWrite });

    [Fact]
    public void CopyRange_BlockAlignedAppend_FastPathDataFaithful()
    {
        var data = new byte[256 * 1024];
        new Random(31).NextBytes(data);
        using (var h = OpenRW("src"))
        {
            h.Write(0, data);   // 块对齐全 Written
        }
        using var src = OpenRW("src");
        using var dst = OpenRW("dst");
        var copied = src.CopyRange(dst, 0, 0, data.Length);   // 块对齐 + 目标纯追加 → 快道
        copied.Should().Be(data.Length);
        var buf = new byte[data.Length];
        dst.Read(0, buf).Should().Be(data.Length);
        buf.Should().BeEquivalentTo(data, "块级快道数据保真");
    }

    [Fact]
    public void CopyRange_UnalignedAndHoled_FallbackSemanticsUnchanged()
    {
        var data = new byte[8192 + 123];   // 非块对齐长度 → 回退公共路径
        new Random(37).NextBytes(data);
        using (var h = OpenRW("u-src"))
        {
            h.Write(0, data);
        }
        using var src = OpenRW("u-src");
        using var dst = OpenRW("u-dst");
        src.CopyRange(dst, 17, 0, data.Length - 17).Should().Be(data.Length - 17);
        var buf = new byte[data.Length - 17];
        dst.Read(0, buf);
        buf.Should().BeEquivalentTo(data.AsSpan(17).ToArray(), "回退路径语义不变（任意偏移/长度");
    }
}
