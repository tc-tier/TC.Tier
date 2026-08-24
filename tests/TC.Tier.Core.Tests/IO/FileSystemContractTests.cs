using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Disk;
using TC.Tier.Core.IO.Mem;

namespace TC.Tier.Core.Tests.IO;

/// <summary>
/// 介质平权契约测试套——同一套 IFileSystem/IFileHandle 断言跑 Disk 与 Mem（"两种介质平权"的机械验证，§11）。
/// 平权矩阵断言（D5 逐项）：打开模式违规 / 共享冲突 / 越过 EOF 零扩展且洞读零 / 新建文件全读零 /
/// 删除即成+旧句柄读旧数据（POSIX 断言集 ⑧）/ 映射独立于父句柄 / CopyRange 无别名 / 向量等价 /
/// 游标规则 / PunchHole 对齐双介质同校验（⑩）。
/// </summary>
public abstract class FileSystemContractTests : IDisposable
{
    /// <summary>创建受测 fs（子类提供介质）。</summary>
    protected abstract IFileSystem Fs { get; }

    protected static FileOpenOptions Opts(AccessMode access = AccessMode.ReadWrite,
        FileOpenMode mode = FileOpenMode.OpenOrCreate, FileSharing sharing = FileSharing.ReadWrite)
        => new() { Access = access, Mode = mode, Sharing = sharing };

    public abstract void Dispose();

    // ═══════════════ 打开语义（平权矩阵）═══════════════

