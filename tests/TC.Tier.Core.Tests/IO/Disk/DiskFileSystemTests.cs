using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Disk;

namespace TC.Tier.Core.Tests.IO.Disk;

/// <summary>
/// DiskFileSystem 单元测试——命名空间平面（Open/EnsureRoot/Exists/Delete/Move/Enumerate/卷信息/卷锁/Dispose 契约）。
/// </summary>
public sealed class DiskFileSystemTests : IDisposable
{
    private readonly string _dir = TestTempDir.Create("core-io-disk");
    private readonly DiskFileSystem _fs;

    public DiskFileSystemTests()
    {
        _fs = DiskFileSystem.OpenOrCreate(_dir);
        _fs.EnsureRoot();
    }

    public void Dispose()
    {
        _fs.Dispose();
        TestTempDir.TryCleanup(_dir);
    }

    private FileOpenOptions WriteOpts(FileOpenMode mode = FileOpenMode.OpenOrCreate) =>
        new() { Access = AccessMode.ReadWrite, Mode = mode };

    [Fact]
    public void EnsureRoot_Idempotent()
    {
        var act = () => _fs.EnsureRoot();
        act.Should().NotThrow();
        Directory.Exists(_dir).Should().BeTrue();
        _fs.FlushRoot();   // Windows no-op / Unix 目录 fsync——不抛即契约
    }

    [Fact]
    public void Create_EmptyRoot_Throws()
    {
        var act = () => DiskFileSystem.OpenOrCreate("");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Exists_Delete_RoundTrip()
    {
        _fs.Exists("f1").Should().BeFalse();
        using (var h = _fs.Open("f1", WriteOpts(FileOpenMode.CreateNew)))
        {
            h.Write(0, new byte[100]);
        }
        _fs.Exists("f1").Should().BeTrue();
        _fs.Delete("f1");
        _fs.Exists("f1").Should().BeFalse();
    }

    [Fact]
    public void Delete_MissingFile_DoesNotThrow()
    {
        var act = () => _fs.Delete("nope");
        act.Should().NotThrow();   // POSIX unlink 语义：不存在=幂等成功（File.Delete 同）
    }

    [Fact]
    public void Move_OverwriteFalse_TargetExists_ThrowsAlreadyExists()
    {
        using (_fs.Open("src", WriteOpts())) { }
        using (_fs.Open("dst", WriteOpts())) { }
        var act = () => _fs.Move("src", "dst", overwrite: false);
        act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.AlreadyExists);
    }

    [Fact]
    public void Move_OverwriteTrue_ReplacesTarget()
    {
        using (var h = _fs.Open("src", WriteOpts()))
        {
            h.Write(0, new byte[] { 1, 2, 3 });
        }
        using (var h = _fs.Open("dst", WriteOpts()))
        {
            h.Write(0, new byte[] { 9, 9, 9, 9, 9, 9 });
        }
        _fs.Move("src", "dst", overwrite: true);
        _fs.Exists("src").Should().BeFalse();
        using (var h = _fs.Open("dst", new FileOpenOptions { Access = AccessMode.Read }))
        {
            var buf = new byte[8];
            h.Read(0, buf).Should().Be(3);
            buf[..3].Should().Equal(1, 2, 3);
        }
    }

    [Fact]
    public void Enumerate_FusedNameLength_NoOrderGuarantee()
    {
        using (var h = _fs.Open("a", WriteOpts())) { h.Write(0, new byte[10]); }
        using (var h = _fs.Open("b", WriteOpts())) { h.Write(0, new byte[20]); }

        var entries = _fs.EnumerateFiles().OrderBy(e => e.Name, StringComparer.Ordinal).ToArray();
        entries.Select(e => e.Name).Should().BeEquivalentTo("a", "b");
        entries[0].Length.Should().Be(10);
        entries[1].Length.Should().Be(20);
    }

    [Fact]
    public void Volume_Probed_SectorAndAllocationUnitPositive()
    {
        _fs.Volume.SectorSize.Should().BeGreaterThan(0);
        _fs.Volume.AllocationUnit.Should().BeGreaterThan(0);
        // SectorSize 是 2 的幂（512/4096 等）
        var s = (uint)_fs.Volume.SectorSize;
        (s & (s - 1)).Should().Be(0u);
    }

    [Fact]
    public void Capabilities_DiskCommonBits()
    {
        _fs.Capabilities.HasFlag(FileSystemCapabilities.Sparse).Should().BeTrue();
        _fs.Capabilities.HasFlag(FileSystemCapabilities.DurableRename).Should().BeTrue();
        _fs.Capabilities.HasFlag(FileSystemCapabilities.Mmap).Should().BeTrue();
        _fs.Capabilities.HasFlag(FileSystemCapabilities.RangeLock).Should().BeTrue();
        _fs.Capabilities.HasFlag(FileSystemCapabilities.ExclusiveLock).Should().BeTrue();
        _fs.Capabilities.HasFlag(FileSystemCapabilities.RandomWrite).Should().BeTrue();   // pwrite 天然成立
        // FlushDataOnly 仅 Linux 置位
        var expected = OperatingSystem.IsLinux();
        _fs.Capabilities.HasFlag(FileSystemCapabilities.FlushDataOnly).Should().Be(expected);
    }

