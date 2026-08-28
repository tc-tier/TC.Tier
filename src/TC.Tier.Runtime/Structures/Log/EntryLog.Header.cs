using System.Runtime.InteropServices;

namespace TC.Tier.Runtime.Structures.Log;

public sealed partial class EntryLog
{
    /// <summary>
    /// EntryLog entry header 布局（unified-binary-layout.md §5.2）。
    /// <para>CRC64 in Header，无独有字段（codecId 在 flags 高位），4B 对齐。headerLen = 22B。</para>
    /// <para>★ 本 struct 仅定义字段偏移 + 常量。读写/校验逻辑由 RecordCodec 统一处理。</para>
    /// </summary>
    [BinaryLayout(Features = BinaryLayoutFeatures.All)]
    [StructLayout(LayoutKind.Explicit, Size = 22)]
    public struct EntryLogHeader
    {
        internal const uint Magic = RecordMagic.EntryLogEntry;
        internal const ushort CurrentVersion = (ushort)((1 << 8) | 0);

        internal const ushort DefaultFlags =
            RecordFlags.FLAG_CRC64 | RecordFlags.FLAG_PAYLOAD_4B | RecordFlags.FLAG_META_EMBEDDED;

        internal const int Alignment = 4;
        internal const int MaxEntrySize = 1 << 22;

        // — 规范字段 (14B) —
        /// <summary>Magic 标识（EntryLog entry 魔数；ValidEquals 校验必须等于 <see cref="Magic"/>）。</summary>
        [FieldOffset(0), ValidEquals(Magic)] public uint MagicValue;

        /// <summary>版本号（ValidEquals 校验必须等于 <see cref="CurrentVersion"/>）。</summary>
        [FieldOffset(4), ValidEquals(CurrentVersion)]
        public ushort Version;

        /// <summary>Flags（ValidHasFlags 校验必须含 <see cref="DefaultFlags"/> 各位）。</summary>
        [FieldOffset(6), ValidHasFlags(DefaultFlags)]
        public ushort Flags;

        /// <summary>payload 字节长度（不含 header/padding）。</summary>
        [FieldOffset(8)] public uint PayloadLength;
        /// <summary>padding 字节长度（对齐到 codec.Alignment 的补零）。</summary>
        [FieldOffset(12)] public ushort PaddingLength;

        // — CRC in Header (8B) —
        /// <summary>CRC64（覆盖 Header 除 Crc 自身 + Payload + Padding）。</summary>
        [FieldOffset(14)] public ulong Crc;
    }
}
