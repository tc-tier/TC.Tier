using System.Runtime.CompilerServices;
using TC.Tier.Runtime.AddressSpace.Extensions;

namespace TC.Tier.Runtime.AddressSpace;

/// <summary>
/// Segment 状态机方法 partial——所有 StableState 流转方法 + 水位管理。
/// <para>★ 无 observer/事件派发——事件改走段表 ISegmentHandler（删 EnqueueChange/FlushPendingEvents）。</para>
/// </summary>
public sealed partial class Segment
{
    // ═══ 水位管理三方法（统一命名：动词+Offset，绑定区间表）═══

    /// <summary>
    /// CAS 单调推进 MaxOffset（写后调用）。
    /// <para>★ 达 GrowthLimit 自动 MarkFull。推进水位不绑区间表——区间记录由 lease Commit 独立操作。</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long AdvanceOffset(long newOffset)
    {
        // 单调推进（不允许回退）——统一用底层 Utility.MonotonicUpdate
        if (!Utility.MonotonicUpdate(ref _maxOffset, newOffset, out long current))
            return current;   // newOffset <= 当前值，不推进
        // ★ 写满自动转 Full（Ready→Full 单调，volatile 发布；非 Ready 不转——防御性静默跳过与旧实现一致）
        if (newOffset < GrowthLimit) return newOffset;
        var cur = StableState;
        if (cur != StableState.Ready) return newOffset;
        Volatile.Write(ref _stateCode, (int)StableState.Full);
        return newOffset;
    }

    /// <summary>
    /// 回退 MaxOffset
    /// </summary>
    /// <param name="maxOffset">新的最大偏移量</param>
    /// <exception cref="InvalidOperationException">当 newOffset 大于当前最大偏移量时抛出</exception>
    public void RetreatOffset(long maxOffset)
    {
        using var lk = AcquireExtentLock();

        {
            if (maxOffset > _maxOffset)
                throw new InvalidOperationException(
                    $"RetreatOffset 非法前进：newOffset={maxOffset} > current={_maxOffset}");
            _maxOffset = maxOffset;
            // ★ 3.3：截断后若段卡在 Full，恢复 Ready（保持不变量 StableState.Full ⟺ MaxOffset≥GrowthLimit；
            //   Full↔Ready 随水位伸缩双向，非物理门轴——不动单向闩）
            if (StableState == StableState.Full)
                Volatile.Write(ref _stateCode, (int)StableState.Ready);
            var idx = _extentList.FindInsertIndex(maxOffset);
            if (idx < _extentList.Count)
                _extentList.RemoveRange(idx, _extentList.Count - idx);

            // 截断最后一个跨越 newOffset 的区间
            if (idx > 0 && _extentList[idx - 1].End > maxOffset)
            {
                var last = _extentList[idx - 1];
                _extentList[idx - 1] =
                    new ExtentRecord(last.Start, maxOffset, last.State, last.Sparse, last.Version + 1);
            }

            RebuildProjections(); // 批量删除——全量重建
        }
    }

    /// <summary>
    /// 推进最小偏移量（变大）——ReclaimHead 段内打洞后头部回收用。
    /// <para>★ 与 <see cref="AdvanceOffset"/> 对称：MaxOffset 前进（Append）/ MinOffset 前进（ReclaimHead）。</para>
    /// <para>★ 删除 [old, new) 的区间记录——这些区间已被回收，不再属于本段。</para>
    /// <para>★ RealSize = MaxOffset - MinOffset 收缩（不含已回收头部）。</para>
    /// </summary>
    /// <param name="minOffset">新的最小偏移量（必须 ≥ 当前值，否则为 NoOp）。</param>
    public void AdvanceMinOffset(long minOffset)
    {
        // ★ 只前进——ReclaimHead 头部回收单调推进。回退调 RetreatMinOffset。
        if (minOffset <= _minOffset) return;
        _minOffset = minOffset;
        using var lk = AcquireExtentLock();

        {
            var idx = _extentList.FindInsertIndex(minOffset);
            if (idx > 0)
            {
                // 截断跨越 minOffset 的区间（[start, end) 中 start < minOffset < end）
                bool truncated = false;
                if (_extentList[idx - 1].End > minOffset)
                {
                    var last = _extentList[idx - 1];
                    _extentList[idx - 1] = new ExtentRecord(minOffset, last.End, last.State, last.Sparse);
                    truncated = true;
                }

                // 删除 minOffset 之前的完整区间：发生截断时保留截断后的 extent（删 idx-1 个），
                //   未截断（minOffset 恰对齐 extent 边界）时删 idx 个。
                if (_extentList[0].End <= minOffset)
                    _extentList.RemoveRange(0, truncated ? idx - 1 : idx);
            }

            RebuildProjections(); // 批量删除——全量重建
        }
    }

