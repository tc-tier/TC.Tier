using System.Runtime.InteropServices;

namespace TC.Tier.Contracts.Storage;

/// <summary>
/// 逻辑地址（段号 + 段内偏移 + 扩展字段），用于定位数据在持久化存储中的位置。
/// </summary>
[BinaryLayout(Features = BinaryLayoutFeatures.StructSize)]
[StructLayout(LayoutKind.Explicit, Size = 16)]
public readonly struct LogicalAddress : IEquatable<LogicalAddress>, IComparable<LogicalAddress>
{
    /// <summary>段号（文件序号 data.{segId}）。</summary>
    [FieldOffset(0)] public readonly int SegId;

    /// <summary>内部扩展字段（ABA 防护）。</summary>
    [FieldOffset(4)] public readonly int Extension;

    /// <summary>段内字节偏移。</summary>
    [FieldOffset(8)] public readonly long Offset;

    /// <summary>零值地址（段号 0、扩展字段 0、偏移 0）——seg0 的起点，是<b>合法地址</b>，不是"无效"哨兵。</summary>
    public static readonly LogicalAddress Empty;

    /// <summary>
    /// 无效地址哨兵（段号 -1、扩展字段 -1、偏移 -1）——表示"无值/未初始化/越界"。
    /// <para>★ 与 <see cref="Empty"/> (0,0) 区分：Empty 是合法的 seg0 起点，Invalid 才是"没有值"。</para>
    /// <para>★ 校验用 <see cref="IsValid"/>：<c>SegId &gt;= 0 &amp;&amp; Offset &gt;= 0</c>（Invalid 的 SegId=-1 或 Offset=-1 不满足）。</para>
    /// </summary>
    public static readonly LogicalAddress Invalid = new(segId: -1, extension: -1, offset: -1);

    /// <summary>
    /// 地址是否有效——SegId &gt;= 0 &amp;&amp; Offset &gt;= 0（Invalid 哨兵的 SegId=-1 返回 false，Empty 的 SegId=0 返回 true）。
    /// <para>★ 用于区分"合法地址（含 seg0 起点 Empty）"与"无效/未初始化/越界（Invalid）"。</para>
    /// </summary>
    public bool IsValid => SegId >= 0 && Offset >= 0;

    /// <summary>公开构造——上层/持久化恢复用，extension 置 0。</summary>
    /// <param name="segId">段号。</param>
    /// <param name="offset">段内字节偏移。</param>
    public LogicalAddress(int segId, long offset)
    {
        SegId = segId;
        Extension = 0;
        Offset = offset;
    }

    /// <summary>
    /// 内部构造——仅供内部使用，允许指定 extension。
    /// </summary>
    /// <param name="segId">段号。</param>
    /// <param name="extension">扩展字段(承载 ABA version 语义)。</param>
    /// <param name="offset">段内字节偏移。</param>
    public LogicalAddress(int segId, int extension, long offset)
    {
        SegId = segId;
        Extension = extension;
        Offset = offset;
    }

    /// <summary>相等比较（仅基于 SegmentId + FileOffset，extension 不参与）。</summary>
    public bool Equals(LogicalAddress other)
        => SegId == other.SegId && Offset == other.Offset;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is LogicalAddress other && Equals(other);

    /// <summary>哈希码（仅基于 SegmentId + FileOffset，extension 不参与）。</summary>
    public override int GetHashCode() => HashCode.Combine(SegId, Offset);

    /// <summary>排序——先 SegmentId 后 FileOffset，extension 不参与。</summary>
    public int CompareTo(LogicalAddress other)
    {
        var segCmp = SegId.CompareTo(other.SegId);
        return segCmp != 0 ? segCmp : Offset.CompareTo(other.Offset);
    }

    /// <summary>字符串形态：<c>seg#{SegId}@0x{Offset:X}</c>。</summary>
    public override string ToString() => $"seg#{SegId}@0x{Offset:X}";

    /// <summary>相等（仅基于 SegmentId + FileOffset，extension 不参与）。</summary>
    public static bool operator ==(LogicalAddress left, LogicalAddress right) => left.Equals(right);
    /// <summary>不等（仅基于 SegmentId + FileOffset，extension 不参与）。</summary>
    public static bool operator !=(LogicalAddress left, LogicalAddress right) => !left.Equals(right);
    /// <summary>大于——先 SegmentId 后 FileOffset。</summary>
    public static bool operator >(LogicalAddress left, LogicalAddress right) => left.CompareTo(right) > 0;
    /// <summary>小于——先 SegmentId 后 FileOffset。</summary>
    public static bool operator <(LogicalAddress left, LogicalAddress right) => left.CompareTo(right) < 0;
    /// <summary>大于等于——先 SegmentId 后 FileOffset。</summary>
    public static bool operator >=(LogicalAddress left, LogicalAddress right) => left.CompareTo(right) >= 0;
    /// <summary>小于等于——先 SegmentId 后 FileOffset。</summary>
    public static bool operator <=(LogicalAddress left, LogicalAddress right) => left.CompareTo(right) <= 0;
}