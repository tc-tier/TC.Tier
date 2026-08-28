using System.Runtime.InteropServices;

namespace TC.Tier.Core.NativeInterop;

/// <summary>
/// 内核原生互操作封装。提供磁盘、块设备等原生 API 封装。
/// <para>★ 跨平台统一入口——所有磁盘级 P/Invoke 收口于此，业务层不判断平台。</para>
/// <para>★ <see langword="internal"/>——Core.IO 的实现底座，编译期封堵外部直调（外部用
///   <c>IFileSystem.Volume</c>，见 docs/native-interop.md §0 映射表）。</para>
/// </summary>
internal static class DiskNative
{
    /// <summary>
    /// ★ 获取磁盘扇区大小（跨平台真实查询，非固定值）。
    /// <para>Windows: <see cref="Kernel32.GetDiskFreeSpace"/>（查卷的物理扇区大小）。</para>
    /// <para>Linux: <c>statvfs</c> 的 <c>f_frsize</c>（文件系统基本块大小，
    ///   无 fd 时作为扇区大小的合理近似；精确值需 ioctl(fd, BLKSSZGET)，但构造时无 fd）。</para>
    /// <para>macOS: <c>statvfs</c> 的 <c>f_frsize</c>（同 Linux，POSIX 标准）。</para>
    /// <para>★ P/Invoke 失败时退回 512（保守下限，DIO 对齐安全值）。</para>
    /// <para>★ 不用 statvfs.f_bsize（文件系统首选块大小，可能偏大如 4096，非 DIO 硬性要求）；
    ///   f_frsize 是基本块大小，更接近物理扇区。</para>
    /// </summary>
    /// <param name="path">文件路径（Windows 取前 2 字符作为卷符；Linux/macOS 取文件所在目录路径）。</param>
    /// <returns>扇区大小（字节）。</returns>
    public static uint GetSectorSize(string path)
    {
        // === Windows: GetDiskFreeSpace ===
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var root = path.Length >= 2 ? path[..2] : path;
            if (Kernel32.GetDiskFreeSpace(root, out _, out var sectorSize, out _, out _))
                return sectorSize;
            return 512;  // P/Invoke 失败退回保守下限
        }
        // === Linux / macOS: statvfs(path).f_frsize ===
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            !RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return 512;// === 其他平台：保守下限 ===
        // statvfs 需要一个已存在的路径（文件或目录）。文件不存在时退回其父目录。
        var queryPath = File.Exists(path) ? path
            : Directory.Exists(path) ? path
            : Path.GetDirectoryName(path);
        if (queryPath is null || LibC.Statvfs(queryPath, out var sv) != 0) return 512; // statvfs 失败或异常值退回保守下限
        var frSize = sv.FrSize;
        if (frSize > 0 && (frSize & (frSize - 1)) == 0)  // 正幂校验
            return (uint)frSize;
        return 512;  // statvfs 失败或异常值退回保守下限
    }
}