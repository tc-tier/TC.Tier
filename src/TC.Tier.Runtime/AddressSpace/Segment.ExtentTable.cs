using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using TC.Tier.Runtime.AddressSpace.Extensions;

namespace TC.Tier.Runtime.AddressSpace;

/// <summary>
/// Segment 的区间子状态机 partial——List&lt;ExtentRecord&gt; + 二分 + 段锁 + 四投影。
/// <para>★ 内联自原 ExtentTable 嵌套类（已合并）：区间数 ≤ 5 的场景下 List + 独立锁是最优结构。</para>
/// <para>★ 三把锁职责分离：_lockWord（段级排他）/ ReadyLock（建段协调）/ _extentLock（区间 List）。</para>
/// </summary>
public sealed partial class Segment
{
    /// <summary>区间 List——按 Start 升序，二分查找。受 _extentLock 保护。</summary>
    /// <para>★ 2.3：ExtentRecord 是 struct，List 索引器返回值拷贝——mutate 局部副本后必须写回
    ///   _extentList[idx] = rec，否则修改丢失。新增代码极易忘记写回。</para>
    private readonly List<ExtentRecord> _extentList = new(capacity: 8);

    /// <summary>
    /// ★ L19 收口（2026-08-22）：在途（in-flight）区间记录独立小表。
    /// <para>宽记录问题：整段 lease（Write/Reclaim/Compact 覆盖已提交区）的在途记录可<b>包含</b>
    /// 多条终态记录（插入序不保证相邻），破坏"终态记录互不相交、按 Start 二分"的扫描前提——
    /// 按 Start 二分落在中间终态记录上，宽在途记录漏扫（追加钻进整理中段，磁盘实锤 512B 丢写）。</para>
    /// <para>处置：终态表维持互不相交（Split/合并机制既有保证），二分协议<b>完整保留</b>；
    /// 在途记录由本表 O(m) 检查（m ≤ 并发 lease 数，常态 0-2——在途记录插入点只有 InsertUnsafe，
    /// 终态转变点 RemoveOutstanding 配对）。全部读写均在 _extentLock 内。</para>
    /// </summary>
    private readonly List<ExtentRecord> _outstanding = new(capacity: 4);

    /// <summary>从在途小表移除 (start, end) 记录——与 InsertUnsafe 的注册配对（幂等：不存在即 no-op）。</summary>
    private void RemoveOutstanding(long start, long end)
    {
        for (var i = _outstanding.Count - 1; i >= 0; i--)
        {
            var r = _outstanding[i];
            if (r.Start == start && r.End == end)
                _outstanding.RemoveAt(i);
        }
    }


    // ── 四投影（锁内 Volatile.Write，读者走属性 Volatile.Read）──
    private long _visibleOffset;
    private long _contiguousOffset;
    private long _minOutstandingStart = long.MaxValue;
    private long _minOutstandingEnd;

    /// <summary>minOutstanding 双字段 seqlock 版本（奇数=写入中，偶数=稳定）——修 2.4 双字段撕裂读。</summary>
    private int _minOutstandingVersion;

    /// <summary>最大可见偏移量（所有区间 End 的最大值）。lock-free 读。</summary>
    public long VisibleOffset => Volatile.Read(ref _visibleOffset);

    /// <summary>连续 Committed 终点。lock-free 读。</summary>
    public long ContiguousOffset => Volatile.Read(ref _contiguousOffset);

    /// <summary>最小在途区间起点（long.MaxValue 表示无在途）。lock-free 读。</summary>
    public long MinOutstandingStart => Volatile.Read(ref _minOutstandingStart);

    /// <summary>最小在途区间终点。lock-free 读。</summary>
    public long MinOutstandingEnd => Volatile.Read(ref _minOutstandingEnd);

    /// <summary>当前区间数量（lock-free 快速判定）。</summary>
    public int ExtentCount
    {
        get
        {
            using var lk = AcquireExtentLock();
            return _extentList.Count;
        }
    }

    // ═══════════════════════════════════════════
    //  区间谓词（需持 _extentLock）
    // ═══════════════════════════════════════════
    /// <summary>
    /// 检查 [start, end) 是否可占（默认请求方 = Write 语义——非 Reclaim 族，Aborted 拒绝）。
    /// </summary>
    /// <param name="start">起始偏移量。</param>
    /// <param name="end">结束偏移量（不含）。</param>
    /// <returns>可占返回 true；不可占返回 false。</returns>
    public bool CanAcquire(long start, long end)
    {
        using var lk = AcquireExtentLock();
        return CanAcquireUnsafe(start, end, ExtentStateCode.WriteLeased);
    }

