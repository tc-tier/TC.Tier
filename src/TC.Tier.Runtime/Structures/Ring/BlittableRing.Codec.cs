using TC.Tier.Core.Primitives;

namespace TC.Tier.Runtime.Structures.Ring;

/// <summary>
/// BlittableRing 的 Codec 分部——私有 <see cref="IRingCodec"/> 实现：header 编解码、CRC 填充/校验与单字段轻量读。
/// </summary>
public partial class BlittableRing<TKey>
{
    private sealed class Codec : IRingCodec
    {
        public int HeaderSize => BlittableRingHeaderCodec.StructSize;
        public int Alignment => BlittableRingHeader.Alignment;
        public int MaxRecordSize => 1 << 22;
        public uint Magic => BlittableRingHeader.Magic;
        public ushort DefaultFlags => BlittableRingHeader.DefaultFlags;
        public int CrcOffset => BlittableRingHeaderCodec.Offset_Crc32C;

        public int PayloadLengthOffset => BlittableRingHeaderCodec.Offset_PayloadLength;

        /// <summary>写完整 header——Magic/Version 等规范字段取常量，Flags/PayloadLength/PaddingLength/PreviousAddress 来自 f。</summary>
        /// <param name="dest">目标缓冲区（长度须 ≥ HeaderSize），header 原地写入。</param>
        /// <param name="f">record 业务字段包（flags/负载长度/padding 长度/前驱地址）。</param>
        public void WriteHeader(Span<byte> dest, in RingRecordFields f)
        {
            // ★ Create()：ValidEquals 规范字段（Magic/Version）自动填常量——只填变化字段
            var header = BlittableRingHeaderCodec.Create();
            header.Flags = f.Flags;
            header.PayloadLength = f.PayloadLength;
            header.PaddingLength = f.PaddingLength;
            header.PreviousAddress = f.PreviousAddress;
            BlittableRingHeaderCodec.Write(dest, in header);
        }

        /// <summary>读 + 验 header：长度边界、Magic、PayloadLength 上界全过才解码（全量解析，含 Version/PreviousAddress）。</summary>
        /// <param name="source">header 起始的字节缓冲区（设备回源/恢复路径用）。</param>
        /// <param name="f">输出解码出的业务字段；失败时为 default。</param>
        /// <returns>true = header 合法且 f 已解码；false = 长度不足/Magic 不符/PayloadLength 超上界（f 为 default）。</returns>
        public bool TryReadHeader(ReadOnlySpan<byte> source, out RingRecordFields f)
        {
            f = default;
            if (source.Length < HeaderSize) return false;
            var h = BlittableRingHeaderCodec.Read(source);
            if (h.MagicValue != Magic) return false;
            if (h.PayloadLength > MaxRecordSize) return false;
            f = new RingRecordFields(h.Flags, h.PayloadLength, h.PaddingLength, h.PreviousAddress);
            return true;
        }

        /// <summary>热路径轻量门：单字段直读 Magic/Flags/PayloadLength，不解析 Version/PreviousAddress/PaddingLength
        /// （f.PaddingLength 恒 0、f.PreviousAddress 为 default）。仅限本进程写入的页池热读。</summary>
        /// <param name="source">header 起始的字节缓冲区。</param>
        /// <param name="f">输出 Flags/PayloadLength（其余字段为 default）；失败时为 default。</param>
        /// <returns>true = 通过 Magic + PayloadLength 上界检查；false = 不合法（f 为 default）。</returns>
        public bool TryReadHeaderLight(ReadOnlySpan<byte> source, out RingRecordFields f)
        {
            f = default;
            if (source.Length < HeaderSize) return false;
            if (BlittableRingHeaderCodec.Read_MagicValue(source) != Magic) return false;
            uint payloadLength = BlittableRingHeaderCodec.Read_PayloadLength(source);
            if (payloadLength > MaxRecordSize) return false;
            f = new RingRecordFields(BlittableRingHeaderCodec.Read_Flags(source), payloadLength, 0, default);
            return true;
        }

        /// <summary>计算并填 CRC32C——按默认 flags，覆盖 [0, headerSize + payloadLength)，CRC 字段在 CrcOffset 处。</summary>
        /// <param name="record">整条记录缓冲区（调用前其余字段须已填好）。</param>
        /// <param name="headerSize">header 字节数。</param>
        /// <param name="payloadLength">payload 长度（字节，不含 padding）。</param>
        public void FillCrc(Span<byte> record, int headerSize, int payloadLength)
        {
            int crcCoverEnd = headerSize + payloadLength;
            RecordCodec.FillCrc(record, DefaultFlags, crcCoverEnd, CrcOffset);
        }

        /// <summary>校验 CRC32C（按默认 flags，覆盖 [0, headerSize + payloadLength)）。</summary>
        /// <param name="record">整条记录缓冲区（只读）。</param>
        /// <param name="headerSize">header 字节数。</param>
        /// <param name="payloadLength">payload 长度（字节，不含 padding）。</param>
        /// <returns>true = CRC 匹配；false = 记录损坏或 CRC 不匹配。</returns>
        public bool VerifyCrc(ReadOnlySpan<byte> record, int headerSize, int payloadLength)
        {
            int crcCoverEnd = headerSize + payloadLength;
            return RecordCodec.VerifyCrc(record, DefaultFlags, crcCoverEnd, CrcOffset);
        }

        /// <summary>原地按位 OR 设置 header 的 flags 位（不改其他字段）。</summary>
        /// <param name="headerSpan">header 起始的字节缓冲区，原地修改。</param>
        /// <param name="flagsToSet">要置位的 flags 位掩码。</param>
        public void OrFlags(Span<byte> headerSpan, ushort flagsToSet)
            => BlittableRingHeaderCodec.OrFlags(headerSpan, flagsToSet);

        /// <summary>判断是否空位（Magic == 0，即槽位从未写入）。</summary>
        /// <param name="headerSpan">header 起始的字节缓冲区。</param>
        /// <returns>true = 空位（从未写入）；false = 已有记录（无论是否校验通过）。</returns>
        public bool IsEmptyRecord(ReadOnlySpan<byte> headerSpan)
            => BlittableRingHeaderCodec.IsEmptyMagicValue(headerSpan);

        /// <summary>只读 Flags 字段（不走全量 header 解码）。</summary>
        /// <param name="headerSpan">header 起始的字节缓冲区（长度须 ≥ Flags 字段偏移 + 2）。</param>
        /// <returns>header 的 flags 位掩码原值。</returns>
        public ushort ReadFlags(ReadOnlySpan<byte> headerSpan)
            => BlittableRingHeaderCodec.Read_Flags(headerSpan);

        /// <summary>只读 PayloadLength 字段（不走全量 header 解码）。</summary>
        /// <param name="headerSpan">header 起始的字节缓冲区（长度须 ≥ PayloadLength 字段偏移 + 4）。</param>
        /// <returns>payload 长度（字节，不含 padding）。</returns>
        public uint ReadPayloadLength(ReadOnlySpan<byte> headerSpan)
            => BlittableRingHeaderCodec.Read_PayloadLength(headerSpan);
    }
}
