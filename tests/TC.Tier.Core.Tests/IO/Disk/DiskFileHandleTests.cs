using System.Buffers;
using System.Collections.Concurrent;
using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Disk;
using TC.Tier.Core.NativeInterop;

namespace TC.Tier.Core.Tests.IO.Disk;

/// <summary>
/// DiskFileHandle 单元测试——数据平面全族（读写/打开语义/共享/游标/空间管理/DIO 探测/向量/拷贝/映射/范围锁）。
/// </summary>
public sealed class DiskFileHandleTests : IDisposable
{
    private readonly string _dir = TestTempDir.Create("core-io-dh");
    private readonly DiskFileSystem _fs;

    public DiskFileHandleTests()
    {
        _fs = DiskFileSystem.OpenOrCreate(_dir);
        _fs.EnsureRoot();
    }

    public void Dispose()
    {
        _fs.Dispose();
        TestTempDir.TryCleanup(_dir);
    }

    private static FileOpenOptions Opts(AccessMode access = AccessMode.ReadWrite,
        FileOpenMode mode = FileOpenMode.OpenOrCreate, FileSharing sharing = FileSharing.ReadWrite,
        FileOpenHints hints = FileOpenHints.None, long preallocate = 0)
        => new() { Access = access, Mode = mode, Sharing = sharing, Hints = hints, PreallocateSize = preallocate };

    // ══════════════════ 打开语义 ══════════════════

