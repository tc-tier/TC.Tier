namespace TC.Tier.Runtime.Structures.Snapshot.Contracts;

/// <summary>
/// SnapshotBase 的帧字段包。Header 仅规范字段（14B，无独有字段）；
/// TotalLength/EntryCount 在 Footer（流式 writer 末尾才知道）。
/// </summary>
/// <param name="Flags">规范 flags。</param>
/// <param name="PayloadLength">payload 长度（Header 视角）。</param>
/// <param name="PaddingLength">对齐填充。</param>
/// <param name="TotalLength">data 总长度（Footer，冗余校验用）。</param>
/// <param name="EntryCount">entry 条数（Footer）。</param>
public readonly record struct SnapshotRecordFields(
    ushort Flags,
    uint PayloadLength,
    ushort PaddingLength,
    ulong TotalLength,
    ulong EntryCount);