    /// <summary>
    /// 回退最小偏移量（变小）——Recovery 物理对齐用。
    /// <para>★ 与 <see cref="RetreatOffset"/> 对称：MaxOffset 回退（ReclaimTail）/ MinOffset 回退（Recovery）。</para>
    /// <para>★ 不删区间记录——回退意味着之前回收的头部又算回来，区间表保持不动由 RebuildProjections 重建。</para>
    /// </summary>
    /// <param name="minOffset">新的最小偏移量（必须 ≤ 当前值，否则为 NoOp）。</param>
    public void RetreatMinOffset(long minOffset)
    {
        // ★ 只回退——Recovery 对齐单调回退。前进调 AdvanceMinOffset。
        if (minOffset >= _minOffset) return;
        _minOffset = minOffset;
        // 不删区间记录——回退场景区间表由调用方负责（Recovery 重建段时区间表本就空）
    }

    // ═══ 稳态流转（统一 MarkXxx：目标态即方法名后缀）═══
    /// <summary>
    /// 将 [start, end) 区间标记为 Aborted
    /// <para>Reclaim 失败回滚——punch/commit 非原子窗口使数据二态未知（完好/已归零），
    /// 读保守拒绝（落 Committed = 静默数据错误，比挂死危险——XIX 决策保留）。</para>
    /// </summary>
    /// <para>1：可以被 Compact 整理</para>
    /// <para>2：可以被 Reclaim 族（中间/头/尾）幂等重占——再 punch 两分支收敛同终态（L1，），
    /// <c>Failed.lastPunchedOffset</c> 断点重试由此可达</para>
    /// <para>3：Write/Append 不可占（占用矩阵 §7.2 不变）</para>
    /// <param name="start">区间起始偏移量（包含）。</param>
    /// <param name="end">区间结束偏移量（不包含）。</param>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    internal void MarkAbort(long start, long end)
    {
        using var lk = AcquireExtentLock();

        {
            var idx = FindLeasedExtentIndex(start);
            if (idx < 0) return;

            var rec = _extentList[idx];
            // ★ 2.1：只标记在途态——防御 FindLeasedExtentIndex 回退分支命中 Committed 等终态导致覆写
            if (!ExtentStateCode.IsInFlight(rec.State)) return;
            rec.State = ExtentStateCode.Aborted;
            rec.Sparse = true;
            _extentList[idx] = rec;
            RemoveOutstanding(rec.Start, rec.End);   // ★ L19 收口：在途小表配对注销
            // ★ 始终增量投影（O(1)）——与 InsertUnsafe 一致，避免 O(N) 全量重建
            RefreshProjectionsIncremental(start, end, becameCommitted: false);
        }
    }

    /// <summary>
    /// 标记区间为 Wasted 空洞
    /// <para>Reclaim 失败，旧数据已物理清除，Wasted 区间是临时洞。</para>
    /// <para>Append 失败，数据不完整，Wasted 区间是临时洞。</para>
    /// <para>Allocate 成功失败只分配了地址，没有数据，Wasted 区间是临时洞。</para>
    /// </summary>
    /// <para>1：可以被 Write 覆写</para>
    /// <para>2：可以被 Reclaim 清理</para>
    /// <param name="start">区间起始偏移量（包含）。</param>
    /// <param name="end">区间结束偏移量（不包含）。</param>
    public void MarkWasted(long start, long end)
    {
        using var lk = AcquireExtentLock();

        {
            var idx = FindLeasedExtentIndex(start);
            if (idx < 0) return;
            var rec = _extentList[idx];
            // ★ 2.1：只标记在途态——同 MarkAbort
            if (!ExtentStateCode.IsInFlight(rec.State)) return;
            rec.State = ExtentStateCode.Wasted;
            _extentList[idx] = rec;
            RemoveOutstanding(rec.Start, rec.End);   // ★ L19 收口：在途小表配对注销
            // ★ 始终增量投影（O(1)）——与 InsertUnsafe 一致
            RefreshProjectionsIncremental(start, end, becameCommitted: false);
        }
    }

    // ═══ 稳态流转（统一 MarkXxx：目标态即方法名后缀——int 背板 CAS 迁移 + 单向闩，零锁）═══

    /// <summary>
    /// CAS 迁移 Empty→Ready + 单向闩 Set——协议幂等回调用（非 Empty 一律 no-op，不 throw）。
    /// <para>★ 单向不可逆（设计决策）：Empty→Ready/Broken/Invalid 之后不回头（删段后引用对象已换）。</para>
    /// </summary>
    /// <returns>true = 本调用完成迁移并开闩；false = 已非 Empty（他方已迁移）。</returns>
    internal bool TryMarkReady()
    {
        if (Interlocked.CompareExchange(ref _stateCode, (int)StableState.Ready, (int)StableState.Empty)
            != (int)StableState.Empty) return false;
        _physicalReady.Set();
        return true;
    }

