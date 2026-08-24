namespace TC.Tier.Runtime.AddressSpace;

/// <summary>
/// 段表启动参数——启动期双尾水位的初始设定（<see cref="SegmentTable.SetStartupTails"/> 的输入）。
/// <para>★ 只携带水位：生命周期参数（GrowthLimit/分段开关）构造期经 <see cref="SegmentTableSettings"/> 传入
///   ——**构造 = 配置，启动 = 双尾**。</para>
/// <para>★ 两个值：同址 = 截断/重置形态；committed &lt; allocated = 扫盘恢复形态（存在已分配未提交尾部）。
///   单值构造即双尾同址。</para>
/// <para>★ 无持久化启动的显式通道：构造段表 → <see cref="SegmentTable.SetStartupTails"/> 定双尾 → 直接 Allocate 运行，
///   <see cref="SegmentTable.LoadAddressTable"/> 全程可选。</para>
/// </summary>
public readonly struct StartupParameters
{
    /// <summary>提交尾初始值。</summary>
    public LogicalAddress CommittedTail { get; }

    /// <summary>分配尾初始值。</summary>
    public LogicalAddress AllocatedTail { get; }

    /// <summary>单值构造——双尾同址（截断/重置形态）。</summary>
    public StartupParameters(LogicalAddress tail) => (CommittedTail, AllocatedTail) = (tail, tail);

    /// <summary>双值构造——committed ≤ allocated（扫盘恢复形态：存在已分配未提交尾部）。</summary>
    public StartupParameters(LogicalAddress committedTail, LogicalAddress allocatedTail)
        => (CommittedTail, AllocatedTail) = (committedTail, allocatedTail);
}
