using System.Runtime.InteropServices;
using TC.Tier.CodeGen;

namespace TC.Tier.Runtime.Structures.SortedIndex.Layout;

/// <summary>
/// SkipList dump 帧几何块（32B，跟在帧头之后）：塔层级 + 条目数 + 塔顶锚点地址
/// （LogicalAddress 摊平三字段——跨程序集嵌套布局判例；末字段恰收口 32B）。
/// <para>★ 读写唯一路径 = 生成 Codec——原手写偏移 + MemoryMarshal 内存重解释退役。</para>
/// </summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = SortedIndexConstants.GeometrySize)]
internal struct SkipListIndexGeometry
{
    /// <summary>当前塔层级（≥1，≤ MaxLevel）。</summary>
    [FieldOffset(0)] internal int CurrentLevel;

    /// <summary>条目数（dump 瞬时——fuzzy 帧由层 0 链实收重数校正）。</summary>
    [FieldOffset(8)] internal long EntryCount;

    /// <summary>塔顶锚点 SegId。</summary>
    [FieldOffset(16)] internal int HeadSegId;

    /// <summary>塔顶锚点 Extension。</summary>
    [FieldOffset(20)] internal int HeadExtension;

    /// <summary>塔顶锚点 Offset。</summary>
    [FieldOffset(24)] internal long HeadOffset;
}
