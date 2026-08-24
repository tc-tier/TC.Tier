namespace TC.Tier.CodeGen;

/// <summary>
/// KV Key 特化封闭注册标注（ring-generic-key-and-index-split 设计稿 §2）。
/// <para>★ 消费方一行声明 <c>[assembly: RingKey(typeof(OrderId))]</c> → 生成器产出<b>全套</b>封闭薄类：
///   RingOfOrderId : BlittableRing&lt;OrderId&gt; + HashOfOrderId : HashIndex&lt;OrderId&gt; +
///   BTreeOfOrderId/SkipListOfOrderId（ctor 转发 + Create 工厂一步生命周期）。
///   开放泛型内核手写一次，封闭形态生成器产出——开放泛型不落消费面，无反射 MakeGenericType（AOT 干净）。</para>
/// <para>★ Type 形态（CodeGen.Abstractions 标注先例）；Key 不满足 unmanaged 约束生成器<b>编译期报错</b>
///   （非运行时炸）；IEquatable&lt;TKey&gt; 缺失由生成物的 CS0314 兜底。</para>
/// </summary>
/// <param name="keyType">Key 类型（须 unmanaged + IEquatable&lt;TKey&gt;）。</param>
[System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class RingKeyAttribute(System.Type keyType) : System.Attribute
{
    /// <summary>Key 类型。</summary>
    public System.Type KeyType { get; } = keyType;
}
