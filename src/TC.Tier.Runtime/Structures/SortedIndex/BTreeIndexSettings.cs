namespace TC.Tier.Runtime.Structures.SortedIndex;

/// <summary>
/// BTreeIndex B+树索引设置——引擎选项直构（继承 SortedIndexSettings）+ 节点容量/填充率。
/// </summary>
public sealed class BTreeIndexSettings : SortedIndexSettings
{
    /// <summary>引擎选项直构（对齐 BlittableRingSettings/EntryLogSettings 双 ctor 形态）。</summary>
    public BTreeIndexSettings(StorageEngineOptions mainEngine) : base(mainEngine) { }

    /// <summary>节点大小（字节——引擎节点分配下限；实际取 max(结构体大小, 256, 本值)）。</summary>
    public int NodeSize { get; init; } = 256;

    /// <summary>最小填充率（百分比——节点合并触发阈值）。</summary>
    public int MinFillPercent { get; init; } = 50;
}
