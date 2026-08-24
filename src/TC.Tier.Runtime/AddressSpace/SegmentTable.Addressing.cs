using System.Runtime.CompilerServices;

namespace TC.Tier.Runtime.AddressSpace;

/// <summary>
/// <see cref="SegmentTable"/> partial——地址算术 + 边界只读（文档 §3.3 §3.7）。
/// <para>★ 纯计算 + 无锁原子读，无状态变更。</para>
/// </summary>
public sealed partial class SegmentTable
{
    // ════════════════════════════════════════════════════════════
    // === 段表边界（只读，原子）===
    // ════════════════════════════════════════════════════════════

    /// <summary>段表头部边界（原子读，无撕裂——16B 单次读）。disposed 后返回 <see cref="LogicalAddress.Invalid"/>。</summary>
    public LogicalAddress MinAddress => _minAddrMem.IsDisposed
        ? LogicalAddress.Invalid
        : _minAddrMem.GetRefUnsafe<LogicalAddress>(0);

    /// <summary>段表最小有效段号（= MinAddress.SegId）。</summary>
    public int MinSegId => MinAddress.SegId;

    /// <summary>最大有效段号——_segments 末尾条目的 SegId（含 Invalid）。</summary>
    public int MaxSegId
    {
        get
        {
            var count = Volatile.Read(ref _segCount);
            return count > 0 ? Volatile.Read(ref _segments)[count - 1].SegId : MinAddress.SegId - 1;
        }
    }

    /// <summary>段表条目数（含 Invalid 占位）。</summary>
    public int SegCount => Volatile.Read(ref _segCount);

    /// <summary>分配尾（AllocatedTail）——新写入的起点。无锁原子读。</summary>
    public LogicalAddress AllocatedTail => _tailSlot.Allocated;

    /// <summary>提交尾（CommittedTail）——已提交数据边界。无锁原子读。</summary>
    public LogicalAddress CommittedTail => _tailSlot.Committed;

    // ════════════════════════════════════════════════════════════
    // === 地址算术（纯计算，无状态，跨段进位/借位）===
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 从 start 前进 length 字节得到新地址（跨段进位）。
    /// <para>★ 只前进：length &lt; 0 抛异常（回退调 <see cref="RetreatAddress"/>）；== 0 返回 start。</para>
    /// <para>★ 返回的 Extension 恒为 0——调用方须显式保留 start.Extension。</para>
    /// <para>★ 区间表示统一（2026-08-21 用户裁定）：半开 [start, end)——end 是"已前进字节之后第一个位置"，
    ///   恰好填满一段时<b>停驻 (segId, segLimit)</b>（段末边界规范形），不产出 (segId+1, 0) 哨兵形态。
    ///   (N, 0) 只保留"段首字节/原点"身份；段末只有一种写法——与 RetreatAddress 互为精确镜像，
    ///   与扫盘恢复尾 (seg, GrowthLimit) 同形。</para>
    /// </summary>
    public LogicalAddress AdvanceAddress(LogicalAddress start, long length)
    {
        switch (length)
        {
            case < 0:
                throw new ArgumentOutOfRangeException(nameof(length), length, "AdvanceAddress 只前进（length >= 0）；回退请调 RetreatAddress。");
            case 0:
                return start;
        }

        // ★ 不读 MaxSegId——跨段进位用 SegmentGrowthLimit(segId) 查段（Compact 后段大小可能不同，
        //   不能用全局 GrowthLimit）。但不读 MaxSegId 属性（Volatile.Read _segCount + 数组），
        //   段不存在时 SegmentGrowthLimit 返回全局值（Hollow 段），算术仍正确。
        var remaining = length;
        var segId = start.SegId;
        var segOff = start.Offset;

        while (true)
        {
            var segLimit = SegmentGrowthLimit(segId);
            var available = segLimit - segOff;
            if (remaining <= available)
            {
                var newOff = segOff + remaining;
                // ★ 区间统一：恰好填满停驻 (segId, segLimit)——段末边界规范形（不再归一成 (segId+1, 0)）。
                return new LogicalAddress(segId, 0, newOff);
            }
            remaining -= available;
            segId++;
            segOff = 0;
        }
    }

    /// <summary>
    /// 从 start 回退 length 字节得到新地址（跨段借位）。
    /// <para>★ 只回退：length &lt; 0 抛异常；== 0 返回 start。</para>
    /// <para>★ 回退到段表头部之前（低于 MinAddress）返回 <see cref="LogicalAddress.Invalid"/>。</para>
    /// </summary>
    public LogicalAddress RetreatAddress(LogicalAddress start, long length)
    {
        switch (length)
        {
            case < 0:
                throw new ArgumentOutOfRangeException(nameof(length), length, "RetreatAddress 只回退（length >= 0）；前进请调 AdvanceAddress。");
            case 0:
                return start;
        }

        var remaining = length;
        var segId = start.SegId;
        var segOff = start.Offset;
        var minSegId = MinSegId;

        while (segId >= minSegId)
        {
            var available = segOff;
            if (available <= 0)
            {
                if (segId <= minSegId) return LogicalAddress.Invalid;
                segId--;
                segOff = SegmentGrowthLimit(segId);   // ★ Hollow 段用 _growthLimit（地址空间连续，被回收段仍占位）
                continue;
            }

            var consume = remaining <= available ? remaining : available;
            remaining -= consume;
            segOff -= consume;

            if (remaining <= 0)
                return new LogicalAddress(segId, 0, segOff);

            if (segId <= minSegId) return LogicalAddress.Invalid;
            segId--;
            segOff = SegmentGrowthLimit(segId);
        }

        return LogicalAddress.Invalid;
    }

    /// <summary>
    /// 计算两个地址之间的字节距离（from → to，跨段累加）。若 from &gt; to 返回负值。
    /// <para>★ 不做 AllocatedTail 上界校验——调用方保证 from/to 合法。</para>
    /// </summary>
    public long GetDistance(LogicalAddress from, LogicalAddress to)
    {
        var cmp = from.CompareTo(to);
        if (cmp == 0) return 0;

        var negate = cmp > 0;
        var start = negate ? to : from;
        var end = negate ? from : to;

        var total = 0L;
        var segId = start.SegId;
        var segOff = start.Offset;

        while (segId < end.SegId)
        {
            total += SegmentGrowthLimit(segId) - segOff;   // ★ Hollow 段用 _growthLimit
            segId++;
            segOff = 0;
        }

        total += end.Offset - segOff;
        return negate ? -total : total;
    }

    /// <summary>
    /// 取段生长上限——段存在用段的，段不存在（Hollow，已回收/未建）用生命周期上限 _growthLimit。
    /// <para>★ 地址空间连续：被回收的段在逻辑地址上仍占 _growthLimit 字节，只是物理没了。</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    // ReSharper disable once MemberCanBePrivate.Global
    public long SegmentGrowthLimit(int segId)
    {
        // ★ 快路径：segId > 本次生命周期创建的最大段号 → 全局 GrowthLimit（零查段表）。
        //   新建的段 GrowthLimit 恒等于全局值；历史/Compact 段（≤ 阈值）大小可能不同，走查段。
        if (segId > _runtimeCreatedSegIdThreshold)
            return GrowthLimit;
        if (TryGetSegmentRaw(segId, out var seg) && seg is not null)
            return seg.GrowthLimit > 0 ? seg.GrowthLimit : GrowthLimit;
        return GrowthLimit;
    }
}
