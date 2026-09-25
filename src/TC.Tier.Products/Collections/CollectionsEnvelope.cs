using System.Runtime.InteropServices;

namespace TC.Tier.Products.Collections;

[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 13)]
internal struct HashEnvelopeHeaderLayout
{
    [FieldOffset(0)] internal uint MagicValue;
    [FieldOffset(4)] internal byte Flags;
    [FieldOffset(5)] internal uint DomainId;
    [FieldOffset(9)] internal int FieldLength;
}

/// <summary>
/// TierHash field envelope（tc-tier-collections-spec §2 产品层协议——TTS1/TTS2 同族版本化变长头）。
/// <para>布局：<c>[Magic 4B "THA1"][Flags 1B][DomainId 4B][FieldLength 4B][field 字节][value 字节]</c>。</para>
/// <para>★ envelope 自述完整 field 字节（点查后校验不匹配 fail-fast——16B 哈希碰撞防线，定案④）；
///   record key（HashKey）只做分类（域 + field 哈希）。</para>
/// <para>★ 变长头 = 固定 13B 前缀 + field 字节；value 紧随其后。flags 预留（bit0 起）。</para>
/// </summary>
public static class HashEnvelope
{
    private const uint MagicValue = 0x31414854U;

    /// <summary>布局魔数（版本化——"THA1"）。</summary>
    public static ReadOnlySpan<byte> Magic => "THA1"u8;

    /// <summary>固定前缀长（Magic 4 + Flags 1 + DomainId 4 + FieldLength 4）。</summary>
    public const int HeaderSize = 13;

    /// <summary>包裹：field + value → envelope 化 record 字节（单块写路径——备份导入/溢出用）。</summary>
    /// <param name="domain">域标识。</param>
    /// <param name="field">field 字节。</param>
    /// <param name="value">值字节。</param>
    /// <returns>完整 envelope record 字节。</returns>
    public static byte[] Wrap(uint domain, ReadOnlySpan<byte> field, ReadOnlySpan<byte> value)
    {
        var buf = new byte[HeaderSize + field.Length + value.Length];
        WriteHeader(buf, domain, field.Length);
        field.CopyTo(buf.AsSpan(HeaderSize));
        value.CopyTo(buf.AsSpan(HeaderSize + field.Length));
        return buf;
    }

    /// <summary>仅编码固定前缀（Ring 分离写——前缀/payload 两段零拼接；payload = field ‖ value 由调用方拼接）。</summary>
    /// <param name="destination">目标缓冲（长度 ≥ <see cref="HeaderSize"/>）。</param>
    /// <param name="domain">域标识。</param>
    /// <param name="fieldLength">field 字节长度。</param>
    internal static void WriteHeader(Span<byte> destination, uint domain, int fieldLength)
    {
        var header = new HashEnvelopeHeaderLayout
        {
            MagicValue = MagicValue,
            Flags = 0,
            DomainId = domain,
            FieldLength = fieldLength,
        };
        HashEnvelopeHeaderLayoutCodec.Write(destination, in header);
    }

    /// <summary>解包（magic/长度校验失败 = 非本产品格式 → false）。</summary>
    /// <param name="src">envelope 化 record 字节。</param>
    /// <param name="domain">输出：域标识。</param>
    /// <param name="field">输出：field 字节（拷贝交付）。</param>
    /// <param name="value">输出：值字节（拷贝交付）。</param>
    /// <returns>true = 解包成功；false = 非本产品格式（魔数/长度非法）。</returns>
    public static bool TryUnwrap(ReadOnlySpan<byte> src, out uint domain, out byte[] field, out byte[] value)
    {
        domain = 0;
        field = [];
        value = [];
        if (src.Length < HeaderSize) return false;
        var header = HashEnvelopeHeaderLayoutCodec.Read(src);
        if (header.MagicValue != MagicValue) return false;
        if (header.FieldLength < 0 || checked(HeaderSize + header.FieldLength) > src.Length) return false;
        domain = header.DomainId;
        field = src.Slice(HeaderSize, header.FieldLength).ToArray();
        value = src.Slice(HeaderSize + header.FieldLength).ToArray();
        return true;
    }

    /// <summary>解析域标识与 field 长度（零拷贝——恢复对账/枚举过滤路；false = 非本产品格式或损坏）。</summary>
    /// <param name="src">envelope 化 record 字节。</param>
    /// <param name="domain">输出：域标识。</param>
    /// <param name="fieldLength">输出：field 字节长度。</param>
    internal static bool TryPeekHeader(ReadOnlySpan<byte> src, out uint domain, out int fieldLength)
    {
        domain = 0;
        fieldLength = 0;
        if (src.Length < HeaderSize) return false;
        var header = HashEnvelopeHeaderLayoutCodec.Read(src);
        if (header.MagicValue != MagicValue) return false;
        if (header.FieldLength < 0 || checked(HeaderSize + header.FieldLength) > src.Length) return false;
        domain = header.DomainId;
        fieldLength = header.FieldLength;
        return true;
    }
}
