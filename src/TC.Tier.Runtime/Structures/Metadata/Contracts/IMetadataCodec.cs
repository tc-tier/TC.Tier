namespace TC.Tier.Runtime.Structures.Metadata.Contracts;

/// <summary>
/// MetadataBase 版本 record codec 接口——注入 MetadataBase，支持替换（对齐 Log 的 ILogCodec / Ring 的 IRingCodec）。
/// <para>★ 私有嵌套 sealed 类实现（VersionedMetadata.Codec），JIT 去虚化。</para>
/// </summary>
public interface IMetadataCodec
{
    /// <summary>record header 字节数。</summary>
    int HeaderSize { get; }

    /// <summary>header magic 常量。</summary>
    uint Magic { get; }

    /// <summary>数据 record 默认 flags。</summary>
    ushort DefaultFlags { get; }

    /// <summary>★ meta record flags（Embedded 模式：DefaultFlags | FLAG_ENTRY_IS_META，区分数据 record vs meta block）。</summary>
    ushort DefaultMetaFlags { get; }

    /// <summary>header 中 CRC 字段偏移。</summary>
    int CrcOffset { get; }

    /// <summary>PreviousVersion 字段偏移（基类热路径直接访问）。</summary>
    int PreviousVersionOffset { get; }

    /// <summary>MetadataVersion 字段偏移。</summary>
    int VersionOffset { get; }

    /// <summary>写完整 header（含版本链字段）。</summary>
    void WriteHeader(Span<byte> dest, in MetadataRecordFields fields);

    /// <summary>读 + 验 header（magic + 长度边界）。失败返回 false。</summary>
    bool TryReadHeader(ReadOnlySpan<byte> source, out MetadataRecordFields fields);

    /// <summary>计算并填 CRC（整块一次性，委托 RecordCodec）。</summary>
    void FillCrc(Span<byte> record, int headerSize, int payloadLength, int paddingLength);

    /// <summary>校验 CRC（委托 RecordCodec）。</summary>
    bool VerifyCrc(ReadOnlySpan<byte> record, int headerSize, int payloadLength, int paddingLength);

    /// <summary>单字段轻量读：版本号（截断/恢复按版本号定位用）。</summary>
    long ReadVersion(ReadOnlySpan<byte> headerSpan);
}
