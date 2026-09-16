namespace TC.Tier.Runtime.Structures.Metadata;

/// <summary>
/// MetadataBase 配置基类。
/// <para>★ 引擎配置走 <see cref="Settings.MainEngine"/>（StorageEngineOptions，构造 = 配置）：
///   Metadata 版本链是单段追加流——便捷构造默认 enableSegmentation=false + 稀疏按需增长；
///   需自定义段几何/hints/清理策略时经 <see cref="MetadataSettings"/> 注入。</para>
/// </summary>
public abstract class MetadataSettings : Settings
{
    /// <summary>Metadata 版本链默认段增长上限（16MB——元数据是结构体级小数据）。</summary>
    private const long DefaultSegmentGrowthLimit = 16L * 1024 * 1024;

    /// <summary>完整构造——注入主引擎选项（自定义段几何/hints/清理策略）。</summary>
    protected MetadataSettings(StorageEngineOptions mainEngine) : base(mainEngine)
    {
    }

    /// <summary>便捷构造——引擎名单段模式（版本链追加流），稀疏按需增长，上限 <see cref="DefaultSegmentGrowthLimit"/>。</summary>
    protected MetadataSettings(string name = "tc.metadata")
        : base(new StorageEngineOptions(name, DefaultSegmentGrowthLimit,
            enableSegmentation: false, preallocateFile: false))
    {
    }


    // === meta 持久化配置 ===

    /// <summary>内存多版本保留窗口（Abort 零 IO 底线 N≥2，可配更高支持 MVCC）。默认 2。</summary>
    public int MaxMemoryVersions { get; init; } = 2;
}

/// <summary>VersionedMetadata 专属配置——指定元数据结构体大小。</summary>
public sealed class VersionedMetadataSettings : MetadataSettings
{
    /// <summary>完整构造——注入主引擎选项。</summary>
    public VersionedMetadataSettings(StorageEngineOptions mainEngine) : base(mainEngine)
    {
    }

    /// <summary>便捷构造——默认单段稀疏（见 <see cref="MetadataSettings"/>）。</summary>
    public VersionedMetadataSettings()
    {
    }

    /// <summary>元数据结构体字节数（调用方写入的 Payload 大小，自动向上对齐到扇区）。
    /// <para>★ 冷热分离（设计决策）：本配置只决定<b>本次运行</b>的版本几何——Write/Prepare 追加的
    ///   新版本 record 大小。恢复载入的历史版本按其盘上真实大小<b>完整交付</b>（不补零、不截断），
    ///   跨重启改大小合法（版本链混尺寸由各 record 头部自述几何支撑）。</para></summary>
    public int PayloadSize { get; init; }

    /// <summary>变长 payload 写入上限（字节）。null = 固定档（行为与 PayloadSize 单配置完全一致——
    /// Write 截断/补零到 PayloadSize）；非 null 且 &gt; PayloadSize = 变长档：Write 允许 ≤ 上限的任意
    /// 长度（热区按需增长、版本 record 按实际长度落盘），超上限抛（fail-fast——分段/扩容归调用方）。
    /// <para>★ 消费形态：keyed 分区元数据（如 TimeSeries dense 水位块——n 随序列增长，每次 Write
    ///   的块长动态）。固定档消费者（Queue/Blob 面零感知）行为逐字节不变。</para></summary>
    public int? MaxPayloadSize { get; init; }
}
