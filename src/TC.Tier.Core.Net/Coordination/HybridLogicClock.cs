namespace TC.Tier.Core.Net.Coordination;

/// <summary>
/// HLC 时间戳（二期-G1 NETGAP-024——混合逻辑时钟的值类型）：物理墙钟（ms）+ 逻辑计数。
/// <para>★ 全序可比（<see cref="CompareTo"/>——先物理后逻辑）；跨节点因果可追踪
/// （接收远端时间戳即吸收其物理上界）；墙钟回拨安全（逻辑计数承载）。</para>
/// </summary>
public readonly record struct HlcTimestamp(long Physical, int Logical) : IComparable<HlcTimestamp>
{
    /// <summary>全序比较（先物理后逻辑——HLC 事件序）。</summary>
    /// <param name="other">比较目标时间戳。</param>
    /// <returns>负数 = 本时间戳先于 <paramref name="other"/>；0 = 两者相等；正数 = 后于。</returns>
    public int CompareTo(HlcTimestamp other)
    {
        var p = Physical.CompareTo(other.Physical);
        return p != 0 ? p : Logical.CompareTo(other.Logical);
    }
    /// <summary>小于（全序——物理优先、逻辑次之）。</summary>
    public static bool operator <(HlcTimestamp left, HlcTimestamp right) => left.CompareTo(right) < 0;
    /// <summary>小于等于。</summary>
    public static bool operator <=(HlcTimestamp left, HlcTimestamp right) => left.CompareTo(right) <= 0;
    /// <summary>大于。</summary>
    public static bool operator >(HlcTimestamp left, HlcTimestamp right) => left.CompareTo(right) > 0;
    /// <summary>大于等于。</summary>
    public static bool operator >=(HlcTimestamp left, HlcTimestamp right) => left.CompareTo(right) >= 0;

    /// <summary>a 是否先于 b（严格 HLC 序）。</summary>
    /// <param name="a">候选先前事件时间戳。</param>
    /// <param name="b">候选后续事件时间戳。</param>
    /// <returns>true = a 严格先于 b；false = a 等于或后于 b。</returns>
    public static bool OccursBefore(HlcTimestamp a, HlcTimestamp b) => a.CompareTo(b) < 0;
}

/// <summary>
/// 混合逻辑时钟（二期-G1 NETGAP-024——HLC 原语，Kulkarni et al. §3 构造）：
/// <para>★ Tick（本地事件）：墙钟前进 → 物理跟钟、逻辑清零；墙钟停滞/回拨 → 逻辑 +1
/// （<b>时间戳永不回退</b>——墙钟回拨被逻辑域吸收）。</para>
/// <para>★ Receive（收到远端时间戳）：物理 = max(本端墙钟, 本端物理, 远端物理)；三方等物理时
/// 逻辑 = max(本端逻辑, 远端逻辑) + 1——因果吸收（远端领先即跟钟，跨分区重连后时序连续）。</para>
/// <para>★ 墙钟源可注入（构造参数 wallNow——测试时钟回拨/跳跃场景）。</para>
/// </summary>
public sealed class HybridLogicClock
{
    private readonly Func<long> _wallNow;
    private readonly object _gate = new();
    private long _physical;
    private int _logical;

    /// <summary>构造（<paramref name="wallNow"/> 缺省 = Unix 毫秒墙钟；测试注入假钟）。</summary>
    public HybridLogicClock(Func<long>? wallNow = null)
        => _wallNow = wallNow ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    /// <summary>构造——时钟缝统一供给源形态（故障注入面 件一）。</summary>
    /// <param name="clock">时钟供给源（取 <c>GetUtcNow</c> 的 Unix 毫秒；跳变/漂移经假钟注入；
    /// <see cref="TimeProvider.System"/> 行为与缺省构造一致）。</param>
    public HybridLogicClock(TimeProvider clock)
        : this(() => clock.GetUtcNow().ToUnixTimeMilliseconds())
    {
        ArgumentNullException.ThrowIfNull(clock);
    }

    /// <summary>当前时间戳（只观测，不推进）。</summary>
    public HlcTimestamp Current => new(Volatile.Read(ref _physical), Volatile.Read(ref _logical));

    /// <summary>本地事件：推进并返回新时间戳（单调——永不早于先前任何返回值）。</summary>
    /// <returns>推进后的新时间戳（墙钟前进 = 物理跟钟、逻辑清零；停滞/回拨 = 逻辑 +1——时间戳永不回退）。</returns>
    public HlcTimestamp Tick()
    {
        lock (_gate)
        {
            var now = _wallNow();
            if (now > _physical)
                (_physical, _logical) = (now, 0);          // 墙钟前进——物理跟钟、逻辑清零
            else
                _logical++;                                 // 墙钟停滞/回拨——逻辑域承载单调性
            return new HlcTimestamp(_physical, _logical);
        }
    }

    /// <summary>收到远端时间戳（因果吸收）：物理 = max(墙钟, 本端物理, 远端物理)；逻辑按
    /// 物理跳进来源区分——远端领先跟钟时逻辑 = remote.Logical + 1（Kulkarni 接收不变式
    /// h.j &gt; m：结果必须严格晚于被吸收的远端时间戳），墙钟领先两者（新物理严格大于远端
    /// 物理）时逻辑归零安全。物理不变时逻辑 = max(本端逻辑, 远端逻辑) + 1。单调保证同 <see cref="Tick"/>。</summary>
    /// <param name="remote">远端时间戳（随消息携带的因果上界）。</param>
    /// <returns>吸收远端因果后的新时间戳（单调且严格晚于 remote——永不早于本端先前任何返回值）。</returns>
    public HlcTimestamp Receive(HlcTimestamp remote)
    {
        lock (_gate)
        {
            var now = _wallNow();
            var maxPhysical = Math.Max(Math.Max(_physical, remote.Physical), now);
            if (maxPhysical > _physical)
            {
                _physical = maxPhysical;
                // ★ #417：跳进来源区分——maxPhysical == remote.Physical（跟钟到远端）时逻辑必须
                //   越过远端计数（remote.Logical + 1），否则结果 (max, 0) 可严格小于远端
                //   (max, remote.Logical>0)——因果倒置（Kulkarni 不等式违反）。
                //   maxPhysical > remote.Physical（墙钟领先两者）时新物理已严格大于远端物理，归零安全。
                _logical = maxPhysical == remote.Physical ? remote.Logical + 1 : 0;
            }
            else
            {
                _logical = Math.Max(_logical, remote.Logical) + 1;   // 同物理——逻辑序推进
            }
            return new HlcTimestamp(_physical, _logical);
        }
    }


}
