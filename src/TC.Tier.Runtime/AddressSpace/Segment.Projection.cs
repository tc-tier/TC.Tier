using TC.Tier.Runtime.AddressSpace.Extensions;

namespace TC.Tier.Runtime.AddressSpace;

public sealed partial class Segment
{
    // ═══════════════════════════════════════════
    //  四投影刷新（需持 _extentLock）
    // ═══════════════════════════════════════════

    /// <summary>
    /// 刷新四投影（可全量或增量）。
    /// </summary>
    /// <param name="start">改动区间的起始偏移。</param>
    /// <param name="end">改动区间的结束偏移。</param>
    /// <param name="becameCommitted">指示区间是否变为 Committed 状态。</param>
    public void RefreshProjection(long start, long end, bool becameCommitted=false)
    {
        if (_extentList.Count <= _compactThreshold)
            RebuildProjections();
        else
            RefreshProjectionsIncremental(start, end, becameCommitted);
    }
    #region 私有方法

    /// <summary>
    /// 原子写 minOutstanding 双字段（seqlock）——修 2.4 双字段撕裂读。
    /// <para>★ 调用方须已持 _extentLock（单写者保证，故用 Volatile.Write 而非 Interlocked）。</para>
    /// <para>★ 版本号先增为奇数（写入中），写完再增为偶数（稳定）；读侧 IsRangeFullyReadable 循环校验。</para>
    /// </summary>
    private void WriteOutstandingUnsafe(long minOs, long minOsEnd)
    {
        var v = _minOutstandingVersion;
        Volatile.Write(ref _minOutstandingVersion, v + 1);   // 奇数=写入中
        Volatile.Write(ref _minOutstandingStart, minOs);
        Volatile.Write(ref _minOutstandingEnd, minOsEnd);
        Volatile.Write(ref _minOutstandingVersion, v + 2);   // 偶数=稳定
    }

    /// <summary>
    /// 全量重建四投影（CompactIntervals/Reclaim 用——批量变更后重算）。
    /// <para>★ 热路径用 RefreshProjectionsIncremental（O(1)），此方法仅偶发全量场景用。</para>
    /// </summary>
    private void RebuildProjections()
    {
        if (_extentList.Count == 0)
        {
            Volatile.Write(ref _visibleOffset, 0L);
            Volatile.Write(ref _contiguousOffset, 0L);
            WriteOutstandingUnsafe(long.MaxValue, 0L);
            return;
        }

        long maxEnd = 0;
        long contiguous = 0;
        var foundGap = false;
        var minOs = long.MaxValue;
        long minOsEnd = 0;

        foreach (var r in _extentList)
        {
            if (r.End > maxEnd) maxEnd = r.End;

            if (!foundGap && r.State != ExtentStateCode.Committed)
            {
                contiguous = r.Start;
                foundGap = true;
            }

            // ★ 非 Committed 区间（Leased/Reclaiming/Aborted/Wasted）都不可读，参与 minOs 投影。
            //   STORAGE-026 (#246)：Aborted/Wasted 虽非在途，但是不可读的洞——读到即垃圾，须与在途同等阻断。
            if (r.State == ExtentStateCode.Committed) continue;
            if (r.Start < minOs)
            {
                minOs = r.Start;
                minOsEnd = r.End;
            }

            // ★ STORAGE-024 (#244)：minOsEnd 取所有非 Committed 区间的最大 End（不止 minStart 区间），
            //   否则多区间下另一非 Committed 区间落在读范围内会漏判。保守：读范围与任一非 Committed
            //   区间可能重叠即进慢路径 ClampReadable 精确判断。
            if (r.End > minOsEnd) minOsEnd = r.End;
        }

        Volatile.Write(ref _contiguousOffset, foundGap ? contiguous : maxEnd);
        Volatile.Write(ref _visibleOffset, maxEnd);
        // ★ seqlock 原子写 minOutstanding 双字段（修 2.4：双字段撕裂读致 IsRangeFullyReadable 误判）
        WriteOutstandingUnsafe(minOs, minOsEnd);
    }