    /// <summary>
    /// 检查 [start, end) 是否可被 <paramref name="requestState"/> 所示请求占住——不加锁，调用方须持 <see cref="AcquireExtentLock"/>。
    /// </summary>
    /// <param name="start">起始偏移量。</param>
    /// <param name="end">结束偏移量（不含）。</param>
    /// <param name="requestState">请求方的在途区间状态（Src 定 kind——Aborted 放行判定用）。</param>
    /// <returns>可占返回 true；不可占返回 false。</returns>
    [SuppressMessage("ReSharper", "InconsistentlySynchronizedField")]
    internal bool CanAcquireUnsafe(long start, long end, byte requestState)
    {
        // ★ L19 收口（2026-08-22）：宽在途记录（整段 lease 包含终态记录）走 _outstanding 小表 O(m)
        //   检查——主表保持"终态互不相交"二分前提，O(log n + k) 协议不变。
        //   在途态一律排他（在途无 Aborted，L1 放行规则不适用）。
        for (var i = 0; i < _outstanding.Count; i++)
        {
            var o = _outstanding[i];
            if (o.End <= start || o.Start >= end) continue;
            return false;
        }

        var idx = _extentList.FindContainingIndex(start);
        // ★ 回退到最早的同 Start extent
        while (idx > 0 && _extentList[idx - 1].Start == _extentList[idx].Start)
            idx--;
        idx = Math.Max(0, idx);
        // ★ L1 销案（2026-08-21）：Reclaim 族（中间/头/尾）可重占 Aborted——重试治愈毒化区。
        //   幂等论证：Aborted = punch/commit 非原子窗口的"数据完好或已归零"二态未知；Reclaim 契约
        //   = 销毁数据成洞（不读数据）——再 punch 两分支收敛同终态（完好→归零；已零→no-op），
        //   终态 Committed+sparse = Reclaim 成功规范终态。Write/Append 仍拒（占用矩阵 §7.2 不变——
        //   写入者不得无感填掉被追踪的永久洞）。Compact 走自身路径（IsRangeFullyDrained 允许 Aborted）。
        var reclaimMayTakeAborted = ExtentStateCode.SourceOf(requestState) == ExtentStateCode.SrcReclaim;
        for (var i = idx; i < _extentList.Count; i++)
        {
            var r = _extentList[i];
            if (r.End <= start) continue;
            if (r.Start >= end) break;
            // ★ 设计文档 §7.2 占用规则矩阵：Committed/Wasted 所有 kind 可占（覆写/填洞）。
            //   在途态（Leased）排他不可占。Aborted：Reclaim 族可占（L1 幂等重占），其余拒绝。
            if (ExtentStateCode.IsOccupiable(r.State)) continue;
            if (reclaimMayTakeAborted && ExtentStateCode.IsAborted(r.State)) continue;
            return false;
        }

        return true;
    }

    /// <summary>
    /// 检查 [start, end) 范围内是否无在途区间（Leased/Punching）——不加锁，调用方须持 <see cref="AcquireExtentLock"/>。
    /// </summary>
    /// <param name="start">起始偏移量。</param>
    /// <param name="end">结束偏移量（不含）。</param>
    /// <returns>无在途区间返回 true；存在在途区间返回 false。</returns>
    [SuppressMessage("ReSharper", "InconsistentlySynchronizedField")]
    internal bool IsRangeFullyDrainedUnsafe(long start, long end)
    {
        // ★ L19 收口（2026-08-22）：宽在途记录走 _outstanding 小表（主表二分协议不变）。
        for (var i = 0; i < _outstanding.Count; i++)
        {
            var o = _outstanding[i];
            if (o.End <= start || o.Start >= end) continue;
            return false;
        }

        var idx = _extentList.FindContainingIndex(start);
        while (idx > 0 && _extentList[idx - 1].Start == _extentList[idx].Start)
            idx--;
        idx = Math.Max(0, idx);
        for (var i = idx; i < _extentList.Count; i++)
        {
            var r = _extentList[i];
            if (r.End <= start) continue;
            if (r.Start >= end) break;
            if (r.State is ExtentStateCode.AppendLeased or ExtentStateCode.WriteLeased or ExtentStateCode.ReclaimLeased
                or ExtentStateCode.CompactLeased)
                return false;
        }

        return true;
    }

    /// <summary>
    /// 检查 [start, end) 范围内是否无在途区间（Leased/Punching）。
    /// <para>★ Compact Phase 0 用：等所有权全部归还（在途 IO 完成）。</para>
    /// <para>★ 与 <see cref="CanAcquire"/> 区别：允许 Aborted（永久洞），只拒绝 Leased/Punching（在途）。</para>
    /// </summary>
    public bool IsRangeFullyDrained(long start, long end)
    {
        using var lk = AcquireExtentLock();
        return IsRangeFullyDrainedUnsafe(start, end);
    }

