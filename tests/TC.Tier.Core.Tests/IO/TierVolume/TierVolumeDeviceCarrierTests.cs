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

    // ═══ 错误路径（跨平台分支各自诚实断言——Windows 实测 + Linux open(2)/errno 实测）═══

    [Fact]
    public void Open_NonexistentDevice_ThrowsWithHint()
    {
        // 不存在设备路径（任何平台下打开都失败）——错误信息须含平台分支的管理员/存在提示（诚实语义）
        var carrier = TierVolumeCarrier.Device("/dev/ZZZ_Nonexistent_9");
        var act = () => TierVolumeFs.New(carrier,
            new TierVolumeFormatOptions { BlockSize = 4096 }, logger: null);
        var ex = act.Should().Throw<FileIOException>().Which;
        if (OperatingSystem.IsWindows())
            ex.Message.Should().Contain("Windows 设备打开失败", "Windows 分支：设备路径翻译 + 明确错误");
        else
            ex.Message.Should().Contain("设备打开失败", "Linux 分支：open(2) errno 明确错误");
    }

    [Fact]
    public void Open_NonexistentWindowsDrivePath_Throws()
    {
        // ★ Windows 专属分支（直接 Windows 设备路径形态——非 /dev/ 前缀）——仅 Windows 执行
        if (!OperatingSystem.IsWindows()) return;
        var carrier = TierVolumeCarrier.Device(@"\\.\ZZZ_Nonexistent_9");
        var act = () => TierVolumeFs.New(carrier,
            new TierVolumeFormatOptions { BlockSize = 4096 }, logger: null);
        act.Should().Throw<FileIOException>();
    }

    // ═══ 真机读写（手动场景——需管理员权限 + 专用载体；门控：TC_TEST_WIN_DEVICE）═══
    // 载体会被 TierVolume 格式覆盖，务必用专用分区/盘（数据无价）：
    //   方案 A（零数据风险）：VHDX 挂载——diskpart `create vdisk file=...vhdx maximum=1024`
    //     → attach → create partition primary → assign letter=V，或对 attach 出的 PhysicalDriveN；
    //   方案 B：压缩某卷腾空间新建裸分区（无需文件系统——TierVolume 直接格式化载体）。
    // 激活（管理员终端）：
    //   $env:TC_TEST_WIN_DEVICE='V:'        # 或 'PhysicalDrive2'
    //   dotnet test tests/TC.Tier.Core.Tests -c Release --filter TierVolumeDeviceCarrierTests
    // 覆盖面：CreateFile share=0 独占 + FSCTL_LOCK_VOLUME 锁卷 + 扇区/容量探测 + 格式化落盘 +
    //   Dispose 释放锁 + Open 重挂自描述恢复 + 读写往返（CI 无真机，故环境变量门控——非跳过藏问题）。
    [Fact]
    public void Manual_DeviceCarrier_RealDeviceRoundTrip()
    {
        var dev = Environment.GetEnvironmentVariable("TC_TEST_WIN_DEVICE");
        if (string.IsNullOrEmpty(dev)) return;  // 手动场景门控：见上方注释激活

        var carrier = TierVolumeCarrier.Device("/dev/" + dev);

        // 1. 格式化 + 写入（独占打开/锁卷/探测全在 New→IO 路径上）
        using (var fs = TierVolumeFs.New(carrier,
                   new TierVolumeFormatOptions { BlockSize = 4096 }, logger: null))
        {
            fs.CreateDirectory("probe");   // 父目录须先建——OpenOrCreate 只建文件不建目录
            using var h = fs.Open("probe/device-roundtrip.txt", new FileOpenOptions
            {
                Access = AccessMode.ReadWrite,
                Mode = FileOpenMode.OpenOrCreate,
                Sharing = FileSharing.ReadWrite,
            });
            h.Write(0, "hello, windows device carrier"u8);
            h.Flush();
        }   // Dispose 必须释放卷锁——下方 Open 重挂成功即锁释放的证明

        // 2. 重挂（自描述恢复）+ 读回校验——OpenExisting 确保数据真的在载体上
        using (var fs = TierVolumeFs.Open(TierVolumeCarrier.Device("/dev/" + dev), logger: null))
        {
            using var h = fs.Open("probe/device-roundtrip.txt", new FileOpenOptions
            {
                Access = AccessMode.Read,
                Mode = FileOpenMode.OpenExisting,
                Sharing = FileSharing.Read,
            });
            var buf = new byte[64];
            var n = h.Read(0, buf);
            n.Should().Be("hello, windows device carrier"u8.Length, "读回长度与写入一致");
            buf[..n].Should().Equal("hello, windows device carrier"u8.ToArray(), "字节往返无损");
        }
    }
}
