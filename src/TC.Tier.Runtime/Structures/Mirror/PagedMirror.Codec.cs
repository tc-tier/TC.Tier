using TC.Tier.Runtime.Structures.Mirror.Contracts;

namespace TC.Tier.Runtime.Structures.Mirror;

/// <summary>
/// PagedMirror 编解码 partial——嵌套 Codec（<see cref="IMirrorCodec"/> 实现）：统一帧 header/footer 读写 + magic/CRC 模式校验。
/// </summary>
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

        /// <summary>写帧头（填本镜像 magic "PMVH" + 当前版本号）。</summary>
        /// <param name="dest">目标缓冲区（长度 ≥ HeaderSize）。</param>
        /// <param name="header">帧头值（MagicValue/Version 由本方法覆写为本镜像常量）。</param>
        public void WriteHeader(Span<byte> dest, in MirrorFrameHeader header)
        {
            var h = header;
            h.MagicValue = HeaderMagic;
            h.Version = MirrorFrameHeader.CurrentVersion;
            MirrorFrameHeaderCodec.Write(dest, in h);
        }

        /// <summary>尝试解析帧头。</summary>
        /// <param name="source">源缓冲区（长度不足 HeaderSize 直接失败）。</param>
        /// <param name="header">输出解析出的帧头；失败时为 default。</param>
        /// <returns>true = magic 为 "PMVH" 且版本匹配且 CRC 模式为 CRC32C；false = 长度不足或校验不符。</returns>
        public bool TryReadHeader(ReadOnlySpan<byte> source, out MirrorFrameHeader header)
        {
            header = default;
            if (source.Length < HeaderSize) return false;
            header = MirrorFrameHeaderCodec.Read(source);
            return header.MagicValue == HeaderMagic
                && header.Version == MirrorFrameHeader.CurrentVersion
                && (header.Flags & RecordFlags.FLAG_CRC_MASK) == RecordFlags.FLAG_CRC32C;
        }

        /// <summary>写帧尾（填本镜像 magic "PMFT" + 当前版本号）。</summary>
        /// <param name="dest">目标缓冲区（长度 ≥ FooterSize）。</param>
        /// <param name="footer">帧尾值（MagicValue/Version 由本方法覆写为本镜像常量）。</param>
        public void WriteFooter(Span<byte> dest, in MirrorFrameFooter footer)
        {
            var f = footer;
            f.MagicValue = FooterMagic;
            f.Version = MirrorFrameFooter.CurrentVersion;
            MirrorFrameFooterCodec.Write(dest, in f);
        }

        /// <summary>尝试解析帧尾。</summary>
        /// <param name="source">源缓冲区（长度不足 FooterSize 直接失败）。</param>
        /// <param name="footer">输出解析出的帧尾；失败时为 default。</param>
        /// <returns>true = magic 为 "PMFT" 且版本匹配且 CRC 模式为 CRC32C；false = 长度不足或校验不符。</returns>
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