    /// <summary>
    /// 计算从 offset 开始的可读区间终点（不含）和第一个非 Committed 区间状态。
    /// </summary>
    /// <param name="offset">起始偏移量。</param>
    /// <param name="bound">边界偏移量。</param>
    /// <returns>返回可读区间终点和第一个非 Committed 区间状态。</returns>
    internal (long ReadableEnd, byte FirstBlockingState) ClampReadable(long offset, long bound)
    {
        using var lk = AcquireExtentLock();

        if (_extentList.Count == 0)
            return (offset, ExtentStateCode.Committed);

        // ★ L19 收口（2026-08-22）：宽在途记录走 _outstanding 小表（主表二分协议不变）——
        //   命中即在途阻断（返回宽记录起点，读门保守拒绝）。
        for (var i = 0; i < _outstanding.Count; i++)
        {
            var o = _outstanding[i];
            if (o.End <= offset || o.Start >= bound) continue;
            if (o.Start <= offset)
                return (o.Start, o.State);
        }

        var idx = _extentList.FindContainingIndex(offset);
        // ★ 同 Start 有多个区间时回退到最早的那个
        while (idx > 0 && _extentList[idx - 1].Start == _extentList[idx].Start)
            idx--;
        var startIdx = idx >= 0 ? idx : 0;
        var readableEnd = offset;

        for (var i = startIdx; i < _extentList.Count && readableEnd < bound; i++)
        {
            var r = _extentList[i];
            if (r.Start > readableEnd) break;

            switch (r.State)
            {
                case ExtentStateCode.Committed:
                // ★ STORAGE-026 平衡设计：Aborted 区间 ClampReadable 读透（reader 拿 hitState 自判，
                //   支持跨洞读到后面 Committed——ClampReadable_Aborted_ReadsThrough 契约）。
                //   "全或无"守卫 IsRangeFullyReadable 单独负责拒绝含 Aborted 的范围（见 RebuildProjections
                //   把 Aborted/Wasted 计入 minOs 投影）——两者职责分离，兼顾安全与读透能力。
                case ExtentStateCode.Aborted:
                    readableEnd = Math.Min(r.End, bound);
                    break;
                default:
                    // 在途态（AppendLeased/WriteLeased/ReclaimLeased/CompactLeased）——阻断读
                    if (ExtentStateCode.IsInFlight(r.State))
                        return (r.Start, r.State);
                    break;
            }
        }

        return (readableEnd, ExtentStateCode.Committed);
    }

    // ═══════════════════════════════════════════
    //  区间修改（需持 _extentLock，方法内部自锁）
    // ═══════════════════════════════════════════

    /// <summary>
    /// 插入一个新的区间记录 [start, end)。
    /// </summary>
    /// <param name="start">区间起始偏移。</param>
    /// <param name="end">区间结束偏移。</param>
    /// <param name="state">区间状态。</param>
    /// <param name="sparse">是否为稀疏区间。</param>
    /// <param name="refresh">是否刷新投影。</param>
    /// <returns>返回插入的区间记录。</returns>
    public ExtentRecord Insert(long start, long end, byte state, bool sparse = false, bool refresh = true)
    {
        using var lk = AcquireExtentLock();
        return InsertUnsafe(start, end, state, sparse, refresh);
    }

    /// <summary>
    /// 插入一个新的区间记录 [start, end)——不加锁，调用方须持 <see cref="AcquireExtentLock"/>。
    /// <para>★ 占住时拆分与 [start,end) 重叠的已有可占区间（Committed/Wasted），保证每个 ExtentRecord
    ///   边界对齐。这是 lease 协议状态流转正确性的前提——后续 MarkWasted/MarkAbort/CompleteAndMerge
    ///   用 FindContainingIndex(start) 精确匹配时，不会因同 Start 歧义找错目标。</para>
    /// <para>★ 拆分模式（old 为被占区间）：</para>
    /// <list type="bullet">
    /// <item>old 完全在 [start,end) 内 → 不动</item>
    /// <item>old.Start &lt; start &lt; old.End → 拆出 [old.Start, start) 前驱</item>
    /// <item>old.Start &lt; end &lt; old.End → 拆出 [end, old.End) 后继</item>
    /// <item>old 同时横跨 start 和 end → 拆成 3 段</item>
    /// </list>
    /// </summary>
    /// <param name="start">区间起始偏移。</param>
    /// <param name="end">区间结束偏移。</param>
    /// <param name="state">区间状态。</param>
    /// <param name="sparse">是否为稀疏区间。</param>
    /// <param name="refresh">是否刷新投影。</param>
    /// <returns>返回插入的区间记录。</returns>
    [SuppressMessage("ReSharper", "InconsistentlySynchronizedField")]
    internal ExtentRecord InsertUnsafe(long start, long end, byte state, bool sparse = false,
        bool refresh = true)
    {
        var record = new ExtentRecord(start, end, state, sparse);
        // ★ Append 快路径：start >= VisibleOffset → 尾部追加。
        //   直接 Add（O(1) 摊还），跳过 FindInsertIndex（O(log N) 二分）+ Insert（O(N) 搬移）。
        //   batch 万次 Append 时，省掉 N 次搬移 = 主要延迟来源。
        if (start >= Volatile.Read(ref _visibleOffset))
        {
            _extentList.Add(record);
        }
        else
        {
            // Write/Reclaim 覆写——中间插入，需拆分重叠 + 二分定位
            SplitOverlappingExtents(start, end, state);
            var idx = _extentList.FindInsertIndex(start);
            _extentList.Insert(idx, record);
        }

        // ★ L19 收口（2026-08-22）：在途记录注册进 _outstanding 小表——占用/排水/可读扫描的
        //   宽记录检查走它（终态表保持互不相交二分前提）。
        if (ExtentStateCode.IsInFlight(state))
            _outstanding.Add(record);

        if (!refresh) return record;
        RefreshProjectionsIncremental(start, end, ExtentStateCode.IsCommitted(state));
        return record;
    }

