using System.Runtime.InteropServices;

namespace TC.Tier.Core.Tests.NativeInterop;

/// <summary>
/// Windows SDK 原生结构布局断言，可在任意 CI 平台运行。
/// </summary>
public class WindowsNativeLayoutTests
{
    [Fact]
    public void FileStandardInfo_MatchesWindowsSdkLayout()
    {
        Marshal.OffsetOf<FileStandardInfo>(nameof(FileStandardInfo.AllocatedSize)).ToInt32().Should().Be(0);
        Marshal.OffsetOf<FileStandardInfo>(nameof(FileStandardInfo.EndOfFile)).ToInt32().Should().Be(8);
        Marshal.OffsetOf<FileStandardInfo>(nameof(FileStandardInfo.NumberOfLinks)).ToInt32().Should().Be(16);
        Marshal.OffsetOf<FileStandardInfo>(nameof(FileStandardInfo.DeletePending)).ToInt32().Should().Be(20);
        Marshal.OffsetOf<FileStandardInfo>(nameof(FileStandardInfo.Directory)).ToInt32().Should().Be(21);
        Marshal.SizeOf<FileStandardInfo>().Should().Be(24);
    }

    [Fact]
    public void GroupAffinity_MatchesWindowsSdkLayout()
    {
        Marshal.OffsetOf<GroupAffinity>(nameof(GroupAffinity.Mask)).ToInt32().Should().Be(0);
        Marshal.OffsetOf<GroupAffinity>(nameof(GroupAffinity.Group)).ToInt32().Should().Be(8);
        Marshal.OffsetOf<GroupAffinity>(nameof(GroupAffinity.Reserved1)).ToInt32().Should().Be(10);
        Marshal.OffsetOf<GroupAffinity>(nameof(GroupAffinity.Reserved2)).ToInt32().Should().Be(12);
        Marshal.OffsetOf<GroupAffinity>(nameof(GroupAffinity.Reserved3)).ToInt32().Should().Be(14);
        Marshal.SizeOf<GroupAffinity>().Should().Be(16);
    }
}
