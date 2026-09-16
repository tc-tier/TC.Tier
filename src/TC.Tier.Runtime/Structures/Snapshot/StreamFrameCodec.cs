namespace TC.Tier.Runtime.Structures.Snapshot;

/// <summary>
/// 流式帧 codec（StreamSnapshot 与 IncrementalSnapshot 共用同一帧格式：
/// [Header 14B][Data][Footer 28B]，Magic="SNHD"/"SNFT"）。
/// </summary>
internal sealed class StreamFrameCodec : ISnapshotCodec
{
    public int HeaderSize => StreamFrameHeaderCodec.StructSize;
    public int FooterSize => StreamFrameFooterCodec.StructSize;
    public int Alignment => 512;
    public uint Magic => StreamSnapshot.StreamFrameHeader.Magic;
    public uint FooterMagic => StreamSnapshot.StreamFrameFooter.FooterMagic;
    public ushort DefaultFlags => StreamSnapshot.StreamFrameHeader.DefaultFlags;
    public ushort DefaultMetaFlags => (ushort)(StreamSnapshot.StreamFrameHeader.DefaultFlags
                                            | RecordFlags.FLAG_ENTRY_IS_META); // ★ IS_META 区分 meta 帧

    /// <summary>写帧头（14B）：填 Flags/PayloadLength/PaddingLength（Magic/Version 由 Create 填常量）。</summary>
    /// <param name="dest">目标缓冲区（长度 ≥ HeaderSize = 14 字节）。</param>
    /// <param name="f">帧字段（Flags/PayloadLength/PaddingLength 有效；TotalLength/EntryCount 由 Footer 携带，此处忽略）。</param>
    public void WriteHeader(Span<byte> dest, in SnapshotRecordFields f)
    {
        // ★ Create()：ValidEquals 规范字段（Magic/Version）自动填常量——只填变化字段
        var header = StreamFrameHeaderCodec.Create();
        header.Flags = f.Flags;
        header.PayloadLength = f.PayloadLength;
        header.PaddingLength = f.PaddingLength;
        StreamFrameHeaderCodec.Write(dest, in header);
    }

    /// <summary>尝试解析帧头。</summary>
    /// <param name="source">源缓冲区（长度不足 HeaderSize = 14 字节直接失败）。</param>
    /// <param name="fields">输出解析出的帧字段（Flags/PayloadLength/PaddingLength；TotalLength/EntryCount 置 0——在 Footer）。</param>
    /// <returns>true = 解析成功（magic 校验通过）；false = 长度不足或 magic 不符（fields 为 default）。</returns>
    public bool TryReadHeader(ReadOnlySpan<byte> source, out SnapshotRecordFields fields)
    {
        fields = default;
        if (source.Length < HeaderSize) return false;
        var h = StreamFrameHeaderCodec.Read(source);
        if (h.MagicValue != StreamSnapshot.StreamFrameHeader.Magic) return false;
        fields = new SnapshotRecordFields(h.Flags, h.PayloadLength, h.PaddingLength, 0, 0);
        return true;
    }

    /// <summary>写帧尾（28B）：填 TotalLength/EntryCount（Magic 由 Create 填常量；Crc 占位 0——调用方回填）。</summary>
    /// <param name="dest">目标缓冲区（长度 ≥ FooterSize = 28 字节）。</param>
    /// <param name="f">帧字段（TotalLength/EntryCount 有效；Flags/PayloadLength/PaddingLength 忽略）。</param>
    public void WriteFooter(Span<byte> dest, in SnapshotRecordFields f)
    {
        // ★ Create()：ValidEquals 规范字段（Magic）自动填常量——只填变化字段
        var footer = StreamFrameFooterCodec.Create();
        footer.TotalLength = f.TotalLength;
        footer.EntryCount = f.EntryCount;
        footer.Crc = 0;
        StreamFrameFooterCodec.Write(dest, in footer);
    }

    /// <summary>解析帧尾。</summary>
    /// <param name="src">源缓冲区（长度 ≥ FooterSize = 28 字节）。</param>
    /// <returns>解析出的帧字段（TotalLength/EntryCount；Flags/PayloadLength/PaddingLength 置 0）。</returns>
    public SnapshotRecordFields ReadFooter(ReadOnlySpan<byte> src)
    {
        var f = StreamFrameFooterCodec.Read(src);
        return new SnapshotRecordFields(0, 0, 0, f.TotalLength, f.EntryCount);
    }
}
