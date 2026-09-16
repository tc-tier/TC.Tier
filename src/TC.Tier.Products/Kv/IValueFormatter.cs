using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TC.Tier.Products.Kv;

/// <summary>
/// 值格式化契约（tierkv-design.md——TValue 无约束 + 注入格式化接口；formatter = <b>纯转 byte</b>）。
/// <para>★ 结构层存储恒为字节（Ring 结构化记录 + 变长本体 + 自动溢出，append-only）——
///   formatter 是 TValue 与字节序列的唯一翻译层；byte/byte[]/ROM&lt;byte&gt;/unmanaged 内建零实现
///   （[KvStore] 生成器自动发射，D6/D7 裁定），其余 TValue 由用户实现或 builder 显式注入。</para>
/// <para>★ 契约：<see cref="GetSize"/> 与 <see cref="Format"/> 对同一 value 必须一致
///   （GetSize(v) == Format 写入字节数）；<see cref="Parse"/> 是 Format 的严格逆（逐字节可逆）。</para>
/// </summary>
/// <typeparam name="TValue">值类型（无约束——格式化能力由本契约承载）。</typeparam>
public interface IValueFormatter<TValue>
{
    /// <summary>value 序列化后的字节数（写入 Ring 前定长申请——变长值按实例返回）。</summary>
    /// <param name="value">待测量序列化字节数的值。</param>
    /// <returns>value 序列化后的字节数。</returns>
    int GetSize(TValue value);

    /// <summary>把 value 写入 destination（长度 = <see cref="GetSize"/> 的返回值）。</summary>
    /// <param name="value">待序列化的值。</param>
    /// <param name="destination">写入目标缓冲区，长度不小于 <see cref="GetSize"/> 返回值。</param>
    void Format(TValue value, Span<byte> destination);

    /// <summary>从 source 读回 value（严格逆操作）。</summary>
    /// <param name="source">待反序列化的字节序列。</param>
    /// <returns>从 source 反序列化得到的值。</returns>
    TValue Parse(ReadOnlySpan<byte> source);
}

/// <summary>
/// 内建 formatter 集（D6 裁定首版覆盖面：byte/byte[]/ROM&lt;byte&gt;/unmanaged blittable）。
/// </summary>
public static class ValueFormatters
{
    /// <summary>byte 内建（1 字节直存）。</summary>
    public static IValueFormatter<byte> Byte { get; } = new ByteFormatter();

    /// <summary>byte[] 内建（变长——长度即数组长度）。</summary>
    public static IValueFormatter<byte[]> ByteArray { get; } = new ByteArrayFormatter();

    /// <summary>ReadOnlyMemory&lt;byte&gt; 内建（变长——只拷贝字节，不持有源生命周期）。</summary>
    public static IValueFormatter<ReadOnlyMemory<byte>> ByteMemory { get; } = new ByteMemoryFormatter();

    /// <summary>unmanaged blittable 值通用内建（sizeof(T) 定长直存——结构体零声明）。</summary>
    /// <returns>unmanaged blittable 值的格式化器实例。</returns>
    public static IValueFormatter<T> Blittable<T>() where T : unmanaged => new BlittableFormatter<T>();

