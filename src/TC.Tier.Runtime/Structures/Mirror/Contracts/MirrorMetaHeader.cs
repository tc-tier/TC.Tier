using TC.Tier.CodeGen;
using TC.Tier.Core.Primitives;
using System.Runtime.InteropServices;

namespace TC.Tier.Runtime.Structures.Mirror.Contracts;

/// <summary>
/// MirrorBase 的 meta 水位 Header（Meta 三段式，12B 纯规范，永不变）。
/// <para>水位进 Payload（MirrorMetaPayload），Header 只有规范字段。对齐 Log/Ring/Metadata。</para>
/// </summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = HeaderSize)]
public struct MirrorMetaHeader
{
    public const uint Magic = RecordMagic.MirrorMeta;
    public const ushort CurrentVersion = (ushort)((1 << 8) | 0);
    public const ushort DefaultFlags = RecordFlags.FLAG_CRC32C
                                     | RecordFlags.FLAG_PAYLOAD_2B
                                     | RecordFlags.FLAG_CRC_IN_FOOTER
                                     | RecordFlags.FLAG_META_STANDALONE;

    private const int HeaderSize = 12;

    [FieldOffset(0), ValidEquals(Magic)]          public uint   MagicValue;
    [FieldOffset(4), ValidEquals(CurrentVersion)] public ushort Version;
    [FieldOffset(6), ValidEquals(DefaultFlags)]   public ushort Flags;
    [FieldOffset(8)]  public ushort PayloadLength;
    [FieldOffset(10)] public ushort PaddingLength;
}
