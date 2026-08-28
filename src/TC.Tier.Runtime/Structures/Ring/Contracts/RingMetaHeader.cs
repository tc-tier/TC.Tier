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
    /// <summary>header magic 常量（RecordMagic.RingMeta，"RMHD"——打开时校验卷身份）。</summary>
    public const uint   Magic          = RecordMagic.RingMeta;          // "RMHD"
    /// <summary>当前 meta 版本号（major=1, minor=0）。</summary>
    public const ushort CurrentVersion = (ushort)((1 << 8) | 0);        // major=1, minor=0
    /// <summary>默认 flags：CRC32C + 2 字节 payload 长度 + CRC 放 footer + 独立 meta record。</summary>
    public const ushort DefaultFlags   = RecordFlags.FLAG_CRC32C | RecordFlags.FLAG_PAYLOAD_2B
                                         | RecordFlags.FLAG_CRC_IN_FOOTER | RecordFlags.FLAG_META_STANDALONE;

    private const int    HeaderSize     = 12;

    // === 规范字段 (12B) ===
    /// <summary>盘上 magic 字段（FieldOffset 0，ValidEquals 校验值必须为 <see cref="Magic"/>）。</summary>
    [FieldOffset(0), ValidEquals(RingMetaHeader.Magic)]          public uint   MagicValue;
    /// <summary>盘上版本字段（FieldOffset 4，ValidEquals 校验值必须为 <see cref="CurrentVersion"/>）。</summary>
    [FieldOffset(4), ValidEquals(RingMetaHeader.CurrentVersion)] public ushort Version;
    /// <summary>盘上 flags 字段（FieldOffset 6，ValidEquals 校验值必须为 <see cref="DefaultFlags"/>）。</summary>
    [FieldOffset(6), ValidEquals(RingMetaHeader.DefaultFlags)]   public ushort Flags;
    /// <summary>payload 字节数（FieldOffset 8，读入 <see cref="RingMetaPayload"/> 的字节范围）。</summary>
    [FieldOffset(8)]  public ushort PayloadLength;
    /// <summary>payload 之后的对齐 padding 字节数（FieldOffset 10）。</summary>
    [FieldOffset(10)] public ushort PaddingLength;
}
