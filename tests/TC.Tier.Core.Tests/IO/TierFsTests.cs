using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Disk;
using TC.Tier.Core.IO.Mem;
using TC.Tier.Core.IO.Remote;
using TC.Tier.Core.IO.Testing;
using TC.Tier.Core.IO.Raw;

namespace TC.Tier.Core.Tests.IO;

/// <summary>
/// TierFs 工厂测试（medium-protocol-and-parity-design §2.2/§2.3——P1 骨架）——动词契约
/// （New 已存在抛 / Open 不存在抛）、local 位置落定（CWD 固化/相对/绝对/UNC）、memory quota 映射、
/// virtual New/Open 全参数、二级协议注册表（fake 构建器）、未落地参数 fail-fast（带阶段号——绝不静默忽略）。
/// </summary>
public sealed class TierFsTests
{
    private static string RootSpec(string path) => path.Replace('\\', '/');

    private static string TempDir()
    {
        var dir = TestTempDir.Create("tierfs-tests");   // 修复：此前绕过 TestTempDir——零清理零重定向（C 盘 29G 残留最大漏源）
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ═══════════════ local：动词契约 + 位置落定 ═══════════════

    [Fact]
    public void Local_New_CreatesEmptyRoot_ThenReNewThrows()
    {
        var root = Path.Combine(TempDir(), "space");
        var fs = TierFs.New($"local://{root.Replace('\\', '/')}");
        Assert.True(Directory.Exists(root));
        fs.Dispose();

        // 空根重 New = 幂等成功（mkdir -p 语义）
        using (TierFs.New($"local://{root.Replace('\\', '/')}")) { }

        // 非空后 New 抛 AlreadyExists
        File.WriteAllText(Path.Combine(root, "data.0"), "x");
        var ex = Assert.Throws<FileIOException>(() => TierFs.New($"local://{root.Replace('\\', '/')}"));
        Assert.Equal(IOError.AlreadyExists, ex.Error);
    }

    [Fact]
    public void Local_Open_MissingRootThrows_ExistsSucceeds()
    {
        var root = Path.Combine(TempDir(), "space");
        var spec = $"local://{root.Replace('\\', '/')}";
        var ex = Assert.Throws<FileIOException>(() => TierFs.Open(spec));
        Assert.Equal(IOError.NotFound, ex.Error);

        Directory.CreateDirectory(root);
        using var fs = TierFs.Open(spec);
        fs.EnsureRoot();
        Assert.True(Directory.Exists(root));
    }

    [Fact]
    public void Local_CwdShortcut_And_Relative_PinnedAtConstruction()
    {
        var cwd = Environment.CurrentDirectory;
        try
        {
            var anchor = TempDir();
            Environment.CurrentDirectory = anchor;

            using var byCwd = TierFs.Open("local");
            // New = 创建空镜像（含根）——相对形态在构造瞬间对 CWD 固化
            using var byRel = TierFs.New("local:sub/dir");
            Environment.CurrentDirectory = cwd;
            Assert.True(Directory.Exists(Path.Combine(anchor, "sub", "dir")));
        }
        finally
        {
            Environment.CurrentDirectory = cwd;
        }
    }

    // ═══════════════ memory：quota 映射 ═══════════════

    [Fact]
    public void Memory_Quota_MapsToCapacity_EnforcedAsDiskFull()
    {
        // Sparse 页粒度 64K：quota=64K 容一页——第二个文件（再租一页）即 DiskFull
        using var fs = (MemoryFileSystem)TierFs.New("memory:?quota=64K");
        fs.CreateFile("first.bin", preallocateSize: 512);
        Assert.True(fs.Exists("first.bin"));
        Assert.Throws<FileIOException>(() => fs.CreateFile("second.bin", preallocateSize: 512));
    }

    [Fact]
    public void Memory_NewAndOpen_SameForm()
    {
        using var a = TierFs.New("memory:");
        using var b = TierFs.Open("memory?quota=-1");
        Assert.IsType<MemoryFileSystem>(b);
    }

    // ═══════════════ G2：memory 访问三态 + 包络 ═══════════════

    [Fact]
    public void Memory_AccessRo_RejectsWriteFamily_AndHandleEnvelope()
    {
        using var fs = (MemoryFileSystem)TierFs.New("memory:?access=ro");
        Assert.Throws<FileIOException>(() => fs.CreateFile("a"));
        Assert.Throws<FileIOException>(() => fs.CreateDirectory("d"));
        Assert.Throws<FileIOException>(() => fs.EnsureRoot());
        // 包络：ro 挂载请求 ReadWrite/Write 句柄——构造期拒绝（不构造句柄）
        Assert.Throws<FileIOException>(() =>
            fs.Open("x", new FileOpenOptions { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate }));
        Assert.Throws<FileIOException>(() =>
            fs.Open("x", new FileOpenOptions { Access = AccessMode.Write, Mode = FileOpenMode.OpenOrCreate }));
        // Read 句柄包络放行（不存在 → NotFound 而非 AccessDenied——包络先过）
        var ex = Assert.Throws<FileIOException>(() =>
            fs.Open("x", new FileOpenOptions { Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting }));
        Assert.Equal(IOError.NotFound, ex.Error);
    }

    [Fact]
    public void Memory_AccessWo_RejectsReadFamily_WritePathAllPass()
    {
        using var fs = (MemoryFileSystem)TierFs.New("memory:?access=wo");
        using (var h = fs.Open("ingest.log",
            new FileOpenOptions { Access = AccessMode.Write, Mode = FileOpenMode.OpenOrCreate }))
        {
            h.Append(new byte[10]);   // 写路径全通（纯摄入）
        }
        Assert.Throws<FileIOException>(() => fs.EnumerateFiles("*").ToList());
        Assert.Throws<FileIOException>(() => fs.EnumerateEntries("*").ToList());
        Assert.Throws<FileIOException>(() => fs.Stat("ingest.log"));
        Assert.Throws<FileIOException>(() =>
            fs.Open("ingest.log", new FileOpenOptions { Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting }));
        Assert.Throws<FileIOException>(() =>
            fs.Open("ingest.log", new FileOpenOptions { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenExisting }));
        _ = fs.Exists("ingest.log");   // Exists 豁免——幂等创建的写路径支撑
    }

    [Fact]
    public void Memory_MapEnvelope_ReadMapRejectedOnWoMount()
    {
        // wo 挂载：写句柄（包络内）可建种；Map(Read/ReadWrite) 越包络——构造期拒绝（映射无只写同拒 Write）
        using var fs = MemoryFileSystem.New(new MemoryFileSystemOptions { Access = AccessMode.Write });
        using (var h = fs.Open("m", new FileOpenOptions { Access = AccessMode.Write, Mode = FileOpenMode.OpenOrCreate }))
        {
            h.Write(0, new byte[64]);
        }
        using (var h = fs.Open("m", new FileOpenOptions { Access = AccessMode.Write, Mode = FileOpenMode.OpenExisting }))
        {
            Assert.Throws<FileIOException>(() => h.Map(0, 32, AccessMode.Read));
            Assert.Throws<FileIOException>(() => h.Map(0, 32, AccessMode.ReadWrite));
            Assert.Throws<FileIOException>(() => h.Map(0, 32, AccessMode.Write));   // 映射无只写
        }
    }

    // ═══════════════ virtual：New/Open 全参数 ═══════════════

    [Fact]
    public void Virtual_NewWithQuota_Write_Close_ThenOpenReadOnly()
    {
        var vol = Path.Combine(TempDir(), "v.raw");
        var spec = vol.Replace('\\', '/');

        using (var fs = (RawFileSystem)TierFs.New($"virtual:///{spec}?quota=4M&label=wal-a"))
        {
            fs.EnsureRoot();
            fs.CreateDirectory("seg");
            fs.CreateFile("seg/data.0");
            using var h = fs.Open("seg/data.0", new FileOpenOptions { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate });
            h.Write(0, new byte[100]);
        }   // Dispose = 关卷提交

        using (var ro = (RawFileSystem)TierFs.Open($"virtual:///{spec}?access=ro"))
        {
            Assert.True(ro.Exists("seg/data.0"));
            // 只读：写路径拒绝
            Assert.ThrowsAny<Exception>(() => ro.CreateFile("seg/data.1"));
        }
    }

    [Fact]
    public void Virtual_NewWithoutQuota_AutoExpandVolume()
    {
        var vol = Path.Combine(TempDir(), "grow.raw");
        var spec = vol.Replace('\\', '/');
        using (var fs = (RawFileSystem)TierFs.New($"virtual:///{spec}"))
        {
            Assert.Equal(64L << 20, fs.Volume.TotalSpace);   // 初始小界（-1 自动扩容已落地）
            Assert.Equal(-1, fs.Volume.QuotaBytes);
            fs.CreateFile("grow", preallocateSize: 70L << 20);
            // 70M 单 extent 需 ≥70M 连续 run——128M 卷新 run ~63.9M，二次倍增至 256M（连续分配边界）
            Assert.Equal(256L << 20, fs.Volume.TotalSpace);
        }
        // 多载体 New（member=）仍须显式 quota——自动扩容仅限单文件载体
        var ex = Assert.Throws<NotSupportedException>(
            () => TierFs.New($"virtual:///{vol.Replace('\\', '/')}?member=/x/v2.raw"));
        Assert.Contains("quota", ex.Message);
    }

    [Fact]
    public void Virtual_OpenQuotaTightens_And_LabelChecks()
    {
        var vol = Path.Combine(TempDir(), "v2.raw");
        var spec = vol.Replace('\\', '/');
        using (TierFs.New($"virtual:///{spec}?quota=32M&label=vol-a")) { }

        // Open 收紧（min 规则）：quota=10M 挂载（含 8M 日志预留计入空间根上限）——小文件通，大文件 DiskFull
        using (var fs = (RawFileSystem)TierFs.Open($"virtual:///{spec}?quota=10M"))
        {
            fs.EnsureRoot();
            fs.CreateFile("small", preallocateSize: 512 << 10);
            Assert.Throws<FileIOException>(() => fs.CreateFile("big", preallocateSize: 12 << 20));
        }

        // Open label 校验：不符即抛（fail-fast——挂错卷的配置错误）
        var ex = Assert.Throws<FileIOException>(() => TierFs.Open($"virtual:///{spec}?label=wrong"));
        Assert.Equal(IOError.NotFound, ex.Error);
        using var ok = TierFs.Open($"virtual:///{spec}?label=vol-a");   // 相符通过
    }

    [Fact]
    public void Virtual_NewWithAccessRo_SealedOnCreation()
    {
        var vol = Path.Combine(TempDir(), "sealed.raw");
        var spec = vol.Replace('\\', '/');
        using var fs = (RawFileSystem)TierFs.New($"virtual:///{spec}?quota=4M&access=ro");
        Assert.ThrowsAny<Exception>(() => fs.CreateFile("x"));   // 建完即封存
    }

    [Fact]
    public void FactoryOpenOrCreate_BindAny_AllMedia()
    {
        // local：全新建 / 既有非空开（机械 New 替换会炸的形态）
        var root = Path.Combine(TempDir(), "oc").Replace(System.IO.Path.DirectorySeparatorChar, '/');
        using (var fresh = TierFs.OpenOrCreate($"local:///{root}"))
        {
            fresh.CreateFile("seed");
        }
        using (var again = TierFs.OpenOrCreate($"local:///{root}"))
        {
            Assert.True(again.Exists("seed"));
        }

        // virtual：未格式化 → 建（自动扩容卷）；已格式化 → 开
        var vol = Path.Combine(TempDir(), "oc.raw").Replace(System.IO.Path.DirectorySeparatorChar, '/');
        using (var vFresh = (RawFileSystem)TierFs.OpenOrCreate($"virtual:///{vol}"))
        {
            Assert.Equal(64L << 20, vFresh.Volume.TotalSpace);   // 懒初始化建卷（New 路径）
        }
        using (var vAgain = (RawFileSystem)TierFs.OpenOrCreate($"virtual:///{vol}"))
        {
            Assert.True(vAgain.Volume.TotalSpace >= 64L << 20, "已格式化 → Open（数据面在档）");
        }

        // 生成重载：OpenOrCreate(spec, TOptions)（[MediumOptions] Verbs 派生）
        using var typed = TierFs.OpenOrCreate("local:///" + root, new DiskFileSystemOptions());   // 无 label——既有根的 label 断言语义之外的安全形态
    }

    // ═══════════════ G2：disk 访问三态 + 包络 ═══════════════

    [Fact]
    public void Local_AccessRo_RejectsWriteFamily_AndHandleEnvelope()
    {
        var root = Path.Combine(TempDir(), "ro");
        Directory.CreateDirectory(root);
        using var ro = TierFs.Open($"local://{RootSpec(root)}?access=ro");
        Assert.Throws<FileIOException>(() => ro.CreateFile("a"));
        Assert.Throws<FileIOException>(() => ro.CreateDirectory("d"));
        Assert.Throws<FileIOException>(() => ro.EnsureRoot());
        Assert.Throws<FileIOException>(() =>
            ro.Open("x", new FileOpenOptions { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenExisting }));
        var ex = Assert.Throws<FileIOException>(() =>
            ro.Open("x", new FileOpenOptions { Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting }));
        Assert.Equal(IOError.NotFound, ex.Error);   // 包络先过——剩下的是 NotFound 而非 AccessDenied
    }

    [Fact]
    public void Local_AccessWo_RejectsReadFamily_WritePathAllPass()
    {
        var root = Path.Combine(TempDir(), "wo");
        using var fs = TierFs.New($"local://{RootSpec(root)}?access=wo");
        using (var h = fs.Open("ingest.log",
            new FileOpenOptions { Access = AccessMode.Write, Mode = FileOpenMode.OpenOrCreate }))
        {
            h.Append(new byte[10]);
        }
        Assert.Throws<FileIOException>(() => fs.EnumerateFiles("*").ToList());
        Assert.Throws<FileIOException>(() => fs.Stat("ingest.log"));
        Assert.Throws<FileIOException>(() =>
            fs.Open("ingest.log", new FileOpenOptions { Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting }));
        _ = fs.Exists("ingest.log");   // Exists 豁免
    }

    [Fact]
    public void Local_Label_MarkerRoundtrip()
    {
        var root = Path.Combine(TempDir(), "lv");
        var spec = RootSpec(root);
        using (TierFs.New($"local://{spec}?label=vol-d")) { }
        Assert.Equal("vol-d", System.IO.File.ReadAllText(Path.Combine(root, ".tier-volume")));   // New = 写标记

        using var ok = TierFs.Open($"local://{spec}?label=vol-d");   // 相符通过
        var ex = Assert.Throws<FileIOException>(() => TierFs.Open($"local://{spec}?label=wrong"));
        Assert.Equal(IOError.NotFound, ex.Error);
    }

    [Fact]
    public void Local_Quota_LazyBaseline_WriteProjection()
    {
        var root = Path.Combine(TempDir(), "lq");
        var spec = RootSpec(root);
        using (var fs = TierFs.New($"local://{spec}"))
        using (var h = fs.Open("seed",
            new FileOpenOptions { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate }))
        {
            h.Write(0, new byte[8 << 10]);   // 基线 8K（无配额零成本）
        }
        using var capped = TierFs.Open($"local://{spec}?quota=16K");
        using (var h = capped.Open("more",
            new FileOpenOptions { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate }))
        {
            h.Write(0, new byte[4 << 10]);   // 8K + 4K = 12K ≤ 16K ✓
            Assert.Throws<FileIOException>(() => h.Write(0, new byte[32 << 10]));   // 超限写前拒
        }
    }

    // ═══════════════ 未落地参数：绝不静默忽略 ═══════════════

    [Fact]
    public void Exclusive_Disk_MountHoldsLock_SecondMountThrows_ReleasedOnDispose()
    {
        var root = Path.Combine(TempDir(), "ex");
        var spec = RootSpec(root);
        using var holder = TierFs.New($"local://{spec}?exclusive=1");
        var ex = Assert.Throws<FileIOException>(() => TierFs.Open($"local://{spec}?exclusive=1"));
        Assert.Equal(IOError.SharingViolation, ex.Error);   // 挂载期持有——第二实例 30s 超时 SharingViolation
        holder.Dispose();
        using var next = TierFs.Open($"local://{spec}?exclusive=1");   // 释放后可再获取
    }

    [Fact]
    public void Exclusive_Memory_RwGateRejectsSecondAcquire()
    {
        using var fs = (MemoryFileSystem)TierFs.New("memory:?exclusive=1");
        // 构造期已持有——同卷再取超时；fs 语义（卷锁）与挂载持有一致
        Assert.Throws<FileIOException>(() => fs.AcquireExclusive(TimeSpan.FromMilliseconds(100)));
        fs.Dispose();
        using var fs2 = (MemoryFileSystem)TierFs.New("memory:?exclusive=1");
        Assert.Throws<FileIOException>(() => fs2.AcquireExclusive(TimeSpan.FromMilliseconds(100)));   // 新卷构造期已持有——再取同拒；释放随 Dispose
    }

    // ═══════════════ G2/G3/G1：remote（fake 协议直连 MemoryObjectStore 底座）═══════════════

    private sealed class FakeRemoteBuilder(IObjectStore? store = null) : ITierProtocolBuilder
    {
        // 共享底座：同一协议多次 New/Open 看到同一对象空间（quota 基线/label 标记跨挂载可見）
        public readonly IObjectStore Store = store ?? new MemoryObjectStore();
        public RemoteFileSystem Fs = null!;

        public IFileSystem Build(TierSpec spec, FileSystemOptions? options, TierFsVerb verb, ILogger? logger)
        {
            Fs = RemoteFileSystem.OpenOrCreate(Store, new RemoteFileSystemOptions
            {
                KeyPrefix = spec.KeyPrefix ?? "",
                Access = spec.Access,
                Label = verb == TierFsVerb.New ? spec.Label : null,
                QuotaBytes = spec.QuotaBytes,
            });
            // G1 Open 校验（与 S3ProtocolBuilder 同款语义——fake 侧对齐真实协议行为）
            if (verb == TierFsVerb.Open && spec.Label is not null && Fs.ReadLabelMarker() != spec.Label)
                throw new FileIOException(IOError.NotFound,
                    $"label 校验不符：期望 '{spec.Label}'（spec label 在 Open = 断言）", null, "open-label-check");
            return Fs;
        }
    }

    private static IFileSystem OpenFake(string spec)
    {
        var b = new FakeRemoteBuilder();
        TierFs.RegisterProtocol("fake2", b);
        return TierFs.Open(spec);
    }

    [Fact]
    public void Remote_AccessRo_RejectsWrites_Envelope()
    {
        using var fs = OpenFake("network:///fake2/h/b/p?access=ro");
        Assert.Throws<FileIOException>(() => fs.CreateFile("a"));
        Assert.Throws<FileIOException>(() => fs.CreateDirectory("d"));
        Assert.Throws<FileIOException>(() =>
            fs.Open("x", new FileOpenOptions { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate }));
        var ex = Assert.Throws<FileIOException>(() =>
            fs.Open("x", new FileOpenOptions { Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting }));
        Assert.Equal(IOError.NotFound, ex.Error);
    }

    [Fact]
    public void Remote_AccessWo_RejectsReads_WritePath()
    {
        using var fs = OpenFake("network:///fake2/h/b/p?access=wo");
        using (var h = fs.Open("ingest",
            new FileOpenOptions { Access = AccessMode.Write, Mode = FileOpenMode.OpenOrCreate }))
        {
            h.Append(new byte[10]);
            h.Flush();
        }
        Assert.Throws<FileIOException>(() => fs.EnumerateFiles("*").ToList());
        Assert.Throws<FileIOException>(() => fs.Stat("ingest"));
        Assert.Throws<FileIOException>(() =>
            fs.Open("ingest", new FileOpenOptions { Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting }));
        _ = fs.Exists("ingest");
    }

    [Fact]
    public void Remote_Quota_LazyBaseline_WriteProjection()
    {
        // 共享底座：先写 8K 基线（quota 未设零成本），再以 quota=16K 打开——基线 + 投影超限 DiskFull
        var builder = new FakeRemoteBuilder();
        TierFs.RegisterProtocol("fake3", builder);
        using (var fs = TierFs.Open("network:///fake3/h/b/p"))
        using (var h = fs.Open("seed",
            new FileOpenOptions { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate }))
        {
            h.Write(0, new byte[8 << 10]);
            h.Flush();
        }
        using var capped = TierFs.Open("network:///fake3/h/b/p?quota=16K");
        using (var h = capped.Open("more",
            new FileOpenOptions { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate }))
        {
            h.Write(0, new byte[4 << 10]);   // 惰性基线 8K + 4K = 12K ≤ 16K ✓
            Assert.Throws<FileIOException>(() => h.Write(0, new byte[32 << 10]));   // 12K + 32K > 16K ✗
        }
    }

    [Fact]
    public void Remote_Label_MarkerRoundtrip()
    {
        var b = new FakeRemoteBuilder();
        TierFs.RegisterProtocol("fake4", b);
        using (TierFs.New("network:///fake4/h/b/p?label=net-a")) { }
        Assert.Equal("net-a", b.Fs.ReadLabelMarker());   // New = 写标记对象

        using var ok = TierFs.Open("network:///fake4/h/b/p?label=net-a");   // 相符通过
        var ex = Assert.Throws<FileIOException>(() => TierFs.Open("network:///fake4/h/b/p?label=wrong"));
        Assert.Equal(IOError.NotFound, ex.Error);   // 不符即抛（Open = 断言）
    }

    // ═══════════════ G4：VolumeInfo 完整自描述（是什么/怎么挂的/什么状态）═══════════════

    [Fact]
    public void VolumeInfo_SelfDescription_AcrossNatures()
    {
        using var mem = TierFs.New("memory:?quota=64K&label=m1&access=ro");
        var mv = mem.Volume;
        Assert.Equal(StorageNature.Memory, mv.Nature);
        Assert.Equal("m1", mv.Label);
        Assert.Equal(AccessMode.Read, mv.Access);
        Assert.Equal(64 << 10, mv.QuotaBytes);
        Assert.True(mv.UsedBytes >= 0);

        var vol = Path.Combine(TempDir(), "g4.raw");
        using var raw = TierFs.New($"virtual:///{RootSpec(vol)}?quota=8M&label=v1");
        var rv = raw.Volume;
        Assert.Equal(StorageNature.Virtual, rv.Nature);
        Assert.Equal("v1", rv.Label);
        Assert.True(rv.UsedBytes > 0);   // 位图推导精确（含日志/元数据预留）
        Assert.Equal(rv.TotalSpace, rv.UsedBytes + rv.FreeSpace);

        var root = Path.Combine(TempDir(), "g4d");
        using var disk = TierFs.New($"local://{RootSpec(root)}?label=d1");
        var dv = disk.Volume;
        Assert.Equal(StorageNature.Local, dv.Nature);
        Assert.Equal("d1", dv.Label);
        Assert.Equal(-1, dv.QuotaBytes);   // 未设 = -1（与 spec 同名往返）
    }

    [Fact]
    public void VolumeInfo_Remote_NatureAndLabel()
    {
        var b = new FakeRemoteBuilder();
        TierFs.RegisterProtocol("fake5", b);
        using var fs = TierFs.New("network:///fake5/h/b/p?label=net-g4");   // New = 写标记对象
        var v = fs.Volume;
        Assert.Equal(StorageNature.Network, v.Nature);
        Assert.Equal("net-g4", v.Label);   // 构造期已知（options 路径零 GET）
        Assert.Null(v.SubKind);            // fake 构建器未声明协议身份——诚实 null
    }

    // ═══════════════ 工厂 × options 合流：spec 定身份 + options 补调优 ═══════════════

    [Fact]
    public void FactoryWithOptions_TuningReachable_MountMerged()
    {
        var root = Path.Combine(TempDir(), "merge");
        var spec = RootSpec(root);
        // spec 带挂载（quota=1M）+ options 带调优（PageSize=8K）与缺省位（access 未在 spec → options 的 ro 生效）
        using var fs = (MemoryFileSystem)TierFs.New("memory:?quota=1M",
            new MemoryFileSystemOptions { Access = AccessMode.Read, PageSize = 8 << 10 });
        var v = fs.Volume;
        Assert.Equal(1 << 20, v.QuotaBytes);          // spec 显式胜出
        Assert.Equal(AccessMode.Read, v.Access);       // spec 未写 → options 值
        Assert.Equal(8 << 10, fs.Volume.AllocationUnit);   // 调优可达（PageSize = mem AllocationUnit）

        // 调优 + 动词契约 + 挂载合流（disk）：置内容后同根再 New → AlreadyExists（空根再 New = 幂等成功）
        using var disk = TierFs.New($"local://{spec}",
            new DiskFileSystemOptions { MetadataMode = DiskMetadataMode.Sidecar });
        disk.CreateFile("seed");
        Assert.Throws<FileIOException>(() => TierFs.New($"local://{spec}"));
    }

    [Fact]
    public void FactoryWithOptions_Precedence_SpecExplicitWins()
    {
        // spec access=ro + options Access=ReadWrite → spec 胜（字符串必须可信——审计可读出真实形态）
        using var fs = (MemoryFileSystem)TierFs.New("memory:?access=ro",
            new MemoryFileSystemOptions { Access = AccessMode.ReadWrite, Label = "opt-label" });
        Assert.Equal(AccessMode.Read, fs.Volume.Access);
        Assert.Equal("opt-label", fs.Volume.Label);   // spec 未写 label → options 值
    }

    [Fact]
    public void FactoryWithOptions_TypeMismatch_FailFast()
    {
        // 类型化重载（P-a）先行拦截：『该重载期望 X，字符串是 Y』——比基类 Expect 文案更前置
        var ex = Assert.Throws<ArgumentException>(() =>
            TierFs.New("memory:", new DiskFileSystemOptions()));   // memory 配了 disk 的 options
        Assert.Contains("memory", ex.Message);
        Assert.Contains("local", ex.Message);
        Assert.Contains("DiskFileSystemOptions", ex.Message);
    }

    // ═══════════════ network：二级协议注册表（开放轴）═══════════════

    private sealed class FakeProtocolBuilder : ITierProtocolBuilder
    {
        public IFileSystem Build(TierSpec spec, FileSystemOptions? options, TierFsVerb verb, ILogger? logger)
            => MemoryFileSystem.New();
    }

    [Fact]
    public void Network_ProtocolRegistry_OpenAxis()
    {
        TierFs.RegisterProtocol("fake", new FakeProtocolBuilder());
        using var fs = TierFs.Open("network:///fake/host/bucket/prefix");
        Assert.IsType<MemoryFileSystem>(fs);

        // 未注册协议 → Unsupported + 已注册清单提示
        var ex = Assert.Throws<FileIOException>(() => TierFs.Open("network:///nosuch/h/b/p"));
        Assert.Equal(IOError.Unsupported, ex.Error);
        Assert.Contains("fake", ex.Message);
    }

    // ═══════════════ TierSpec 补充：tls 参数 ═══════════════

    [Fact]
    public void Tls_DefaultTrue_CanDisable_NetworkOnly()
    {
        Assert.True(TierSpec.Parse("network:///s3/h/b/p").Tls);
        Assert.False(TierSpec.Parse("network:///s3/h/b/p?tls=0").Tls);
        Assert.True(TierSpec.Parse("network:///s3/h/b/p?tls=1").Tls);
        Assert.Throws<FormatException>(() => TierSpec.Parse("local:///x?tls=0"));
        Assert.Throws<FormatException>(() => TierSpec.Parse("network:///s3/h/b/p?tls=0&tls=1"));
    }
}
