namespace TC.Tier.Runtime.Structures.Mirror;

/// <summary>
/// MirrorBase 配置基类。
/// <para>★ 引擎配置走 <see cref="Settings.MainEngine"/>（StorageEngineOptions，构造 = 配置）：
///   Mirror 版本链是单段追加流——便捷构造默认 enableSegmentation=false + 稀疏按需增长。</para>
/// <para>★ N=2 轮替是固定策略（保留当前+上一 checkpoint，更老即收），不设配置项（spec §2.7）。</para>
/// </summary>
public abstract class MirrorSettings : Settings
{
    /// <summary>Mirror 版本链默认段增长上限（64MB——checkpoint 镜像中等体量，2 倍空间上界可控）。</summary>
    public const long DefaultSegmentGrowthLimit = 64L * 1024 * 1024;

    /// <summary>完整构造——注入主引擎选项（自定义段几何/hints/清理策略）。</summary>
    public MirrorSettings(StorageEngineOptions mainEngine) : base(mainEngine)
    {
    }

    /// <summary>便捷构造——引擎名单段模式（版本链追加流），稀疏按需增长。</summary>
    public MirrorSettings(string name = "tc.mirror")
        : base(new StorageEngineOptions(name, DefaultSegmentGrowthLimit,
            enableSegmentation: false, preallocateFile: false))
    {
    }

    // === meta 持久化配置 ===
}

/// <summary>WholeMirror 专属配置（整体镜像，v2 流式帧——尺寸写尾时才知，无需预告）。</summary>
public sealed class WholeMirrorSettings : MirrorSettings
{
    /// <summary>完整构造——注入主引擎选项。</summary>
    public WholeMirrorSettings(StorageEngineOptions mainEngine) : base(mainEngine)
    {
    }

    /// <summary>便捷构造——默认单段稀疏。</summary>
    public WholeMirrorSettings()
    {
    }
}

/// <summary>PagedMirror 专属配置——页几何（每页一个 record，PageSize = 1 &lt;&lt; LogPageSizeBits）。</summary>
public sealed class PagedMirrorSettings : MirrorSettings
{
    /// <summary>完整构造——注入主引擎选项。</summary>
    public PagedMirrorSettings(StorageEngineOptions mainEngine) : base(mainEngine)
    {
    }

    /// <summary>便捷构造——默认单段稀疏。</summary>
    public PagedMirrorSettings()
    {
    }

    /// <summary>页大小位宽（PageSize = 1 &lt;&lt; LogPageSizeBits，源结构页对齐）。默认 22（4MB）。</summary>
    public int LogPageSizeBits { get; init; } = 22;
}
