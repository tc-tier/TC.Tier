using FluentAssertions;
using TC.Tier.Core.IO;
using TC.Tier.Core.IO.TierVolume;
using TC.Tier.Core.IO.Shared;

namespace TC.Tier.Core.Tests.IO.TierVolume;

/// <summary>
/// Windows 裸设备载体测试（补齐 2026-08-26——Linux /dev/ 的 Windows 化身）：
/// 语法映射（/dev/C: → \\.\C: 卷；/dev/PhysicalDriveN → 物理盘）+ 卷形态判定 + 错误路径
/// （不存在设备明确报错——真实裸盘读写需管理员权限 + 真实分区，手动验证场景）。
/// </summary>
public class TierVolumeDeviceCarrierTests
{
    // ═══ 语法映射（跨平台纯函数——Windows 设备路径翻译）═══

    [Theory]
    [InlineData("/dev/C:", @"\\.\C:")]
    [InlineData("/dev/D:", @"\\.\D:")]
    [InlineData("/dev/PhysicalDrive1", @"\\.\PhysicalDrive1")]
    [InlineData("/dev/PhysicalDrive0", @"\\.\PhysicalDrive0")]
    [InlineData("/dev/nvme0n1", @"\\.\nvme0n1")]   // Linux 风格路径原样翻译（Windows 上不存在——打开报错）
    [InlineData(@"C:\data\tier.tier", @"C:\data\tier.tier")]   // 文件载体不翻译
    public void ToWindowsDevicePath_MapsDevPrefix(string input, string expected)
        => TierVolumeFs.ToWindowsDevicePath(input).Should().Be(expected);

    [Theory]
    [InlineData(@"\\.\C:", true)]
    [InlineData(@"\\.\Z:", true)]
    [InlineData(@"\\.\PhysicalDrive1", false)]
    [InlineData(@"\\.\nvme0n1", false)]
    public void IsVolumePath_DetectsDriveLetterForm(string path, bool expected)
        => TierVolumeFs.IsVolumePath(path).Should().Be(expected);

    // ═══ 错误路径（Windows 实测——不存在设备明确报错；真实设备需管理员权限 + 手动验证）═══

    [Fact]
    public void Open_NonexistentDevice_ThrowsWithHint()
    {
        // 不存在设备路径（任何权限下 CreateFile 都失败）——错误信息须含管理员/存在提示（诚实语义）
        var carrier = TierVolumeCarrier.Device("/dev/ZZZ_Nonexistent_9");
        var act = () => TierVolumeFs.New(carrier,
            new TierVolumeFormatOptions { BlockSize = 4096 }, logger: null);
        act.Should().Throw<FileIOException>()
            .Which.Message.Should().Contain("Windows 设备打开失败", "设备路径翻译 + 明确错误");
    }

    [Fact]
    public void Open_NonexistentWindowsDrivePath_Throws()
    {
        // 直接 Windows 设备路径形态（非 /dev/ 前缀）——同走设备分支
        var carrier = TierVolumeCarrier.Device(@"\\.\ZZZ_Nonexistent_9");
        var act = () => TierVolumeFs.New(carrier,
            new TierVolumeFormatOptions { BlockSize = 4096 }, logger: null);
        act.Should().Throw<FileIOException>();
    }
}
