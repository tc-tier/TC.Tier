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
}
