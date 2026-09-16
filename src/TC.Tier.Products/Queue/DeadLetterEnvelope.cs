using System.Text;
using TC.Tier.Contracts.Storage;
using System.Runtime.InteropServices;

namespace TC.Tier.Products.Queue;

[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 32)]
internal struct DeadLetterEnvelopeHeaderLayout
{
    [FieldOffset(0)] internal ulong MagicValue;
    [FieldOffset(8)] internal int SourceSegId;
    [FieldOffset(12)] internal int SourceExtension;
    [FieldOffset(16)] internal long SourceOffset;
    [FieldOffset(24)] internal int FinalCount;
    [FieldOffset(28)] internal int GroupLength;
}

/// <summary>
/// 死信 envelope codec（spec §2/§7.2——"TQDL01\0"）：死信入 DLQ 时包裹原 payload，
/// 携带溯源三件（源地址/源组/最终重投计数）；Replay 时解包还原原 payload。
/// <para>布局：<c>[Magic 8B][SourceAddr 16B][FinalCount 4B][GroupLen 4B][Group UTF8][原 payload]</c>。</para>
/// </summary>
public static class DeadLetterEnvelope
{
    private const ulong MagicValue = 0x003130514C444351UL;

    /// <summary>布局魔数（8 字节——与头布局严格等宽；"TQDLQ01\0"）。</summary>
    public static ReadOnlySpan<byte> Magic => "TQDLQ01\0"u8;

    /// <summary>定长头（Magic + SourceAddr + FinalCount + GroupLen）。</summary>
    public const int FixedHeaderSize = DeadLetterEnvelopeHeaderLayoutCodec.StructSize;

    /// <summary>包裹：死信 entry = envelope 头 + 原 payload。</summary>
    /// <param name="source">原消息地址（溯源三件之一）。</param>
    /// <param name="group">源组名（溯源三件之一）。</param>
    /// <param name="finalCount">最终重投计数（溯源三件之一——达上限死信）。</param>
    /// <param name="payload">原消息字节。</param>
    /// <returns>envelope 头 + 原 payload 拼接后的完整死信 entry 字节。</returns>
    public static byte[] Wrap(LogicalAddress source, string group, int finalCount, ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(group);
        int gLen = Encoding.UTF8.GetByteCount(group);
        var buf = new byte[FixedHeaderSize + gLen + payload.Length];
        var header = new DeadLetterEnvelopeHeaderLayout
        {
            MagicValue = MagicValue,
            SourceSegId = source.SegId,
            SourceExtension = source.Extension,
            SourceOffset = source.Offset,
            FinalCount = finalCount,
            GroupLength = gLen,
        };
        DeadLetterEnvelopeHeaderLayoutCodec.Write(buf, in header);
        Encoding.UTF8.GetBytes(group, buf.AsSpan(FixedHeaderSize, gLen));
        payload.CopyTo(buf.AsSpan(FixedHeaderSize + gLen));
        return buf;
    }

    /// <summary>解包（魔数/几何校验失败 = 非死信 entry → false）。</summary>
    /// <param name="src">死信 entry 字节。</param>
    /// <param name="source">溯源：原消息地址。</param>
    /// <param name="group">溯源：源组名。</param>
    /// <param name="finalCount">溯源：最终重投计数。</param>
    /// <param name="payload">原 payload（拷贝交付）。</param>
    /// <returns>true = 解包成功；false = 非死信 entry（魔数/几何非法）。</returns>
    public static bool TryUnwrap(ReadOnlySpan<byte> src,
        out LogicalAddress source, out string group, out int finalCount, out byte[] payload)
    {
        source = LogicalAddress.Invalid;
        group = string.Empty;
        finalCount = 0;
        payload = Array.Empty<byte>();
        if (src.Length < FixedHeaderSize) return false;
        var header = DeadLetterEnvelopeHeaderLayoutCodec.Read(src);
        if (header.MagicValue != MagicValue) return false;

        source = new LogicalAddress(header.SourceSegId, header.SourceExtension, header.SourceOffset);
        finalCount = header.FinalCount;
        int gLen = header.GroupLength;
        if (gLen < 0 || FixedHeaderSize + gLen > src.Length) return false;

        group = Encoding.UTF8.GetString(src.Slice(FixedHeaderSize, gLen));
        payload = src.Slice(FixedHeaderSize + gLen).ToArray();
        return true;
    }
}
