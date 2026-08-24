using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Image;
using TC.Tier.Core.IO.Mem;
using TC.Tier.Core.IO.Raw;

namespace TC.Tier.Core.Tests.IO.Raw;

/// <summary>
/// Raw 审查修复回归族（审查结论）：
/// B1 部分块写陈旧字节泄漏 / B1b unwritten 整区间转换泄漏 / B2 成员键滞留 / B3 RAWC 毁卷脚枪 /
/// D1a 打开句柄拒删 / D1b epoch 延迟回收 / D2 句柄生命周期 / D4 unwritten 保真 / D5 非可寻址流 /
/// D7 ReadOnlyVolume / D8 单成员 Defrag / D10 游标迁移。
/// </summary>
public sealed class RawIntegrityFixesTests : IDisposable
{
    private readonly string _dir = TestTempDir.Create("core-io-raw-fixes");
    private readonly List<RawFileSystem> _openFs = [];

    public void Dispose()
    {
        foreach (var fs in _openFs) fs.Dispose();
        TestTempDir.TryCleanup(_dir);
    }

    private string NewVolumePath() => Path.Combine(_dir, $"vol-{Guid.NewGuid():N}.raw");

    private RawFileSystem Format(long capacity, RawFormatOptions? options = null)
    {
        var fs = RawFileSystem.New(RawCarrier.File(NewVolumePath()),
            options ?? new RawFormatOptions { QuotaBytes = capacity });
        _openFs.Add(fs);
        return fs;
    }

    private static FileOpenOptions RWOpts() => new()
    { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate, Sharing = FileSharing.ReadWrite };

    private static FileOpenOptions ROpts() => new()
    { Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting, Sharing = FileSharing.ReadWrite };

    // ═══════════════ B1：部分块写陈旧字节泄漏 ═══════════════

    [Fact]
    public void PartialHoleWrite_AfterBlockRecycling_ReadsZero()
    {
        using var fs = Format(256L << 10, new RawFormatOptions { QuotaBytes = 256L << 10, JournalReserveBytes = 0 });
        // 填满全部数据块的可辨识数据 → 删除 → 全部块回收为陈旧内容
        using (var a = fs.Open("a", RWOpts()))
            a.Write(0, new byte[240 << 10].Select((_, i) => (byte)(0x40 + i % 0x40)).ToArray());
        fs.Delete("a");

        // b：两个 4K 区间，中间留洞
        using var b = fs.Open("b", RWOpts());
        b.Write(8192, new byte[4096].Select((_, i) => (byte)0xEE).ToArray());
        b.Write(0, new byte[4096].Select((_, i) => (byte)0x11).ToArray());

        // 非块对齐写洞中 1 字节——新分配块必须零基，不得复活陈旧字节
        b.Write(4608, new byte[] { 0x99 });

        var z = new byte[512];
        b.Read(4096, z).Should().Be(512);
        z.Should().OnlyContain(x => x == 0, "B1：洞的未写部分读零——不得泄漏已删除文件的数据");
        b.Read(4608, z).Should().Be(512, "文件长度 12288（尾区间在）——洞区写入后读满");
        z[0].Should().Be(0x99);
        z[1..].Should().OnlyContain(x => x == 0, "写点之后的新增覆盖区零基");
    }

    [Fact]
    public void PartialHoleWrite_DirectMode_AfterRecycling_ReadsZero()
    {
        using var fs = Format(256L << 10, new RawFormatOptions { QuotaBytes = 256L << 10, JournalReserveBytes = 0 });
        using (var a = fs.Open("a", RWOpts()))
            a.Write(0, new byte[240 << 10].Select((_, i) => (byte)(0x40 + i % 0x40)).ToArray());
        fs.Delete("a");

        var directOpts = new FileOpenOptions
        {
            Access = AccessMode.ReadWrite,
            Mode = FileOpenMode.OpenOrCreate,
            Sharing = FileSharing.ReadWrite,
            Hints = FileOpenHints.NoBuffering,
        };
        using var b = fs.Open("b", directOpts);
        b.Write(8192, new byte[4096].Select((_, i) => (byte)0xEE).ToArray());
        b.Write(0, new byte[4096].Select((_, i) => (byte)0x11).ToArray());
        b.Write(4608, new byte[] { 0x99 });

        var z = new byte[512];
        b.Read(4096, z).Should().Be(512);
        z.Should().OnlyContain(x => x == 0, "B1 直达档：载体零基——洞的未写部分读零");
    }

