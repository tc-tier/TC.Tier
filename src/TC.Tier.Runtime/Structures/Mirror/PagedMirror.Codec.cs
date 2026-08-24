using TC.Tier.Runtime.Structures.Mirror.Contracts;

namespace TC.Tier.Runtime.Structures.Mirror;

public sealed partial class PagedMirror
{
    /// <summary>
    /// PagedMirror 的帧 codec——统一帧布局 + 本镜像的 magic（PMVH/PMFT）/CRC32C/PerKey 链。
    /// <para>★ 子类唯一实现点（格式布局归 codec，机制归 MirrorBase）。</para>
    /// </summary>
    private sealed class Codec : IMirrorCodec
    {
        public int HeaderSize => MirrorFrameHeaderCodec.StructSize;
        public int FooterSize => MirrorFrameFooterCodec.StructSize;
        public uint HeaderMagic => RecordMagic.PagedMirrorVersioned; // "PMVH"
        public uint FooterMagic => RecordMagic.PagedMirrorFooter;    // "PMFT"
        public ushort DefaultFlags => RecordFlags.FLAG_CRC32C;
        public ushort DefaultMetaFlags => (ushort)(DefaultFlags | RecordFlags.FLAG_ENTRY_IS_META);
        public MirrorChainKind ChainKind => MirrorChainKind.PerKey;

        public void WriteHeader(Span<byte> dest, in MirrorFrameHeader header)
        {
            var h = header;
            h.MagicValue = HeaderMagic;
            h.Version = MirrorFrameHeader.CurrentVersion;
            MirrorFrameHeaderCodec.Write(dest, in h);
        }

        public bool TryReadHeader(ReadOnlySpan<byte> source, out MirrorFrameHeader header)
        {
            header = default;
            if (source.Length < HeaderSize) return false;
            header = MirrorFrameHeaderCodec.Read(source);
            return header.MagicValue == HeaderMagic
                && header.Version == MirrorFrameHeader.CurrentVersion
                && (header.Flags & RecordFlags.FLAG_CRC_MASK) == RecordFlags.FLAG_CRC32C;
        }

        public void WriteFooter(Span<byte> dest, in MirrorFrameFooter footer)
        {
            var f = footer;
            f.MagicValue = FooterMagic;
            f.Version = MirrorFrameFooter.CurrentVersion;
            MirrorFrameFooterCodec.Write(dest, in f);
        }

        public bool TryReadFooter(ReadOnlySpan<byte> source, out MirrorFrameFooter footer)
        {
            footer = default;
            if (source.Length < FooterSize) return false;
            footer = MirrorFrameFooterCodec.Read(source);
            return footer.MagicValue == FooterMagic
                && footer.Version == MirrorFrameFooter.CurrentVersion
                && (footer.Flags & RecordFlags.FLAG_CRC_MASK) == RecordFlags.FLAG_CRC32C;
        }
    }
}
