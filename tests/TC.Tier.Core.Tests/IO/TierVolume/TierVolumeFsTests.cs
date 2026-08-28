using System.IO.Hashing;
using TC.Tier.Core.IO;
using TC.Tier.Core.IO.TierVolume;

namespace TC.Tier.Core.Tests.IO.TierVolume;

/// <summary>
/// TierVolumeFs 契约测试（raw-medium-and-conversion-design §2/§3/§4）——
/// 格式与打开 / 一卷一实例 / 双 superblock / 未知保留值拒开 / 断电恢复（dirty → 可写继续）/
/// 命名空间 CRUD / 能力位 / 卷几何精确性。
/// </summary>
public sealed class TierVolumeFsTests : IDisposable
{
    private readonly string _dir = TestTempDir.Create("core-io-tv");
    private readonly List<TierVolumeFs> _openFs = [];

    private string NewVolumePath() => Path.Combine(_dir, $"vol-{Guid.NewGuid():N}.tier");

    private TierVolumeFs Format(long capacity = 16L << 20, TierVolumeFormatOptions? options = null)
    {
        var fs = TierVolumeFs.New(TierVolumeCarrier.File(NewVolumePath()),
            options ?? new TierVolumeFormatOptions { QuotaBytes = capacity });
        _openFs.Add(fs);
        return fs;
    }

    private TierVolumeFs Reopen(string path, TierVolumeOpenOptions? options = null)
    {
        var fs = TierVolumeFs.Open(TierVolumeCarrier.File(path), options);
        _openFs.Add(fs);
        return fs;
    }

    public void Dispose()
    {
        foreach (var fs in _openFs) fs.Dispose();
        TestTempDir.TryCleanup(_dir);
    }

    private static FileOpenOptions RWOpts() => new()
    { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate, Sharing = FileSharing.ReadWrite };

    private static FileOpenOptions ROpts() => new()
    { Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting, Sharing = FileSharing.ReadWrite };

    // ═══════════════ 格式与打开 ═══════════════

    [Fact]
    public void Format_ThenOpen_Succeeds()
    {
        var path = NewVolumePath();
        using (var fs = TierVolumeFs.New(TierVolumeCarrier.File(path),
                   new TierVolumeFormatOptions { QuotaBytes = 8 << 20, Label = "test-vol" }))
        {
            fs.VolumeUuid.Should().NotBe(Guid.Empty);
            fs.Volume.TotalSpace.Should().Be(8 << 20, "TotalSpace 精确（§3.5 增强行）");
        }
        using var reopened = TierVolumeFs.Open(TierVolumeCarrier.File(path));
        reopened.VolumeUuid.Should().NotBe(Guid.Empty, "UUID 持久（superblock）");
        reopened.Volume.TotalSpace.Should().Be(8 << 20);
    }

