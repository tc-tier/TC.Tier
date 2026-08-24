namespace TC.Tier.Runtime.Structures.Metadata.Contracts;

/// <summary>
/// VersionedMetadata record 业务字段包（供 codec 读写 header 时传参，对齐 RingRecordFields）。
/// </summary>
public readonly record struct MetadataRecordFields(
    ushort Flags,
    uint PayloadLength,
    ushort PaddingLength,
    LogicalAddress PreviousVersion,
    long MetadataVersion);