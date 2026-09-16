using System.Runtime.InteropServices;

namespace TC.Tier.Core.Tests.NativeInterop;

/// <summary>
/// statvfs 结构布局断言（STORAGE-118）——Linux glibc 112B（11 字段 + __f_spare[6]）、
/// Darwin/macOS 88B（11 字段，无 spare）。布局为编译期事实，任意 CI 平台可断言。
/// </summary>
public class StatvfsLayoutTests
{
    [Fact]
    public void StatvfsData_LinuxGlibcLayout_Matches()
    {
        Marshal.SizeOf<LibC.StatvfsData>().Should().Be(112);
        Marshal.OffsetOf<LibC.StatvfsData>(nameof(LibC.StatvfsData.FBsize)).ToInt32().Should().Be(0);
        Marshal.OffsetOf<LibC.StatvfsData>(nameof(LibC.StatvfsData.FrSize)).ToInt32().Should().Be(8);
        Marshal.OffsetOf<LibC.StatvfsData>(nameof(LibC.StatvfsData.FNamemax)).ToInt32().Should().Be(80);
    }

    [Fact]
    public void StatvfsDataMac_DarwinLayout_Matches()
    {
        Marshal.SizeOf<LibC.StatvfsDataMac>().Should().Be(88);
        Marshal.OffsetOf<LibC.StatvfsDataMac>(nameof(LibC.StatvfsDataMac.FBsize)).ToInt32().Should().Be(0);
        Marshal.OffsetOf<LibC.StatvfsDataMac>(nameof(LibC.StatvfsDataMac.FrSize)).ToInt32().Should().Be(8);
        Marshal.OffsetOf<LibC.StatvfsDataMac>(nameof(LibC.StatvfsDataMac.FNamemax)).ToInt32().Should().Be(80);
    }
}
