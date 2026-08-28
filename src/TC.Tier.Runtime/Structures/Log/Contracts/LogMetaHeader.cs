using System.Runtime.InteropServices;

namespace TC.Tier.Runtime.Structures.Log.Contracts;

/// <summary>
/// LogMeta header（12B 纯规范字段，对齐 RingMetaHeader 范式）。
/// <para>★ 三段式：[LogMetaHeader 12B][LogMetaPayload 64B(+预留扩展)][Crc32Footer 4B]。</para>
/// <para>★ 水位（BeginAddress/TailAddress/CommittedOffset）不在 Header——作为 Payload 区首部（LogMetaPayload）。</para>
/// <para>★ FlushedUntilAddress 删除——新模型用 engine.Flush，引擎 CommittedTail 是 pwrite 水位不等于已 fsync。</para>
/// <para>★ [BinaryLayout] → 源生成器生成 LogMetaHeaderCodec.Write/Read。</para>
/// <para>参见 base.md §3 D。</para>
/// </summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 12)]
public struct LogMetaHeader
{
    internal const uint   Magic          = RecordMagic.LogMeta;
    internal const ushort CurrentVersion = (ushort)((1 << 8) | 0);
    internal const ushort DefaultFlags   = RecordFlags.FLAG_CRC32C | RecordFlags.FLAG_PAYLOAD_2B
                                         | RecordFlags.FLAG_CRC_IN_FOOTER | RecordFlags.FLAG_META_STANDALONE;

    // === 规范字段 (12B，与 RingMetaHeader 完全同构) ===
    /// <summary>Magic 标识（LogMeta 魔数；ValidEquals 校验必须等于 <see cref="Magic"/>）。</summary>
    [FieldOffset(0), ValidEquals(Magic)]            public uint   MagicValue;
    /// <summary>版本号（ValidEquals 校验必须等于 <see cref="CurrentVersion"/>）。</summary>
    [FieldOffset(4), ValidEquals(CurrentVersion)]   public ushort Version;
    /// <summary>Flags（ValidEquals 校验必须等于 <see cref="DefaultFlags"/>）。</summary>
    [FieldOffset(6), ValidEquals(DefaultFlags)]     public ushort Flags;
    /// <summary>payload 字节长度（水位 + opaque 扩展区，由策略 WritePayload 时按实际数据长度填）。</summary>
    [FieldOffset(8)]  public ushort PayloadLength;
    /// <summary>padding 字节长度（补齐策略布局对齐）。</summary>
    [FieldOffset(10)] public ushort PaddingLength;
}
