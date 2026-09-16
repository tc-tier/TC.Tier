using System.Runtime.InteropServices;
using TC.Tier.CodeGen;

namespace TC.Tier.Core.Net;

/// <summary>
/// 16B 不透明定长字节串（通用线协议容器——spec-12 §2 "声明即字节原序直拷"的通用形态；
/// 语义化包装如 <see cref="NodeId"/> 组合本类型，16B 布局唯一真源在此）。
/// <para>★ [BinaryLayout] 标注（嵌套件——spec-12 §10）：生成的 <c>Opaque16Codec</c> 小端直写
///   双承载字 = 字节原序直拷；ctor/<see cref="CopyTo"/> 走生成的单字段读写——本类型内零
///   BinaryPrimitives、零偏移字面量。</para>
/// <para>★ 布局零语义：纯字节容器（IPv6 地址/密钥指纹/校验和等）；字节字典序全序与 hex32
///   文本形式是容器的自然属性，不赋予任何领域语义。</para>
/// </summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 16)]
public readonly struct Opaque16 : IEquatable<Opaque16>, IComparable<Opaque16>
{
    private const string HexDigits = "0123456789abcdef";

    [FieldOffset(0)] internal readonly ulong _hi; // 字节 0..7（原序承载——仅作字节容器）
    [FieldOffset(8)] internal readonly ulong _lo; // 字节 8..15

    /// <summary>全零。</summary>
    public static readonly Opaque16 Empty;

    /// <summary>线编码字节数（= 布局 [StructLayout].Size 同源生成——布局变更此处跟随，零漂移）。</summary>
    public static int Size => Opaque16Codec.StructSize;

    /// <summary>线布局构造（BinaryLayout 生成 codec 的 Read 路径——承载字直赋，无校验）。</summary>
    /// <param name="hi">字节 0..7 承载字。</param>
    /// <param name="lo">字节 8..15 承载字。</param>
    internal Opaque16(ulong hi, ulong lo)
    {
        _hi = hi;
        _lo = lo;
    }

    /// <summary>从 16B 不透明字节构造（字节原序直拷——<see cref="CopyTo"/> 逆变换）。</summary>
    /// <param name="bytes">16 字节源——长度必须恰为 <see cref="Size"/>（多余由调用方切片）。</param>
    /// <exception cref="ArgumentException"><paramref name="bytes"/> 长度 ≠ <see cref="Size"/>。</exception>
    public Opaque16(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
            throw new ArgumentException($"Opaque16 源长度必须为 {Size} 字节：{bytes.Length}。", nameof(bytes));
        _hi = Opaque16Codec.Read__hi(bytes);
        _lo = Opaque16Codec.Read__lo(bytes);
    }

    /// <summary>解析 hex32 文本形式（大小写不敏感）。</summary>
    /// <param name="text">恰 32 位十六进制字符（无连字符/前缀）。</param>
    /// <returns>对应字节串。</returns>
    /// <exception cref="FormatException">长度非 32 或含非十六进制字符。</exception>
    public static Opaque16 Parse(ReadOnlySpan<char> text)
    {
        if (text.Length != Size * 2)
            throw new FormatException($"Opaque16 文本形式必须为 {Size * 2} 位十六进制：{text.Length} 位。");
        Span<byte> bytes = stackalloc byte[Size];
        for (int i = 0; i < Size; i++)
        {
            char hiChar = text[i * 2];
            char loChar = text[i * 2 + 1];
            int hi = HexDigit(hiChar);
            int lo = HexDigit(loChar);
            if (hi < 0)
                throw new FormatException($"Opaque16 文本形式含非十六进制字符（位置 {i * 2}）：'{hiChar}'。");
            if (lo < 0)
                throw new FormatException($"Opaque16 文本形式含非十六进制字符（位置 {i * 2 + 1}）：'{loChar}'。");
            bytes[i] = (byte)(hi << 4 | lo);
        }
        return new(bytes);
    }

    /// <summary>原序写出 16B（字节原序直拷——与构造对称；读写走生成的单字段方法收口字节序）。</summary>
    /// <param name="destination">目标缓冲（长度 ≥ <see cref="Size"/>）。</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> 长度 &lt; <see cref="Size"/>。</exception>
    public void CopyTo(Span<byte> destination)
    {
        if (destination.Length < Size)
            throw new ArgumentException($"目标缓冲 {destination.Length} 不足以容纳 Opaque16（需 {Size}）。", nameof(destination));
        Opaque16Codec.Write__hi(destination, _hi);
        Opaque16Codec.Write__lo(destination, _lo);
    }

    /// <summary>文本形式（32 位小写十六进制，无连字符——诊断同源）。</summary>
    /// <returns>hex32 字符串。</returns>
    public override string ToString() => string.Create(Size * 2, this, static (chars, v) =>
    {
        for (int i = 0; i < Size; i++)
        {
            byte b = v.ByteAt(i);
            chars[i * 2] = HexDigits[b >> 4];
            chars[i * 2 + 1] = HexDigits[b & 0xF];
        }
    });

    /// <summary>值相等（16B 全等）。</summary>
    /// <param name="other">比较对象。</param>
    /// <returns>16 字节完全一致。</returns>
    public bool Equals(Opaque16 other) => _hi == other._hi && _lo == other._lo;

    /// <summary>
    /// 字节字典序全序（容器的自然序——byte[0] 最高位；纯序关系，不赋予语义）。
    /// 逐字节比较（承载字为 LE 读入值——数值序 ≠ 字节字典序）。
    /// </summary>
    /// <param name="other">比较对象。</param>
    /// <returns>负 = 本值较小；零 = 相等；正 = 本值较大。</returns>
    public int CompareTo(Opaque16 other)
    {
        for (int i = 0; i < Size; i++)
        {
            int cmp = ByteAt(i).CompareTo(other.ByteAt(i));
            if (cmp != 0) return cmp;
        }
        return 0;
    }

    /// <summary>比较算子族（CA1036——IComparable 契约补全；字节字典序与 CompareTo 同源）。</summary>
    public static bool operator <(Opaque16 left, Opaque16 right) => left.CompareTo(right) < 0;
    /// <inheritdoc cref="operator &lt;(Opaque16, Opaque16)" />
    public static bool operator <=(Opaque16 left, Opaque16 right) => left.CompareTo(right) <= 0;
    /// <inheritdoc cref="operator &lt;(Opaque16, Opaque16)" />
    public static bool operator >(Opaque16 left, Opaque16 right) => left.CompareTo(right) > 0;
    /// <inheritdoc cref="operator &lt;(Opaque16, Opaque16)" />
    public static bool operator >=(Opaque16 left, Opaque16 right) => left.CompareTo(right) >= 0;

    /// <summary>值相等（装箱形态）。</summary>
    /// <param name="obj">比较对象。</param>
    /// <returns>同为 <see cref="Opaque16"/> 且 16 字节完全一致。</returns>
    public override bool Equals(object? obj) => obj is Opaque16 other && Equals(other);

    /// <summary>哈希（字典键语义）。</summary>
    /// <returns>哈希值。</returns>
    public override int GetHashCode() => HashCode.Combine(_hi, _lo);

    /// <summary>值相等。</summary>
    /// <param name="left">左操作数。</param>
    /// <param name="right">右操作数。</param>
    /// <returns>16 字节完全一致。</returns>
    public static bool operator ==(Opaque16 left, Opaque16 right) => left.Equals(right);

    /// <summary>值不等。</summary>
    /// <param name="left">左操作数。</param>
    /// <param name="right">右操作数。</param>
    /// <returns>存在字节差异。</returns>
    public static bool operator !=(Opaque16 left, Opaque16 right) => !left.Equals(right);

    private readonly byte ByteAt(int index) => index < 8
        ? (byte)(_hi >> (index * 8))
        : (byte)(_lo >> ((index - 8) * 8));

    private static int HexDigit(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };
}
