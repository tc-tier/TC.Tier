using TC.Tier.Runtime.AddressSpace;
using TC.Tier.Runtime.AddressSpace.Leases;

namespace TC.Tier.Runtime.Tests.AddressSpace;

/// <summary>
/// lease 测试辅助扩展——访问 internal 成员（InternalsVisibleTo 已配）。
/// </summary>
internal static class LeaseTestExtensions
{
    /// <summary>经 SegmentTable.CompactLease 入口造 CompactLease（与其他 lease 入口一致，走 factory）。</summary>
    public static CompactLease CompactLeaseForTest(this SegmentTable table, LogicalAddress from, LogicalAddress to)
        => table.CompactLease(from, to);

    /// <summary>找段内 start 处的区间状态——精确匹配 start，找不到返回 0xFF。</summary>
    public static byte ExtentStateAt(this SegmentTable table, int segId, long start)
    {
        using var reader = table.SnapshotSegmentExtents(segId);
        while (reader.MoveNext())
        {
            var (s, _, state, _) = reader.Current;
            if (s == start) return state;
        }
        return 0xFF;
    }

    /// <summary>统计段内某状态的区间数。</summary>
    public static int CountState(this SegmentTable table, int segId, byte state)
    {
        var count = 0;
        using var reader = table.SnapshotSegmentExtents(segId);
        while (reader.MoveNext())
        {
            if (reader.Current.State == state) count++;
        }
        return count;
    }

    /// <summary>段内区间总数（持锁读后释放——避免 ExtentReader 链式 .Count 泄漏 SpinLock）。</summary>
    public static int ExtentCount(this SegmentTable table, int segId)
    {
        using var reader = table.SnapshotSegmentExtents(segId);
        return reader.Count;
    }
}

