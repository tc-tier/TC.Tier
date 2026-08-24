using TC.Tier.Runtime.Structures.SortedIndex.Layout;

namespace TC.Tier.Runtime.Structures.SortedIndex;

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

        public void WriteHeader(Span<byte> dest, long bodyLength)
        {
            // ★ Create()：ValidEquals 规范字段（Magic/Version/Flags）自动填常量——只填变化字段
            var header = SkipListIndexHeaderCodec.Create();
            header.Kind = SortedIndexConstants.KindSorted;
            header.BodyLength = bodyLength;
            SkipListIndexHeaderCodec.Write(dest, in header);
        }

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

        public void WriteFooter(Span<byte> dest, LogicalAddress watermark)
        {
            // ★ Create()：ValidEquals 规范字段（Magic）自动填常量——只填变化字段
            var footer = SkipListIndexFooterCodec.Create();
            footer.Watermark = watermark;
            SkipListIndexFooterCodec.Write(dest, in footer);
        }

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