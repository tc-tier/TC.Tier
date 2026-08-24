using TC.Tier.CodeGen;
namespace TC.Tier.Core.IO.Mem;

/// <summary>
/// 内存文件系统选项（medium-protocol-and-parity-design §6——继承 <see cref="FileSystemOptions"/> 基类）。
/// <para>★ 基类三成员在此介质全部生效：Access（包络执法）、QuotaBytes（物理占用计费——G3 改名归一，
///   -1 = 无上限）、Label（P4 落位）。</para>
/// <para>★ 用户决策维 = 内存预算（Sparse 省内存）vs 访问性能（Reserved 直址零分配）。</para>
/// </summary>
[MediumOptions("memory", Verbs = "New,Open,OpenOrCreate")]
public sealed class MemoryFileSystemOptions : FileSystemOptions
{
    /// <summary>分配模式（默认 Sparse）。</summary>
    public MemoryAllocationMode Allocation { get; init; } = MemoryAllocationMode.Sparse;

    /// <summary>稀疏模式页粒度（默认 64K——平衡元数据密度与碎片；模拟超大稀疏文件可调大）。Reserved 模式忽略。</summary>
    public int PageSize { get; init; } = 64 * 1024;
}
