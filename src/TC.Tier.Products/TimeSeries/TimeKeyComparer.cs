using System.Runtime.InteropServices;
using TC.Tier.Core.Primitives;

namespace TC.Tier.Products.TimeSeries;

/// <summary>
/// TimeKey 专用比较器（时间索引注入——TimeKey 是 record struct 无 IComparable，
/// KeyComparer&lt;TKey&gt;.Default 的 Comparer 路径会运行时炸；Queue 教训直接沿用）。
/// <para>★ 字段字典序（Timestamp 主序 + Tiebreaker 次序）= spec 定案②复合键序——
///   索引键序即时间序，同刻按地址（写入序）稳定交付。</para>
/// </summary>
public sealed class TimeKeyComparer : IKeyComparer<TimeKey>
{
    /// <inheritdoc/>
    public ulong GetHashCode64(TimeKey key)
    {
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<TimeKey>(in key));
        return UnifiedXxHash.Hash64(bytes);
    }

    /// <inheritdoc/>
    public bool Equals(TimeKey x, TimeKey y) => x == y;

    /// <inheritdoc/>
    public int Compare(TimeKey x, TimeKey y)
    {
        int c = x.Timestamp.CompareTo(y.Timestamp);
        return c != 0 ? c : x.Tiebreaker.CompareTo(y.Tiebreaker);
    }
}
