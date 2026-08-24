namespace TC.Tier.Contracts.Layout;

/// <summary>
/// 统一二进制布局 magic 值登记
/// 全部 uint32（4B），全树唯一。落盘 LE，hex dump 读 ASCII 可辨识类型。
/// </summary>
public static partial class RecordMagic
{
    // ══ Log entry ══
    /// <summary>"DLOG" — DeltaLog entry header magic。</summary>
    public const uint DeltaLogEntry = 0x474F4C44;

    /// <summary>"ELOG" — EntryLog entry header magic。</summary>
    public const uint EntryLogEntry = 0x474F4C45;

    // ══ Blob ══
    /// <summary>"SBHD" — StreamBlock frame header magic。</summary>
    public const uint StreamBlockHeader = 0x44484253;

    /// <summary>"SBFT" — StreamBlock frame footer magic（FLAG_FOOTER_MAGIC，反向扫描用）。</summary>
    public const uint StreamBlockFooter = 0x54464253;

    /// <summary>"IMHD" — IndexMirror header magic。</summary>
    public const uint IndexMirror = 0x44484D49;

    /// <summary>"FBHD" — FixedBlock header magic。</summary>
    public const uint FixedBlock = 0x44484246;

    /// <summary>"PMHD" — PageMirror header magic。</summary>
    public const uint PageMirror = 0x44484D50;

    // ══ Meta watermark ══
    /// <summary>"SMHD" — StreamMeta (Blob) header magic。</summary>
    public const uint StreamMeta = 0x44484D53;

    /// <summary>"LMHD" — LogMeta header magic。</summary>
    public const uint LogMeta = 0x44484D4C;

    // ══ Ring ══
    /// <summary>"BRHD" — BlittableRing record header magic。</summary>
    public const uint BlittableRing = 0x44485242;

    // ══ Overflow (WiscKey value log) ══
    /// <summary>"OVRF" — Overflow record frame header magic。</summary>
    public const uint OverflowRecord = 0x4652564F;

    // ══ PageFrame（页包裹）══
    /// <summary>"LPGF" — LogPageFrame magic（Log 页包裹，整页 CRC 加速扫描）。</summary>
    public const uint LogPageFrame = 0x4647504C;

    /// <summary>"RPGF" — RingPageFrame magic（Ring 页包裹，身份校验 + 加速扫描）。</summary>
    public const uint RingPageFrame = 0x46475052;

    /// <summary>"BPGF" — BlobPageFrame magic（Blob 页包裹，保留 SBHD/SBFT）。</summary>
    public const uint BlobPageFrame = 0x46475042;

    // ══ Ring Meta watermark ══
    /// <summary>"RMHD" — RingMeta header magic。</summary>
    public const uint RingMeta = 0x44484D52;

    // ══ MetadataBase（原 Blob/FixedBlock 拆分，版本链元数据）══
    /// <summary>"VMHD" — VersionedMetadata 版本 record header magic。</summary>
    public const uint VersionedMetadata = 0x44484D56;

    /// <summary>"MDHD" — MetadataMeta header magic（MetadataBase 的 meta 水位）。</summary>
    public const uint MetadataMeta = 0x44484D44;

    // ══ MirrorBase（原 Blob/IndexMirror·PageMirror 拆分，checkpoint 镜像版本链）══
    /// <summary>"WMHD" — WholeMirror 版本帧 header magic（v2 流式帧：双魔术值推导长度，瘦头无长度字段）。</summary>
    public const uint WholeMirror = 0x44484D57;

    /// <summary>"WMFT" — WholeMirror 版本帧 footer magic（尾锚：长度/链指针/版本/CRC 全在尾）。</summary>
    public const uint WholeMirrorFooter = 0x54464D57;

    /// <summary>"PMVH" — PagedMirror 版本帧 header magic（v2 帧化：per-page 多链）。</summary>
    public const uint PagedMirrorVersioned = 0x48564D50;

    /// <summary>"PMFT" — PagedMirror 版本帧 footer magic（尾锚：长度/链指针/版本/CRC 全在尾）。</summary>
    public const uint PagedMirrorFooter = 0x54464D50;

    /// <summary>"MMHD" — MirrorMeta header magic（MirrorBase 的 meta 水位）。</summary>
    public const uint MirrorMeta = 0x44484D4D;

    // ══ SnapshotBase（原 Blob/StreamBlock 拆分，GB/TB 大数据流帧）══
    /// <summary>"SNHD" — Snapshot 帧头 magic（StreamSnapshot）。</summary>
    public const uint SnapshotFrame = 0x44484E53;

    /// <summary>"SNFT" — Snapshot 帧尾 magic（Backward 扫描定位帧尾用）。</summary>
    public const uint SnapshotFrameFooter = 0x54464E53;

    /// <summary>"SNMD" — SnapshotMeta header magic（SnapshotBase 的 meta 水位）。</summary>
    public const uint SnapshotMeta = 0x444D4E53;

    // ══ 索引结构主存储帧（各族私有格式契约——codec 契约禁跨族共用）══
    /// <summary>"PIHD" — ProbingIndex 主存储帧 header magic（先行校验——不符即无效）。</summary>
    public const uint ProbingIndexHeader = 0x44484950;

    /// <summary>"PIFT" — ProbingIndex 主存储帧 footer magic（总验收——W + CRC64）。</summary>
    public const uint ProbingIndexFooter = 0x54464950;

    /// <summary>"BIHD" — BTreeIndex 主存储帧 header magic（先行校验——配错数据文件立即失败）。</summary>
    public const uint BTreeIndexHeader = 0x44484942;

    /// <summary>"BIFT" — BTreeIndex 主存储帧 footer magic（总验收——W + CRC64）。</summary>
    public const uint BTreeIndexFooter = 0x54464942;

    /// <summary>"SLHD" — SkipListIndex 主存储帧 header magic（先行校验——配错数据文件立即失败）。</summary>
    public const uint SkipListIndexHeader = 0x44484C53;

    /// <summary>"SLFT" — SkipListIndex 主存储帧 footer magic（总验收——W + CRC64）。</summary>
    public const uint SkipListIndexFooter = 0x54464C53;
}
