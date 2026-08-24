namespace TC.Tier.Runtime.Structures.Log;

public sealed partial class EntryLog
{
    /// <summary>
    /// EntryLog 编解码器
    /// </summary>
    private sealed class Codec : ILogCodec
    {
        public int HeaderSize => EntryLogHeaderCodec.StructSize;
        public int Alignment => EntryLogHeader.Alignment;
        public int MaxEntrySize => EntryLogHeader.MaxEntrySize;

        public void WriteHeader(Span<byte> dest, int payloadLength, int paddingLength, bool isMeta)
        {
            var flags = (ushort)(EntryLogHeader.DefaultFlags | (isMeta ? RecordFlags.FLAG_ENTRY_IS_META : (ushort)0));
            // ★ Create()：ValidEquals 规范字段（Magic/Version）自动填常量——只填变化字段
            var header = EntryLogHeaderCodec.Create();
            header.Flags = flags;                        // Flags 叠加 IS_META（覆写 Create 默认值）
            header.PayloadLength = (uint)payloadLength;
            header.PaddingLength = (ushort)paddingLength;
            EntryLogHeaderCodec.Write(dest, in header);
            var crcCoverEnd = EntryLogHeaderCodec.StructSize + payloadLength + paddingLength;
            RecordCodec.FillCrc(dest, flags, crcCoverEnd, EntryLogHeaderCodec.Offset_Crc);
        }

        public bool TryReadHeader(ReadOnlySpan<byte> source, out int payloadLength, out int paddingLength, out bool isMeta, bool verifyCrc = false)
        {
            payloadLength = 0; paddingLength = 0; isMeta = false;
            if (source.Length < EntryLogHeaderCodec.StructSize) return false;
            var h = EntryLogHeaderCodec.Read(source);
            if (h.MagicValue != EntryLogHeader.Magic) return false;
            payloadLength = (int)h.PayloadLength;
            paddingLength = h.PaddingLength;
            if (payloadLength is < 0 or > EntryLogHeader.MaxEntrySize)
            { payloadLength = 0; return false; }
            isMeta = (h.Flags & RecordFlags.FLAG_ENTRY_IS_META) != 0;
            if (!verifyCrc) return true;
            var crcCoverEnd = EntryLogHeaderCodec.StructSize + payloadLength + paddingLength;
            return RecordCodec.VerifyCrc(source, h.Flags, crcCoverEnd, EntryLogHeaderCodec.Offset_Crc);
        }
    }
}
