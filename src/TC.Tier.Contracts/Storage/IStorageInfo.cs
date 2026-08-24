namespace TC.Tier.Contracts.Storage;

/// <summary>
/// 存储信息视图——只承载"描述存储引擎能力与配置"的只读属性 + 路径解析方法，不承担任何 IO 操作。
/// <para>★ 定位原则：本接口是 <see cref="IStorageEngine"/> 的纯信息子集——
///   "引擎是什么样、配置如何、段在哪"（声明性/配置性信息）。
///   <see cref="IStorageEngine"/> 在此基础上追加"对设备执行 IO"的行为（Append/Read/Write/Flush 等）
///   以及这些行为产出的动态水位（AllocatedTail/CommittedTail 等）。</para>
/// <para>★ 介质/绝对路径/容量不在其列：介质 = 注入引擎的根空间文件系统（Runtime 层 IFileSystem），
///   引擎只持相对路径（<see cref="EngineName"/> 即根空间下子目录）；容量归 fs 卷自身。
///   零实现纪律：本层不引用 Core——DIO 探测结果（Core IO 四态枚举）由 Runtime 引擎类型自身属性
///   <c>StorageEngine.UnbufferedSupport</c> 暴露，不进契约面。</para>
/// </summary>
public interface IStorageInfo
{
    // === 定位 ===

    /// <summary>引擎名（= 根空间下子目录路径，可多级 <c>"a/b"</c>，'/' 唯一分隔符）。
    /// <para>段文件名前缀等均派生自此。</para></summary>
    string EngineName { get; }

    // === 规模配置 ===

    /// <summary>活跃段生长上限（字节）；段真实大小见地址表 RealSize。
    /// <para>★ 决定单段最大字节数，跨段时按此切分。</para></summary>
    long SegmentGrowthLimit { get; }

    // === IO 能力/模式 ===

    /// <summary>卷扇区大小（字节），I/O 对齐计算的基准（来自注入根空间的卷几何）。</summary>
    uint SectorSize { get; }

    // === 分段/分配策略 ===

    /// <summary>是否启用分段模式（true = 每段独立文件，false = 单文件平铺）。
    /// <para>★ 决定 <see cref="SegmentFileName"/> 的路径格式。</para></summary>
    bool EnableSegmentation { get; }

    /// <summary>新段创建时是否真实预分配（构造固定，生命周期不变）。</summary>
    bool PreallocateFile { get; }

    // === 路径解析（纯方法，无 IO）===

    /// <summary>
    /// 解析指定段号的段文件路径（★ 根空间下相对路径，'/' 唯一分隔符）。
    /// <para>★ 多段模式 = <c>{engine}/{engine}.{segId}</c>；单段模式 = <c>{engine}/{engine}</c>（忽略 segId）。
    ///   引擎名可多级（<c>"a/b"</c>）。</para>
    /// <para>★ 子系统（Compact/Checkpoint）必须用此方法定位段文件，禁止自己拼路径——
    ///   单段/多段差异由实现保证，避免子系统各自猜测导致路径错位。</para></summary>
    /// <param name="segId">段号。</param>
    /// <returns>段文件相对路径。</returns>
    string SegmentFileName(int segId);
}
