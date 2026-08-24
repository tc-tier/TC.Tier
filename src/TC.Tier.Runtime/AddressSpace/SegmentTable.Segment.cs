using System.Runtime.CompilerServices;

namespace TC.Tier.Runtime.AddressSpace;

/// <summary>
/// <see cref="SegmentTable"/> partial——段操作（查询/建段/收缩/紧凑/替换/段事件）。
/// <para>★ 段数组 COW 管理：读无锁，结构变更持 <see cref="SegmentTable._mutationLock"/>。</para>
/// <para>★ 建段自洽：<see cref="AppendSegmentRaw"/> 根据 _handler 决定 Empty（待物理建）/ Written（立即可用）。</para>
/// </summary>
public sealed partial class SegmentTable
{
    // ════════════════════════════════════════════════════════════
    // === 段查询（只读，无锁）===
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// O(1) 下标查段——返回只读视图。段不存在返回 <see cref="Segment.Hollow"/> 哨兵。
    /// </summary>
    /// <param name="segId">段 ID。</param>
    /// <returns>返回对应的段对象或 <see cref="Segment.Hollow"/>。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SegmentView GetSegment(int segId)
    {
        // ★ 读者侧 acquire 对称（对齐发布侧 Volatile.Write）：字段 + 槽位都要 acquire——
        //   ARM 弱序下 plain load 可见扩容/发布/摘索引的中间态（x64 TSO 恰好安全，不依赖）
        var index = Volatile.Read(ref _segIndex);
        var idx = (uint)segId < (uint)index.Length ? Volatile.Read(ref index[segId]) : -1; // ★ (uint) 同时守卫负数和上越界
        return idx >= 0 ? Volatile.Read(ref _segments)[idx] : Segment.Hollow;
    }
    /// <summary>
    /// O(1) 下标查段——返回只读视图。段不存在返回 null。
    /// </summary>
    /// <param name="segId">段 ID。</param>
    /// <param name="seg">输出参数，返回段对象或 null。</param>
    /// <returns>如果段存在返回 true，否则返回 false。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetSegment(int segId, out SegmentView? seg)
    {
        if (TryGetSegmentRaw(segId, out var segment) && segment is not null)
        {
            seg = segment;
            return true;
        }

        seg = null;
        return false;
    }
    /// <summary>
    /// 获取 [start, end) 区间跨段的连续范围列表（只读视图）。
    /// </summary>
    /// <param name="start">起始逻辑地址。</param>
    /// <param name="end">结束逻辑地址（不包含）。</param>
    /// <returns>返回跨段的连续范围列表，每个范围包含段 ID、段内起始偏移、段内结束偏移、真实大小和生长上限。</returns>
    public IReadOnlyList<(int SegId, long SegOff,long SegEnd, long GrowthLimit)> GetExtentRanges(
        LogicalAddress start, LogicalAddress end)
    {
        if (start >= end) return Array.Empty<(int, long, long, long)>();
        var result = new List<(int, long, long, long)>();
        var segId = start.SegId;
        var segOff = start.Offset;
        var maxSegId = MinSegId + SegCount - 1;
        while (segId <= maxSegId && (segId < end.SegId || (segId == end.SegId && segOff < end.Offset)))
        {
            if (!TryGetSegmentRaw(segId, out var seg) || seg is null || !seg.IsValid) { segId++; segOff = 0; continue; }
            var segEnd = segId == end.SegId ? end.Offset : seg.GrowthLimit;
            if (segEnd > segOff)
                result.Add((segId, segOff, segEnd, seg.GrowthLimit));
            segId++;
            segOff = 0;
        }
        return result;
    }



   /// <summary>
   /// segId → _segments 下标裸映射。段不存在返回 -1。
   /// </summary>
   /// <param name="segId">段 ID。</param>
   /// <returns>返回对应的数组下标，如果段不存在返回 -1。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int SegToIndex(int segId)
    {
        var index = Volatile.Read(ref _segIndex);   // ★ 读者侧 acquire 对称
        return (uint)segId < (uint)index.Length ? Volatile.Read(ref index[segId]) : -1;
    }



