namespace TC.Tier.Runtime.Structures.SortedIndex;

public sealed class BTreeIndexSettings : SortedIndexSettings
{
    /// <summary>引擎选项直构（对齐 BlittableRingSettings/EntryLogSettings 双 ctor 形态）。</summary>
    public BTreeIndexSettings(StorageEngineOptions mainEngine) : base(mainEngine) { }

    public int NodeSize { get; init; } = 256;
    public int MinFillPercent { get; init; } = 50;
}
