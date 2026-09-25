using System.Runtime.InteropServices;

namespace TC.Tier.Products.Collections;

[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 13)]
internal struct SetEnvelopeHeaderLayout
{
    [FieldOffset(0)] internal uint MagicValue;
    [FieldOffset(4)] internal byte Flags;
    [FieldOffset(5)] internal uint DomainId;
    [FieldOffset(9)] internal int MemberLength;
}

/// <summary>
/// TierSet member envelope（tc-tier-collections-spec §2 产品层协议——THA1 同族版本化变长头）。
/// <para>布局：<c>[Magic 4B "TSE1"][Flags 1B][DomainId 4B][MemberLength 4B][member 字节]</c>。</para>
/// <para>★ envelope 自述完整 member 字节（点查后校验不匹配 fail-fast——16B 哈希碰撞防线，定案④）。</para>
/// </summary>
public static class SetEnvelope
{
    private const uint MagicValue = 0x31455354U;

    /// <summary>布局魔数（版本化——"TSE1"）。</summary>
    public static ReadOnlySpan<byte> Magic => "TSE1"u8;

    /// <summary>固定前缀长（Magic 4 + Flags 1 + DomainId 4 + MemberLength 4）。</summary>
    public const int HeaderSize = 13;

    /// <summary>包裹：member → envelope 化 record 字节。</summary>
    public static byte[] Wrap(uint domain, ReadOnlySpan<byte> member)
    {
        var buf = new byte[HeaderSize + member.Length];
        WriteHeader(buf, domain, member.Length);
        member.CopyTo(buf.AsSpan(HeaderSize));
        return buf;
    }

    /// <summary>仅编码固定前缀（Ring 分离写）。</summary>
    internal static void WriteHeader(Span<byte> destination, uint domain, int memberLength)
    {
        var header = new SetEnvelopeHeaderLayout
        {
            MagicValue = MagicValue,
            Flags = 0,
            DomainId = domain,
            MemberLength = memberLength,
        };
        SetEnvelopeHeaderLayoutCodec.Write(destination, in header);
    }

    /// <summary>解包（magic/长度校验失败 = 非本产品格式 → false）。</summary>
    public static bool TryUnwrap(ReadOnlySpan<byte> src, out uint domain, out byte[] member)
    {
        domain = 0;
        member = [];
        if (!TryPeekHeader(src, out domain, out var memberLength)) return false;
        member = src.Slice(HeaderSize, memberLength).ToArray();
        return true;
    }

    /// <summary>解析头部（零拷贝——恢复对账/枚举过滤路；false = 非本产品格式或损坏）。</summary>
    internal static bool TryPeekHeader(ReadOnlySpan<byte> src, out uint domain, out int memberLength)
    {
        domain = 0;
        memberLength = 0;
        if (src.Length < HeaderSize) return false;
        var header = SetEnvelopeHeaderLayoutCodec.Read(src);
        if (header.MagicValue != MagicValue) return false;
        if (header.MemberLength < 0 || checked(HeaderSize + header.MemberLength) > src.Length) return false;
        domain = header.DomainId;
        memberLength = header.MemberLength;
        return true;
    }
}
