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
    /// <summary>meta header magic——"MMHD"（MirrorBase 的 meta 水位标识）。</summary>
    public const uint Magic = RecordMagic.MirrorMeta;

    /// <summary>当前版本号（major=1, minor=0）。</summary>
    public const ushort CurrentVersion = (ushort)((1 << 8) | 0);

    /// <summary>默认 flags——CRC32C + payload 2B 长度段 + CRC 在 Footer + 独立 meta 模式。</summary>
    public const ushort DefaultFlags = RecordFlags.FLAG_CRC32C
                                      | RecordFlags.FLAG_PAYLOAD_2B
                                      | RecordFlags.FLAG_CRC_IN_FOOTER
                                      | RecordFlags.FLAG_META_STANDALONE;

    private const int HeaderSize = 12;

    /// <summary>盘上 magic 字段（必须等于 <see cref="Magic"/>）。</summary>
    [FieldOffset(0), ValidEquals(Magic)]          public uint   MagicValue;

    /// <summary>盘上版本字段（必须等于 <see cref="CurrentVersion"/>）。</summary>
    [FieldOffset(4), ValidEquals(CurrentVersion)] public ushort Version;

    /// <summary>盘上 flags 字段（必须等于 <see cref="DefaultFlags"/>）。</summary>
    [FieldOffset(6), ValidEquals(DefaultFlags)]   public ushort Flags;

    /// <summary>payload 长度（结构化水位 + opaque 实际用量——自描述锚点）。</summary>
    [FieldOffset(8)]  public ushort PayloadLength;

    /// <summary>padding 长度。</summary>
    [FieldOffset(10)] public ushort PaddingLength;
}
