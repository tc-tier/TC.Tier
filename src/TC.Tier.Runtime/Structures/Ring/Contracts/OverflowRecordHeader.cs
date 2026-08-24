using System.Runtime.InteropServices;

namespace TC.Tier.Runtime.Structures.Ring.Contracts;

/// <summary>
/// 溢出记录帧头（WiscKey-style value log）
/// <para>★ [BinaryLayout] → 源生成器生成 OverflowRecordHeaderCodec.Write/Read。</para>
/// <para>物理布局：18B Header (Magic+Version+Flags+PayloadLen+PadLen+CRC32C) + Payload + Padding。</para>
/// <para>MagicLocator 通过 OverflowRecord 魔数粗定位，恢复时逐条 CRC 校验前向求精。</para>
/// <para>参见 base.md §2.7。</para>
/// </summary>
 [BinaryLayout(Features = BinaryLayoutFeatures.All)]
 [StructLayout(LayoutKind.Explicit, Size = 18)]
internal struct OverflowRecordHeader
{
    public const uint   Magic          = RecordMagic.OverflowRecord;  // "OVRF" = 0x4652564F
    public const ushort CurrentVersion = (ushort)((1 << 8) | 0);     // major=1, minor=0
    public const ushort DefaultFlags   = RecordFlags.FLAG_CRC32C | RecordFlags.FLAG_PAYLOAD_4B;
    public const int    Alignment      = 4;  // magic(4) version(2) flags(2) payloadLen(4) padLen(2) crc(4) = 18B

    [FieldOffset(0),  ValidEquals(OverflowRecordHeader.Magic)]          public uint   MagicValue;
    [FieldOffset(4),  ValidEquals(OverflowRecordHeader.CurrentVersion)] public ushort Version;
    [FieldOffset(6),  ValidHasFlags(OverflowRecordHeader.DefaultFlags)] public ushort Flags;
    [FieldOffset(8)]  public uint   PayloadLength;   // = value.Length
    [FieldOffset(12)] public ushort PaddingLength;
    [FieldOffset(14)] public uint   Crc32C;
    // ★ CRC 偏移用 SG 生成的 OverflowRecordHeaderCodec.Offset_Crc32C，禁止手写重复常量。
}
