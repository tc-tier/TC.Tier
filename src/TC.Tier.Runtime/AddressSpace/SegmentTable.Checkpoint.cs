namespace TC.Tier.Runtime.AddressSpace;

/// <summary>
/// <see cref="SegmentTable"/> partial——持久化（LoadAddressTable/SaveAddressTable）。
/// <para>★ 三段式恢复：头部（ReadHeader）→ 段载荷（ReadSegment 循环 AppendUnsafe）→ 尾部（ReadFooter 直接 LoadTail）。</para>
/// <para>★ 无 InitializeTail/ValidateWatermark——footer 是水位权威，读出什么就 Load 什么。
///   水位的最终修正（2PC/快照）由 ApplyHints 独立完成。</para>
/// </summary>
public sealed partial class SegmentTable
{
    /// <summary>
    /// 从地址表读入恢复——三段式重建段表 + 双尾水位。
    /// <para>★ 头部：ReadHeader → 校验 growthLimit。</para>
    /// <para>★ 段载荷：ReadSegment 循环 → AppendUnsafe 逐段填入（段从 reader 构造好直接插入）。</para>
    /// <para>★ 尾部：ReadFooter → 直接 LoadTail（footer 是水位权威，不需要 InitializeTail 算水位）。</para>
    /// <para>★ 空设备 fallback：合成 seg0(Empty) + LoadTail(Empty)，让 handler 建物理段。</para>
    /// </summary>
    public void LoadAddressTable(IAddressTableReader reader)
    {
        // ★ 生命周期门禁：LoadAddressTable 仅恢复阶段可调，且一次性（重复加载会叠加段 + 重置水位，破坏状态）
        if ((LifecyclePhase)Volatile.Read(ref _phase) != LifecyclePhase.Recovery)
            throw new InvalidOperationException("LoadAddressTable 仅在恢复阶段（Allocate 之前）可调——段表已进入运行阶段。");
        // ★ 原子 test-and-set——消除"两并发调用都通过 _addressTableLoaded 检查再各自写 true"的窗口（4.3）。
        //   CAS 成功（0→1）才继续；已被标记则拒绝。即使后续抛异常，标记也已落下，禁止再调。
        if (Interlocked.CompareExchange(ref _addressTableLoaded, 1, 0) != 0)
            throw new InvalidOperationException("LoadAddressTable 已执行过——地址表恢复是一次性的，不允许重复加载。");

        if (!reader.ReadHeader(out var growthLimit))
            throw new InvalidOperationException("地址表头部读取失败");
        if (growthLimit <= 0)
            throw new InvalidOperationException(
                $"地址表头部 growthLimit={growthLimit} 非法——必须 > 0（本次生命周期段大小上限）");
        GrowthLimit = growthLimit; // 设置生命周期段的生长上限（后续建段/扩容都用此值）
        int? firstSegId = null;
        var lastSegId = -1;
        var lastMaxOffset = 0L;
        while (reader.ReadSegment(out var segId, out var entry))
        {
            firstSegId ??= segId;
            // ★ 扫盘现算真实水位（footer 缺失时的 fallback 依据）：末段 maxOffset
            if (segId >= lastSegId)
            {
                lastSegId = segId;
                lastMaxOffset = entry.MaxOffset;
            }

            // ★ 命名字段取用（SegmentScanEntry 已强校验）——彻底消灭元组位序错位（两次事故的教训）。
            var seg = new Segment(segId, maxOffset: entry.MaxOffset, minOffset: entry.MinOffset,
                growthLimit: entry.GrowthLimit, stableState: entry.StableState, compactThreshold: 256, logger: _logger);
            ExecuteUnderLock(() => AppendSegmentRawUnsafe(seg));
        }

        // ★ VII-3 extent 级保真：扫盘 reader 旁路携带的段区间摘要（meta extension）→ 精确重建洞布局。
        //   无摘要 / 解码无效 / 超容量的段保持粗粒度重建（与旧行为等价，降级不失败）。
        if (reader is IExtentSummaryProvider { ExtentSummaries: { } summaries })
        {
            foreach (var (summarySegId, payload) in summaries)
            {
                if (ExtentSummaryCodec.Decode(payload) is not { } records) continue;
                if (!TryGetSegmentRaw(summarySegId, out var summarySeg) || summarySeg is null) continue;
                summarySeg.InstallExtents(records);
            }
        }

        var hasSegments = firstSegId is not null;
        if (firstSegId is { } fs)
            SetMinAddress(new LogicalAddress(fs, 0));

        reader.ReadFooter(out var footerCommitted, out var footerAllocated);

        if (hasSegments)
        {
            // footer 是水位权威——直接 Load（不需要 InitializeTail 算、不需要 ValidateWatermark 校验）
            // ★ fallback 链（修正）：footer 缺失（扫盘 checkpoint 无 footer）时，
            //   用扫盘现算的真实水位（末段 maxOffset）——旧实现退 Empty 把 reopen 水位归零，
            //   导致 NoHint/MetaFile 族"tail=0、读回 0 字节"（RecoverySemantics 根因）。
            var real = new LogicalAddress(lastSegId, lastMaxOffset);
            LoadTail(footerAllocated ?? real, footerCommitted ?? real);
        }
        else
        {
            // 空设备：合成 seg0(Empty) + Load(Empty)，让 handler 建物理段
            var seg0 = new Segment(segId: 0, maxOffset: 0, minOffset: 0, growthLimit,
                StableState.Empty, compactThreshold: 256, logger: _logger);
            lock (_mutationLock)
            {
                AppendSegmentRawUnsafe(seg0);
            }

            SetMinAddress(new LogicalAddress(0, 0));
            // ★ fallback 链（6.3）：committed 兜底 Empty；allocated 再兜底退到 committed
            //   （footer 全缺时双尾均 Empty，seg0@0x0 为合法起点）
            var start = footerCommitted ?? LogicalAddress.Empty;
            LoadTail(footerAllocated ?? start, start);
            // 空设备合成 seg0(Empty) 后，立即协调建物理段
            _handler?.OnSegmentCreate(0, growthLimit, isHighPriority: true);
        }
    }

