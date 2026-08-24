namespace TC.Tier.Runtime.Structures.Snapshot;

public sealed partial class StreamSnapshot
{
    /// <summary>StreamSnapshot 的 codec——委托源生成 StreamFrameHeaderCodec/StreamFrameFooterCodec。</summary>
    private sealed class Codec : ISnapshotCodec
    {
        public int HeaderSize => StreamFrameHeaderCodec.StructSize;
        public int FooterSize => StreamFrameFooterCodec.StructSize;
        public int Alignment => 512;
        public uint Magic => StreamFrameHeader.Magic;
        public uint FooterMagic => StreamFrameFooter.FooterMagic;
        public ushort DefaultFlags => StreamFrameHeader.DefaultFlags;
        public ushort DefaultMetaFlags => (ushort)(StreamFrameHeader.DefaultFlags
                                                | RecordFlags.FLAG_ENTRY_IS_META); // ★ IS_META 区分 meta 帧

        public void WriteHeader(Span<byte> dest, in SnapshotRecordFields f)
        {
            // ★ Create()：ValidEquals 规范字段（Magic/Version）自动填常量——只填变化字段
            var header = StreamFrameHeaderCodec.Create();
            header.Flags = f.Flags;
            header.PayloadLength = f.PayloadLength;
            header.PaddingLength = f.PaddingLength;
            StreamFrameHeaderCodec.Write(dest, in header);
        }

        public bool TryReadHeader(ReadOnlySpan<byte> source, out SnapshotRecordFields fields)
        {
            fields = default;
            if (source.Length < HeaderSize) return false;
            var h = StreamFrameHeaderCodec.Read(source);
            if (h.MagicValue != StreamFrameHeader.Magic) return false;
            fields = new SnapshotRecordFields(h.Flags, h.PayloadLength, h.PaddingLength, 0, 0);
            return true;
        }

        public void WriteFooter(Span<byte> dest, in SnapshotRecordFields f)
        {
            // ★ Create()：ValidEquals 规范字段（Magic）自动填常量——只填变化字段
            var footer = StreamFrameFooterCodec.Create();
            footer.TotalLength = f.TotalLength;
            footer.EntryCount = f.EntryCount;
            footer.Crc = 0;
            StreamFrameFooterCodec.Write(dest, in footer);
        }

        public SnapshotRecordFields ReadFooter(ReadOnlySpan<byte> src)
        {
            var f = StreamFrameFooterCodec.Read(src);
            return new SnapshotRecordFields(0, 0, 0, f.TotalLength, f.EntryCount);
        }
    }
}