    /// <summary>
    /// 检查指定段的 [start, end) 区间是否完全可读（已提交/已写入）。
    /// </summary>
    /// <param name="segId">段 ID。</param>
    /// <param name="start">起始偏移。</param>
    /// <param name="end">结束偏移（不包含）。</param>
    /// <returns>如果区间完全可读返回 true，否则返回 false。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsRangeFullyReadable(int segId, long start, long end)
    {
        if (TryGetSegmentRaw(segId, out var seg) && seg is not null)
            return seg.IsRangeFullyReadable(start, end);
        return false;
    }

    /// <summary>
    /// 检查 [start, end) 是否含终态不可读区间（Aborted/Wasted）——读侧活性守卫用
    /// （AcquireReadPlan 自旋中探测：终态不可读永不变可读，须快速失败防挂死）。
    /// </summary>
    public bool ContainsPermanentlyUnreadable(int segId, long start, long end)
    {
        if (TryGetSegmentRaw(segId, out var seg) && seg is not null)
            return seg.ContainsPermanentlyUnreadable(start, end);
        return false;
    }

    /// <summary>
    /// Dispose 整体停止——开全部段的物理门单向闩，唤醒等待中的 lease/reader 让它们退出
    /// （等待者醒后查段表 Dispose 状态抛 <see cref="ObjectDisposedException"/>，段状态不被本操作改动）。
    /// </summary>
    public void PulseAllSegmentsReady()
    {
        var count = Volatile.Read(ref _segCount);
        var segs = Volatile.Read(ref _segments);
        for (var i = 0; i < count; i++)
            segs[i].SignalPhysicalReady();
    }


    /// <summary>
    /// 获取指定段的读写锁（SpinRWLock），用于读计划共享/水位回退排他。
    /// </summary>
    /// <param name="segId">段 ID。</param>
    /// <param name="lockWord">输出参数，返回指定段的读写锁。</param>
    /// <returns>如果成功获取锁返回 true，否则返回 false。</returns>
    public bool TryGetLock(int segId,out SpinRWLock? lockWord)
    {
        if (!TryGetSegmentRaw(segId, out var seg) || seg is null)
        {
            lockWord = null;
            return false;
        }
        lockWord = seg.SegmentLock;
        return true;
    }
    /// <summary>
    /// 在段表的变更锁上下文中执行工作委托，确保在执行期间段表不会被其他线程修改。
    /// </summary>
    /// <param name="body">要执行的工作委托。</param>
    public void ExecuteUnderLock(Action body)
    {
        lock (_mutationLock) body();
    }

