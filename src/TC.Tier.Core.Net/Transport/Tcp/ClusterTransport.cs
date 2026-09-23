using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using TC.Tier.Core.Logging;
using TC.Tier.Core.Net.Channels;
using TC.Tier.Core.Observability;
using TC.Tier.Core.Net.Security;
using TC.Tier.Core.Net.Wire;
using TC.Tier.Core.Execution;

namespace TC.Tier.Core.Net.Transport.Tcp;

/// <summary>
/// 统一传输枢纽（spec-12 §4——装配期每节点一个；协议域注册/数据报发送/节点观测/故障注入）。
/// <para>★ 寻址两制（§4.1）：成员制 = 装配期静态地址表（NodeId → 端点）驱动自动拨号与集群路由；
///   地址制 = <see cref="ConnectAsync"/> 直连（对端身份握手得知——不在地址表也可建立，
///   链路仅承载该连接上的定向通信，断开不自动重连）。</para>
/// <para>★ 准入分层（§4.3）：连接准入 = 任何完成合法握手（帧/版本/ClusterTag/安全形态）的连接
///   可建立——成员与直连共用同一监听口；成员准入 = 地址表成员（拨号归属违规检查：成员制
///   较小方拨号——入站来自较大成员 = 握手期违规；地址制有地址者拨号，大小无关）。</para>
/// <para>★ 成员制连接模型：每有序节点对一条 TCP 长连接——NodeId 较小方主动拨号并独占
///   重连退避（较大方只监听）；所有协议域共享该连接；断线重连指数退避（初值 ×倍率，
///   封顶 = raft 消费方按 ElectionTimeoutMin/2 覆盖——选举协议不被重连节奏劫持）。</para>
/// <para>★ UDP 数据报端点（§4.5）：<see cref="TransportOptions.UdpListenEndPoint"/> 配置后
///   绑定本端 UDP 并经握手通告（Negotiate 帧承载，特性位 bit1）；bearer 声明 UDP 的协议域在
///   对端端点已知且单报预算内时走 UDP，其余回落 TCP 帧流承载——尽力送达语义不变。</para>
/// <para>★ 生命周期：构造（校验/装配）→ <see cref="Start"/>（监听 + 拨号循环）→
///   <see cref="DisposeAsync"/>（取消 + 链路关闭 + 有界收尾）。</para>
/// <para>★ 尽力送达（§5.1 语义）：对端未连接/注入丢弃 = 静默丢弃 + 计数——协议域
///   （raft 定时器）自愈；发送只对参数错误抛。</para>
/// </summary>
public sealed partial class ClusterTransport : IProtocolTransport, ICoreProtocolPort, IRaftDialer, IPeerRegistry
{
    /// <summary>UDP 接收缓冲（UDP 数据报理论上限 65527——一次租借循环期独占复用）。</summary>
    private const int UdpReceiveBufferSize = 65536;

    private readonly ConcurrentDictionary<NodeId, PeerLink> _links = new();
    private readonly DatagramDispatcher _dispatcher;   // 协议域注册/分发（Channels/ 形态组件——全介质单源）
    private readonly RequestBroker _requests;          // 请求回调（Channels/ 形态组件——全介质单源）
    private readonly StreamBroker _streams;            // 流式会话（TCP 介质实现——Channels/ 契约）
    private readonly ConcurrentDictionary<NodeId, byte> _everConnected = new();
    private readonly ConcurrentDictionary<NodeId, IPEndPoint> _udpEndpoints = new();
    private readonly ConcurrentDictionary<IPEndPoint, NodeId> _udpReverse = new();
    private readonly ConcurrentDictionary<Task, byte> _loopThreads = new();   // 长循环专用线程跟踪（Dispose 有界等退）
    private readonly CancellationTokenSource _cts = new();
    // ★ 二期-C2 §5.3：动态成员三面——对端表（端点可更新）/ per-peer 拨号循环取消源 / 入站黑名单
    private readonly ConcurrentDictionary<NodeId, PeerEntry> _peers = new();
    private readonly ConcurrentDictionary<NodeId, CancellationTokenSource> _dialLoops = new();
    private readonly ConcurrentDictionary<NodeId, byte> _inboundDeny = new();
    // ★ 二期-D4：握手失败冷却（同源 IP → 冷却到期 ticks）+ 拒绝计数（诊断面）
    private readonly ConcurrentDictionary<IPAddress, long> _handshakeCooldown = new();
    private long _cooldownRejects;
    // ★ 二期-D6 NETGAP-011：运行时可变旋钮（重连/保活/超时子集——Update 校验收口，各旋钮独立生效）
    private readonly RuntimeTunables _tunables = new();
    private readonly object _tunablesGate = new();
    // ★ 二期-H3/H4：审计 sink + 授权钩子（null = 全放行——既有语义保持）
    private readonly INodeAuthorizer? _authorizer;
    private readonly IAuditSink? _audit;
    private readonly QosManager _qos = new();   // ★ 二期-E4：每协议/每来源限流（NETGAP-032）

    /// <summary>对端表条目（端点可更新——拨号循环每轮重读）。</summary>
    private sealed class PeerEntry
    {
        public volatile IPEndPoint Endpoint = default!;
    }
    private TcpListener? _listener;
    private UdpClient? _udp;
    private readonly ConcurrentDictionary<NodeId, byte[]> _udpAuthKeys = new();   // KeyPair 档：对端 UDP 标签键（TCP 会话派生）
    private IPEndPoint? _udpLocalEndPoint;
    private int _started;
    private int _disposed;

