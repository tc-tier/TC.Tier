namespace TC.Tier.Runtime.Structures.Metadata;

public sealed partial class VersionedMetadata
{
    /// <summary>
    /// 版本化元数据的 codec，负责读写 header、计算/验证 crc。
    /// </summary>
    private sealed class Codec : IMetadataCodec
    {
        public int HeaderSize => MetadataHeaderCodec.StructSize;
        public uint Magic => MetadataHeader.Magic;
        public ushort DefaultFlags => MetadataHeader.DefaultFlags;
        public ushort DefaultMetaFlags => MetadataHeader.MetaFlags; // ★ IS_META，区分 meta record
        public int CrcOffset => MetadataHeaderCodec.Offset_Crc;
        public int PreviousVersionOffset => MetadataHeaderCodec.Offset_PreviousVersion;
        public int VersionOffset => MetadataHeaderCodec.Offset_MetadataVersion;

        public void WriteHeader(Span<byte> dest, in MetadataRecordFields f)
        {
            // ★ Create()：ValidEquals 规范字段（Magic/Version）自动填常量——只填变化字段
            var header = MetadataHeaderCodec.Create();
            header.Flags = f.Flags;
            header.PayloadLength = f.PayloadLength;
            header.PaddingLength = f.PaddingLength;
            header.PreviousVersion = f.PreviousVersion;
            header.MetadataVersion = f.MetadataVersion;
            MetadataHeaderCodec.Write(dest, in header);
        }

        public bool TryReadHeader(ReadOnlySpan<byte> source, out MetadataRecordFields fields)
        {
            fields = default;
            if (source.Length < HeaderSize) return false;
            var h = MetadataHeaderCodec.Read(source);
            if (h.MagicValue != MetadataHeader.Magic) return false;
            fields = new MetadataRecordFields(
                h.Flags, h.PayloadLength, h.PaddingLength, h.PreviousVersion, h.MetadataVersion);
            return true;
        }

        public void FillCrc(Span<byte> record, int headerSize, int payloadLength, int paddingLength)
        {
            var crcCoverEnd = headerSize + payloadLength + paddingLength;
            RecordCodec.FillCrc(record, MetadataHeader.DefaultFlags, crcCoverEnd, CrcOffset);
        }

        public bool VerifyCrc(ReadOnlySpan<byte> record, int headerSize, int payloadLength, int paddingLength)
        {
            var crcCoverEnd = headerSize + payloadLength + paddingLength;
            return RecordCodec.VerifyCrc(record, MetadataHeader.DefaultFlags, crcCoverEnd, CrcOffset);
        }

        public long ReadVersion(ReadOnlySpan<byte> headerSpan)
            => MetadataHeaderCodec.Read_MetadataVersion(headerSpan);
    }
}