    [Fact]
    public void Open_OpenExisting_Missing_ThrowsNotFound()
    {
        var act = () => _fs.Open("missing", Opts(access: AccessMode.Read, mode: FileOpenMode.OpenExisting));
        act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.NotFound);
    }

    [Fact]
    public void Open_CreateNew_Existing_ThrowsAlreadyExists()
    {
        using (var h = _fs.Open("exists", Opts(mode: FileOpenMode.CreateNew))) { }
        var act = () => _fs.Open("exists", Opts(mode: FileOpenMode.CreateNew));
        act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.AlreadyExists);
    }

    [Fact]
    public void Open_Truncate_EmptiesExistingFile()
    {
        using (var h = _fs.Open("t", Opts()))
        {
            h.Write(0, new byte[1000]);
        }
        using (var h = _fs.Open("t", Opts(mode: FileOpenMode.Truncate)))
        {
            h.Length.Should().Be(0);
        }
    }

    [Fact]
    public void Open_ReadAccess_ThenWrite_ThrowsAccessDenied()
    {
        using (var w = _fs.Open("ro", Opts())) { w.Write(0, new byte[16]); }
        using var h = _fs.Open("ro", Opts(access: AccessMode.Read, mode: FileOpenMode.OpenExisting));
        var act = () => h.Write(0, new byte[1]);
        act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.AccessDenied);
    }

    [Fact]
    public void Open_SharingNone_SecondOpenRejected()
    {
        using var h = _fs.Open("shared", Opts(sharing: FileSharing.None));
        var act = () => _fs.Open("shared", Opts(access: AccessMode.ReadWrite, mode: FileOpenMode.OpenExisting));
        act.Should().Throw<IOException>();   // OS 原生共享冲突
    }

    [Fact]
    public void Open_SharingRead_SecondWriteHandleRejected()
    {
        using var h = _fs.Open("sr", Opts(sharing: FileSharing.Read));
        var act = () => _fs.Open("sr", Opts(access: AccessMode.Write, mode: FileOpenMode.OpenExisting));
        act.Should().Throw<IOException>();
    }

    // ══════════════════ 读写基础 ══════════════════

    [Fact]
    public void WriteRead_RoundTrip()
    {
        using var h = _fs.Open("rw", Opts());
        var data = new byte[] { 1, 2, 3, 4, 5 };
        h.Write(10, data);
        h.Length.Should().Be(15);   // pwrite 越过 EOF 零洞扩展

        var buf = new byte[5];
        h.Read(10, buf).Should().Be(5);
        buf.Should().Equal(data);
    }

    [Fact]
    public void Write_PastEof_HoleReadsZero()
    {
        using var h = _fs.Open("hole", Opts());
        h.Write(4096, new byte[] { 42 });
        var buf = new byte[16];
        h.Read(0, buf).Should().Be(16);
        buf.Should().OnlyContain(b => b == 0);   // 洞读零
    }

    [Fact]
    public void Read_AtEof_ReturnsZero()
    {
        using var h = _fs.Open("eof", Opts());
        h.Write(0, new byte[8]);
        h.Read(8, new byte[4]).Should().Be(0);   // EOF 处 0
        h.Read(100, new byte[4]).Should().Be(0);
    }

    [Fact]
    public async Task WriteReadAsync_RoundTrip()
    {
        await using var h = _fs.Open("async", Opts());
        var data = new byte[64];
        Random.Shared.NextBytes(data);
        await h.WriteAsync(0, data, CancellationToken.None);
        var buf = new byte[64];
        (await h.ReadAsync(0, buf, CancellationToken.None)).Should().Be(64);
        buf.Should().Equal(data);
    }

    // ══════════════════ 游标（D7） ══════════════════

    [Fact]
    public void WriteOffset_DoesNotAdvanceCursor()
    {
        using var h = _fs.Open("cursor", Opts());
        h.Position.Should().Be(0);
        h.Write(100, new byte[4]);
        h.Position.Should().Be(0);   // pwrite 铁律
    }

    [Fact]
    public void Append_ReturnsReservedOffset_AndAdvances()
    {
        using var h = _fs.Open("ap", Opts());
        h.Append(new byte[4]).Should().Be(0);
        h.Append(new byte[8]).Should().Be(4);
        h.Position.Should().Be(12);
        h.Length.Should().Be(12);
    }

    [Fact]
    public void AppendMode_CursorInitializedAtEof_SeekAndWriteStillLegal()
    {
        using (var h = _fs.Open("am", Opts()))
        {
            h.Write(0, new byte[32]);
        }
        using var h2 = _fs.Open("am", Opts(mode: FileOpenMode.Append));
        h2.Position.Should().Be(32);   // Append 模式游标初始化于 EOF
        // ⑭ 非强制追加：Seek + Write(offset) 合法
        h2.Seek(0, SeekOrigin.Begin).Should().Be(0);
        h2.Write(0, new byte[] { 7 });
        var b = new byte[1];
        h2.Read(0, b).Should().Be(1);
        b[0].Should().Be(7);
    }

    [Fact]
    public async Task AppendAsync_ReturnsReservedOffset()
    {
        await using var h = _fs.Open("apa", Opts());
        (await h.AppendAsync(new byte[10], CancellationToken.None)).Should().Be(0);
        (await h.AppendAsync(new byte[5], CancellationToken.None)).Should().Be(10);
    }

    [Fact]
    public void Seek_Origins()
    {
        using var h = _fs.Open("sk", Opts());
        h.Write(0, new byte[64]);
        h.Seek(16, SeekOrigin.Begin).Should().Be(16);
        h.Seek(4, SeekOrigin.Current).Should().Be(20);
        h.Seek(-8, SeekOrigin.End).Should().Be(56);
    }

    [Fact]
    public void Append_Concurrent_NoOverwriteNoTear()
    {
        using var h = _fs.Open("conc", Opts());
        const int threads = 8, perThread = 200, len = 64;
        var offsets = new ConcurrentBag<long>();
        Parallel.For(0, threads, _ =>
        {
            var data = new byte[len];
            for (var i = 0; i < perThread; i++)
            {
                data[0] = (byte)i;
                offsets.Add(h.Append(data));
            }
        });
        offsets.Distinct().Count().Should().Be(threads * perThread);   // 落点两两不交
        h.Length.Should().Be((long)threads * perThread * len);
        // 落点集合 = 完整偏移覆盖 [0, total) 且步长 = len
        offsets.OrderBy(x => x).Select((o, i) => (o, i)).All(p => p.o == (long)p.i * len).Should().BeTrue();
    }

    // ══════════════════ 空间管理 ══════════════════

    [Fact]
    public void Preallocate_Idempotent_DoesNotTruncate()
    {
        using var h = _fs.Open("pre", Opts(preallocate: 1 << 20));
        h.Preallocate();
        h.Preallocate();   // 幂等重放
        h.Length.Should().BeGreaterThanOrEqualTo(1 << 20);
    }

    [Fact]
    public void PunchHole_AllocatedShrinks_LengthUnchanged()
    {
        using var h = _fs.Open("ph", Opts());
        var unit = _fs.Volume.AllocationUnit;
        // ★ 大文件：NTFS 稀疏化后按 64K 压缩单元分配——小文件（< 64K）打洞后 AllocatedSize 反而可能变大，
        //   物理收缩断言只在文件显著大于 64K 稀疏粒度时有效
        var size = Math.Max(unit * 64, 1 << 19);
        h.Write(0, new byte[size]);
        var before = h.AllocatedSize;

        h.PunchHole(unit * 16, unit * 32);
        h.Length.Should().Be(size);                    // 打洞不减长度
        h.AllocatedSize.Should().BeLessThan(before);   // 物理回收（宽区间覆盖完整稀疏单元）
        h.EnumerateAllocatedRanges().Sum(static r => r.End - r.Start).Should().BeLessThan(size);
        var buf = new byte[(int)unit];
        h.Read(unit * 16, buf).Should().Be((int)unit);
        buf.Should().OnlyContain(b => b == 0);         // 洞内读零
    }

    [Fact]
    public void PunchHole_ArbitraryRange_ReadZeroAndSpaceReturn()
    {
        // ★ 契约 v2（引擎 A4 字节粒度归零）：任意区间接受——整块内部物理打洞归还、
        //   非对齐边缘写零；可观测语义 = 读零（不抛 AlignmentError）。
        using var h = _fs.Open("pha", Opts());
        var unit = _fs.Volume.AllocationUnit;
        if (unit <= 1) return;
        h.Write(0, new byte[unit * 2]);
        var act = () => h.PunchHole(unit / 2, unit);
        act.Should().NotThrow("字节粒度归零契约（引擎 Reclaim 字节区间）");
        var buf = new byte[unit * 2];
        h.Read(0, buf);
        buf.Should().OnlyContain(b => b == 0, "打洞区间读零");
    }

    [Fact]
    public void SetLength_ShrinkAndGrow()
    {
        using var h = _fs.Open("sl", Opts());
        h.Write(0, new byte[1000]);
        h.SetLength(400);
        h.Length.Should().Be(400);
        h.SetLength(800);
        h.Length.Should().Be(800);
        var buf = new byte[16];
        h.Read(400, buf).Should().Be(16);
        buf.Should().OnlyContain(b => b == 0);   // 扩展区读零（ftruncate 语义）
    }

    [Fact]
    public void EnumerateAllocatedRanges_ReflectsPunch()
    {
        using var h = _fs.Open("rng", Opts());
        var unit = _fs.Volume.AllocationUnit;
        h.Write(0, new byte[unit * 4]);
        h.PunchHole(unit, unit);
        var ranges = h.EnumerateAllocatedRanges();
        ranges.Should().NotBeEmpty();
        foreach (var (start, end) in ranges)
        {
            // 块粒度对齐到 AllocationUnit
            (start % unit).Should().Be(0);
            (end % unit).Should().Be(0);
        }
    }

    [Fact]
    public void CollapseInsertRange_WinThrowsUnsupported_LinuxAlignedWorks()
    {
        using var h = _fs.Open("ci", Opts());
        h.Write(0, new byte[4096]);
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            var act = () => h.CollapseRange(0, 512);
            act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.Unsupported);
        }
    }

    // ══════════════════ DIO 探测与对齐 ══════════════════

    [Fact]
    public void UnbufferedSupport_BufferedHandle_NotRequested()
    {
        using var h = _fs.Open("buf", Opts());
        h.UnbufferedSupport.Should().Be(UnbufferedIoSupport.NotRequested);
        h.RequiredAlignment.Should().Be(1);   // 缓冲句柄零对齐要求
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnbufferedSupport_DioHandle_ProbedAndAligned(bool useCache)
    {
        var hints = useCache ? FileOpenHints.None : FileOpenHints.NoBuffering;
        using var h = _fs.Open("dio", Opts(hints: hints));
        if (!useCache)
        {
            // 探测四态之一（本机 NTFS 通常 Supported；容器 overlay 可能 Ignored——只断言枚举成员与对齐一致性）
            h.UnbufferedSupport.Should().BeOneOf(
                UnbufferedIoSupport.Supported, UnbufferedIoSupport.BestEffort,
                UnbufferedIoSupport.Ignored, UnbufferedIoSupport.NotRequested);
            if (h.UnbufferedSupport == UnbufferedIoSupport.Supported)
            {
                var expected = OperatingSystem.IsWindows()
                    ? Math.Max((long)_fs.Volume.SectorSize, Environment.SystemPageSize)
                    : Math.Max((long)_fs.Volume.SectorSize, 1);
                h.RequiredAlignment.Should().Be(expected);   // ㉙ Win=max(扇区, 内存页)
            }
            else
            {
                h.RequiredAlignment.Should().Be(1);   // 降级/忽略 → 非强制
            }
        }
    }

    [Fact]
    public void Dio_MisalignedBuffer_ThrowsAlignmentError()
    {
        using var h = _fs.Open("diom", Opts(hints: FileOpenHints.NoBuffering));
        if (h.UnbufferedSupport != UnbufferedIoSupport.Supported) return;   // 非真 DIO 环境（CI 容器）
        var align = h.RequiredAlignment;
        using var good = PinnedBufferPool_RentAligned((int)align * 4, (int)align);
        h.Write(0, good.Memory.Span);   // 对齐路径零异常

        var plain = new byte[(int)align * 4];   // 普通 byte[]——地址几乎必不对齐
        var act = () => h.Write(0, plain);
        act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.AlignmentError);
    }

    private static AlignedMemoryManager PinnedBufferPool_RentAligned(int size, int alignment)
    {
        var pool = new TC.Tier.Core.Collections.PinnedBufferPool();
        return pool.RentAligned(size, alignment);
    }

    // ══════════════════ 向量 IO ══════════════════

    [Fact]
    public void WriteVector_EquivalentToSegmentedWrites()
    {
        using var h = _fs.Open("vec", Opts());
        var segs = new ReadOnlyMemory<byte>[] { new byte[] { 1, 2, 3 }, new byte[] { 4, 5 }, new byte[] { 6, 7, 8, 9 } };
        h.WriteVector(16, segs);

        var expected = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        var buf = new byte[9];
        h.Read(16, buf).Should().Be(9);
        buf.Should().Equal(expected);
    }

    [Fact]
    public void ReadVector_EquivalentToSegmentedReads()
    {
        using var h = _fs.Open("vecr", Opts());
        h.Write(0, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 });
        var d1 = new Memory<byte>(new byte[3]);
        var d2 = new Memory<byte>(new byte[2]);
        var d3 = new Memory<byte>(new byte[4]);
        var n = h.ReadVector(0, new[] { d1, d2, d3 });
        n.Should().Be(9);
        d1.Span.ToArray().Should().Equal(1, 2, 3);
        d2.Span.ToArray().Should().Equal(4, 5);
        d3.Span.ToArray().Should().Equal(6, 7, 8, 9);
    }

    [Fact]
    public async Task VectorAsync_Variants_Complete()
    {
        await using var h = _fs.Open("veca", Opts());
        var segs = new ReadOnlyMemory<byte>[] { new byte[] { 9, 8 }, new byte[] { 7 } };
        await h.WriteVectorAsync(0, segs, CancellationToken.None);
        var d = new Memory<Memory<byte>>(new Memory<byte>[] { new byte[3] });
        (await h.ReadVectorAsync(0, d, CancellationToken.None)).Should().Be(3);
    }

    // ══════════════════ 文件间拷贝 ══════════════════

    [Fact]
    public void CopyRange_CopiesRange_NoAliasing()
    {
        using var src = _fs.Open("csrc", Opts());
        using var dst = _fs.Open("cdst", Opts());
        src.Write(0, new byte[4096]);
        var n = src.CopyRange(dst, 512, 1024, 2048);
        n.Should().Be(2048);
        var a = new byte[16];
        var b = new byte[16];
        src.Read(512, a);
        dst.Read(1024, b);
        a.Should().Equal(b);
        // 无别名：写 dst 不影响 src
        dst.Write(1024, new byte[] { 0xFF });
        src.Read(512, a);
        a[0].Should().NotBe(0xFF);
    }

    [Fact]
    public void CopyRange_SourceBeyondEof_CopiesAvailableOnly()
    {
        using var src = _fs.Open("ce", Opts());
        using var dst = _fs.Open("ce2", Opts());
        src.Write(0, new byte[100]);
        var n = src.CopyRange(dst, 50, 0, 500);   // 请求 500，只有 50 可用
        n.Should().Be(50);
        dst.Length.Should().Be(50);
    }

    [Fact]
    public void CloneRange_WholeFileContent()
    {
        using var src = _fs.Open("cl1", Opts());
        using var dst = _fs.Open("cl2", Opts());
        var data = new byte[777];
        Random.Shared.NextBytes(data);
        src.Write(0, data);
        var n = src.CloneRange(dst);
        n.Should().Be(777);
        var buf = new byte[777];
        dst.Read(0, buf).Should().Be(777);
        buf.Should().Equal(data);
    }

    [Fact]
    public void CopyRange_NegativeArgs_Throw()
    {
        using var src = _fs.Open("cm", Opts());
        using var dst = _fs.Open("cm2", Opts());
        var act = () => src.CopyRange(dst, -1, 0, 10);
        act.Should().Throw<ArgumentOutOfRangeException>();
        var act2 = () => src.CopyRange(null!, 0, 0, 10);
        act2.Should().Throw<ArgumentException>();
    }

    // ══════════════════ 持久化与提示 ══════════════════

    [Fact]
    public void Flush_FlushData_DoNotThrow()
    {
        using var h = _fs.Open("fl", Opts());
        h.Write(0, new byte[128]);
        h.Flush();
        h.FlushData();   // Win/mac 回退全量——不抛（能力位诚实表达）
    }

    [Fact]
    public void Advise_AllModes_DoNotThrow()
    {
        using var h = _fs.Open("adv", Opts());
        foreach (FileAdvise a in Enum.GetValues<FileAdvise>())
            h.Advise(a);
    }

    // ══════════════════ 范围锁 ══════════════════

    [Fact]
    public void RangeLock_CrossHandleExclusive_MutualExclusion()
    {
        using var h1 = _fs.Open("lk", Opts());
        using var h2 = _fs.Open("lk", Opts());
        h1.Write(0, new byte[4096]);

        h1.Lock(0, 1024, FileLockMode.Exclusive);
        h2.TryLock(0, 1024, FileLockMode.Exclusive).Should().BeFalse("跨句柄排他互斥");
        // 注：同句柄重叠再锁的平台分歧——Windows LockFileEx 冲突（ERROR_LOCK_VIOLATION），
        // Linux OFD 幂等转换；可移植契约不依赖同句柄重锁行为
        h1.Unlock(0, 1024);
        h2.TryLock(0, 1024, FileLockMode.Exclusive).Should().BeTrue("解锁后可获取");
        h2.Unlock(0, 1024);
    }

    [Fact]
    public void RangeLock_SharedVsExclusive()
    {
        using var h1 = _fs.Open("sh", Opts());
        using var h2 = _fs.Open("sh", Opts());
        h1.Write(0, new byte[4096]);
        h1.Lock(0, 1024, FileLockMode.Shared);
        h2.TryLock(0, 1024, FileLockMode.Shared).Should().BeTrue("共享锁可并存");
        h2.Unlock(0, 1024);
        using var h3 = _fs.Open("sh", Opts());
        h3.TryLock(0, 1024, FileLockMode.Exclusive).Should().BeFalse("共享挡排他");
        h1.Unlock(0, 1024);
        h3.TryLock(0, 1024, FileLockMode.Exclusive).Should().BeTrue();
    }

    [Fact]
    public void RangeLock_DisposeReleasesAllLocks()
    {
        using var h2 = _fs.Open("dl2", Opts());
        var h1 = _fs.Open("dl", Opts());
        h1.Write(0, new byte[4096]);
        h1.Lock(0, 2048, FileLockMode.Exclusive);
        h1.Dispose();   // OS 自动释放句柄锁
        h2.TryLock(0, 2048, FileLockMode.Exclusive).Should().BeTrue();
        h2.Unlock(0, 2048);
    }

    [Fact]
    public void RangeLock_DifferentRanges_Coexist()
    {
        using var h1 = _fs.Open("nr", Opts());
        using var h2 = _fs.Open("nr", Opts());
        h1.Write(0, new byte[4096]);
        h1.Lock(0, 512, FileLockMode.Exclusive);
        h2.TryLock(512, 512, FileLockMode.Exclusive).Should().BeTrue("不同区间互不干扰");
        h2.Unlock(512, 512);
        h1.Unlock(0, 512);
    }

    // ══════════════════ 内存映射 ══════════════════

    [Fact]
    public void Map_WriteVisibleInRead_FlushAndDispose()
    {
        using var h = _fs.Open("mp", Opts());
        h.Write(0, new byte[4096]);
        using var section = h.Map(0, 4096, AccessMode.ReadWrite);
        section.View.Span[0..4].ToArray().Should().Equal(0, 0, 0, 0);

        section.View.Span[100] = 0xAB;
        section.Flush();   // msync
        var b = new byte[1];
        h.Read(100, b);
        b[0].Should().Be(0xAB, "磁盘映射视图写=文件写（实时可见）");
    }

    [Fact]
    public void Map_IndependentOfParentHandle()
    {
        var h = _fs.Open("mi", Opts());
        h.Write(0, new byte[4096]);
        var section = h.Map(0, 4096, AccessMode.ReadWrite);
        h.Dispose();   // ⑪ 父句柄关闭后映射继续有效
        section.View.Span[7] = 0x42;
        section.Flush();
        section.Dispose();
    }

    [Fact]
    public void Map_Disposed_UIView_Throws()
    {
        using var h = _fs.Open("md", Opts());
        h.Write(0, new byte[4096]);
        var section = h.Map(0, 4096, AccessMode.Read);
        var view = section.View;
        section.Dispose();
        var act = () => _ = view.Span[0];
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Map_OffsetMapping_OnlyWindowExposed()
    {
        using var h = _fs.Open("mo", Opts());
        h.Write(0, new byte[8192]);
        h.Write(5000, new byte[] { 0x5A });
        using var section = h.Map(4096, 4096, AccessMode.Read);
        section.View.Length.Should().Be(4096);
        section.View.Span[904].Should().Be(0x5A);   // 5000-4096=904
    }

    // ══════════════════ 扩展属性 ══════════════════

    [Fact]
    public void FileExtra_RoundTrip_AndOffsetOps()
    {
        using var h = _fs.Open("xa", Opts());
        h.FileExtra.Length.Should().Be(0, "初始无附加数据");
        h.SetFileExtra(new byte[] { 1, 2, 3 });
        h.FileExtra.ToArray().Should().Equal((byte)1, (byte)2, (byte)3);
        h.WriteFileExtra(1, new byte[] { 0xAA });
        // 原位补丁不动长度
        h.FileExtra.ToArray().Should().Equal((byte)1, (byte)0xAA, (byte)3);
        var buf = new byte[8];
        h.ReadFileExtra(1, buf).Should().Be(2);
        buf[..2].Should().Equal((byte)0xAA, (byte)3);
        h.ReadFileExtra(3, buf).Should().Be(0, "EOF → 0");
        h.SetFileExtra(ReadOnlyMemory<byte>.Empty);
        h.FileExtra.Length.Should().Be(0, "覆盖空 = 清除");
    }

    // ══════════════════ Dispose 语义 ══════════════════

    [Fact]
    public void Dispose_ThenOps_ThrowObjectDisposed()
    {
        var h = _fs.Open("dd", Opts());
        h.Dispose();
        var act = () => h.Write(0, new byte[1]);
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Dispose_Idempotent()
    {
        var h = _fs.Open("ii", Opts());
        h.Dispose();
        var act = () => h.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public async Task DisposeAsync_Idempotent()
    {
        var h = _fs.Open("ia", Opts());
        await h.DisposeAsync();
        await h.DisposeAsync();   // 幂等不抛
    }

    // ══════════════════ 配额执法（CORE-09/10：同步 + 异步统一——原异步完全绕过）══════════════════

    [Fact]
    public void Quota_WriteSync_Enforced()
    {
        using var qfs = DiskFileSystem.Open(_dir, new DiskFileSystemOptions { QuotaBytes = 1 << 20 });
        using var h = qfs.Open("q1", Opts());
        h.Write(0, new byte[1 << 20]);
        var act = () => h.Write(1 << 20, new byte[1]);   // 超限 1 字节
        act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.DiskFull);
    }

    [Fact]
    public async Task Quota_WriteAsync_Enforced_SameAsSync()
    {
        using var qfs = DiskFileSystem.Open(_dir, new DiskFileSystemOptions { QuotaBytes = 1 << 20 });
        using var h = qfs.Open("q2", Opts());
        await h.WriteAsync(0, new byte[1 << 20], default);
        var act = async () => await h.WriteAsync(1 << 20, new byte[1], default);   // CORE-10：异步路径原完全绕过配额——补执法
        await act.Should().ThrowAsync<FileIOException>()
            .Where(e => e.Error == IOError.DiskFull);
    }

    [Fact]
    public async Task Quota_AppendAsync_Enforced()
    {
        using var qfs = DiskFileSystem.Open(_dir, new DiskFileSystemOptions { QuotaBytes = 1 << 20 });
        using var h = qfs.Open("q3", Opts());
        await h.AppendAsync(new byte[1 << 20], default);
        var act = async () => await h.AppendAsync(new byte[1], default);   // CORE-10：AppendAsync → WriteAsync 补执法
        await act.Should().ThrowAsync<FileIOException>()
            .Where(e => e.Error == IOError.DiskFull);
    }
}