    /// <summary>
    /// 保存地址表到持久化——写入段表 + 两水位。
    /// <para>★ 跳过 Invalid 段（已回收的头部段，地址空间无意义，恢复时不需要）——否则 LoadAddressTable
    ///   会从第一个 Invalid 段推断 MinSegId，导致头部回收后保存恢复丢失回收事实。</para>
    /// <para>★ M3 修复（）：保存期间持<b>双尾水位独占</b>（TryHoldTailWatermark）——
    ///   并发 Append 的尾推进被挡（TryUpdateAllocated 前检失败自旋重试）、并发 ReclaimTail 回退被拒——
    ///   快照 = 段列表 + 双尾的一致性视图（原实现遍历段表与读 footer 之间水位可被推进 → 恢复时
    ///   footer 水位超前于段列表 = 已提交数据被当洞覆盖）。推进者的段在推进前已 EnsureSegmentsForLength
    ///   在表——hold 后遍历见完整几何。hold 失败（回退者持有时）有界自旋（回退是短操作——截断毫秒级）。</para>
    /// </summary>
    public void SaveAddressTable(IAddressTableWriter writer)
    {
        var spinner = new SpinWait();
        while (!_tailSlot.TryHoldTailWatermark())
            spinner.SpinOnce();   // 回退者（ReclaimTail）持有——等待其完成（短操作）
        try
        {
            var count = SegCount;
            // 先数有效段（排除 Invalid），header 的 segCount 只数有效段
            int validCount = 0;
            for (var i = 0; i < count; i++)
                if (GetSegmentByIndexRaw(i)!.StableState != StableState.Invalid)
                    validCount++;

            writer.WriteHeader(MinSegId, validCount, GrowthLimit);
            for (var i = 0; i < count; i++)
            {
                var segment = GetSegmentByIndexRaw(i)!;
                if (segment.StableState == StableState.Invalid) continue; // 跳过已回收段
                // ★ 第 2 参是 MinOffset——与 IAddressTableReader.ReadSegment 的 minOffset 对称，直接存/取，无需反推。
                var spec = new SegmentSpec(segment.MinOffset, segment.GrowthLimit, segment.MaxOffset, segment.StableState);
                writer.WriteSegment(segment.SegId, spec);
            }

            writer.WriteFooter(CommittedTail, AllocatedTail);
        }
        finally
        {
            _tailSlot.ReleaseTailWatermark();   // ★ M3：保存完必释放——推进者恢复
        }
    }

    /// <summary>装配期设双尾初值（LoadAddressTable 读 footer 后调，裸写）。</summary>
    private void LoadTail(LogicalAddress allocated, LogicalAddress committed)
    {
        _tailSlot.Load(allocated, committed);
    }
}