using System.Runtime.InteropServices;
using TC.Tier.CodeGen;

namespace TC.Tier.Core.Net;

/// <summary>
/// 4B 不透明定长字节串（通用线协议容器——IPv4 地址等；与 <see cref="Opaque16"/> 同型的窄形态）。
/// <para>★ [BinaryLayout] 标注（嵌套件）：生成的 <c>Opaque4Codec</c> 小端直写承载字 = 字节原序直拷；
/// ctor/<see cref="CopyTo"/> 走生成的单字段读写——本类型内零 BinaryPrimitives、零偏移字面量。</para>
/// </summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 4)]
public readonly struct Opaque4 : IEquatable<Opaque4>
{
    [FieldOffset(0)] internal readonly uint _value;   // 字节 0..3（原序承载——仅作字节容器）

    /// <summary>全零。</summary>
    public static readonly Opaque4 Empty;

    /// <summary>线编码字节数（= 布局 [StructLayout].Size 同源生成——布局变更此处跟随，零漂移）。</summary>
    public static int Size => Opaque4Codec.StructSize;

    /// <summary>线布局构造（BinaryLayout 生成 codec 的 Read 路径——承载字直赋，无校验）。</summary>
    /// <param name="value">字节 0..3 承载字。</param>
    internal Opaque4(uint value)
    {
        _value = value;
    }

    /// <summary>从 4B 不透明字节构造（字节原序直拷——<see cref="CopyTo"/> 逆变换）。</summary>
    /// <param name="bytes">4 字节源——长度必须恰为 <see cref="Size"/>。</param>
    /// <exception cref="ArgumentException"><paramref name="bytes"/> 长度 ≠ <see cref="Size"/>。</exception>
    public Opaque4(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
            throw new ArgumentException($"Opaque4 源长度必须为 {Size} 字节：{bytes.Length}。", nameof(bytes));
        _value = Opaque4Codec.Read__value(bytes);
    }

    /// <summary>原序写出 4B（字节原序直拷——与构造对称；读写走生成的单字段方法收口字节序）。</summary>
    /// <param name="destination">目标缓冲（长度 ≥ <see cref="Size"/>）。</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> 长度 &lt; <see cref="Size"/>。</exception>
    public void CopyTo(Span<byte> destination)
    {
        if (destination.Length < Size)
            throw new ArgumentException($"目标缓冲 {destination.Length} 不足以容纳 Opaque4（需 {Size}）。", nameof(destination));
        Opaque4Codec.Write__value(destination, _value);
    }

    /// <summary>文本形式（8 位小写十六进制——诊断）。</summary>
    /// <returns>hex8 字符串。</returns>
    public override string ToString() => string.Create(Size * 2, this, static (chars, v) =>
    {
        const string hex = "0123456789abcdef";
        for (int i = 0; i < Size; i++)
        {
            byte b = (byte)(v._value >> (i * 8));
            chars[i * 2] = hex[b >> 4];
            chars[i * 2 + 1] = hex[b & 0xF];
        }
    });

    /// <summary>值相等（4B 全等）。</summary>
    /// <param name="other">比较对象。</param>
    /// <returns>4 字节完全一致。</returns>
    public bool Equals(Opaque4 other) => _value == other._value;

    /// <summary>值相等（装箱形态）。</summary>
    /// <param name="obj">比较对象。</param>
    /// <returns>同为 <see cref="Opaque4"/> 且 4 字节完全一致。</returns>
    public override bool Equals(object? obj) => obj is Opaque4 other && Equals(other);

    /// <summary>哈希（字典键语义）。</summary>
    /// <returns>哈希值。</returns>
    public override int GetHashCode() => _value.GetHashCode();

    /// <summary>值相等。</summary>
    /// <param name="left">左操作数。</param>
    /// <param name="right">右操作数。</param>
    /// <returns>4 字节完全一致。</returns>
    public static bool operator ==(Opaque4 left, Opaque4 right) => left.Equals(right);

    /// <summary>值不等。</summary>
    /// <param name="left">左操作数。</param>
    /// <param name="right">右操作数。</param>
    /// <returns>存在字节差异。</returns>
    public static bool operator !=(Opaque4 left, Opaque4 right) => !left.Equals(right);
}
