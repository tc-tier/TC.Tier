using TC.Tier.CodeGen;
namespace TC.Tier.Core.IO.TierVolume;

/// <summary>
/// 虚拟文件系统打开选项（medium-protocol-and-parity-design §6——继承 FileSystemOptions 基类）。
/// <para>★ 原布尔 <c>ReadOnly</c> 并入基类 <see cref="FileSystemOptions.Access"/>（G2 三态化——
///   Read = 只读打开[dirty 降级形态]，ReadWrite = 缺省；Write = 纯摄入——虚拟卷无此概念，Open 即拒）。</para>
/// <para>★ 基类 <see cref="FileSystemOptions.QuotaBytes"/> = 挂载收紧：有效上限 = min(quota, 供给)（§5.3）；
///   <see cref="FileSystemOptions.Label"/> = Open 校验（不符即抛 fail-fast）。</para>
/// </summary>
[MediumOptions("virtual", Verbs = "Open")]
public sealed class TierVolumeOpenOptions : FileSystemOptions
{
    /// <summary>
    /// 数据页缓存预算上限（字节，默认 64 MiB——§3.4 自管页缓存；0 = 禁用读缓存
    /// （直达档行为——大扫描不冲刷内存））。
    /// </summary>
    public long PageCacheBytes { get; init; } = 64L << 20;

    /// <summary>降级打开（RM-04 v2b）：多载体卷允许成员缺失——只读形态（写/变异全拒）；
    /// 数据落在缺失成员上的读抛 <see cref="IOError.IOFailure"/>（诚实——洞数据不可伪造）。</summary>
    public bool AllowDegraded { get; init; }

    /// <summary>
    /// 载体预分配方式（IS-02——Open 侧须与格式化档一致：Full 档跳过载体稀疏标记，
    /// 避免把 full 载体静默降回稀疏语义；Metadata = 现行稀疏标记）。
    /// </summary>
    public PreallocationMode Preallocation { get; init; } = PreallocationMode.Metadata;

    /// <summary>
    /// 载体句柄写穿档（IS-03，默认 false——Open 侧须与格式化档一致）：
    /// 载体以 FILE_FLAG_WRITE_THROUGH/O_SYNC 打开，journal 提交免独立 fsync（写穿完成即单屏障）。
    /// </summary>
    public bool CarrierWriteThrough { get; init; }

    /// <summary>
    /// 同文件写并发档（V2 §2.1，默认 <see cref="WriteConcurrencyMode.Serial"/>——现状行为；
    /// 与 <see cref="TierVolumeFormatOptions.WriteConcurrency"/> 同语义——Open 侧按挂载意图覆写）。
    /// </summary>
    public WriteConcurrencyMode WriteConcurrency { get; init; } = WriteConcurrencyMode.Serial;

    /// <summary>
    /// 快照挂载（V2 §1.1——快照 = 可挂载的一致存档点）：非空 = 以该快照只读挂载卷
    /// （快照镜像 + 冻结位图；与 <see cref="FileSystemOptions.Access"/> 必须组合 Read——
    /// 变异全拒 ReadOnlyVolume 语义；与活卷同载体并发安全——冻结块永不复用/打洞）。
    /// </summary>
    public string? SnapshotName { get; init; }
}