    [Fact]
    public void OpenExisting_Missing_ThrowsNotFound()
    {
        var act = () => Fs.Open("nope", Opts(access: AccessMode.Read, mode: FileOpenMode.OpenExisting));
        act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.NotFound);
    }

    [Fact]
    public void CreateNew_Existing_ThrowsAlreadyExists()
    {
        using (Fs.Open("dup", Opts())) { }
        var act = () => Fs.Open("dup", Opts(mode: FileOpenMode.CreateNew));
        act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.AlreadyExists);
    }

    [Fact]
    public void Truncate_Empties()
    {
        using (var h = Fs.Open("t", Opts())) { h.Write(0, new byte[512]); }
        using var h2 = Fs.Open("t", Opts(mode: FileOpenMode.Truncate));
        h2.Length.Should().Be(0);
    }

    [Fact]
    public void SharingConflict_Detected()
    {
        using var h1 = Fs.Open("shared", Opts(sharing: FileSharing.Read));
        var act = () => Fs.Open("shared", Opts(access: AccessMode.Write, mode: FileOpenMode.OpenExisting));
        act.Should().Throw<IOException>();
    }

    // ═══════════════ 读写语义（pwrite/pread 平权）═══════════════

    [Fact]
    public void Write_PastEof_ZeroExtends_HoleReadsZero()
    {
        using var h = Fs.Open("hole", Opts());
        h.Write(16384, new byte[] { 7 });
        h.Length.Should().Be(16385);
        var buf = new byte[64];
        h.Read(0, buf).Should().Be(64);
        buf.Should().OnlyContain(b => b == 0);
        h.Read(16384, buf).Should().Be(1);
        buf[0].Should().Be(7);
    }

    [Fact]
    public void NewFile_ReadsAllZero()
    {
        using var h = Fs.Open("zero", Opts());
        var buf = new byte[4096];
        h.Read(0, buf).Should().Be(0);   // 空文件在 EOF
        h.Write(4096, new byte[] { 9 });   // 零洞扩展到 4097
        h.Read(0, buf).Should().Be(4096);
        buf.Should().OnlyContain(b => b == 0);   // 未写区域读零
        h.Read(4096, buf).Should().Be(1);
        buf[0].Should().Be(9);
    }

    [Fact]
    public void Read_AtEof_ReturnsZero_NotThrow()
    {
        using var h = Fs.Open("eof", Opts());
        h.Write(0, new byte[16]);
        h.Read(16, new byte[8]).Should().Be(0);
        h.Read(1000, new byte[8]).Should().Be(0);
    }

    [Fact]
    public void SetLength_ShrinkAndExtend_ExtendedReadsZero()
    {
        using var h = Fs.Open("sl", Opts());
        h.Write(0, new byte[8192]);
        h.SetLength(4096);
        h.Length.Should().Be(4096);
        h.SetLength(8192);
        var buf = new byte[4096];
        h.Read(4096, buf).Should().Be(4096);
        buf.Should().OnlyContain(b => b == 0);
    }

    // ═══════════════ 命名空间（平权矩阵）═══════════════

    [Fact]
    public void Move_OverwriteFalse_ThrowsAlreadyExists()
    {
        using (Fs.Open("src", Opts())) { }
        using (Fs.Open("dst", Opts())) { }
        var act = () => Fs.Move("src", "dst", overwrite: false);
        act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.AlreadyExists);
    }

    [Fact]
    public void Move_OverwriteTrue_ContentReplaced()
    {
        using (var h = Fs.Open("src", Opts())) { h.Write(0, new byte[] { 1, 2, 3 }); }
        using (var h = Fs.Open("dst", Opts())) { h.Write(0, new byte[] { 9, 9, 9, 9, 9 }); }
        Fs.Move("src", "dst", overwrite: true);
        Fs.Exists("src").Should().BeFalse();
        using var h2 = Fs.Open("dst", Opts(access: AccessMode.Read, mode: FileOpenMode.OpenExisting));
        var buf = new byte[8];
        h2.Read(0, buf).Should().Be(3);
        buf[..3].Should().Equal(1, 2, 3);
    }

    [Fact]
    public void Delete_Missing_IdempotentSuccess()
    {
        var act = () => Fs.Delete("nope");
        act.Should().NotThrow();
    }

    [Fact]
    public void Enumerate_FusedNameLength()
    {
        using (var h = Fs.Open("a", Opts())) { h.Write(0, new byte[11]); }
        using (var h = Fs.Open("b", Opts())) { h.Write(0, new byte[22]); }
        var entries = Fs.EnumerateFiles().OrderBy(e => e.Name, StringComparer.Ordinal).ToArray();
        entries.Length.Should().BeGreaterThanOrEqualTo(2);
        entries.Where(e => e.Name is "a" or "b").Select(e => (e.Name, e.Length))
            .Should().Equal(("a", 11L), ("b", 22L));
    }

    [Fact]
    public void EnsureRootFlushRoot_NoThrow()
    {
        var act = () => { Fs.EnsureRoot(); Fs.FlushRoot(); };
        act.Should().NotThrow();
    }

    // ═══════════════ PunchHole 对齐契约（⑩ 双介质同校验）═══════════════

    [Fact]
    public void PunchHole_ArbitraryRange_ReadZero_BothMedia()
    {
        // ★ 契约 v2：任意区间接受（字节粒度归零——引擎 A4）；可观测语义 = 读零，不再抛 AlignmentError。
        var unit = Fs.Volume.AllocationUnit;
        using var h = Fs.Open("ph", Opts());
        h.Write(0, new byte[unit * 4]);
        var act = () => h.PunchHole(unit / 2, unit);
        act.Should().NotThrow("字节粒度归零契约（引擎 Reclaim 字节区间）");
        var buf = new byte[unit * 4];
        h.Read(0, buf);
        buf.Should().OnlyContain(b => b == 0, "打洞区间读零");
    }

    [Fact]
    public void PunchHole_Aligned_LengthUnchanged_HoleReadsZero()
    {
        var unit = Fs.Volume.AllocationUnit;
        using var h = Fs.Open("ph2", Opts());
        h.Write(0, new byte[unit * 4]);
        h.PunchHole(unit, unit * 2);
        h.Length.Should().Be(unit * 4);
        var buf = new byte[(int)unit];
        h.Read(unit, buf).Should().Be((int)unit);
        buf.Should().OnlyContain(b => b == 0);
    }

    // ═══════════════ 游标（D7 平权）═══════════════

    [Fact]
    public void Cursor_Rules()
    {
        using var h = Fs.Open("cur", Opts());
        h.Write(100, new byte[4]);
        h.Position.Should().Be(0);   // pwrite 不动游标
        h.Append(new byte[4]).Should().Be(0);
        h.Append(new byte[8]).Should().Be(4);
        h.Position.Should().Be(12);
        h.Seek(0, SeekOrigin.Begin).Should().Be(0);
        h.Seek(0, SeekOrigin.End).Should().Be(104);
    }

    [Fact]
    public void Append_ConcurrentSameHandle_AllDistinctFullCoverage()
    {
        using var h = Fs.Open("ap", Opts());
        const int threads = 6, perThread = 200, len = 64;
        var offsets = new System.Collections.Concurrent.ConcurrentBag<long>();
        System.Threading.Tasks.Parallel.For(0, threads, _ =>
        {
            var data = new byte[len];
            for (var i = 0; i < perThread; i++)
                offsets.Add(h.Append(data));
        });
        offsets.Distinct().Count().Should().Be(threads * perThread);
        h.Length.Should().Be((long)threads * perThread * len);
    }

    [Fact]
    public void AppendMode_SeekAndWriteStillLegal()
    {
        using (var h = Fs.Open("am", Opts())) { h.Write(0, new byte[64]); }
        using var h2 = Fs.Open("am", Opts(mode: FileOpenMode.Append));
        h2.Position.Should().Be(64);
        h2.Seek(0, SeekOrigin.Begin);
        h2.Write(0, new byte[] { 5 });
        var b = new byte[1];
        h2.Read(0, b).Should().Be(1);
        b[0].Should().Be(5);
    }

    // ═══════════════ 拷贝与向量（平权）═══════════════

    [Fact]
    public void CopyRange_Correct_NoAlias()
    {
        using var src = Fs.Open("cs", Opts());
        using var dst = Fs.Open("cd", Opts());
        src.Write(0, new byte[8192]);
        var n = src.CopyRange(dst, 512, 1024, 2048);
        n.Should().Be(2048);
        var a = new byte[32];
        var b = new byte[32];
        src.Read(512, a);
        dst.Read(1024, b);
        a.Should().Equal(b);
        dst.Write(1024, new byte[] { 0xFF });
        src.Read(512, a);
        a[0].Should().NotBe(0xFF, "禁止可观察别名");
    }

    [Fact]
    public void VectorReadWrite_EquivalentToSegmented()
    {
        using var h = Fs.Open("vec", Opts());
        h.WriteVector(32, new ReadOnlyMemory<byte>[] { new byte[] { 1, 2 }, new byte[] { 3, 4, 5 } });
        var d1 = new Memory<byte>(new byte[2]);
        var d2 = new Memory<byte>(new byte[3]);
        h.ReadVector(32, new[] { d1, d2 }).Should().Be(5);
        d1.Span.ToArray().Should().Equal(1, 2);
        d2.Span.ToArray().Should().Equal(3, 4, 5);
    }

    // ═══════════════ 映射（平权：写入经映射可见于 Read）═══════════════

    [Fact]
    public void Map_WriteVisibleViaRead_AfterFlush()
    {
        using var h = Fs.Open("mp", Opts());
        h.Write(0, new byte[8192]);
        using var section = h.Map(0, 4096, AccessMode.ReadWrite);
        section.View.Span[123] = 0x5E;
        section.Flush();   // mem Sparse 物化写回 / 磁盘+Reserved 直址——统一经 Flush 后可见
        var b = new byte[1];
        h.Read(123, b);
        b[0].Should().Be(0x5E);
    }

    [Fact]
    public void Map_IndependentOfParentHandle_BothMedia()
    {
        var h = Fs.Open("mi", Opts());
        h.Write(0, new byte[8192]);
        var section = h.Map(0, 4096, AccessMode.ReadWrite);
        h.Dispose();
        var act = () => section.View.Span[7] = 0x42;
        act.Should().NotThrow();
        section.Dispose();
    }

    [Fact]
    public void Map_Disposed_ThrowsObjectDisposed()
    {
        using var h = Fs.Open("md", Opts());
        h.Write(0, new byte[8192]);
        var section = h.Map(0, 4096, AccessMode.ReadWrite);
        var view = section.View;
        section.Dispose();
        ((Action)(() => _ = view.Span[0])).Should().Throw<ObjectDisposedException>();
    }

    // ═══════════════ Dispose 语义（㉜ 全家桶节选）═══════════════

    [Fact]
    public void HandleDispose_ThenOps_ThrowObjectDisposed()
    {
        var h = Fs.Open("dd", Opts());
        h.Dispose();
        h.Dispose();   // 幂等
        ((Action)(() => h.Write(0, new byte[1]))).Should().Throw<ObjectDisposedException>();
    }

    // ═══════════════ 根空间层级（filesystem-root-space-design——目录族/创建解耦/模式匹配/元数据）═══════════════

    [Fact]
    public void Directory_MkdirP_Idempotent_AncestorsVisible()
    {
        Fs.CreateDirectory("s1/eng0/compact");
        Fs.DirectoryExists("s1").Should().BeTrue();
        Fs.DirectoryExists("s1/eng0").Should().BeTrue();
        Fs.DirectoryExists("s1/eng0/compact").Should().BeTrue();
        Fs.CreateDirectory("s1/eng0/compact");   // 幂等
        Fs.CreateDirectory("s1");                // 祖先幂等
        Fs.DirectoryExists("no-such").Should().BeFalse();
        Fs.Capabilities.HasFlag(FileSystemCapabilities.EmptyDirectories).Should().BeTrue("Disk/Mem 置位（Remote 不在平权套）");
    }

    [Fact]
    public void Directory_Lifecycle_DeleteEmptyOnly()
    {
        Fs.CreateDirectory("life");
        Fs.CreateFile("life/f0");
        ((Action)(() => Fs.DeleteDirectory("life"))).Should().Throw<FileIOException>()
            .Which.Error.Should().Be(IOError.DirectoryNotEmpty);
        Fs.Delete("life/f0");
        Fs.DeleteDirectory("life");
        Fs.DirectoryExists("life").Should().BeFalse();
        ((Action)(() => Fs.DeleteDirectory("life"))).Should().Throw<FileIOException>()
            .Which.Error.Should().Be(IOError.NotFound);
    }

    [Fact]
    public void CreateFile_Decoupled_Exists_AlreadyExists_Preallocate()
    {
        Fs.CreateDirectory("eng");
        Fs.CreateFile("eng/seg-0", preallocateSize: 1 << 20);
        Fs.Exists("eng/seg-0").Should().BeTrue();
        ((Action)(() => Fs.CreateFile("eng/seg-0"))).Should().Throw<FileIOException>()
            .Which.Error.Should().Be(IOError.AlreadyExists);
        // 预分配逻辑长度生效（句柄 Length = 预分配值——不写入即达）
        using (var h = Fs.Open("eng/seg-0", new FileOpenOptions { Mode = FileOpenMode.OpenExisting, Access = AccessMode.Read }))
            h.Length.Should().Be(1 << 20);
        // 运行时打开（OpenExisting）——创建/打开解耦
        using (var h = Fs.Open("eng/seg-0", new FileOpenOptions { Mode = FileOpenMode.OpenExisting, Access = AccessMode.Write }))
            h.Write(0, new byte[8]);
        // 父目录缺失 → NotFound（对齐 disk ENOENT）
        ((Action)(() => Fs.CreateFile("no-dir/f"))).Should().Throw<FileIOException>()
            .Which.Error.Should().Be(IOError.NotFound);
    }

    [Fact]
    public void CreateFile_FileExtra_RoundTrip_Limit()
    {
        var meta = new byte[] { 1, 2, 3, 4 };
        Fs.CreateFile("meta-f", extra: meta);
        var stat = Fs.Stat("meta-f");
        stat.FileExtra.ToArray().Should().Equal(meta);
        // FileExtra 随创建写入；未带文件读回空
        Fs.CreateFile("meta-empty");
        Fs.Stat("meta-empty").FileExtra.Length.Should().Be(0);
        // 1536 统一强制
        ((Action)(() => Fs.CreateFile("meta-big", extra: new byte[IFileSystem.MaxFileExtraBytes + 1])))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void FileExtra_OffsetOps_PreadPwriteContract()
    {
        Fs.CreateFile("fx");
        using (var h = Fs.Open("fx", Opts()))
        {
            // 全量写 → 偏移读（含尾段不足 + EOF→0）
            h.SetFileExtra(new byte[] { 10, 20, 30, 40, 50 });
            var buf = new byte[10];
            h.ReadFileExtra(1, buf).Should().Be(4, "尾段不足返实际量");
            buf[..4].Should().Equal(20, 30, 40, 50);
            h.ReadFileExtra(5, buf).Should().Be(0, "offset=长度 → EOF 0");
            h.ReadFileExtra(99, buf).Should().Be(0);
            // 精准字节写：原位补丁不动长度
            h.WriteFileExtra(1, new byte[] { 0xAA, 0xBB });
            h.FileExtra.ToArray().Should().Equal(10, 0xAA, 0xBB, 40, 50);
            // 越尾零扩展（gap 填零）
            h.WriteFileExtra(7, new byte[] { 0xCC });
            h.FileExtra.ToArray().Should().Equal(10, 0xAA, 0xBB, 40, 50, 0, 0, 0xCC);
            // 预算封顶：偏移写扩展越限即抛（增长点①）
            ((Action)(() => h.WriteFileExtra(IFileSystem.MaxFileExtraBytes, new byte[] { 1 })))
                .Should().Throw<ArgumentException>();
            // 预算封顶：全量覆盖越限即抛（增长点②）
            ((Action)(() => h.SetFileExtra(new byte[IFileSystem.MaxFileExtraBytes + 1])))
                .Should().Throw<ArgumentException>();
            // 完全覆盖可缩（无 truncate 成员——缩短=覆盖）
            h.SetFileExtra(new byte[] { 9 });
            h.FileExtra.ToArray().Should().Equal(9);
            h.ReadFileExtra(1, buf).Should().Be(0);
            // 清除 = 空
            h.SetFileExtra(ReadOnlyMemory<byte>.Empty);
            h.FileExtra.Length.Should().Be(0);
        }
        // 句柄写落盘后 fs 级可见（两平面互见）
        using (var h = Fs.Open("fx", Opts()))
            h.SetFileExtra(new byte[] { 7, 7 });
        Fs.Stat("fx").FileExtra.ToArray().Should().Equal(7, 7);
    }

    [Fact]
    public void Stat_File_FullInfo_Missing_Throws()
    {
        using (var h = Fs.Open("stat-f", Opts())) { h.Write(0, new byte[7]); }
        var st = Fs.Stat("stat-f");
        st.Type.Should().Be(FsEntryType.File);
        st.Length.Should().Be(7);
        st.LastWriteTime.Should().NotBe(DateTimeOffset.MinValue, "Disk/Mem 文件必带修改时间");
        st.Name.Should().Be("stat-f");
        ((Action)(() => Fs.Stat("no-such-entry"))).Should().Throw<FileIOException>()
            .Which.Error.Should().Be(IOError.NotFound);
    }

    [Fact]
    public void Enumeration_Hierarchical_TopVsRecursive_Names()
    {
        Fs.CreateDirectory("h/a");
        Fs.CreateFile("h/top", preallocateSize: 3);
        Fs.CreateFile("h/a/x", preallocateSize: 4);
        Fs.CreateFile("h/a/y", preallocateSize: 5);

        // 非递归（一层）：目录名单组件
        Fs.EnumerateFiles("h", "*").Select(e => e.Name).Should().Equal("top");
        Fs.EnumerateDirectories("h", "*").Select(e => e.Name).Should().Equal("a");
        // 递归：文件名 = 相对所枚举目录的多组件路径
        Fs.EnumerateFiles("h", "*", recursive: true).Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal)
            .Should().Equal("a/x", "a/y", "top");
        // 混合族 = 文件 ∪ 目录
        var entries = Fs.EnumerateEntries("h", "*").OrderBy(e => e.Name, StringComparer.Ordinal).ToArray();
        entries.Select(e => (e.Name, e.Type)).Should().Equal(("a", FsEntryType.Directory), ("top", FsEntryType.File));
        // 递归混合
        var all = Fs.EnumerateEntries("h", "*", recursive: true).OrderBy(e => e.Name, StringComparer.Ordinal).ToArray();
        all.Select(e => (e.Name, e.Type)).Should()
            .Equal(("a", FsEntryType.Directory), ("a/x", FsEntryType.File), ("a/y", FsEntryType.File), ("top", FsEntryType.File));
        // 缺目录 → NotFound
        ((Action)(() => Fs.EnumerateFiles("no-dir", "*"))).Should().Throw<FileIOException>()
            .Which.Error.Should().Be(IOError.NotFound);
    }

    [Fact]
    public void Enumeration_Pattern_Matching()
    {
        Fs.CreateDirectory("p");
        Fs.CreateFile("p/tc.log.0");
        Fs.CreateFile("p/tc.log.1");
        Fs.CreateFile("p/tc.log.marker");
        Fs.CreateFile("p/other.bin");

        // 引擎段扫描形态：{DeviceName}.*
        Fs.EnumerateFiles("p", "tc.log.*").Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal)
            .Should().Equal("tc.log.0", "tc.log.1", "tc.log.marker");
        // 缺省 * 全匹配
        Fs.EnumerateFiles("p", "*").Count().Should().Be(4);
        // ? 单字符
        Fs.EnumerateFiles("p", "tc.log.?").Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal)
            .Should().Equal("tc.log.0", "tc.log.1");
        // Ordinal 区分大小写（mem 对齐 Linux；NTFS 差异文档化）
        Fs.CreateFile("p/CASE.TXT");
        Fs.EnumerateFiles("p", "case.txt").Count().Should().Be(0);
        Fs.EnumerateFiles("p", "CASE.TXT").Count().Should().Be(1);
        // 空 pattern 拒绝
        ((Action)(() => Fs.EnumerateFiles("p", ""))).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void MoveDirectory_TreeMoves_AtomicCapability()
    {
        Fs.CreateDirectory("mv/src");
        Fs.CreateFile("mv/src/f0", preallocateSize: 2);
        Fs.CreateDirectory("mv/src/sub");
        Fs.CreateFile("mv/src/sub/f1", preallocateSize: 3);

        Fs.MoveDirectory("mv/src", "mv/dst");
        Fs.DirectoryExists("mv/src").Should().BeFalse();
        Fs.Exists("mv/dst/f0").Should().BeTrue();
        Fs.Exists("mv/dst/sub/f1").Should().BeTrue();
        Fs.Capabilities.HasFlag(FileSystemCapabilities.AtomicDirectoryMove)
            .Should().BeTrue("Disk/Mem 置位（Remote 回退语义不在平权套）");

        // 目标已存在 → AlreadyExists；源缺失 → NotFound
        Fs.CreateDirectory("mv/other");
        ((Action)(() => Fs.MoveDirectory("mv/dst", "mv/other"))).Should().Throw<FileIOException>()
            .Which.Error.Should().Be(IOError.AlreadyExists);
        ((Action)(() => Fs.MoveDirectory("mv/no", "mv/x"))).Should().Throw<FileIOException>()
            .Which.Error.Should().Be(IOError.NotFound);
    }

    [Fact]
    public void Enumeration_HiddenDotClass_DefaultExcluded_ExplicitPatternExempts()
    {
        Fs.CreateFile("vis");
        Fs.CreateFile(".hid1");                    // 隐藏文件（sidecar/系统文件类）
        Fs.CreateDirectory(".hd");                 // 隐藏目录
        Fs.CreateFile(".hd/inner");                // 隐藏子树内容
        Fs.CreateDirectory("open");
        Fs.CreateFile("open/.hid2");               // 深层隐藏组件

        // 默认 "*"：隐藏类不可见（根层 + 深层 + 隐藏子树整支）
        Fs.EnumerateFiles("*").Select(e => e.Name).Should().Equal("vis");
        Fs.EnumerateDirectories("*").Select(e => e.Name).Should().Equal("open");
        Fs.EnumerateFiles("*", recursive: true).Select(e => e.Name).Should().Equal("vis");

        // A 方案豁免：pattern 首字符 '.' = 显式查看隐藏类（pattern 仍匹配最终组件）
        Fs.EnumerateFiles(".*").Select(e => e.Name).Should().Equal(".hid1");
        Fs.EnumerateDirectories(".*").Select(e => e.Name).Should().Equal(".hd");

        // 隐藏子树内容经显式路径扫描可见（直接访问不受隐藏影响）
        Fs.EnumerateFiles(".hd", "*").Select(e => e.Name).Should().Equal("inner");
        Fs.Exists(".hid1").Should().BeTrue();
        using (var h = Fs.Open(".hid1", new FileOpenOptions { Mode = FileOpenMode.OpenExisting, Access = AccessMode.Write }))
            h.Write(0, new byte[1]);
        Fs.Stat(".hd/inner").Type.Should().Be(FsEntryType.File);
        // 直接 Delete 隐藏文件同样有效
        Fs.Delete("open/.hid2");
        Fs.Exists("open/.hid2").Should().BeFalse();
    }

    [Fact]
    public void FileExtra_TwoPlanesMutuallyVisible()
    {
        // fs 级创建即写 → 句柄级可读（同平面同限同通道——FileExtra）
        var meta = new byte[] { 0x11, 0x22 };
        Fs.CreateFile("uni", extra: meta);
        using (var h = Fs.Open("uni", new FileOpenOptions { Mode = FileOpenMode.OpenExisting, Access = AccessMode.Read }))
            h.FileExtra.ToArray().Should().Equal(meta);
        // 句柄级 SetFileExtra → fs 级 Stat 可读
        using (var h = Fs.Open("uni", new FileOpenOptions { Mode = FileOpenMode.OpenExisting, Access = AccessMode.Write }))
            h.SetFileExtra(new byte[] { 0x33 });
        Fs.Stat("uni").FileExtra.ToArray().Should().Equal(0x33);
    }

    [Fact]
    public void HierarchicalPath_OpenReadWrite_RoundTrip()
    {
        Fs.CreateDirectory("rw/a/b");
        using (var h = Fs.Open("rw/a/b/f", new FileOpenOptions { Mode = FileOpenMode.OpenOrCreate, Access = AccessMode.Write }))
            h.Write(0, new byte[] { 9, 9, 9 });
        Fs.Exists("rw/a/b/f").Should().BeTrue();
        using (var h = Fs.Open("rw/a/b/f", new FileOpenOptions { Mode = FileOpenMode.OpenExisting, Access = AccessMode.Read }))
        {
            var buf = new byte[3];
            h.Read(0, buf).Should().Be(3);
            buf.Should().AllBeEquivalentTo(9);
        }
        // 父目录缺失的 Open → NotFound（disk ENOENT 平权）
        ((Action)(() => Fs.Open("no-dir/f", Opts()))).Should().Throw<FileIOException>()
            .Which.Error.Should().Be(IOError.NotFound);
    }
}

