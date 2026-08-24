using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Mem;

namespace TC.Tier.Core.Tests.IO.Mem;

/// <summary>
/// MemoryFileSystem/MemFileHandle 单元测试——功能面（CRUD/打开语义/共享/打洞/配额/双模式/映射/锁/游标）。
/// 并发竞态（⑯–㉓）见 MemoryFileSystemConcurrencyTests。
/// </summary>
public sealed class MemoryFileSystemTests
{
    private static MemoryFileSystem NewFs(MemoryAllocationMode mode = MemoryAllocationMode.Sparse,
        long? quota = null, int pageSize = 4096)
        => MemoryFileSystem.New(new MemoryFileSystemOptions
        {
            Allocation = mode,
            QuotaBytes = quota ?? -1,   // -1 = 无上限（基类哨兵；null 形参兼容既有用例）
            PageSize = pageSize,
        });

    private static FileOpenOptions Opts(AccessMode access = AccessMode.ReadWrite,
        FileOpenMode mode = FileOpenMode.OpenOrCreate, FileSharing sharing = FileSharing.ReadWrite,
        FileOpenHints hints = FileOpenHints.None, long preallocate = 0)
        => new() { Access = access, Mode = mode, Sharing = sharing, Hints = hints, PreallocateSize = preallocate };

    // ══════════════════ CRUD 与命名空间 ══════════════════

    [Theory]
    [InlineData(MemoryAllocationMode.Sparse)]
    [InlineData(MemoryAllocationMode.Reserved)]
    public void Crud_CreateWriteReadDelete(MemoryAllocationMode mode)
    {
        using var fs = NewFs(mode);
        fs.Exists("f").Should().BeFalse();
        using (var h = fs.Open("f", Opts(mode: FileOpenMode.CreateNew)))
        {
            h.Write(0, new byte[] { 1, 2, 3, 4 });
        }
        fs.Exists("f").Should().BeTrue();
        using (var h = fs.Open("f", Opts(access: AccessMode.Read, mode: FileOpenMode.OpenExisting)))
        {
            var buf = new byte[4];
            h.Read(0, buf).Should().Be(4);
            buf.Should().Equal(1, 2, 3, 4);
        }
        fs.Delete("f");
        fs.Exists("f").Should().BeFalse();
    }

    [Fact]
    public void Enumerate_FusedNameLength()
    {
        using var fs = NewFs();
        using (var h = fs.Open("a", Opts())) { h.Write(0, new byte[10]); }
        using (var h = fs.Open("b", Opts())) { h.Write(0, new byte[20]); }

        var entries = fs.EnumerateFiles().OrderBy(e => e.Name, StringComparer.Ordinal).ToArray();
        entries.Select(e => e.Name).Should().Equal("a", "b");
        entries[0].Length.Should().Be(10);
        entries[1].Length.Should().Be(20);
        fs.EnumerateFilePaths().Should().BeEquivalentTo("a", "b");
    }

    [Fact]
    public void Enumerate_IsOrdinalCaseSensitive_AlignsLinux()
    {
        using var fs = NewFs();
        using (fs.Open("A", Opts())) { }
        using (fs.Open("a", Opts())) { }
        fs.EnumerateFiles().Select(e => e.Name).OrderBy(x => x, StringComparer.Ordinal)
            .Should().Equal("A", "a");   // 大小写共存（NTFS 差异文档化）
    }

