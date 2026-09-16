using TC.Tier.Runtime.Structures.Ring;
using System.Runtime.InteropServices;

namespace TC.Tier.Products.TimeSeries;

[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 21)]
internal struct TimeSeriesEnvelopeHeaderLayout
{
    [FieldOffset(0)] internal uint MagicValue;
    [FieldOffset(4)] internal byte Flags;
    [FieldOffset(5)] internal long Timestamp;
    [FieldOffset(13)] internal long Seq;
}

[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 25)]
internal struct TimeSeriesEnvelopeDenseHeaderLayout
{
    [FieldOffset(0)] internal uint MagicValue;
    [FieldOffset(4)] internal byte Flags;
    [FieldOffset(5)] internal uint SeriesId;
    [FieldOffset(9)] internal long Timestamp;
    [FieldOffset(17)] internal long Seq;
}

/// <summary>
/// TierTimeSeries 样本 envelope（tc-tier-timeseries-spec §2 产品层协议——TQE1 同族版本化变长头）。
/// <para>布局（"TTS1"+flags 共 5B 头 + 双槽 16B）：
/// <c>[Magic 4B "TTS1"][Flags 1B][Timestamp 8B][Seq 8B?] + 原 payload</c>。</para>
/// <para>★ flags：bit0=HAS_SEQ（客户端序槽有效——乱序同刻消歧/幂等的预留面）；
///   其余位预留（压缩标记/系统样本——定案⑦ Gorilla 系后续迭代，接口零变更）。</para>
/// <para>★ Timestamp = UTC Ticks，envelope 槽自述完整 ts（record key 只做分类）；
///   恢复对账 resolver 经本头读回 ts（非时序 record magic 不符 → 不入索引）。</para>
/// </summary>
public static class TimeSeriesEnvelope
{
    private const uint MagicValue = 0x31535454U;

    /// <summary>布局魔数（版本化）。</summary>
    public static ReadOnlySpan<byte> Magic => "TTS1"u8;

    /// <summary>flags：Seq 槽有效。</summary>
    public const byte FlagHasSeq = 0x1;

    /// <summary>固定头长（Magic 4 + Flags 1 + Timestamp 8 + Seq 8）。</summary>
    public const int HeaderSize = TimeSeriesEnvelopeHeaderLayoutCodec.StructSize;

    /// <summary>包裹：原 payload → envelope 化样本字节。</summary>
    /// <param name="timestamp">样本时刻（UTC Ticks）。</param>
    /// <param name="seq">客户端序（null = 不携带——FlagHasSeq 不置位）。</param>
    /// <param name="payload">原值字节。</param>
    /// <returns>固定头 + 原 payload 拼接后的完整 envelope 样本字节。</returns>
    public static byte[] Wrap(long timestamp, long? seq, ReadOnlySpan<byte> payload)
    {
        var buf = new byte[HeaderSize + payload.Length];
        WriteHeader(buf, timestamp, seq);
        payload.CopyTo(buf.AsSpan(HeaderSize));
        return buf;
    }

    /// <summary>仅编码固定 envelope 头，供 Ring scatter 写入避免临时拼接数组。</summary>
    /// <param name="destination">目标缓冲（长度 ≥ <see cref="HeaderSize"/>）。</param>
    /// <param name="timestamp">样本时刻（UTC Ticks）。</param>
    /// <param name="seq">客户端序（null = 不携带）。</param>
    internal static void WriteHeader(Span<byte> destination, long timestamp, long? seq)
    {
        byte flags = seq.HasValue ? FlagHasSeq : (byte)0;
        var header = new TimeSeriesEnvelopeHeaderLayout
        {
            MagicValue = MagicValue,
            Flags = flags,
            Timestamp = timestamp,
            Seq = seq ?? 0,
        };
        TimeSeriesEnvelopeHeaderLayoutCodec.Write(destination, in header);
    }

    /// <summary>解包（magic 校验失败 = 非本产品格式 → false）。</summary>
    /// <param name="src">envelope 化样本字节。</param>
    /// <param name="flags">输出：flags（bit0=HAS_SEQ）。</param>
    /// <param name="timestamp">输出：样本时刻（UTC Ticks）。</param>
    /// <param name="seq">输出：客户端序（HAS_SEQ 未置位 = 0）。</param>
    /// <param name="payload">输出：原 payload（拷贝交付）。</param>
    /// <returns>true = 解包成功；false = 非本产品格式（魔数/长度非法）。</returns>
    public static bool TryUnwrap(ReadOnlySpan<byte> src, out byte flags, out long timestamp, out long seq,
        out byte[] payload)
    {
        flags = 0; timestamp = 0; seq = 0;
        payload = Array.Empty<byte>();
        if (src.Length < HeaderSize) return false;
        var header = TimeSeriesEnvelopeHeaderLayoutCodec.Read(src);
        if (header.MagicValue != MagicValue) return false;
        flags = header.Flags;
        timestamp = header.Timestamp;
        seq = (header.Flags & FlagHasSeq) != 0 ? header.Seq : 0;
        payload = src.Slice(HeaderSize).ToArray();
        return true;
    }