    /// <summary>
    /// 增量刷新投影——热路径用（真正 O(1)）。
    /// <para>★ visibleOffset：End 单调，取 max。</para>
    /// <para>★ minOutstanding：保守不变量（L11 修复 2026-08-21，对齐 RebuildProjections 的
    ///   STORAGE-024 语义）：minOs 单调递减可（新区间更小则取代），但 <b>minOsEnd 只放大不缩小</b>——
    ///   它是"任一非 Committed 区间的最大 End"的保守下界，缩小即出现漏判窗口（在途写被读快路径放行
    ///   → 撕裂读，探针实锤）。becameCommitted 路径扫不到后继非 Committed 时同样保守：minOsEnd
    ///   若仍 > changedEnd 说明存在更靠右的在途区间（非全量扫不可知），降级 Rebuild。</para>
    /// <para>★ contiguousOffset：热路径不维护——仅 CompactIntervals/Rebuild 全量算。</para>
    /// <para>★ safe-side：增量保守，读快路径宁可进慢路径不误放。</para>
    /// </summary>
    /// <param name="changedStart">改动区间的 start。</param>
    /// <param name="changedEnd">改动区间的 end。</param>
    /// <param name="becameCommitted">true=区间变 Committed（CompleteAndMerge）；false=新增/变非 Committed（Insert/Abort/MarkWasted）。</param>
    private void RefreshProjectionsIncremental(long changedStart, long changedEnd, bool becameCommitted)
    {
        // visibleOffset：End 单调递增
        if (changedEnd > Volatile.Read(ref _visibleOffset))
            Volatile.Write(ref _visibleOffset, changedEnd);

        var curMinOs = Volatile.Read(ref _minOutstandingStart);
        var curMinOsEnd = Volatile.Read(ref _minOutstandingEnd);

        if (becameCommitted)
        {
            // 区间变 Committed——仅当它是当前最小在途时才需找下一个
            if (changedStart != curMinOs) return;
            var idx = _extentList.FindContainingIndex(changedStart);
            if (idx < 0) return;
            long nextOs = long.MaxValue;
            long nextOsEnd = 0;
            for (var i = idx + 1; i < _extentList.Count; i++)
            {
                if (_extentList[i].State != ExtentStateCode.Committed)
                {
                    nextOs = _extentList[i].Start;
                    nextOsEnd = _extentList[i].End;
                    break;
                }
            }

            // ★ L11 保守化：找不到后继在途但旧 minOsEnd 仍 > changedEnd = 存在非后继相邻的在途区间
            //   （多重在途形态），增量视角不可知全体最大 End——降级全量重建（安全侧）
            if (nextOs == long.MaxValue && curMinOsEnd > changedEnd)
            {
                RebuildProjections();
                return;
            }
            // 找到后继：nextOsEnd 取 max（后继之后可能还有更靠右的在途——保留旧下界）
            if (nextOsEnd < curMinOsEnd && curMinOsEnd != 0) nextOsEnd = curMinOsEnd;

            WriteOutstandingUnsafe(nextOs, nextOsEnd);
        }
        else
        {
            // 新增/变非 Committed
            if (changedStart < curMinOs)
            {
                // ★ L11 保守化：新 minOsEnd 取 max(changedEnd, 旧值)——只放大不缩小
                var newEnd = changedEnd > curMinOsEnd ? changedEnd : curMinOsEnd;
                WriteOutstandingUnsafe(changedStart, newEnd);
            }
            else if (changedEnd > curMinOsEnd)
            {
                // start 不小于当前 minOs 但 End 更靠右——在途区间族的最大 End 增大（单调，安全更新）
                WriteOutstandingUnsafe(curMinOs, changedEnd);
            }
        }
    }

    #endregion
}