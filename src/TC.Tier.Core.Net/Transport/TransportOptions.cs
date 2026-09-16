using System.Net;

namespace TC.Tier.Core.Net.Transport;

/// <summary>
/// 传输装配选项（spec-12 §4.5——不可变 record，With 派生；运行参数零写死：缺省只服务零配置起步，
/// 装配期全部可覆盖；协议常量除外（帧头 16B/PayloadLen 上限/CorrId 8B = 线格式定数））。
/// <para>★ 地址表 = 装配期静态注入（NodeId → IPEndPoint）；Core.Net 不做服务发现
///   （发现是 p2p 层职责）。动态成员 = 协议域配置变更后重建本传输。</para>
/// <para>★ UDP 数据报端点：<see cref="UdpListenEndPoint"/> 非空 = 绑定
///   UDP 端点并经握手通告（特性位 bit1，Negotiate 帧承载）；null = UDP 关闭。</para>
/// </summary>
/// <param name="ListenEndPoint">本端 TCP 监听端点（null = 不监听——纯拨号方；对称节点可随时开监听）。</param>
/// <param name="Peers">对端地址表（装配期静态——成员制 TCP 拨号目标）。</param>
/// <param name="HandshakeTimeout">握手超时（三步任一环未完成即超时断连——缺省 3s）。</param>
/// <param name="ReconnectInitialDelay">重连退避初值（缺省 100ms）。</param>
/// <param name="ReconnectMaxDelay">重连退避封顶（缺省 2s——raft 消费方按 ElectionTimeoutMin/2 覆盖：
///   选举节奏不被重连劫持；此处为传输层通用缺省）。</param>
/// <param name="ClusterTag">集群归属标签（握手期校验——错集群 fail-fast，§3.3）。</param>
/// <param name="UdpListenEndPoint">本端 UDP 数据报端点（null = 关闭；port 0 = 装配期取实际端口。
///   地址须为具体可达地址——通配 Any 绑定在通告时以 TCP 连接本端地址替代）。</param>
/// <param name="UdpMaxDatagramBytes">单报预算（缺省 1200；不做 IP 分片依赖，超预算回落 TCP 承载）。</param>
/// <param name="EnableKeepalive">传输级保活（默认关闭——raft 心跳即业务级保活；
///   特性位 bit0 协商双方都开才生效）。</param>
/// <param name="KeepaliveInterval">保活周期（null = 10s 缺省；测试可调小）。</param>
/// <param name="KeepaliveMaxUnanswered">连续无入站保活次数达此值断连（缺省 3）。</param>
/// <param name="ReconnectBackoffFactor">重连退避倍率（缺省 ×2——连续失败按倍率增长，建立过即复位）。</param>
/// <param name="SlowDispatchThreshold">慢回调计数阈值（null = 10ms 缺省量级值——§5.1
///   快进快出契约的可观测兜底，非机制）。</param>
/// <param name="RequestTimeout">请求回调缺省超时（缺省 3s——at-most-once 单发等待；per-call RequestOptions 覆盖）。</param>
/// <param name="RequestPendingCapacity">请求回调 pending 关联表容量（在途请求数上限，缺省 4096——满抛 fail-fast）。</param>
/// <param name="RequestDedupCapacity">应答端 CorrId 去重窗口容量（已服务请求记忆上限，缺省 1024——
///   at-least-once 缓存响应重放不重复执行；窗口满淘汰最老）。</param>
/// <param name="MaxInboundLinks">最大入站链路数（二期-D4 连接治理——握手准入上限；0 = 不限）。</param>
/// <param name="HandshakeFailCooldown">握手失败冷却（二期-D4——同源 IP 握手失败后的准入冷却窗；Zero = 关闭）。</param>
/// <param name="RequestQueueWait">请求背压 Queue 策略的排队等待上限（缺省 500ms——超时回落 fail-fast）。</param>
/// <param name="RequestPriorityReserve">请求背压 Priority 域预留槽位（缺省 256——共享池满仍可入）。</param>
/// <param name="RequestDedupMaxBytes">应答端去重窗口字节软预算（缺省 8 MiB——大响应驻留上界 =
///   预算 + 最大单条应答；超预算驱逐最老。单条超预算独占驻留——"不缓存"会破坏 at-least-once
///   确认契约，故预算是驱逐目标非准入门槛）。</param>
/// <param name="StreamWindowFrames">流式会话入站缓冲帧数（背压窗口，缺省 16——满 = 读循环 await 传导对端写）。</param>
/// <param name="WriteQueueFrames">写队列帧容量（缺省 64——写者入队背压深度：流写背压停 writer 循环
///   不锁写者，数据报/心跳/请求回调有界等待不饿死；帧序 = 单 writer FIFO）。</param>
public sealed record TransportOptions(
    IPEndPoint? ListenEndPoint,
    IReadOnlyDictionary<NodeId, IPEndPoint> Peers,
    TimeSpan HandshakeTimeout,
    TimeSpan ReconnectInitialDelay,
    TimeSpan ReconnectMaxDelay,
    uint ClusterTag = 0,
    IPEndPoint? UdpListenEndPoint = null,
    int UdpMaxDatagramBytes = 1200,
    bool EnableKeepalive = false,
    TimeSpan? KeepaliveInterval = null,
    int KeepaliveMaxUnanswered = 3,
    double ReconnectBackoffFactor = 2.0,
    TimeSpan? SlowDispatchThreshold = null,
    TimeSpan RequestTimeout = default,
    int RequestPendingCapacity = 4096,
    int RequestDedupCapacity = 1024,
    int StreamWindowFrames = 16,
    int WriteQueueFrames = 64,
    long RequestDedupMaxBytes = TransportOptions.DefaultRequestDedupMaxBytes,
    int MaxInboundLinks = 0,
    TimeSpan HandshakeFailCooldown = default,
    TimeSpan RequestQueueWait = default,
    int RequestPriorityReserve = 256)
{
    /// <summary>缺省集群标签（0——单集群部署零配置起步；多集群共存必须显式配置区分，§3.3）。</summary>
    public const uint DefaultClusterTag = 0;

    /// <summary>去重窗口字节软预算缺省（8 MiB）。</summary>
    private const long DefaultRequestDedupMaxBytes = 8 << 20;

    /// <summary>保活周期缺省（10s——NAT/QUIC 映射场景）。</summary>
    private static readonly TimeSpan DefaultKeepaliveInterval = TimeSpan.FromSeconds(10);

    /// <summary>慢回调阈值缺省（10ms 量级值——可观测兜底非机制，实施定）。</summary>
    private static readonly TimeSpan DefaultSlowDispatchThreshold = TimeSpan.FromMilliseconds(10);

    /// <summary>请求回调超时缺省（3s）。</summary>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(3);

    /// <summary>生效请求超时（显式配置 ?? 缺省 3s——default(TimeSpan) 视为未配置）。</summary>
    public TimeSpan EffectiveRequestTimeout => RequestTimeout == TimeSpan.Zero ? DefaultRequestTimeout : RequestTimeout;

    /// <summary>缺省选项（握手 3s / 退避 100ms ×2 封顶 2s / UDP 关闭 / 保活关闭）。</summary>
    /// <param name="listenEndPoint">本端 TCP 监听端点（null = 不监听）。</param>
    /// <param name="peers">对端地址表（装配期静态）。</param>
    /// <returns>缺省值派生的选项实例。</returns>
    public static TransportOptions Default(IPEndPoint? listenEndPoint, IReadOnlyDictionary<NodeId, IPEndPoint> peers) =>
        new(listenEndPoint, peers,
            HandshakeTimeout: TimeSpan.FromSeconds(3),
            ReconnectInitialDelay: TimeSpan.FromMilliseconds(100),
            ReconnectMaxDelay: TimeSpan.FromSeconds(2));

    /// <summary>生效保活周期（显式配置 ?? 缺省 10s）。</summary>
    public TimeSpan EffectiveKeepaliveInterval => KeepaliveInterval ?? DefaultKeepaliveInterval;

    /// <summary>生效慢回调阈值（显式配置 ?? 缺省 10ms）。</summary>
    public TimeSpan EffectiveSlowDispatchThreshold => SlowDispatchThreshold ?? DefaultSlowDispatchThreshold;

    // ══ With 链（With 新实例——StorageEngineOptions 同款惯例）══

    /// <summary>派生：本端 TCP 监听端点。</summary>
    /// <param name="listenEndPoint">新监听端点（null = 不监听）。</param>
    /// <returns>替换了监听端点的新实例（其余位不变）。</returns>
    public TransportOptions WithListen(IPEndPoint? listenEndPoint) => this with { ListenEndPoint = listenEndPoint };

    /// <summary>派生：对端地址表（装配期静态注入替换）。</summary>
    /// <param name="peers">新对端地址表（整表替换，非空）。</param>
    /// <returns>替换了地址表的新实例（其余位不变）。</returns>
    public TransportOptions WithPeers(IReadOnlyDictionary<NodeId, IPEndPoint> peers) => this with { Peers = peers };

    /// <summary>派生：集群归属标签（握手期校验——错集群 fail-fast）。</summary>
    /// <param name="clusterTag">新集群归属标签。</param>
    /// <returns>替换了集群标签的新实例（其余位不变）。</returns>
    public TransportOptions WithClusterTag(uint clusterTag) => this with { ClusterTag = clusterTag };

    /// <summary>派生：握手超时。</summary>
    /// <param name="timeout">新握手超时（须为正——<see cref="Validate"/> 校验）。</param>
    /// <returns>替换了握手超时的新实例（其余位不变）。</returns>
    public TransportOptions WithHandshakeTimeout(TimeSpan timeout) => this with { HandshakeTimeout = timeout };

    /// <summary>派生：重连退避（初值/倍率/封顶）。</summary>
    /// <param name="initialDelay">重连退避初值（须为正）。</param>
    /// <param name="backoffFactor">退避倍率（须 ≥ 1）。</param>
    /// <param name="maxDelay">退避封顶（不得低于初值）。</param>
    /// <returns>替换了三项退避参数的新实例（其余位不变）。</returns>
    public TransportOptions WithReconnect(TimeSpan initialDelay, double backoffFactor, TimeSpan maxDelay) =>
        this with { ReconnectInitialDelay = initialDelay, ReconnectBackoffFactor = backoffFactor, ReconnectMaxDelay = maxDelay };

    /// <summary>派生：本端 UDP 数据报端点与单报预算（endpoint null = 关闭 UDP）。</summary>
    /// <param name="udpListenEndPoint">新 UDP 监听端点（null = 关闭 UDP；port 0 = 装配期取实际端口）。</param>
    /// <param name="maxDatagramBytes">单报预算（字节，须为正；默认 1200）。</param>
    /// <returns>替换了 UDP 端点与预算的新实例（其余位不变）。</returns>
    public TransportOptions WithUdp(IPEndPoint? udpListenEndPoint, int maxDatagramBytes = 1200) =>
        this with { UdpListenEndPoint = udpListenEndPoint, UdpMaxDatagramBytes = maxDatagramBytes };

    /// <summary>派生：传输级保活（周期 null = 10s 缺省）。</summary>
    /// <param name="enable">是否启用传输级保活（缺省关闭——特性位协商双方都开才生效）。</param>
    /// <param name="interval">保活周期（null = 10s 缺省；默认 null）。</param>
    /// <param name="maxUnanswered">连续无入站保活断连阈值（须 ≥ 1；默认 3）。</param>
    /// <returns>替换了三项保活参数的新实例（其余位不变）。</returns>
    public TransportOptions WithKeepalive(bool enable, TimeSpan? interval = null, int maxUnanswered = 3) =>
        this with { EnableKeepalive = enable, KeepaliveInterval = interval, KeepaliveMaxUnanswered = maxUnanswered };

    /// <summary>派生：慢回调计数阈值（null = 10ms 缺省）。</summary>
    /// <param name="threshold">新慢回调阈值（null = 缺省 10ms；显式值须为正）。</param>
    /// <returns>替换了慢回调阈值的新实例（其余位不变）。</returns>
    public TransportOptions WithSlowDispatchThreshold(TimeSpan? threshold) => this with { SlowDispatchThreshold = threshold };

    /// <summary>派生：连接治理（二期-D4）——最大入站链路数（0 = 不限）+ 握手失败冷却（同源 IP；Zero = 关闭）。</summary>
    /// <param name="maxInboundLinks">最大入站链路数（握手准入上限；0 = 不限）。</param>
    /// <param name="handshakeFailCooldown">同源 IP 握手失败后的准入冷却窗（Zero = 关闭）。</param>
    /// <returns>替换了连接治理参数的新实例（其余位不变）。</returns>
    public TransportOptions WithConnectionGovernance(int maxInboundLinks, TimeSpan handshakeFailCooldown) =>
        this with { MaxInboundLinks = maxInboundLinks, HandshakeFailCooldown = handshakeFailCooldown };

    /// <summary>派生：请求背压旋钮（二期-D5）——Queue 策略排队等待上限 + Priority 域预留槽位。</summary>
    /// <param name="requestQueueWait">Queue 策略排队等待上限（Zero = 生效 500ms 缺省——超时回落 fail-fast）。</param>
    /// <param name="requestPriorityReserve">Priority 域预留槽位数（共享池满仍可入）。</param>
    /// <returns>替换了请求背压旋钮的新实例（其余位不变）。</returns>
    public TransportOptions WithRequestBackpressure(TimeSpan requestQueueWait, int requestPriorityReserve) =>
        this with { RequestQueueWait = requestQueueWait, RequestPriorityReserve = requestPriorityReserve };

    /// <summary>派生：请求回调（缺省超时/关联表容量/去重窗口容量与字节软预算）。</summary>
    /// <param name="timeout">缺省请求超时（null = 视为未配置，生效 3s 缺省；默认 null）。</param>
    /// <param name="pendingCapacity">pending 关联表容量（在途请求数上限，须 ≥ 1；默认 4096）。</param>
    /// <param name="dedupCapacity">应答端去重窗口容量（须 ≥ 1；默认 1024）。</param>
    /// <param name="dedupMaxBytes">去重窗口字节软预算（字节；null = 保持现值；默认 null）。</param>
    /// <returns>替换了请求回调参数的新实例（其余位不变）。</returns>
    public TransportOptions WithRequest(TimeSpan? timeout = null, int pendingCapacity = 4096,
        int dedupCapacity = 1024, long? dedupMaxBytes = null) =>
        this with { RequestTimeout = timeout ?? default, RequestPendingCapacity = pendingCapacity,
            RequestDedupCapacity = dedupCapacity, RequestDedupMaxBytes = dedupMaxBytes ?? RequestDedupMaxBytes };

    /// <summary>
    /// 装配期校验（fail-fast）——record 的 <c>with</c> 派生不重跑构造校验，故校验收敛为显式面，
    /// 由传输构造（选项唯一消费入口）调用：时序非正/倍率 &lt;1/预算非正/封顶低于初值均为矛盾配置。
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">任一运行参数矛盾。</exception>
    /// <exception cref="ArgumentException">地址表含 Empty 哨兵节点。</exception>
    public void Validate()
    {
        if (HandshakeTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(HandshakeTimeout), "握手超时必须为正。");
        if (ReconnectInitialDelay <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ReconnectInitialDelay), "重连退避初值必须为正。");
        if (ReconnectMaxDelay < ReconnectInitialDelay) throw new ArgumentOutOfRangeException(nameof(ReconnectMaxDelay), "重连退避封顶不得低于初值。");
        if (ReconnectBackoffFactor < 1.0) throw new ArgumentOutOfRangeException(nameof(ReconnectBackoffFactor), "重连退避倍率必须 ≥ 1（连续失败退避不衰减）。");
        if (KeepaliveMaxUnanswered < 1) throw new ArgumentOutOfRangeException(nameof(KeepaliveMaxUnanswered), "保活断连阈值必须 ≥ 1。");
        if (UdpMaxDatagramBytes <= 0) throw new ArgumentOutOfRangeException(nameof(UdpMaxDatagramBytes), "UDP 单报预算必须为正。");
        if (SlowDispatchThreshold is { } threshold && threshold <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(SlowDispatchThreshold), "慢回调阈值必须为正。");
        if (RequestTimeout != default && RequestTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout), "请求超时必须为正。");
        if (RequestPendingCapacity < 1)
            throw new ArgumentOutOfRangeException(nameof(RequestPendingCapacity), "请求关联表容量必须 ≥ 1。");
        if (RequestDedupCapacity < 1)
            throw new ArgumentOutOfRangeException(nameof(RequestDedupCapacity), "去重窗口容量必须 ≥ 1。");
        if (RequestDedupMaxBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(RequestDedupMaxBytes), "去重窗口字节软预算必须 ≥ 1。");
        if (StreamWindowFrames < 1)
            throw new ArgumentOutOfRangeException(nameof(StreamWindowFrames), "流式会话背压窗口帧数必须 ≥ 1。");
        if (WriteQueueFrames < 1)
            throw new ArgumentOutOfRangeException(nameof(WriteQueueFrames), "写队列帧容量必须 ≥ 1。");

        ArgumentNullException.ThrowIfNull(Peers);
        foreach (var (peer, endpoint) in Peers)
        {
            if (peer == NodeId.Empty) throw new ArgumentException("地址表含 Empty 哨兵节点 ID。", nameof(Peers));
            ArgumentNullException.ThrowIfNull(endpoint, nameof(Peers));
        }
    }
}
