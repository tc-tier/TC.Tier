namespace TC.Tier.Runtime.Structures.SortedIndex;

/// <summary>
/// SkipListIndex 跳表索引设置——引擎选项直构（继承 SortedIndexSettings）+ 层几何/回收策略。
/// </summary>
public sealed class SkipListIndexSettings : SortedIndexSettings
{
    /// <summary>引擎选项直构（对齐 BlittableRingSettings/EntryLogSettings 双 ctor 形态）。</summary>
    public SkipListIndexSettings(StorageEngineOptions mainEngine) : base(mainEngine) { }

    /// <summary>塔最大层数（head 哨兵按满层建；几何层分配 p=1/2 封顶此值）。</summary>
    public int MaxLevel { get; init; } = 16;

    /// <summary>高层缓存阈值——层数 ≥ 此值的节点全量内存缓存（塔高层稀疏，缓存代价低）。</summary>
    public int HighLevelCacheThreshold { get; init; } = 8;

    /// <summary>CAS 插入最大重试次数（塔链竞争失败重试上限）。</summary>
    public int MaxRetryCount { get; init; } = 3;

    /// <summary>物理回收延迟（毫秒——等 epoch 退出后回收被删节点）。</summary>
    public long SafeReclaimDelayMs { get; init; } = 1000;
}
