using System.IO.Hashing;
using TC.Tier.Core.IO;
using TC.Tier.Core.IO.TierVolume;
using TC.Tier.Core.NativeInterop;

namespace TC.Tier.Core.Tests.IO.TierVolume;

/// <summary>
/// V2 §1.3 文件载体打洞（qcow2 discard=unmap 平价）——契约测试：
/// 块回收时对 `.tier` 载体下发 fallocate(PUNCH_HOLE)/FSCTL_SET_ZERO_DATA，
/// 物理尺寸跟踪活数据（存档紧凑）；advisory——载体 FS 不支持打洞时诚实降级（无回归）。
/// </summary>
public sealed class TierVolumeCarrierPunchTests : IDisposable
{
    private readonly string _dir = TestTempDir.Create("core-io-tv-punch");
    private readonly string _volPath;

    public TierVolumeCarrierPunchTests() => _volPath = Path.Combine(_dir, "v.tier");

    public void Dispose() => TestTempDir.TryCleanup(_dir);

    private static FileOpenOptions RWO() => new()
    { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate, Sharing = FileSharing.ReadWrite };

    /// <summary>载体所在文件系统是否支持真打洞（fallocate PUNCH_HOLE）——tmpfs/overlayfs 不支持（EINVAL）。</summary>
    private static bool CarrierSupportsPunch()
    {
        var probe = Path.Combine(Path.GetTempPath(), $"punch-probe-{Guid.NewGuid():N}.bin");
        using (var h = File.OpenHandle(probe, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            RandomAccess.Write(h, new byte[4096], 0);
            var result = FileNative.PunchHole(h, 0, 4096);
            if (result == PunchResult.Punched) return true;
        }
        File.Delete(probe);
        return false;
    }

    private static long CarrierAllocatedBytes(string path)
    {
        using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return FileNative.GetFileAllocatedDiskSize(h);
    }

    [Fact]
    public void Delete_PunchesCarrierSpace_VolumeReopensIntact()
    {
        var punchSupported = CarrierSupportsPunch();
        using (var fs = TierVolumeFs.New(TierVolumeCarrier.File(_volPath),
                   new TierVolumeFormatOptions { QuotaBytes = 64L << 20 }))
        {
            fs.CreateFile("big");
            using var h = fs.Open("big", RWO());
            var chunk = new byte[1 << 20];
            new Random(21).NextBytes(chunk);
            for (var i = 0; i < 40; i++)
                h.Write((long)i << 20, chunk);   // 40MB 写绕直落——载体物理分配
            h.Flush();
        }
        var before = CarrierAllocatedBytes(_volPath);
        before.Should().BeGreaterThan(32L << 20, "40MB 写入后载体物理占用 ≥ 数据量");

        using (var fs = TierVolumeFs.Open(TierVolumeCarrier.File(_volPath)))
        {
            fs.Exists("big").Should().BeTrue("删除前文件在档");
            fs.Delete("big");   // 无句柄在档——即时回收 + TrimCarrierBlocks（文件打洞）
        }

        var after = CarrierAllocatedBytes(_volPath);
        if (punchSupported)
            after.Should().BeLessThan(before / 3,
                "打洞归还宿主空间——物理尺寸跟踪活数据（qcow2 unmap 平价；不支持打洞的 FS 跳过此断言）");

        // 语义无回归：重开卷完整可用（删除生效、几何不变）
        using var reopened = TierVolumeFs.Open(TierVolumeCarrier.File(_volPath));
        reopened.Exists("big").Should().BeFalse("删除持久");
        reopened.Volume.TotalSpace.Should().Be(64L << 20);
    }

    [Fact]
    public void PunchHole_ThenReallocateSameBlocks_ReadsNewOwnerNotResidue()
    {
        // 打洞后同块复用：新属主写入完整覆盖——读不得见旧数据残影（B1 零基纪律与打洞独立成立）
        using var fs = TierVolumeFs.New(TierVolumeCarrier.File(_volPath),
                   new TierVolumeFormatOptions { QuotaBytes = 32L << 20 });
        var dataA = new byte[4096];
        new Random(22).NextBytes(dataA);
        using (var a = fs.Open("A", RWO()))
        {
            a.Write(0, dataA);
            a.Flush();
        }
        fs.Delete("A");   // 打洞（若载体支持）
        using (var b = fs.Open("B", RWO()))
        {
            b.Write(0, dataA);   // first-fit 复用同块
            b.Flush();
        }
        using var br = fs.Open("B", new FileOpenOptions
        { Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting, Sharing = FileSharing.ReadWrite });
        var buf = new byte[4096];
        br.Read(0, buf);
        buf.Should().BeEquivalentTo(dataA, "重分配后读见新属主（打洞不影响 B1 零基纪律）");
    }
}