/// <summary>磁盘介质的平权契约。</summary>
public sealed class DiskFileSystemContractTests : FileSystemContractTests
{
    private readonly string _dir = TestTempDir.Create("core-io-contract-disk");
    private readonly DiskFileSystem _fs;

    public DiskFileSystemContractTests()
    {
        _fs = DiskFileSystem.OpenOrCreate(_dir);
        _fs.EnsureRoot();
    }

    protected override IFileSystem Fs => _fs;

    public override void Dispose()
    {
        _fs.Dispose();
        TestTempDir.TryCleanup(_dir);
    }

    [Fact]
    public void Delete_WithOpenHandle_WindowsRejects_PosixSucceeds()
    {
        // ⑧ 删除语义按平台断言（磁盘侧平台原生——不建进程内注册表模拟）
        using var h = _fs.Open("held", Opts(sharing: FileSharing.ReadWrite));
        h.Write(0, new byte[64]);
        if (OperatingSystem.IsWindows())
        {
            // Windows：无 Sharing.Delete 时 OS 拒绝删除（真进程级保护）
            var act = () => _fs.Delete("held");
            act.Should().Throw<IOException>();
        }
        else
        {
            // POSIX：unlink 无条件成功——旧句柄继续读旧数据
            var act = () => _fs.Delete("held");
            act.Should().NotThrow();
            _fs.Exists("held").Should().BeFalse();
            var b = new byte[8];
            h.Read(0, b).Should().Be(8);
        }
    }

