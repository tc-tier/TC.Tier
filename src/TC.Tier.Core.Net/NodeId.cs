using System.Runtime.InteropServices;
using System.Security.Cryptography;
using TC.Tier.CodeGen;

namespace TC.Tier.Core.Net;

/// <summary>
/// 节点身份——网络核心协议一等结构（spec-12 §2：自定义结构，不借任何 BCL 类型当协议身份）。
/// <para>★ 组合 <see cref="Opaque16"/>（16B 不透明字节串——布局唯一真源在彼处，本类型是身份
///   语义包装）：字节原序直拷、无端序解释，协议字节与运行时类型解耦。</para>
/// <para>★ [BinaryLayout] 标注 = 线协议标准嵌套件（spec-12 §10）——生成的 <c>NodeIdCodec</c>
///   委托 <c>Opaque16Codec</c>（小端直写承载字 = 字节原序直拷）；本类型内零 BinaryPrimitives、
///   零偏移字面量。</para>
/// <para>★ 布局零语义：不塞集群号/代次/预留位——集群归属是握手字段，fencing/代次是 raft 任期
///   与证书的事；预留字段 = 想当然。</para>
/// <para>★ 宽度 128-bit：集群身份第一性质 = 免协调不碰撞，128-bit 使其永不成议题
///   （身份每消息一次、从不每条目，字典基数 = 节点数）。</para>
/// <para>★ Guid 只在边界：配置/数据库/第三方互操作的适配转换；线上与存储无 Guid 概念。</para>
/// <para>★ 安全绑定文本 = <see cref="ToString"/> 文本形式（mTLS 档 SAN
///   <c>nid:&lt;hex32&gt;</c> URI 条目；KeyPair 档 NodeId↔公钥钉扎绑定——spec-12 §3.4）。</para>
/// </summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 16)]
public readonly struct NodeId : IEquatable<NodeId>, IComparable<NodeId>
{
    /// <summary>线编码字节数（= 布局 [StructLayout].Size 同源生成——布局变更此处跟随，零漂移）。</summary>
    public static int Size => NodeIdCodec.StructSize;

    [FieldOffset(0)] internal readonly Opaque16 _value;   // 唯一布局字段（16B 布局唯一真源在 Opaque16）

    /// <summary>未赋值哨兵（全零——非合法节点身份；与 <see langword="default"/> 同一）。</summary>
    public static readonly NodeId Empty;

    /// <summary>线布局构造（BinaryLayout 生成 codec 的 Read 路径——经 Opaque16 承载，无校验）。</summary>
    /// <param name="value">16B 不透明承载。</param>
    internal NodeId(Opaque16 value)
    {
        _value = value;
    }

    /// <summary>从 16B 不透明字节构造（字节原序直拷——<see cref="CopyTo"/> 逆变换）。</summary>
    /// <param name="bytes">16 字节源——长度必须恰为 <see cref="Size"/>（多余由调用方切片）。</param>
    /// <exception cref="ArgumentException"><paramref name="bytes"/> 长度 ≠ <see cref="Size"/>。</exception>
    public NodeId(ReadOnlySpan<byte> bytes) : this(new Opaque16(bytes))
    {
    }

    /// <summary>缺省生成（128-bit 密码学随机——免协调不碰撞）。</summary>
    /// <returns>新节点身份（全零概率 ~2^-128，视为不发生）。</returns>
    public static NodeId NewRandom()
    {
        Span<byte> bytes = stackalloc byte[Size];
        RandomNumberGenerator.Fill(bytes);
        return new(new Opaque16(bytes));
    }

    /// <summary>
    /// 时间有序生成（v7 式——同 16B 不透明布局）：前 6B = 48-bit 大端 Unix 毫秒时间戳
    /// （字节串字典序 = 时间序），后 10B 密码学随机（同毫秒内不碰撞）。
    /// </summary>
    /// <returns>新节点身份。</returns>
    public static NodeId NewSequential()
    {
        Span<byte> bytes = stackalloc byte[Size];
        RandomNumberGenerator.Fill(bytes);
        long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bytes[0] = (byte)(ms >> 40);
        bytes[1] = (byte)(ms >> 32);
        bytes[2] = (byte)(ms >> 24);
        bytes[3] = (byte)(ms >> 16);
        bytes[4] = (byte)(ms >> 8);
        bytes[5] = (byte)ms;
        return new(new Opaque16(bytes));
    }

    /// <summary>解析 hex32 文本形式（配置/诊断/SAN——大小写不敏感）。</summary>
    /// <param name="text">恰 32 位十六进制字符（无连字符/前缀）。</param>
    /// <returns>对应节点身份。</returns>
    /// <exception cref="FormatException">长度非 32 或含非十六进制字符。</exception>
    public static NodeId Parse(ReadOnlySpan<char> text) => new(Opaque16.Parse(text));

    /// <summary>解析 hex32 文本形式（Try 形态——SAN/外部输入判定，失败不抛）。</summary>
    /// <param name="text">文本（恰 32 位十六进制字符）。</param>
    /// <param name="nodeId">解析产物。</param>
    /// <returns>false = 长度/字符非法。</returns>
    public static bool TryParse(ReadOnlySpan<char> text, out NodeId nodeId)
    {
        try
        {
            nodeId = Parse(text);
            return true;
        }
        catch (FormatException)
        {
            nodeId = Empty;
            return false;
        }
    }

    /// <summary>原序写出 16B（字节原序直拷——与构造对称，无端序解释）。</summary>
    /// <param name="destination">目标缓冲（长度 ≥ <see cref="Size"/>）。</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> 长度 &lt; <see cref="Size"/>。</exception>
    public void CopyTo(Span<byte> destination) => _value.CopyTo(destination);

    /// <summary>文本形式（32 位小写十六进制，无连字符——日志/诊断/SAN 同源）。</summary>
    /// <returns>hex32 字符串。</returns>
    public override string ToString() => _value.ToString();

    /// <summary>值相等（16B 全等）。</summary>
    /// <param name="other">比较对象。</param>
    /// <returns>16 字节完全一致。</returns>
    public bool Equals(NodeId other) => _value.Equals(other._value);

    /// <summary>
    /// 字节字典序全序（§4.3 拨号归属"较小 NodeId 方拨号"的判定基础；NewSequential 的时间序性同源）。
    /// 纯序关系，不赋予身份语义。
    /// </summary>
    /// <param name="other">比较对象。</param>
    /// <returns>负 = 本值较小；零 = 相等；正 = 本值较大。</returns>
    public int CompareTo(NodeId other) => _value.CompareTo(other._value);

    /// <summary>比较算子族（CA1036——IComparable 契约补全；字节字典序与 CompareTo 同源）。</summary>
    public static bool operator <(NodeId left, NodeId right) => left.CompareTo(right) < 0;
    /// <inheritdoc cref="operator &lt;(NodeId, NodeId)" />
    public static bool operator <=(NodeId left, NodeId right) => left.CompareTo(right) <= 0;
    /// <inheritdoc cref="operator &lt;(NodeId, NodeId)" />
    public static bool operator >(NodeId left, NodeId right) => left.CompareTo(right) > 0;
    /// <inheritdoc cref="operator &lt;(NodeId, NodeId)" />
    public static bool operator >=(NodeId left, NodeId right) => left.CompareTo(right) >= 0;

    /// <summary>值相等（装箱形态）。</summary>
    /// <param name="obj">比较对象。</param>
    /// <returns>同为 <see cref="NodeId"/> 且 16 字节完全一致。</returns>
    public override bool Equals(object? obj) => obj is NodeId other && Equals(other);

    /// <summary>哈希（字典键语义）。</summary>
    /// <returns>哈希值。</returns>
    public override int GetHashCode() => _value.GetHashCode();

    /// <summary>值相等。</summary>
    /// <param name="left">左操作数。</param>
    /// <param name="right">右操作数。</param>
    /// <returns>16 字节完全一致。</returns>
    public static bool operator ==(NodeId left, NodeId right) => left.Equals(right);

    /// <summary>值不等。</summary>
    /// <param name="left">左操作数。</param>
    /// <param name="right">右操作数。</param>
    /// <returns>存在字节差异。</returns>
    public static bool operator !=(NodeId left, NodeId right) => !left.Equals(right);
}
