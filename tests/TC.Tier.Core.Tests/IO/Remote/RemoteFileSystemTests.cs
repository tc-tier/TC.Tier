using System.Text;
using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Remote;
using TC.Tier.Core.IO.Testing;

namespace TC.Tier.Core.Tests.IO;

/// <summary>
/// RemoteFileSystem 桥契约测试（B3.3 验收 = §7.1 全项：staging 语义 / 持久化×池协议 H2 / 未触区间回填 H1 /
/// AppendCursor 边界 M4 / PunchHole H3 / 延迟加载 / 路径穿越 / CopyRange / 元数据 / 能力位矩阵 /
/// 差异专项 L5 / 恢复）。受测 store = MemoryObjectStore（无网全量）。
/// </summary>
public class RemoteFileSystemTests : IDisposable
{
    private readonly MemoryObjectStore _store = new();
    private readonly RemoteFileSystem _fs;

    public RemoteFileSystemTests()
        => _fs = RemoteFileSystem.OpenOrCreate(_store, new RemoteFileSystemOptions());

    private static FileOpenOptions Opts(AccessMode access = AccessMode.ReadWrite,
        FileOpenMode mode = FileOpenMode.OpenOrCreate, FileSharing sharing = FileSharing.ReadWrite)
        => new() { Access = access, Mode = mode, Sharing = sharing };

    public void Dispose()
    {
        _fs.Dispose();
        _store.Dispose();
        GC.SuppressFinalize(this);
    }

    // ═══════════════ 根空间层级（前缀模拟——filesystem-root-space-design §6）═══════════════

    [Fact]
    public void Hierarchical_CapabilityBits_RemoteHonest()
    {
        _fs.Capabilities.HasFlag(FileSystemCapabilities.EmptyDirectories).Should().BeFalse("S3 无空目录——目录因内容而存在");
        _fs.Capabilities.HasFlag(FileSystemCapabilities.AtomicDirectoryMove).Should().BeFalse("回退 = 逐对象 Copy+Delete");
    }