    /// <summary>
    /// 拆分与 [start,end) 重叠的已有可占区间（Committed/Wasted；Reclaim 族请求另含 Aborted——L1）
    /// ——不加锁，调用方须持 <see cref="AcquireExtentLock"/>。
    /// <para>★ 拆分后每个 ExtentRecord 的边界与 [start,end) 对齐，避免同 Start 歧义。</para>
    /// <para>★ 只拆可占区间；在途态（Leased）由 CanAcquireUnsafe 排他保证不重叠。</para>
    /// </summary>
    /// <param name="start">占住区间起始。</param>
    /// <param name="end">占住区间结束（不含）。</param>
    /// <param name="requestState">请求方的在途区间状态（Reclaim 族对 Aborted 也拆分）。</param>
    private void SplitOverlappingExtents(long start, long end, byte requestState)
    {
        var reclaimMaySplitAborted =
            ExtentStateCode.SourceOf(requestState) == ExtentStateCode.SrcReclaim;
        // 从 start 之前的第一个可能重叠区间开始扫描（FindContainingIndex 找 Start ≤ start 的最大 idx）
        var startIdx = _extentList.FindContainingIndex(start);
        if (startIdx < 0) startIdx = 0;
        for (var i = startIdx; i < _extentList.Count; i++)
        {
            var old = _extentList[i];
            if (old.Start >= end) break; // 已超过 [start,end)，无重叠
            if (old.End <= start) continue; // 在 start 之前，无重叠
            // 只拆可占区间（Committed/Wasted；Reclaim 族另含 Aborted——L1）；在途态不应重叠（CanAcquireUnsafe 已保证）
            if (!ExtentStateCode.IsOccupiable(old.State)
                && !(reclaimMaySplitAborted && ExtentStateCode.IsAborted(old.State))) continue;

            // 拆分：old 与 [start,end) 有重叠
            var beforeLen = start - old.Start; // 前驱长度（>0 表示 old.Start < start）
            var afterStart = end; // 后继起点
            var afterLen = old.End - end; // 后继长度（>0 表示 old.End > end）

            switch (beforeLen)
            {
                case > 0 when afterLen > 0:
                {
                    // 横跨两端：拆成 [old.Start,start) + [start,end)占位 + [end,old.End)
                    // 先把当前条改成前驱 [old.Start,start)，再插后继 [end,old.End)
                    _extentList[i] = new ExtentRecord(old.Start, start, old.State, old.Sparse);
                    var afterIdx = _extentList.FindInsertIndex(end);
                    _extentList.Insert(afterIdx, new ExtentRecord(end, old.End, old.State, old.Sparse));
                    // 当前条已改小，i 不动继续（下一轮看后继是否还与别的重叠——不会，因为 i 现在 < afterIdx）
                    break;
                }
                case > 0:
                {
                    // 只横跨 start：[old.Start,start) + [start,old.End)（old.End ≤ end）
                    _extentList[i] = new ExtentRecord(old.Start, start, old.State, old.Sparse);
                    var restIdx = _extentList.FindInsertIndex(start);
                    _extentList.Insert(restIdx, new ExtentRecord(start, old.End, old.State, old.Sparse));
                    // 新插的 [start,old.End) 在 i+1，i 推进后正好指向它
                    i++;
                    break;
                }
                default:
                {
                    if (afterLen > 0)
                    {
                        // 只横跨 end：[old.Start,end) + [end,old.End)（old.Start ≥ start）
                        _extentList[i] = new ExtentRecord(old.Start, end, old.State, old.Sparse);
                        var afterIdx = _extentList.FindInsertIndex(end);
                        _extentList.Insert(afterIdx, new ExtentRecord(end, old.End, old.State, old.Sparse));
                    }

                    break;
                }
            }
            // else: old 完全在 [start,end) 内 → 不动
        }
    }


    /// <summary>
    /// 将 [start, end) 区间标记为 Committed——自适应双路径。
    /// <para>★ 小 list（≤ _compactThreshold）：快路径——直接合并相邻 + 清同 start 冗余 + 全量投影（小 list 最快）。</para>
    /// <para>★ 大 list（> _compactThreshold）：O(1) 标记路径——只改状态 + 增量投影，合并推迟到 CompactIntervals。</para>
    /// <para>★ 碎片率超阈值时通过 _compactCallback 入队异步压缩。</para>
    /// </summary>
    public void CompleteAndMerge(long start, long end, bool sparse)
    {
        using var lk = AcquireExtentLock();

        // ★ Append 快路径：start >= VisibleOffset → 尾部追加。
        //   改 Leased→Committed + 合并前驱（最后一条 Committed，O(1) 检查）。
        //   不合并前驱会导致 List 无限增长（每次 Append 一条不合并）→ 二分/插入随 N 退化。
        if (start >= Volatile.Read(ref _visibleOffset))
        {
            var idx = FindLeasedExtentIndex(start);
            if (idx < 0) return;
            var rec = _extentList[idx];
            rec.State = ExtentStateCode.Committed;
            rec.Sparse = sparse;
            RemoveOutstanding(rec.Start, rec.End);   // ★ L19 收口：在途小表配对注销
            // ★ 前驱合并：最后一条 Committed 的 End == start → 合并成一条（保持 List 紧凑）
            if (idx > 0 && _extentList[idx - 1].State == ExtentStateCode.Committed
                        && _extentList[idx - 1].End == start)
            {
                var prev = _extentList[idx - 1];
                _extentList[idx - 1] = new ExtentRecord(prev.Start, rec.End, ExtentStateCode.Committed,
                    prev.Sparse | sparse);
                _extentList.RemoveAt(idx); // 合并后删当前条——List 条目不增长
            }
            else
            {
                _extentList[idx] = rec;
            }

            RefreshProjectionsIncremental(start, end, becameCommitted: true);

            return;
        }

        // ★ 快路径：小 list 用原版合并逻辑（小 list 下全量扫描 + 合并比增量更块）
        if (_extentList.Count <= _compactThreshold)
        {
            CompleteAndMergeEager(start, end, sparse);
            return;
        }

        // ★ 大 list 路径：O(1) 标记，不合并，推迟到 CompactIntervals
        CompleteAndMergeLazy(start, end, sparse);
    }

