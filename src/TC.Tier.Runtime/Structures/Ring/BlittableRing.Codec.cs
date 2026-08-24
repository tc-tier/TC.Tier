using TC.Tier.Core.Primitives;

namespace TC.Tier.Runtime.Structures.Ring;

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

        public void FillCrc(Span<byte> record, int headerSize, int payloadLength)
        {
            int crcCoverEnd = headerSize + payloadLength;
            RecordCodec.FillCrc(record, DefaultFlags, crcCoverEnd, CrcOffset);
        }

        public bool VerifyCrc(ReadOnlySpan<byte> record, int headerSize, int payloadLength)
        {
            int crcCoverEnd = headerSize + payloadLength;
            return RecordCodec.VerifyCrc(record, DefaultFlags, crcCoverEnd, CrcOffset);
        }

        public void OrFlags(Span<byte> headerSpan, ushort flagsToSet)
            => BlittableRingHeaderCodec.OrFlags(headerSpan, flagsToSet);

        public bool IsEmptyRecord(ReadOnlySpan<byte> headerSpan)
            => BlittableRingHeaderCodec.IsEmptyMagicValue(headerSpan);

        public ushort ReadFlags(ReadOnlySpan<byte> headerSpan)
            => BlittableRingHeaderCodec.Read_Flags(headerSpan);

        public uint ReadPayloadLength(ReadOnlySpan<byte> headerSpan)
            => BlittableRingHeaderCodec.Read_PayloadLength(headerSpan);
    }
}
