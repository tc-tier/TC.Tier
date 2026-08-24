namespace TC.Tier.Runtime.Structures.SortedIndex;

public sealed class SkipListIndexSettings : SortedIndexSettings
{
    /// <summary>引擎选项直构（对齐 BlittableRingSettings/EntryLogSettings 双 ctor 形态）。</summary>
    public SkipListIndexSettings(StorageEngineOptions mainEngine) : base(mainEngine) { }

    public int MaxLevel { get; init; } = 16;
    public int HighLevelCacheThreshold { get; init; } = 8;
    public int MaxRetryCount { get; init; } = 3;
    public long SafeReclaimDelayMs { get; init; } = 1000;
}
