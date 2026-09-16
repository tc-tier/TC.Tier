using TC.Tier.Core.IO;
using TC.Tier.Core.IO.TierVolume;
using TC.Tier.Products.Wal;

namespace TC.Tier.Products.Tests.Wal;

/// <summary>
/// 持久化质量地板验证（契约① 介质前提——TierWalBuilder.StartAsync 零 IO fail-fast）：
/// Virtual 介质须 CarrierWriteThrough 挂载（能力位 CarrierWriteThrough 仅挂载旋钮置位时诚实报告）；
/// 验证可显式关闭（基准探针测量基线档/非 raft 用途）；Local/Memory 通过。
/// </summary>
public class TierWalDurabilityValidationTests : IDisposable
{
    private readonly string _dir = TestTempDir.Create("tc-wal-durability");
    private readonly List<IFileSystem> _fss = [];

    public void Dispose()
    {
        foreach (var fs in _fss) fs.Dispose();
        TestTempDir.TryCleanup(_dir);
        GC.SuppressFinalize(this);
    }

    private IFileSystem NewVirtual(bool carrierWriteThrough)
    {
        var vol = Path.Combine(_dir, $"vol-{Guid.NewGuid():N}.raw");
        var fs = TierFs.New($"virtual:///{vol.Replace('\\', '/')}",
            new TierVolumeFormatOptions { CarrierWriteThrough = carrierWriteThrough });
        _fss.Add(fs);
        return fs;
    }

    private static TierWalOptions ManualCommit() => TierWalOptions.Default
        .WithCommitInterval(TimeSpan.FromMilliseconds(-1))
        .WithMaxUnflushedBytes(long.MaxValue)
        .WithMaxUnflushedCount(int.MaxValue);

    [Fact]
    public async Task Start_Virtual_NoCarrierWriteThrough_Throws()
    {
        var fs = NewVirtual(carrierWriteThrough: false);
        // ★ 能力位诚实：未置旋钮不报告 CarrierWriteThrough
        fs.Capabilities.HasFlag(FileSystemCapabilities.CarrierWriteThrough).Should().BeFalse();

        var act = () => ManualCommit().Builder(fs).StartAsync();
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*CarrierWriteThrough*");
    }

    [Fact]
    public async Task Start_Virtual_CarrierWriteThrough_Starts()
    {
        var fs = NewVirtual(carrierWriteThrough: true);
        fs.Capabilities.HasFlag(FileSystemCapabilities.CarrierWriteThrough).Should().BeTrue();

        await using var wal = await ManualCommit().Builder(fs).StartAsync();
        wal.PersistedIndex.Should().Be(0);
    }

    [Fact]
    public async Task Start_Virtual_ValidationDisabled_Starts()
    {
        var fs = NewVirtual(carrierWriteThrough: false);

        await using var wal = await ManualCommit().WithDurabilityValidation(false).Builder(fs).StartAsync();
        wal.PersistedIndex.Should().Be(0);
    }

    [Fact]
    public async Task Start_Memory_Medium_Starts()
    {
        using var vol = new TestVolume();
        await using var wal = await ManualCommit().Builder(vol.Fs).StartAsync();
        wal.PersistedIndex.Should().Be(0);
    }
}
