using System.Runtime.CompilerServices;

namespace TC.Tier.Runtime.AddressSpace.Extensions;

/// <summary>
/// Segment 的区间表扩展方法——二分查找
/// </summary>
internal static class SegmentExtentTableExtensions
{
    /// <summary>
    /// 返回包含 offset 的区间的 index（Start ≤ offset 小于 End）。不存在返回 -1。
    /// </summary>
    /// <param name="list">区间列表。</param>
    /// <param name="offset">偏移量。</param>
    /// <returns>包含 offset 的区间的 index，若不存在则返回 -1。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int FindContainingIndex(this List<ExtentRecord> list, long offset)
    {
        int lo = 0, hi = list.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            if (list[mid].Start <= offset)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return found;
    }

    /// <summary>
    /// 返回应插入 offset 的 index（Start ≥ offset）。若所有区间的 Start 都小于 offset，则返回 list.Count。
    /// </summary>
    /// <param name="list">区间列表。</param>
    /// <param name="offset">偏移量。</param>
    /// <returns>应插入 offset 的 index。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int FindInsertIndex(this List<ExtentRecord> list, long offset)
    {
        int lo = 0, hi = list.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (list[mid].Start < offset)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo;
    }
}