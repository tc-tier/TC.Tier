using System.Runtime.CompilerServices;

namespace TC.Tier.Runtime.AddressSpace;

public sealed partial class SegmentTable
{
    // ── lease 诊断跟踪（从 ExtentRecord.LeaseRef 移入——§7.5）──
    private readonly ShardLockWeakReference<Guid, ITrackedLease> _activeLeaseRefs = new();

    /// <summary>
    /// 获取当前所有活跃的 lease 信息（诊断/测试用）。
    /// </summary>
    /// <returns>当前所有活跃 lease 的信息列表。</returns>
    public IEnumerable<LeaseInfo> GetActiveLeases()
    {
        return from lease in _activeLeaseRefs.AllValues where lease.State == LeaseState.Active select new LeaseInfo
        {
            Id = lease.Id,
            Start = lease.Start,
            End = lease.End,
            LeaseState = lease.State,
            CreatedTimestampMs = lease.CreatedTimestampMs,
            SegIds = lease.SegIds.ToArray(),
        };
    }

    /// <summary>
    /// 强制释放 lease（诊断/测试用）。按 leaseId 从诊断表查 lease 并 Rollback。
    /// </summary>
    /// <param name="leaseId">要释放的 lease 的 ID。</param>
    /// <returns>如果找到并释放了 lease，则返回 true；否则返回 false。</returns>
    public bool ForceRelease(Guid leaseId)
    {
        if (!_activeLeaseRefs.TryGet(leaseId, out var lease)) return false;
        if (lease is not { State: LeaseState.Active }) return false;
        lease.Rollback();
        return true;
    }

    // ── 区间表快照（状态机测试用）──

    /// <summary>
    /// 拍指定段的区间表快照（诊断/测试用）——验证 lease 协议的区间状态流转。
    /// </summary>
    public Segment.ExtentReader SnapshotSegmentExtents(int segId)
    {
        if (!TryGetSegmentRaw(segId, out var seg) || seg is null)
            return Segment.ExtentReader.Empty;
        return seg.EnumerateExtents();
    }

    // ── 碎片化监控（运维/外部分析用）──

    /// <summary>
    /// 单段碎片化统计——区间分布、空洞率、可合并建议。
    /// <para>★ 外部碎片化分析入口——决定是否触发 CompactIntervals / RangeCompact。</para>
    /// </summary>
    public SegmentFragmentation GetSegmentFragmentation(int segId)
    {
        if (!TryGetSegmentRaw(segId, out var seg) || seg is null)
            return new SegmentFragmentation { SegId = segId };

        // ★ 零拷贝持锁遍历——不创建 List 副本，大量区间时不浪费内存
        using var reader = seg.EnumerateExtents();
        var frag = new SegmentFragmentation { SegId = segId, TotalExtents = reader.Count };
        while (reader.MoveNext())
        {
            var (start, end, state, _) = reader.Current;
            var bytes = end - start;
            if (ExtentStateCode.IsCommitted(state)) { frag.CommittedCount++; frag.CommittedBytes += bytes; }
            else if (state == ExtentStateCode.Wasted) { frag.WastedCount++; frag.WastedBytes += bytes; }
            else if (ExtentStateCode.IsAborted(state)) { frag.AbortedCount++; frag.AbortedBytes += bytes; }
            else if (ExtentStateCode.IsInFlight(state)) { frag.InFlightCount++; frag.InFlightBytes += bytes; }
        }
        return frag;
    }

    /// <summary>
    /// 全表碎片化汇总——遍历所有有效段，返回每段的碎片化统计。
    /// <para>★ 外部运维工具用——全段表碎片化总览，决定整理策略。</para>
    /// </summary>
    public IEnumerable<SegmentFragmentation> GetTableFragmentation()
    {
        var segs = Volatile.Read(ref _segments);
        var count = Volatile.Read(ref _segCount);
        for (var i = 0; i < count; i++)
        {
            var seg = segs[i];
            if (seg.StableState == StableState.Invalid) continue;
            yield return GetSegmentFragmentation(seg.SegId);
        }
    }
}