    /// <summary>
    /// 安装替换段的完整区间表布局（RangeCompact 原子换表用）。
    /// <para>★ 调用方保证：本对象<b>尚未发布</b>（构造于 AtomicCompactReplace 的 mutationLock 内、
    ///   Volatile.Write 之前）——单线程构造期操作，无并发；发布即随 lease 提交原子生效。
    ///   布局为最终区间表（界外保留区含其原 State/Sparse + 打包 Committed 区），覆盖构造期的连续 seed。</para>
    /// </summary>
    /// <param name="extents">升序区间布局。</param>
    internal void InstallExtents(IReadOnlyList<ExtentRecord> extents)
    {
        using var lk = AcquireExtentLock();
        _extentList.Clear();
        _outstanding.Clear();   // ★ L19 收口：整表替换——在途小表同步清空
        foreach (var e in extents)
            _extentList.Add(e);
        RebuildProjections();
    }

    /// <summary>
    /// ★ Compact 原位更新（L12 修复 2026-08-21）——同段号换内脏而非新建对象换槽：
    /// 持 extent lock 内一次性完成 [换区间表 + 几何字段（maxOffset/growthLimit）+ 版本递增]，
    /// 对象身份/锁实例/物理门不变。
    /// <para>★ 引用恒稳收益：自旋写者（AcquireExtent）、读计划锁（SegmentLock）、句柄池持的都是
    ///   同一对象——与 Compact 更新天然互斥/自动可见，L12"死对象双占"根因消失。</para>
    /// <para>★ 版本哨兵闭环：<see cref="CompactVersion"/> 在锁内随内脏同变。钻入写者（在
    ///   ReleaseCompact 窗口抢到旧世界区间的）的 ExtentLease 携带旧版本快照——其 Commit/Rollback
    ///   被段表侧版本校验拦截（快速失败，上层重试），不再依赖清场等待。</para>
    /// <para>★ 调用方（<c>AtomicCompactReplace</c>）持 <c>_mutationLock</c>；本方法再持 extent lock
    ///   做内脏单发布——锁序 mutationLock → extent lock 与既有路径一致。</para>
    /// </summary>
    /// <param name="newGrowthLimit">重整后的生长上限。</param>
    /// <param name="newMaxOffset">重整后的最大偏移。</param>
    /// <param name="layout">最终区间表布局（升序）。</param>
    internal void ApplyCompactReplacement(long newGrowthLimit, long newMaxOffset, IReadOnlyList<ExtentRecord> layout)
    {
        using var lk = AcquireExtentLock();
        _extentList.Clear();
        _outstanding.Clear();   // ★ L19 收口：整表替换——在途小表同步清空（lease 自身记录随换表消失）
        foreach (var e in layout)
            _extentList.Add(e);
        GrowthLimit = newGrowthLimit;
        Volatile.Write(ref _maxOffset, newMaxOffset);
        Volatile.Write(ref _compactVersion, _compactVersion + 1);
        RebuildProjections();
    }

    /// <summary>
    /// ★ Compact 原位更新（带 minOffset——RangeCompact 打包前缀保留区形态）：
    /// 与 <see cref="ApplyCompactReplacement(long, long, IReadOnlyList{ExtentRecord})"/> 同协议，
    /// 额外推进 <c>_minOffset</c>（头部保留区起点，重整后由 spec 裁定）。
    /// </summary>
    internal void ApplyCompactReplacement(long newGrowthLimit, long newMaxOffset, long newMinOffset,
        IReadOnlyList<ExtentRecord> layout)
        => ApplyCompactReplacement(newGrowthLimit, newMaxOffset, newMinOffset, long.MaxValue, layout);

    /// <summary>
    /// ★ Compact 原位更新（L19 收口 2026-08-22，带 preserveFrom——数据窗外旧区间保留）：
    /// 持 extent lock 内一次性完成 [换区间表 + 几何字段（maxOffset/growthLimit）+ 版本递增]，
    /// 对象身份/锁实例/物理门不变。
    /// <para>★ preserveFrom &lt; long.MaxValue 时，旧区间表中 ≥ preserveFrom 的<b>终态</b>记录
    ///   （Committed/Wasted/Aborted，状态与 sparse 位照搬）拼接进新布局——窗口外已提交数据
    ///   不被洗成 sparse 读零（写者恰在 lease 获取前提交的竞态）。在途态（本 lease 的
    ///   CompactLeased 记录）不携带（Commit 后幂等清理）。</para>
    /// </summary>
    internal void ApplyCompactReplacement(long newGrowthLimit, long newMaxOffset, long newMinOffset,
        long preserveFrom, IReadOnlyList<ExtentRecord> layout)
    {
        using var lk = AcquireExtentLock();
        var final = new List<ExtentRecord>(layout.Count + 4);
        foreach (var e in layout)
            final.Add(e);
        if (preserveFrom < long.MaxValue)
        {
            foreach (var r in _extentList)
            {
                if (r.End <= preserveFrom) continue;
                if (ExtentStateCode.IsInFlight(r.State)) continue;
                var start = Math.Max(r.Start, preserveFrom);
                if (start >= r.End) continue;
                final.Add(new ExtentRecord(start, r.End, r.State, r.Sparse));
            }
        }
        _extentList.Clear();
        _outstanding.Clear();   // ★ L19 收口：整表替换——在途小表同步清空（lease 自身记录随换表消失）
        foreach (var e in final)
            _extentList.Add(e);
        GrowthLimit = newGrowthLimit;
        Volatile.Write(ref _maxOffset, newMaxOffset);
        Volatile.Write(ref _minOffset, newMinOffset);
        Volatile.Write(ref _compactVersion, _compactVersion + 1);
        RebuildProjections();
    }

