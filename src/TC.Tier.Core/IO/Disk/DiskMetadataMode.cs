namespace TC.Tier.Core.IO.Disk;

/// <summary>
/// 磁盘元数据存储模式（构造期显式配置——部署决策，非逐文件运行时隐式判定）。
/// <para>★ 决策依据（filesystem-root-space-design §3.6 修订）：隐式"写失败回退"会产生混合态
///   （部分文件元数据在 xattr、部分在 sidecar）——读取端被迫永远双通道探测。</para>
/// </summary>
public enum DiskMetadataMode
{
    /// <summary>xattr 优先，写失败回退 sidecar（默认——向后兼容）；读取双通道（xattr 先、sidecar 兜底）。</summary>
    Fallback = 0,

    /// <summary>仅 xattr/ADS——构造期 <c>ProbeFileMetaSupport</c> 探测通道可用性，不可用即抛（部署错误 fail-fast）。</summary>
    ExtendedAttr = 1,

    /// <summary>仅 sidecar <c>.{name}</c> 伴生文件——读取单通道（免 xattr 探测 syscall）；枚举隐藏配对 sidecar。</summary>
    Sidecar = 2,
}
