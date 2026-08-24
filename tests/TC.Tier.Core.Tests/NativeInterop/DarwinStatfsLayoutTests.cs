using System.Runtime.InteropServices;
using TC.Tier.Core.NativeInterop;

namespace TC.Tier.Core.Tests.NativeInterop;

// 注：LibC.DarwinStatfs / ReadDarwinFstypename 是 LibC（internal）的嵌套类型/方法，须 LibC. 前缀限定。
using LibC = TC.Tier.Core.NativeInterop.LibC;

/// <summary>
/// LibC.DarwinStatfs（macOS struct statfs）布局断言。
/// STORAGE-022 (#242)：字段顺序曾严重错位（FFstypename 在 FOwner 之前），
/// 导致 ReadDarwinFstypename 读到乱码。此测试固定 XNU __DARWIN_STRUCT_STATFS64 (LP64) 的关键偏移。
/// </summary>
/// <remarks>
/// macOS 专属 P/Invoke 无法在 Windows/CI 实测，这里靠 Marshal.OffsetOf/sizeof 校验托管布局
/// 与 XNU 头文件一致——若有人再次打乱字段顺序，偏移断言会立即失败。
/// </remarks>
public class DarwinStatfsLayoutTests
{
    // XNU bsd/sys/mount.h __DARWIN_STRUCT_STATFS64 (LP64) 预期偏移
    // f_bsize@0 f_iosize@4 f_blocks@8 f_bfree@16 f_bavail@24 f_files@32 f_ffree@40 f_fsid@48
    // f_owner@56 f_type@60 f_flags@64 f_fssubtype@68 f_fstypename@72
    private const int ExpectedFstypenameOffset = 72;
    private const int ExpectedOwnerOffset = 56;

    [Fact]
    public void FFstypename_Offset_Matches_XNU_LP64()
    {
        // ★ 关键断言：FFstypename 必须在 FOwner/FType/FFlags/FSSubtype 之后（偏移 72），
        //   而非旧错误代码的偏移 56（紧跟 FSid）。错位会读到 FSyncWrites 区域的字节 = 乱码。
        var offset = Marshal.OffsetOf<LibC.DarwinStatfs>(nameof(LibC.DarwinStatfs.FFstypename));
        ((int)offset).Should().Be(ExpectedFstypenameOffset,
            "FFstypename 必须在 FOwner/FType/FFlags/FSSubtype 之后（对照 XNU LP64）——错位导致 macOS DirectIO 探测读乱码（#242）");
    }

    [Fact]
    public void FOwner_Follows_FSid()
    {
        var offset = Marshal.OffsetOf<LibC.DarwinStatfs>(nameof(LibC.DarwinStatfs.FOwner));
        ((int)offset).Should().Be(ExpectedOwnerOffset,
            "FOwner 必须紧跟 FFsid（f_fsid@48 + 8B = 56）");
    }

    [Fact]
    public void StructSize_Matches_XNU_LP64()
    {
        // 56(到FSid尾) + 16(owner/type/flags/fssubtype) + 16(fstypename)
        // + 1024 + 1024(mnton/mntfrom) + 4(flags_ext) + 28(reserved[7]) = 2168
        int size = Marshal.SizeOf<LibC.DarwinStatfs>();
        size.Should().Be(2168, "总大小须与 XNU struct statfs (LP64) 一致");
    }

    [Fact]
    public unsafe void ReadDarwinFstypename_Decodes_Ascii_UntilNul()
    {
        // 构造一个手填的 LibC.DarwinStatfs，在 FFstypename 写入 "apfs"，验证解码
        LibC.DarwinStatfs s = default;
        var bytes = "apfs"u8;
        for (int i = 0; i < bytes.Length; i++) s.FFstypename[i] = bytes[i];
        var name = LibC.ReadDarwinFstypename(ref s);
        name.Should().Be("apfs");
    }

    [Fact]
    public void ReadDarwinFstypename_Empty_When_AllZero()
    {
        LibC.DarwinStatfs s = default;
        var name = LibC.ReadDarwinFstypename(ref s);
        name.Should().BeEmpty("全零的 fstypename 数组应解码为空字符串");
    }
}
