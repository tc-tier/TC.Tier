using TC.Tier.Core.IO;
using TC.Tier.Core.IO.TierVolume;

namespace TC.Tier.Core.Tests.IO.TierVolume;

/// <summary>
/// IS-03：载体句柄写穿档（CarrierWriteThrough）——FILE_FLAG_WRITE_THROUGH/O_SYNC 载体 +
/// journal 提交免独立 fsync（写穿完成即单屏障——"写数据 + journal + fsync"三段压成一次写穿）。
/// ★ MMF 直映射写绕 WT 句柄（TierVolumeMappedSection.Flush 经 msync）——屏障不可省，不随写穿档短路。
/// </summary>
public sealed class TierVolumeCarrierWriteThroughTests : IDisposable
{
    private readonly string _dir = TestTempDir.Create("core-io-tv-wt");
    private readonly string _volPath;

    public TierVolumeCarrierWriteThroughTests() => _volPath = Path.Combine(_dir, "v.tier");

    public void Dispose() => TestTempDir.TryCleanup(_dir);

    private TierVolumeFs Format() => TierVolumeFs.New(TierVolumeCarrier.File(_volPath),
        new TierVolumeFormatOptions { QuotaBytes = 32L << 20, CarrierWriteThrough = true });

    private TierVolumeFs Reopen() => TierVolumeFs.Open(TierVolumeCarrier.File(_volPath),
        new TierVolumeOpenOptions { CarrierWriteThrough = true });

    private static FileOpenOptions RWO() => new()
    { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate, Sharing = FileSharing.ReadWrite };

    [Fact]
    public void CarrierWriteThrough_WithFileWtHint_SurvivesImmediateCrash()
    {
        using var fs = Format();
        using (var h = fs.Open("wt", new FileOpenOptions
        {
            Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate,
            Sharing = FileSharing.ReadWrite, Hints = FileOpenHints.WriteThrough,
        }))
        {
            h.Write(0, new byte[500]);   // 无显式 Flush——写透应逐写提交（写穿载体：journal 记录写穿完成即屏障）
        }
        fs.CrashSimulate();
        using var fs2 = Reopen();
        fs2.Exists("wt").Should().BeTrue("载体写穿档 + 句柄 WriteThrough——逐写提交崩溃窗口归零");
        using (var h = fs2.Open("wt", new FileOpenOptions
        { Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting, Sharing = FileSharing.ReadWrite }))
            h.Length.Should().Be(500);
    }

    [Fact]
    public void CarrierWriteThrough_BufferedWrite_FlushSurvives()
    {
        var payload = new byte[8192];
        Random.Shared.NextBytes(payload);
        using var fs = Format();
        using (var h = fs.Open("b", RWO()))
        {
            h.Write(0, payload);
            h.Flush();   // 写穿档 Flush 短路（数据已随写穿达盘——对齐 StorageEngine WT 短路模式）
        }
        fs.CrashSimulate();
        using var fs2 = Reopen();
        using (var h = fs2.Open("b", new FileOpenOptions
        { Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting, Sharing = FileSharing.ReadWrite }))
        {
            h.Length.Should().Be(payload.Length);
            var buf = new byte[payload.Length];
            h.Read(0, buf);
            buf.Should().Equal(payload);
        }
    }

    [Fact]
    public void CarrierWriteThrough_MmapSectionFlush_SurvivesCrash()
    {
        using var fs = Format();
        using (var h = fs.Open("m", RWO()))
        {
            h.Write(0, new byte[8192]);   // 单区间连续 Written
            h.Flush();
        }
        using (var h = fs.Open("m", RWO()))
        using (var map = h.Map(0, 8192, AccessMode.ReadWrite))
        {
            map.View.Span[0] = 0xAB;
            map.View.Span[8191] = 0xCD;
            map.Flush();   // msync + fsync（MMF 绕 WT 句柄——屏障不可省，不随写穿档短路）
        }
        fs.CrashSimulate();
        using var fs2 = Reopen();
        using (var h = fs2.Open("m", new FileOpenOptions
        { Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting, Sharing = FileSharing.ReadWrite }))
        {
            var buf = new byte[2];
            h.Read(0, buf).Should().Be(2);
            buf[0].Should().Be(0xAB, "视图写入经 MSF 落载体——写穿档下 MMF 屏障仍完整");
            h.Read(8190, buf).Should().Be(2);
            buf[1].Should().Be(0xCD);
        }
    }
}
