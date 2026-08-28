using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Image;
using TC.Tier.Core.IO.Mem;
using TC.Tier.Core.IO.TierVolume;

namespace TC.Tier.Core.Tests.IO.TierVolume;

/// <summary>
/// 卷级快照（V2 §1.1——快照 = 冻结检查点）契约测试族：
/// 捕获一致性 / 冻结钉块 / 只读挂载 / 持久化与崩溃矩阵 / 删除对账 / 上限与互斥。
/// </summary>
public sealed class TierVolumeSnapshotTests : IDisposable
{
    private readonly string _dir = TestTempDir.Create("core-io-tv-snapshot");
    private readonly string _volPath;

    public TierVolumeSnapshotTests() => _volPath = Path.Combine(_dir, "v.tier");

    public void Dispose() => TestTempDir.TryCleanup(_dir);

    private TierVolumeFs Format(long capacity = 64L << 20)
        => TierVolumeFs.New(TierVolumeCarrier.File(_volPath), new TierVolumeFormatOptions
        {
            QuotaBytes = capacity,
            JournalReserveBytes = 8L << 20,
        });

    private static FileOpenOptions RWO() => new()
    { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate, Sharing = FileSharing.ReadWrite };

    private static FileOpenOptions RO() => new()
    { Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting, Sharing = FileSharing.ReadWrite };

    private static TierVolumeOpenOptions SnapMount(string name) => new()
    { Access = AccessMode.Read, SnapshotName = name };

    [Fact]
    public void CreateSnapshot_CapturesConsistentState_LiveWritesDoNotAffectReads()
    {
        var fs = Format();
        var data = new byte[8192];
        new Random(11).NextBytes(data);
        using (var h = fs.Open("f", RWO()))
        {
            h.Write(0, data);
            h.Flush();
        }
        var info = fs.CreateSnapshot("s1");
        info.Name.Should().Be("s1");
        fs.ListSnapshots().Should().ContainSingle().Which.Name.Should().Be("s1");

        // 活卷改写/删除——快照读面不动
        var overwrite = new byte[8192];
        new Random(22).NextBytes(overwrite);
        using (var h = fs.Open("f", RWO()))
        {
            h.Write(0, overwrite);
            h.Flush();
        }
        fs.Delete("f");

        using var mount = TierVolumeFs.Open(TierVolumeCarrier.File(_volPath), SnapMount("s1"));
        mount.Exists("f").Should().BeTrue("快照读面 = 捕获时刻状态");
        using (var h = mount.Open("f", RO()))
        {
            h.Length.Should().Be(8192);
            var buf = new byte[8192];
            h.Read(0, buf).Should().Be(8192);
            buf.Should().Equal(data, "快照数据 = 捕获时刻数据（冻结钉块未被活卷覆写）");
        }
        fs.Exists("f").Should().BeFalse("活卷已删——与快照读面互不干扰");
    }

    [Fact]
    public void FrozenBlocksPinned_DeleteThenHeavyWrite_SnapshotDataStable()
    {
        var fs = Format(capacity: 16L << 20);   // 小卷——强制复用空间（无钉块则必被覆写）
        var victim = new byte[64 * 1024];
        new Random(33).NextBytes(victim);
        using (var h = fs.Open("victim", RWO()))
        {
            h.Write(0, victim);
            h.Flush();
        }
        fs.CreateSnapshot("s-pin");

        // 删除冻结核文件 + 大量新写（无钉块则 victim 块必被重用覆写）
        fs.Delete("victim");
        var filler = new byte[64 * 1024];
        new Random(44).NextBytes(filler);
        for (var i = 0; i < 128; i++)
        {
            using var h = fs.Open($"fill{i}", RWO());
            h.Write(0, filler);
        }
        fs.FlushRoot();

        using var mount = TierVolumeFs.Open(TierVolumeCarrier.File(_volPath), SnapMount("s-pin"));
        using (var h = mount.Open("victim", RO()))
        {
            var buf = new byte[64 * 1024];
            h.Read(0, buf).Should().Be(64 * 1024);
            buf.Should().Equal(victim, "冻结块不被复用——快照读面稳定");
        }
    }

