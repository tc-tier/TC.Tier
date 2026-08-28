namespace TC.Tier.Runtime.Structures.Metadata;

/// <summary>
/// MetadataBase 配置基类。
/// <para>★ 引擎配置走 <see cref="Settings.MainEngine"/>（StorageEngineOptions，构造 = 配置）：
///   Metadata 版本链是单段追加流——便捷构造默认 enableSegmentation=false + 稀疏按需增长；
///   需自定义段几何/hints/清理策略时经 <see cref="MetadataSettings(StorageEngineOptions)"/> 注入。</para>
/// </summary>
public abstract class MetadataSettings : Settings
{
    /// <summary>Metadata 版本链默认段增长上限（16MB——元数据是结构体级小数据）。</summary>
    public const long DefaultSegmentGrowthLimit = 16L * 1024 * 1024;

    /// <summary>完整构造——注入主引擎选项（自定义段几何/hints/清理策略）。</summary>
    public MetadataSettings(StorageEngineOptions mainEngine) : base(mainEngine)
    {
    }

    /// <summary>便捷构造——引擎名单段模式（版本链追加流），稀疏按需增长，上限 <see cref="DefaultSegmentGrowthLimit"/>。</summary>
    public MetadataSettings(string name = "tc.metadata")
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

    /// <summary>便捷构造——默认单段稀疏（见 <see cref="MetadataSettings(string)"/>）。</summary>
    public VersionedMetadataSettings()
    {
    }

    /// <summary>元数据结构体字节数（调用方写入的 Payload 大小，自动向上对齐到扇区）。
    /// <para>★ 冷热分离（设计决策）：本配置只决定<b>本次运行</b>的版本几何——Write/Prepare 追加的
    ///   新版本 record 大小。恢复载入的历史版本按其盘上真实大小<b>完整交付</b>（不补零、不截断），
    ///   跨重启改大小合法（版本链混尺寸由各 record 头部自述几何支撑）。</para></summary>
    public int PayloadSize { get; init; }
}
