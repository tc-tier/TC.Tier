using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Disk;
using TC.Tier.Core.IO.TierVolume;
using TC.Tier.Core.IO.Remote;
using TC.Tier.Core.IO.Testing;

namespace TC.Tier.Core.Tests.IO;

/// <summary>
/// IS-04：四介质统一显式选择轴（PreallocationMode，每介质挂载级）——
/// network 显式请求 Full 抛 Unsupported（不静默）/ local full 档 = 物理占位强制（两路径）/
/// TierFs 合流透传 virtual 载体档（BuildVirtual 重建不丢字段）。
/// </summary>
public sealed class PreallocationAxisTests : IDisposable
{
    private readonly string _dir = TestTempDir.Create("core-io-prealloc-axis");

    public void Dispose() => TestTempDir.TryCleanup(_dir);

    // ═══════════════ network：显式请求 Full = 抛 Unsupported ═══════════════

    [Fact]
    public void Remote_PreallocationFull_ThrowsUnsupported()
    {
        using var store = new MemoryObjectStore();
        var act = () => RemoteFileSystem.New(store,
            new RemoteFileSystemOptions { Preallocation = PreallocationMode.Full });
        act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.Unsupported,
            "network 无预分配概念（对象存储无块布局）——显式请求 full 不静默");
    }

    // ═══════════════ local：full 档 = 物理占位强制（两路径）═══════════════

    [Fact]
    public void Disk_PreallocationFull_CreateFile_PhysicallyAllocates()
    {
        var root = Path.Combine(_dir, "disk-full");
        using var fs = DiskFileSystem.New(root, new DiskFileSystemOptions { Preallocation = PreallocationMode.Full });
        fs.CreateFile("seg.bin", 1L << 20);

        using var h = File.OpenHandle(Path.Combine(root, "seg.bin"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        FileNative.GetFileAllocatedDiskSize(h).Should().BeGreaterThan((long)(1L << 20) * 9 / 10,
            "full 档 CreateFile 预分配 = 物理占位（不静默降级稀疏）");
    }

    [Fact]
    public void Disk_PreallocationFull_OpenWithPreallocateSize_PhysicallyAllocates()
    {
        var root = Path.Combine(_dir, "disk-full-2");
        using var fs = DiskFileSystem.New(root, new DiskFileSystemOptions { Preallocation = PreallocationMode.Full });
        using (var h = fs.Open("seg.bin", new FileOpenOptions
        { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate, Sharing = FileSharing.ReadWrite, PreallocateSize = 1L << 20 }))
        { }

        using var fh = File.OpenHandle(Path.Combine(root, "seg.bin"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        FileNative.GetFileAllocatedDiskSize(fh).Should().BeGreaterThan((long)(1L << 20) * 9 / 10,
            "full 档句柄 Preallocate = 物理占位（不静默降级稀疏）");
    }

    // ═══════════════ virtual：TierFs 合流透传（typed 重载 → BuildVirtual 重建不丢档）═══════════════

    [Fact]
    public void TierFs_VirtualTypedOptions_FullCarrier_PassesThrough()
    {
        if (OperatingSystem.IsMacOS()) return;   // GetFileAllocatedDiskSize macOS 降级=逻辑大小，物理断言不可用

        var vol = Path.Combine(_dir, $"vol-{Guid.NewGuid():N}.tier");
        var spec = vol.Replace('\\', '/');
        using (var fs = (TierVolumeFs)TierFs.New($"virtual:///{spec}",
                   new TierVolumeFormatOptions { QuotaBytes = 16L << 20, Preallocation = PreallocationMode.Full }))
        { }

        using var h = File.OpenHandle(vol, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        FileNative.GetFileAllocatedDiskSize(h).Should().BeGreaterThan((long)(16L << 20) * 9 / 10,
            "TierFs 合流透传 full 载体档（BuildVirtual 重建不丢字段）");
    }
}
