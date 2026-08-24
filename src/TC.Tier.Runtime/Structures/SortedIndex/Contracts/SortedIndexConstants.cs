using TC.Tier.Contracts.Layout;

namespace TC.Tier.Runtime.Structures.SortedIndex.Contracts;

/// <summary>
/// SortedIndex 族别常量（Kind 字段值——体几何由族自描述；比较族两结构同族同 kind）。
/// </summary>
public static class SortedIndexConstants
{
    /// <summary>体几何块尺寸（比较族几何块 32B——BTree/SkipList 一致）。</summary>
    public const int GeometrySize = 32;

    /// <summary>族别（Kind 字段值——体几何由族自描述；比较族两结构同族同 kind）。</summary>
    public const ushort KindSorted = 0;

    /// <summary>规范帧字段（两结构同式——flags 默认值）。</summary>
    public const ushort DefaultFlags = RecordFlags.FLAG_CRC64
                                     | RecordFlags.FLAG_FOOTER_MAGIC;
}
