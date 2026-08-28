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
    /// <summary>Magic 常量（MetadataMeta 魔数，meta 块身份校验）。</summary>
    public const uint Magic = RecordMagic.MetadataMeta;
    /// <summary>当前版本号（major=1, minor=0）。</summary>
    public const ushort CurrentVersion = (ushort)((1 << 8) | 0);
    /// <summary>默认 Flags（CRC32C | PAYLOAD_2B | CRC_IN_FOOTER | META_STANDALONE）。</summary>
    public const ushort DefaultFlags = RecordFlags.FLAG_CRC32C
                                    | RecordFlags.FLAG_PAYLOAD_2B
                                    | RecordFlags.FLAG_CRC_IN_FOOTER
                                    | RecordFlags.FLAG_META_STANDALONE;

    private const int HeaderSize = 12;

    /// <summary>Magic 标识（ValidEquals 校验必须等于 <see cref="Magic"/>）。</summary>
    [FieldOffset(0), ValidEquals(Magic)]          public uint   MagicValue;
    /// <summary>版本号（ValidEquals 校验必须等于 <see cref="CurrentVersion"/>）。</summary>
    [FieldOffset(4), ValidEquals(CurrentVersion)] public ushort Version;
    /// <summary>Flags（ValidEquals 校验必须等于 <see cref="DefaultFlags"/>）。</summary>
    [FieldOffset(6), ValidEquals(DefaultFlags)]   public ushort Flags;
    /// <summary>payload 字节长度（水位 + opaque 扩展区，策略按实际数据长度填）。</summary>
    [FieldOffset(8)]  public ushort PayloadLength;
    /// <summary>padding 字节长度（补齐策略布局对齐）。</summary>
    [FieldOffset(10)] public ushort PaddingLength;
}
