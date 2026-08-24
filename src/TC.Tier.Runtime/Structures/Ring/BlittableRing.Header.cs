using System.Runtime.InteropServices;

namespace TC.Tier.Runtime.Structures.Ring;

/// <summary>
/// Ring record 三段式 header v2.0（对齐 EntryLogHeader/DeltaLogHeader 范式：CRC in Header）。
/// <para>★ 命名空间顶级（非 BlittableRing&lt;TKey&gt; 嵌套）——[BinaryLayout] 生成器按非泛型全名引用，
///   且 header 格式与 TKey 无关（key 长度恒 sizeof(TKey)，由类型静态提供）。</para>
/// <para>★ v2.0（泛型改版）：KeyLength 字段退役——key 长度是类型事实（sizeof(TKey)）不再是盘上事实；
///   PayloadLength 语义保持"header 后 payload 总长"（数据 record = KeySize + ValueLen；meta record = blockSize），
///   扫描推进口 Header + PayloadLength + Padding 不变。</para>
/// <para>★ 规范字段 14B + padding 2B + PreviousAddress(LogicalAddress 16B，8 对齐) + Reserved 4B + CRC32C 4B。</para>
/// <para>★ PreviousAddress 是 LogicalAddress（base.md §2.2 全程 LogicalAddress）——版本链地址大小无关。</para>
/// <para>★ PreviousAddress 的 Offset(long) 必须落在 8 字节对齐边界，否则未对齐 long 访问在某些路径崩溃。</para>
/// <para>★ [BinaryLayout] + [FieldOffset] → 源生成器生成 BlittableRingHeaderCodec.Write/Read。</para>
/// <para>参见 base.md §2.9。</para>
/// </summary>
[BinaryLayout(OrFlags = "Flags", IsEmpty = "MagicValue", Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 40)]
internal struct BlittableRingHeader
{
    public const uint Magic = RecordMagic.BlittableRing; // "BRHD"
    public const ushort CurrentVersion = (ushort)((2 << 8) | 0); // major=2, minor=0——泛型改版（KeyLength 退役，旧盘不认）
    public const ushort DefaultFlags = RecordFlags.FLAG_CRC32C | RecordFlags.FLAG_PAYLOAD_4B;
    public const int Alignment = 8;

    // === 规范字段 (14B) ===
    [FieldOffset(0), ValidEquals(Magic)] public uint MagicValue;

    [FieldOffset(4), ValidEquals(CurrentVersion)]
    public ushort Version;

    [FieldOffset(6), ValidHasFlags(DefaultFlags)]
    public ushort Flags;

    [FieldOffset(8)] public uint PayloadLength; // = KeySize + ValueLength（数据）/ blockSize（meta）

    [FieldOffset(12)] public ushort PaddingLength;

    // === Ring record 独有字段（PreviousAddress 8 对齐 @16）===
    [FieldOffset(16)] public LogicalAddress PreviousAddress; // 16B，版本链（hybrid log 多版本/undo 依赖）

    [FieldOffset(32)] public uint Reserved; // 4B 保留（v1 KeyLength@32 + Reserved@34 合并；写零防 CRC 脏字节）

    // === CRC32C in Header (4B) ===
    [FieldOffset(36)] public uint Crc32C;
    // ★ CRC 偏移用 SG 生成的 BlittableRingHeaderCodec.Offset_Crc32C，禁止手写重复常量。
}
