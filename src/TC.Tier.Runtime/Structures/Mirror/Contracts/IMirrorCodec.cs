namespace TC.Tier.Runtime.Structures.Mirror.Contracts;

/// <summary>
/// Mirror 版本帧格式 codec 契约——<b>子类唯一实现点</b>（格式布局归 codec，机制归 MirrorBase）。
/// <para>★ 统一帧格式（流式族教义）：双魔术值（头+尾）+ 推导长度（尾位−头，格式零长度字段）；
///   CRC 覆盖头+payload+尾前缀 [0,<see cref="MirrorFrameFooter.CrcPrefixSize"/>)，算法位在 flags。
///   codec 的职责 = magic 常量 + 布局读写与结构校验（magic/version/CRC 算法位）+ 链拓扑声明。</para>
/// <para>★ 两种镜像的实现差异收敛到此：WholeMirror（WMHD/WMFT，CRC64，Single 链）；
///   PagedMirror（PMVH/PMFT，CRC32C，PerKey 链按 PageId）。机制（写会话三拍/帧走链/尾锚/
///   MetaHost 嵌入/N=2/2PC）全部在 MirrorBase，子类零机制 override（COORDINATION §4 铁律 10）。</para>
/// </summary>
public interface IMirrorCodec
{
    /// <summary>帧头字节数（= MirrorFrameHeaderCodec.StructSize）。</summary>
    int HeaderSize { get; }

    /// <summary>帧尾字节数（= MirrorFrameFooterCodec.StructSize）。</summary>
    int FooterSize { get; }

    /// <summary>帧头 magic（本镜像的——WMHD/PMHD）。</summary>
    uint HeaderMagic { get; }

    /// <summary>帧尾 magic（本镜像的——WMFT/PMFT）。</summary>
    uint FooterMagic { get; }

    /// <summary>默认 flags（CRC 算法位：FLAG_CRC64 / FLAG_CRC32C）。</summary>
    ushort DefaultFlags { get; }

    /// <summary>Transport 嵌入 meta 帧 flags（DefaultFlags | FLAG_ENTRY_IS_META）。</summary>
    ushort DefaultMetaFlags { get; }

    /// <summary>版本帧链拓扑（恢复编排分派依据——机制在基类）。</summary>
    MirrorChainKind ChainKind { get; }

    /// <summary>写帧头（codec 强制填本格式的 magic/version——格式布局归 codec）。</summary>
    void WriteHeader(Span<byte> dest, in MirrorFrameHeader header);

    /// <summary>读帧头并结构校验（magic + version + CRC 算法位）。失败返回 false。</summary>
    bool TryReadHeader(ReadOnlySpan<byte> source, out MirrorFrameHeader header);

    /// <summary>写帧尾（codec 强制填本格式的 magic/version；Crc 字段由调用方填）。</summary>
    void WriteFooter(Span<byte> dest, in MirrorFrameFooter footer);

    /// <summary>读帧尾并结构校验（magic + version + CRC 算法位）。失败返回 false。</summary>
    bool TryReadFooter(ReadOnlySpan<byte> source, out MirrorFrameFooter footer);
}
