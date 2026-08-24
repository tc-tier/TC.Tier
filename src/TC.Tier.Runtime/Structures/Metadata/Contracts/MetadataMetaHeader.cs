using TC.Tier.CodeGen;
using TC.Tier.Core.Primitives;
using System.Runtime.InteropServices;

namespace TC.Tier.Runtime.Structures.Metadata.Contracts;

/// <summary>
/// MetadataBase 的 meta 水位 Header（§5.7 Meta 三段式，12B 纯规范，永不变）。
/// <para>水位进 Payload（MetadataMetaPayload），Header 只有规范字段。对齐 Log/Ring。</para>
/// </summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = HeaderSize)]
public struct MetadataMetaHeader
{
    public const uint Magic = RecordMagic.MetadataMeta;
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
