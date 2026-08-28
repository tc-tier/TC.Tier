namespace TC.Tier.Runtime.Structures.Snapshot;

/// <summary>IncrementalSnapshot 专属配置——段累积合并阈值（opaque 段表容量走基类 MetaOpaqueBytes，调用方显式配置）。</summary>
public sealed class IncrementalSnapshotSettings : SnapshotSettings
{
    /// <summary>完整构造——注入主引擎选项（GB/TB 追加流建议分段开启）。</summary>
    public IncrementalSnapshotSettings(StorageEngineOptions mainEngine) : base(mainEngine)
    {
    }

    /// <summary>便捷构造——默认分段开启。</summary>
    public IncrementalSnapshotSettings()
        : base(new StorageEngineOptions("tc.snapshot.inc", 256L * 1024 * 1024,
            enableSegmentation: true, preallocateFile: false))
    {
    }

    /// <summary>
    /// ★ 段累积合并阈值：段数达到此值触发 CompactSegments（合并 = 全量重写一次——
    /// 低频；raft 只需最新快照——段累积不无限增长）。
    /// </summary>
    public int CompactSegmentThreshold { get; init; } = 8;
}
