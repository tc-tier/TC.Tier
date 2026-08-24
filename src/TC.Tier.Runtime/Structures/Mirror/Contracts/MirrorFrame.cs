using System.Runtime.InteropServices;

namespace TC.Tier.Runtime.Structures.Mirror.Contracts;

/// <summary>
/// Mirror 体系统一版本帧 header（共享布局——两种镜像同族格式，差异只在 magic，归 codec）。
/// <para>布局：[magic 4B][version 2B][flags 2B][PageId 8B][LogicalAddress 8B][MirrorVersion 8B] = 32B。</para>
/// <para>★ 流式帧教义：格式零长度字段——帧长 = 尾位−头（推导的事实）；"写时已知长度"是写侧
///   便利与内存账面，不进盘上格式。帧判定链零长度依赖：双 magic 匹配 + CRC 过 + 版本合法
///   （magic 只提名候选，CRC 才是裁决；假命中 → CRC 必不过 → 跳过重同步；真 magic 永不缺席）。</para>
/// <para>★ PageId/LogicalAddress 为 PagedMirror 字段（WholeMirror 恒 0）——共享布局统一优先，
///   字段冗余一帧一次可接受。MirrorVersion 头尾双写：头版本供前向走链早判，尾版本供尾锚免回头。</para>
/// <para>源生成器（[BinaryLayout]）生成 MirrorFrameHeaderCodec。magic 归 codec（读写按 codec 的
///   HeaderMagic 校验——struct 上不做 ValidEquals）。</para>
/// </summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = HeaderSize)]
public struct MirrorFrameHeader
{
    public const ushort CurrentVersion = (ushort)((2 << 8) | 0); // major=2, minor=0

    private const int HeaderSize = 32;

    /// <summary>帧头 magic（codec 填写/校验——WMHD/PMHD）。</summary>
    [FieldOffset(0)] public uint MagicValue;

    [FieldOffset(4), ValidEquals(CurrentVersion)]
    public ushort Version;

    /// <summary>flags（CRC 算法位 + FLAG_LAST_PARTIAL / FLAG_ENTRY_IS_META 变体——读侧校验算法位）。</summary>
    [FieldOffset(6)] public ushort Flags;

    /// <summary>页标识（PagedMirror per-page 链键；WholeMirror 恒 0）。</summary>
    [FieldOffset(8)] public long PageId;

    /// <summary>源页逻辑地址（PagedMirror 随页透传；WholeMirror 恒 0）。</summary>
    [FieldOffset(16)] public long LogicalAddress;

    /// <summary>checkpoint 版本号（头尾双写一致）。</summary>
    [FieldOffset(24)] public long MirrorVersion;
}

/// <summary>
/// Mirror 体系统一版本帧 footer（共享布局——长度/链指针/版本/CRC 全在尾）。
/// <para>布局：[magic 4B][version 2B][flags 2B][PreviousVersion 16B][MirrorVersion 8B][Crc 8B] = 40B。</para>
/// <para>★ CRC 覆盖 <b>头（32B 全部）+ payload + 尾 [0,32)（Crc 字段之前）</b>——写侧边写边累积
///   收官落尾，全程不需要知道总长；读侧从头读到尾 magic 为止流式验证。</para>
/// <para>★ 尾锚恢复：Locate(footer magic, Last) 直达最新帧尾——旧代再烂也遮不住新代；
///   PreviousVersion 回跳一步 = N=2 第二新（旧帧只验结构不读体）。</para>
/// </summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = FooterSize)]
public struct MirrorFrameFooter
{
    public const ushort CurrentVersion = (ushort)((2 << 8) | 0); // major=2, minor=0

    private const int FooterSize = 40;

    /// <summary>Crc 字段之前的字节数（CRC 覆盖域的尾前缀长度）。</summary>
    public const int CrcPrefixSize = 32;

    /// <summary>帧尾 magic（codec 填写/校验——WMFT/PMFT）。</summary>
    [FieldOffset(0)] public uint MagicValue;

    [FieldOffset(4), ValidEquals(CurrentVersion)]
    public ushort Version;

    /// <summary>flags（与头一致）。</summary>
    [FieldOffset(6)] public ushort Flags;

    /// <summary>版本链指针（WholeMirror=全局上一帧头；PagedMirror=页内上一帧头；链尾哨兵 = <see cref="LogicalAddress.Invalid"/>——Empty 是合法 seg0@0）。</summary>
    [FieldOffset(8)] public LogicalAddress PreviousVersion;

    /// <summary>checkpoint 版本号（与头双写一致——尾锚免回头）。</summary>
    [FieldOffset(24)] public long MirrorVersion;

    /// <summary>CRC（覆盖 头 + payload + 尾前缀 [0,32)；算法位在 flags——CRC64/CRC32C）。</summary>
    [FieldOffset(32)] public ulong Crc;
}

/// <summary>帧几何账目（基类帧账面的值类型：head→footer 对 + 头尾字段快照）。</summary>
/// <param name="Head">帧头地址。</param>
/// <param name="FooterAddress">帧尾（footer）地址。</param>
/// <param name="Header">帧头字段。</param>
/// <param name="Footer">帧尾字段。</param>
public readonly record struct MirrorFrameInfo(
    LogicalAddress Head,
    LogicalAddress FooterAddress,
    MirrorFrameHeader Header,
    MirrorFrameFooter Footer);

/// <summary>
/// 版本帧链拓扑（格式语义的一部分——归 codec 声明，基类按声明分派恢复编排）。
/// <para>★ 机制归基类：两种拓扑的恢复编排（尾锚快速路径 / 全走链）全在 MirrorBase，
///   子类零 override 恢复逻辑——链拓扑差异经 codec 声明进基类分派。</para>
/// </summary>
public enum MirrorChainKind
{
    /// <summary>全局单链（WholeMirror）：最新帧即链头——尾锚直达 + PreviousVersion 回跳 N=2 第二新。</summary>
    Single,

    /// <summary>per-key 多链（PagedMirror：PageId 为键）：恢复须全走链按 PageId 重建各链头。</summary>
    PerKey,
}