    /// <summary>
    /// 按 TValue 自动解析 formatter（D6 首版覆盖面：byte/byte[]/ROM&lt;byte&gt;/unmanaged blittable——
    /// 与 <c>[KvStore]</c> 生成器同一裁定面）。其余 TValue 无内建——抛 InvalidOperationException，
    /// 由用户实现 <see cref="IValueFormatter{TValue}"/> 经 builder/[KvStore] Formatter= 显式注入。
    /// <para>★ blittable 判定走运行时形态检查（IsReferenceOrContainsReferences——编译期 unmanaged
    /// 约束在开放泛型 Auto&lt;T&gt; 处不可用），落 <see cref="RuntimeBlittableFormatter{T}"/> 无约束等价实现。</para>
    /// </summary>
    /// <returns>匹配 TValue 的内建格式化器；TValue 无内建 formatter 时抛 <see cref="InvalidOperationException"/>。</returns>
    public static IValueFormatter<T> Auto<T>()
    {
        if (typeof(T) == typeof(byte)) return (IValueFormatter<T>)(object)Byte;
        if (typeof(T) == typeof(byte[])) return (IValueFormatter<T>)(object)ByteArray;
        if (typeof(T) == typeof(ReadOnlyMemory<byte>)) return (IValueFormatter<T>)(object)ByteMemory;
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>()) return RuntimeBlittableFormatter<T>.Instance;
        throw new InvalidOperationException(
            $"TValue {typeof(T)} 无内建 formatter——实现 IValueFormatter<{typeof(T)}> 后经 builder/[KvStore] Formatter= 显式注入");
    }

    /// <summary>
    /// 运行时 blittable formatter（Auto&lt;T&gt; 专用——无 unmanaged 约束的 BlittableFormatter 等价物；
    /// 布局/字节序语义与 <see cref="BlittableFormatter{T}"/> 完全一致，仅访问走 Unsafe 无约束形态）。
    /// </summary>
    private sealed class RuntimeBlittableFormatter<T> : IValueFormatter<T>
    {
        public static readonly RuntimeBlittableFormatter<T> Instance = new();

        /// <summary>T 的非托管默认布局大小（字节，<see cref="Unsafe.SizeOf{T}"/>）。</summary>
        /// <param name="value">待测量序列化字节数的值（大小与值无关，恒 sizeof(T)）。</param>
        /// <returns>T 的布局大小（字节）。</returns>
        public int GetSize(T value) => Unsafe.SizeOf<T>();

        /// <summary>把 T 的内存布局按非对齐写入 destination。</summary>
        /// <param name="value">待序列化的值。</param>
        /// <param name="destination">写入目标缓冲区，长度不小于 sizeof(T)（不足抛 ArgumentException）。</param>
        public void Format(T value, Span<byte> destination)
        {
            if (destination.Length < Unsafe.SizeOf<T>())
                throw new ArgumentException($"destination 长度 {destination.Length} < GetSize {Unsafe.SizeOf<T>()}");
            Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(destination), value);
        }

        /// <summary>从 source 按非对齐读回 T（Format 的逆操作，字节布局一致）。</summary>
        /// <param name="source">待反序列化的字节序列，长度不小于 sizeof(T)（不足抛 ArgumentException）。</param>
        /// <returns>从 source 读回的 T 实例。</returns>
        public T Parse(ReadOnlySpan<byte> source)
        {
            if (source.Length < Unsafe.SizeOf<T>())
                throw new ArgumentException($"source 长度 {source.Length} < GetSize {Unsafe.SizeOf<T>()}");
            return Unsafe.ReadUnaligned<T>(ref MemoryMarshal.GetReference(source));
        }
    }

    private sealed class ByteFormatter : IValueFormatter<byte>
    {
        /// <summary>恒 1 字节。</summary>
        /// <param name="value">待测量序列化字节数的值（忽略，恒 1）。</param>
        /// <returns>1（字节）。</returns>
        public int GetSize(byte value) => 1;
        /// <summary>把单字节写入 destination 首位。</summary>
        /// <param name="value">待序列化的字节。</param>
        /// <param name="destination">写入目标缓冲区，长度不小于 1。</param>
        public void Format(byte value, Span<byte> destination) => destination[0] = value;
        /// <summary>读回首字节。</summary>
        /// <param name="source">待反序列化的字节序列，长度不小于 1。</param>
        /// <returns>source[0]。</returns>
        public byte Parse(ReadOnlySpan<byte> source) => source[0];
    }

    private sealed class ByteArrayFormatter : IValueFormatter<byte[]>
    {
        /// <summary>数组长度（字节，变长）。</summary>
        /// <param name="value">待测量序列化字节数的数组（非 null）。</param>
        /// <returns>value.Length（字节）。</returns>
        public int GetSize(byte[] value) => value.Length;
        /// <summary>把数组逐字节拷入 destination。</summary>
        /// <param name="value">待序列化的数组（非 null）。</param>
        /// <param name="destination">写入目标缓冲区，长度不小于 value.Length（不足 CopyTo 抛异常）。</param>
        public void Format(byte[] value, Span<byte> destination) => value.CopyTo(destination);
        /// <summary>把字节序列完整拷出为新数组。</summary>
        /// <param name="source">待反序列化的字节序列。</param>
        /// <returns>source 内容的新数组（长度 = source.Length）。</returns>
        public byte[] Parse(ReadOnlySpan<byte> source) => source.ToArray();
    }

    private sealed class ByteMemoryFormatter : IValueFormatter<ReadOnlyMemory<byte>>
    {
        /// <summary>内存区长度（字节，变长）。</summary>
        /// <param name="value">待测量序列化字节数的内存区。</param>
        /// <returns>value.Length（字节）。</returns>
        public int GetSize(ReadOnlyMemory<byte> value) => value.Length;
        /// <summary>把内存区字节拷入 destination（只拷贝内容，不持有源生命周期）。</summary>
        /// <param name="value">待序列化的内存区。</param>
        /// <param name="destination">写入目标缓冲区，长度不小于 value.Length（不足 CopyTo 抛异常）。</param>
        public void Format(ReadOnlyMemory<byte> value, Span<byte> destination)
            => value.Span.CopyTo(destination);
        /// <summary>把字节序列完整拷出为独立内存副本。</summary>
        /// <param name="source">待反序列化的字节序列。</param>
        /// <returns>source 内容的新副本（长度 = source.Length）。</returns>
        public ReadOnlyMemory<byte> Parse(ReadOnlySpan<byte> source) => source.ToArray();
    }

    private sealed class BlittableFormatter<T> : IValueFormatter<T> where T : unmanaged
    {
        /// <summary>T 的非托管默认布局大小（字节，<see cref="Unsafe.SizeOf{T}"/>）。</summary>
        /// <param name="value">待测量序列化字节数的值（大小与值无关，恒 sizeof(T)）。</param>
        /// <returns>T 的布局大小（字节）。</returns>
        public int GetSize(T value) => Unsafe.SizeOf<T>();
        /// <summary>把 T 的内存布局写入 destination（blittable 定长直存）。</summary>
        /// <param name="value">待序列化的值。</param>
        /// <param name="destination">写入目标缓冲区，长度不小于 sizeof(T)。</param>
        public void Format(T value, Span<byte> destination) => MemoryMarshal.Write(destination, in value);
        /// <summary>从 source 读回 T（Format 的逆操作，字节布局一致）。</summary>
        /// <param name="source">待反序列化的字节序列，长度不小于 sizeof(T)。</param>
        /// <returns>从 source 读回的 T 实例。</returns>
        public T Parse(ReadOnlySpan<byte> source) => MemoryMarshal.Read<T>(source);
    }
}
