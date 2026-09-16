namespace TC.Tier.Runtime.Structures.Metadata;

/// <summary>
/// VersionedMetadata 编解码 partial——嵌套 Codec（<see cref="IMetadataCodec"/> 实现）：
/// 版本 record header 读写 + CRC 填充/校验 + 版本号读取。
/// </summary>
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

        /// <summary>写版本 record 头（42B）：填规范字段 + 版本链字段 PreviousVersion/MetadataVersion（Magic/Version 由 Create 填常量）。</summary>
        /// <param name="dest">目标缓冲区（长度 ≥ HeaderSize = 42 字节）。</param>
        /// <param name="f">record 字段（Flags/PayloadLength/PaddingLength/PreviousVersion/MetadataVersion 全部有效）。</param>
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

        /// <summary>尝试解析版本 record 头。</summary>
        /// <param name="source">源缓冲区（长度不足 HeaderSize = 42 字节直接失败）。</param>
        /// <param name="fields">输出解析出的 record 字段（含版本链字段）；失败时为 default。</param>
        /// <returns>true = 解析成功（magic 校验通过）；false = 长度不足或 magic 不符。</returns>
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

        /// <summary>计算并填充 record CRC（CRC32C in Header——覆盖 Header 除 Crc 自身 + payload + padding）。</summary>
        /// <param name="record">完整 record 缓冲区（header+payload+padding 已填好）。</param>
        /// <param name="headerSize">header 字节大小（42）。</param>
        /// <param name="payloadLength">payload 字节长度。</param>
        /// <param name="paddingLength">对齐补零字节长度。</param>
        public void FillCrc(Span<byte> record, int headerSize, int payloadLength, int paddingLength)
        {
            var crcCoverEnd = headerSize + payloadLength + paddingLength;
            RecordCodec.FillCrc(record, MetadataHeader.DefaultFlags, crcCoverEnd, CrcOffset);
        }

        /// <summary>校验 record CRC（口径同 <see cref="FillCrc"/>）。</summary>
        /// <param name="record">完整 record 缓冲区。</param>
        /// <param name="headerSize">header 字节大小（42）。</param>
        /// <param name="payloadLength">payload 字节长度。</param>
        /// <param name="paddingLength">对齐补零字节长度。</param>
        /// <returns>true = CRC 一致（record 完整）；false = CRC 不符（record 损坏/撕裂）。</returns>
        public bool VerifyCrc(ReadOnlySpan<byte> record, int headerSize, int payloadLength, int paddingLength)
        {
            var crcCoverEnd = headerSize + payloadLength + paddingLength;
            return RecordCodec.VerifyCrc(record, MetadataHeader.DefaultFlags, crcCoverEnd, CrcOffset);
        }

        /// <summary>从 record 头读取版本号（MetadataVersion 字段）。</summary>
        /// <param name="headerSpan">record 头字节（长度 ≥ HeaderSize = 42 字节）。</param>
        /// <returns>该 record 的版本号（long，链上单调递增）。</returns>
        public long ReadVersion(ReadOnlySpan<byte> headerSpan)
            => MetadataHeaderCodec.Read_MetadataVersion(headerSpan);
    }
}