namespace TC.Tier.Runtime.Structures.Snapshot;

/// <summary>
/// SnapshotBase 配置基类。
/// <para>★ 引擎配置走 <see cref="Settings.MainEngine"/>（StorageEngineOptions，构造 = 配置）——
///   snapshot 是 GB/TB 追加流，便捷构造默认**分段开启**（大数据跨段扩容，与 Metadata/Mirror 单段不同）。</para>
/// </summary>
public abstract class SnapshotSettings : Settings
{
    /// <summary>完整构造——注入主引擎选项（GB/TB 追加流建议分段开启）。</summary>
    public SnapshotSettings(StorageEngineOptions mainEngine) : base(mainEngine)
    {
    }

    /// <summary>双缓冲会话的 buffer 大小（默认 128KB，按扇区对齐取整生效）。</summary>
    public int SessionBufferSize { get; init; } = 128 * 1024;


    // === meta 持久化配置 ===
}

/// <summary>StreamSnapshot 专属配置——流式帧几何。</summary>
public sealed class StreamSnapshotSettings : SnapshotSettings
{
    /// <summary>完整构造——注入主引擎选项。</summary>
    public StreamSnapshotSettings(StorageEngineOptions mainEngine) : base(mainEngine)
    {
    }

    /// <summary>便捷构造——默认分段开启（GB/TB 追加流跨段扩容）+ 稀疏按需增长。</summary>
    public StreamSnapshotSettings()
        : base(new StorageEngineOptions("tc.snapshot", 256L * 1024 * 1024,
            enableSegmentation: true, preallocateFile: false))
    {
    }
}
