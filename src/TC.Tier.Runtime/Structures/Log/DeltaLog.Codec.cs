namespace TC.Tier.Runtime.Structures.Log;

public sealed partial class DeltaLog
{
    /// <summary>
    /// DeltaLog 编解码器
    /// </summary>
    private sealed class Codec : ILogCodec
    {
        public int HeaderSize => DeltaLogHeaderCodec.StructSize;
        public int Alignment => DeltaLogHeader.Alignment;
        public int MaxEntrySize => DeltaLogHeader.MaxEntrySize;

        public void WriteHeader(Span<byte> dest, int payloadLength, int paddingLength, bool isMeta)
        {
            ushort flags =
                (ushort)(DeltaLogHeader.DefaultFlags | (isMeta ? RecordFlags.FLAG_ENTRY_IS_META : (ushort)0));
            // ★ Create()：ValidEquals 规范字段（Magic/Version）自动填常量——只填变化字段
            var header = DeltaLogHeaderCodec.Create();
            header.Flags = flags;                        // Flags 叠加 IS_META（覆写 Create 默认值）
            header.PayloadLength = (uint)payloadLength;
            header.PaddingLength = (ushort)paddingLength;
            DeltaLogHeaderCodec.Write(dest, in header);
            int crcCoverEnd = DeltaLogHeaderCodec.StructSize + payloadLength + paddingLength;
            RecordCodec.FillCrc(dest, flags, crcCoverEnd, DeltaLogHeaderCodec.Offset_Crc);
        }

        public bool TryReadHeader(ReadOnlySpan<byte> source, out int payloadLength, out int paddingLength,
            out bool isMeta, bool verifyCrc = false)
        {
            payloadLength = 0;
            paddingLength = 0;
            isMeta = false;
            if (source.Length < DeltaLogHeaderCodec.StructSize) return false;
            var h = DeltaLogHeaderCodec.Read(source);
            if (h.MagicValue != DeltaLogHeader.Magic) return false;
            payloadLength = (int)h.PayloadLength;
            paddingLength = h.PaddingLength;
            if (payloadLength is < 0 or > DeltaLogHeader.MaxEntrySize)
            {
                payloadLength = 0;
                return false;
            }

            isMeta = (h.Flags & RecordFlags.FLAG_ENTRY_IS_META) != 0;
            if (!verifyCrc) return true;
            var crcCoverEnd = DeltaLogHeaderCodec.StructSize + payloadLength + paddingLength;
            return RecordCodec.VerifyCrc(source, h.Flags, crcCoverEnd, DeltaLogHeaderCodec.Offset_Crc);
        }
    }
}