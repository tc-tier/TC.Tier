namespace TC.Tier.Runtime.Structures.ProbingIndex;

/// <summary>
/// ProbingIndex 主存储持久化格式契约（基接口——将来 BTree 标准化对齐时接入）。
/// <para>★ 命名律：dump/checkpoint 是内部机制词不进类型名——codec 按结构名命名。</para>
/// <para>★ 契约律（对齐 <see cref="TC.Tier.Runtime.Structures.Log.Contracts.ILogCodec"/>）：
///   基类只管机制（帧走链/CRC 总验收/体长定界），格式知识（magic/version/kind/字段布局）
///   全在实现侧——接口只传机制需要的原始值，不暴露任何具体格式类型。</para>
/// </summary>
public interface IProbingIndexCodec
{
    /// <summary>头字节数（实现格式自有）。</summary>
    int HeaderSize { get; }

    /// <summary>尾字节数（实现格式自有）。</summary>
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

/// <summary>
/// HashIndex 主存储 codec——委托 ProbingIndexHeaderCodec/FooterCodec 源生成（Kind=Probing）。
/// <para>★ 格式布局本件（子类=格式定义者）：magic/version/flags/kind 常量与字段布局是
///   本结构的格式知识，基类经 <see cref="IProbingIndexCodec"/> 只拿机制需要的值。</para>
/// </summary>
public sealed class HashIndexCodec : IProbingIndexCodec
{
    /// <summary>单例（codec 无状态——机制状态全在基类，共享安全）。</summary>
    public static readonly HashIndexCodec Instance = new();

    private HashIndexCodec() { }

    /// <inheritdoc/>
    public int HeaderSize => ProbingIndexHeaderCodec.StructSize;

    /// <inheritdoc/>
    public int FooterSize => ProbingIndexFooterCodec.StructSize;

    /// <inheritdoc/>
    public int FooterCrcOffset => ProbingIndexFooterCodec.Offset_Crc;

    /// <inheritdoc/>
    public void WriteHeader(Span<byte> dest, long bodyLength)
    {
        // ★ Create()：ValidEquals 规范字段（Magic/Version/Flags/Kind）自动填常量——只填变化字段
        var header = ProbingIndexHeaderCodec.Create();
        header.BodyLength = bodyLength;
        ProbingIndexHeaderCodec.Write(dest, in header);
    }

    /// <inheritdoc/>
    public bool TryReadHeader(ReadOnlySpan<byte> src, out long bodyLength)
    {
        bodyLength = -1;
        if (src.Length < ProbingIndexHeaderCodec.StructSize) return false;
        var h = ProbingIndexHeaderCodec.Read(src);
        if (h.MagicValue != ProbingIndexFormat.ProbingIndexHeader.Magic
            || h.Version != ProbingIndexFormat.ProbingIndexHeader.CurrentVersion
            || h.Kind != ProbingIndexFormat.KindProbing
            || h.BodyLength < 0)
            return false;
        bodyLength = h.BodyLength;
        return true;
    }

    /// <inheritdoc/>
    public void WriteFooter(Span<byte> dest, LogicalAddress watermark)
    {
        // ★ Create()：ValidEquals 规范字段（Magic）自动填常量——只填变化字段
        var footer = ProbingIndexFooterCodec.Create();
        footer.Watermark = watermark;
        ProbingIndexFooterCodec.Write(dest, in footer);
    }

    /// <inheritdoc/>
    public bool TryReadFooter(ReadOnlySpan<byte> src, out LogicalAddress watermark, out ulong crc)
    {
        watermark = LogicalAddress.Invalid;
        crc = 0;
        if (src.Length < ProbingIndexFooterCodec.StructSize) return false;
        var f = ProbingIndexFooterCodec.Read(src);
        if (f.Magic != ProbingIndexFormat.ProbingIndexFooter.FooterMagic) return false;
        watermark = f.Watermark;
        crc = f.Crc;
        return true;
    }
}