    [Fact]
    public void SharingIsAdvisory_BypassLayerDelete_PosixNotIntercepted()
    {
        // ⑨ 保护边界：绕过本层的原生删除不受 Sharing 拦截（POSIX 上直接 unlink 验证 advisory 本质；
        //   Windows 磁盘删除被 OS 真拦截——两端行为都不依赖，可移植纪律见 io.md）
        if (OperatingSystem.IsWindows()) return;
        using var h = _fs.Open("adv", Opts(sharing: FileSharing.None));
        var fullPath = Path.Combine(_dir, "adv");
        var act = () => File.Delete(fullPath);
        act.Should().NotThrow("POSIX 删除无条件成功——advisory 不拦裸 IO");
        _fs.Exists("adv").Should().BeFalse();
    }
}

/// <summary>内存介质的平权契约。</summary>
public sealed class MemoryFileSystemContractTests : FileSystemContractTests
{
    private readonly MemoryFileSystem _fs = MemoryFileSystem.New();

    protected override IFileSystem Fs => _fs;

    public override void Dispose() => _fs.Dispose();

    [Fact]
    public void Delete_WithOpenHandle_Succeeds_PosixAligned()
    {
        // ⑧ mem 对齐 POSIX 延迟释放（磁盘侧 Windows 行为由磁盘契约测试覆盖）
        var h = _fs.Open("held", Opts());
        h.Write(0, new byte[64]);
        var act = () => _fs.Delete("held");
        act.Should().NotThrow();
        _fs.Exists("held").Should().BeFalse();
        var b = new byte[8];
        h.Read(0, b).Should().Be(8);   // 旧句柄读旧数据至关闭
        h.Dispose();
    }
}

/// <summary>跨介质互操作断言。</summary>
public sealed class CrossMediumTests
{
    [Fact]
    public void CopyRange_CrossMedium_ThrowsArgumentException()
    {
        var dir = TestTempDir.Create("core-io-cross");
        using var disk = DiskFileSystem.OpenOrCreate(dir);
        disk.EnsureRoot();
        using var mem = MemoryFileSystem.New();
        using var dh = disk.Open("d", new FileOpenOptions { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate });
        using var mh = mem.Open("m", new FileOpenOptions { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate });
        dh.Write(0, new byte[64]);

        ((Action)(() => dh.CopyRange(mh, 0, 0, 64))).Should().Throw<ArgumentException>();
        ((Action)(() => mh.CopyRange(dh, 0, 0, 64))).Should().Throw<ArgumentException>();

        disk.Dispose();
        mem.Dispose();
        TestTempDir.TryCleanup(dir);
    }
}