    /// <summary>★ L12 版本哨兵：段表侧 {Kind}Commit/Rollback 入口校验——lease 携带的版本
    /// 与段当前版本不符 = 该 lease 的区间记录已随 Compact 重整消失（旧世界认知），
    /// 操作无效且继续执行会静默丢写——快速失败让上层重试。</summary>
    internal void ThrowIfStaleVersion(int segId, int compactVersion)
    {
        if (Volatile.Read(ref _compactVersion) != compactVersion)
            throw new InvalidOperationException(
                $"seg{SegId} 已被 Compact 原位重整（lease 版本 {compactVersion} ≠ 当前 {_compactVersion}）——" +
                "区间记录已随重整消失，lease 失效：请重试整笔操作");
        _ = segId;
    }

    /// <summary>
    /// 释放 [start, end) 的 Compact 租约（在途区间归还后调用）。
    /// </summary>
    /// <param name="start">区间起始偏移。</param>
    /// <param name="end">区间结束偏移。</param>
    /// <para>★ 2.2：幂等——找不到在途区间时静默返回（不抛异常），容忍并发释放/Compact retry。</para>
    public void ReleaseCompact(long start, long end)
    {
        using var lk = AcquireExtentLock();

        {
            for (var i = _extentList.Count - 1; i >= 0; i--)
            {
                var record = _extentList[i];
                if (record.Start != start || record.End != end || !ExtentStateCode.IsInFlight(record.State))
                    continue;

                _extentList.RemoveAt(i);
                RemoveOutstanding(start, end);   // ★ L19 收口：在途小表配对注销
                RebuildProjections();
                return;
            }
        }

        // ★ 2.2：幂等——找不到区间静默返回（可能已被并发释放或 Compact retry 已处理），避免抛异常中断流程
    }

    /// <summary>
    /// 快路径 CompleteAndMerge——小 list 用，直接合并相邻 + 清同 start 冗余 + 全量投影。
    /// </summary>
    private void CompleteAndMergeEager(long start, long end, bool sparse)
    {
        // 先清除同 start 的 Wasted/Aborted 记录（被新 Committed 覆盖）
        for (int i = _extentList.Count - 1; i >= 0; i--)
        {
            if (_extentList[i].Start == start
                && (_extentList[i].State is ExtentStateCode.Wasted or ExtentStateCode.Aborted))
            {
                _extentList.RemoveAt(i);
            }
        }

        var idx = _extentList.FindContainingIndex(start);
        if (idx < 0 || _extentList[idx].Start != start)
            return;
        // 同 Start 有多个区间时找非 Committed 的（Leased/Reclaiming）
        while (idx >= 0 && _extentList[idx].Start == start)
        {
            if (_extentList[idx].State != ExtentStateCode.Committed)
                break;
            idx--;
        }

        if (idx < 0 || _extentList[idx].Start != start || _extentList[idx].State == ExtentStateCode.Committed)
            return;

        var rec = _extentList[idx];
        rec.State = ExtentStateCode.Committed;
        rec.Sparse = sparse;
        _extentList[idx] = rec;
        RemoveOutstanding(rec.Start, rec.End);   // ★ L19 收口：在途小表配对注销

        // 合并前驱
        if (idx > 0 && _extentList[idx - 1].State == ExtentStateCode.Committed
                    && _extentList[idx - 1].End == start)
        {
            var prev = _extentList[idx - 1];
            _extentList[idx - 1] = new ExtentRecord(prev.Start, end, ExtentStateCode.Committed, prev.Sparse | sparse);
            _extentList.RemoveAt(idx);
            idx--;
            start = prev.Start;
            rec = _extentList[idx];
        }

        // 合并后继
        if (idx + 1 < _extentList.Count && _extentList[idx + 1].State == ExtentStateCode.Committed
                                        && _extentList[idx + 1].Start == end)
        {
            var next = _extentList[idx + 1];
            _extentList[idx] =
                new ExtentRecord(rec.Start, next.End, ExtentStateCode.Committed, rec.Sparse | next.Sparse);
            _extentList.RemoveAt(idx + 1);
        }

        // ★ 清除被最终合并区间完全覆盖的旧条目（Committed/Wasted/Aborted）。
        //   覆盖写场景（含非 sparse）：同 [start,end) 反复覆写时，占位 Split 不删完全重合的旧
        //   Committed（只拆边界不齐的），前驱/后继合并又只认严格相接（prev.End==start /
        //   next.Start==end）——同区间重合条永不归并，每次复写净 +1 条 → 表线性膨胀、
        //   单 op O(记录数)（2026-08-21 压测实锤：10 万次复写 42→299 µs/op）。
        //   此处清理 = 归并收口：旧记录语义已被新 lease 的 Committed 完全取代。
        //   在途态（Leased）不删——CanAcquireUnsafe 排他保证本区间无并发在途。
        long finalStart = _extentList[idx].Start;
        long finalEnd = _extentList[idx].End;
        for (int i = _extentList.Count - 1; i >= 0; i--)
        {
            if (i == idx) continue;
            var r = _extentList[i];
            if (r.Start >= finalStart && r.End <= finalEnd
                && (ExtentStateCode.IsOccupiable(r.State) || ExtentStateCode.IsAborted(r.State)))
                _extentList.RemoveAt(i);
        }

        RefreshProjection(start, end, becameCommitted: true);
    }

