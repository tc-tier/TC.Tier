using TC.Tier.Runtime.Structures.SortedIndex.Layout;

namespace TC.Tier.Runtime.Structures.SortedIndex;

/// <summary>
/// SkipListIndex codec 分部——主存储帧头/帧尾编解码（SkipListIndexCodec，magic SLHD/SLFT）。
/// </summary>
public partial class SkipListIndex<TKey>
{
    /// <summary>
    /// SkipListIndex 主存储 codec——委托 SkipListIndexHeaderCodec/FooterCodec 源生成（独有 magic SLHD/SLFT）。
    /// <para>★ 一个子类一个 Codec 实现类（对齐 EntryLog.Codec/DeltaLog.Codec 律）：独有头尾——
    ///   配错数据文件（如 BTree 引擎误配 SkipList）在头先行校验即失败，杜绝 Magic+CRC 全过的静默误读。</para>
    /// </summary>
    private sealed class SkipListIndexCodec : ISortedIndexCodec
    {
        public static readonly SkipListIndexCodec Instance = new();

        private SkipListIndexCodec()
        {
        }

        public int HeaderSize => SkipListIndexHeaderCodec.StructSize;
        public int FooterSize => SkipListIndexFooterCodec.StructSize;
        public int FooterCrcOffset => SkipListIndexFooterCodec.Offset_Crc;

        /// <summary>写主存储帧头——Magic/Version/Flags 取常量，Kind=Sorted，BodyLength 来自参数。</summary>
        /// <param name="dest">目标缓冲区（长度须 ≥ HeaderSize）。</param>
        /// <param name="bodyLength">帧体长度（字节）。</param>
        public void WriteHeader(Span<byte> dest, long bodyLength)
        {
            // ★ Create()：ValidEquals 规范字段（Magic/Version/Flags）自动填常量——只填变化字段
            var header = SkipListIndexHeaderCodec.Create();
            header.Kind = SortedIndexConstants.KindSorted;
            header.BodyLength = bodyLength;
            SkipListIndexHeaderCodec.Write(dest, in header);
        }

        /// <summary>读 + 验帧头：长度边界、Magic/Version/Kind/BodyLength ≥ 0 全过才接受。</summary>
        /// <param name="src">帧头起始的字节缓冲区。</param>
        /// <param name="bodyLength">输出帧体长度（字节）；失败时为 -1。</param>
        /// <returns>true = 帧头合法且 bodyLength 已解码；false = 不合法（bodyLength 为 -1）。</returns>
        public bool TryReadHeader(ReadOnlySpan<byte> src, out long bodyLength)
        {
            bodyLength = -1;
            if (src.Length < SkipListIndexHeaderCodec.StructSize) return false;
            var h = SkipListIndexHeaderCodec.Read(src);
            if (h.MagicValue != SkipListIndexHeader.Magic
                || h.Version != SkipListIndexHeader.CurrentVersion
                || h.Kind != SortedIndexConstants.KindSorted
                || h.BodyLength < 0)
                return false;
            bodyLength = h.BodyLength;
            return true;
        }

        /// <summary>写主存储帧尾——Magic 取常量，Watermark 来自参数（CRC 由调用方后续填充）。</summary>
        /// <param name="dest">目标缓冲区（长度须 ≥ FooterSize）。</param>
        /// <param name="watermark">持久化水位（恢复重放起点锚点）。</param>
        public void WriteFooter(Span<byte> dest, LogicalAddress watermark)
        {
            // ★ Create()：ValidEquals 规范字段（Magic）自动填常量——只填变化字段
            var footer = SkipListIndexFooterCodec.Create();
            footer.Watermark = watermark;
            SkipListIndexFooterCodec.Write(dest, in footer);
        }

        /// <summary>读 + 验帧尾：长度边界 + Footer Magic 校验。</summary>
        /// <param name="src">帧尾起始的字节缓冲区。</param>
        /// <param name="watermark">输出持久化水位；失败时为 <see cref="LogicalAddress.Invalid"/>。</param>
        /// <param name="crc">输出帧尾 CRC 原值（校验由调用方承担）；失败时为 0。</param>
        /// <returns>true = 帧尾合法，watermark/crc 已解码；false = 不合法。</returns>
        public bool TryReadFooter(ReadOnlySpan<byte> src, out LogicalAddress watermark, out ulong crc)
        {
            watermark = LogicalAddress.Invalid;
            crc = 0;
            if (src.Length < SkipListIndexFooterCodec.StructSize) return false;
            var f = SkipListIndexFooterCodec.Read(src);
            if (f.Magic != SkipListIndexFooter.FooterMagic) return false;
            watermark = f.Watermark;
            crc = f.Crc;
            return true;
        }
    }
}