    [Fact]
    public void Hierarchical_CreateAndEnumerate_PrefixSemantics()
    {
        _fs.CreateFile("s/eng/data.0");
        _fs.CreateFile("s/eng/data.1");
        _fs.CreateFile("s/eng/compact/tmp.0");
        _fs.CreateFile("root-file");

        _fs.DirectoryExists("s").Should().BeTrue();
        _fs.DirectoryExists("s/eng").Should().BeTrue();
        _fs.DirectoryExists("s/eng/compact").Should().BeTrue();
        _fs.DirectoryExists("no-such").Should().BeFalse();

        // 一层枚举（单参=根 pattern / 双参=path+pattern）
        _fs.EnumerateFiles("*").Select(e => e.Name).Should().Equal("root-file");
        _fs.EnumerateDirectories("*").Select(e => e.Name).Should().Equal("s");
        _fs.EnumerateFiles("s/eng", "*").Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal)
            .Should().Equal("data.0", "data.1");   // compact 是目录——Files 族不含（混合族见下）
        // 目录条目时间不可得（MinValue/null 诚实约定）
        var dirEntry = _fs.EnumerateDirectories("s/eng", "*").Single();
        dirEntry.Name.Should().Be("compact");
        dirEntry.LastWriteTime.Should().Be(DateTimeOffset.MinValue);
        // 递归：多组件名
        _fs.EnumerateFiles("s", "*", recursive: true).Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal)
            .Should().Equal("eng/compact/tmp.0", "eng/data.0", "eng/data.1");
        // 混合族 = 文件 ∪ 目录
        var both = _fs.EnumerateEntries("s/eng", "*").OrderBy(e => e.Name, StringComparer.Ordinal).ToArray();
        both.Select(e => (e.Name, e.Type)).Should()
            .Equal(("compact", FsEntryType.Directory), ("data.0", FsEntryType.File), ("data.1", FsEntryType.File));
        // pattern：引擎段扫描形态
        _fs.EnumerateFiles("s/eng", "data.*").Select(e => e.Name).OrderBy(n => n, StringComparer.Ordinal)
            .Should().Equal("data.0", "data.1");
    }

    [Fact]
    public void Hierarchical_CreateDirectory_NoOp_DirExistsByContent()
    {
        _fs.CreateDirectory("any/deep/path");   // 文档化 no-op
        _fs.DirectoryExists("any/deep/path").Should().BeFalse("S3 目录因内容而存在");
        _fs.CreateFile("any/deep/path/f");
        _fs.DirectoryExists("any/deep/path").Should().BeTrue();
    }

    [Fact]
    public void Hierarchical_DeleteDirectory_EmptyOnly_TwoStatesUnified()
    {
        _fs.CreateFile("d/f0");
        ((Action)(() => _fs.DeleteDirectory("d"))).Should().Throw<FileIOException>()
            .Which.Error.Should().Be(IOError.DirectoryNotEmpty);
        _fs.Delete("d/f0");
        // ★ CORE-17：删完子项后的空目录 = 成功 no-op（原 NotFound 抛——正常流程必失败 = 死 API；
        //   S3 无空目录——空/不存在统一幂等删除成功）
        ((Action)(() => _fs.DeleteDirectory("d"))).Should().NotThrow();
    }

    [Fact]
    public void Hierarchical_MoveDirectory_FallbackCopyDelete()
    {
        _fs.CreateFile("mv/a0");
        _fs.CreateFile("mv/sub/a1");
        _fs.MoveDirectory("mv", "mv2");
        _fs.DirectoryExists("mv").Should().BeFalse();
        _fs.Exists("mv2/a0").Should().BeTrue();
        _fs.Exists("mv2/sub/a1").Should().BeTrue();
        // 目标不存在 = 合法新名（rename 语义——搬入）
        _fs.MoveDirectory("mv2", "mv3");
        _fs.Exists("mv3/a0").Should().BeTrue();
        _fs.DirectoryExists("mv2").Should().BeFalse();
        // 目标已有内容 → AlreadyExists；源缺失 → NotFound
        _fs.CreateFile("occupied/f");
        ((Action)(() => _fs.MoveDirectory("mv3", "occupied"))).Should().Throw<FileIOException>()
            .Which.Error.Should().Be(IOError.AlreadyExists);
        ((Action)(() => _fs.MoveDirectory("no-dir", "x"))).Should().Throw<FileIOException>()
            .Which.Error.Should().Be(IOError.NotFound);
    }

    [Fact]
    public void Hierarchical_CreateFile_FileExtraRoundTrip_AlreadyExists()
    {
        var meta = new byte[] { 0xAA, 0xBB, 0xCC };
        _fs.CreateFile("m0", extra: meta);
        var st = _fs.Stat("m0");
        st.FileExtra.ToArray().Should().Equal(meta);
        st.Type.Should().Be(FsEntryType.File);
        ((Action)(() => _fs.CreateFile("m0"))).Should().Throw<FileIOException>()
            .Which.Error.Should().Be(IOError.AlreadyExists);
        ((Action)(() => _fs.CreateFile("m-big", extra: new byte[IFileSystem.MaxFileExtraBytes + 1])))
            .Should().Throw<ArgumentException>();
        ((Action)(() => _fs.Stat("no-such"))).Should().Throw<FileIOException>()
            .Which.Error.Should().Be(IOError.NotFound);
    }

    [Fact]
    public void FileExtra_TwoPlanesMutuallyVisible_Remote()
    {
        var meta = new byte[] { 0x44 };
        _fs.CreateFile("uni-r", extra: meta);
        using (var h = _fs.Open("uni-r", Opts(access: AccessMode.Read, mode: FileOpenMode.OpenExisting)))
        {
            h.FileExtra.ToArray().Should().Equal(meta, "对象用户元数据同库——句柄级立即可见（Head 缓存）");
        }
        // SetFileExtra 入 staging：句柄级立即可见；fs 级须 Flush 提交后可见（随 PUT 原子）
        using (var h = _fs.Open("uni-r", Opts()))
        {
            h.SetFileExtra(new byte[] { 0x55 });
            h.FileExtra.Length.Should().Be(1, "staging 优先");
            h.FileExtra.Span[0].Should().Be((byte)0x55);
            h.WriteFileExtra(1, new byte[] { 0x66 });   // 偏移写 = staging RMW
            h.FileExtra.ToArray().Should().Equal(0x55, 0x66);
            h.Flush();
        }
        _fs.Stat("uni-r").FileExtra.ToArray().Should().Equal(0x55, 0x66);
    }

    [Fact]
    public void Stat_AndEnumeration_ReportLastModified()
    {
        _fs.CreateFile("ts-f");
        var st = _fs.Stat("ts-f");
        st.LastWriteTime.Should().NotBe(DateTimeOffset.MinValue, "HeadObject LastModified 已接（不再占位）");

        var entry = _fs.EnumerateFiles("*").Single(e => e.Name == "ts-f");
        entry.LastWriteTime.Should().NotBe(DateTimeOffset.MinValue, "列举条目 LastModified 已接");
    }

    [Fact]
    public void Enumeration_HiddenDotClass_LockObjectInvisible()
    {
        _fs.CreateFile("plain");
        using (_fs.AcquireExclusive(TimeSpan.FromSeconds(5)))
        {
            // 卷锁对象（.tier-volume-lock，根层点前缀）默认枚举不可见；豁免 pattern 可见
            _fs.EnumerateFiles("*").Select(e => e.Name).Should().Equal("plain");
            _fs.EnumerateFiles(".*").Select(e => e.Name).Should().Contain(".tier-volume-lock");
        }
    }

    // ═══════════════ staging 语义（§7.1）═══════════════

    [Fact]
    public void Write_PastEof_ZeroExtends_HoleReadsZero()
    {
        using var h = _fs.Open("hole", Opts());
        h.Write(16384, new byte[] { 7 });
        h.Length.Should().Be(16385);
        var buf = new byte[64];
        h.Read(0, buf).Should().Be(64);
        buf.Should().OnlyContain(b => b == 0);
        h.Read(16384, buf).Should().Be(1);
        buf[0].Should().Be(7);
    }

    [Fact]
    public void ReadYourWrites_BeforeFlush()
    {
        using var h = _fs.Open("ryw", Opts());
        h.Write(0, new byte[] { 1, 2, 3 });
        var buf = new byte[3];
        h.Read(0, buf).Should().Be(3);
        buf.Should().Equal(1, 2, 3);
        _store.Counters.Puts.Should().Be(0);   // 未 Flush——零上传
    }

    [Fact]
    public void Flush_MakesObjectVisible_ToAnotherFsInstance()
    {
        using (var h = _fs.Open("vis", Opts()))
        {
            h.Write(0, new byte[] { 4, 5, 6 });
            h.Flush();
        }
        using var fs2 = RemoteFileSystem.OpenOrCreate(_store);
        using var r = fs2.Open("vis", Opts(access: AccessMode.Read, mode: FileOpenMode.OpenExisting));
        var buf = new byte[8];
        r.Read(0, buf).Should().Be(3);
        buf[..3].Should().Equal(4, 5, 6);
        fs2.Dispose();
    }

    [Fact]
    public void Flush_Idempotent_NoDirtyNoUpload()
    {
        using var h = _fs.Open("idem", Opts());
        h.Write(0, new byte[] { 1 });
        h.Flush();
        var puts = Volatile.Read(ref _store.Counters.Puts);
        h.Flush();   // 无变更——no-op
        h.Flush();
        Volatile.Read(ref _store.Counters.Puts).Should().Be(puts);
    }

    // ═══════════════ 持久化 × 池协议（H2——验收）═══════════════

    [Fact]
    public void PoolDispose_HandleDispose_KeepsStaging_DataContinuous()
    {
        var pool = new FileHandlePool(_fs);
        using (var h = pool.Acquire("pooled", Opts()))
        {
            h.Append(new byte[] { 1, 2, 3 });
        }   // Dispose = 归还（staging 留池）
        Volatile.Read(ref _store.Counters.Puts).Should().Be(0);   // 未 Flush 不上传

        using (var h2 = pool.Acquire("pooled", Opts()))
        {
            h2.Length.Should().Be(3);   // 归还后数据连续（read-your-writes 跨借用）
            h2.Append(new byte[] { 4 }).Should().Be(3);
            h2.Flush();   // using 块内显式 Flush——"用完即持久"的正确姿势
        }
        pool.Dispose();
        using var r = _fs.Open("pooled", Opts(access: AccessMode.Read, mode: FileOpenMode.OpenExisting));
        r.Length.Should().Be(4);
    }

    [Fact]
    public void UnflushedClose_ObjectDoesNotExist()
    {
        using (var h = _fs.Open("gone", Opts()))
        {
            h.Write(0, new byte[] { 1, 2, 3 });
        }   // 池外直开 Dispose = 关闭（未 Flush 丢弃——"未 fsync 即丢"）
        _fs.Exists("gone").Should().BeFalse();
    }

    [Fact]
    public void ReleaseClose_ObjectDoesNotExist()
    {
        var pool = new FileHandlePool(_fs);
        var h = pool.Acquire("rc", Opts());
        h.Write(0, new byte[] { 9 });
        pool.Release(h, close: true);   // 池内三出口之一（定向关闭）——不 flush
        _fs.Exists("rc").Should().BeFalse();
        pool.Dispose();
    }

    [Fact]
    public void RemoveAll_DoesNotFlush_DoesNotResurrectDeleted()
    {
        var pool = new FileHandlePool(_fs);
        var h = pool.Acquire("rm", Opts());
        h.Write(0, new byte[] { 9 });
        pool.RemoveAll(p => true);   // 引擎删段后回收句柄——flush 会复活已删对象（禁止）
        _fs.Exists("rm").Should().BeFalse();
        pool.Dispose();
    }

    // ═══════════════ 未触区间回填（H1——正确性命脉）═══════════════

    [Fact]
    public void Backfill_UntouchedMiddle_PreservedFromOldObject()
    {
        // 造旧对象：256KiB 模式数据
        var old = new byte[256 * 1024];
        new Random(7).NextBytes(old);
        using (var h = _fs.Open("bf", Opts()))
        {
            h.Write(0, old);
            h.Flush();
        }

        // 重开：随机覆写两点（首尾），中间未触
        using (var h2 = _fs.Open("bf", Opts()))
        {
            h2.Write(1024, new byte[] { 0xAA });
            h2.Write(256 * 1024 - 1, new byte[] { 0xBB });
            h2.Flush();   // 中间区间必须从旧对象回填——静默清零 = 正确性 bug
        }

        using var r = _fs.Open("bf", Opts(access: AccessMode.Read, mode: FileOpenMode.OpenExisting));
        var buf = new byte[256 * 1024];
        r.Read(0, buf).Should().Be(buf.Length);
        buf[1024].Should().Be((byte)0xAA);
        buf[^1].Should().Be((byte)0xBB);
        for (var i = 0; i < buf.Length; i++)
        {
            if (i == 1024 || i == buf.Length - 1) continue;
            _ = i;
            buf[i].Should().Be(old[i], $"offset {i} 未触区间必须与旧对象一致（回填正确性）");
        }
    }

    [Fact]
    public void SetLength_ShrinkThenExtend_ReadsZero_NotOldData()
    {
        var old = new byte[64 * 1024];
        new Random(11).NextBytes(old);
        using (var h = _fs.Open("tr", Opts()))
        {
            h.Write(0, old);
            h.Flush();
        }
        using (var h2 = _fs.Open("tr", Opts()))
        {
            h2.SetLength(1024);       // 截断
            h2.SetLength(64 * 1024);  // 再扩展——POSIX truncate-extend 读零（不复活旧数据）
            h2.Flush();
        }
        using var r = _fs.Open("tr", Opts(access: AccessMode.Read, mode: FileOpenMode.OpenExisting));
        var buf = new byte[64 * 1024];
        r.Read(0, buf).Should().Be(buf.Length);
        buf[..1024].Should().Equal(old[..1024]);       // 保留段数据不变
        buf[1024..].Should().OnlyContain(b => b == 0); // 扩展段全零
    }

    // ═══════════════ AppendCursor（M4 边界验收）═══════════════

    [Fact]
    public void Append_CrossHandleSameFs_OffsetsDoNotOverlap()
    {
        using var h1 = _fs.Open("ap", Opts());
        using var h2 = _fs.Open("ap", Opts());   // 同 fs 第二句柄——文件级游标共享
        var o1 = h1.Append(new byte[10]);
        var o2 = h2.Append(new byte[10]);
        o2.Should().Be(o1 + 10);   // 跨句柄原子推进（文件级游标——落点不交）
        h2.Length.Should().Be(20); // 写者视角：staging 覆盖 [0,20)（h1 视角隔离——句柄级缓存 §4.5）
    }

    [Fact]
    public void Append_ConcurrentSameHandle_AllDistinctFullCoverage()
    {
        using var h = _fs.Open("apc", Opts());
        const int threads = 6, perThread = 100, len = 64;
        var offsets = new System.Collections.Concurrent.ConcurrentBag<long>();
        System.Threading.Tasks.Parallel.For(0, threads, _ =>
        {
            var data = new byte[len];
            for (var i = 0; i < perThread; i++)
                offsets.Add(h.Append(data));
        });
        offsets.Distinct().Count().Should().Be(threads * perThread);
        h.Length.Should().Be((long)threads * perThread * len);
        h.Flush();
        _fs.Open("apc", Opts(access: AccessMode.Read, mode: FileOpenMode.OpenExisting)).Length.Should().Be((long)threads * perThread * len);
    }

    [Fact]
    public void Append_AfterSetLengthTruncate_StartsFromNewEnd()
    {
        using var h = _fs.Open("apt", Opts());
        h.Append(new byte[100]);
        h.SetLength(40);
        h.Append(new byte[8]).Should().Be(40);   // 权威复位后从新末端
    }

    [Fact]
    public void Append_AfterDeleteRebuild_StartsFromZero()
    {
        using (var h = _fs.Open("apd", Opts()))
        {
            h.Append(new byte[64]);
            h.Flush();
        }
        _fs.Delete("apd");
        using var h2 = _fs.Open("apd", Opts());
        h2.Append(new byte[4]).Should().Be(0);   // 盒摘除后按新 Length（0）重建
    }

    [Fact]
    public void Append_CrossFsInstance_InvisibleToEachOther()
    {
        // 边界断言（M4）——跨 fs 实例互不可见（远程必绿：固化预期行为非 bug）
        using var fs2 = RemoteFileSystem.OpenOrCreate(_store);
        using var h1 = _fs.Open("xinst", Opts());
        using var h2 = fs2.Open("xinst", Opts());
        h1.Append(new byte[100]);
        h2.Append(new byte[10]).Should().Be(0);   // fs2 视角对象仍空（h1 未 Flush）
        fs2.Dispose();
    }

    // ═══════════════ PunchHole（H3 + 防偏移错位）═══════════════

    [Fact]
    public void PunchHole_Unaligned_DoesNotThrow_AllocationUnitIs1()
    {
        _fs.Volume.AllocationUnit.Should().Be(1);   // H3：无物理对齐约束
        using var h = _fs.Open("ph", Opts());
        h.Write(0, new byte[4096]);
        var act = () => h.PunchHole(1000, 777);   // 未对齐
        act.Should().NotThrow();
    }

    [Fact]
    public void PunchHole_AfterFlush_ContentLevelZero_NoOffsetShift()
    {
        var data = new byte[256 * 1024];
        new Random(3).NextBytes(data);
        using (var h = _fs.Open("ph2", Opts()))
        {
            h.Write(0, data);
            h.Flush();
        }
        using (var h2 = _fs.Open("ph2", Opts()))
        {
            h2.PunchHole(64 * 1024, 128 * 1024);   // 中段 128KiB 打洞
            h2.Flush();   // 全量上传——跳 part = 错位（禁止）
        }
        using var r = _fs.Open("ph2", Opts(access: AccessMode.Read, mode: FileOpenMode.OpenExisting));
        r.Length.Should().Be(data.Length);   // 长度不变
        var buf = new byte[data.Length];
        r.Read(0, buf).Should().Be(data.Length);
        buf[..(64 * 1024)].Should().Equal(data[..(64 * 1024)]);            // 洞前逐字节正确（防错位）
        buf[(64 * 1024)..(192 * 1024)].Should().OnlyContain(b => b == 0);  // 洞区间全零（内容级）
        buf[(192 * 1024)..].Should().Equal(data[(192 * 1024)..]);          // 洞后逐字节正确
    }

    // ═══════════════ 延迟加载（网络调用计数断言）═══════════════

    [Fact]
    public void OpenExisting_ZeroDownload()
    {
        using (var h = _fs.Open("lazy", Opts()))
        {
            h.Write(0, new byte[128 * 1024]);
            h.Flush();
        }
        var getsBefore = Volatile.Read(ref _store.Counters.Gets);
        var h2 = _fs.Open("lazy", Opts());
        Volatile.Read(ref _store.Counters.Gets).Should().Be(getsBefore);   // Open 零下载（仅 Head）
        h2.Dispose();
    }

    [Fact]
    public void PureAppendHandle_NeverFetchesHistory()
    {
        using (var h = _fs.Open("pa", Opts()))
        {
            h.Write(0, new byte[128 * 1024]);
            h.Flush();
        }
        var getsBefore = Volatile.Read(ref _store.Counters.Gets);
        using var h2 = _fs.Open("pa", Opts());
        for (var i = 0; i < 100; i++)
            h2.Append(new byte[256]);   // 纯追加
        Volatile.Read(ref _store.Counters.Gets).Should().Be(getsBefore);   // 追加路径永不加载历史数据
        var buf = new byte[128 * 1024];
        h2.Read(0, buf).Should().Be(buf.Length);   // 读路径：物化旧数据区间（按需）
        Volatile.Read(ref _store.Counters.Gets).Should().BeGreaterThan(getsBefore);
    }

    [Fact]
    public void RandomOverwrite_FetchesOnDemand_DataCorrect()
    {
        var data = new byte[192 * 1024];
        new Random(5).NextBytes(data);
        using (var h = _fs.Open("ro", Opts()))
        {
            h.Write(0, data);
            h.Flush();
        }
        using var h2 = _fs.Open("ro", Opts());
        h2.Write(100_000, new byte[] { 0xEE });   // 按需拉取该区间
        var buf = new byte[data.Length];
        h2.Read(0, buf).Should().Be(data.Length); // 读全量（read-your-writes 含旧数据）
        buf[100_000].Should().Be((byte)0xEE);
        for (var i = 0; i < buf.Length; i++)
        {
            if (i == 100_000) continue;
            buf[i].Should().Be(data[i]);
        }
    }

    // ═══════════════ 路径穿越（KeyPrefix 边界防线）═══════════════

    [Theory]
    [InlineData("../escape")]
    [InlineData("..")]
    [InlineData("a\\b")]
    [InlineData("")]
    [InlineData("a:b")]
    public void PathTraversal_AllRejected(string path)
    {
        ((Action)(() => _fs.Open(path, Opts()))).Should().Throw<ArgumentException>();
        ((Action)(() => _fs.Exists(path))).Should().Throw<ArgumentException>();
        ((Action)(() => _fs.Delete(path))).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void KeyPrefix_NamespaceIsolation()
    {
        // 同桶他前缀对象注入——本 fs 不可见（越权防线）
        _store.PutAsync("other-engine/secret", new byte[] { 1 }).AsTask().GetAwaiter().GetResult();
        using var fs = RemoteFileSystem.OpenOrCreate(_store, new RemoteFileSystemOptions { KeyPrefix = "engine-a/" });
        fs.Exists("secret").Should().BeFalse();   // 他前缀对象不可见
        fs.EnumerateFiles().Should().NotContain(e => e.Name == "secret");
        fs.Dispose();
    }

    // ═══════════════ CopyRange（服务端零流量路径）═══════════════

    [Fact]
    public void CopyRange_NewTarget_ServerSideZeroDownload()
    {
        var data = new byte[100_000];
        new Random(9).NextBytes(data);
        using (var h = _fs.Open("cr-src", Opts()))
        {
            h.Write(0, data);
            h.Flush();
        }
        using var src = _fs.Open("cr-src", Opts(access: AccessMode.Read, mode: FileOpenMode.OpenExisting));
        using var dst = _fs.Open("cr-dst", Opts());   // 全新目标

        var getsBefore = Volatile.Read(ref _store.Counters.GetBytes);
        var copiesBefore = Volatile.Read(ref _store.Counters.CopyRanges);
        src.CopyRange(dst, 1024, 0, 50_000).Should().Be(50_000);
        Volatile.Read(ref _store.Counters.CopyRanges).Should().Be(copiesBefore + 1);   // 服务端路径
        Volatile.Read(ref _store.Counters.GetBytes).Should().Be(getsBefore);           // 零下载零出口

        dst.Length.Should().Be(50_000);
        var buf = new byte[50_000];
        dst.Read(0, buf).Should().Be(50_000);
        buf.Should().Equal(data.AsSpan(1024, 50_000).ToArray());
    }

    [Fact]
    public void CopyRange_PartialOverwrite_FallsBackToLocalLoop()
    {
        var data = new byte[8192];
        new Random(13).NextBytes(data);
        using var src = _fs.Open("cs", Opts());
        using var dst = _fs.Open("cd", Opts());
        src.Write(0, data);
        dst.Write(0, new byte[4096]);   // 目标非全新 → 回退本地循环

        var copiesBefore = Volatile.Read(ref _store.Counters.CopyRanges);
        src.CopyRange(dst, 512, 1024, 2048).Should().Be(2048);
        Volatile.Read(ref _store.Counters.CopyRanges).Should().Be(copiesBefore);   // 未走服务端

        var a = new byte[32];
        var b = new byte[32];
        src.Read(512, a);
        dst.Read(1024, b);
        a.Should().Equal(b);
    }

    // ═══════════════ FileExtra（对象用户元数据 ↔ PUT 原子快照）═══════════════

    [Fact]
    public void FileExtra_RoundTripsThroughFlush_Base64ByteFidelity()
    {
        var payload = new byte[] { 0x00, 0xFF, 0x10, 0x7F, 0xB1 };   // 任意字节——Base64 往返保真
        using (var h = _fs.Open("x", Opts()))
        {
            h.SetFileExtra(payload);
            h.Write(0, new byte[] { 1 });
            h.FileExtra.ToArray().Should().Equal(payload);   // Flush 前读 = staging 值
            h.Flush();
        }
        using var r = _fs.Open("x", Opts(access: AccessMode.Read, mode: FileOpenMode.OpenExisting));
        r.FileExtra.ToArray().Should().Equal(payload);   // Flush 后新 Open 可见
    }

    [Fact]
    public void FileExtra_ReadOnlyHandle_RejectsWrite()
    {
        _fs.CreateFile("xro", extra: new byte[] { 1 });
        using var h = _fs.Open("xro", Opts(access: AccessMode.Read, mode: FileOpenMode.OpenExisting));
        h.FileExtra.ToArray().Should().Equal(1);
        ((Action)(() => h.SetFileExtra(new byte[] { 2 }))).Should().Throw<InvalidOperationException>();
        ((Action)(() => h.WriteFileExtra(0, new byte[] { 2 }))).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void FileExtra_OverLimit_AtWriteNotFlush()
    {
        using var h = _fs.Open("xl", Opts());
        // 预算封顶前移到写入点（非 Flush 聚合点）——统一平面契约
        ((Action)(() => h.SetFileExtra(new byte[IFileSystem.MaxFileExtraBytes + 1])))
            .Should().Throw<ArgumentException>();
        ((Action)(() => h.WriteFileExtra(IFileSystem.MaxFileExtraBytes, new byte[] { 1 })))
            .Should().Throw<ArgumentException>();
    }

    // ═══════════════ 能力位矩阵 + Unsupported 族 ════════════════

    [Fact]
    public void Capabilities_Matrix()
    {
        var caps = _fs.Capabilities;
        caps.Should().HaveFlag(FileSystemCapabilities.DurableRename)
            .And.HaveFlag(FileSystemCapabilities.ExclusiveLock)
            .And.HaveFlag(FileSystemCapabilities.Advise)
            .And.HaveFlag(FileSystemCapabilities.CopyRange);
        // G8/G11 翻案后置位：RangeLock（进程内 advisory 区间表）/ Mmap（物化映射——形态差异见 io.md 差异表）
        caps.Should().HaveFlag(FileSystemCapabilities.RangeLock)
            .And.HaveFlag(FileSystemCapabilities.Mmap);
        caps.Should().NotHaveFlag(FileSystemCapabilities.Sparse)
            .And.NotHaveFlag(FileSystemCapabilities.DirectIO)
            .And.NotHaveFlag(FileSystemCapabilities.WriteThrough)
            .And.NotHaveFlag(FileSystemCapabilities.FlushDataOnly)
            .And.NotHaveFlag(FileSystemCapabilities.RangeShift)
            .And.NotHaveFlag(FileSystemCapabilities.VectorIO)
            .And.NotHaveFlag(FileSystemCapabilities.RandomWrite);   // 延迟加载悬崖——不置位
    }

    [Fact]
    public void UnsupportedFamily_Throws()
    {
        using var h = _fs.Open("un", Opts());
        h.Write(0, new byte[8]);   // Lock/Map 族已真实现（G8/G11）——RangeShift 族仍 Unsupported
        ((Action)(() => h.CollapseRange(0, 4))).Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.Unsupported);
        ((Action)(() => h.InsertRange(0, 4))).Should().Throw<FileIOException>();
    }

    [Fact]
    public void Volume_Geometry()
    {
        _fs.Volume.SectorSize.Should().Be(1);
        _fs.Volume.AllocationUnit.Should().Be(1);   // H3：无物理对齐约束
        _fs.Volume.FreeSpace.Should().Be(-1);
    }

    // ═══════════════ 差异专项（L5——读句柄不追新，固化为预期行为）═══════════════

    [Fact]
    public void Difference_OpenedReaderSeesOldData_AfterWriterFlush()
    {
        using (var h = _fs.Open("stale", Opts()))
        {
            h.Write(0, new byte[8192]);
            h.Flush();
        }
        using var reader = _fs.Open("stale", Opts(access: AccessMode.Read, mode: FileOpenMode.OpenExisting));
        reader.Length.Should().Be(8192);

        using var writer = _fs.Open("stale", Opts());
        writer.Append(new byte[4096]);
        writer.Flush();   // 追加已持久——但已开读句柄不追新（远程必绿：句柄级缓存语义）

        reader.Length.Should().Be(8192);   // 缓存长度（L5 双重陈旧来源之一）
        var buf = new byte[4096];
        reader.Read(8192, buf).Should().Be(0);   // 追加区不可见

        // 重新 Open = 拉最新版本（需要追新的正确姿势）
        using var fresh = _fs.Open("stale", Opts(access: AccessMode.Read, mode: FileOpenMode.OpenExisting));
        fresh.Length.Should().Be(12288);
    }

    // ═══════════════ 恢复（与磁盘恢复协议同构）═══════════════

    [Fact]
    public void Recovery_NewInstanceEnumeratesRebuildsSegmentTable()
    {
        for (var i = 0; i < 5; i++)
        {
            using var h = _fs.Open($"seg-{i:D3}", Opts());
            h.Write(0, new byte[1024 * (i + 1)]);
            h.Flush();
        }
        using var fs2 = RemoteFileSystem.OpenOrCreate(_store);
        var entries = fs2.EnumerateFiles().OrderBy(e => e.Name, StringComparer.Ordinal).ToArray();
        entries.Length.Should().Be(5);
        for (var i = 0; i < 5; i++)
        {
            entries[i].Name.Should().Be($"seg-{i:D3}");
            entries[i].Length.Should().Be(1024L * (i + 1));   // (Name, Size) 融合——恢复扫描零额外 Head
        }
        fs2.Dispose();
    }

    // ═══════════════ 命名空间（打开语义/共享/Move/Delete——平权节选）═══════════════

    [Fact]
    public void OpenSemantics_Matrix()
    {
        ((Action)(() => _fs.Open("miss", Opts(access: AccessMode.Read, mode: FileOpenMode.OpenExisting))))
            .Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.NotFound);

        using (var h = _fs.Open("dup", Opts())) { h.Write(0, new byte[1]); h.Flush(); }   // 存在性 = store 状态（Flush 唯一持久化点；无脏 Flush = no-op）
        ((Action)(() => _fs.Open("dup", Opts(mode: FileOpenMode.CreateNew))))
            .Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.AlreadyExists);

        using (var h = _fs.Open("tr", Opts())) { h.Write(0, new byte[512]); h.Flush(); }
        using var h2 = _fs.Open("tr", Opts(mode: FileOpenMode.Truncate));
        h2.Length.Should().Be(0);
    }

    [Fact]
    public void SharingConflict_Detected_SameInstance()
    {
        using var h1 = _fs.Open("shared", Opts(sharing: FileSharing.Read));
        ((Action)(() => _fs.Open("shared", Opts(access: AccessMode.Write, mode: FileOpenMode.OpenExisting))))
            .Should().Throw<IOException>();
    }

    [Fact]
    public void Move_ServerSideCopy_SourceGone_TargetIntact()
    {
        var data = new byte[10_000];
        new Random(21).NextBytes(data);
        using (var h = _fs.Open("mv", Opts()))
        {
            h.Write(0, data);
            h.Flush();
        }
        _fs.Move("mv", "mv2");
        _fs.Exists("mv").Should().BeFalse();
        using var r = _fs.Open("mv2", Opts(access: AccessMode.Read, mode: FileOpenMode.OpenExisting));
        r.Length.Should().Be(data.Length);
        var buf = new byte[data.Length];
        r.Read(0, buf).Should().Be(data.Length);
        buf.Should().Equal(data);
    }

    [Fact]
    public void Move_OverwriteFalse_TargetExists_Throws()
    {
        using (var h = _fs.Open("m1", Opts())) { h.Write(0, new byte[1]); h.Flush(); }
        using (var h = _fs.Open("m2", Opts())) { h.Write(0, new byte[1]); h.Flush(); }
        ((Action)(() => _fs.Move("m1", "m2", overwrite: false)))
            .Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.AlreadyExists);
    }

    [Fact]
    public void Delete_Idempotent()
    {
        var act = () => _fs.Delete("nope");
        act.Should().NotThrow();
        act.Should().NotThrow();
    }

    // ═══════════════ multipart 大对象路径 ════════════════

    [Fact]
    public void LargeObject_MultipartFlush_BytePerfect()
    {
        var options = new RemoteFileSystemOptions { MultipartThreshold = 64 * 1024, PartSize = 5 * 1024 * 1024 };
        using var store = new MemoryObjectStore();
        using var fs = RemoteFileSystem.OpenOrCreate(store, options);
        var data = new byte[3 * 64 * 1024 + 123];   // 3+ parts（阈值 64KiB）
        new Random(33).NextBytes(data);
        using (var h = fs.Open("big", Opts()))
        {
            h.Write(0, data);
            h.Flush();
        }
        using var r = fs.Open("big", Opts(access: AccessMode.Read, mode: FileOpenMode.OpenExisting));
        var buf = new byte[data.Length];
        r.Read(0, buf).Should().Be(data.Length);
        buf.Should().Equal(data);
    }

    [Fact]
    public void LargeObject_MixedMaterialization_ServerCopyPlusUpload()
    {
        // 首段未触（服务端 UploadPartCopy）+ 后段写入（上传）混合——数据逐字节正确
        var options = new RemoteFileSystemOptions { MultipartThreshold = 1024 * 1024 };   // PartSize 默认 8MB
        using var store = new MemoryObjectStore();
        using var fs = RemoteFileSystem.OpenOrCreate(store, options);
        var data = new byte[12 * 1024 * 1024];   // 2 parts（8MB+4MB）
        new Random(44).NextBytes(data);
        using (var h = fs.Open("mix", Opts()))
        {
            h.Write(0, data);
            h.Flush();
        }
        using (var h2 = fs.Open("mix", Opts()))
        {
            h2.Write(12 * 1024 * 1024 - 1, new byte[] { 0x99 });   // 只触第 2 part——第 1 part 走服务端拷贝
            h2.Flush();
        }
        using var r = fs.Open("mix", Opts(access: AccessMode.Read, mode: FileOpenMode.OpenExisting));
        var buf = new byte[data.Length];
        r.Read(0, buf).Should().Be(data.Length);
        buf[^1].Should().Be((byte)0x99);
        buf[..^1].Should().Equal(data[..^1]);
        fs.Dispose();
    }

    [Fact]
    public void Multipart_ConcurrentUpload_ManyParts_BytePerfect()
    {
        // 多 part（6×5MB）+ 交错物化（偶数 part 写/奇数 part 服务端拷贝）+ 并发度 2——
        // 分类正确性 + Task.WhenAll 排序（PartNumber 升序拼接契约）+ 节流路径全覆盖
        var options = new RemoteFileSystemOptions
        {
            MultipartThreshold = 1024 * 1024,
            MaxConcurrency = 2,
        };
        using var store = new MemoryObjectStore();
        using var fs = RemoteFileSystem.OpenOrCreate(store, options);
        var data = new byte[6 * 5 * 1024 * 1024];
        new Random(66).NextBytes(data);
        using (var h = fs.Open("many", Opts()))
        {
            h.Write(0, data);
            h.Flush();
        }
        using (var h2 = fs.Open("many", Opts()))
        {
            for (var part = 0; part < 6; part += 2)   // 偶数 part 覆写一字节
                h2.Write((long)part * 5 * 1024 * 1024, new byte[] { (byte)(0xC0 + part) });
            h2.Flush();
        }
        using var r = fs.Open("many", Opts(access: AccessMode.Read, mode: FileOpenMode.OpenExisting));
        var buf = new byte[data.Length];
        r.Read(0, buf).Should().Be(data.Length);
        for (var part = 0; part < 6; part += 2)
            buf[part * 5 * 1024 * 1024].Should().Be((byte)(0xC0 + part));   // 覆写点
        for (var i = 0; i < buf.Length; i++)
        {
            if (i % (5 * 1024 * 1024) == 0 && i / (5 * 1024 * 1024) % 2 == 0) continue;
            buf[i].Should().Be(data[i], $"offset {i}（并发 part 排序/回填正确性）");
        }
        fs.Dispose();
    }

    [Fact]
    public void OrphanUploadCleanup_StartupScanAbortsStaleSessions()
    {
        // 造孤儿：会话创建 + 上传 part 后遗弃（不 complete/abort——崩溃残留形态）
        var orphan = _store.CreateMultipartUpload("orphan-seg", null);
        orphan.UploadPartAsync(1, new byte[5 * 1024 * 1024]).AsTask().GetAwaiter().GetResult();
        var fresh = _store.CreateMultipartUpload("fresh-seg", null);   // 刚发起——不误杀
        _store.BackdateUploadSessionForTest("orphan-seg", DateTimeOffset.UtcNow - TimeSpan.FromHours(2));   // 崩溃残留形态（2h 前发起）
        _store.ActiveUploadSessions.Should().Be(2);

        // 带 OrphanUploadCleanup 的 fs 构造 → 扫描：orphan（早于阈值）清、fresh 保留
        using var fs2 = RemoteFileSystem.OpenOrCreate(_store, new RemoteFileSystemOptions
        {
            OrphanUploadCleanup = TimeSpan.FromMinutes(30),
        });
        _store.ActiveUploadSessions.Should().Be(1);   // 仅 fresh 在途
        var remaining = _store.ListMultipartUploadsAsync().AsTask().GetAwaiter().GetResult();
        remaining.Should().ContainSingle(x => x.Key == "fresh-seg");
        _fs.Exists("orphan-seg").Should().BeFalse();   // 残留未 complete——对象本就不存在

        // 默认 null = 不扫描
        using var fs3 = RemoteFileSystem.OpenOrCreate(_store);
        _store.ActiveUploadSessions.Should().Be(1);
    }

    [Fact]
    public void IncrementalFlush_SecondFlushUploadsOnlyDelta()
    {
        var options = new RemoteFileSystemOptions { MultipartThreshold = 1024 * 1024 };   // PartSize 默认 8MB
        using var store = new MemoryObjectStore();
        using var fs = RemoteFileSystem.OpenOrCreate(store, options);
        var data = new byte[12 * 1024 * 1024];
        new Random(77).NextBytes(data);

        using (var h = fs.Open("inc", Opts()))
        {
            h.Write(0, data);
            h.Flush();   // 首次全量上传
        }
        var partBytes1 = Volatile.Read(ref store.Counters.UploadPartBytes);

        // 追加 4MB → 二次 Flush：旧 part（clean 页/未触）全部服务端拷贝，仅增量 part 上传
        using (var h2 = fs.Open("inc", Opts()))
        {
            h2.Append(data.AsMemory(0, 4 * 1024 * 1024).Span.ToArray());
            var copiesBefore = Volatile.Read(ref store.Counters.UploadPartCopies);
            h2.Flush();
            var partBytes2 = Volatile.Read(ref store.Counters.UploadPartBytes);
            var delta = partBytes2 - partBytes1;
            delta.Should().BeGreaterThan(4L * 1024 * 1024);   // ≥ 增量（跨界 part 补齐到 part 边界）
            delta.Should().BeLessThanOrEqualTo(8L * 1024 * 1024 + 2 * 1024 * 1024);   // 远小于全量 16MB
            Volatile.Read(ref store.Counters.UploadPartCopies).Should().BeGreaterThan(copiesBefore);   // 旧 part 走拷贝
        }

        // 数据逐字节正确（增量拼装无错位）
        using var r = fs.Open("inc", Opts(access: AccessMode.Read, mode: FileOpenMode.OpenExisting));
        var buf = new byte[16 * 1024 * 1024];
        r.Read(0, buf).Should().Be(buf.Length);
        buf[..(12 * 1024 * 1024)].Should().Equal(data);
        buf[(12 * 1024 * 1024)..].Should().Equal(data[..(4 * 1024 * 1024)]);
        fs.Dispose();
    }

    [Fact]
    public void IncrementalFlush_PunchHole_DirtiesPart_NotServerCopied()
    {
        var options = new RemoteFileSystemOptions { MultipartThreshold = 1024 * 1024 };
        using var store = new MemoryObjectStore();
        using var fs = RemoteFileSystem.OpenOrCreate(store, options);
        var data = new byte[12 * 1024 * 1024];
        new Random(88).NextBytes(data);
        using (var h = fs.Open("ph-inc", Opts()))
        {
            h.Write(0, data);
            h.Flush();
        }
        using (var h2 = fs.Open("ph-inc", Opts()))
        {
            h2.PunchHole(1024 * 1024, 1024 * 1024);   // 第一个 part（[0,8MB)）内打洞 → 该 part 变脏
            h2.Flush();
        }
        // 洞语义正确（内容级——增量优化不破坏打洞）
        using var r = fs.Open("ph-inc", Opts(access: AccessMode.Read, mode: FileOpenMode.OpenExisting));
        var buf = new byte[data.Length];
        r.Read(0, buf).Should().Be(buf.Length);
        buf[..(1024 * 1024)].Should().Equal(data[..(1024 * 1024)]);
        buf[(1024 * 1024)..(2 * 1024 * 1024)].Should().OnlyContain(b => b == 0);
        buf[(2 * 1024 * 1024)..].Should().Equal(data[(2 * 1024 * 1024)..]);
        fs.Dispose();
    }

    [Fact]
    public void HoleMetadata_ReadPath_SkipsRangeGet()
    {
        var data = new byte[512 * 1024];
        new Random(99).NextBytes(data);
        using (var h = _fs.Open("hm", Opts()))
        {
            h.Write(0, data);
            h.PunchHole(64 * 1024, 256 * 1024);   // 中段 256KiB 洞
            h.Flush();   // 洞区间随对象元数据原子提交（tier-holes）
        }

        var getsBefore = Volatile.Read(ref _store.Counters.Gets);
        using var r = _fs.Open("hm", Opts(access: AccessMode.Read, mode: FileOpenMode.OpenExisting));
        var buf = new byte[512 * 1024];
        r.Read(0, buf).Should().Be(buf.Length);
        buf[..(64 * 1024)].Should().Equal(data[..(64 * 1024)]);              // 洞前真实取数
        buf[(64 * 1024)..(320 * 1024)].Should().OnlyContain(b => b == 0);    // 洞区间零（本地填充）
        buf[(320 * 1024)..].Should().Equal(data[(320 * 1024)..]);            // 洞后真实取数
        var fetched = Volatile.Read(ref _store.Counters.Gets) - getsBefore;
        fetched.Should().BeLessThanOrEqualTo(2);   // 仅洞前/洞后分片（256KiB 洞不发 GET——读路径收益）

        // 写填洞后 Flush：洞元数据随内容更新（重开读到写入值，非零）
        using (var h2 = _fs.Open("hm", Opts()))
        {
            h2.Write(100 * 1024, new byte[] { 0xEE });
            h2.Flush();
        }
        using var r2 = _fs.Open("hm", Opts(access: AccessMode.Read, mode: FileOpenMode.OpenExisting));
        var one = new byte[1];
        r2.Read(100 * 1024, one).Should().Be(1);
        one[0].Should().Be((byte)0xEE);
    }

    // ═══════════════ spill（staging 超 内存预算落盘）═══════════════

    [Fact]
    public void Spill_OverMemoryLimit_DataStillCorrect()
    {
        var spillDir = TestTempDir.Create("tier-spill");   // 改走 TestTempDir（原手工清理保留——本改补 TC_TEST_TMP 支持）
        try
        {
            var options = new RemoteFileSystemOptions
            {
                StagingMemoryLimit = 32 * 1024,     // 32KiB 预算——立即触发 spill
                StagingPageSize = 8 * 1024,
                Spill = RemoteSpill.ToDisk(spillDir),
            };
            using var store = new MemoryObjectStore();
            using var fs = RemoteFileSystem.OpenOrCreate(store, options);
            var data = new byte[256 * 1024];
            new Random(55).NextBytes(data);
            using (var h = fs.Open("sp", Opts()))
            {
                h.Write(0, data);
                h.Flush();   // spill 中数据正确上传
            }
            using var r = fs.Open("sp", Opts(access: AccessMode.Read, mode: FileOpenMode.OpenExisting));
            var buf = new byte[data.Length];
            r.Read(0, buf).Should().Be(data.Length);
            buf.Should().Equal(data);
            fs.Dispose();
        }
        finally
        {
            try { Directory.Delete(spillDir, true); } catch { /* 清理尽力 */ }
        }
    }

    [Fact]
    public void SpillToMemory_NoDisk_OverLimit_DataStillCorrect()
    {
        var options = new RemoteFileSystemOptions
        {
            StagingMemoryLimit = 16 * 1024,
            StagingPageSize = 8 * 1024,
            Spill = RemoteSpill.ToMemory(),   // 无盘形态——spool 到 fs 级私有内存卷（非 DiskFull）
        };
        using var store = new MemoryObjectStore();
        using var fs = RemoteFileSystem.OpenOrCreate(store, options);
        var data = new byte[128 * 1024];
        new Random(61).NextBytes(data);
        using (var h = fs.Open("msp", Opts()))
        {
            var act = () => h.Write(0, data);
            act.Should().NotThrow();   // 超预算 spill 进内存卷——不再 DiskFull
            h.Flush();
        }
        using var r = fs.Open("msp", Opts(access: AccessMode.Read, mode: FileOpenMode.OpenExisting));
        var buf = new byte[data.Length];
        r.Read(0, buf).Should().Be(data.Length);
        buf.Should().Equal(data);
        fs.Dispose();
    }

    [Fact]
    public void NoSpillDirectory_OverLimit_ThrowsDiskFull()
    {
        var options = new RemoteFileSystemOptions
        {
            StagingMemoryLimit = 16 * 1024,
            StagingPageSize = 8 * 1024,
            Spill = null,   // 不配置 = 无中转（超限 DiskFull 既有语义）
        };
        using var store = new MemoryObjectStore();
        using var fs = RemoteFileSystem.OpenOrCreate(store, options);
        using var h = fs.Open("nos", Opts());
        ((Action)(() => h.Write(0, new byte[64 * 1024]))).Should().Throw<FileIOException>()
            .Which.Error.Should().Be(IOError.DiskFull);   // 无盘形态——超限即 DiskFull
        fs.Dispose();
    }

    // ═══════════════ Dispose 语义（磁盘方向："离开目录"）═══════════════

    [Fact]
    public void Dispose_OpenThrows_OpenHandlesStillWork()
    {
        var store = new MemoryObjectStore();
        var fs = RemoteFileSystem.OpenOrCreate(store);
        var h = fs.Open("alive", Opts());
        h.Write(0, new byte[] { 1 });
        fs.Dispose();
        ((Action)(() => fs.Open("any", Opts()))).Should().Throw<ObjectDisposedException>();
        var act = () => { var b = new byte[1]; h.Read(0, b); };   // 已开句柄继续可用（staging 自持）
        act.Should().NotThrow();
        h.Dispose();
        store.Dispose();
    }

    // ═══════════════ 异步族（直通对象层异步）═══════════════

    [Fact]
    public async Task AsyncFamily_Works()
    {
        using var h = _fs.Open("async", Opts());
        var o1 = await h.AppendAsync(new byte[] { 1, 2, 3 }, CancellationToken.None);
        o1.Should().Be(0);
        var buf = new byte[3];
        (await h.ReadAsync(0, buf, CancellationToken.None)).Should().Be(3);
        buf.Should().Equal(1, 2, 3);
        var o2 = await h.AppendAsync(new byte[] { 4 }, CancellationToken.None);
        o2.Should().Be(3);   // 追加式文件只经 Append 增长（调用方纪律）
        await h.WriteAsync(5, new byte[] { 9 }, CancellationToken.None);   // 覆写既有区间（非增长）
        var b = new byte[1];
        (await h.ReadAsync(5, b, CancellationToken.None)).Should().Be(1);
        b[0].Should().Be(9);
        h.Flush();
    }

    // ═══════════════ 动词面（P2 收尾：New / Open / OpenOrCreate——Create 已退役）═══════════════

    [Fact]
    public void Verb_New_OnNonEmptyPrefix_ThrowsAlreadyExists()
    {
        using (var h = _fs.Open("exist", Opts()))
        {
            h.Write(0, new byte[8]);
            h.Flush();
        }
        _fs.Dispose();   // 释放 fencing（store 保留——前缀内容仍在）
        var act = () => RemoteFileSystem.New(_store);
        act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.AlreadyExists,
            "New 的前缀枚举检查——防误覆盖既有命名空间（设计 §2.3）");
    }

    [Fact]
    public void Verb_Open_LabelAssertion_Enforced()
    {
        var store = new MemoryObjectStore();
        RemoteFileSystem.New(store, new RemoteFileSystemOptions { Label = "vol-x" }).Dispose();
        var ok = RemoteFileSystem.Open(store, new RemoteFileSystemOptions { Label = "vol-x" });   // 标记对象在 store 内
        ok.Dispose();
        var act = () => RemoteFileSystem.Open(store, new RemoteFileSystemOptions { Label = "wrong" });
        act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.NotFound,
            "Open + label = 断言（标记对象比对——fail-fast）");
    }

    [Fact]
    public void Verb_OpenOrCreate_BindAny_BothStates()
    {
        var store = new MemoryObjectStore();
        using (var fresh = RemoteFileSystem.OpenOrCreate(store))   // 全新——空视图
        {
            fresh.EnumerateFiles("*").Should().BeEmpty();
        }
        using (var h = RemoteFileSystem.OpenOrCreate(store).Open("keep", Opts()))
        {
            h.Write(0, new byte[4]);
            h.Flush();
        }
        using var existing = RemoteFileSystem.OpenOrCreate(store);   // 既有（含内容）——不抛
        existing.EnumerateFiles("*").Count().Should().Be(1, "bind-any 两态通吃");
        store.Dispose();
    }

    // ══════════════════ CORE-04/05/06 数据损坏级回归（补集加载/截断-扩展/Flush 并发写）══════════════════

    [Fact]
    public void Core04_NonPageAlignedAppend_PreservesOldTail()
    {
        var store = new MemoryObjectStore();
        using (var fs = RemoteFileSystem.OpenOrCreate(store, new RemoteFileSystemOptions { StagingPageSize = 64 * 1024 }))
        using (var h = fs.Open("f", Opts()))
        {
            h.Write(0, new byte[64 * 1024]);   // 旧对象 = 整页（非页对齐旧数据：页尾 [64K] = 0xAB）
            h.Write(64 * 1024, new byte[] { 0xAB });   // 旧数据尾字节（offset 64K = 页末）
            h.Flush();
        }
        using (var fs2 = RemoteFileSystem.OpenOrCreate(store, new RemoteFileSystemOptions { StagingPageSize = 64 * 1024 }))
        using (var h2 = fs2.Open("f", Opts()))
        {
            // ★ CORE-04：非页对齐追加——追加起点 offset=64K+1 位于旧数据页内（页首 64K 含旧数据 0xAB）。
            //   旧判据（offset >= effectiveBase 早退）→ staging 整页零新建 → 页内 [64K] 旧数据被零覆盖。
            //   修复后：不早退 → 补集加载 [64K, 64K+1) → 追加后旧数据保留。
            h2.Write(64 * 1024 + 1, new byte[] { 0xCD });   // 追加（同页内，offset 在旧数据之后）
            h2.Flush();
            var buf = new byte[1];
            h2.Read(64 * 1024, buf).Should().Be(1);
            buf[0].Should().Be(0xAB, "页内 [64K] 旧数据尾字节必须保留（修复前 = 零覆盖 = 静默损坏）");
            h2.Read(64 * 1024 + 1, buf).Should().Be(1);
            buf[0].Should().Be(0xCD);
        }
        store.Dispose();
    }

    [Fact]
    public void Core05_ShrinkThenExtend_Multipart_ReadsZero_NotOldData()
    {
        var store = new MemoryObjectStore();
        using (var fs = RemoteFileSystem.OpenOrCreate(store, new RemoteFileSystemOptions { MultipartThreshold = 8 * 1024 * 1024, StagingMemoryLimit = 256L * 1024 * 1024 }))
        using (var h = fs.Open("f", Opts()))
        {
            var seed = new byte[1024];
            for (long off = 0; off < 100L * 1024 * 1024; off += seed.Length)
                h.Write(off, seed);   // 100MB（multipart 路径）
            h.Flush();
            h.SetLength(10L * 1024 * 1024);   // 截断到 10MB
            for (long off = 10L * 1024 * 1024; off < 50L * 1024 * 1024; off += seed.Length)
                h.Write(off, new byte[1024]);   // 扩展写 [10MB, 50MB)（全零数据）
            h.Flush();
            var buf = new byte[1024];
            h.Read(10L * 1024 * 1024, buf).Should().Be(1024);
            buf.Should().OnlyContain(b => b == 0, "CORE-05：截断-扩展区 = 零（旧实现自拷贝复活已丢弃数据）");
        }
        store.Dispose();
    }

    [Fact]
    public async Task Core06_ConcurrentAppendAndFlush_NoLostWrites()
    {
        var store = new MemoryObjectStore();
        using var fs = RemoteFileSystem.OpenOrCreate(store, new RemoteFileSystemOptions { MultipartThreshold = 8 * 1024 * 1024 });
        using var h = fs.Open("f", Opts());
        // CORE-06：Flush 与并发 Append——所有已返回的写必须在最终读回可见（旧实现 Flush 期间
        //   并发写被分类 pass 吞掉 + MarkAllClean 擦脏标 = 永久丢失）
        const int writes = 64;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tasks = new List<Task>();
        for (var i = 0; i < writes; i++)
        {
            var idx = i;
            tasks.Add(Task.Run(() =>
            {
                var chunk = new byte[64 * 1024];
                chunk[0] = (byte)idx;
                h.Append(chunk);
                if (idx % 4 == 0) h.Flush();   // 每 4 写一次 Flush（与写并发）
            }));
        }
        await Task.WhenAll(tasks);
        var finalLen = h.Length;
        var verify = new byte[64 * 1024];
        var totalRead = 0;
        for (long off = 0; off < finalLen; off += verify.Length)
        {
            var n = h.Read(off, verify);
            if (n <= 0) break;
            totalRead += n;
        }
        totalRead.Should().Be((int)finalLen, "CORE-06：全部已返回写必须落盘可见（finalLen=" + finalLen + "）");
        store.Dispose();
    }
}