    /// <summary>
    /// 标记段物理就绪：StableState 从 Empty 流转到 Ready。
    /// </summary>
    public void MarkReady()
    {
        // ★ 3.2：运行时校验替代 Debug.Assert（Release 构建中不消失）
        if (!TryMarkReady())
            throw new InvalidOperationException($"MarkReady 只能从 Empty 流转，当前 State={StableState}");
    }

    /// <summary>
    /// 找 start 处的区间 index——不加锁，调用方须持 <see cref="Segment.ExtentLock"/>。
    /// <para>★ 同 Start 多条时优先返回在途态（Leased）——lease Commit/Rollback 转终态的目标就是 lease 占住时插的那条。</para>
    /// <para>★ 找不到在途态时退回任意一条 Start==start 的（兼容 AllocateRaw 直接 MarkWasted 还没 Leased 的情况）。</para>
    /// <para>★ 修复同 Start 歧义 bug：之前 FindContainingIndex 可能找错目标（改了 Committed 而非 Leased）。</para>
    /// </summary>
    /// <param name="start">区间起始偏移。</param>
    /// <returns>匹配的 index，找不到返回 -1。</returns>
    private int FindLeasedExtentIndex(long start)
    {
        var idx = _extentList.FindContainingIndex(start);
        if (idx < 0 || _extentList[idx].Start != start) return -1;
        // 同 Start 优先找在途态（Leased）——它才是 lease Commit/Rollback 要转终态的目标
        if (ExtentStateCode.IsInFlight(_extentList[idx].State)) return idx;
        for (var i = idx + 1; i < _extentList.Count && _extentList[i].Start == start; i++)
            if (ExtentStateCode.IsInFlight(_extentList[i].State)) return i;
        for (var i = idx - 1; i >= 0 && _extentList[i].Start == start; i--)
            if (ExtentStateCode.IsInFlight(_extentList[i].State)) return i;
        // 退回：无在途态，返回任意一条同 Start 的（AllocateRaw 路径：直接 MarkWasted 无 Leased）
        return idx;
    }

    /// <summary>
    /// 标记段失效：StableState 流转到 Invalid（Compact/Recovery 删段用）。
    /// </summary>
    public void MarkInvalid()
    {
        // ★ 幂等：Invalid 是终态，重复设值无变化（不 throw——容忍回收路径防御性二次调用，如 ShrinkHead/Compact）。
        //   Exchange 无条件落 Invalid + 单向闩唤醒物理门等待者（§6.1 零锁协调——它们看到 Invalid 终态而非永久挂起）。
        var prev = Interlocked.Exchange(ref _stateCode, (int)StableState.Invalid);
        if (prev != (int)StableState.Invalid)
            _physicalReady.Set();
    }

    /// <summary>
    /// 标记段进入 Compact 整理态：StableState → Compacting。
    /// </summary>
    internal void MarkCompacting()
    {
        // ★ 3.2：运行时校验替代 Debug.Assert（Compact 排他由 SegmentLock 保证迁移互斥）
        var cur = StableState;
        if (cur is not (StableState.Ready or StableState.Full))
            throw new InvalidOperationException($"MarkCompacting 只能从 Ready/Full 流转，当前={cur}");
        Volatile.Write(ref _stateCode, (int)StableState.Compacting);
    }

    /// <summary>
    /// CAS 迁移 Empty→Broken + 单向闩开（等待者看到 Broken 终态）——协议幂等回调用。
    /// <para>★ 与 <see cref="TryMarkReady"/> 对称：池预建与正式建段的失败回调在高并发下可先后命中
    ///   同一段——重复失败回调不得抛（异常路径里再抛 = worker 毒化），迟到失败回调遇已迁移段
    ///   （Ready/Invalid/Broken）一律 no-op，不打断健康段（S1 固化 ）。</para>
    /// </summary>
    /// <returns>true = 本调用完成迁移并开闩；false = 已非 Empty（他方已迁移）。</returns>
    internal bool TryMarkBroken()
    {
        if (Interlocked.CompareExchange(ref _stateCode, (int)StableState.Broken, (int)StableState.Empty)
            != (int)StableState.Empty) return false;
        _physicalReady.Set();
        return true;
    }

    /// <summary>定时等待物理门单向闩（WaitSegmentReady 用——先查状态再等闩，醒后复查）。</summary>
    internal bool WaitPhysicalReady(int timeoutMs) => _physicalReady.Wait(timeoutMs);

    /// <summary>开闩唤醒物理门等待者（Dispose 唤醒——不改状态；等待者醒后查终态/段表 Dispose 抛出）。</summary>
    internal void SignalPhysicalReady() => _physicalReady.Set();
}