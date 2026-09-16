namespace TC.Tier.Core.Net.Raft;

/// <summary>
/// Raft 状态机参数（spec-01 §8 参数化——契约① 数字推导；spec-02 §6 心跳约束）。
/// <para>★ 选举超时 [150ms, 300ms] 是配置默认值（最终默认由 spec-09 压测稳定性裁决，非设计决策）；
///   心跳 ≤50ms（≪ T₀ 下界 150ms÷3——丢 1–2 个心跳不触发误选举）。</para>
/// </summary>
public sealed record RaftOptions
{
    /// <summary>默认配置（spec-01 §8 定案默认值）。</summary>
    public static RaftOptions Default { get; } = new();

    /// <summary>选举超时下界 T₀（默认 150ms——契约① 窗口下界）。</summary>
    public TimeSpan ElectionTimeoutMin { get; private init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>选举超时上界（默认 300ms = 2T₀——每次触发后重掷）。</summary>
    public TimeSpan ElectionTimeoutMax { get; private init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Leader 心跳间隔（默认 50ms——spec-02 §6；必须 ≪ ElectionTimeoutMin）。</summary>
    public TimeSpan HeartbeatInterval { get; private init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>入站事件队列容量（默认 4096——背压有界，spec-01 §6.2）。</summary>
    public int EventQueueCapacity { get; private init; } = 4096;

    /// <summary>In-flight 完成源窗口容量（池化完成源槽数——热路径零分配；打满自动回退 TCS 直通）。
    /// 默认 4096。</summary>
    public int InFlightCapacity { get; private init; } = 4096;

    /// <summary>复制策略（条数/字节/时间三维度攒批；默认全零 = 立即推送）。</summary>
    public ReplicationPolicy Replication { get; private init; } = new();

    /// <summary>复制应答档（默认 <see cref="ReplicationAck.Majority"/>——现状不变）。
    /// <para>决定默认写入入口 <see cref="RaftStateMachine.ReplicateAsync"/> 的完成水位：
    /// Majority = applied 水位（read-your-writes）；LeaderLocal = 本地持久化即返（Redis 类
    /// 异步一致部署形态——显式 per-call <see cref="RaftStateMachine.ReplicateLeaderLocalAsync"/>
    /// 与 <see cref="RaftStateMachine.ReplicateCommittedAsync"/> 不受此配置影响）。</para></summary>
    public ReplicationAck ReplicationAck { get; private init; } = ReplicationAck.Majority;

    /// <summary>随机源（选举超时重掷——测试注入固定种子保证确定性；null = Random.Shared）。</summary>
    public Random? Random { get; private init; }

    /// <summary>租约读时钟漂移上界（缺省 <see cref="TimeSpan.Zero"/> = 关）。
    /// <para>&gt; 0 时 <see cref="RaftStateMachine.ReadIndexAsync"/> 走租约快路径：leader 在
    /// "最近一次多数派确认（选举票/心跳应答）+ (选举窗下界 − 漂移界)"窗口内的线性读零往返返回——
    /// 新 leader 最早也要整段选举窗后才会诞生，窗内本 leader 的 commitIndex 即线性读位点。
    /// 窗外自动回落心跳往返确认。时钟漂移纪律由运维承担（NTP）。</para></summary>
    public TimeSpan LeaseClockDriftBound { get; private init; } = TimeSpan.Zero;

    /// <summary>时钟供给源（故障注入面 件一——时钟缝；缺省 <see cref="TimeProvider.System"/> 行为逐字节零变化）。
    /// <para>选举/心跳/复制计时走单调钟（<c>GetMsTimestamp</c> 扩展——ms 域，与 TickCount64 同域）
    /// ——换源不停走，墙钟跳变不误触发选举超时）；租约读窗按墙钟语义由注入测试显式快进/跳变驱动。</para>
    /// <para>★ 注入假钟时须让节拍注册表同钟：不传 registry 参数则状态机自动为本节点新建同钟注册表；
    /// 显式传入的 registry 须以同一 provider 构造（时钟域不一致 = deadline 判定错乱）。</para></summary>
    public TimeProvider Clock { get; private init; } = TimeProvider.System;

    /// <summary>With 链——选举超时区间（测试加速用；须 min &lt; max）。</summary>
    /// <param name="min">选举超时下界 T₀（须 &lt; <paramref name="max"/>）。</param>
    /// <param name="max">选举超时上界（须 &gt; <paramref name="min"/>）。</param>
    /// <returns>新的 RaftOptions 实例（副本语义）。</returns>
    public RaftOptions WithElectionTimeout(TimeSpan min, TimeSpan max) => this with
    {
        ElectionTimeoutMin = min,
        ElectionTimeoutMax = max,
    };

    /// <summary>With 链——心跳间隔。</summary>
    /// <param name="interval">心跳间隔（必须 ≪ ElectionTimeoutMin——丢 1–2 个心跳不触发误选举）。</param>
    /// <returns>新的 RaftOptions 实例（副本语义）。</returns>
    public RaftOptions WithHeartbeatInterval(TimeSpan interval) => this with { HeartbeatInterval = interval };

    /// <summary>With 链——复制策略。</summary>
    /// <param name="policy">复制策略（条数/字节/时间三维度攒批；全零 = 立即推送）。</param>
    /// <returns>新的 RaftOptions 实例（副本语义）。</returns>
    public RaftOptions WithReplication(ReplicationPolicy policy) => this with { Replication = policy };

    /// <summary>With 链——复制应答档（缺省档切换：LeaderLocal = 默认入口本地持久化即返）。</summary>
    /// <param name="ack">复制应答档（Majority = applied；LeaderLocal = 本地持久化即返）。</param>
    /// <returns>新的 RaftOptions 实例（副本语义）。</returns>
    public RaftOptions WithReplicationAck(ReplicationAck ack) => this with { ReplicationAck = ack };

    /// <summary>With 链——随机源（固定种子测试确定性）。</summary>
    /// <param name="random">随机源（选举超时重掷——null = Random.Shared）。</param>
    /// <returns>新的 RaftOptions 实例（副本语义）。</returns>
    public RaftOptions WithRandom(Random random) => this with { Random = random };

    /// <summary>With 链——时钟供给源（时钟缝注入：假钟快进/跳变/漂移的确定性测试；须与节拍注册表同钟）。</summary>
    /// <param name="clock">时钟供给源（null 不合法——显式还原 System 用 <see cref="TimeProvider.System"/>）。</param>
    /// <returns>新的 RaftOptions 实例（副本语义）。</returns>
    public RaftOptions WithClock(TimeProvider clock) => this with { Clock = clock };

    /// <summary>With 链——开启租约读（clockDriftBound 须 ∈ (0, ElectionTimeoutMin)——
    /// 漂移界 ≥ 选举窗则租约窗非正，永不生效）。</summary>
    /// <param name="clockDriftBound">集群时钟漂移上界（运维保证——NTP 纪律）。</param>
    /// <returns>新的 RaftOptions 实例（副本语义）。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="clockDriftBound"/> 非正或 ≥ <see cref="ElectionTimeoutMin"/>。</exception>
    public RaftOptions WithLeaseReads(TimeSpan clockDriftBound)
    {
        if (clockDriftBound <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(clockDriftBound), clockDriftBound, "时钟漂移界必须为正。");
        if (clockDriftBound >= ElectionTimeoutMin)
            throw new ArgumentOutOfRangeException(nameof(clockDriftBound),
                $"时钟漂移界必须 < ElectionTimeoutMin（{ElectionTimeoutMin.TotalMilliseconds}ms）——否则租约窗非正。");
        return this with { LeaseClockDriftBound = clockDriftBound };
    }

    /// <summary>掷一个选举超时（[min, max] 均匀——每次触发后重新掷，spec-01 §4.1 规则 4）。</summary>
    internal TimeSpan RollElectionTimeout(Random random)
    {
        var min = (long)ElectionTimeoutMin.TotalMilliseconds;
        var max = (long)ElectionTimeoutMax.TotalMilliseconds;
        return TimeSpan.FromMilliseconds(random.NextInt64(min, max + 1));
    }
}