    [Fact]
    public void Move_NoOverwrite_TargetExists_Throws()
    {
        using var fs = NewFs();
        using (fs.Open("src", Opts())) { }
        using (fs.Open("dst", Opts())) { }
        var act = () => fs.Move("src", "dst", overwrite: false);
        act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.AlreadyExists);
    }

    [Theory]
    [InlineData(MemoryAllocationMode.Sparse)]
    [InlineData(MemoryAllocationMode.Reserved)]
    public void Move_Overwrite_ReplacesContent(MemoryAllocationMode mode)
    {
        using var fs = NewFs(mode);
        using (var h = fs.Open("src", Opts())) { h.Write(0, new byte[] { 1, 2, 3 }); }
        using (var h = fs.Open("dst", Opts())) { h.Write(0, new byte[] { 9, 9, 9, 9 }); }
        fs.Move("src", "dst", overwrite: true);
        fs.Exists("src").Should().BeFalse();
        using (var h = fs.Open("dst", Opts(access: AccessMode.Read, mode: FileOpenMode.OpenExisting)))
        {
            var buf = new byte[4];
            h.Read(0, buf).Should().Be(3);
            buf[..3].Should().Equal(1, 2, 3);
        }
    }

    [Fact]
    public void Move_SourceHandle_ContinuesToReadOriginalData_PosixSemantics()
    {
        using var fs = NewFs(MemoryAllocationMode.Reserved);
        using (var h = fs.Open("src", Opts())) { h.Write(0, new byte[] { 7, 7, 7 }); }
        var srcHandle = fs.Open("src", Opts());
        fs.Move("src", "dst", overwrite: false);
        // POSIX fd 语义：改名后句柄继续有效（(slot,gen) 不受扰）
        var buf = new byte[3];
        srcHandle.Read(0, buf).Should().Be(3);
        buf.Should().Equal(7, 7, 7);
        srcHandle.Dispose();
    }

    [Fact]
    public void Move_Overwrite_OpenHandleOnDst_ReadsOldDataUntilClose()
    {
        using var fs = NewFs(MemoryAllocationMode.Reserved);
        using (var h = fs.Open("src", Opts())) { h.Write(0, new byte[] { 1, 1 }); }
        var oldDst = fs.Open("dst", Opts());
        oldDst.Write(0, new byte[] { 5, 5, 5, 5 });

        fs.Move("src", "dst", overwrite: true);
        fs.Exists("dst").Should().BeTrue();

        // 旧 dst 句柄读旧数据（Detached——POSIX rename 覆盖：旧 inode 延迟到 close）
        var buf = new byte[4];
        oldDst.Read(0, buf).Should().Be(4);
        buf.Should().Equal(5, 5, 5, 5);
        oldDst.Dispose();
    }

    [Fact]
    public void Delete_OpenHandle_ReadsOldDataUntilClose()
    {
        using var fs = NewFs(MemoryAllocationMode.Reserved);
        var h = fs.Open("f", Opts());
        h.Write(0, new byte[] { 3, 3, 3 });
        fs.Delete("f");
        fs.Exists("f").Should().BeFalse();
        var buf = new byte[3];
        h.Read(0, buf).Should().Be(3);   // POSIX unlink：名字先消、数据延迟
        buf.Should().Equal(3, 3, 3);
        h.Dispose();
    }

    [Fact]
    public void Delete_SharingDeleteFlag_NoObservableEffect()
    {
        // POSIX/mem 上删除本就无条件成功——Sharing.Delete 无观察效应（⑨ 断言集）
        using var fs = NewFs();
        var h = fs.Open("f", Opts(sharing: FileSharing.Delete));
        h.Write(0, new byte[8]);
        var act = () => fs.Delete("f");
        act.Should().NotThrow();
        fs.Exists("f").Should().BeFalse();
        h.Dispose();
    }

    // ══════════════════ 打开语义与共享 ══════════════════

    [Fact]
    public void Open_OpenExisting_Missing_ThrowsNotFound()
    {
        using var fs = NewFs();
        var act = () => fs.Open("missing", Opts(access: AccessMode.Read, mode: FileOpenMode.OpenExisting));
        act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.NotFound);
    }

    [Fact]
    public void Open_CreateNew_Existing_ThrowsAlreadyExists()
    {
        using var fs = NewFs();
        using (fs.Open("f", Opts(mode: FileOpenMode.CreateNew))) { }
        var act = () => fs.Open("f", Opts(mode: FileOpenMode.CreateNew));
        act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.AlreadyExists);
    }

    [Fact]
    public void Open_Truncate_EmptiesFile()
    {
        using var fs = NewFs();
        using (var h = fs.Open("f", Opts())) { h.Write(0, new byte[100]); }
        using (var h = fs.Open("f", Opts(mode: FileOpenMode.Truncate)))
        {
            h.Length.Should().Be(0);
        }
    }

    [Fact]
    public void Open_SharingConflict_SameFsInstance_Throws()
    {
        using var fs = NewFs();
        using var h1 = fs.Open("f", Opts(sharing: FileSharing.Read));
        var act = () => fs.Open("f", Opts(access: AccessMode.Write, mode: FileOpenMode.OpenExisting));
        act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.SharingViolation);
    }

    [Fact]
    public void Open_SharingNone_RejectsEverything()
    {
        using var fs = NewFs();
        using var h1 = fs.Open("f", Opts(sharing: FileSharing.None));
        var act = () => fs.Open("f", Opts(access: AccessMode.Read, mode: FileOpenMode.OpenExisting));
        act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.SharingViolation);
    }

    [Fact]
    public void Open_ReverseSharing_NewHandleTooRestrictive_Throws()
    {
        using var fs = NewFs();
        using var h1 = fs.Open("f", Opts(access: AccessMode.ReadWrite, sharing: FileSharing.ReadWrite));
        // 已有句柄需要写；新开句柄 Sharing=Read（不允许写）→ 反向冲突
        var act = () => fs.Open("f", Opts(access: AccessMode.Read, sharing: FileSharing.Read, mode: FileOpenMode.OpenExisting));
        act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.SharingViolation);
    }

    [Fact]
    public void Open_HandleDispose_UnregistersSharing()
    {
        using var fs = NewFs();
        var h1 = fs.Open("f", Opts(sharing: FileSharing.Read));
        h1.Dispose();
        var act = () => fs.Open("f", Opts(access: AccessMode.Write, mode: FileOpenMode.OpenExisting));
        act.Should().NotThrow();
    }

    // ══════════════════ 读写语义（pwrite 平权）══════════════════

    [Theory]
    [InlineData(MemoryAllocationMode.Sparse)]
    [InlineData(MemoryAllocationMode.Reserved)]
    public void Write_PastEof_ZeroExtends_HoleReadsZero(MemoryAllocationMode mode)
    {
        using var fs = NewFs(mode);
        using var h = fs.Open("f", Opts());
        h.Write(8192, new byte[] { 42 });
        h.Length.Should().Be(8193);
        var buf = new byte[16];
        h.Read(0, buf).Should().Be(16);
        buf.Should().OnlyContain(b => b == 0);   // 洞读零
        h.Read(8192, buf).Should().Be(1);
        buf[0].Should().Be(42);
    }

    [Fact]
    public void NewFileReadsZero_AfterPoolReuse_DeterministicZeroFill()
    {
        // 确定性零填充：写脏 → 删 → 新建同名 → 全读零（池复租不残留）
        using var fs = NewFs(MemoryAllocationMode.Reserved, quota: 1 << 20);
        using (var h = fs.Open("f", Opts()))
        {
            h.Write(0, new byte[4096]);
            h.Read(0, new byte[16]);   // 触碰
        }
        fs.Delete("f");
        using (var h = fs.Open("f", Opts()))
        {
            h.Write(0, new byte[1]);   // 触发租借（可能命中池的复租）
            var buf = new byte[4096];
            h.Read(0, buf);
            buf[1..].Should().OnlyContain(b => b == 0);
        }
    }

    [Theory]
    [InlineData(MemoryAllocationMode.Sparse)]
    [InlineData(MemoryAllocationMode.Reserved)]
    public async Task AsyncVariants_CompleteSynchronously(MemoryAllocationMode mode)
    {
        await using var h = NewFs(mode).Open("f", Opts());
        await h.WriteAsync(0, new byte[64], CancellationToken.None);
        var buf = new byte[64];
        (await h.ReadAsync(0, buf, CancellationToken.None)).Should().Be(64);
        // WriteAsync 是 pwrite（不推进游标）——Append 落点 0（D7 游标规则）
        (await h.AppendAsync(new byte[8], CancellationToken.None)).Should().Be(0);
    }

    // ══════════════════ 游标（D7）══════════════════

    [Fact]
    public void Cursor_PwriteDoesNotAdvance_AppendReserves()
    {
        using var fs = NewFs();
        using var h = fs.Open("f", Opts());
        h.Position.Should().Be(0);
        h.Write(100, new byte[4]);
        h.Position.Should().Be(0);   // pwrite 不动游标
        h.Append(new byte[4]).Should().Be(0);
        h.Append(new byte[8]).Should().Be(4);
        h.Position.Should().Be(12);
        h.Seek(0, SeekOrigin.End).Should().Be(104);   // End 基准 = 文件长度（100+4）
    }

    [Fact]
    public void AppendMode_CursorAtEof_SeekWriteLegal()
    {
        using var fs = NewFs();
        using (var h = fs.Open("f", Opts())) { h.Write(0, new byte[32]); }
        using var h2 = fs.Open("f", Opts(mode: FileOpenMode.Append));
        h2.Position.Should().Be(32);
        h2.Seek(0, SeekOrigin.Begin);
        h2.Write(0, new byte[] { 9 });
        var b = new byte[1];
        h2.Read(0, b).Should().Be(1);
        b[0].Should().Be(9);
    }

    [Fact]
    public void Append_FailureOnDiskFull_CarriesReservedOffset_HoleReadsZero_HandleUsable()
    {
        // ★ 物理计费在换租瞬间为新旧双份（磁盘满时扩展失败的等价语义）——capacity 预留双份余量
        using var fs = NewFs(MemoryAllocationMode.Reserved, quota: 8192);
        using var h = fs.Open("f", Opts());
        h.Append(new byte[2048]).Should().Be(0);   // 占 2048
        var ex = Assert.Throws<FileIOException>(() => h.Append(new byte[5000]));
        ex.Error.Should().Be(IOError.DiskFull);
        ex.ReservedOffset.Should().Be(2048);       // ① 预留落点
        h.Length.Should().Be(2048);                // 洞未落地

        // ④ 句柄继续可用（容量内原地写——freeze 已在异常路径解除，不锁死写者）
        var act = () => h.Write(0, new byte[1024]);
        act.Should().NotThrow();
        var buf = new byte[2048];
        h.Read(0, buf);
        buf.Should().OnlyContain(b => b == 0);
    }

    // ══════════════════ 空间管理 ══════════════════

    [Fact]
    public void Reserved_AllocatedSizeEqualsLength_PunchHoleShrinksAccounting()
    {
        using var fs = NewFs(MemoryAllocationMode.Reserved);
        using var h = fs.Open("f", Opts());
        h.Write(0, new byte[8192]);
        h.AllocatedSize.Should().Be(8192);   // ⑦ Reserved 口径：AllocatedSize == Length

        h.PunchHole(0, 4096);
        h.Length.Should().Be(8192);
        h.AllocatedSize.Should().Be(4096);   // 记账收缩（物理不还——容量预留）
        var buf = new byte[16];
        h.Read(0, buf);
        buf.Should().OnlyContain(b => b == 0);
    }

    [Fact]
    public void Sparse_PhysicalAccounting_PunchHoleReleasesPages()
    {
        using var fs = NewFs(MemoryAllocationMode.Sparse, pageSize: 4096);
        using var h = fs.Open("f", Opts());
        h.Write(0, new byte[16384]);   // 4 页
        h.AllocatedSize.Should().Be(16384);

        h.PunchHole(0, 4096);          // 整页 → 物理释放
        h.AllocatedSize.Should().Be(12288);
        h.EnumerateAllocatedRanges().Sum(static r => r.End - r.Start).Should().Be(12288);
        var buf = new byte[16];
        h.Read(0, buf);
        buf.Should().OnlyContain(b => b == 0);   // 洞内读零（读路径无掩蔽）

        // 对齐契约内的洞必为整页倍（非对齐 punch 抛 AlignmentError——另有专项）
        h.PunchHole(8192, 4096);
        h.AllocatedSize.Should().Be(8192);

        // 大文件写少量 → 物理占用 ≈ 已写页
        using var h2 = fs.Open("big", Opts());
        h2.Write(0, new byte[1]);
        h2.Length.Should().Be(1);
        h2.AllocatedSize.Should().Be(4096);      // 一页
    }

    [Fact]
    public void Sparse_GrowFile_NoPhysicalAllocation()
    {
        using var fs = NewFs(MemoryAllocationMode.Sparse, pageSize: 4096);
        fs.CreateOrReplaceFile("f", 1 << 20);   // 1MB 逻辑
        using var h = fs.Open("f", Opts());
        h.Length.Should().Be(1 << 20);
        h.AllocatedSize.Should().Be(0);   // 逻辑扩展零物理
    }

    [Theory]
    [InlineData(MemoryAllocationMode.Sparse)]
    [InlineData(MemoryAllocationMode.Reserved)]
    public void PunchHole_ArbitraryRange_ReadZero_BothModes(MemoryAllocationMode mode)
    {
        // ★ 契约 v2：mem 字节精确 extent 洞（Reserved memset / Sparse 页边界零化）——不抛 AlignmentError。
        using var fs = NewFs(mode, pageSize: 4096);
        using var h = fs.Open("f", Opts());
        h.Write(0, new byte[8192]);
        var act = () => h.PunchHole(2048, 4096);
        act.Should().NotThrow("mem 字节粒度归零契约（引擎 Reclaim 字节区间）");
        var buf = new byte[8192];
        h.Read(0, buf);
        buf.Should().OnlyContain(b => b == 0, "打洞区间读零");
    }

    [Fact]
    public void GrowFile_PreservesData_BothModes()
    {
        foreach (var mode in new[] { MemoryAllocationMode.Sparse, MemoryAllocationMode.Reserved })
        {
            using var fs = NewFs(mode);
            using var h = fs.Open("f", Opts());
            h.Write(0, new byte[] { 1, 2, 3 });
            fs.GrowFile("f", 8192);
            h.Length.Should().Be(8192);
            var buf = new byte[3];
            h.Read(0, buf);
            buf.Should().Equal(1, 2, 3);
        }
    }

    [Fact]
    public void Truncate_ShrinkThenReExtend_ReadsZero()
    {
        using var fs = NewFs(MemoryAllocationMode.Reserved);
        using var h = fs.Open("f", Opts());
        h.Write(0, new byte[8192]);
        h.SetLength(4096);
        h.Length.Should().Be(4096);
        h.SetLength(8192);
        var buf = new byte[4096];
        h.Read(4096, buf);
        buf.Should().OnlyContain(b => b == 0);   // 扩展区读零（ftruncate 语义）
    }

    [Fact]
    public void EnumerateAllocatedRanges_PageGranularity()
    {
        using var fs = NewFs(MemoryAllocationMode.Sparse, pageSize: 4096);
        using var h = fs.Open("f", Opts());
        h.Write(8192, new byte[8192]);   // 页 2-3
        var ranges = h.EnumerateAllocatedRanges();
        ranges.Should().Contain((8192L, 16384L));
        foreach (var (start, end) in ranges)
        {
            (start % 4096).Should().Be(0);
            (end % 4096).Should().Be(0);
        }
    }

    [Theory]
    [InlineData(MemoryAllocationMode.Sparse)]
    [InlineData(MemoryAllocationMode.Reserved)]
    public void CollapseInsertRange_DataShift(MemoryAllocationMode mode)
    {
        using var fs = NewFs(mode, pageSize: 512);
        using var h = fs.Open("f", Opts());
        h.Write(0, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        h.SetLength(8192);   // 对齐空间

        h.CollapseRange(512, 512);   // [512,1024) 移除 → 数据前移
        h.Length.Should().Be(8192 - 512);

        h.InsertRange(512, 512);
        h.Length.Should().Be(8192);
        var buf = new byte[8];
        h.Read(0, buf);
        buf.Should().Equal(1, 2, 3, 4, 5, 6, 7, 8);   // 头部数据不受影响（都在第一页内）
    }

    // ══════════════════ 配额 ══════════════════

    [Fact]
    public void Capacity_Exceeded_ThrowsDiskFull()
    {
        using var fs = NewFs(MemoryAllocationMode.Sparse, quota: 8192, pageSize: 4096);
        using var h = fs.Open("f", Opts());
        h.Write(0, new byte[8192]);
        var act = () => h.Write(8192, new byte[4096]);   // 越 EOF 扩展需新物理页 → 配额顶
        act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.DiskFull);
    }

    [Fact]
    public void Capacity_PhysicalAccounting_ReleaseThenRewrite()
    {
        // 配额按物理占用计：写满→释放→可再写
        using var fs = NewFs(MemoryAllocationMode.Sparse, quota: 8192, pageSize: 4096);
        using (var h = fs.Open("f", Opts()))
        {
            h.Write(0, new byte[8192]);
        }
        fs.Delete("f");   // 释放（无观察者——立即回收）
        var act = () =>
        {
            using var h2 = fs.Open("g", Opts());
            h2.Write(0, new byte[8192]);
        };
        act.Should().NotThrow("释放后物理占用归零，配额可复用");
    }

    // ══════════════════ 槽代际（ABA 防护）══════════════════

    [Fact]
    public void DeleteThenRecreate_OpenHandle_ReadsOldData_PosixSemantics()
    {
        using var fs = NewFs(MemoryAllocationMode.Reserved);
        var h = fs.Open("f", Opts());
        h.Write(0, new byte[] { 1, 1, 1 });
        fs.Delete("f");
        fs.CreateOrReplaceFile("f", 3);
        using var h2 = fs.Open("f", Opts());
        h2.Write(0, new byte[] { 2, 2, 2 });

        // 句柄未关闭 → 槽 Detached 不复用 → 旧句柄继续读旧数据（引用计数防护——非 ABA 场景）
        var buf = new byte[3];
        h.Read(0, buf);
        buf.Should().Equal(1, 1, 1);
        h2.Read(0, buf);
        buf.Should().Equal(2, 2, 2);
        h.Dispose();
    }

    [Fact]
    public void StaleHandle_AfterDetach_ThrowsNotFound()
    {
        using var fs = NewFs(MemoryAllocationMode.Reserved);
        var h = fs.Open("f", Opts());
        h.Write(0, new byte[16]);
        fs.DetachFile("f");   // 强制负载转移（Data 清空 + 代际 bump）——旧句柄失配
        var act = () => h.Read(0, new byte[4]);
        act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.NotFound);
        h.Dispose();
    }

    // ══════════════════ 映射 ══════════════════

    [Fact]
    public void Map_Reserved_DirectAddress_WriteThroughRealTime()
    {
        using var fs = NewFs(MemoryAllocationMode.Reserved);
        using var h = fs.Open("f", Opts());
        h.Write(0, new byte[4096]);
        using var section = h.Map(0, 4096, AccessMode.ReadWrite);
        section.View.Span[100] = 0xAB;
        var b = new byte[1];
        h.Read(100, b);
        b[0].Should().Be(0xAB, "Reserved 直址：视图写=文件写（实时可见）");
    }

    [Fact]
    public void Map_Sparse_MaterializedWriteBack_OnFlushAndDispose()
    {
        using var fs = NewFs(MemoryAllocationMode.Sparse, pageSize: 4096);
        using var h = fs.Open("f", Opts());
        h.Write(0, new byte[4096]);

        var section = h.Map(0, 4096, AccessMode.ReadWrite);
        section.View.Span[50] = 0xCD;
        // 可见性时差：Flush 前读旧值（文档化差异——需要实时可见用 Reserved/Write 路径）
        var b = new byte[1];
        h.Read(50, b);
        b[0].Should().Be(0);
        section.Flush();
        h.Read(50, b);
        b[0].Should().Be(0xCD, "Flush 写回后可见（写穿透契约）");
        section.Dispose();

        // Dispose 写回路径
        var s2 = h.Map(0, 4096, AccessMode.ReadWrite);
        s2.View.Span[60] = 0xEE;
        s2.Dispose();
        h.Read(60, b);
        b[0].Should().Be(0xEE);
    }

    [Fact]
    public void Map_Sparse_ReadOnly_NoWriteBack()
    {
        using var fs = NewFs(MemoryAllocationMode.Sparse, pageSize: 4096);
        using var h = fs.Open("f", Opts());
        h.Write(0, new byte[4096]);
        using var section = h.Map(0, 4096, AccessMode.Read);
        section.View.Span[50] = 0xCD;   // 快照内写（不影响文件——纯物化快照）
        var b = new byte[1];
        h.Read(50, b);
        b[0].Should().Be(0);
    }

    [Fact]
    public void Map_IndependentOfParentHandle()
    {
        using var fs = NewFs(MemoryAllocationMode.Reserved);
        var h = fs.Open("f", Opts());
        h.Write(0, new byte[4096]);
        var section = h.Map(0, 4096, AccessMode.ReadWrite);
        h.Dispose();   // ⑪ 父句柄关闭后映射继续有效
        section.View.Span[7] = 0x42;
        using (var h2 = fs.Open("f", Opts()))
        {
            var b = new byte[1];
            h2.Read(7, b);
            b[0].Should().Be(0x42);
        }
        section.Dispose();
    }

    [Fact]
    public void Map_GrowFileDuringMapping_OldViewStaysValid()
    {
        using var fs = NewFs(MemoryAllocationMode.Reserved);
        using var h = fs.Open("f", Opts());
        h.Write(0, new byte[4096]);
        using var section = h.Map(0, 4096, AccessMode.ReadWrite);
        section.View.Span[0] = 0x11;
        fs.GrowFile("f", 65536);   // 换租——旧 buffer 被映射钉住（Refs>0 延迟归还）
        section.View.Span[0].Should().Be(0x11);   // 头号陷阱回归：视图仍有效
        var b = new byte[1];
        h.Read(0, b);
        b[0].Should().Be(0x11);
        section.Dispose();
    }

    [Fact]
    public void Map_Disposed_ThrowsObjectDisposed()
    {
        using var fs = NewFs(MemoryAllocationMode.Sparse, pageSize: 4096);
        using var h = fs.Open("f", Opts());
        h.Write(0, new byte[4096]);
        var section = h.Map(0, 4096, AccessMode.Read);
        var view = section.View;
        section.Dispose();
        var act = () => _ = view.Span[0];
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void SpatialOps_AppliedToMaterializedMaps()
    {
        // ㉚ 空间操作 × 映射：PunchHole 后 Sparse 物化视图同步清零（平权——不读旧数据）
        using var fs = NewFs(MemoryAllocationMode.Sparse, pageSize: 4096);
        using var h = fs.Open("f", Opts());
        h.Write(0, new byte[8192]);
        using var section = h.Map(0, 8192, AccessMode.ReadWrite);
        section.View.Span[100] = 0xFF;
        h.PunchHole(0, 4096);
        section.View.Span[100].Should().Be(0, "打洞同步应用到物化副本（否则平权破绽）");
    }

    // ══════════════════ 范围锁 ══════════════════

    [Fact]
    public void RangeLock_CrossHandle_MutualExclusion_RealEffect()
    {
        using var fs = NewFs();
        using var h1 = fs.Open("f", Opts());
        using var h2 = fs.Open("f", Opts());
        h1.Lock(0, 1024, FileLockMode.Exclusive);
        h2.TryLock(0, 1024, FileLockMode.Exclusive).Should().BeFalse();
        h1.Unlock(0, 1024);
        h2.TryLock(0, 1024, FileLockMode.Exclusive).Should().BeTrue();
    }

    [Fact]
    public void RangeLock_SharedCoexist_ExclusiveBlocks()
    {
        using var fs = NewFs();
        using var h1 = fs.Open("f", Opts());
        using var h2 = fs.Open("f", Opts());
        h1.Lock(0, 512, FileLockMode.Shared);
        h2.TryLock(0, 512, FileLockMode.Shared).Should().BeTrue();
        h2.Unlock(0, 512);
        using var h3 = fs.Open("f", Opts());
        h3.TryLock(0, 512, FileLockMode.Exclusive).Should().BeFalse();
        h1.Unlock(0, 512);
        h3.TryLock(0, 512, FileLockMode.Exclusive).Should().BeTrue();
    }

    [Fact]
    public void RangeLock_SameHandleOverlapping_Converts()
    {
        using var fs = NewFs();
        using var h = fs.Open("f", Opts());
        h.Lock(0, 512, FileLockMode.Shared);
        h.TryLock(0, 512, FileLockMode.Exclusive).Should().BeTrue("同 owner 重叠=转换（POSIX OFD 语义）");
    }

    [Fact]
    public void RangeLock_HandleDispose_ReleasesAll()
    {
        using var fs = NewFs();
        using var h2 = fs.Open("f", Opts());
        var h1 = fs.Open("f", Opts());
        h1.Lock(0, 2048, FileLockMode.Exclusive);
        h1.Dispose();
        h2.TryLock(0, 2048, FileLockMode.Exclusive).Should().BeTrue();
    }

    // ══════════════════ xattr / 拷贝 / 向量 ══════════════════

    [Fact]
    public void FileExtra_Roundtrip_SlotBlob()
    {
        using var fs = NewFs();
        using var h = fs.Open("f", Opts());
        h.SetFileExtra(new byte[] { 9, 8 });
        h.FileExtra.ToArray().Should().Equal(9, 8);
        h.ReadFileExtra(1, new byte[4]).Should().Be(1, "尾段不足返实际量");
    }

    [Fact]
    public void CopyRange_Correct_NoAliasing()
    {
        using var fs = NewFs(MemoryAllocationMode.Reserved);
        using var src = fs.Open("a", Opts());
        using var dst = fs.Open("b", Opts());
        src.Write(0, new byte[4096]);
        src.CopyRange(dst, 0, 1024, 2048).Should().Be(2048);
        var a = new byte[16];
        var b = new byte[16];
        src.Read(0, a);
        dst.Read(1024, b);
        a.Should().Equal(b);
        dst.Write(1024, new byte[] { 0xFF });
        src.Read(0, a);
        a[0].Should().NotBe(0xFF);
        src.CloneRange(dst).Should().Be(4096);
    }

    [Fact]
    public void VectorReadWrite_EquivalentToSegmented()
    {
        using var fs = NewFs();
        using var h = fs.Open("f", Opts());
        h.WriteVector(16, new ReadOnlyMemory<byte>[] { new byte[] { 1, 2, 3 }, new byte[] { 4, 5 } });
        var d1 = new Memory<byte>(new byte[3]);
        var d2 = new Memory<byte>(new byte[2]);
        h.ReadVector(16, new[] { d1, d2 }).Should().Be(5);
        d1.Span.ToArray().Should().Equal(1, 2, 3);
        d2.Span.ToArray().Should().Equal(4, 5);
    }

    // ══════════════════ Detach/Install 与事件 ══════════════════

    [Fact]
    public void DetachInstall_RoundTrip()
    {
        using var fs = NewFs(MemoryAllocationMode.Reserved);
        using (var h = fs.Open("tmp", Opts())) { h.Write(0, new byte[2048]); }
        var payload = fs.DetachFile("tmp");
        fs.Exists("tmp").Should().BeFalse();
        fs.InstallFile("seg-000001.data", payload);
        using (var h = fs.Open("seg-000001.data", Opts(access: AccessMode.Read, mode: FileOpenMode.OpenExisting)))
        {
            h.Length.Should().Be(2048);
        }
    }

    [Fact]
    public void Events_Fired()
    {
        using var fs = NewFs();
        var created = 0;
        var deleted = 0;
        var replaced = 0;
        fs.FileCreated += (_, _) => created++;
        fs.FileDeleted += _ => deleted++;
        fs.FileReplaced += (_, _) => replaced++;
        using (var h = fs.Open("a", Opts())) { }
        fs.Delete("a");
        using (var h = fs.Open("b", Opts())) { }
        fs.Move("b", "c");
        created.Should().Be(2);
        deleted.Should().Be(1);
        replaced.Should().Be(1);
    }

    // ══════════════════ 构造模型 ══════════════════

    [Fact]
    public void Default_SingletonDisposeNoOp_GlobalPathSpace()
    {
        MemoryFileSystem.Default.Dispose();   // no-op
        using (var h = MemoryFileSystem.Default.Open($"default-{Guid.NewGuid():N}", Opts()))
        {
            h.Write(0, new byte[8]);
        }
        MemoryFileSystem.Default.Dispose();
    }

    [Fact]
    public void Create_PrivateVolumes_AreIsolated()
    {
        using var fs1 = NewFs();
        using var fs2 = NewFs();
        using (var h = fs1.Open("f", Opts())) { h.Write(0, new byte[8]); }
        fs2.Exists("f").Should().BeFalse("私有卷路径空间隔离（测试隔离纪律）");
    }

    [Fact]
    public void FsDispose_UnplugsVolume_HandlesFail()
    {
        var fs = NewFs(MemoryAllocationMode.Reserved);
        var h = fs.Open("f", Opts());
        h.Write(0, new byte[16]);
        var section = h.Map(0, 16, AccessMode.ReadWrite);
        fs.Dispose();   // 拔盘
        ((Action)(() => h.Read(0, new byte[4]))).Should().Throw<ObjectDisposedException>();
        ((Action)(() => fs.Open("x", Opts()))).Should().Throw<ObjectDisposedException>();
        var view = section.View;
        ((Action)(() => _ = view.Span[0])).Should().Throw<ObjectDisposedException>();   // ㉕ 无悬垂窗口
    }

    [Fact]
    public void Capabilities_ByAllocationMode()
    {
        NewFs(MemoryAllocationMode.Sparse).Capabilities.HasFlag(FileSystemCapabilities.Sparse).Should().BeTrue();
        NewFs(MemoryAllocationMode.Reserved).Capabilities.HasFlag(FileSystemCapabilities.Sparse).Should().BeFalse();
        using (var fs = NewFs())
        {
            fs.Capabilities.HasFlag(FileSystemCapabilities.Mmap).Should().BeTrue();
            fs.Capabilities.HasFlag(FileSystemCapabilities.RangeLock).Should().BeTrue();
            fs.Capabilities.HasFlag(FileSystemCapabilities.ExclusiveLock).Should().BeTrue();
            fs.Capabilities.HasFlag(FileSystemCapabilities.MaintenanceGate).Should().BeTrue();
            fs.Capabilities.HasFlag(FileSystemCapabilities.Advise).Should().BeFalse();
            fs.Capabilities.HasFlag(FileSystemCapabilities.RandomWrite).Should().BeTrue();   // 槽直址/页路由无页缺失代价
        }
        using (NewFs().AcquireExclusive(TimeSpan.FromSeconds(1))) { }   // 真锁可用（RAII 立即归还）
    }

    [Fact]
    public void Volume_MemGeometry()
    {
        using var fs = NewFs(MemoryAllocationMode.Sparse, quota: 1 << 20, pageSize: 4096);
        fs.Volume.SectorSize.Should().Be(512, "磁盘模拟几何——逻辑扇区（DIO 对齐基准）");
        fs.Volume.AllocationUnit.Should().Be(4096, "页 = 分配粒度（空间操作基准）——与对齐基是两码事");
        fs.Volume.TotalSpace.Should().Be(1 << 20);
    }

    [Fact]
    public void UnbufferedSupportAndAlignment_MemSemantics()
    {
        using var fs = NewFs();
        // 缓冲句柄：无约束（NotRequested / 1）
        using (var h = fs.Open("f", Opts()))
        {
            h.UnbufferedSupport.Should().Be(UnbufferedIoSupport.NotRequested);
            h.RequiredAlignment.Should().Be(1);
        }
        // DIO 句柄（行为保真模拟——）：Supported + 对齐强制（Disk 同款）
        using (var d = fs.Open("f", Opts(hints: FileOpenHints.NoBuffering)))
        {
            d.UnbufferedSupport.Should().Be(UnbufferedIoSupport.Supported,
                "带 NoBuffering 请求了禁缓冲——报 NotRequested 违反枚举契约（ramfs→Ignored 也不对：本无缓冲层可吞）");
            d.RequiredAlignment.Should().Be(512,
                "DIO 对齐基 = 逻辑扇区几何（Volume.SectorSize 同源——Linux 块设备最广泛形态；页是分配粒度不是对齐基）");
        }
    }

    [Fact]
    public void Dio_MisalignedAccess_ThrowsAlignmentError_DiskBehaviorParity()
    {
        // ★ 行为保真主测试：Mem 上对齐 bug 必须当场爆炸——不能等切 Disk 生产时才炸
        using var fs = NewFs(pageSize: 4096);
        using var h = fs.Open("f", Opts(hints: FileOpenHints.NoBuffering));
        using var mgr = new TC.Tier.Core.Primitives.AlignedMemoryManager(8192, 512);   // 512 扇区对齐缓冲
        var mem = mgr.Memory;
        // ① offset 非对齐（100 非 512 倍）
        ((Action)(() => h.Write(100, mem.Span[..4096]))).Should().Throw<FileIOException>()
            .Where(e => e.Error == IOError.AlignmentError);
        // ② length 非对齐
        ((Action)(() => h.Write(0, mem.Span[..4095]))).Should().Throw<FileIOException>()
            .Where(e => e.Error == IOError.AlignmentError);
        // ③ 缓冲地址非对齐（对齐基址 +1 偏移切片）
        ((Action)(() => h.Write(0, mem.Span.Slice(1, 4096)))).Should().Throw<FileIOException>()
            .Where(e => e.Error == IOError.AlignmentError);
        // ④ 读路径同律（offset + 地址）
        ((Action)(() => h.Read(100, mem.Span[..4096]))).Should().Throw<FileIOException>()
            .Where(e => e.Error == IOError.AlignmentError);
        ((Action)(() => h.Read(0, mem.Span.Slice(1, 4096)))).Should().Throw<FileIOException>()
            .Where(e => e.Error == IOError.AlignmentError);
        // ⑤ 三重对齐访问正常工作（数据保真）
        mem.Span[..4096].Fill((byte)0x5A);
        h.Write(0, mem.Span[..4096]);
        mem.Span.Clear();
        h.Read(0, mem.Span[..4096]).Should().Be(4096);
        mem.Span[..4096].ToArray().Should().OnlyContain(b => b == 0x5A, "对齐路径数据保真");
        // ⑥ 缓冲句柄不受影响（同卷同文件非 DIO 打开零约束）
        using (var b = fs.Open("f", Opts()))
        {
            var act = () => b.Write(100, new byte[100]);
            act.Should().NotThrow("缓冲句柄恒 1 对齐");
        }
        // ⑦ vector：offset/总长对齐即合法（片长可非对齐——Disk 总长语义）；offset 非对齐抛
        using var p1 = new TC.Tier.Core.Primitives.AlignedMemoryManager(2560, 512);
        using var p2 = new TC.Tier.Core.Primitives.AlignedMemoryManager(1536, 512);
        var act7 = () => h.WriteVector(0, new ReadOnlyMemory<byte>[] { p1.Memory, p2.Memory });   // 总长 4096（512 倍）
        act7.Should().NotThrow("总长对齐 + 片地址对齐 = 合法（片长 2560/1536 非 512 倍也行——与 Disk 一致）");
        ((Action)(() => h.WriteVector(100, new ReadOnlyMemory<byte>[] { p2.Memory }))).Should().Throw<FileIOException>()
            .Where(e => e.Error == IOError.AlignmentError, "vector offset 非对齐");
    }

    [Fact]
    public void AcquireExclusive_MemRealLock_TimeoutAndRaii()
    {
        using var fs = NewFs();
        using (var lease = fs.AcquireExclusive(TimeSpan.FromSeconds(10)))
        {
            // 非重入：持锁再获取立即失败（与 Disk 一致）
            var act = () => fs.AcquireExclusive(TimeSpan.FromMilliseconds(50));
            act.Should().Throw<FileIOException>().Where(e => e.Error == IOError.SharingViolation);
        }
        // 释放后可再获取（lease 必须回收——lambda 内获取后显式 Dispose）
        IDisposable? lease2 = null;
        var act2 = () => lease2 = fs.AcquireExclusive(TimeSpan.FromSeconds(1));
        act2.Should().NotThrow("lease Dispose 后锁归还");
        lease2!.Dispose();
        // 超时语义：他人持锁时短超时 SharingViolation
        using (var lease3 = fs.AcquireExclusive(TimeSpan.FromSeconds(10)))
        {
            var other = Task.Run(() =>
            {
                try { fs.AcquireExclusive(TimeSpan.FromMilliseconds(100)); return false; }
                catch (FileIOException e) { return e.Error == IOError.SharingViolation; }
            });
            // ★ 轮询等待完成（满套并行负载下 Task.Run 起跑有池调度延迟——固定 Wait 会假红，仓库既定模式）
            SpinWait.SpinUntil(() => other.IsCompleted, 10000).Should().BeTrue("并发获取任务应完成");
            other.Result.Should().BeTrue("并发获取超时 → SharingViolation（Disk 卷锁同语义）");
        }
        fs.Capabilities.HasFlag(FileSystemCapabilities.ExclusiveLock).Should().BeTrue("能力位已置");
    }

    [Fact]
    public void EnsureRootFlushRoot_NoOp()
    {
        using var fs = NewFs();
        var act = () => { fs.EnsureRoot(); fs.FlushRoot(); };
        act.Should().NotThrow();
    }

    // ══════════════════ Sparse 写路径与预分配（设计补全）══════════════════

    [Fact]
    public void Sparse_FullPageWriteSkipZeroing_UncoveredSegmentsStillZero()
    {
        // 免清零租借安全性：新页的部分写——未覆盖段（前导/尾部）必须确定性读零（池复租不残留旧数据）
        using var fs = NewFs(MemoryAllocationMode.Sparse, pageSize: 4096);
        using (var h = fs.Open("seed", Opts()))
        {
            var dirty = new byte[4096];
            Array.Fill(dirty, (byte)0xAB);
            h.Write(0, dirty);   // 页 0 = 0xAB（此后 PunchHole 释放回池——下次租借复用此页）
            h.PunchHole(0, 4096);
        }
        using (var h = fs.Open("f", Opts()))
        {
            var patch = new byte[100];
            Array.Fill(patch, (byte)0xCD);
            h.Write(4100, patch);   // 新租页（大概率复用 0xAB 脏页）中段部分写
            var buf = new byte[8300];
            h.Read(0, buf);
            buf.AsSpan(0, 4100).ToArray().Should().OnlyContain(b => b == 0, "页前读零");
            buf.AsSpan(4100, 100).ToArray().Should().OnlyContain(b => b == 0xCD, "写入段");
            buf.AsSpan(4200).ToArray().Should().OnlyContain(b => b == 0, "★ 尾部未覆盖段读零——免清零租借不泄漏池残留");
        }
    }

    [Fact]
    public void Sparse_Preallocate_PhysicalReservation_ZeroRead_PureMemcpyPath()
    {
        // 预分配物理化（家族契约对齐：Disk fallocate / Raw unwritten 同语义）
        using var fs = NewFs(MemoryAllocationMode.Sparse, quota: 1 << 20, pageSize: 4096);
        using (var h = fs.Open("f", Opts(preallocate: 8192)))
        {
            h.Length.Should().Be(8192);
            h.AllocatedSize.Should().Be(8192, "预分配 = 物理预留（此前仅逻辑长度——设计缺口已补）");
            var buf = new byte[16];
            h.Read(0, buf);
            buf.Should().OnlyContain(b => b == 0, "预留区读零");
            h.Write(0, new byte[8192]);   // 预热页写入 = 纯 memcpy（零分配零清零）
            h.Read(0, buf);
            buf.Should().OnlyContain(b => b == 0, "写入 0 字节");
        }
        // 配额判据：预分配物理占用计入容量——边界内成立、超配额拒绝
        using (var h2 = fs.Open("g", Opts(preallocate: (1 << 20) - 8192)))
        {
            h2.AllocatedSize.Should().Be((1 << 20) - 8192, "配额边界内成立（8K + 1016K = 1MB 恰满）");
        }
        var act = () => fs.Open("overflow", Opts(preallocate: 4096));
        act.Should().Throw<FileIOException>().Where(e => e.Error == IOError.DiskFull,
            "预分配物理占用计入容量配额——满了因空间不因条目");
    }

    [Fact]
    public void Sparse_CreateFilePreallocate_Physical()
    {
        using var fs = NewFs(MemoryAllocationMode.Sparse, pageSize: 4096);
        fs.CreateFile("f", preallocateSize: 12288);
        using (var h = fs.Open("f", Opts()))
        {
            h.AllocatedSize.Should().Be(12288, "fs 级 CreateFile(preallocateSize) 同样物理预留");
        }
    }

    // ══════════════════ CORE-13/14 契约（游标盒覆盖清理 + slot-keyed 空间操作）══════════════════

    [Fact]
    public void Move_Overwrite_AppendStartsAtNewLength_NoOverwriteOfOldData()
    {
        using var fs = NewFs();
        using (var h = fs.Open("src", Opts(mode: FileOpenMode.CreateNew)))
            h.Write(0, new byte[100_000]);
        using (var h = fs.Open("dst", Opts(mode: FileOpenMode.CreateNew)))
            h.Write(0, new byte[10_000]);
        fs.Move("src", "dst", overwrite: true);
        using var hd = fs.Open("dst", Opts(mode: FileOpenMode.OpenOrCreate));
        // CORE-13：覆盖后新句柄 Append 必须从新文件长度（100KB）起——旧盒（10KB 长度）残留 = 覆写旧数据/留零洞
        var reserved = hd.Append(new byte[4]);
        reserved.Should().Be(100_000, "Append 落点 = 覆盖后文件长度（旧盒 10KB 残留 = 覆写 10KB 起的新数据）");
    }

    [Fact]
    public void SetLength_StaleHandle_DoesNotAffectSamePathNewFile()
    {
        using var fs = NewFs();
        var h = fs.Open("f", Opts(mode: FileOpenMode.CreateNew));
        h.Write(0, new byte[50_000]);
        fs.Delete("f");
        using (var h2 = fs.Open("f", Opts(mode: FileOpenMode.CreateNew)))
        {
            h2.Write(0, new byte[20_000]);
            // CORE-14：旧句柄 SetLength 不得截断/扩展同名新文件（path 重解析 = 跨代越权）
            // 句柄在档 → 槽 Detached 未回收（gen 不变）——旧句柄操作自己的不可见数据，新文件（新槽）不受扰
            h.SetLength(0);
            h2.Length.Should().Be(20_000, "旧句柄不得影响新文件长度（新文件在独立槽——旧句柄 path 重解析 = 越权）");
        }
    }

    [Fact]
    public void Preallocate_StaleHandle_DoesNotGrowSamePathNewFile()
    {
        using var fs = NewFs();
        var h = fs.Open("f", Opts(mode: FileOpenMode.CreateNew, preallocate: 0));
        h.Write(0, new byte[50_000]);
        fs.Delete("f");
        using (var h2 = fs.Open("f", Opts(mode: FileOpenMode.CreateNew)))
        {
            h2.Write(0, new byte[20_000]);
            h.Preallocate();   // 旧句柄预分配（无 preallocate 尺寸 = no-op）——新文件长度不受扰
            h2.Length.Should().Be(20_000);
        }
    }
}
