using System.Runtime.InteropServices;

namespace TC.Tier.Runtime.Benchmarks.Storage;

/// <summary>
/// ★ 冷读辅助 —— 用 posix_fadvise(DONTNEED) 确定性驱逐 page cache，无需 root。
/// <para>★ 解决"35GB DRAM 下 128MB 测试文件全驻 cache"问题：
///   写完后调 EvictRange → 数据从 page cache 驱逐 → 下次读即真磁盘冷读。</para>
/// <para>★ POSIX_FADV_DONTNEED(4)：提示内核这些页不再需要，可立即丢弃。
///   不保证 100% 驱逐（取决于内核版本/内存压力），但实测确定性足够做基线对照。</para>
/// </summary>
internal static partial class ColdReadHelper
{
    private const int POSIX_FADV_DONTNEED = 4;
    private const int POSIX_FADV_WILLNEED = 3;

    [LibraryImport("libc", EntryPoint = "posix_fadvise", SetLastError = true)]
    private static partial int PosixFadvise(int fd, long offset, long len, int advice);

    /// <summary>驱逐 [offset, offset+len) 范围的页缓存（Linux 独有，其他平台 no-op）。</summary>
    public static void EvictRange(int fd, long offset, long len)
    {
        if (!OperatingSystem.IsLinux()) return;
        if (fd < 0) return;
        PosixFadvise(fd, offset, len, POSIX_FADV_DONTNEED);
    }

    /// <summary>驱逐整个文件的页缓存。</summary>
    public static void EvictFile(int fd, long fileSize)
    {
        if (!OperatingSystem.IsLinux()) return;
        if (fd < 0) return;
        PosixFadvise(fd, 0, fileSize, POSIX_FADV_DONTNEED);
    }

    /// <summary>判断当前平台是否支持 fadvise（仅 Linux）。</summary>
    public static bool IsSupported => OperatingSystem.IsLinux();
}
