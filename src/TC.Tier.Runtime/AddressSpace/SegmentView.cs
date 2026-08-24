using System.Runtime.CompilerServices;

namespace TC.Tier.Runtime.AddressSpace;

/// <summary>
/// 段的只读值类型视图——段表对外的段信息载体，不含 segId（调用方已知）。
/// <para>★ 段表对外不暴露可变 <see cref="Segment"/> 引用——所有状态变更经段表包装方法。</para>
/// <para>★ 值类型零分配；Hollow 哨兵表示段不存在。</para>
/// <para>★ 4.4：快照语义——构造时拷贝 StableState/MaxOffset/MinOffset/GrowthLimit 四标量，
///   此后不再更新，不反映段的后续变更（如 Compact 替换 / ReclaimHead 回收）。
///   长生命周期持有者需重新调 GetSegment/TryGetSegment 取新快照。</para>
/// </summary>
public readonly struct SegmentView
{
    /// <summary>
    /// 段的稳定状态。调用方用 if (seg.StableState == StableState.Stable) 直接判断。
    /// </summary>
    public StableState StableState { get;  }
    /// <summary>
    /// 段的最大偏移（字节，≥0；Hollow = -1）。调用方用 if (seg.MaxOffset >= 0) 直接判断。
    /// </summary>
    public long MaxOffset { get; }
    /// <summary>
    /// 段的实际大小（字节，≥0；Hollow = -1）。调用方用 if (seg.RealSize >= 0) 直接判断。
    /// </summary>
    public long MinOffset { get; }
    /// <summary>
    /// 段的实际大小（字节，≥0；Hollow = -1）。调用方用 if (seg.RealSize >= 0) 直接判断。
    /// </summary>
    public long RealSize => MaxOffset - MinOffset;
    /// <summary>
    /// 可见前缀（已提交 extent 推到的最大 End——读门权威）。调用方用 if (seg.VisibleOffset >= 0) 直接判断。
    /// <para>★ 语义（读门楔死取证）：CommittedTail/MaxOffset 是<b>游标</b>（Allocate 即推），
    ///   可读性跟<b>物理提交的 extent</b> 走。Read 计划按本值裁剪尾块——请求超出可见前缀返回可见部分
    ///   （部分读），绝不自旋等待"游标与可见的差值区间"（未写占位永不可见 = 永真自旋楔死）。</para>
    /// </summary>
    public long VisibleOffset { get; }
    /// <summary>
    /// 段增长上限（字节，>0；默认 32MB，启动后不变）。调用方用 if (seg.GrowthLimit > 0) 直接判断。
    /// </summary>
    public long GrowthLimit { get;  }
    /// <summary>
    /// ★ 物理状态就绪（物理门开）——Ready/Full/Compacting（Compacting 物理存在，整理排他由区间锁管）。
    /// Empty（建段中）/Broken（门永关）/Invalid（准入吊销）非就绪。
    /// </summary>
    public bool IsPhysicalReady => StableState is StableState.Ready or StableState.Full or StableState.Compacting;
    /// <summary>
    /// 段是否已满（MaxOffset ≥ GrowthLimit）。调用方用 if (seg.IsFull) 直接判断。
    /// </summary>
    public bool IsFull => MaxOffset >= GrowthLimit;

    /// <summary>是否有效段（非 Hollow/默认值）。调用方用 if (seg.IsValid) 直接判断。</summary>
    public bool IsValid => MaxOffset >= 0 && MinOffset >= 0 && GrowthLimit > 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private SegmentView(Segment seg)
    {
        StableState = seg.StableState;
        MaxOffset = seg.MaxOffset;
        MinOffset = seg.MinOffset;
        GrowthLimit = seg.GrowthLimit;
        VisibleOffset = seg.VisibleOffset;
    }
    /// <summary>
    /// Segment → SegmentView 隐式转换（值类型零分配）。
    /// </summary>
    /// <param name="seg">要转换的段对象。</param>
    /// <returns>返回对应的 SegmentView。</returns>
    public static implicit operator SegmentView(Segment seg) => new(seg);
}
