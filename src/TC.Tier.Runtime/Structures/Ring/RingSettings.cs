using TC.Tier.Core.Primitives;
using TC.Tier.Core.Primitives;

namespace TC.Tier.Runtime.Structures.Ring;

/// <summary>
/// Ring 基类配置（abstract，{ get; init; } 不可变，对齐 LogSettings/BlobSettings）。
/// <para>★ 心智模型（engine-migration-rewrite.md §0/§1）：IO 底层 = 完全屏蔽复杂操作的持久化内存。
/// Ring 不碰段/对齐/落盘细节，只在引擎地址空间上做页池几何（mutable→readonly→flushed→evicted 状态机）+ 环形驱逐。</para>
/// <para>★ 通用设备字段（RootDirectory/DeviceName/SegmentSize/DeleteOnClose/DirectIoMode/PersistenceMode/RecoverDevice/Engine）
/// 对齐 LogSettings 同名字段——4 基类统一新引擎模型。Ring 专属字段（PageSize/MemorySize/MaxPageCount/MutableFraction/
/// Preallocate/OverflowPolicy/MinOverflowSize）是 Ring 独有。</para>
/// <para>★ PageCount 是派生量（= MemorySize / PageSize），不在 Settings 里直接配。</para>
/// <para>参见 base.md §2.8。</para>
/// </summary>
public abstract class RingSettings : Settings
{
    /// <summary>Ring 缺省 opaque 容量 256B（基类 MetaOpaqueBytes 的结构级缺省；初始化器可覆盖）。</summary>
    protected RingSettings(string name = "tc.ring")
        : base(name, segmentGrowthLimit: AlignmentConst.Alignment1G) => MetaOpaqueBytes = 256;

    /// <summary>引擎选项直构（对齐 LogSettings 双 ctor 形态——测试/组合根经 StorageEngineOptions 装配）。</summary>
    protected RingSettings(StorageEngineOptions mainEngine) : base(mainEngine) => MetaOpaqueBytes = 256;

    // === Ring 专属字段（页池几何 + mutable 区）===
    /// <summary>页大小（字节）。默认 AlignmentConst.Alignment32M。校验：2 的幂 / [4KB, 1GB]。</summary>
    public int PageSize { get; init; } = AlignmentConst.Alignment32M;

    /// <summary>总内存容量（字节）。默认 AlignmentConst.Alignment16G。校验：>= PageSize 且整除为 2 的幂页数。</summary>
    public long MemorySize { get; init; } = AlignmentConst.Alignment16G;

    /// <summary>页数上界。默认 8192。</summary>
    public int MaxPageCount { get; init; } = 8192;

    /// <summary>mutable 区占比 (0,1)。默认 0.9。</summary>
    public double MutableFraction { get; init; } = 0.9;

    /// <summary>构造时全量预分配所有页？默认 false（懒分配）。</summary>
    public bool Preallocate { get; init; }

    // === 溢出配置（WiscKey 式 KV 分离，默认关闭）===
    /// <summary>溢出策略。Disabled（默认）= Value 内联；Enabled = 超 MinOverflowSize 的 Value 分离到溢出引擎。</summary>
    public OverflowPolicy OverflowPolicy { get; init; } = OverflowPolicy.Disabled;

    /// <summary>Value 溢出阈值（字节）。默认 0（Enabled 时全部溢出）。</summary>
    public int MinOverflowSize { get; init; }

    // === 冷读回源配置 ===
    /// <summary>冷页缓存容量。null=按 ColdReadRatio 派生（默认）。
    /// 显式设置则覆盖 ratio，直接作为 ClockCache 槽位数（向上取整到 2 的幂）。</summary>
    public int? ClockCacheCapacity { get; init; }

    /// <summary>冷回源比例 [0.0, 1.0]。控制 ClockCache 容量占 PageCount 的比例。
    /// 0.0=不缓存（每次冷读走部分页回源，省内存）；
    /// 0.25=当前默认(PageCount/4)；
    /// 1.0=缓存全部页（近乎全热）。</summary>
    public double ColdReadRatio { get; init; } = 0.25;

    /// <summary>单条 record 部分页回源的 buffer 上限（字节）。
    /// record 的 HeaderSize+PayloadLength 超过此值时，回退到 LoadColdPage（整页路径）。
    /// 默认 1MB。</summary>
    public int ColdRecordBufferLimit { get; init; } = AlignmentConst.Alignment1M;
}