    [Fact]
    public void SnapshotMount_ReadOnly_AllMutationsRejected()
    {
        var fs = Format();
        using (var h = fs.Open("f", RWO()))
            h.Write(0, new byte[100]);
        fs.CreateSnapshot("s-ro");

        using var mount = TierVolumeFs.Open(TierVolumeCarrier.File(_volPath), SnapMount("s-ro"));
        mount.Volume.Access.Should().Be(AccessMode.Read);
        mount.Invoking(m => m.CreateFile("x")).Should().Throw<FileIOException>()
            .Where(ex => ex.Error == IOError.ReadOnlyVolume);
        mount.Invoking(m => m.Delete("x")).Should().Throw<FileIOException>();
        mount.Invoking(m => m.CreateSnapshot("s2")).Should().Throw<FileIOException>()
            .Where(ex => ex.Error == IOError.ReadOnlyVolume);
        mount.Invoking(m => m.CreateDirectory("d")).Should().Throw<FileIOException>();
        Action openWrite = () => mount.Open("x", new FileOpenOptions
        { Access = AccessMode.ReadWrite, Mode = FileOpenMode.CreateNew });
        openWrite.Should().Throw<FileIOException>().Where(ex => ex.Error == IOError.ReadOnlyVolume);
        using (var h = mount.Open("f", RO()))
            h.Invoking(x => x.Write(0, new byte[10])).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void SnapshotsPersist_ReopenLiveVolume_TableAndMountIntact()
    {
        var data = new byte[2048];
        new Random(55).NextBytes(data);
        {
            var fs = Format();
            using (var h = fs.Open("f", RWO()))
                h.Write(0, data);
            fs.CreateSnapshot("s-persist");
            fs.Dispose();
        }

        using var fs2 = TierVolumeFs.Open(TierVolumeCarrier.File(_volPath));
        fs2.ListSnapshots().Should().ContainSingle(s => s.Name == "s-persist");
        using var mount = TierVolumeFs.Open(TierVolumeCarrier.File(_volPath), SnapMount("s-persist"));
        using (var h = mount.Open("f", RO()))
        {
            var buf = new byte[2048];
            h.Read(0, buf).Should().Be(2048);
            buf.Should().Equal(data);
        }
    }

    [Fact]
    public void CrashAfterCapture_DirtyRecovery_KeepsSnapshotAndFrozenData()
    {
        var data = new byte[4096];
        new Random(66).NextBytes(data);
        var fs = Format();
        using (var h = fs.Open("f", RWO()))
        {
            h.Write(0, data);
            h.Flush();
        }
        fs.CreateSnapshot("s-crash");   // 检查点原子——翻转即持久（含冻结位图区）
        fs.Delete("f");                 // 冻结块钉住（快照引用）——记录随下次提交
        using (var h = fs.Open("ghost", RWO()))
        {
            h.Write(0, new byte[128]);
            h.Flush();   // 仅日志提交（FileDelete + ghost 写入记录；无检查点——重放将重执行删除）
        }
        fs.CrashSimulate();

        // dirty 恢复：对账（冻结块保持 used）+ 重放（FileDelete 重执行经冻结感知释放——钉块不还位图）
        using var fs2 = TierVolumeFs.Open(TierVolumeCarrier.File(_volPath));
        fs2.ListSnapshots().Should().ContainSingle(s => s.Name == "s-crash");
        fs2.Exists("f").Should().BeFalse("已提交删除经重放生效");
        fs2.Exists("ghost").Should().BeTrue("已提交写入经重放生效");
        using var mount = TierVolumeFs.Open(TierVolumeCarrier.File(_volPath), SnapMount("s-crash"));
        using (var h = mount.Open("f", RO()))
        {
            var buf = new byte[4096];
            h.Read(0, buf).Should().Be(4096);
            buf.Should().Equal(data, "冻结块经 dirty 对账/重放仍保持 used——快照读面完整");
        }
    }

    [Fact]
    public void DeleteSnapshot_FreesPinnedBlocks_FreeSpaceGrows()
    {
        var fs = Format();
        var data = new byte[4L << 20];
        using (var h = fs.Open("big", RWO()))
            h.Write(0, data);
        fs.CreateSnapshot("s-del");
        fs.Delete("big");   // 4MB 钉块
        fs.FlushRoot();
        var pinned = fs.Volume.FreeSpace;
        fs.DeleteSnapshot("s-del");
        fs.FlushRoot();
        var freed = fs.Volume.FreeSpace;
        freed.Should().BeGreaterThan(pinned, "删除快照 = 位图差集对账——独占冻结块还位图");
        fs.ListSnapshots().Should().BeEmpty();
        Action openDeleted = () => TierVolumeFs.Open(TierVolumeCarrier.File(_volPath), SnapMount("s-del"));
        openDeleted.Should().Throw<FileIOException>().Where(ex => ex.Error == IOError.NotFound);
    }

    [Fact]
    public void DeleteSnapshot_ActiveMount_RejectedUntilClosed()
    {
        var fs = Format();
        using (var h = fs.Open("f", RWO()))
            h.Write(0, new byte[100]);
        fs.CreateSnapshot("s-mount");
        var mount = TierVolumeFs.Open(TierVolumeCarrier.File(_volPath), SnapMount("s-mount"));
        fs.Invoking(f => f.DeleteSnapshot("s-mount")).Should().Throw<FileIOException>()
            .Where(ex => ex.Error == IOError.SharingViolation);
        mount.Dispose();
        fs.Invoking(f => f.DeleteSnapshot("s-mount")).Should().NotThrow();
    }

    [Fact]
    public void MaxSnapshots_ExceededRejected()
    {
        var fs = Format();
        for (var i = 0; i < 16; i++)
            fs.CreateSnapshot($"s{i}");
        fs.Invoking(f => f.CreateSnapshot("s16")).Should().Throw<FileIOException>()
            .Where(ex => ex.Error == IOError.IOFailure);
    }

    [Fact]
    public void DuplicateNameRejected()
    {
        var fs = Format();
        fs.CreateSnapshot("dup");
        fs.Invoking(f => f.CreateSnapshot("dup")).Should().Throw<FileIOException>()
            .Where(ex => ex.Error == IOError.AlreadyExists);
        fs.Invoking(f => f.CreateSnapshot("bad/name")).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CreateSnapshot_NonJournaledVolume_Rejected()
    {
        var path = Path.Combine(_dir, "nj.tier");
        using var fs = TierVolumeFs.New(TierVolumeCarrier.File(path), new TierVolumeFormatOptions
        {
            QuotaBytes = 16L << 20,
            JournalReserveBytes = 0,   // 无日志
        });
        fs.Invoking(f => f.CreateSnapshot("s")).Should().Throw<FileIOException>()
            .Where(ex => ex.Error == IOError.Unsupported);
    }

    [Fact]
    public void AddCarrier_WithSnapshots_Rejected()
    {
        var fs = Format();
        fs.CreateSnapshot("s-add");
        var member = TierVolumeCarrier.File(Path.Combine(_dir, "m1.tier"));
        fs.Invoking(f => f.AddCarrier(member, 16L << 20)).Should().Throw<FileIOException>()
            .Where(ex => ex.Error == IOError.Unsupported);
    }

    [Fact]
    public void CreateSnapshot_AutoExpandVolume_WorksAcrossExpansion()
    {
        var path = Path.Combine(_dir, "auto.tier");
        var fs = TierVolumeFs.New(TierVolumeCarrier.File(path), new TierVolumeFormatOptions
        {
            QuotaBytes = -1,   // 自动扩容（初始界 64MiB）
            JournalReserveBytes = 8L << 20,
        });
        using (var h = fs.Open("big", RWO()))
            h.Write(0, new byte[96L << 20]);   // > 初始界——触发自动扩容
        fs.CreateSnapshot("s-auto");
        using (var h = fs.Open("big", RWO()))
            h.Write(0, new byte[1024]);   // 捕获后覆写（冻结块 CoW）
        fs.FlushRoot();

        using var mount = TierVolumeFs.Open(TierVolumeCarrier.File(path),
            new TierVolumeOpenOptions { Access = AccessMode.Read, SnapshotName = "s-auto" });
        using (var h = mount.Open("big", RO()))
        {
            h.Length.Should().Be(96L << 20, "扩容后捕获的快照完整（冻结区按新容量尺寸）");
            var probe = new byte[1024];
            h.Read(0, probe).Should().Be(1024);
        }
        fs.Dispose();
    }

    [Fact]
    public void SnapshotOpsJournaled_ReplayIdempotent_AfterCrash()
    {
        var fs = Format();
        fs.CreateSnapshot("s-a");
        fs.CreateSnapshot("s-b");
        fs.FlushRoot();   // 记录落盘
        fs.CrashSimulate();

        using var fs2 = TierVolumeFs.Open(TierVolumeCarrier.File(_volPath));
        fs2.ListSnapshots().Select(s => s.Name).Should().BeEquivalentTo(["s-a", "s-b"],
            "SnapshotCreate 记录重放幂等（表已含则跳——无重复条目）");
    }

    [Fact]
    public void SnapshotDelete_CrashAfter_ReopenTableConsistent()
    {
        var fs = Format();
        fs.CreateSnapshot("s-x");
        fs.CreateSnapshot("s-y");
        fs.DeleteSnapshot("s-x");
        fs.FlushRoot();
        fs.CrashSimulate();

        using var fs2 = TierVolumeFs.Open(TierVolumeCarrier.File(_volPath));
        fs2.ListSnapshots().Select(s => s.Name).Should().BeEquivalentTo(["s-y"],
            "SnapshotDelete 记录重放幂等（表已不含则跳）");
    }

    [Fact]
    public void SnapshotMount_UnknownName_NotFound()
    {
        using var fs = Format();
        fs.CreateSnapshot("real");
        Action openGhost = () => TierVolumeFs.Open(TierVolumeCarrier.File(_volPath), SnapMount("ghost"));
        openGhost.Should().Throw<FileIOException>().Where(ex => ex.Error == IOError.NotFound);
    }

    [Fact]
    public void SnapshotMount_ReadWriteAccess_Rejected()
    {
        using var fs = Format();
        fs.CreateSnapshot("s-acc");
        Action openRw = () => TierVolumeFs.Open(TierVolumeCarrier.File(_volPath), new TierVolumeOpenOptions
        { Access = AccessMode.ReadWrite, SnapshotName = "s-acc" });
        openRw.Should().Throw<ArgumentException>("快照挂载恒只读");
    }

    [Fact]
    public void ParallelWrites_WithSnapshots_FrozenCoWPreservesSnapshot()
    {
        var path = Path.Combine(_dir, "par.tier");
        var fs = TierVolumeFs.New(TierVolumeCarrier.File(path), new TierVolumeFormatOptions
        {
            QuotaBytes = 64L << 20,
            JournalReserveBytes = 8L << 20,
            WriteConcurrency = WriteConcurrencyMode.Parallel,   // §2.1 并行档——CoW 在规划段（锁内）决策
        });
        var data = new byte[1L << 20];
        new Random(88).NextBytes(data);
        using (var h = fs.Open("f", RWO()))
            h.Write(0, data);
        fs.CreateSnapshot("s-par");

        // 不相交区间并发覆写（各自 256KB 区——冻结命中 → CoW → 合并提交）
        var overwrite = new byte[1 << 20];
        new Random(99).NextBytes(overwrite);
        var tasks = Enumerable.Range(0, 4).Select(i => Task.Run(() =>
        {
            using var h = fs.Open("f", new FileOpenOptions
            { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenExisting, Sharing = FileSharing.ReadWrite });
            h.Write((long)i << 18, overwrite.AsSpan(i << 18, 1 << 18));
        })).ToArray();
        Task.WaitAll(tasks);
        fs.FlushRoot();

        using var mount = TierVolumeFs.Open(TierVolumeCarrier.File(path),
            new TierVolumeOpenOptions { Access = AccessMode.Read, SnapshotName = "s-par" });
        using (var h = mount.Open("f", RO()))
        {
            var buf = new byte[1 << 20];
            h.Read(0, buf).Should().Be(1 << 20);
            buf.Should().Equal(data, "并行覆写走冻结 CoW——快照读面 = 捕获时刻数据");
        }
        fs.Dispose();
    }

    [Fact]
    public void SnapshotMount_CapturableAsArchive_LiveClosure()
    {
        var data = new byte[12 * 1024];
        new Random(77).NextBytes(data);
        var fs = Format();
        using (var h = fs.Open("f", RWO()))
            h.Write(0, data);
        fs.CreateSnapshot("s-arch");
        using (var h = fs.Open("f", RWO()))
            h.Write(0, new byte[12 * 1024]);   // 活卷覆写
        fs.FlushRoot();

        // 「存档 = 活卷」闭环：快照挂载即一致存档点——可经管线采集
        using var mount = TierVolumeFs.Open(TierVolumeCarrier.File(_volPath), SnapMount("s-arch"));
        var ms = new MemoryStream();
        var summary = RootSpaceImage.Capture(mount, ms, new ImageOptions { Compression = ImageCompression.None });
        summary.EntryCount.Should().Be(1);
        ms.Position = 0;
        using var restored = MemoryFileSystem.New();
        RootSpaceImage.Restore(ms, restored);
        using (var h = restored.Open("f", RO()))
        {
            var buf = new byte[12 * 1024];
            h.Read(0, buf).Should().Be(12 * 1024);
            buf.Should().Equal(data, "采集物 = 快照时刻数据");
        }
    }
}
