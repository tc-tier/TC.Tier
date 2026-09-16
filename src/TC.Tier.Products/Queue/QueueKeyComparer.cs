using System.Runtime.InteropServices;
using TC.Tier.Core.Primitives;

namespace TC.Tier.Products.Queue;

/// <summary>
/// QueueKey 专用比较器（延迟索引注入——QueueKey 是 record struct 无 IComparable，
/// KeyComparer&lt;TKey&gt;.Default 的 Comparer 路径会运行时炸；哈希走 UnifiedXxHash 与默认同款）。
/// <para>★ 字段字典序（DueTime 主序 + Tiebreaker 次序）= spec 定案④复合键序——
///   同 dueTime 稳定 FIFO（Tiebreaker = record 地址，单调）。</para>
/// </summary>
public sealed class QueueKeyComparer : IKeyComparer<QueueKey>
{
    /// <inheritdoc/>
    public ulong GetHashCode64(QueueKey key)
    {
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<QueueKey>(in key));
        return UnifiedXxHash.Hash64(bytes);
    }

    /// <inheritdoc/>
    public bool Equals(QueueKey x, QueueKey y) => x == y;

    /// <inheritdoc/>
    public int Compare(QueueKey x, QueueKey y)
    {
        int c = x.DueTime.CompareTo(y.DueTime);
        return c != 0 ? c : x.Tiebreaker.CompareTo(y.Tiebreaker);
    }
}
