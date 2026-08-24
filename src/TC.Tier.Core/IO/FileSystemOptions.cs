namespace TC.Tier.Core.IO;

/// <summary>
/// options 家族基类（medium-protocol-and-parity-design §6）——名字回收自 G12 删除的遗留 God-options。
/// <para>★ 准入标准 = "同词同义且全介质成立"；v1 三成员——Access（空间访问三态，一个枚举三个平面）/
///   Label（New = 设置 / Open = 校验——时刻语义见 §2.5）/ QuotaBytes（空间根上限，-1 哨兵）。</para>
/// <para>★ 不满足准入标准的参数宁可重复出现在子类，不上收（防 God-options 回潮）。</para>
/// </summary>
public abstract class FileSystemOptions
{
    /// <summary>空间访问三态（缺省 ReadWrite）——fs.Access 是全空间总上包络：句柄/映射构造期校验 ⊑（§5.2）。</summary>
    public AccessMode Access { get; init; } = AccessMode.ReadWrite;

    /// <summary>卷标签（≤32 UTF-8 字节；New = 写入空间根卷记录 / Open = 校验不符即抛——fail-fast）。</summary>
    public string? Label { get; init; }

    /// <summary>空间根容量上限：-1 = 无上限（raw 文件载体 = 按需自动扩容）；&gt;0 = 强制硬限（超限 DiskFull）。</summary>
    public long QuotaBytes { get; init; } = -1;

    /// <summary>排他打开（G5 统一表达）：构造期获取排他（超时 30s 抛 SharingViolation）、Dispose 释放——
    /// 四介质各自最优映射（锁文件/进程内真锁/fencing/内建），强度差异走能力位与差异声明。</summary>
    public bool Exclusive { get; init; }
}