    /// <summary>构造（不绑定资源——启动在 <see cref="Start"/>；选项校验 fail-fast——装配期矛盾配置即抛）。</summary>
    /// <param name="self">本端节点 ID。</param>
    /// <param name="options">传输选项（地址表/握手超时/重连退避）。</param>
    /// <param name="logger">日志（可选）。</param>
    /// <param name="hub">可观测中心（可选——null = Disabled：Net 视图零开销；spec-12 §9.1 唯一接入点）。</param>
    /// <param name="security">安全选项（可选——null = 明文档）。</param>
    /// <param name="authorizer">节点授权钩子（二期-H4——入站请求准入判定；null = 全放行）。</param>
    /// <param name="audit">审计 sink（二期-H3——准入/拒绝事件落审计面；null = 不落）。</param>
    /// <exception cref="ArgumentException">本端节点 ID 不能为 Empty 哨兵。</exception>
    public ClusterTransport(NodeId self, TransportOptions options, SecurityOptions? security = null,
        ILogger? logger = null, ObservabilityHub? hub = null,
        INodeAuthorizer? authorizer = null, IAuditSink? audit = null)
    {
        if (self == NodeId.Empty) throw new ArgumentException("本端节点 ID 不能为 Empty 哨兵。", nameof(self));
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        Self = self;
        Options = options;
        Security = security ?? SecurityOptions.Plain;
        Logger = logger;
        _faultsImpl = new FaultsImpl(this);
        _dispatcher = new DatagramDispatcher(options.EffectiveSlowDispatchThreshold, OnUnknownProtocolDropped, OnSlowDispatch);
        _requests = new RequestBroker(options.RequestPendingCapacity, options.RequestDedupCapacity, options.RequestDedupMaxBytes,
            options.RequestQueueWait, options.RequestPriorityReserve,
            slowRequestThreshold: Options.EffectiveSlowDispatchThreshold,
            onSlowRequest: OnSlowRequest, tracer: Hub?.Tracer);
        _streams = new StreamBroker(options.StreamWindowFrames,
            slowAcceptThreshold: Options.EffectiveSlowDispatchThreshold,
            onSlowAccept: OnSlowStreamAccept);
        NetView = hub?.Net;
        Hub = hub;
        _authorizer = authorizer;
        _audit = audit;
    }

    /// <summary>审计事件上报（安全事件/管理动作——sink 缺省无则静默）。</summary>
    internal void Audit(string @event, string actor, string detail)
        => _audit?.OnAudit(@event, actor, detail);

    /// <summary>安全配置（spec-12 §3.4——null = 明文档；握手防降级与记录层按此驱动）。</summary>
    public SecurityOptions Security { get; }

    /// <summary>安全档线协议值（握手 Security 字段——<see cref="Security"/>.Mode 同源）。</summary>
    internal byte SecurityByte => (byte)Security.Mode;

    private void OnUnknownProtocolDropped(byte protocolId)
        => NetView?.OnDatagramDropped(protocolId, "unknown_protocol");

    private void OnSlowDispatch(byte protocolId)
    {
        NetView?.OnSlowDispatch(protocolId);
        Logger?.LogDebug("数据报分发慢回调：protocol=0x{Protocol:X2} 耗时 >{Threshold}ms",
            protocolId, (int)Options.EffectiveSlowDispatchThreshold.TotalMilliseconds);
    }

    /// <summary>请求回调慢处理回调（二期-I3——NetView 计数 + 日志）。</summary>
    private void OnSlowRequest(byte protocolId, double elapsedMs)
    {
        NetView?.OnSlowRequest(protocolId, elapsedMs);
        Logger?.LogDebug("请求回调慢处理：protocol=0x{Protocol:X2} 耗时 {Elapsed:F0}ms", protocolId, elapsedMs);
    }

    /// <summary>流 acceptor 慢回调（二期-I3——NetView 计数 + 日志）。</summary>
    private void OnSlowStreamAccept(byte protocolId, double elapsedMs)
    {
        NetView?.OnSlowStreamAccept(protocolId, elapsedMs);
        Logger?.LogDebug("流 acceptor 慢回调：protocol=0x{Protocol:X2} 耗时 {Elapsed:F0}ms", protocolId, elapsedMs);
    }

    /// <summary>本端节点 ID。</summary>
    public NodeId Self { get; }

    /// <summary>生效传输配置（装配自证——显式方法/注入/管道裁决后的最终值，§4.5 诊断面；
    /// 与 <see cref="Hosting.NodeEndpoint.Options"/> 同语义）。</summary>
    public TransportOptions Options { get; }
    internal ILogger? Logger { get; }
    internal FaultsImpl Injector => _faultsImpl;

    /// <summary>Net 维度视图（null = Disabled——装配期注入；spec-12 §9.1 只调视图绝不直调 sink）。</summary>
    internal ObservabilityHub.NetView? NetView { get; }

    /// <summary>观测中枢（二期-I4 trace 传播走 Hub 入口——不直调 tracer）。</summary>
    internal ObservabilityHub? Hub { get; }

    /// <summary>故障注入（spec-12 §9.2 常设面——InProcess/TCP 等价复用）。</summary>
    public ITransportFaultInjector Faults => _faultsImpl;

    /// <summary>监听实际绑定端点（Start 后可用；port 0 装配时取实际端口——测试/装配便利）。</summary>
    public IPEndPoint? LocalEndPoint => (IPEndPoint?)_listener?.LocalEndpoint;

    /// <summary>UDP 数据报实际绑定端点（Start 且配置了 UdpListenEndPoint 后可用）。</summary>
    public IPEndPoint? UdpLocalEndPoint => _udpLocalEndPoint;

    /// <summary>本端握手通告特性位（§3.3——配置驱动：UDP 端点已配 = bit1，保活已配 = bit0，
    /// 追踪启用 = bit3（二期-I4））。</summary>
    internal byte AdvertisedFeatures =>
        (byte)((Options.UdpListenEndPoint is not null ? HandshakeFeatures.UdpEndpoint : HandshakeFeatures.None)
               | (Options.EnableKeepalive ? HandshakeFeatures.Keepalive : HandshakeFeatures.None)
               | (Hub is { TracingEnabled: true } ? HandshakeFeatures.Trace : HandshakeFeatures.None));

    /// <summary>对端可达性观测（握手完成 = 在线——PeerConnected 判定点唯一）。</summary>
    public event Action<NodeId>? PeerConnected;

    /// <summary>对端离线观测（Established 链路断开才触发——握手失败不算）。</summary>
    public event Action<NodeId>? PeerGone;

    // ══ 生命周期 ══

    /// <summary>启动（监听循环 + 每个较小 ID 对端一个拨号循环）——幂等。</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0) return;

        if (Options.ListenEndPoint is { } listen)
        {
            _listener = new TcpListener(listen);
            _listener.Start();
            StartLoopThread($"tcp-accept-{Self}", AcceptLoopAsync);
        }

        if (Options.UdpListenEndPoint is { } udpListen)
        {
            var udp = new UdpClient(udpListen);
            DisableUdpConnReset(udp);
            _udp = udp;
            _udpLocalEndPoint = (IPEndPoint)udp.Client.LocalEndPoint!;
            StartLoopThread($"udp-receive-{Self}", UdpReceiveLoopAsync);
        }

