using TC.Tier.CodeGen;
namespace TC.Tier.Core.IO.Disk;

/// <summary>
/// 本地文件系统选项（medium-protocol-and-parity-design §6——继承 FileSystemOptions 基类）。
/// <para>★ 基类成员生效：Access（G2 包络执法）/ QuotaBytes（G3——枚举基线 + 写前拒）/ Label（G1——.tier-volume 标记）。</para>
/// </summary>
[MediumOptions("local", Verbs = "New,Open,OpenOrCreate")]
public sealed class DiskFileSystemOptions : FileSystemOptions
{
    /// <summary>FileExtra 存储模式（构造期显式配置——部署决策，非逐文件运行时隐式判定）。</summary>
    public DiskMetadataMode MetadataMode { get; init; } = DiskMetadataMode.Fallback;

    /// <summary>
    /// FileExtra 元数据写是否随写 fsync 持久（默认 true）。
    /// <para>★ false = 缓存写（写入即读——缓存管理器读己写恒成立；掉电窗口内可能丢失，
    ///   耐久化由上层锚定）——卷级策略开关：churn 卷（满并发建/删/改）上 FlushFileBuffers
    ///   等 NTFS 元数据静默窗无界停滞（磁盘全闲也可数分钟），高频元数据场景必须关。
    ///   同时作用于 xattr/ADS 通道 flush 与 sidecar 通道 WriteThrough。</para>
    /// </summary>
    public bool MetaWriteDurable { get; init; } = true;

    /// <summary>
    /// 预分配方式轴（IS-04，默认 <see cref="PreallocationMode.Metadata"/> = 现行 best-effort 稀疏降级）。
    /// <para><see cref="PreallocationMode.Full"/> = 物理占位强制：CreateFile/句柄 Preallocate 走
    ///   <c>EnsurePhysicalAllocation</c>（SetFileValidData/fallocate/F_PREALLOCATE/零写兜底），
    ///   失败显式报错而非静默降级为稀疏（部署错误 fail-fast）。</para>
    /// </summary>
    public PreallocationMode Preallocation { get; init; } = PreallocationMode.Metadata;
}