    // ═══════════════════════════════════════════════════════════════════
    // TTS2（dense 模式——#443 设计稿 §4）：头增 SeriesId 4B，Magic 换代互不误读
    // ═══════════════════════════════════════════════════════════════════

    private const uint DenseMagicValue = 0x32535454U;

    /// <summary>dense 布局魔数（版本化——"TTS2"；与单序列 TTS1 互不读对方文件）。</summary>
    public static ReadOnlySpan<byte> DenseMagic => "TTS2"u8;

    /// <summary>dense 固定头长（Magic 4 + Flags 1 + SeriesId 4 + Timestamp 8 + Seq 8 = 25B）。</summary>
    public const int DenseHeaderSize = TimeSeriesEnvelopeDenseHeaderLayoutCodec.StructSize;

    /// <summary>dense 包裹：原 payload → envelope 化样本字节。</summary>
    /// <param name="seriesId">序列标识（入头——恢复重放路由 + 写侧路由凭证）。</param>
    /// <param name="timestamp">样本时刻（UTC Ticks）。</param>
    /// <param name="seq">客户端序（null = 不携带——FlagHasSeq 不置位）。</param>
    /// <param name="payload">原值字节。</param>
    /// <returns>固定头 + 原 payload 拼接后的完整 envelope 样本字节。</returns>
    public static byte[] WrapDense(uint seriesId, long timestamp, long? seq, ReadOnlySpan<byte> payload)
    {
        var buf = new byte[DenseHeaderSize + payload.Length];
        WriteDenseHeader(buf, seriesId, timestamp, seq);
        payload.CopyTo(buf.AsSpan(DenseHeaderSize));
        return buf;
    }

    /// <summary>仅编码 dense 固定 envelope 头（Ring scatter 写入——避免临时拼接数组）。</summary>
    /// <param name="destination">目标缓冲（长度 ≥ <see cref="DenseHeaderSize"/>）。</param>
    /// <param name="seriesId">序列标识。</param>
    /// <param name="timestamp">样本时刻（UTC Ticks）。</param>
    /// <param name="seq">客户端序（null = 不携带）。</param>
    internal static void WriteDenseHeader(Span<byte> destination, uint seriesId, long timestamp, long? seq)
    {
        byte flags = seq.HasValue ? FlagHasSeq : (byte)0;
        var header = new TimeSeriesEnvelopeDenseHeaderLayout
        {
            MagicValue = DenseMagicValue,
            Flags = flags,
            SeriesId = seriesId,
            Timestamp = timestamp,
            Seq = seq ?? 0,
        };
        TimeSeriesEnvelopeDenseHeaderLayoutCodec.Write(destination, in header);
    }

    /// <summary>dense 解包（TTS2 magic 校验失败 = 非本产品格式 → false）。</summary>
    /// <param name="src">envelope 化样本字节。</param>
    /// <param name="flags">输出：flags（bit0=HAS_SEQ）。</param>
    /// <param name="seriesId">输出：序列标识。</param>
    /// <param name="timestamp">输出：样本时刻（UTC Ticks）。</param>
    /// <param name="seq">输出：客户端序（HAS_SEQ 未置位 = 0）。</param>
    /// <param name="payload">输出：原 payload（拷贝交付）。</param>
    /// <returns>true = 解包成功；false = 非本产品格式（魔数/长度非法）。</returns>
    public static bool TryUnwrapDense(ReadOnlySpan<byte> src, out byte flags, out uint seriesId,
        out long timestamp, out long seq, out byte[] payload)
    {
        flags = 0; seriesId = 0; timestamp = 0; seq = 0;
        payload = Array.Empty<byte>();
        if (src.Length < DenseHeaderSize) return false;
        var header = TimeSeriesEnvelopeDenseHeaderLayoutCodec.Read(src);
        if (header.MagicValue != DenseMagicValue) return false;
        flags = header.Flags;
        seriesId = header.SeriesId;
        timestamp = header.Timestamp;
        seq = (header.Flags & FlagHasSeq) != 0 ? header.Seq : 0;
        payload = src.Slice(DenseHeaderSize).ToArray();
        return true;
    }
}
