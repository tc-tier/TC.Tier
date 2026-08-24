namespace TC.Tier.Core.IO.Raw;

/// <summary>
/// 载体描述（raw-medium-and-conversion-design §2.1）——两种物理形态，一份逻辑布局。
/// <para>★ 文件载体：OS 文件系统上的 `.raw` 单文件（跨进程锁 = 伴生锁文件）。</para>
/// <para>★ 设备载体：Linux 块设备/裸分区路径（`/dev/...`；跨进程锁 = flock——Windows 物理盘不在
///   支持范围，能力诚实降级，§14.4）。</para>
/// </summary>
public sealed record RawCarrier
{
    /// <summary>载体路径（文件路径或设备路径——绝对路径）。</summary>
    public string Path { get; private init; }

    /// <summary>是否块设备载体（false = `.raw` 文件载体）。</summary>
    public bool IsDevice { get; private init; }

    /// <summary>
    /// 隐式转换：字符串路径 → 文件载体（`.raw` 文件载体，非设备载体）。
    /// </summary>
    /// <param name="path">文件路径</param>
    /// <returns>对应的 RawCarrier 实例</returns>
    public static implicit operator RawCarrier(string path) => new() { Path = path, IsDevice = false };
    /// <summary>文件载体工厂。</summary>
    public static RawCarrier File(string path) => new() { Path = path, IsDevice = false };

    /// <summary>块设备载体工厂（Linux `/dev/...`）。</summary>
    public static RawCarrier Device(string path) => new() { Path = path, IsDevice = true };

    /// <summary>规范化身份键（实例唯一性登记用——全小写 + 正斜杠归一）。</summary>
    internal string IdentityKey =>
        (IsDevice ? "dev:" : "file:") + Path.Replace('\\', '/').ToLowerInvariant();
}
