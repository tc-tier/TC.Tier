using System.Runtime.InteropServices;

namespace TC.Tier.Runtime.Structures.Metadata;

public abstract partial class MetadataBase
{
    /// <summary>
    /// VersionedMetadata 版本 record header（统一三段式 + 版本链独有字段）。
    /// <para>布局：[规范字段 14B][PreviousVersion 16B][Version 8B][Crc 4B] = 42B，Payload 在 Header 之后，无 Footer（CRC in Header）。</para>
    /// <para>★ PreviousVersion/Version 是版本链指针，跟数据一起（数据头包裹的一部分，不是 meta）。</para>
    /// <para>源生成器（[BinaryLayout]）生成 VersionedMetadataHeaderCodec（偏移/小端/CRC 编译期生成）。</para>
    /// </summary>
    [BinaryLayout(Features = BinaryLayoutFeatures.All)]
    [StructLayout(LayoutKind.Explicit, Size = HeaderSize)]
    public struct MetadataHeader
    {
        public const uint Magic = RecordMagic.VersionedMetadata;
        public const ushort CurrentVersion = (ushort)((1 << 8) | 0); // major=1, minor=0

        public const ushort DefaultFlags = RecordFlags.FLAG_CRC32C
                                           | RecordFlags.FLAG_PAYLOAD_4B;

        /// <summary>★ Embedded meta record flags（DefaultFlags | FLAG_ENTRY_IS_META）——区分数据版本 record vs 嵌入版本链的 meta block。</summary>
        public const ushort MetaFlags = DefaultFlags
                                        | RecordFlags.FLAG_ENTRY_IS_META;

        private const int HeaderSize = 42;

        // === 规范字段（14B，unified-binary-layout §1.2）===
        [FieldOffset(0), ValidEquals(Magic)] public uint MagicValue;

        [FieldOffset(4), ValidEquals(CurrentVersion)]
        public ushort Version;

        [FieldOffset(6), ValidEquals(DefaultFlags)]
        public ushort Flags;

        [FieldOffset(8)] public uint PayloadLength; // 元数据结构体字节数
        [FieldOffset(12)] public ushort PaddingLength; // 扇区对齐填充

        // === 版本链独有字段（跟数据一起，数据头包裹的一部分）===
        /// <summary>版本链指针，指向上一版本（链尾为 LogicalAddress.Empty）。</summary>
        [FieldOffset(14)] public LogicalAddress PreviousVersion;

        /// <summary>版本号（写入返回值，调用方按版本号寻址）。</summary>
        [FieldOffset(30)] public long MetadataVersion;

        // === CRC（in Header，对齐 Log/Ring 模式）===
        /// <summary>CRC32C（覆盖 Header 除 Crc 自身 + Payload + Padding）。</summary>
        [FieldOffset(38)] public uint Crc;
    }
}