namespace TC.Tier.Runtime.Structures.Log;

/// <summary>
/// EntryLog 编解码 partial——嵌套 Codec（<see cref="ILogCodec"/> 实现）：记录头写入与解析。
/// <para>★ Header 布局见 <see cref="EntryLogHeader"/>；CRC 由 <see cref="RecordCodec"/> 统一处理。</para>
/// </summary>
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

        /// <summary>
        /// 写入单条 entry 记录头：填规范字段（Flags 叠加 IS_META 位）并计算 CRC64
        /// （覆盖 header 除 Crc 自身 + payload + padding）写入头内 Crc 字段。
        /// </summary>
        /// <param name="dest">目标缓冲区；长度须 ≥ <see cref="Codec.HeaderSize"/>（22 字节）且按 4 字节对齐（<see cref="Codec.Alignment"/>）。</param>
        /// <param name="payloadLength">payload 字节数；取值范围 [0, <see cref="Codec.MaxEntrySize"/>]（4 MiB）。</param>
        /// <param name="paddingLength">对齐补零字节数（4 字节对齐产生的 0..3 字节 padding 的长度）。</param>
        /// <param name="isMeta">true = 元数据记录（Flags 置 FLAG_ENTRY_IS_META）；false = 普通记录。</param>
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

        /// <summary>
        /// 尝试从源缓冲区解析 entry 记录头。
        /// </summary>
        /// <param name="source">源缓冲区；长度不足 HeaderSize（22 字节）直接失败。</param>
        /// <param name="payloadLength">输出 payload 字节数（不含 header/padding）；解析失败时为 0。</param>
        /// <param name="paddingLength">输出对齐补零字节数；仅成功时有效。</param>
        /// <param name="isMeta">输出 true = 元数据记录、false = 普通记录；解析失败时为 false。</param>
        /// <param name="verifyCrc">true = 额外做 CRC64 校验（覆盖 header+payload+padding），默认 false。</param>
        /// <returns>true = 解析成功（verifyCrc=true 时还需 CRC 校验通过）；false = 源长度不足、magic 不符
        /// （此时所有 out 为 0/false）、payloadLength 越界（payloadLength 归 0）或 CRC 校验失败（其余 out 有效）。</returns>
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
