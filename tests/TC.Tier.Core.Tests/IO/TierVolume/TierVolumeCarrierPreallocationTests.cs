using TC.Tier.Core.IO;
using TC.Tier.Core.IO.TierVolume;

namespace TC.Tier.Core.Tests.IO.TierVolume;

/// <summary>
/// IS-02：TierVolume 载体预分配档——Metadata（现行稀疏）vs Full（物理占位）载体物化语义。
/// <para>物理占用断言经 <see cref="FileNative.GetFileAllocatedDiskSize"/>（Win=AllocatedSize / Linux=st_blocks；
///   macOS 降级为逻辑大小——物理断言在该平台跳过）。</para>
/// </summary>
public sealed class TierVolumeCarrierPreallocationTests : IDisposable
{
    private readonly string _dir = TestTempDir.Create("core-io-tv-prealloc");
    private readonly List<TierVolumeFs> _openFs = [];

    private string NewVolumePath() => Path.Combine(_dir, $"vol-{Guid.NewGuid():N}.tier");

    public void Dispose()
    {
        foreach (var fs in _openFs) fs.Dispose();
        TestTempDir.TryCleanup(_dir);
    }

    private static long CarrierAllocatedBytes(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return FileNative.GetFileAllocatedDiskSize(handle);
    }

    // ═══════════════ 载体物化 ═══════════════

    [Fact]
    public void Format_Metadata_StaysSparse()
    {
        if (OperatingSystem.IsMacOS()) return;   // macOS GetFileAllocatedDiskSize 降级=逻辑大小，物理断言不可用

        var path = NewVolumePath();
        using (var fs = TierVolumeFs.New(TierVolumeCarrier.File(path), new TierVolumeFormatOptions { QuotaBytes = 16L << 20 })) { }

        var allocated = CarrierAllocatedBytes(path);
        allocated.Should().BeLessThan(1L << 20,
            $"Metadata 档载体稀疏——SetLength 只改元数据（实际分配 {allocated} 字节）");
    }

    [Fact]
    public void Format_Full_PhysicallyAllocatesCarrier()
    {
        if (OperatingSystem.IsMacOS()) return;   // 同上

        var path = NewVolumePath();
        using (var fs = TierVolumeFs.New(TierVolumeCarrier.File(path),
                   new TierVolumeFormatOptions { QuotaBytes = 16L << 20, Preallocation = PreallocationMode.Full })) { }

        var allocated = CarrierAllocatedBytes(path);
        allocated.Should().BeGreaterThan((long)(16L << 20) * 9 / 10,
            $"full 档载体物理占位——创建时物化全部空间（实际分配 {allocated} 字节）");
    }

    // ═══════════════ full 档挂载往返 ═══════════════

    [Fact]
    public void Format_Full_ThenOpenWithFullMount_RoundTrips()
    {
        var path = NewVolumePath();
        var payload = new byte[8192];
        Random.Shared.NextBytes(payload);

        using (var fs = TierVolumeFs.New(TierVolumeCarrier.File(path),
                   new TierVolumeFormatOptions { QuotaBytes = 8L << 20, Preallocation = PreallocationMode.Full }))
        {
            _openFs.Add(fs);
            using var h = fs.Open("data.bin", new FileOpenOptions
            { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate, Sharing = FileSharing.ReadWrite });
            h.Write(0, payload);
        }

        var reopened = TierVolumeFs.Open(TierVolumeCarrier.File(path), new TierVolumeOpenOptions { Preallocation = PreallocationMode.Full });
        _openFs.Add(reopened);
        using var rh = reopened.Open("data.bin", new FileOpenOptions
        { Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting, Sharing = FileSharing.ReadWrite });
        var buf = new byte[payload.Length];
        rh.Read(0, buf);
        buf.Should().Equal(payload, "full 档重开（跳过稀疏标记）读写一致");
    }
}