    /// <summary>
    /// 等待段物理就绪（物理门）——**零锁**：volatile 查状态 + 单向闩分片等待（§6.1）。
    /// <para>★ 协调协议：先查状态（volatile）→ 等闩（Set 只发生在 Empty→Ready/Broken/Invalid 单向迁移）→
    ///   醒后单一出口判定——双检零竞态，无锁序、无释放窗口（X-1 死锁家族结构性消灭）。</para>
    /// <para>★ 非 Empty 态闩必已开（三出口都 Set）——进入慢路径的终态段零阻塞直达出口判定，
    ///   无需前置 Broken/Invalid 检查（单一检查点）。</para>
    /// <para>★ 有界放弃协议保留：1s 分片 / 5s 周期告警 / 60s 超时抛出（worker 病理性停摆的安全闹）。</para>
    /// </summary>
    public void WaitSegmentReady(int segId, ILogger? logger = null)
    {
        // ★ 单段模式：非 MinSegId 的段不存在也不应存在——直接返回（lease 已校验数据全在 seg0）。
        if (EnableSingleSegment && segId != MinSegId) throw new SegmentCreationException($"单段模式下段 {segId} 不存在（仅 seg{MinSegId} 可用）", segId);
        // ★ 段表已 Dispose——不再等待，直接抛
        ObjectDisposedException.ThrowIf(_disposed, $"段表已被回收（Dispose）");
        if (!TryGetSegmentRaw(segId, out var seg) || seg is null) return;
        // 快路径：物理就绪（volatile 读）——Ready/Full/Compacting
        if (seg.IsPhysicalReady) return;

        // 慢路径：单向闩分片等待——预算/告警走 Settings 统一参数（与 AcquireExtent 自旋同构：
        // deadline + attempts % WarnEvery），段表内不造策略常量
        var deadline = Environment.TickCount64 + _spinMilliseconds;
        var attempts = 0;
        while (!seg.WaitPhysicalReady(ReadyWaitParkSliceMs))
        {
            if (++attempts % _warnEvery == 0)
                logger?.LogWarning("段 {SegId} 等待物理就绪已 {Attempts} 次让步（建段协调异常缓慢）", segId, attempts);
            if (Environment.TickCount64 > deadline)
                throw new SegmentCreationException(
                    $"段 {segId} 等待物理就绪超时（{_spinMilliseconds}ms，worker 病理性停摆？）", segId);
            // ★ 段表停止时退出等待（Dispose 全段开闩后此处自醒）
            ObjectDisposedException.ThrowIf(_disposed, $"段 {segId} 已被回收（Dispose）");
        }

        // 单一终态出口（闩开 = Empty→Ready/Broken/Invalid 单向迁移已发生）
        switch (seg.StableState)
        {
            case StableState.Broken:
                throw new SegmentCreationException($"段 {segId} 物理创建失败", segId);
            case StableState.Invalid:
                throw new SegmentCreationException($"段 {segId} 在等待期间被回收（Invalid）", segId);
            default:
                return;   // Ready/Full/Compacting——物理就绪
        }
    }

    /// <summary>
    /// 建段回调——由 <see cref="ISegmentHandler"/> 在建段完成后调用，**零锁**完成状态迁移并开闩（§6.1）。
    /// <para>★ 迁移原子性：成功/失败分支均 CAS（<see cref="Segment.TryMarkReady"/>/TryMarkBroken——
    ///   Empty→Ready/Broken 单向，非 Empty no-op 幂等：重复/迟到失败回调不打断已迁移段）。
    ///   等待者经单向闩唤醒后复查状态。</para>
    /// </summary>
    /// <param name="segId">段 ID。</param>
    /// <param name="success">建段是否成功。</param>
    public void CreateSegmentCallback(int segId, bool success)
    {
        if (!TryGetSegmentRaw(segId, out var seg) || seg is null) return;
        if (success)
            seg.TryMarkReady();
        else
            seg.TryMarkBroken();
    }


