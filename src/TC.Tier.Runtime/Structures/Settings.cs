namespace TC.Tier.Runtime.Structures;

/// <summary>
/// 设置基类——提供通用配置项和主存储引擎选项访问。
/// </summary>
public abstract class Settings(StorageEngineOptions mainEngine)
{
    /// <summary>
    /// 主存储引擎选项（供子类访问和配置）。
    /// </summary>
    /// <param name="name">引擎名</param>
    /// <param name="segmentGrowthLimit">段增长上限（单位字节）</param>
    /// <param name="enableSegmentation">是否启用段分段</param>
    /// <param name="preallocateFile">是否预分配文件空间</param>
    /// <param name="deleteOnClose">是否在关闭时删除存储引擎</param>
    protected Settings(string name, long segmentGrowthLimit = 256L * 1024 * 1024,
        bool enableSegmentation = true, bool preallocateFile = true, bool deleteOnClose = false) : this(
        new StorageEngineOptions(name, segmentGrowthLimit, enableSegmentation, preallocateFile, deleteOnClose))
    {
    }

    /// <summary>
    /// 存储引擎名称。
    /// </summary>
    public string Name { get; } = mainEngine.EngineName;

    /// <summary>
    /// 是否预分配文件空间（true：创建文件时预分配指定大小，false：按需增长文件大小）。
    /// </summary>
    public bool PreallocateFile => mainEngine.PreallocateFile;

    /// <summary>
    /// 是否在关闭时删除存储引擎（true：关闭时删除所有文件，false：保留文件）。
    /// </summary>
    public bool DeleteOnClose => mainEngine.DeleteOnClose;

    // === meta 持久化（全部结构统一公共配置）===

    /// <summary>meta 持久化策略类型。默认 Disabled（结构构造期按 Kind 装配；DeltaLogSettings 缺省 Transport）。</summary>
    public MetaPolicyKind MetaPolicyKind { get; init; } = MetaPolicyKind.Disabled;

    /// <summary>外部可写入 Meta opaque 区的容量（字节）。启动后不可改，重启可调整。
    /// <para>★ 只做<b>写侧</b>约束（写入上限 + buffer/引擎段几何）——不参与盘上布局：
    ///   meta 块四段自描述（[统一头][水位][opaque 实际用量][统一尾]），footer/CRC 偏移
    ///   全由 header.PayloadLength（水位+实际 opaque）推出，容量零参与盘上几何。</para></summary>
    public int MetaOpaqueBytes { get; init; }

    /// <summary>
    /// 主存储引擎选项（供子类访问和配置）。
    /// </summary>
    public StorageEngineOptions MainEngine => mainEngine;
}