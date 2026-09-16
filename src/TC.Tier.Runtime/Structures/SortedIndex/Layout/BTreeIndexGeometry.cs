using System.Runtime.InteropServices;
using TC.Tier.CodeGen;

namespace TC.Tier.Runtime.Structures.SortedIndex.Layout;

/// <summary>
/// BTree dump 帧几何块（32B，跟在帧头之后）：根地址（LogicalAddress 摊平三字段——跨程序集
/// 嵌套布局判例）+ 条目数 + 节点结构尺寸（ TKey 布局自检凭据）+ 保留 4B 收口。
/// <para>★ 读写唯一路径 = 生成 Codec（<see cref="BTreeIndexGeometryCodec"/>）——原手写偏移 +
/// MemoryMarshal 内存重解释退役（LE 平台假设拆除）。</para>
/// </summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = SortedIndexConstants.GeometrySize)]
internal struct BTreeIndexGeometry
{
    /// <summary>根地址 SegId。</summary>
    [FieldOffset(0)] internal int RootSegId;

    /// <summary>根地址 Extension。</summary>
    [FieldOffset(4)] internal int RootExtension;

    /// <summary>根地址 Offset。</summary>
    [FieldOffset(8)] internal long RootOffset;

    /// <summary>条目数（dump 瞬时——fuzzy 帧由实收重数校正）。</summary>
    [FieldOffset(16)] internal long EntryCount;

    /// <summary>BTreeNode 结构尺寸（TKey 布局自检——不符 = 别的流，fail-safe 全量重放）。</summary>
    [FieldOffset(24)] internal int NodeStructSize;

    /// <summary>保留收口（几何块族契约 = 32B）。</summary>
    [FieldOffset(28)] internal int Reserved;
}
