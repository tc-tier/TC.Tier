namespace TC.Tier.CodeGen;

/// <summary>
/// 标记抽象基类为线消息族根（spec-12 §10 E2——消息族编码：<c>[Tag 1B][根字段声明序][子类字段声明序]</c>）。
/// <para>★ 族根声明公共前缀字段（全体子类共享）；直接派生子类各标 <see cref="WireMessageTagAttribute"/>
///   携带 tag。生成 <c>XxxCodec.Encode(msg) → byte[]</c> / <c>TryDecode(ReadOnlySpan&lt;byte&gt;, out msg) → bool</c>
///   与 tag 常量（<c>Tag子类名</c>）——tag 判别、公共前缀、字段序、字节序全部生成物。</para>
/// <para>★ 成员支持：bool/基元整型（小端）、标注 [BinaryLayout] struct（嵌套件）、
///   byte[]/ReadOnlyMemory&lt;byte&gt;（blob：<c>[Len 4B][bytes]</c>）、数组/列表 of 上述与
///   ReadOnlyMemory&lt;byte&gt;（<c>[Count 4B][item×N]</c>，blob 项为 <c>[Len 4B][bytes]</c>）。</para>
/// <para>★ 未知 tag / 截断 / Count 超上限 = TryDecode false（畸形报文拦截；变长成员上限经
///   <see cref="WireMemberAttribute"/> 显式声明）。</para>
/// </summary>
[System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class WireMessageAttribute : System.Attribute
{
}

/// <summary>消息族子类的 tag 标注（族内唯一——重复值编译期 TCSG050 Error）。</summary>
/// <param name="tag">消息 tag（线首字节——族内唯一，重复值编译期 TCSG050 Error）。</param>
[System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class WireMessageTagAttribute(byte tag) : System.Attribute
{
    /// <summary>消息 tag（线首字节）。</summary>
    public byte Tag { get; } = tag;
}

/// <summary>变长成员的防御上限显式声明（缺此标注的变长成员报 TCSG052）。
/// 两种实参形态等价：<c>[WireMember(8)]</c>（位置）/ <c>[WireMember(MaxCount = 8)]</c>（命名——等号只绑可写属性）。</summary>
[System.AttributeUsage(System.AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class WireMemberAttribute : System.Attribute
{
    /// <summary>命名实参形态（无参构造 + 属性赋值）。</summary>
    public WireMemberAttribute()
    {
    }

    /// <summary>位置实参形态。</summary>
    /// <param name="maxCount">项数上限。</param>
    public WireMemberAttribute(int maxCount)
    {
        MaxCount = maxCount;
    }

    /// <summary>项数上限（Count 超此值 = TryDecode false——畸形报文拦截）。</summary>
    public int MaxCount { get; set; }
}
