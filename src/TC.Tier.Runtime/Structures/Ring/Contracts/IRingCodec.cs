namespace TC.Tier.Runtime.Structures.Ring.Contracts;

/// <summary>
/// Ring record 三段式 codec 接口——注入 RingBase，消除对具体 header 类型的依赖。
/// <para>基类只管页/地址/水位，codec 管 record 格式（header/CRC/字段偏移/单字段读）。</para>
/// </summary>
public interface IRingCodec
{
    /// <summary>record header 字节数。</summary>
    int HeaderSize { get; }
    /// <summary>record 对齐粒度（payload 后 padding 单位）。</summary>
    int Alignment { get; }
    /// <summary>record 上界（拒绝 corrupt header）。</summary>
    int MaxRecordSize { get; }
    /// <summary>header magic 常量。</summary>
    uint Magic { get; }
    /// <summary>header 默认 flags。</summary>
    ushort DefaultFlags { get; }
    /// <summary>header 中 CRC 字段的偏移。</summary>
    int CrcOffset { get; }

    // ── 字段偏移（生成器产出 Offset_* 常量 → Codec 暴露）──
    /// <summary>PayloadLength 字段在 header 内的字节偏移。</summary>
    int PayloadLengthOffset { get; }

    /// <summary>写完整 header。</summary>
    void WriteHeader(Span<byte> dest, in RingRecordFields fields);
    /// <summary>读 + 验 header（magic + 长度边界）。失败返回 false。</summary>
    bool TryReadHeader(ReadOnlySpan<byte> source, out RingRecordFields fields);
    /// <summary>计算并填 CRC32C。</summary>
    void FillCrc(Span<byte> record, int headerSize, int payloadLength);
    /// <summary>校验 CRC32C。</summary>
    bool VerifyCrc(ReadOnlySpan<byte> record, int headerSize, int payloadLength);
    /// <summary>原地 OR 设置 flags 位。</summary>
    void OrFlags(Span<byte> headerSpan, ushort flagsToSet);
    /// <summary>判断是否空位（magic==0）。</summary>
    bool IsEmptyRecord(ReadOnlySpan<byte> headerSpan);

    // ── 单字段轻量读（基类 GetKey/TryGetValue/TryGetKey 热路径用，不走全量 TryReadHeader）──
    /// <summary>只读 Flags 字段。</summary>
    ushort ReadFlags(ReadOnlySpan<byte> headerSpan);
    /// <summary>只读 PayloadLength 字段。</summary>
    uint ReadPayloadLength(ReadOnlySpan<byte> headerSpan);
}

/// <summary>
/// Ring record 业务字段包（供 codec 读写 header 时传参）。
/// </summary>
public readonly record struct RingRecordFields(
    ushort Flags,
    uint PayloadLength,
    ushort PaddingLength,
    LogicalAddress PreviousAddress);
