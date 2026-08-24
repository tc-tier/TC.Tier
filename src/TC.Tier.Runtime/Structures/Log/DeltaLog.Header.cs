using System.Runtime.InteropServices;

namespace TC.Tier.Runtime.Structures.Log;

public sealed partial class DeltaLog
{
    /// <summary>
    /// DeltaLog entry header 布局（unified-binary-layout.md §5.1）。
    /// <para>CRC32C in Header，无独有字段，4B 对齐。headerLen = 18B。</para>
    /// <para>★ 本 struct 仅定义字段偏移 + 常量。读写/校验逻辑由 RecordCodec 统一处理。</para>
    /// </summary>
    [BinaryLayout(Features = BinaryLayoutFeatures.All)]
    [StructLayout(LayoutKind.Explicit, Size = 18)]
    public struct DeltaLogHeader
    {
        internal const uint Magic = RecordMagic.DeltaLogEntry;
        internal const ushort CurrentVersion = (ushort)((1 << 8) | 0);

        internal const ushort DefaultFlags =
            RecordFlags.FLAG_CRC32C | RecordFlags.FLAG_PAYLOAD_4B | RecordFlags.FLAG_META_EMBEDDED;

        internal const int Alignment = 4;
        internal const int MaxEntrySize = 1 << 22;

        // — 规范字段 (14B) —
        [FieldOffset(0), ValidEquals(DeltaLogHeader.Magic)]
        public uint MagicValue;

        [FieldOffset(4), ValidEquals(DeltaLogHeader.CurrentVersion)]
        public ushort Version;

        [FieldOffset(6), ValidHasFlags(DeltaLogHeader.DefaultFlags)]
        public ushort Flags;

        [FieldOffset(8)] public uint PayloadLength;
        [FieldOffset(12)] public ushort PaddingLength;

        // — CRC in Header (4B) —
        [FieldOffset(14)] public uint Crc;
    }
}
