using TC.Tier.CodeGen;
namespace TC.Tier.Core.IO.Raw;

/// <summary>
/// 虚拟文件系统打开选项（medium-protocol-and-parity-design §6——继承 FileSystemOptions 基类）。
/// <para>★ 原布尔 <c>ReadOnly</c> 并入基类 <see cref="FileSystemOptions.Access"/>（G2 三态化——
///   Read = 只读打开[dirty 降级形态]，ReadWrite = 缺省；Write = 纯摄入——虚拟卷无此概念，Open 即拒）。</para>
/// <para>★ 基类 <see cref="FileSystemOptions.QuotaBytes"/> = 挂载收紧：有效上限 = min(quota, 供给)（§5.3）；
///   <see cref="FileSystemOptions.Label"/> = Open 校验（不符即抛 fail-fast）。</para>
/// </summary>
[MediumOptions("virtual", Verbs = "Open")]
public sealed class RawOpenOptions : FileSystemOptions
{
    /// <summary>
    /// 数据页缓存预算上限（字节，默认 64 MiB——§3.4 自管页缓存；0 = 禁用读缓存
    /// （直达档行为——大扫描不冲刷内存））。
    /// </summary>
    public long PageCacheBytes { get; init; } = 64L << 20;

    /// <summary>降级打开（RM-04 v2b）：多载体卷允许成员缺失——只读形态（写/变异全拒）；
    /// 数据落在缺失成员上的读抛 <see cref="IOError.IOFailure"/>（诚实——洞数据不可伪造）。</summary>
    public bool AllowDegraded { get; init; }
}