    /// <summary>
    /// 大 list CompleteAndMerge——O(1) 标记，不合并不清理，推迟到 CompactIntervals。
    /// </summary>
    private void CompleteAndMergeLazy(long start, long end, bool sparse)
    {
        var idx = _extentList.FindContainingIndex(start);
        if (idx < 0 || _extentList[idx].Start != start)
            return;
        // 同 Start 找在途（Leased/Reclaiming）——前后扫同 start 邻居
        var targetIdx = -1;
        if (_extentList[idx].State is ExtentStateCode.AppendLeased or ExtentStateCode.WriteLeased
            or ExtentStateCode.ReclaimLeased or ExtentStateCode.CompactLeased)
            targetIdx = idx;
        if (targetIdx < 0)
            for (var i = idx - 1; i >= 0 && _extentList[i].Start == start; i--)
                if (_extentList[i].State is ExtentStateCode.AppendLeased or ExtentStateCode.WriteLeased
                    or ExtentStateCode.ReclaimLeased or ExtentStateCode.CompactLeased)
                {
                    targetIdx = i;
                    break;
                }

        if (targetIdx < 0)
            for (var i = idx + 1; i < _extentList.Count && _extentList[i].Start == start; i++)
                if (_extentList[i].State is ExtentStateCode.AppendLeased or ExtentStateCode.WriteLeased
                    or ExtentStateCode.ReclaimLeased or ExtentStateCode.CompactLeased)
                {
                    targetIdx = i;
                    break;
                }

        if (targetIdx < 0) return;
        idx = targetIdx;

        var rec = _extentList[idx];
        rec.State = ExtentStateCode.Committed;
        rec.Sparse = sparse;
        _extentList[idx] = rec;
        RemoveOutstanding(rec.Start, rec.End);   // ★ L19 收口：在途小表配对注销

        // ★ sparse 模式：清除被新区间完全覆盖的旧条目（同 Eager 路径语义）
        if (sparse)
        {
            for (int i = _extentList.Count - 1; i >= 0; i--)
            {
                if (i == idx) continue;
                var r = _extentList[i];
                if (r.Start >= start && r.End <= end)
                    _extentList.RemoveAt(i);
            }
        }

        RefreshProjection(start, end, becameCommitted: true);
    }


    /// <summary>
    /// 压缩区间表——合并相邻同状态的区间，清理冗余条目。持锁，O(N)。
    /// </summary>
    public void CompactExtentTable()
    {
        using var lk = AcquireExtentLock();


        if (_extentList.Count <= 1) return;

        var compacted = new List<ExtentRecord>(_extentList.Count);
        // 按 Start 排序处理（list 应已有序，但合并需保证）
        foreach (var cur in _extentList)
        {
            if (compacted.Count == 0)
            {
                compacted.Add(cur);
                continue;
            }

            var prev = compacted[^1];
            // 合并相邻同状态：前驱.End==后继.Start 且状态相同
            if (prev.End == cur.Start && prev.State == cur.State &&
                prev.State is ExtentStateCode.Committed or ExtentStateCode.Wasted)
            {
                compacted[^1] = new ExtentRecord(prev.Start, cur.End, prev.State, prev.Sparse | cur.Sparse);
            }
            else
            {
                compacted.Add(cur);
            }
        }

        _extentList.Clear();
        _extentList.AddRange(compacted);
        RebuildProjections(); // 全量重建投影
    }

    /// <summary>
    /// 确保在段初始化时，如果区间表为空且 committedEnd 有效，则添加一个全量 Committed 区间并重建投影。
    /// </summary>
    /// <param name="committedEnd">已提交的结束偏移量（包含）。</param>
    private void EnsureCommittedSeed(long committedEnd)
    {
        using var lk = AcquireExtentLock();
        // ★ minOffset==0（段从头开始）且 committedEnd>0 时种 [0,committedEnd) Committed seed。
        //   之前 _minOffset <= 0 误判 minOffset=0（合法值）为"不种"，导致 Compact 新段区间表空。
        //   minOffset>0（头部已回收）时不种全段——[0,minOffset) 已不属于本段。
        if (_extentList.Count != 0 || _minOffset != 0 || committedEnd <= 0) return;
        _extentList.Add(new ExtentRecord(0, committedEnd, ExtentStateCode.Committed));
        RebuildProjections(); // 初始化——全量
    }


