namespace TC.Tier.Contracts.Serialization;

/// <summary>
/// Span 序列化器接口——TValue 类型化的非 blittable 档（tierkv-orchestration-design.md §2）。
/// <para>★ 通用对象序列化：调用方注入实现，TierKvValue&lt;T&gt; 经此将 T 序列化为字节流写入 Ring、
///   读回时反序列化。与 blittable 直拷档互补（blittable 走 <c>MemoryMarshal.Read/Write</c> 零分配快路径）。</para>
/// <para>★ 契约：<see cref="GetMaxSize"/> 返回值上界（TierKvValue 用于预分配缓冲区）；
///   <see cref="Write"/> 必须写入 ≤ <see cref="GetMaxSize"/> 字节并返回实际写入长度；
///   <see cref="Read"/> 从源 span 反序列化（长度由调用方传入实际 record value 长度）。</para>
/// <para>★ [TierValue] 源生成器（设计稿第三档——独立一笔）将为此接口自动发射实现：
///   attribute 声明 → 生成器产出 <c>XxxSerializer : ISpanSerializer&lt;Xxx&gt;</c>。</para>
/// </summary>
/// <typeparam name="T">值类型（非 blittable 也支持——引用类型/含引用字段的结构）。</typeparam>
public interface ISpanSerializer<T> where T : notnull
{
    /// <summary>值的最大序列化字节数（上界——用于预分配缓冲区）。</summary>
    int GetMaxSize();

    /// <summary>序列化 value 到 destination（写入 ≤ <see cref="GetMaxSize"/> 字节）。</summary>
    /// <returns>实际写入字节数。</returns>
    int Write(Span<byte> destination, in T value);

    /// <summary>从 source 反序列化为 T（source 长度 = record 实际 value 长度）。</summary>
    T Read(ReadOnlySpan<byte> source);
}
