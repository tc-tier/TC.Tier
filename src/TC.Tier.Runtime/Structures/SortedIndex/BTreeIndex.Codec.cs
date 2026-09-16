using TC.Tier.Runtime.Structures.SortedIndex.Layout;

namespace TC.Tier.Runtime.Structures.SortedIndex;

/// <summary>
/// BTreeIndex codec 分部——主存储帧头/帧尾编解码（BTreeIndexCodec，magic BIHD/BIFT）。
/// </summary>
public partial class BTreeIndex<TKey>
{
    /// <summary>
    /// BTreeIndex 主存储 codec——委托 BTreeIndexHeaderCodec/FooterCodec 源生成（独有 magic BIHD/BIFT）。
    /// <para>★ 一个子类一个 Codec 实现类（对齐 EntryLog.Codec/DeltaLog.Codec 律）：独有头尾——
    ///   配错数据文件（如 SkipList 引擎误配 BTree）在头先行校验即失败，杜绝 Magic+CRC 全过的静默误读。</para>
    /// </summary>
    private sealed class BTreeIndexCodec : ISortedIndexCodec
    {
        public static readonly BTreeIndexCodec Instance = new();

        private BTreeIndexCodec()
        {
        }

        public int HeaderSize => BTreeIndexHeaderCodec.StructSize;
        public int FooterSize => BTreeIndexFooterCodec.StructSize;
        public int FooterCrcOffset => BTreeIndexFooterCodec.Offset_Crc;

        /// <summary>写主存储帧头——Magic/Version/Flags 取常量，Kind=Sorted，BodyLength 来自参数。</summary>
        /// <param name="dest">目标缓冲区（长度须 ≥ HeaderSize）。</param>
        /// <param name="bodyLength">帧体长度（字节）。</param>
        public void WriteHeader(Span<byte> dest, long bodyLength)
        {
            // ★ Create()：ValidEquals 规范字段（Magic/Version/Flags）自动填常量——只填变化字段
            var header = BTreeIndexHeaderCodec.Create();
            header.Kind = SortedIndexConstants.KindSorted;
            header.BodyLength = bodyLength;
            BTreeIndexHeaderCodec.Write(dest, in header);
        }

        /// <summary>读 + 验帧头：长度边界、Magic/Version/Kind/BodyLength ≥ 0 全过才接受。</summary>
        /// <param name="src">帧头起始的字节缓冲区。</param>
        /// <param name="bodyLength">输出帧体长度（字节）；失败时为 -1。</param>
        /// <returns>true = 帧头合法且 bodyLength 已解码；false = 不合法（bodyLength 为 -1）。</returns>
        public bool TryReadHeader(ReadOnlySpan<byte> src, out long bodyLength)
        {
            bodyLength = -1;
            if (src.Length < BTreeIndexHeaderCodec.StructSize) return false;
            var h = BTreeIndexHeaderCodec.Read(src);
            if (h.MagicValue != BTreeIndexHeader.Magic
                || h.Version != BTreeIndexHeader.CurrentVersion
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
            var footer = BTreeIndexFooterCodec.Create();
            footer.Watermark = watermark;
            BTreeIndexFooterCodec.Write(dest, in footer);
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
            if (src.Length < BTreeIndexFooterCodec.StructSize) return false;
            var f = BTreeIndexFooterCodec.Read(src);
            if (f.Magic != BTreeIndexFooter.FooterMagic) return false;
            watermark = f.Watermark;
            crc = f.Crc;
            return true;
        }
    }
}
