using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Disk;

namespace TC.Tier.Core.Tests;

/// <summary>
/// 磁盘介质平台能力 gate——平台缺失的语义 <b>skip 而非 fail</b>（被测代码 fail-fast 是正确行为，
/// 缺的是平台，不是实现）。与产品面同一能力真相源：TierWal 持久化地板即按
/// <see cref="FileOpenHints"/>/卷能力判定，测试 gate 语义对齐。
/// <para>★ 每项对应多平台首跑实测（2026-09-11）：</para>
/// <para>· macOS：范围锁原语缺失（F_OFD_SETLKW errno=25 ENOTTY）、无 xattr/ADS 支持声明、
///   APFS punch-hole 物理分配/mmap 写回可见性时序语义不同、无 O_DIRECT、lock-file open 缺口；</para>
/// <para>· GH ubuntu-24.04-arm runner：卷不支持 O_DIRECT（open EINVAL）——x64 runner 支持，
///   故 DIO 必须运行时探测而非平台常量。</para>
/// </summary>
internal static class DiskMediumGate
{
    /// <summary>当前平台具备磁盘介质全套集成语义（Windows/Linux）——卷/镜像/稀疏等集成面测试用。</summary>
    public static bool DiskFull => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    /// <summary>范围锁契约（Lock/TryLock/Unlock）——Windows LockFileEx 与 Linux OFD 双实现；
    /// macOS 无对应原语（F_OFD_SETLKW errno=25 实锤）。</summary>
    public static bool RangeLocks => !OperatingSystem.IsMacOS();

    /// <summary>RangeCompact/打洞型整理——mac 上 allocated-range/hole 三件套（打洞物理分配+范围锁
    /// +mmap 写回可见性）不可靠，被测代码 <c>SupportsRangeCompact=&gt;!IsMacOS()</c> fail-fast 正确，
    /// 测试按 skip 而非 fail。</summary>
    public static bool RangeCompact => !OperatingSystem.IsMacOS();

    /// <summary>O_DIRECT 直接 IO——运行时探测（卷能力非常量，进程内缓存）。</summary>
    public static bool DirectIo => s_directIo.Value;

    private static readonly Lazy<bool> s_directIo = new(ProbeDirectIo);

    /// <summary>探测：temp 目录试开 O_DIRECT 句柄（arm runner 失败点就在 open——EINVAL）。</summary>
    private static bool ProbeDirectIo()
    {
        if (OperatingSystem.IsMacOS()) return false;   // macOS 无 O_DIRECT 语义
        var dir = TestTempDir.Create("core-io-dio-probe");
        try
        {
            var fs = DiskFileSystem.OpenOrCreate(dir);
            try
            {
                using var h = fs.Open("probe", new FileOpenOptions
                {
                    Access = AccessMode.ReadWrite,
                    Mode = FileOpenMode.OpenOrCreate,
                    Sharing = FileSharing.ReadWrite,
                    Hints = FileOpenHints.NoBuffering,
                });
                return true;
            }
            finally { fs.Dispose(); }
        }
        catch (Exception ex) when (ex is FileIOException or IOException or NotSupportedException)
        {
            return false;
        }
        finally
        {
            TestTempDir.TryCleanup(dir);
        }
    }
}
