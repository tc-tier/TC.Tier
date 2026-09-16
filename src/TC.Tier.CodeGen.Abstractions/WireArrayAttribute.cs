namespace TC.Tier.CodeGen;

/// <summary>
/// 标记 struct 为变长集合线编码（spec-12 §10 E1——<c>[Count 4B][item×N]</c>：
/// 基元整型 / 标注 [BinaryLayout] struct 项 / byte[] 字节块）。
/// <para>★ 布局 = 字段声明序：非数组字段构成定长前缀（声明顺序即线顺序——加字段 = 加一行声明，
///   位置由生成物累积，零偏移知识）；恰一个数组字段承载变长区，永远在前缀之后。
///   构造函数参数序 = （前缀字段声明序 + 数组字段）。</para>
/// <para>★ 生成 <c>XxxCodec.Encode(Span&lt;byte&gt;, in T) → int</c>（返回写入长度）与
///   <c>TryDecode(ReadOnlySpan&lt;byte&gt;, out T) → bool</c>——计数、位置、字节序全部生成物，
///   消费层零手写字节序与偏移。</para>
/// <para>★ 嵌套项（标注 [BinaryLayout] struct）的项大小经其生成的 <c>StructSize</c> 获得——
///   嵌套元素布局须开 <see cref="BinaryLayoutFeatures.StructSize"/>。</para>
/// <para>★ 防御上限显式声明：Count &gt; <see cref="MaxCount"/> = TryDecode false（畸形报文拦截）。</para>
/// </summary>
[System.AttributeUsage(System.AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class WireArrayAttribute : System.Attribute
{
    /// <summary>防御上限（项数/字节数超此值 = TryDecode false——有界集合契约，必须 &gt; 0）。</summary>
    public int MaxCount { get; set; }
}
