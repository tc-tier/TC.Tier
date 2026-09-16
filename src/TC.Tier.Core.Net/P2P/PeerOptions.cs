namespace TC.Tier.Core.Net.P2P;

/// <summary>
/// HyParView 会员管理参数（spec-06 §2 定案 × spec-12 §4.5 运行参数零写死——
/// 文中数值仅为缺省，装配期全可覆盖）。
/// </summary>
public sealed record PeerOptions
{
    /// <summary>默认配置（spec-06 §2 表）。</summary>
    public static PeerOptions Default { get; } = new();

    /// <summary>活跃视图大小 k（默认 4——对称活跃邻居上限）。</summary>
    public int ActiveViewSize { get; private init; } = 4;

    /// <summary>被动视图上限（默认 20——非对称备用邻居池）。</summary>
    public int PassiveViewSize { get; private init; } = 20;

    /// <summary>JOIN 随机游走 TTL（默认 6——转发跳数上限）。</summary>
    public int JoinTtl { get; private init; } = 6;

    /// <summary>JOIN 应答有界等待（默认 5s——LAN 内往返毫秒级；超时 = 应答丢失，种子已在活跃视图即会员关系已建立，视图收敛交给 shuffle/周期维护）。</summary>
    public TimeSpan JoinWaitTimeout { get; private init; } = TimeSpan.FromSeconds(5);

    /// <summary>Shuffle 周期（默认 60s——被动视图定期交换，成员搅动收敛）。</summary>
    public TimeSpan ShuffleInterval { get; private init; } = TimeSpan.FromSeconds(60);

    /// <summary>心跳间隔（默认 5s——PhiAccrual 采样周期）。</summary>
    public TimeSpan HeartbeatInterval { get; private init; } = TimeSpan.FromSeconds(5);

    /// <summary>φ 故障阈值（默认 8——spec-06 §5 ≈ 误判率可忽略）。</summary>
    public double PhiThreshold { get; private init; } = 8;

    /// <summary>主动随机游走长度（默认 3——论文参数 ARWL）。</summary>
    public int Arwl { get; private init; } = 3;

    /// <summary>被动随机游走长度（默认 6——论文参数 PRWL）。</summary>
    public int Prwl { get; private init; } = 6;

    /// <summary>随机源（视图选择/游走——测试注入固定种子保证确定性；null = Random.Shared）。</summary>
    public Random? Random { get; private init; }

    /// <summary>Gossip 广播（null = 未开启——opt-in 能力，<see cref="PeerMechanism.Broadcast"/>
    ///   挂载后非空；spec-06 §6 dissemination 件——会员管理不含广播，广播是独立组件）。</summary>
    public BroadcastOptions? Broadcast { get; private init; }

    /// <summary>With 链——活跃视图大小（测试收敛验证）。</summary>
    /// <param name="k">新活跃视图大小（对称活跃邻居上限）。</param>
    /// <returns>替换了活跃视图大小的新实例（其余位不变）。</returns>
    public PeerOptions WithActiveViewSize(int k) => this with { ActiveViewSize = k };

    /// <summary>With 链——被动视图上限。</summary>
    /// <param name="n">新被动视图上限（非对称备用邻居池容量）。</param>
    /// <returns>替换了被动视图上限的新实例（其余位不变）。</returns>
    public PeerOptions WithPassiveViewSize(int n) => this with { PassiveViewSize = n };

    /// <summary>With 链——JOIN TTL。</summary>
    /// <param name="ttl">新 JOIN 随机游走 TTL（转发跳数上限）。</param>
    /// <returns>替换了 JOIN TTL 的新实例（其余位不变）。</returns>
    public PeerOptions WithJoinTtl(int ttl) => this with { JoinTtl = ttl };

    /// <summary>With 链——JOIN 应答等待（丢包介质测试缩短等待）。</summary>
    /// <param name="timeout">新 JOIN 应答有界等待时长。</param>
    /// <returns>替换了 JOIN 等待时长的新实例（其余位不变）。</returns>
    public PeerOptions WithJoinWaitTimeout(TimeSpan timeout) => this with { JoinWaitTimeout = timeout };

    /// <summary>With 链——shuffle 周期（测试加速）。</summary>
    /// <param name="interval">新 shuffle 周期（被动视图定期交换间隔）。</param>
    /// <returns>替换了 shuffle 周期的新实例（其余位不变）。</returns>
    public PeerOptions WithShuffleInterval(TimeSpan interval) => this with { ShuffleInterval = interval };

    /// <summary>With 链——心跳间隔（测试加速）。</summary>
    /// <param name="interval">新心跳间隔（PhiAccrual 采样周期）。</param>
    /// <returns>替换了心跳间隔的新实例（其余位不变）。</returns>
    public PeerOptions WithHeartbeatInterval(TimeSpan interval) => this with { HeartbeatInterval = interval };

    /// <summary>With 链——φ 阈值。</summary>
    /// <param name="threshold">新 φ 故障阈值（越大越不容忍延迟抖动——误判率越低、检出越慢）。</param>
    /// <returns>替换了 φ 阈值的新实例（其余位不变）。</returns>
    public PeerOptions WithPhiThreshold(double threshold) => this with { PhiThreshold = threshold };

    /// <summary>With 链——随机源。</summary>
    /// <param name="random">新随机源（视图选择/游走；测试可注入固定种子）。</param>
    /// <returns>替换了随机源的新实例（其余位不变）。</returns>
    public PeerOptions WithRandom(Random random) => this with { Random = random };

    /// <summary>With 链——开启 Gossip 广播（opt-in；null = 用 <see cref="BroadcastOptions.Default"/> 缺省表）。</summary>
    /// <param name="broadcast">广播参数；null = <see cref="BroadcastOptions.Default"/>（默认 null）。</param>
    /// <returns>开启了广播的新实例（其余位不变）。</returns>
    public PeerOptions WithBroadcast(BroadcastOptions? broadcast = null)
        => this with { Broadcast = broadcast ?? BroadcastOptions.Default };

    /// <summary>时钟供给源（故障注入面 件一——时钟缝 P1 落点；缺省 <see cref="TimeProvider.System"/> 行为零变化）。
    /// <para>心跳节拍/shuffle 节拍/活跃表新鲜度经本源驱动——假钟下由快进确定性触发。</para></summary>
    public TimeProvider Clock { get; private init; } = TimeProvider.System;

    /// <summary>With 链——时钟供给源。</summary>
    /// <param name="clock">时钟供给源（非空）。</param>
    /// <returns>替换 Clock 后的新实例。</returns>
    public PeerOptions WithClock(TimeProvider clock) => this with { Clock = clock };
}
