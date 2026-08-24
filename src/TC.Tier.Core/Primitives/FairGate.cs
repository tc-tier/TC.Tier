namespace TC.Tier.Core.Primitives;

/// <summary>
/// 公平门——围绕「重试循环获取资源」的到达顺序协调器。资源本身不属于本门（占用/释放由调用方的
/// tryAcquire/业务逻辑负责），门只做两件事：<b>让后来者不插队</b>、<b>唤醒并让渡先手</b>。
/// <para>★ 协议（三方角色）：</para>
/// <para>  获取方 fast path：先查 <see cref="HasWaiters"/>——有等待者时<b>不走快路径</b>（否则零间隙
///   复占者永远插队，被唤醒者的调度延迟 &gt; 让渡窗口时持续失手——实测 3/8 残余超时根因）。</para>
/// <para>  获取方 slow path：<see cref="TryAcquireSlow"/>——登记等待者，持门锁内执行 tryAcquire，
///   成功返回 true；失败 park 等待唤醒（带超时防丢失唤醒），返回 false 由调用方重试。</para>
/// <para>  释放方：资源变为可获取后调 <see cref="Wake"/>——PulseAll + 让渡 5ms 先手（唤醒者随后的
///   复占是热自旋 µs 级，刚被唤醒的竞争者若无先手则每次 Pulse 后仍被抢回——实测 4/8 超时残余）。</para>
/// <para>★ 演进自 SegmentTable 手搓公平门（AcquireExtent 双写者长持锁窗口饥饿根治，
///   7a9685aa），行为保持式下沉——50ms park / 5ms 让渡为当时调优值，常量不做配置项。</para>
/// <para>★ 成本：无等待者时 fast path 仅一次 volatile 读（~1ns），Wake 零阻塞直返；有等待者走
///   Monitor（µs 级）——用于「临界区外就是 IO」的场景足够，不做队列自旋（无真实调用点）。</para>
/// <para>★ 约束：tryAcquire 在门锁内执行——其中不得再获取与本门逆序的锁（锁序由调用方保证）；
///   tryAcquire 必须纯内存、无异常、快速返回。</para>
/// </summary>
public sealed class FairGate
{
    /// <summary>park 超时（ms）——防丢失唤醒的兜底轮询间隔（7a9685aa 调优值）。</summary>
    private const int ParkTimeoutMs = 50;

    /// <summary>唤醒让渡（ms）——Wake 者放弃门 5ms 给被唤醒者先手（7a9685aa 调优值）。</summary>
    private const int HandoffYieldMs = 5;

    private readonly object _gate = new();
    private int _waiters;

    /// <summary>是否存在慢路径等待者——fast path 据此让位（新到者不插队）。</summary>
    public bool HasWaiters => Volatile.Read(ref _waiters) > 0;

    /// <summary>
    /// 慢路径尝试：登记等待者 → 持门锁内执行 <paramref name="tryAcquire"/> → 成功返回 true；
    /// 失败 park 等待 <see cref="Wake"/>（超时 <see cref="ParkTimeoutMs"/> 防丢失唤醒），返回 false 由调用方重试。
    /// </summary>
    /// <param name="tryAcquire">占用尝试（门锁内执行）——成功即已占用资源，必须纯内存、无异常。</param>
    /// <returns>true = 已占用（调用方停止重试）；false = 未占用（调用方回到协议循环）。</returns>
    public bool TryAcquireSlow(Func<bool> tryAcquire)
    {
        Interlocked.Increment(ref _waiters);
        try
        {
            lock (_gate)
            {
                if (tryAcquire()) return true;
                Monitor.Wait(_gate, ParkTimeoutMs);
                return false;
            }
        }
        finally
        {
            Interlocked.Decrement(ref _waiters);
        }
    }

    /// <summary>
    /// 唤醒全部等待者并让渡先手。无等待者时立即返回（零阻塞）。
    /// <para>★ 时机：资源已变为可获取之后再调（唤醒过早 = 被唤醒者双检必败，白烧一次调度）。</para>
    /// </summary>
    public void Wake()
    {
        if (Volatile.Read(ref _waiters) == 0) return;
        lock (_gate)
        {
            Monitor.PulseAll(_gate);
            // ★ 唤醒者让渡先手：唤醒者随后的复占是热自旋（µs 级），刚被唤醒的竞争者若无先手
            //   则每次 Pulse 后仍被抢回。Wait 释放门锁给被唤醒者 + 5ms 窗口——公平 handoff。
            Monitor.Wait(_gate, HandoffYieldMs);
        }
    }
}