    // ═══════════════════════════════════════════
    //  Read 快路径（无锁，四投影 Volatile.Read）
    // ═══════════════════════════════════════════

    /// <summary>
    /// 检查 [start, end) 区间是否全部可读（Read 用）。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsRangeFullyReadable(long start, long end)
    {
        // VisibleOffset 检查
        if (end > Volatile.Read(ref _visibleOffset))
            return false;
        // ★ seqlock 读——消除 _minOutstandingStart/End 双字段撕裂读（2.4）。
        //   写侧（WriteOutstandingUnsafe，持 _extentLock 单写者）前后增版本号；
        //   读侧循环到版本号一致（偶数）才用值；写入中（奇数）或版本变动则保守返回 false 进慢路径 ClampReadable。
        long minOs, minOsEnd;
        int v1, v2;
        do
        {
            v1 = Volatile.Read(ref _minOutstandingVersion);
            if ((v1 & 1) != 0) return false; // 写入中——保守进慢路径 ClampReadable
            minOs = Volatile.Read(ref _minOutstandingStart);
            minOsEnd = Volatile.Read(ref _minOutstandingEnd);
            v2 = Volatile.Read(ref _minOutstandingVersion);
        } while (v1 != v2);

        // ★ 区间重叠判定：在途区间必须跟读范围重叠才拒绝
        if (minOs >= end) return true;
        return minOsEnd <= start;
    }

    /// <summary>
    /// 检查 [start, end) 是否含<b>终态不可读</b>区间（Aborted/Wasted——失败写/回收垃圾，
    /// 永不变可读）。读侧活性守卫用：含终态不可读时 <see cref="IsRangeFullyReadable"/> 恒 false，
    /// 等待它的自旋（AcquireReadPlan）必须快速失败而非无限自旋（挂死）。
    /// <para>★ L14 修复（2026-08-21）：end &gt; VisibleOffset 同判永不可读——ReclaimTail 的
    ///   RetreatOffset 删记录回退 VisibleOffset（不落 Aborted/Wasted 终态标记），跨截断点的读
    ///   plan 既不可读也扫不到终态记录 → 永久自旋。合法读的区间尾 ≤ CommittedTail ≤ VisibleOffset
    ///   （Committed 蕴含记录存在），end 越过 VisibleOffset ⟺ 该区已被截断抹除——快速失败
    ///   与"读已删段抛 PartitionInvalidException"同语义。</para>
    /// </summary>
    public bool ContainsPermanentlyUnreadable(long start, long end)
    {
        // 截断死区：区间尾越过可见投影（记录已被 RetreatOffset 删除）——永不可读
        if (end > Volatile.Read(ref _visibleOffset)) return true;
        using var lk = AcquireExtentLock();
        foreach (var r in _extentList)
        {
            if (r.End <= start) continue;
            if (r.Start >= end) break;
            if (r.State is ExtentStateCode.Aborted or ExtentStateCode.Wasted) return true;
        }
        return false;
    }

    /// <summary>
    /// 拍快照——拷贝当前 ExtentRecord 列表（Compact 搬迁用，仅冷路径）。
    /// <para>★ 热路径/诊断用 <see cref="EnumerateExtents"/>——零拷贝持锁遍历，不分配 List。</para>
    /// </summary>
    public IReadOnlyList<ExtentRecord> SnapshotExtents()
    {
        using var lk = AcquireExtentLock();
        var snapshot = new List<ExtentRecord>(_extentList.Count);
        snapshot.AddRange(_extentList);
        return snapshot;
    }

    /// <summary>
    /// ★ 持锁零拷贝遍历器——外部 using + while(MoveNext()) 一条一条读，不创建 List 副本。
    /// <para>★ 大量区间时不浪费内存（全量拷贝 = N × 32B List 分配）。</para>
    /// <para>★ 须快速处理——持 SpinLock 期间阻塞写者，不能做 IO/慢操作。</para>
    /// </summary>
    public ExtentReader EnumerateExtents() => new(this);

    /// <summary>★ ref struct 持锁遍历器——using 自动释放 SpinLock，零分配零拷贝。
    /// <para>★ C# 12 兼容：ref struct 不实现 IDisposable，靠 pattern-based using 调 <see cref="Dispose"/>。</para></summary>
    public ref struct ExtentReader
    {
        private readonly List<ExtentRecord> _list;
        private MonitorScope _lock;
        private int _index;

        /// <summary>
        /// 空遍历器（不持锁，Count=0，MoveNext 永假）。
        /// </summary>
        public static ExtentReader Empty => new(Hollow);

        internal ExtentReader(Segment seg)
        {
            _list = seg._extentList;
            _lock = seg.AcquireExtentLock(); // 持锁——遍历期间写者阻塞
            _index = -1;
        }

        /// <summary>当前区间（值类型元组，零拷贝——直接读 List 元素字段）。</summary>
        public readonly (long Start, long End, byte State, bool Sparse) Current
        {
            get
            {
                var r = _list[_index];
                return (r.Start, r.End, r.State, r.Sparse);
            }
        }

        /// <summary>区间总数。</summary>
        public readonly int Count => _list.Count;

        public bool MoveNext() => ++_index < _list.Count;

        public void Dispose() => _lock.Dispose(); // 释放 Monitor
    }
}

