namespace TC.Tier.Runtime.Structures.SortedIndex.Contracts;

/// <summary>
/// 比较族主存储格式契约（族私有——IXXXCodec 是每一族的私有契约，禁止跨族共用）。
/// <para>★ 契约律（对齐 <see cref="TC.Tier.Runtime.Structures.Log.Contracts.ILogCodec"/>）：
///   基类只管机制（帧走链/CRC 总验收/体长定界），格式知识（magic/version/kind/字段布局）
///   全在实现侧——接口只传机制需要的原始值，不暴露任何具体格式类型。</para>
/// </summary>
public interface ISortedIndexCodec
{
    /// <summary>帧头字节长（机制侧定界——头 = magic/version/flags/kind/体长，写头时体长已知）。</summary>
    int HeaderSize { get; }
    /// <summary>帧尾字节长（机制侧定界——尾 = magic/水位 W/CRC64）。</summary>
    int FooterSize { get; }

    /// <summary>CRC 字段在尾内偏移——帧走链 CRC 覆盖截止点（位置=格式知识，由实现声明）。</summary>
    int FooterCrcOffset { get; }

    /// <summary>写头（实现自填 magic/version/flags/kind——格式自有）。</summary>
    void WriteHeader(Span<byte> dest, long bodyLength);

    /// <summary>读头并全校验（magic/version/kind/体长）——有效输出体长（帧走链定界）。</summary>
    bool TryReadHeader(ReadOnlySpan<byte> src, out long bodyLength);

    /// <summary>写尾（实现自填 magic；CRC 字段留零由基类覆写——值=机制累积器）。</summary>
    void WriteFooter(Span<byte> dest, LogicalAddress watermark);

    /// <summary>读尾并校验 magic——有效输出水位 W 与 CRC 值（总验收由基类裁决）。</summary>
    bool TryReadFooter(ReadOnlySpan<byte> src, out LogicalAddress watermark, out ulong crc);
}
