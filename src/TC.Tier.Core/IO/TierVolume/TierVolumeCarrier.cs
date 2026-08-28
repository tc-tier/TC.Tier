namespace TC.Tier.Core.IO.TierVolume;

/// <summary>
/// 载体描述（raw-medium-and-conversion-design §2.1）——两种物理形态，一份逻辑布局。
/// <para>★ 文件载体：OS 文件系统上的 `.tier` 单文件（跨进程锁 = 伴生锁文件）。</para>
/// <para>★ 设备载体：裸块设备/裸分区（跨进程排他 = 卷级独占）——
///   Linux：`/dev/...`（原生 open(2) + flock(LOCK_EX|LOCK_NB) + O_DIRECT）；</para>
///   Windows（2026-08-26 补齐）：`virtual:///dev/C:`（卷 → `\\.\C:`，CreateFile share=0 独占 +
///   FSCTL_LOCK_VOLUME 锁卷）或 `virtual:///dev/PhysicalDriveN`（物理盘 → `\\.\PhysicalDriveN`，
///   独占句柄排他）；NO_BUFFERING 直 IO + WRITE_THROUGH 写穿档（IS-03 同机制）。
/// </summary>
public sealed record TierVolumeCarrier
{
    /// <summary>载体路径（文件路径或设备路径——绝对路径）。</summary>
    public string Path { get; private init; }

    /// <summary>是否块设备载体（false = `.tier` 文件载体）。</summary>
    public bool IsDevice { get; private init; }

    /// <summary>
    /// 隐式转换：字符串路径 → 文件载体（`.tier` 文件载体，非设备载体）。
    /// </summary>
    /// <param name="path">文件路径</param>
    /// <returns>对应的 <see cref="TierVolumeCarrier"/> 实例</returns>
    public static implicit operator TierVolumeCarrier(string path) => new() { Path = path, IsDevice = false };
    /// <summary>文件载体工厂。</summary>
    /// <param name="path">文件路径</param>
    /// <returns>对应的 <see cref="TierVolumeCarrier"/> 实例</returns>
    public static TierVolumeCarrier File(string path) => new() { Path = path, IsDevice = false };

    /// <summary>块设备载体工厂（Linux `/dev/...`）。</summary>
    /// <param name="path">设备路径</param>
    /// <returns>对应的 <see cref="TierVolumeCarrier"/> 实例</returns>
    public static TierVolumeCarrier Device(string path) => new() { Path = path, IsDevice = true };

    /// <summary>规范化身份键（实例唯一性登记用——全小写 + 正斜杠归一）。</summary>
    internal string IdentityKey =>
        (IsDevice ? "dev:" : "file:") + Path.Replace('\\', '/').ToLowerInvariant();
}
