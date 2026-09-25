using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace TC.Tier.Products.Collections;

[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 21)]
internal struct ZSetEnvelopeHeaderLayout
{
    [FieldOffset(0)] internal uint MagicValue;
    [FieldOffset(4)] internal byte Flags;
    [FieldOffset(5)] internal uint DomainId;
    [FieldOffset(9)] internal ulong ScoreEncoded;
    [FieldOffset(17)] internal int MemberLength;
}

/// <summary>
/// TierZSet member envelope（tc-tier-collections-spec §2 产品层协议——THA1 同族版本化变长头）。
/// <para>布局：<c>[Magic 4B "TZS1"][Flags 1B][DomainId 4B][ScoreEnc 8B][MemberLength 4B][member 字节]</c>。</para>
/// <para>★ ScoreEnc = score 全序编码（ScoreCodec——BinaryLayout Emit 支持集不含 double，槽存编码值；
///   score 有序索引经此直读编码——零往返转换）；envelope 自述完整 member 字节 + score，
///   点查后校验 member 字节不匹配 fail-fast（16B 哈希碰撞防线，定案④）。</para>
/// </summary>
public static class ZSetEnvelope
{
    private const uint MagicValue = 0x31535A54U;

    /// <summary>布局魔数（版本化——"TZS1"）。</summary>
    public static ReadOnlySpan<byte> Magic => "TZS1"u8;

    /// <summary>固定前缀长（Magic 4 + Flags 1 + DomainId 4 + ScoreEnc 8 + MemberLength 4）。</summary>
    public const int HeaderSize = 21;

    /// <summary>包裹：member → envelope 化 record 字节。</summary>
    /// <param name="domain">域标识。</param>
    /// <param name="score">分数（原始 double——NaN 拒绝；槽存全序编码）。</param>
    /// <param name="member">member 字节。</param>
    /// <returns>完整 envelope record 字节。</returns>
    public static byte[] Wrap(uint domain, double score, ReadOnlySpan<byte> member)
    {
        if (double.IsNaN(score))
            throw new ArgumentException("score 为 NaN——ZSet 分数不接受 NaN", nameof(score));
        var buf = new byte[HeaderSize + member.Length];
        WriteHeader(buf, domain, ScoreCodec.Encode(score), member.Length);
        member.CopyTo(buf.AsSpan(HeaderSize));
        return buf;
    }

    /// <summary>仅编码固定前缀（Ring 分离写）。</summary>
    /// <param name="destination">目标缓冲（长度 ≥ <see cref="HeaderSize"/>）。</param>
    /// <param name="domain">域标识。</param>
    /// <param name="scoreEncoded">score 全序编码（ScoreCodec）。</param>
    /// <param name="memberLength">member 字节长度。</param>
    internal static void WriteHeader(Span<byte> destination, uint domain, ulong scoreEncoded, int memberLength)
    {
        var header = new ZSetEnvelopeHeaderLayout
        {
            MagicValue = MagicValue,
            Flags = 0,
            DomainId = domain,
            ScoreEncoded = scoreEncoded,
            MemberLength = memberLength,
        };
        ZSetEnvelopeHeaderLayoutCodec.Write(destination, in header);
    }

    /// <summary>解包（magic/长度校验失败 = 非本产品格式 → false）。</summary>
    /// <param name="src">envelope 化 record 字节。</param>
    /// <param name="domain">输出：域标识。</param>
    /// <param name="score">输出：分数（解码后 double）。</param>
    /// <param name="member">输出：member 字节（拷贝交付）。</param>
    /// <returns>true = 解包成功；false = 非本产品格式（魔数/长度非法）。</returns>
    public static bool TryUnwrap(ReadOnlySpan<byte> src, out uint domain, out double score, out byte[] member)
    {
        domain = 0;
        score = 0;
        member = [];
        if (!TryPeekHeader(src, out domain, out var encoded, out var memberLength)) return false;
        score = ScoreCodec.Decode(encoded);
        member = src.Slice(HeaderSize, memberLength).ToArray();
        return true;
    }

    /// <summary>解析头部（零拷贝——恢复对账/枚举/有序迭代路；false = 非本产品格式或损坏）。</summary>
    /// <param name="src">envelope 化 record 字节。</param>
    /// <param name="domain">输出：域标识。</param>
    /// <param name="scoreEncoded">输出：score 全序编码。</param>
    /// <param name="memberLength">输出：member 字节长度。</param>
    internal static bool TryPeekHeader(ReadOnlySpan<byte> src, out uint domain, out ulong scoreEncoded, out int memberLength)
    {
        domain = 0;
        scoreEncoded = 0;
        memberLength = 0;
        if (src.Length < HeaderSize) return false;
        var header = ZSetEnvelopeHeaderLayoutCodec.Read(src);
        if (header.MagicValue != MagicValue) return false;
        if (header.MemberLength < 0 || checked(HeaderSize + header.MemberLength) > src.Length) return false;
        domain = header.DomainId;
        scoreEncoded = header.ScoreEncoded;
        memberLength = header.MemberLength;
        return true;
    }
}
