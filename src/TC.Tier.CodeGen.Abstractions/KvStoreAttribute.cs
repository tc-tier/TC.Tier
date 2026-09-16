namespace TC.Tier.CodeGen;

/// <summary>
/// TierKv 封闭注册标注（tierkv-design.md §0.1/§4 W1——开放泛型产品类的封闭消费面，[RingKey] 同款先例）。
/// <para>★ 消费方一行声明 <c>[assembly: KvStore(typeof(OrderKey), typeof(Position))]</c> → 生成器产出
///   封闭形态 <c>TierKvOfOrderKeyPosition : TierKv&lt;OrderKey, Position&gt;</c>：ctor 转发 + CreateAsync
///   工厂（默认装配 + 生命周期一步到位）+ 内嵌派生 Ring/索引实现（派生肢获得构造权，开放泛型零构造）；
///   formatter 按 TValue 自动发射（byte/byte[]/ROM&lt;byte&gt;/unmanaged 零代码，D6/D7 裁定）——
///   其余 TValue 必须显式 <see cref="Formatter"/>（编译期校验，缺失 = 编译期报错非运行时炸）。</para>
/// <para>★ 主索引两层并存（§0.1）：<see cref="IndexKind"/> = 编译期缺省声明（生成器发射 DefaultOptions）；
///   运行经 TierKvOptions.IndexKind 覆盖（显式胜出）——CreateAsync 全三族索引实现齐备可路由。</para>
/// <para>★ TKey 约束校验：不满足 unmanaged → 生成器编译期报错；IEquatable&lt;TKey&gt; 缺失由生成物
///   CS0314 兜底（[RingKey] 同款）。</para>
/// </summary>
/// <param name="keyType">键类型（须 unmanaged + IEquatable&lt;TKey&gt;）。</param>
/// <param name="valueType">值类型（无约束——内建覆盖面之外须 <see cref="Formatter"/>）。</param>
[System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class KvStoreAttribute(System.Type keyType, System.Type valueType) : System.Attribute
{
    /// <summary>键类型。</summary>
    public System.Type KeyType { get; } = keyType;

    /// <summary>值类型。</summary>
    public System.Type ValueType { get; } = valueType;

    /// <summary>
    /// 主索引编译期缺省（TierKvOptions.IndexKind 的整数值——0=Hash/1=BTree/2=SkipList；
    /// int 直传保持 Abstractions 程序集零依赖——消费面写 <c>(int)KvIndexKind.BTree</c>）。
    /// 运行 TierKvOptions.IndexKind 覆盖标注缺省（显式胜出）。
    /// </summary>
    public int IndexKind { get; set; }

    /// <summary>
    /// 值格式化器（TValue 在内建覆盖面之外时必填——须实现 IValueFormatter&lt;TValue&gt;，
    /// 生成器编译期校验接口实现；内建覆盖面（byte/byte[]/ROM&lt;byte&gt;/unmanaged）可不填。
    /// </summary>
    public System.Type? Formatter { get; set; }
}