    #region 私有Segment方法
    /// <summary>
    /// O(1) 下标查真实段对象——段表内部用。段不存在返回 null。
    /// </summary>
    /// <param name="segId">段 ID。</param>
    /// <param name="seg">输出参数，返回段对象或 null。</param>
    /// <returns>如果段存在返回 true，否则返回 false。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryGetSegmentRaw(int segId, out Segment? seg)
    {
        // ★ 读者侧 acquire 对称：字段（扩容发布）+ 槽位（注册/替换/摘索引发布）都要 acquire
        var index = Volatile.Read(ref _segIndex);
        var idx = (uint)segId < (uint)index.Length ? Volatile.Read(ref index[segId]) : -1;
        if (idx >= 0)
        {
            seg = Volatile.Read(ref _segments)[idx];
            return true;
        }
        seg = null;
        return false;
    }
    /// <summary>
    /// 按数组下标（非 segId）取段——SaveAddressTable 遍历用。越界返回 null。
    /// </summary>
    /// <param name="index">数组下标。</param>
    /// <returns>返回对应的段对象或 null。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Segment? GetSegmentByIndexRaw(int index)
    {
        return (uint)index >= (uint)Volatile.Read(ref _segCount) ? null : Volatile.Read(ref _segments)[index];
    }
    /// <summary>
    /// 追加段到段表（建段自洽：根据 _handler 决定 Empty/Written）。
    /// </summary>
    /// <param name="segId">段 ID。</param>
    /// <param name="isHighPriority">是否高优先级。</param>
    /// <exception cref="InvalidOperationException">当 GrowthLimit 小于等于 0 时抛出。</exception>
    private void AppendSegmentRaw(int segId, bool isHighPriority = false)
    {
        lock (_mutationLock)
        {
            if (segId < _segIndex.Length)
            {
                var idx = _segIndex[segId];
                if (idx >= 0)
                {
                    goto coordinate;
                }
            }

            if (GrowthLimit <= 0)
                throw new InvalidOperationException(
                    $"SegmentGrowthLimit must be > 0 to create segments, got {GrowthLimit}.");
            var stableState = _handler is not null ? StableState.Empty : StableState.Ready;
            // ★ 参数顺序对齐 Segment 构造：(segId, maxOffset, minOffset, growthLimit, stableState, ...)
            //   之前误传 (segId, 0, GrowthLimit, 0, ...) → minOffset=GrowthLimit、growthLimit=0 → Invalid 段 → EnsureSegmentsForLength 死循环
            var seg = new Segment(segId, maxOffset: 0, minOffset: 0, growthLimit: GrowthLimit, stableState: stableState, logger: _logger);
            AppendSegmentRawUnsafe(seg);
            // ★ 更新阈值——本次生命周期创建的最大段号，SegmentGrowthLimit 快路径用
            if (segId > _runtimeCreatedSegIdThreshold)
                _runtimeCreatedSegIdThreshold = segId;
        }
        coordinate:
        _handler?.OnSegmentCreate(segId, GrowthLimit, isHighPriority);
    }

    /// <summary>
    /// 追加已构造的段到数组末尾（恢复路径 LoadAddressTable 用——段从 reader 构造好直接插入）。
    /// <para>★ private + 非线程安全，调用方须持 <see cref="_mutationLock"/>。</para>
    /// </summary>
    private void AppendSegmentRawUnsafe(Segment seg)
    {
        if (seg.SegId >= _segIndex.Length)
        {
            // ★ VII-8 根因修复（2026-08-16）：新数组【先填 -1 再单点发布】——禁用 Array.Resize。
            //   Array.Resize 内部对字段做普通写：零初始化数组先可见、-1 Fill 后可见——无锁读者
            //   （CAS 门/Ensure 步进）在窗口内读到 _segIndex[x]=0 →「段存在（slot 0）」→ 跳过注册，
            //   尾水位穿过未注册段后 Ensure 只看 tail 当前段、永不回填 = 永久空洞（重开截断、数据不可达）。
            //   x64 TSO 下 store 按序可见也救不了：读者见到字段=新数组时其后的 Fill store 尚不可见。
            //   对齐下方 _segments 扩容的正确模式（build-then-publish）。
            var oldLen = _segIndex.Length;
            var newSize = oldLen == 0 ? 16 : Math.Max(oldLen * 2, seg.SegId + 1);
            var grownIndex = new int[newSize];
            Array.Copy(_segIndex, grownIndex, oldLen);
            Array.Fill(grownIndex, -1, oldLen, newSize - oldLen);
            Volatile.Write(ref _segIndex, grownIndex);
        }

        var count = Volatile.Read(ref _segCount);   // 与全部读者同构（写者虽互斥，保持对称防回归）
        var segments = _segments;
        if (segments.Length == 0 || count >= segments.Length)
        {
            var newSize = segments.Length == 0 ? 16 : Math.Max(segments.Length * 2, count + 1);
            var grown = new Segment[newSize];
            Array.Copy(segments, grown, count);
            segments = grown;
            Volatile.Write(ref _segments, segments);
        }

        segments[count] = seg;
        // ★ volatile 写元素——保证无锁读路径（SegToIndex/TryGetSegmentRaw）跨线程可见新映射
        Volatile.Write(ref _segIndex[seg.SegId], count);
        // ★ 对称发布（读者全走 Volatile.Read(ref _segCount)）——ARM 弱序下普通写不保证及时可见
        Volatile.Write(ref _segCount, count + 1);
    }

    #endregion
}