    // ═══════════════ B1b：unwritten 部分写不污染未写块 ═══════════════

    [Fact]
    public void PreallocatedExtent_PartialWrite_UntouchedBlocksReadZero()
    {
        using var fs = Format(16L << 20);
        using var h = fs.Open("pre", new FileOpenOptions
        {
            Access = AccessMode.ReadWrite,
            Mode = FileOpenMode.OpenOrCreate,
            Sharing = FileSharing.ReadWrite,
            PreallocateSize = 1 << 20,
        });
        h.Write(4096, new byte[] { 9 });   // 中间块部分写

        var buf = new byte[4096];
        h.Read(0, buf).Should().Be(4096);
        buf.Should().OnlyContain(b => b == 0, "B1b：未触及块保持 unwritten——读零（不依赖载体陈旧内容）");
        h.Read(8192, buf).Should().Be(4096);
        buf.Should().OnlyContain(b => b == 0, "B1b：写点之后的未触及块同样读零");
        h.Read(4096, buf).Should().Be(4096);
        buf[0].Should().Be(9, "写点数据在位");
        buf[1..].Should().OnlyContain(b => b == 0, "写点块的未写残段零基");
        h.AllocatedSize.Should().Be(1 << 20, "预分配物理保持");
    }

    [Fact]
    public void PreallocatedExtent_DirectFullBlockWrite_DataVisible_UntouchedReadZero()
    {
        using var fs = Format(16L << 20);
        var directOpts = new FileOpenOptions
        {
            Access = AccessMode.ReadWrite,
            Mode = FileOpenMode.OpenOrCreate,
            Sharing = FileSharing.ReadWrite,
            Hints = FileOpenHints.NoBuffering,
            PreallocateSize = 1 << 20,
        };
        using var h = fs.Open("pre", directOpts);
        // 直达档整块 run（64KB）写进 unwritten 区——转换必须覆盖整 run（B1 族：整块快道单迭代多块）
        h.Write(0, new byte[64 << 10].Select((_, i) => (byte)(i % 251)).ToArray());

        var buf = new byte[4096];
        h.Read(0, buf).Should().Be(4096);
        buf.Should().BeEquivalentTo(Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)), "整块 run 全部转换——数据可见");
        h.Read(60 << 10, buf).Should().Be(4096);
        buf.Should().BeEquivalentTo(Enumerable.Range(0, 4096).Select(i => (byte)((60 * 1024 + i) % 251)), "run 末块数据可见");
        h.Read(64 << 10, buf).Should().Be(4096);
        buf.Should().OnlyContain(b => b == 0, "run 之外未触及区保持 unwritten——读零");
    }

    // ═══════════════ B2：多载体 Dispose 成员键滞留 ═══════════════

    [Fact]
    public void MultiCarrierDispose_MemberCarrierNotGhostLocked()
    {
        var v1 = NewVolumePath();
        var v2 = NewVolumePath();
        using (var fs = RawFileSystem.New(RawCarrier.File(v1), new RawFormatOptions { QuotaBytes = 8L << 20 }))
            fs.AddCarrier(RawCarrier.File(v2), 4L << 20);
        using (var fs = RawFileSystem.Open([RawCarrier.File(v1), RawCarrier.File(v2)])) { }

        // 成员载体非独立卷（RAWC 头）——打开应报格式错误，而不是被已 Dispose 卷的幽灵登记拦成共享冲突
        var act = () =>
        {
            using var solo = RawFileSystem.Open(RawCarrier.File(v2));
        };
        var ex = act.Should().Throw<FileIOException>("成员载体不是独立卷——按格式错误拒开").Which;
        ex.Error.Should().NotBe(IOError.SharingViolation,
            "B2：Dispose 退全部成员载体键——载体不再被幽灵登记占锁（错误码 = 格式问题而非共享冲突）");
    }

    // ═══════════════ B3：Format 覆盖成员载体 ═══════════════

    [Fact]
    public void Format_OverMemberCarrier_ThrowsAlreadyExists()
    {
        var v1 = NewVolumePath();
        var v2 = NewVolumePath();
        using (var fs = RawFileSystem.New(RawCarrier.File(v1), new RawFormatOptions { QuotaBytes = 8L << 20 }))
            fs.AddCarrier(RawCarrier.File(v2), 4L << 20);

        var act = () => RawFileSystem.New(RawCarrier.File(v2), new RawFormatOptions { QuotaBytes = 4L << 20 });
        act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.AlreadyExists,
            "B3：RAWC 成员头探测——格式化覆盖成员载体 = 毁卷脚枪，显式拒绝");
    }

    // ═══════════════ D1a：打开句柄在档拒删 ═══════════════

    [Fact]
    public void Delete_WithOpenHandle_ThrowsSharingViolation()
    {
        using var fs = Format(8L << 20);
        using var h = fs.Open("f", RWOpts());
        h.Write(0, new byte[4096]);

        var act = () => fs.Delete("f");
        act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.SharingViolation,
            "D1a：打开句柄在档——删除会即时回收块，锁外快照读者不安全，拒绝是唯一安全语义");

        h.Dispose();
        var after = () => fs.Delete("f");
        after.Should().NotThrow();   // 关闭后正常删除
        fs.Exists("f").Should().BeFalse();
    }

    [Fact]
    public void MoveOverwrite_WithOpenTargetHandle_ThrowsSharingViolation()
    {
        using var fs = Format(8L << 20);
        using var dst = fs.Open("dst", RWOpts());
        dst.Write(0, new byte[100]);
        fs.CreateFile("src");

        var act = () => fs.Move("src", "dst", overwrite: true);
        act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.SharingViolation,
            "D1a：覆盖目标有打开句柄——与 Delete 同语义拒绝");
    }

    // ═══════════════ D1b：epoch 延迟回收 ═══════════════

    [Fact]
    public void RetiredBlocks_NotReusedWhileReaderProtected()
    {
        using var fs = Format(256L << 10, new RawFormatOptions { QuotaBytes = 256L << 10, JournalReserveBytes = 0 });
        using (var a = fs.Open("a", RWOpts()))
            a.Write(0, new byte[240 << 10]);   // 占满数据区
        fs.Delete("a");

        // 读者线程进入保护域（等价于 RawFileHandle.Read 的包夹）——期间收缩释放的块不得被复用
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var reader = new Thread(() =>
        {
            fs.EnterReadEpoch();
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            fs.ExitReadEpoch();
        })
        { IsBackground = true };
        reader.Start();
        entered.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("读者进入保护域");

        try
        {
            using (var b = fs.Open("b", RWOpts()))
            {
                b.Write(0, new byte[64 << 10]);
                b.SetLength(0);   // 收缩 → 16 块进入延迟回收队列（读者受保护中）
            }

            var act = () =>
            {
                using var c = fs.Open("c", RWOpts());
                c.Write(0, new byte[240 << 10]);   // 全卷填充——回收块被占用中 → 必须 DiskFull
            };
            act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.DiskFull,
                "D1b：读者受保护期间回收块保持 used——分配器不可复用（杜绝 read-after-free）");
        }
        finally
        {
            release.Set();
            reader.Join(TimeSpan.FromSeconds(10));
        }

        // 读者退出后：分配路径推进回收（TryFreeRetiredLocked）→ 空间恢复可用
        var retry = () =>
        {
            using var c = fs.Open("c", RWOpts());
            c.Write(0, new byte[64 << 10]);
        };
        retry.Should().NotThrow("D1b：读者退出 + 推进后，回收块恢复可用");
    }

    // ═══════════════ D2：fs.Dispose 后句柄生命周期 ═══════════════

    [Fact]
    public void HandleOps_AfterFsDispose_ThrowObjectDisposed()
    {
        var path = NewVolumePath();
        IFileHandle h;
        using (var fs = RawFileSystem.New(RawCarrier.File(path), new RawFormatOptions { QuotaBytes = 8L << 20 }))
        {
            h = fs.Open("f", RWOpts());
            h.Write(0, new byte[64]);
        }   // fs.Dispose（clean 关闭 + 载体释放）

        var act = () => h.Write(0, new byte[1]);
        act.Should().Throw<ObjectDisposedException>("D2：卷已关闭——写操作显式失败（不得静默内存成功）");

        var read = () => h.Read(0, new byte[1]);
        read.Should().Throw<ObjectDisposedException>("D2：读同样显式失败");
        h.Dispose();
    }

    // ═══════════════ D4：unwritten 保真往返 ═══════════════

    [Fact]
    public void ImageRoundtrip_PreallocatedFile_PreservesAllocationAndZeroReads()
    {
        var srcPath = NewVolumePath();
        var dstPath = NewVolumePath();
        using var src = RawFileSystem.New(RawCarrier.File(srcPath), new RawFormatOptions { QuotaBytes = 16L << 20 });
        using var dst = RawFileSystem.New(RawCarrier.File(dstPath), new RawFormatOptions { QuotaBytes = 16L << 20 });
        using (var h = src.Open("pre", new FileOpenOptions
        {
            Access = AccessMode.ReadWrite,
            Mode = FileOpenMode.OpenOrCreate,
            Sharing = FileSharing.ReadWrite,
            PreallocateSize = 1 << 20,
        }))
        {
            h.Write(4096, new byte[4096].Select((_, i) => (byte)i).ToArray());
            h.Write(1 << 19, new byte[4096].Select((_, i) => (byte)0xAB).ToArray());
        }

        using var staging = new MemoryStream();
        var capture = RootSpaceImage.Capture(src, staging, new ImageOptions { Compression = ImageCompression.None });
        capture.RawBytes.Should().Be(8192, "D4：unwritten 段不占数据帧——只搬 written 数据");
        staging.Position = 0;
        RootSpaceImage.Restore(staging, dst);

        using var dh = dst.Open("pre", ROpts());
        dh.AllocatedSize.Should().Be(1 << 20, "D4：预分配语义重建（物理预留保真）");
        var buf = new byte[4096];
        dh.Read(4096, buf).Should().Be(4096);
        buf.Should().BeEquivalentTo(Enumerable.Range(0, 4096).Select(i => (byte)i), "written 数据保真");
        dh.Read(1 << 19, buf).Should().Be(4096);
        buf.Should().OnlyContain(b => b == 0xAB, "written 数据保真（尾段）");
        dh.Read(0, buf).Should().Be(4096);
        buf.Should().OnlyContain(b => b == 0, "D4：unwritten 区读零（非陈旧载体内容）");
        dh.Read(8192, buf).Should().Be(4096);
        buf.Should().OnlyContain(b => b == 0, "D4：中间 unwritten 区读零");
    }

    // ═══════════════ D5：非可寻址流还原 ═══════════════

    private sealed class NonSeekableStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void Restore_FromNonSeekableStream_Succeeds()
    {
        using var src = MemoryFileSystem.New();
        using var dst = MemoryFileSystem.New();
        src.EnsureRoot();
        using (var h = src.Open("data", RWOpts()))
            h.Write(0, new byte[300_000].Select((_, i) => (byte)(i % 251)).ToArray());
        src.CreateDirectory("sub");
        using (var h = src.Open("sub/nested", RWOpts()))
            h.Write(0, new byte[1234].Select((_, i) => (byte)(i % 97)).ToArray());

        using var staging = new MemoryStream();
        RootSpaceImage.Capture(src, staging);
        staging.Position = 0;
        using var stream = new NonSeekableStream(staging);   // 网络/管道形态——Position/Length 全拒

        var summary = RootSpaceImage.Restore(stream, dst);

        summary.EntryCount.Should().BeGreaterThan(0);
        using var dh = dst.Open("data", ROpts());
        var buf = new byte[1000];
        dh.Read(123456, buf).Should().Be(1000);
        buf.Should().BeEquivalentTo(Enumerable.Range(0, 1000).Select(i => (byte)((123456 + i) % 251)), "D5：纯流式还原数据保真");
    }

    [Fact]
    public void Restore_TruncatedStream_Throws()
    {
        using var src = MemoryFileSystem.New();
        using var dst = MemoryFileSystem.New();
        src.EnsureRoot();
        using (var h = src.Open("data", RWOpts()))
            h.Write(0, new byte[100_000]);

        using var staging = new MemoryStream();
        RootSpaceImage.Capture(src, staging);
        var bytes = staging.ToArray();
        using var truncated = new MemoryStream(bytes, 0, bytes.Length - 10);   // 砍掉流尾

        var act = () => RootSpaceImage.Restore(truncated, dst);
        act.Should().Throw<FileIOException>("D5：截断流显式失败（缺 TCE1 对账）");
    }

    // ═══════════════ D7：ReadOnlyVolume 专用错误码 ═══════════════

    [Fact]
    public void ReadOnlyOpen_WriteIntent_ThrowsReadOnlyVolume()
    {
        var path = NewVolumePath();
        using (var fs = RawFileSystem.New(RawCarrier.File(path), new RawFormatOptions { QuotaBytes = 8L << 20 })) { }

        using var ro = RawFileSystem.Open(RawCarrier.File(path), new RawOpenOptions { Access = AccessMode.Read });
        var act = () => ro.Open("f", RWOpts());
        act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.ReadOnlyVolume,
            "D7：只读卷写意图——专用判别码（不与 AccessDenied 权限问题混淆）");
    }

    // ═══════════════ D8：跨成员碎片 Map 补救 ═══════════════

    [Fact]
    public void CrossMemberExtent_Map_DefragLandsInSingleMember()
    {
        var v1 = NewVolumePath();
        var v2 = NewVolumePath();
        var fs = RawFileSystem.New(RawCarrier.File(v1),
            new RawFormatOptions { QuotaBytes = 256L << 10, JournalReserveBytes = 0 });
        _openFs.Add(fs);
        fs.AddCarrier(RawCarrier.File(v2), 256L << 10);   // 第二成员容纳物化 run

        // A 占满成员 0 数据区（块 4..56）→ B 的 40KB 连续 run 必跨成员边界（57..66）
        using (var a = fs.Open("a", RWOpts()))
            a.Write(0, new byte[212 << 10]);
        using var b = fs.Open("b", RWOpts());
        b.Write(0, new byte[40 << 10].Select((_, i) => (byte)(i % 251)).ToArray());

        // 单连续但跨成员 → Map 自动物化整理（成员内分配）→ 可映射
        using (var map = b.Map(0, 4096, AccessMode.Read))
        {
            map.View.Span[0].Should().Be(0, "物化后数据保真");
            map.View.Span[123].Should().Be(123);
        }
        b.Dispose();

        using var b2 = fs.Open("b", ROpts());
        using var map2 = b2.Map(0, b2.Length, AccessMode.Read);
        map2.View.Length.Should().Be((int)b2.Length, "D8：单成员连续区间直映射成立（跨成员文件经整理可映射）");
    }

    // ═══════════════ D10：Move 后跨代句柄 Append 原子 ═══════════════

    [Fact]
    public void Move_OpenSourceHandle_NewHandleSharesCursor_NoOverwrite()
    {
        using var fs = Format(16L << 20);
        using var h1 = fs.Open("a", RWOpts());
        h1.Write(0, new byte[4096].Select((_, i) => (byte)0x11).ToArray());

        fs.Move("a", "b");
        using var h2 = fs.Open("b", RWOpts());   // 新代句柄（D10：须与 h1 共享游标）

        var results = new long[2];
        Parallel.Invoke(
            () => results[0] = h1.Append(new byte[1000].Select((_, i) => (byte)0x22).ToArray()),
            () => results[1] = h2.Append(new byte[1000].Select((_, i) => (byte)0x33).ToArray()));

        results.Should().OnlyHaveUniqueItems("D10：移动后新旧句柄共享同一追加游标——预留不重叠");
        h1.Length.Should().Be(4096 + 2000);
    }
}
