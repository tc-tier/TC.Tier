using System.Runtime.InteropServices;
using TC.Tier.Core.Primitives;

namespace TC.Tier.Runtime.Structures.Ring.Contracts;

/// <summary>
/// RingMeta header（对齐 LogMetaHeader 范式：规范字段开头 12B + 水位进 Payload）。
/// <para>★ [BinaryLayout(Features = BinaryLayoutFeatures.All)] → 源生成器生成 RingMetaHeaderCodec.Write/Read。</para>
/// <para>参见 base.md §2.7。</para>
/// <para>★ public：IRingMetaPolicy 为 public 接口，参数/返回类型须同级可见（CS0050/CS0051）。</para>
/// </summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = HeaderSize)]
public struct RingMetaHeader
{
    public const uint   Magic          = RecordMagic.RingMeta;          // "RMHD"
    public const ushort CurrentVersion = (ushort)((1 << 8) | 0);        // major=1, minor=0
    public const ushort DefaultFlags   = RecordFlags.FLAG_CRC32C | RecordFlags.FLAG_PAYLOAD_2B
                                         | RecordFlags.FLAG_CRC_IN_FOOTER | RecordFlags.FLAG_META_STANDALONE;

    private const int    HeaderSize     = 12;

    // === 规范字段 (12B) ===
    [FieldOffset(0), ValidEquals(RingMetaHeader.Magic)]          public uint   MagicValue;
    [FieldOffset(4), ValidEquals(RingMetaHeader.CurrentVersion)] public ushort Version;
    [FieldOffset(6), ValidEquals(RingMetaHeader.DefaultFlags)]   public ushort Flags;
    [FieldOffset(8)]  public ushort PayloadLength;
    [FieldOffset(10)] public ushort PaddingLength;
}