        // ★ 二期-C2：静态地址表 → 动态对端表（拨号循环按归属规则自起，端点每轮重读）
        foreach (var (peerId, peerEndpoint) in Options.Peers)
            _peers.GetOrAdd(peerId, _ => new PeerEntry()).Endpoint = peerEndpoint;
        foreach (var peerId in _peers.Keys)
        {
            if (peerId == Self || Self.CompareTo(peerId) > 0) continue;   // 较小方才拨号（spec-12 §4.3）
            StartPeerDialLoop(peerId);
        }
    }

    /// <summary>
    /// 长稳定循环起线（专用 LongRunning 线程 + AsyncPump 泵域——2026-09-03 活性判例：池续体丢失
    /// = 接收/写/保活循环停转 = 链路僵死/心跳停发）。循环体契约：域内 await 不写
    /// <c>ConfigureAwait(false)</c>——续体回流泵线程，池不在传输关键路径。
    /// 线程句柄入 <see cref="_loopThreads"/>，<see cref="DisposeAsync"/> 有界等退。
    /// </summary>
    private void StartLoopThread(string name, Func<CancellationToken, Task> loop, CancellationToken? token = null)
    {
        var pump = new AsyncPump(name, Logger);
        var ct = token ?? _cts.Token;
        var thread = Task.Factory.StartNew(
            () =>
            {
                try { pump.Run(() => loop(ct), ct); }
                catch (OperationCanceledException) { }   // Dispose 取消——常态退出
                catch (Exception ex) { Logger?.LogError(ex, "传输循环泵外逃逸：{Loop}", name); }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
        _loopThreads[thread] = 0;
        thread.ContinueWith(static (t, s) => ((ConcurrentDictionary<Task, byte>)s!).TryRemove(t, out _),
            _loopThreads, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>
    /// Windows：禁用 SIO_UDP_CONNRESET——ICMP 不可达不再引发随后 Receive 抛 ConnectionReset
    /// （尽力送达语义下对端未开/已死不构成错误；非 Windows 无此形态，接收循环 SocketException 兜底）。
    /// </summary>
    private static void DisableUdpConnReset(UdpClient udp)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            udp.Client.IOControl(unchecked((int)0x9800000C), [0], null);
        }
        catch (SocketException)
        {
            // 平台/版本不支持——忽略，接收循环异常兜底仍覆盖
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listener = _listener!;
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                Logger?.LogDebug("Accept 失败（继续）：{Message}", ex.Message);
                continue;
            }

            client.NoDelay = true;
            StartLoopThread($"tcp-in-{client.Client.RemoteEndPoint}", innerCt => AcceptHandshakeAsync(client, innerCt));
        }
    }

    private async Task AcceptHandshakeAsync(TcpClient client, CancellationToken ct)
    {
        // ★ 二期-D4 连接治理：准入预检（入站链路上限 + 握手失败冷却）——超限即断（对端快速失败语义）
        var remoteEp = client.Client.RemoteEndPoint;
        if (!TryAcceptInbound(remoteEp))
        {
            client.Close();
            return;
        }
        var link = PeerLink.Accept(this, client);
        StartWriteLoop(link, client);   // ★ 写循环先于握手起（握手帧走写队列——队列无人消费 = 死锁）
        try
        {
            if (!await link.HandshakeAsync(ct))
            {
                OnInboundHandshakeFailed(remoteEp);   // 失败退避——同源冷却
                Audit("handshake_failed", remoteEp?.ToString() ?? "?", "入站握手失败");
                return;   // 准入/归属/版本/安全违规在握手期拒绝（Error + 断连）
            }
            // ★ 二期-H4：授权钩子——链路准入（身份已知后判定；拒绝 = 审计 + 断连）
            if (_authorizer is { } authz && !authz.AuthorizeLink(link.Remote))
            {
                Audit("authz_link_denied", link.Remote.ToString(), "授权钩子拒绝链路");
                link.Close();
                return;
            }
            RegisterLink(link.Remote, link);
            await link.RunReceiveAsync(ct);
        }
        finally
        {
            await link.DisposeAsync();
        }
    }

    /// <summary>入站准入预检（二期-D4）：入站链路上限 + 同源握手失败冷却。false = 拒绝（调用方断连）。</summary>
    private bool TryAcceptInbound(System.Net.EndPoint? remote)
    {
        if (Options.MaxInboundLinks > 0 && _links.Count >= Options.MaxInboundLinks)
        {
            Logger?.LogDebug("入站连接拒绝（链路数上限 {Cap}）", Options.MaxInboundLinks);
            Audit("capacity_rejected", remote?.ToString() ?? "?", $"max={Options.MaxInboundLinks}");
            return false;
        }
        if (Options.HandshakeFailCooldown > TimeSpan.Zero && remote is IPEndPoint ip)
        {
            var key = ip.Address;   // 同源 IP 维度（端口为临时值——限速/退避按源收敛）
            var now = Environment.TickCount64;
            var until = _handshakeCooldown.TryGetValue(key, out var v) ? Volatile.Read(ref v) : 0;
            if (now < until)
            {
                Interlocked.Increment(ref _cooldownRejects);
                Logger?.LogDebug("入站连接拒绝（握手失败冷却）：{Remote}", remote);
                return false;
            }
        }
        return true;
    }

    /// <summary>入站握手失败记录（同源 IP 冷却窗起算——失败退避/限速面；握手成功自然清除无痕）。</summary>
    private void OnInboundHandshakeFailed(System.Net.EndPoint? remote)
    {
        if (Options.HandshakeFailCooldown <= TimeSpan.Zero) return;
        if (remote is IPEndPoint ip)
            _handshakeCooldown[ip.Address] = Environment.TickCount64 + (long)Options.HandshakeFailCooldown.TotalMilliseconds;
    }

    /// <summary>冷却拒绝计数（诊断——InternalsVisibleTo 测试面）。</summary>
    internal long CooldownRejects => Interlocked.Read(ref _cooldownRejects);

    /// <summary>运行时可变旋钮读面（PeerLink 等内部组件用点重读——D6）。</summary>
    internal RuntimeTunables Tunables => _tunables;

    /// <summary>
    /// 运行时配置热更新（二期-D6 NETGAP-011——重连/保活/超时旋钮子集）：在克隆上应用
    /// configure 并整体校验（非法 = 整体拒绝，不部分生效）；各旋钮独立生效（无跨旋钮一致性
    /// 承诺）。装配不可变面（地址表/安全档/容量边界）不在本面。
    /// </summary>
    /// <param name="configure">旋钮改写（在克隆上执行）。</param>
    /// <returns>更新后的旋钮快照。</returns>
    public RuntimeTunables UpdateRuntimeTunables(Action<RuntimeTunables> configure)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(configure);
        lock (_tunablesGate)
        {
            var candidate = _tunables.Clone();
            configure(candidate);
            candidate.Validate();
            candidate.CopyTo(_tunables);
            return _tunables.Clone();   // 快照——外部不可变更内部状态
        }
    }

    /// <summary>旋钮快照读面（诊断/状态导出）。</summary>
    /// <returns>当前运行时旋钮快照（克隆——外部改动不影响内部状态）。</returns>
    public RuntimeTunables GetRuntimeTunables()
    {
        lock (_tunablesGate) return _tunables.Clone();
    }

    /// <summary>
    /// 地址制直连（spec-12 §4.1/§4.3——有地址者拨号，NodeId 大小无关）：拨号 → 三步握手 →
    /// 链路登记（对端身份从握手得知并返回——此前未知）。
    /// <para>★ 连接准入 = 合法握手（帧/版本/ClusterTag/安全形态）——对端无需在地址表；
    ///   链路仅承载该连接上的定向通信（不参与成员制自动拨号重连——断开由调用方观察
    ///   <see cref="PeerGone"/> 自决重拨，§4.1 路由语义）。</para>
    /// <para>失败（拒绝/超时/断连）抛 <see cref="NetIOException"/>（携带对端 Error 细节）——
    /// 调用方显式请求的建立语义，不同于数据报的尽力送达。</para>
    /// </summary>
    /// <param name="endpoint">对端端点（IPEndPoint 或 DnsEndPoint——域名直连）。</param>
    /// <param name="ct">取消令牌（覆盖连接与握手全程）。</param>
    /// <returns>对端节点 ID（握手得知）。</returns>
    /// <exception cref="NetIOException">连接/握手失败（含对端拒绝——错 ClusterTag/安全档/版本）。</exception>
    public async ValueTask<NodeId> ConnectAsync(EndPoint endpoint, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(endpoint);

        var client = await ConnectTcpAsync(endpoint, _tunables.HandshakeTimeout, ct).ConfigureAwait(false);
        var link = PeerLink.DialAddressed(this, client);
        StartWriteLoop(link, client);   // ★ 写循环先于握手起（同 AcceptHandshakeAsync 注释）
        try
        {
            if (!await link.HandshakeAsync(ct).ConfigureAwait(false))
                throw new NetIOException($"地址制直连握手失败：{endpoint}（{link.HandshakeFailureReason}）。");
            RegisterLink(link.Remote, link);   // 直连链路进链路表——定向发送可用；不触发拨号循环（无自动重连）
            StartLoopThread($"tcp-receive-{link.Remote}", link.RunReceiveAsync);
            return link.Remote;
        }
        catch
        {
            await link.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary><see cref="IRaftDialer"/> 显式桥（二期-C1 §4.4——组通道拨号委托点）。</summary>
    ValueTask<NodeId> IRaftDialer.DialAsync(IPEndPoint endpoint, CancellationToken ct) => ConnectAsync(endpoint, ct);

    /// <summary>TCP 连接建立（Socket 层 <see cref="EndPoint"/> 重载——IPEndPoint/DnsEndPoint 同路径；
    /// 超时 = 握手超时同源，连接阶段即握手语义的一部分；Socket 层失败统一包 NetIOException——
    /// 目标未连快速失败契约与 InProcess"目标未注册"同构（§5.2）。</summary>
    private static async Task<TcpClient> ConnectTcpAsync(EndPoint endpoint, TimeSpan timeout, CancellationToken ct)
    {
        var client = new TcpClient();   // 双栈（IPv6 dual-mode——IPv4 映射可达）
        bool connected = false;
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(timeout);
            try
            {
                await client.Client.ConnectAsync(endpoint, connectCts.Token).ConfigureAwait(false);
            }
            catch (SocketException ex)
            {
                throw new NetIOException($"连接失败：{endpoint}（{ex.SocketErrorCode}）。", ex);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                // 连接超时（握手超时同源——连接阶段即握手语义的一部分）：与 Socket 拒绝同为
                // "未连快速失败"——统一包 NetIOException（调用方不区分被拒/超时；外部取消照传）
                throw new NetIOException($"连接超时：{endpoint}（{timeout.TotalMilliseconds:F0}ms）。", ex);
            }
            connected = true;
            client.NoDelay = true;
            return client;
        }
        finally
        {
            if (!connected) client.Dispose();
        }
    }

    private async Task DialLoopAsync(NodeId peer, CancellationToken ct)
    {
        var delay = _tunables.ReconnectInitialDelay;
        while (!ct.IsCancellationRequested)
        {
            // ★ 二期-C2：端点动态重读（AddPeer 换端点下一拨号轮生效；表移除 = 循环退出）
            if (!_peers.TryGetValue(peer, out var entry)) break;
            var endpoint = entry.Endpoint;
            if (_faultsImpl.IsPartitioned(Self, peer))
            {
                if (!await BackoffDelayAsync(delay, ct)) break;
                continue;
            }

            bool established = false;
            PeerLink? link = null;
            try
            {
                var client = await ConnectTcpAsync(endpoint, _tunables.HandshakeTimeout, ct);
                link = PeerLink.DialKnown(this, peer, client);
                StartWriteLoop(link, client);   // ★ 写循环先于握手起（同 AcceptHandshakeAsync 注释）
                if (!await link.HandshakeAsync(ct))
                    throw new NetIOException($"握手失败：{peer}");

                if (_everConnected.TryAdd(peer, 0)) { /* 首连不算重连 */ }
                else NetView?.OnReconnect(peer.ToString());
                RegisterLink(peer, link);   // 事件触发前指标已可见（PeerConnected 消费方读数无竞态）
                established = true;
                await link.RunReceiveAsync(ct);
            }
            catch (Exception ex)
            {
                // ★ 半建链一并收尾（含 OCE 打断在建链形态——Close 幂等）：写循环线程已随
                //   StartWriteLoop 起线，漏收尾 = 链路未闭 + 专用线程永久泄漏（#470）
                if (link is not null) await link.DisposeAsync();
                if (ex is not OperationCanceledException)
                    Logger?.LogDebug("拨号循环（{Peer}）：{Message}", peer, ex.Message);
            }

            delay = established ? _tunables.ReconnectInitialDelay : // 建立过即复位——退避只惩罚连续失败
                TimeSpan.FromTicks(Math.Min((long)(delay.Ticks * _tunables.ReconnectBackoffFactor), _tunables.ReconnectMaxDelay.Ticks));
            if (!await BackoffDelayAsync(delay, ct)) break;
        }
        _dialLoops.TryRemove(peer, out var done);
        done?.Dispose();   // 循环退出注销（AddPeer 复入可重起）
    }

    // ══ 动态成员（二期-C2 §5.3——IPeerRegistry + 入站黑名单）══

    /// <summary>按归属规则自起 per-peer 拨号循环（幂等——已有循环不重复；仅 self 为较小 ID 方时调用）。</summary>
    private void StartPeerDialLoop(NodeId peerId)
    {
        if (_dialLoops.ContainsKey(peerId)) return;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        if (!_dialLoops.TryAdd(peerId, cts))
        {
            cts.Dispose();
            return;
        }
        StartLoopThread($"tcp-dial-{peerId}", token => DialLoopAsync(peerId, token), cts.Token);
    }

    /// <inheritdoc/>
    public void AddPeer(NodeId id, IPEndPoint endpoint)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(endpoint);
        if (id == Self) throw new ArgumentException("不能将本端加入对端表。", nameof(id));
        _inboundDeny.TryRemove(id, out _);   // 复入解禁（裁定②）
        _peers.GetOrAdd(id, _ => new PeerEntry()).Endpoint = endpoint;   // 同 ID 换端点 = 更新（下一拨号轮生效）
        if (Self.CompareTo(id) < 0)
            StartPeerDialLoop(id);   // 较小 ID 方才拨号——自起新循环（幂等）
    }

    /// <inheritdoc/>
    public void RemovePeer(NodeId id, bool closeLink = true)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _peers.TryRemove(id, out _);   // 在表则出表
        // ★ 黑名单无论在表与否都生效（被动接受方从未 AddPeer 对端——表空但链路在）；
        //   运行时易失（重启清零，长期治理归配置/组协议），AddPeer 复入解禁
        _inboundDeny[id] = 1;
        if (_dialLoops.TryRemove(id, out var cts))
        {
            cts.Cancel();   // 停拨号循环（循环线程经 ct 自然退出）
            cts.Dispose();
        }
        if (closeLink && _links.TryRemove(id, out var link))
            link.Close();   // 关闭既有链路（在途请求按既有语义失败——调用方重试）
    }

    /// <summary>对端表快照（IPeerRegistry）。</summary>
    public IReadOnlyDictionary<NodeId, IPEndPoint> Peers
        => _peers.ToDictionary(kv => kv.Key, kv => kv.Value.Endpoint);

    /// <summary>入站黑名单判定（PeerLink 监听侧握手检查点——被移除成员重拨入拒）。</summary>
    internal bool IsInboundDenied(NodeId id) => _inboundDeny.ContainsKey(id);

    /// <summary>退避等待。false = 已取消（循环退出）。</summary>
    private async Task<bool> BackoffDelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>写循环起线（每链专用线程——握手帧走写队列，写循环必须先于握手运行；名字带对端端点——
    /// 握手前身份未知，Established 后诊断以 Remote 为准）。</summary>
    private void StartWriteLoop(PeerLink link, TcpClient client)
        => StartLoopThread($"tcp-write-{client.Client.RemoteEndPoint}", link.RunWriteLoopAsync);

    private void RegisterLink(NodeId peer, PeerLink link)
    {
        if (_links.TryGetValue(peer, out var stale))
        {
            _links.TryRemove(new KeyValuePair<NodeId, PeerLink>(peer, stale));
            stale.Close();    // 旧链路让位（触发其 PeerGone——随后本链路 PeerConnected）
        }
        _links[peer] = link;
        PeerConnected?.Invoke(peer);
    }

    internal void OnLinkClosed(NodeId remote, PeerLink link, bool established)
    {
        _links.TryRemove(new KeyValuePair<NodeId, PeerLink>(remote, link));
        _streams.OnLinkClosed(link);   // 该链路全部会话 Reset（不跨重连恢复，§5.3；多对端互不相干）
        if (established) PeerGone?.Invoke(remote);
    }

    // ══ 协议域注册与数据报 ══

    /// <summary>
    /// 协议域注册·公开口（spec-12 §3.5 注册面分流）——只放行注册区 0x60-0xAF
    /// （<see cref="ProtocolIds.IsUserRegistrable"/>）：使用方在此自管唯一性；
    /// 内部区/隔离区/保留一律 fail-fast（结构上使用方无法占用内部号）。
    /// 注册与分发全介质单源（<see cref="DatagramDispatcher"/>——同构门注册面保障）。
    /// </summary>
    /// <param name="protocolId">协议域 ID（注册区 0x60-0xAF）。</param>
    /// <param name="handler">入站数据报处理器。</param>
    /// <param name="bearer">发送承载声明（默认 TCP 帧流；UDP 见 <see cref="DatagramBearer.Udp"/>）。</param>
    public void RegisterProtocol(byte protocolId, IDatagramHandler handler, DatagramBearer bearer = DatagramBearer.Tcp)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _dispatcher.RegisterUser(protocolId, handler, bearer);
    }

    /// <summary>
    /// 协议域注册·内部口——只放行内部核心区 0x00-0x4F（<see cref="ProtocolIds.IsCore"/>）：
    /// 机制面（Raft/HyParView/SwarmSync 等）专用，程序集内可见。
    /// </summary>
    /// <param name="protocolId">协议域 ID（内部核心区）。</param>
    /// <param name="handler">入站数据报处理器。</param>
    /// <param name="bearer">发送承载声明。</param>
    internal void RegisterCoreProtocol(byte protocolId, IDatagramHandler handler, DatagramBearer bearer = DatagramBearer.Tcp)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _dispatcher.RegisterCore(protocolId, handler, bearer);
    }

    // ── ICoreProtocolPort（显式实现委托 internal 方法——隐式实现要求 public，内部口不出程序集）──

    void ICoreProtocolPort.RegisterCoreProtocol(byte protocolId, IDatagramHandler handler, DatagramBearer bearer)
        => RegisterCoreProtocol(protocolId, handler, bearer);

    void ICoreProtocolPort.RegisterCoreRequestHandler(byte protocolId, IRequestHandler handler)
        => RegisterCoreRequestHandler(protocolId, handler);

    void ICoreProtocolPort.RegisterCoreStreamAcceptor(byte protocolId, IStreamAcceptor acceptor)
        => RegisterCoreStreamAcceptor(protocolId, acceptor);

    // ══ 请求回调（spec-12 §5.2）══

    /// <summary>
    /// 请求回调 handler 注册·公开口（§5.2/§3.5——注册区 0x60-0xAF，与数据报同一分流规则）。
    /// 同 ID 重复注册抛；数据报与请求回调为独立 handler 表（同协议域可两形态并用）。
    /// </summary>
    /// <param name="protocolId">协议域 ID（注册区）。</param>
    /// <param name="handler">入站请求处理器。</param>
    public void RegisterRequestHandler(byte protocolId, IRequestHandler handler)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _requests.RegisterUserHandler(protocolId, handler);
    }

    /// <summary>请求回调 handler 注册·内部口（机制面专用——核心区）。</summary>
    /// <param name="protocolId">协议域 ID（内部核心区）。</param>
    /// <param name="handler">入站请求处理器。</param>
    internal void RegisterCoreRequestHandler(byte protocolId, IRequestHandler handler)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _requests.RegisterCoreHandler(protocolId, handler);
    }

    /// <summary>
    /// 请求回调发送（§5.2——at-most-once 缺省）：目标未连 = 抛 <see cref="NetIOException"/>
    /// （调用方有应答期待）；注入丢弃/无应答 = 超时抛 <see cref="TimeoutException"/>；
    /// 取消传播；应答到达返回载荷。
    /// </summary>
    /// <param name="target">目标节点。</param>
    /// <param name="protocolId">协议域 ID。</param>
    /// <param name="payload">请求载荷。</param>
    /// <param name="options">per-call 选项（null = 传输缺省超时）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>应答载荷。</returns>
    public ValueTask<byte[]> SendRequestAsync(NodeId target, byte protocolId, ReadOnlyMemory<byte> payload,
        RequestOptions? options = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (payload.Length > FrameCodec.MaxPayloadLength - RequestCodec.PrefixSize)
            throw new ArgumentOutOfRangeException(nameof(payload),
                $"请求载荷 {payload.Length} 超上限（帧上限扣除 CorrId 前缀——大块走流式通道，spec-12 §3.1/§5.3）。");

        if (!_links.TryGetValue(target, out var link) || !link.IsEstablished)
            throw new NetIOException($"目标未连接：{target}（请求回调不静默——调用方有应答期待）。");

        // ★ 二期-I4：trace 上下文（显式优先，否则 tracer 可用即自动捕获 Current）——
        //   上线与否由链路协商特性位门控（混版对端零影响）
        var traceContext = options?.TraceContext ?? Hub?.CaptureTraceContext();
        // ★ 直通 broker 等待态（池化源背书——本层零箱零 Task；校验在调用点同步抛）
        return _requests.SendAsync(
            (corrId, zct) => link.SendRequestFrameAsync(protocolId, corrId, payload, zct, traceContext),
            options?.Timeout ?? _tunables.RequestTimeout, options?.Retry, ct, protocolId);
    }

    /// <summary>应答到达（PeerLink 读循环——完成 pending；迟到应答忽略）。</summary>
    internal void OnResponseArrived(ulong corrId, ReadOnlyMemory<byte> payload) => _requests.OnResponse(corrId, payload);

    /// <summary>域级背压策略声明（二期-D5——产品按域声明；缺省 FailFast）。</summary>
    /// <param name="protocolId">协议域 ID。</param>
    /// <param name="policy">域背压策略（重复设置 = 覆盖；未声明域 = FailFast 缺省）。</param>
    public void SetBackpressurePolicy(byte protocolId, BackpressurePolicy policy)
        => _requests.SetBackpressurePolicy(protocolId, policy);

    /// <summary>请求域限流（二期-E4——域级聚合，perSecond 令牌/秒 + burst 突发）。</summary>
    /// <param name="domain">协议域 ID。</param>
    /// <param name="perSecond">令牌回填速率（令牌/秒，须 &gt; 0）。</param>
    /// <param name="burst">桶容量（突发额度，须 ≥ 1；重复设置 = 重置桶）。</param>
    public void SetRequestRateLimit(byte domain, double perSecond, int burst)
        => _qos.SetDomainLimit(domain, perSecond, burst);

    /// <summary>请求来源级限流（二期-E4——租户=来源节点，每来源独立桶）。</summary>
    /// <param name="domain">协议域 ID。</param>
    /// <param name="source">来源节点 ID（租户语义）。</param>
    /// <param name="perSecond">令牌回填速率（令牌/秒，须 &gt; 0）。</param>
    /// <param name="burst">桶容量（突发额度，须 ≥ 1；重复设置 = 重置桶）。</param>
    public void SetRequestSourceRateLimit(byte domain, NodeId source, double perSecond, int burst)
        => _qos.SetSourceLimit(domain, source, perSecond, burst);

    /// <summary>入站数据报域限流（二期-E4——尽力语义下超限静默丢弃 + 计数）。</summary>
    /// <param name="domain">协议域 ID。</param>
    /// <param name="perSecond">令牌回填速率（令牌/秒，须 &gt; 0）。</param>
    /// <param name="burst">桶容量（突发额度，须 ≥ 1；重复设置 = 重置桶）。</param>
    public void SetDatagramRateLimit(byte domain, double perSecond, int burst)
        => _qos.SetDatagramDomainLimit(domain, perSecond, burst);

    /// <summary>入站请求分发（PeerLink 读循环——回程上下文由链路构造）。</summary>
    internal void DispatchRequest(NodeId from, byte protocolId, ulong corrId, ReadOnlyMemory<byte> payload, IReplyContext reply, byte[]? traceWireContext = null)
    {
        // ★ 二期-H4：域级请求准入（拒绝 = 审计 + 无应答丢弃——对端超时自愈）
        if (_authorizer is { } authz && !authz.AuthorizeRequest(from, protocolId))
        {
            Audit("authz_request_denied", from.ToString(), $"domain=0x{protocolId:X2}");
            return;
        }
        // ★ 二期-E4：QoS 域级/来源级限流（超限 = 无应答丢弃——对端超时重试语义）
        if (!_qos.TryAdmitRequest(protocolId, from))
        {
            NetView?.OnDatagramDropped(protocolId, "qos");
            return;
        }
        _requests.DispatchRequest(from, protocolId, corrId, payload, reply, traceWireContext);
    }

    // ══ 流式会话（spec-12 §5.3）══

    /// <summary>
    /// 流接受面注册·公开口（§5.3/§3.5——注册区 0x60-0xAF）；未注册 acceptor 的协议域 = 拒绝开流。
    /// </summary>
    /// <param name="protocolId">协议域 ID（注册区）。</param>
    /// <param name="acceptor">入站会话接受面。</param>
    public void RegisterStreamAcceptor(byte protocolId, IStreamAcceptor acceptor)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _streams.RegisterUserAcceptor(protocolId, acceptor);
    }

    /// <summary>流接受面注册·内部口（机制面专用——核心区）。</summary>
    /// <param name="protocolId">协议域 ID（内部核心区）。</param>
    /// <param name="acceptor">入站会话接受面。</param>
    internal void RegisterCoreStreamAcceptor(byte protocolId, IStreamAcceptor acceptor)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _streams.RegisterCoreAcceptor(protocolId, acceptor);
    }

    /// <summary>
    /// 打开流式会话（§5.3——StreamOpen → 等 Accept；目标未连抛 <see cref="NetIOException"/>；
    /// 对端不接受/链路断 = <see cref="NetIOException"/>；等待超时 = <see cref="TimeoutException"/>）。
    /// 会话不跨重连恢复（链路断 = Reset，上层重开）。
    /// </summary>
    /// <param name="target">目标节点。</param>
    /// <param name="protocolId">协议域 ID。</param>
    /// <param name="ct">取消令牌。</param>
    /// <param name="openPayload">开流载荷（随 Open 帧透传给对端 acceptor；组路由场景 = host 剥前缀后的内层载荷——二期-C1）。</param>
    /// <returns>会话。</returns>
    public async ValueTask<IWireStream> OpenStreamAsync(NodeId target, byte protocolId, ReadOnlyMemory<byte> openPayload = default, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_links.TryGetValue(target, out var link) || !link.IsEstablished)
            throw new NetIOException($"目标未连接：{target}（无法开流）。");
        return await _streams.OpenAsync(link, protocolId, openPayload, _tunables.RequestTimeout, ct).ConfigureAwait(false);
    }

    internal ValueTask OnStreamOpen(PeerLink link, byte remoteSession, byte protocolId, ReadOnlyMemory<byte> openPayload, CancellationToken ct)
        => _streams.OnOpenAsync(link, remoteSession, protocolId, openPayload, ct);

    internal void OnStreamAccept(PeerLink link, byte peerLocalSession, byte protocolId, byte echoedLocal)
        => _streams.OnAccept(link, peerLocalSession, protocolId, echoedLocal);

    internal ValueTask OnStreamData(PeerLink link, byte remoteSession, ReadOnlyMemory<byte> payload)
        => _streams.OnDataAsync(link, remoteSession, payload);

    internal void OnStreamEnd(PeerLink link, byte remoteSession) => _streams.OnEnd(link, remoteSession);

    internal void OnStreamReset(PeerLink link, byte remoteSession) => _streams.OnReset(link, remoteSession);

    internal void OnStreamAck(PeerLink link, byte remoteSession) => _streams.OnAck(link, remoteSession);

    /// <summary>
    /// 数据报发送（尽力送达——spec-12 §5.1）。
    /// <para>★ 承载路由：bearer 声明 UDP 且对端端点已通告且单报预算内 →
    ///   UDP 数据报；否则 TCP 帧流承载（含 UDP bearer 的端点未知/超预算/本端未开 UDP 回落）。</para>
    /// <para>对端未连接/注入丢弃 = 静默丢弃 + 计数；介质写失败 = 吞掉（UDP）/ 关链路（TCP）；
    ///   仅参数错误与取消外泄（TCP 背压 = 本端 socket 写 await）。</para>
    /// </summary>
    /// <param name="target">目标节点。</param>
    /// <param name="protocolId">协议域 ID。</param>
    /// <param name="payload">载荷（≤帧协议上限——大块走流式通道）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>完成时数据报已按承载路由发出（尽力送达——对端未连接/注入丢弃 = 静默丢弃，不抛）。</returns>
    public async ValueTask SendDatagramAsync(NodeId target, byte protocolId, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (payload.Length > FrameCodec.MaxPayloadLength)
            throw new ArgumentOutOfRangeException(nameof(payload),
                $"载荷 {payload.Length} 超上限 {FrameCodec.MaxPayloadLength}（大块走流式通道——spec-12 §3.1）。");

        if (_udp is not null
            && payload.Length <= Options.UdpMaxDatagramBytes
            && _dispatcher.TryGetBearer(protocolId, out var bearer) && bearer == DatagramBearer.Udp
            && _udpEndpoints.TryGetValue(target, out var udpEndpoint))
        {
            await SendUdpAsync(target, udpEndpoint, protocolId, payload, ct).ConfigureAwait(false);
            return;
        }

        if (!_links.TryGetValue(target, out var link) || !link.IsEstablished)
        {
            NetView?.OnDatagramDropped(protocolId, "no_link");
            return;
        }
        await link.SendDatagramAsync(protocolId, payload, ct).ConfigureAwait(false);
    }

    /// <summary>UDP 数据报发送（同一帧格式单报承载——注入面与 TCP 同一实例语义）。</summary>
    private async Task SendUdpAsync(NodeId target, IPEndPoint endpoint, byte protocolId, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (!await _faultsImpl.ApplyDeliveryDelay(Self, target, ct).ConfigureAwait(false)) return;   // 分区/丢包/延迟/乱序
        var udp = _udp!;
        // ★ UDP 报文认证（spec-12 §3.4——KeyPair 档）：报文 = 帧 + 32B 尾标签（无会话键 = 认证不可做，回落 TCP）
        byte[]? udpKey = null;
        if (Security.Mode == SecurityMode.KeyPair && !_udpAuthKeys.TryGetValue(target, out udpKey))
            return;   // 无键（会话未建立/已断）——回落由调用方 SendDatagramAsync 的 TCP 路径承担（本方法被选中时即已通告——无键直接静默比错发安全）
        int tagSize = udpKey is null ? 0 : 32;
        int frameLength = FrameCodec.HeaderSize + payload.Length + tagSize;
        var buffer = ArrayPool<byte>.Shared.Rent(frameLength);
        try
        {
            int written = FrameCodec.Encode(FrameKind.Datagram, ChannelIds.Datagram, protocolId, payload.Span, buffer.AsSpan(0, frameLength));
            if (udpKey is not null)
            {
                // ★ E8 边界裁定（二期）：UDP 数据报 = 认证-only（32B HMAC 尾标签——防伪造/篡改/
                //   来源冒充），不做逐报加密——加密需 DTLS/MsQuic 数据报（.NET 8 均不可用/违背零
                //   外部依赖铁律）；机密性需求走 TCP/流式通道（mTLS 或 KeyPair AEAD）。
                SecureSession.ComputeUdpTag(udpKey, buffer.AsSpan(0, written)).CopyTo(buffer.AsSpan(written));
                written += 32;
            }
            await udp.Client.SendToAsync(buffer.AsMemory(0, written), SocketFlags.None, endpoint, ct).ConfigureAwait(false);
            if (NetView is { } net)
            {
                if (net.ShouldSampleFrame()) net.OnFrameSent(protocolId);
                net.OnUdpDatagramSent();
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            NetView?.OnUdpSendFailure(target.ToString());
            Logger?.LogDebug("UDP 数据报发送失败（尽力送达——静默）：{Target} {Message}", target, ex.Message);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>UDP 接收循环（§4.5——整帧数据报一步解码；单循环独占接收缓冲零分配复用）。</summary>
    private async Task UdpReceiveLoopAsync(CancellationToken ct)
    {
        var udp = _udp!;
        var buffer = ArrayPool<byte>.Shared.Rent(UdpReceiveBufferSize);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                SocketReceiveFromResult result;
                try
                {
                    result = await udp.Client.ReceiveFromAsync(buffer.AsMemory(), SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), ct);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException ex)
                {
                    Logger?.LogDebug("UDP 接收异常（继续）：{Message}", ex.Message);
                    continue;
                }

                if (!FrameCodec.TryDecode(buffer.AsSpan(0, result.ReceivedBytes), out var header, out _))
                {
                    NetView?.OnDatagramDropped(0, "udp_malformed");   // 坏帧/截断——数据报介质无连接可断，丢弃 + 计数
                    continue;
                }
                if (header.Kind != FrameKind.Datagram)
                {
                    NetView?.OnDatagramDropped(header.ProtocolId, "udp_malformed");   // UDP 只承载数据报帧（管理帧走 TCP）
                    continue;
                }
                if (result.RemoteEndPoint is not IPEndPoint remote || !_udpReverse.TryGetValue(remote, out var from))
                {
                    NetView?.OnDatagramDropped(header.ProtocolId, "udp_unknown_source");   // 源准入 = 握手通告端点映射
                    continue;
                }
                // ★ UDP 报文认证（KeyPair 档）：尾 32B 标签校验（帧字节全入域；无键/验败 = 丢弃计数）
                if (Security.Mode == SecurityMode.KeyPair)
                {
                    var frameBytes = result.ReceivedBytes - 32;
                    if (frameBytes <= 0
                        || !_udpAuthKeys.TryGetValue(from, out var authKey)
                        || !SecureSession.VerifyUdpTag(authKey, buffer.AsSpan(0, frameBytes), buffer.AsSpan(frameBytes, 32)))
                    {
                        NetView?.OnDatagramDropped(header.ProtocolId, "udp_auth_failed");
                        continue;
                    }
                }

                if (NetView is { } net)
                {
                    if (net.ShouldSampleFrame()) net.OnFrameReceived(header.ProtocolId);
                    net.OnUdpDatagramReceived();
                }
                DispatchDatagram(from, header.ProtocolId, buffer.AsMemory(FrameCodec.HeaderSize, (int)header.PayloadLength));
                // 分发为同步回调（快进快出契约）——返回后缓冲继续复用安全
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>注册对端 UDP 认证键（KeyPair 档 PeerLink 会话建立——TCP 会话确认键派生；
    /// 无键 + 本端 KeyPair 档 = UDP 承载回落 TCP（认证必做）；断链清理）。</summary>
    internal void RegisterUdpAuthKey(NodeId peer, byte[] udpKey) => _udpAuthKeys[peer] = udpKey;

    /// <summary>清理对端 UDP 认证键（链路断开——防旧键验新连接报文）。</summary>
    internal void UnregisterUdpAuthKey(NodeId peer) => _udpAuthKeys.TryRemove(peer, out _);

    /// <summary>登记对端 UDP 端点（Negotiate 通告到达——TCP 握手认证后身份可信；重连换端口覆盖旧映射）。</summary>
    internal void StoreUdpEndpoint(NodeId peer, IPEndPoint endpoint)
    {
        if (_udpEndpoints.TryGetValue(peer, out var old) && !old.Equals(endpoint))
            _udpReverse.TryRemove(old, out _);
        _udpEndpoints[peer] = endpoint;
        _udpReverse[endpoint] = peer;
    }

    /// <summary>对端 UDP 端点是否已通告（测试等待面）。</summary>
    internal bool HasUdpEndpoint(NodeId peer) => _udpEndpoints.ContainsKey(peer);

    /// <summary>构建本端 UDP 通告端点（通配绑定以 TCP 连接本端地址替代——port 取实际绑定值）。</summary>
    internal IPEndPoint? BuildUdpAnnounce(IPEndPoint tcpLocal)
    {
        if (_udpLocalEndPoint is not { } udpLocal) return null;
        if (udpLocal.Address.Equals(IPAddress.Any) || udpLocal.Address.Equals(IPAddress.IPv6Any))
            return new IPEndPoint(tcpLocal.Address, udpLocal.Port);
        return udpLocal;
    }

    /// <summary>入站数据报分发（Channels/ 单源组件——异常隔离/慢回调/未知协议钩子在介质侧接 metrics）。</summary>
    internal void DispatchDatagram(NodeId from, byte protocolId, ReadOnlyMemory<byte> payload)
    {
        // ★ 二期-E4：数据报域限流——尽力语义下超限静默丢弃 + 计数
        if (!_qos.TryAdmitDatagram(protocolId, from))
        {
            NetView?.OnDatagramDropped(protocolId, "qos");
            return;
        }
        _dispatcher.Dispatch(from, protocolId, payload);
    }

    /// <summary>成员路由判定（§4.3——仅判定"是否地址表成员"：驱动成员制拨号循环与归属违规检查；连接准入与此无关）。</summary>
    internal bool IsKnownPeer(NodeId peer) => peer != Self && _peers.ContainsKey(peer);   // 二期-C2：动态表

    /// <summary>目标链路已建立（join 引导等消费方在地址制拨号前探测——避免重复握手替换既有链路）。</summary>
    /// <param name="peer">目标节点。</param>
    /// <returns>true = 链路已建立（定向发送可用）。</returns>
    public bool IsConnected(NodeId peer) => _links.TryGetValue(peer, out var link) && link.IsEstablished;

    /// <inheritdoc/>
    /// <returns>完成时监听器/UDP/全部链路/循环线程均已关闭（幂等——重复调用立即完成）。</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _cts.CancelAsync();
        try { _listener?.Stop(); } catch { /* 监听未启动/已停——幂等 */ }
        try { _udp?.Dispose(); } catch { /* UDP 未启动/已释放——幂等 */ }
        foreach (var link in _links.Values) link.Close();
        // ★ 循环线程有界等退（Replication StopAllLanesAndWaitAsync 同款——总预算 2s；
        //   超时残留由进程收尾，listener/UDP/链路已关闭 = 循环退出条件已就位）
        var deadline = Environment.TickCount64 + 2000;
        foreach (var loopThread in _loopThreads.Keys)
        {
            var remain = (int)Math.Max(0, deadline - Environment.TickCount64);
            if (remain == 0) break;
            try { await loopThread.WaitAsync(TimeSpan.FromMilliseconds(remain)).ConfigureAwait(false); }
            catch (TimeoutException) { }
            catch (Exception)
            {
                // ignored
            } // 循环线程不外泄异常（泵外逃逸已日志兜底）——Dispose 不被循环失败阻断
        }
        _cts.Dispose();
    }
}
