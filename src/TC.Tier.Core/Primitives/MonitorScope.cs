using System.Runtime.CompilerServices;

namespace TC.Tier.Core.Primitives;

/// <summary>
/// <see cref="MonitorScope"/>——using 块自动获取/释放 <see cref="Monitor"/>（与 <see cref="SpinLockScope"/>
/// 同构的 RAII 面板，锁对象版）。
/// <para>★ 取代 no-tracking <see cref="SpinLock"/> 的场景（挂起取证定案）：SpinLock 的
///   复合锁字（持锁位 | 等待计数 | 跟踪禁用位）在 Sleep 型重竞争交错下可被孤立——实测 dump 中
///   <c>_owner=0x80000003</c>（持锁+1 登记等待者）而全进程零持有线程，等待者 Sleep(1) 循环永等
///   持锁位清零（ConcurrentReadWrite WriteThrough 挂起 60s+，fsync 0.1ms 洗清磁盘延迟）。
///   Monitor：owner 跟踪健壮 + 竞争者 futex park（内核精确唤醒）+ 无外部可腐蚀状态。</para>
/// <para>★ 契约：Enter/Exit 同线程配对（临界区内不跨 await/线程交接）；可重入（Monitor 语义）。
///   零分配（ref struct + 装箱过的锁对象由持有方常驻）。</para>
/// <para>★ C# 12 兼容：ref struct 不实现 <see cref="IDisposable"/>（C# 13），pattern-based using。</para>
/// </summary>
public ref struct MonitorScope
{
    private readonly object _gate;

    /// <summary>获取 Monitor 的 scope（using 块自动 Enter/Exit，零分配）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static MonitorScope Enter(object gate) => new(gate);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private MonitorScope(object gate)
    {
        _gate = gate;
        Monitor.Enter(gate);
    }

    /// <summary>释放锁（构造已 Enter——无条件 Exit）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Dispose() => Monitor.Exit(_gate);
}
