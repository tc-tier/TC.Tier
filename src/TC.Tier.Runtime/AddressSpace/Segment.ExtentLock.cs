namespace TC.Tier.Runtime.AddressSpace;

public sealed partial class Segment
{
    /// <summary>
    /// ★ 区间 List 锁——Monitor（object）。
    /// <para>临界区短（Insert/CompleteAndMerge 微秒级），同段并发低——未竞争 Monitor 走 thin lock
    /// 快路径（~20ns），竞争者 futex park（内核精确唤醒）。</para>
    /// <para>★ 2026-08-20 挂起取证换轨（原 no-tracking SpinLock）：其复合锁字（持锁位|等待计数）
    ///   在 Sleep 型重竞争交错下可被孤立——WriteThrough 双写者×双读者真磁盘挂起 60s+ 的 dump
    ///   实锤 <c>_owner=0x80000003</c>（持锁+1 等待者）而全进程零持有线程，等待者永等。
    ///   Monitor 的 owner 跟踪健壮、无可腐蚀的复合状态、异常/超时路径语义封闭。</para>
    /// </summary>
    private readonly object _extentGate = new();

    /// <summary>
    /// 获取区间锁的 scope（using 块自动 Enter/Exit <see cref="Primitives.MonitorScope"/>，零分配）。
    /// </summary>
    /// <returns>返回一个 <see cref="Primitives.MonitorScope"/> 实例，用于在 using 块中自动管理锁的获取和释放。</returns>
    public MonitorScope AcquireExtentLock() => MonitorScope.Enter(_extentGate);
}