    [Fact]
    public void Format_OnFormattedCarrier_ThrowsAlreadyExists()
    {
        var path = NewVolumePath();
        using (var fs = TierVolumeFs.New(TierVolumeCarrier.File(path), new TierVolumeFormatOptions { QuotaBytes = 8 << 20 })) { }
        var act = () => TierVolumeFs.New(TierVolumeCarrier.File(path), new TierVolumeFormatOptions { QuotaBytes = 8 << 20 });
        act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.AlreadyExists,
            "格式化显式语义（对齐 CreateFile——幂等由调用方组合）");
    }

    [Fact]
    public void Open_UnformattedCarrier_Throws()
    {
        var path = NewVolumePath();
        File.WriteAllText(path, "not a raw volume");
        var act = () => TierVolumeFs.Open(TierVolumeCarrier.File(path));
        act.Should().Throw<FileIOException>("magic 不符拒开");
    }

    [Fact]
    public void OneVolumeOneInstance_SecondOpen_Rejected()
    {
        var path = NewVolumePath();
        using var fs = TierVolumeFs.New(TierVolumeCarrier.File(path), new TierVolumeFormatOptions { QuotaBytes = 8 << 20 });
        var act = () => TierVolumeFs.Open(TierVolumeCarrier.File(path));
        act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.SharingViolation,
            "一卷一实例（§2.4）——进程内登记立即拒绝");
    }

    [Fact]
    public void Disposed_Instance_CanBeReopened()
    {
        var path = NewVolumePath();
        using (var fs = TierVolumeFs.New(TierVolumeCarrier.File(path), new TierVolumeFormatOptions { QuotaBytes = 8 << 20 }))
        {
            using (var h = fs.Open("f", RWOpts())) h.Write(0, new byte[100]);
        }
        using var reopened = TierVolumeFs.Open(TierVolumeCarrier.File(path));
        reopened.Exists("f").Should().BeTrue("Dispose 登记 unregister——重开可见持久内容");
    }

    // ═══════════════ 前向兼容双门（§3.9）═══════════════

    [Fact]
    public void UnknownSuperblockFlags_Rejected()
    {
        var path = NewVolumePath();
        using (TierVolumeFs.New(TierVolumeCarrier.File(path), new TierVolumeFormatOptions { QuotaBytes = 8 << 20 })) { }
        // 模拟 v2+ 卷：未知 flags + 重算的合法 CRC（单纯篡改会先被 CRC 拦——那是另一条正确防线）
        // 0x0020 = 未认领位（0x0002 Journaled / 0x0004 MultiCarrier / 0x0008 AutoExpand 已认领——RM-03/RM-04/§5.3；
        // 0x0010 Snapshots 已认领——V2 §1.1）
        foreach (var sbOffset in new[] { 0L, 4096L })
        {
            var sb = new byte[4096];
            using (var raw = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                RandomAccess.Read(raw, sb, sbOffset);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(sb.AsSpan(6), 0x0020);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(4088), Crc32.HashToUInt32(sb.AsSpan(0, 4088)));
            using (var raw = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                RandomAccess.Write(raw, sb, sbOffset);
        }
        var act = () => TierVolumeFs.Open(TierVolumeCarrier.File(path));
        act.Should().Throw<FileIOException>().WithMessage("*未知 flags*",
            "未知保留值拒开——绝不静默忽略（§3.9 前向兼容双门；模拟 v2+ 卷对 v1 的前向拒开）");
    }

    [Fact]
    public void PrimarySuperblockCorrupt_BackupAdopted()
    {
        var path = NewVolumePath();
        using (var fs = TierVolumeFs.New(TierVolumeCarrier.File(path), new TierVolumeFormatOptions { QuotaBytes = 8 << 20 }))
        using (var h = fs.Open("f", RWOpts()))
            h.Write(0, new byte[100]);
        // 破坏主侧 CRC（翻转尾部数据字节——CRC 必失配）
        using (var raw = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            RandomAccess.Write(raw, new byte[] { 0xDE, 0xAD }, 4000);
        using var reopened = TierVolumeFs.Open(TierVolumeCarrier.File(path));
        reopened.Exists("f").Should().BeTrue("备份侧采纳（单侧损坏可恢复，§4.1）");
        using var h2 = reopened.Open("f", ROpts());
        h2.Length.Should().Be(100);
    }

    // ═══════════════ 断电恢复底线（§4.1）═══════════════

    [Fact]
    public void CrashRecovery_DirtyReopen_WritableContinues()
    {
        var path = NewVolumePath();
        // 模拟崩溃：Format 后未 Dispose（直接放弃——绕过 clean 关闭协议）
        var fs1 = TierVolumeFs.New(TierVolumeCarrier.File(path), new TierVolumeFormatOptions { QuotaBytes = 16 << 20 });
        using (var h = fs1.Open("committed", RWOpts()))
        {
            h.Write(0, new byte[500]);
            h.Flush();   // 提交（数据+元数据+翻转）
        }
        using (var h = fs1.Open("pending", RWOpts()))
        {
            h.Write(0, new byte[300]);
            // 不 Flush 不 Dispose → 崩溃语义（pending 丢失窗口 = fsync 语义）
        }
        fs1.CrashSimulate();   // 测试后门：不执行 clean 关闭，仅释放资源与登记

        using var fs2 = TierVolumeFs.Open(TierVolumeCarrier.File(path));
        fs2.Exists("committed").Should().BeTrue("已提交内容完整");
        fs2.Exists("pending").Should().BeFalse("未提交写丢失（fsync 语义窗口）");
        var act = () =>
        {
            using var h = fs2.Open("after-recovery", RWOpts());
            h.Write(0, new byte[64]);
        };
        act.Should().NotThrow("dirty 恢复后立即可写继续（§4.1——断电恢复底线）");
    }

    // ═══════════════ 命名空间 ═══════════════

    [Fact]
    public void Namespace_CRud_AndHierarchy()
    {
        using var fs = Format();
        fs.EnsureRoot();
        fs.CreateDirectory("a");
        fs.CreateDirectory("a/b");
        fs.DirectoryExists("a").Should().BeTrue();
        fs.DirectoryExists("a/b").Should().BeTrue();
        fs.DirectoryExists("nope").Should().BeFalse();

        fs.CreateFile("a/b/f1");
        fs.Exists("a/b/f1").Should().BeTrue();
        fs.Stat("a/b/f1").Type.Should().Be(FsEntryType.File);

        fs.Move("a/b/f1", "a/f2");
        fs.Exists("a/b/f1").Should().BeFalse();
        fs.Exists("a/f2").Should().BeTrue();

        fs.MoveDirectory("a", "c");
        fs.Exists("c/f2").Should().BeTrue("目录整体 re-key（元数据事务）");
        fs.DirectoryExists("c/b").Should().BeTrue();

        fs.Delete("c/f2");
        fs.Exists("c/f2").Should().BeFalse();
        fs.DeleteDirectory("c/b");
        fs.DirectoryExists("c/b").Should().BeFalse();
        fs.DeleteDirectory("c");

        var entries = fs.EnumerateEntries(recursive: true).ToList();
        entries.Should().BeEmpty();
    }

    [Fact]
    public void DeleteDirectory_NonEmpty_Rejected()
    {
        using var fs = Format();
        fs.CreateDirectory("d");
        fs.CreateFile("d/f");
        var act = () => fs.DeleteDirectory("d");
        act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.DirectoryNotEmpty);
    }

    [Fact]
    public void CreateFile_ParentMissing_Rejected()
    {
        using var fs = Format();
        var act = () => fs.CreateFile("no/such/file");
        act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.NotFound, "对齐 disk ENOENT 语义");
    }

    [Fact]
    public void Persistence_AcrossDisposeReopen()
    {
        var path = NewVolumePath();
        using (var fs = TierVolumeFs.New(TierVolumeCarrier.File(path), new TierVolumeFormatOptions { QuotaBytes = 16 << 20 }))
        {
            fs.CreateDirectory("d1");
            fs.CreateFile("d1/f", extra: new byte[] { 1, 2, 3 });
            using var h = fs.Open("d1/f", RWOpts());
            h.Write(0, new byte[1000]);
            h.Flush();
        }
        using var reopened = TierVolumeFs.Open(TierVolumeCarrier.File(path));
        reopened.Stat("d1/f").FileExtra.ToArray().Should().BeEquivalentTo(new byte[] { 1, 2, 3 },
            "FileExtra 内联随元数据持久");
        using var h2 = reopened.Open("d1/f", ROpts());
        h2.Length.Should().Be(1000);
        var buf = new byte[1000];
        h2.Read(0, buf).Should().Be(1000);
    }

    // ═══════════════ 能力位与几何 ═══════════════

    [Fact]
    public void CapabilityMatrix_v1_Declared()
    {
        using var fs = Format();
        var caps = fs.Capabilities;
        // §3.5 v1 实达位
        foreach (var bit in new[]
                 {
                     FileSystemCapabilities.Sparse,
                     FileSystemCapabilities.RangeLock,
                     FileSystemCapabilities.RandomWrite,
                     FileSystemCapabilities.EmptyDirectories,
                     FileSystemCapabilities.DurableRename,
                     FileSystemCapabilities.AtomicDirectoryMove,
                     FileSystemCapabilities.ExclusiveLock,
                     FileSystemCapabilities.MaintenanceGate,
                     FileSystemCapabilities.ContiguousCapture,
                     FileSystemCapabilities.CopyRange,
                     FileSystemCapabilities.VectorIO,
                     FileSystemCapabilities.RangeShift,
                 })
            caps.HasFlag(bit).Should().BeTrue($"v1 实达位：{bit}");
        // P3 补齐位（两档模型 + 预取 + MMF）
         foreach (var bit in new[]
                  {
                      FileSystemCapabilities.DirectIO,
                      FileSystemCapabilities.Advise,
                      FileSystemCapabilities.Mmap,
                      FileSystemCapabilities.WriteThrough,   // RM-07 接线（逐写日志提交）
                      FileSystemCapabilities.FlushDataOnly,  // RM-09 接线（FlushData ≠ Flush 真可区分）
                  })
             caps.HasFlag(bit).Should().BeTrue($"接线位：{bit}");
    }

    [Fact]
    public void VolumeGeometry_FreeSpacePrecise()
    {
        using var fs = Format(capacity: 16 << 20);
        var before = fs.Volume.FreeSpace;
        using (var h = fs.Open("f", RWOpts()))
        {
            h.Write(0, new byte[8192]);   // 2 块
            h.Flush();
        }
        var after = fs.Volume.FreeSpace;
        (before - after).Should().BeGreaterThanOrEqualTo(8192,
            "FreeSpace 精确扣减（数据块 + 元数据镜像块，§3.5 增强行——含提交开销所以 ≥）");
        after.Should().BeGreaterThan(0);
        fs.Volume.SectorSize.Should().Be(4096);
        fs.Volume.AllocationUnit.Should().Be(4096);
    }

    [Fact]
    public void ReadOnlyOpen_WritesRejected()
    {
        var path = NewVolumePath();
        using (var fs = TierVolumeFs.New(TierVolumeCarrier.File(path), new TierVolumeFormatOptions { QuotaBytes = 8 << 20 }))
        using (var h = fs.Open("f", RWOpts()))
            h.Write(0, new byte[64]);
        using var ro = TierVolumeFs.Open(TierVolumeCarrier.File(path), new TierVolumeOpenOptions { Access = AccessMode.Read });
        var act = () => ro.Open("g", RWOpts());
        act.Should().Throw<FileIOException>().WithMessage("*只读*");
        ro.Exists("f").Should().BeTrue("只读卷读路径正常");
    }

    [Fact]
    public void MaintenanceGate_Integrated()
    {
        using var fs = Format();
        using var h = fs.Open("f", RWOpts());
        h.Write(0, new byte[8]);   // 门闩外写基线（读放行断言需要内容）
        using var lease = fs.EnterMaintenance("test", MaintenanceScope.WriteOperations);
        var act = () => h.Write(0, new byte[4]);
        act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.UnderMaintenance);
        h.Read(0, new byte[4]).Should().Be(4, "Write 档读放行");
    }

    [Fact]
    public void SortedKeyIndex_StaysConsistent_UnderMixedMutations()
    {
        using var fs = Format(capacity: 64 << 20);
        // 三层 × 每层 300 文件 = 900 条目（索引规模足够暴露漂移）
        for (var d = 0; d < 3; d++)
        {
            fs.CreateDirectory($"d{d}/sub");
            for (var i = 0; i < 300; i++)
                using (var h = fs.Open($"d{d}/f{i}", RWOpts()))
                    h.Write(0, new byte[16]);
        }
        // 混合变异：删 1/3、移 1/3、新建
        for (var d = 0; d < 3; d++)
        {
            for (var i = 0; i < 300; i++)
            {
                if (i % 3 == 0) fs.Delete($"d{d}/f{i}");
                else if (i % 3 == 1) fs.Move($"d{d}/f{i}", $"d{d}/sub/g{i}");
            }
            for (var i = 0; i < 50; i++)
                using (var h = fs.Open($"d{d}/n{i}", RWOpts()))
                    h.Write(0, new byte[16]);
        }
        fs.MoveDirectory("d1", "d1moved");
        // 枚举真相比对：递归全集 = Exists 逐路径核验（索引视图不得多不得少）
        var listed = fs.EnumerateFiles(recursive: true).Select(e => e.Name).ToHashSet();   // 递归枚举 Name = 相对根路径（仅文件）
        var expected = new HashSet<string>();
        foreach (var d in new[] { "d0", "d1moved", "d2" })
        {
            for (var i = 0; i < 300; i++)
            {
                if (i % 3 == 0) continue;
                var dir = i % 3 == 1 ? $"{d}/sub" : d;   // i%3==1 全部移入各自 sub（MoveDirectory 再整体前缀改写）
                var name = i % 3 == 1 ? $"g{i}" : $"f{i}";
                var rel = $"{dir}/{name}";
                if (fs.Exists(rel)) expected.Add(rel);
            }
            for (var i = 0; i < 50; i++)
                if (fs.Exists($"{d}/n{i}")) expected.Add($"{d}/n{i}");
        }
        listed.Count.Should().Be(expected.Count, "索引视图与 Exists 真相一致（RM-11 无多无少）");
        listed.Should().BeEquivalentTo(expected);
        // 目录存在性判定（前缀视图路径）
        fs.DirectoryExists("d1moved/sub").Should().BeTrue();
        fs.DirectoryExists("d1").Should().BeFalse("迁走后旧前缀无条目");
    }
}