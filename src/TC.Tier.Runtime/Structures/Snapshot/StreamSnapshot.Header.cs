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
        /// <summary>Magic 常量（快照帧魔数 "SNHD"，帧头身份校验）。</summary>
        public const uint Magic = RecordMagic.SnapshotFrame; // "SNHD"
        /// <summary>当前版本号（major=1, minor=0）。</summary>
        public const ushort CurrentVersion = (ushort)((1 << 8) | 0);

        /// <summary>默认 Flags（CRC64 | PAYLOAD_4B | CRC_IN_FOOTER | FOOTER_MAGIC）。</summary>
        public const ushort DefaultFlags = RecordFlags.FLAG_CRC64
                                         | RecordFlags.FLAG_PAYLOAD_4B
                                         | RecordFlags.FLAG_CRC_IN_FOOTER
                                         | RecordFlags.FLAG_FOOTER_MAGIC;

        private const int HeaderSize = 14;

        /// <summary>Magic 标识（ValidEquals 校验必须等于 <see cref="Magic"/>）。</summary>
        [FieldOffset(0), ValidEquals(Magic)] public uint MagicValue;

        /// <summary>版本号（ValidEquals 校验必须等于 <see cref="CurrentVersion"/>）。</summary>
        [FieldOffset(4), ValidEquals(CurrentVersion)]
        public ushort Version;

        /// <summary>flags（ValidHasFlags——可叠加 FLAG_ENTRY_IS_META 区分 meta 帧）。</summary>
        [FieldOffset(6), ValidHasFlags(DefaultFlags)]
        public ushort Flags;

        /// <summary>data 字节长度（不含 header/padding/footer）。</summary>
        [FieldOffset(8)] public uint PayloadLength;
        /// <summary>padding 字节长度（扇区对齐补零）。</summary>
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
        /// <summary>Footer 字节大小（28B = Magic 4B + TotalLength 8B + EntryCount 8B + Crc 8B）。</summary>
        public const int FooterSize = 28;

        /// <summary>"SNFT" — Footer magic（反向扫描定位帧尾）。</summary>
        public const uint FooterMagic = RecordMagic.SnapshotFrameFooter;

        /// <summary>Footer magic 标识（ValidEquals 校验必须等于 <see cref="FooterMagic"/>）。</summary>
        [FieldOffset(0), ValidEquals(FooterMagic)]
        public uint Magic;

        /// <summary>data 总长度（冗余，恢复校验用）。</summary>
        [FieldOffset(4)] public ulong TotalLength; // data 总长度（冗余，恢复校验用）
        /// <summary>entry 条数。</summary>
        [FieldOffset(12)] public ulong EntryCount; // entry 条数
        /// <summary>Crc64（覆盖 Header + Data + Footer 前 20B）。</summary>
        [FieldOffset(20)] public ulong Crc; // Crc64
    }
}
