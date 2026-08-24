namespace TC.Tier.Runtime.Structures.Snapshot.Contracts;

/// <summary>
/// SnapshotBase 数据格式 codec 接口（对齐 IMirrorCodec/IMetadataCodec 模式）。
/// <para>流式帧：[Header 14B 规范][Payload][Footer 28B（Magic+TotalLength+EntryCount+Crc64）]。
/// FooterMagic 供 Backward 扫描定位帧尾（恢复取真尾）；CRC64 流式增量累积（data 可能跨多次追加）。</para>
/// </summary>
public interface ISnapshotCodec
{
    /// <summary>帧头字节数（14）。</summary>
    int HeaderSize { get; }

    /// <summary>帧尾字节数（28）。</summary>
    int FooterSize { get; }

    /// <summary>对齐粒度（扇区）。</summary>
    int Alignment { get; }

    /// <summary>帧头 magic。</summary>
    uint Magic { get; }

    /// <summary>帧尾 magic（Backward 扫描定位用）。</summary>
    uint FooterMagic { get; }

    /// <summary>默认 flags。</summary>
    ushort DefaultFlags { get; }

    /// <summary>meta 帧 flags（DefaultFlags | FLAG_ENTRY_IS_META）。</summary>
    ushort DefaultMetaFlags { get; }

    /// <summary>写帧头。</summary>
    void WriteHeader(Span<byte> dest, in SnapshotRecordFields fields);

    /// <summary>读帧头（校验 magic）。</summary>
    bool TryReadHeader(ReadOnlySpan<byte> source, out SnapshotRecordFields fields);

    /// <summary>写帧尾到 dest（至少 FooterSize 字节）。</summary>
    void WriteFooter(Span<byte> dest, in SnapshotRecordFields fields);

    /// <summary>读帧尾。src 至少 FooterSize 字节。</summary>
    SnapshotRecordFields ReadFooter(ReadOnlySpan<byte> src);
}