    [Fact]
    public void Open_IllegalPath_Throws()
    {
        var act = () => _fs.Open("../escape", WriteOpts());
        act.Should().Throw<ArgumentException>();
        // 层级路径合法（根空间）；父目录缺失 → NotFound（原 DirectoryNotFoundException→Unknown 修正）
        var act2 = () => _fs.Open("sub/dir", WriteOpts());
        act2.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.NotFound);
        _fs.CreateDirectory("sub");
        using var _ = _fs.Open("sub/dir", WriteOpts());   // 目录存在后合法创建
    }

    [Fact]
    public void AcquireExclusive_SecondAcquirer_TimesOut()
    {
        using var lease = _fs.AcquireExclusive(TimeSpan.FromMilliseconds(100));
        var act = () => _fs.AcquireExclusive(TimeSpan.FromMilliseconds(150));
        act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.SharingViolation);
    }

    [Fact]
    public void AcquireExclusive_AfterRelease_CanReacquire()
    {
        var lease = _fs.AcquireExclusive(TimeSpan.FromMilliseconds(100));
        lease.Dispose();
        var act = () => _fs.AcquireExclusive(TimeSpan.FromMilliseconds(1000));
        act.Should().NotThrow();
    }

    [Fact]
    public void AcquireExclusive_NonReentrant_SecondAcquireTimesOut()
    {
        // 非重入：持有期间二次 Acquire（同线程=重入、跨线程=争用，不可区分）一律按争用处理 → 超时 SharingViolation
        using var lease = _fs.AcquireExclusive(TimeSpan.FromMilliseconds(100));
        var act = () => _fs.AcquireExclusive(TimeSpan.FromMilliseconds(50));
        act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.SharingViolation);
    }

    [Fact]
    public void AcquireExclusive_CrossInstanceSameRoot_MutualExclusion()
    {
        using var lease = _fs.AcquireExclusive(TimeSpan.FromMilliseconds(100));
        using var fs2 = DiskFileSystem.OpenOrCreate(_dir);
        var act = () => fs2.AcquireExclusive(TimeSpan.FromMilliseconds(150));
        act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.SharingViolation);
    }

    [Fact]
    public void AcquireExclusive_LeaseIdempotentDispose()
    {
        var lease = _fs.AcquireExclusive(TimeSpan.FromMilliseconds(100));
        lease.Dispose();
        var act = () => lease.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void AcquireExclusive_DefaultTimeout_BlocksThenThrowsOrAcquires()
    {
        // 默认（无竞争）获取成功即可
        using var lease = _fs.AcquireExclusive(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Dispose_LeaseHeld_ForceReleaseWithContractViolationPath()
    {
        var fs = DiskFileSystem.OpenOrCreate(_dir);
        var lease = fs.AcquireExclusive(TimeSpan.FromMilliseconds(100));
        fs.Dispose();   // 违约释放：不抛、不卡
        // 释放后可重新获取（新实例）
        using var fs2 = DiskFileSystem.OpenOrCreate(_dir);
        var act = () => fs2.AcquireExclusive(TimeSpan.FromSeconds(2));
        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_OpenHandlesRemainValid_NamespaceOpsThrow()
    {
        var fs = DiskFileSystem.OpenOrCreate(_dir);
        var handle = fs.Open("survivor", WriteOpts());
        fs.Dispose();

        // 磁盘方向："离开目录"——已开句柄继续有效
        var act = () => handle.Write(0, new byte[] { 1, 2, 3 });
        act.Should().NotThrow();
        handle.Dispose();

        // 命名空间操作与 Open 抛 ObjectDisposedException
        ((Action)(() => fs.Open("x", WriteOpts()))).Should().Throw<ObjectDisposedException>();
        ((Action)(() => fs.Exists("x"))).Should().Throw<ObjectDisposedException>();
        ((Action)(() => fs.Delete("x"))).Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Dispose_Idempotent()
    {
        var fs = DiskFileSystem.OpenOrCreate(_dir);
        fs.Dispose();
        var act = () => fs.Dispose();
        act.Should().NotThrow();
    }
    // ═══════════════ 动词面（P2 收尾：OpenOrCreate bind-any 终态——Create 已退役）═══════════════

    [Fact]
    public void OpenOrCreate_BindAny_FreshAndPrePopulated()
    {
        var fresh = Path.Combine(_dir, $"oc-{Guid.NewGuid():N}");
        using (var fs = DiskFileSystem.OpenOrCreate(fresh))
        {
            fs.Exists("a").Should().BeFalse();   // 全新根——建后即空视图
        }
        // 预填充目录（机械 New 替换会炸的那格）：OpenOrCreate 不校验空否
        using (var fs = DiskFileSystem.OpenOrCreate(fresh))
        {
            fs.CreateFile("a");
        }
        using (var again = DiskFileSystem.OpenOrCreate(fresh))
        {
            again.Exists("a").Should().BeTrue("既有非空根——bind-any 打开");
        }
    }
}
