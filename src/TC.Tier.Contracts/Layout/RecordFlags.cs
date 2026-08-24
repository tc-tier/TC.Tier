// ReSharper disable InconsistentNaming

using System.Diagnostics.CodeAnalysis;

namespace TC.Tier.Contracts.Layout;

/// <summary>
/// 统一二进制布局 flags 常量
/// 低 8 位 = 跨类型统一标志；高 8 位 = 类型自定义标志。
/// </summary>
[SuppressMessage("Naming", "CA1707:标识符不应包含下划线")]
public static class RecordFlags
{
    #region 系统默认 Layout Flag

    // ══ CRC 算法段（bits 0-1）══
    /// <summary>
    /// 无 CRC（不计算 CRC，flags 低 2 位 = 0）。
    /// </summary>
    public const ushort FLAG_CRC_NONE = 0x00;

    /// <summary>
    /// CRC32 算法（IEEE 802.3 多项式，Intel SSE4.2 指令集硬件加速）。
    /// </summary>
    public const ushort FLAG_CRC32 = 0x01;

    /// <summary>
    /// CRC64 算法（ECMA-182 多项式，Intel SSE4.2 指令集硬件加速）。
    /// </summary>
    public const ushort FLAG_CRC64 = 0x02;

    /// <summary>
    /// CRC32C 算法（Castagnoli 多项式，Intel SSE4.2 指令集硬件加速）。
    /// </summary>
    public const ushort FLAG_CRC32C = 0x03;

    /// <summary>
    /// CRC 算法掩码（用于提取 flags 的 CRC 算法段，0/4/8 字节）。
    /// </summary>
    public const ushort FLAG_CRC_MASK = 0x03;

    // ══ CRC 位置段（bit 2）══
    /// <summary>
    /// CRC 字段在 Header 末尾（flags bit2 = 0，Header 末尾 + payload + padding 覆盖范围）。
    /// </summary>
    public const ushort FLAG_CRC_IN_FOOTER = 0x04;

    // ══ payloadMaxSize 段（bits 3-4）══
    /// <summary>
    /// payloadMaxSize = 2B（flags bits3-4 = 00）。
    /// </summary>
    public const ushort FLAG_PAYLOAD_2B = 0x08;

    /// <summary>
    /// payloadMaxSize = 4B（flags bits3-4 = 01）。
    /// </summary>
    public const ushort FLAG_PAYLOAD_4B = 0x10;

    /// <summary>
    /// payloadMaxSize = 8B（flags bits3-4 = 10）。
    /// </summary>
    public const ushort FLAG_PAYLOAD_8B = 0x18;

    /// <summary>
    /// payloadMaxSize 掩码（用于提取 flags 的 payloadMaxSize 段，2/4/8 字节）。
    /// </summary>
    public const ushort FLAG_PAYLOAD_MASK = 0x18;

    // ══ 元数据模式段（bits 5-6）══
    /// <summary>
    /// meta 模式段：嵌入式 meta（flags bits5-6 = 01）。
    /// </summary>
    public const ushort FLAG_META_EMBEDDED = 0x20;

    /// <summary>
    /// meta 模式段：独立 meta（flags bits5-6 = 10）。
    /// </summary>
    public const ushort FLAG_META_STANDALONE = 0x40;

    /// <summary>
    /// meta 模式段掩码（用于提取 flags 的 meta 模式段，嵌入式/独立）。
    /// </summary>
    public const ushort FLAG_META_MASK = 0x60;

    // ══ per-entry 标记（bit 7）══
    /// <summary>
    /// entry 是 meta（flags bit7 = 1，meta record 用于嵌入式 meta 或独立 meta）。
    /// </summary>
    public const ushort FLAG_ENTRY_IS_META = 0x80;

    // ══ 跨类型通用高位常量 ══
    /// <summary>
    /// footer 魔数标记。
    /// </summary>
    public const ushort FLAG_FOOTER_MAGIC = 0x1000;

    #endregion

    // ══ 类型自定义常量（高 8 位，由 magic 区分上下文）══

    // EntryLog codecId（bits 8-11）
    /// <summary>
    /// codecId = 0（flags bits8-11 = 0000，EntryLog V0）。
    /// </summary>
    public const ushort FLAG_CODEC_V0 = 0x0000;

    /// <summary>
    /// codecId = 1（flags bits8-11 = 0001，EntryLog V1）。
    /// </summary>
    public const ushort FLAG_CODEC_V1 = 0x0100;

    /// <summary>
    /// codecId = 2（flags bits8-11 = 0010，EntryLog V2）。
    /// </summary>
    public const ushort FLAG_CODEC_MASK = 0x0F00;

    // PageMirror 末页标记
    /// <summary>
    /// 末页标记（flags bit8 = 1，PageMirror 最后一页）。
    /// </summary>
    public const ushort FLAG_LAST_PARTIAL = 0x0100;

    // BlittableRing record 标志位（bits 8-11 = record 状态位；bit 13 = overflow 位；bit 12 (0x1000) 保留给 FLAG_FOOTER_MAGIC）
    /// <summary>record 有效（非空位）。</summary>
    public const ushort FLAG_RINGRECORD_VALID = 0x0100;

    /// <summary>墓碑记录（删除标记）。</summary>
    public const ushort FLAG_RINGRECORD_TOMBSTONE = 0x0200;

    /// <summary>record 已 Seal（不可再写）。</summary>
    public const ushort FLAG_RINGRECORD_SEALED = 0x0400;

    /// <summary>新版本记录（CPR/版本切换用）。</summary>
    public const ushort FLAG_RINGRECORD_INNEWVERSION = 0x0800;

    /// <summary>Value 溢出到溢出设备（payload 含 AddressInfo，非内联 Value）。★ 用 bit 13 (0x2000) 避开 FLAG_FOOTER_MAGIC(0x1000)。</summary>
    public const ushort FLAG_VALUE_OVERFLOW = 0x2000;

    // ══ 辅助方法 ══

    /// <summary>由 flags 提取 CRC 字段长度（0/4/8）。</summary>
    public static int GetCrcLen(ushort flags) => (flags & FLAG_CRC_MASK) switch
    {
        FLAG_CRC_NONE => 0,
        FLAG_CRC32 or FLAG_CRC32C => 4,
        FLAG_CRC64 => 8,
        _ => 0
    };
}