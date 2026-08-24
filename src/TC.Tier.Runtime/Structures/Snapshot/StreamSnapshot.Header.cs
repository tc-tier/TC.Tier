using System.Runtime.InteropServices;

namespace TC.Tier.Runtime.Structures.Snapshot;

public sealed partial class StreamSnapshot
{
    /// <summary>
    /// 流式帧 Header（14B 仅规范字段，无独有字段——TotalLength/EntryCount 放 Footer：流式 writer 末尾才知道）。
    /// <para>flags = CRC64 | PAYLOAD_4B | CRC_IN_FOOTER | FOOTER_MAGIC。对齐粒度：扇区。</para>
    /// </summary>
    [BinaryLayout(Features = BinaryLayoutFeatures.All)]
    [StructLayout(LayoutKind.Explicit, Size = HeaderSize)]
    public struct StreamFrameHeader
    {
        public const uint Magic = RecordMagic.SnapshotFrame; // "SNHD"
        public const ushort CurrentVersion = (ushort)((1 << 8) | 0);

        public const ushort DefaultFlags = RecordFlags.FLAG_CRC64
                                         | RecordFlags.FLAG_PAYLOAD_4B
                                         | RecordFlags.FLAG_CRC_IN_FOOTER
                                         | RecordFlags.FLAG_FOOTER_MAGIC;

        private const int HeaderSize = 14;

        [FieldOffset(0), ValidEquals(Magic)] public uint MagicValue;

        [FieldOffset(4), ValidEquals(CurrentVersion)]
        public ushort Version;

        /// <summary>flags（ValidHasFlags——可叠加 FLAG_ENTRY_IS_META 区分 meta 帧）。</summary>
        [FieldOffset(6), ValidHasFlags(DefaultFlags)]
        public ushort Flags;

        [FieldOffset(8)] public uint PayloadLength;
        [FieldOffset(12)] public ushort PaddingLength;
    }

    /// <summary>
    /// 流式帧 Footer（28B = Magic + TotalLength + EntryCount + Crc64）。
    /// <para>★ FooterMagic 供 Backward 扫描定位帧尾（恢复取真尾）。CRC64 覆盖
    /// Header + Data + Footer 前 20B（CRC 自身 8B 不参与）。</para>
    /// </summary>
    [BinaryLayout(Features = BinaryLayoutFeatures.All)]
    [StructLayout(LayoutKind.Explicit, Size = FooterSize)]
    public struct StreamFrameFooter
    {
        public const int FooterSize = 28;

        /// <summary>"SNFT" — Footer magic（反向扫描定位帧尾）。</summary>
        public const uint FooterMagic = RecordMagic.SnapshotFrameFooter;

        [FieldOffset(0), ValidEquals(FooterMagic)]
        public uint Magic;

        [FieldOffset(4)] public ulong TotalLength; // data 总长度（冗余，恢复校验用）
        [FieldOffset(12)] public ulong EntryCount; // entry 条数
        [FieldOffset(20)] public ulong Crc; // Crc64
    }
}
