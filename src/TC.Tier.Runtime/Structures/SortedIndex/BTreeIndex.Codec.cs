using TC.Tier.Runtime.Structures.SortedIndex.Layout;

namespace TC.Tier.Runtime.Structures.SortedIndex;

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

        public void WriteHeader(Span<byte> dest, long bodyLength)
        {
            // ★ Create()：ValidEquals 规范字段（Magic/Version/Flags）自动填常量——只填变化字段
            var header = BTreeIndexHeaderCodec.Create();
            header.Kind = SortedIndexConstants.KindSorted;
            header.BodyLength = bodyLength;
            BTreeIndexHeaderCodec.Write(dest, in header);
        }

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

        public void WriteFooter(Span<byte> dest, LogicalAddress watermark)
        {
            // ★ Create()：ValidEquals 规范字段（Magic）自动填常量——只填变化字段
            var footer = BTreeIndexFooterCodec.Create();
            footer.Watermark = watermark;
            BTreeIndexFooterCodec.Write(dest, in footer);
        }

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
