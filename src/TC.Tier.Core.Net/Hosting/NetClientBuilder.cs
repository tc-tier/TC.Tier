using System.Net;
using System.Security.Cryptography.X509Certificates;
using TC.Tier.Core.Logging;
using TC.Tier.Core.Observability;
using TC.Tier.Core.Net.Channels;
using TC.Tier.Core.Net.Ports;
using TC.Tier.Core.Net.Security;
using TC.Tier.Core.Net.Transport;
using TC.Tier.Core.Net.Transport.Tcp;

namespace TC.Tier.Core.Net.Hosting;

/// <summary>
/// 地址制直连装配器（spec-12 §11——客户端预设：缺省不监听，可开监听=对称节点；客户端/服务器
/// 无固定模式——装配预设可覆写，§4.2）。
/// <para>★ Connect 后对端身份握手才得知（§4.1 地址制——有地址者拨号；目标未连 =
///   <see cref="NetIOException"/> 快速失败）；<see cref="NodeEndpoint.RemoteId"/> 携带。</para>
/// <para>★ 注册面：协议域/请求回调/流接受面启动前注册（对端推流/回调到达即服务）。
///   身份/安全档形态同 <see cref="ClusterBuilder"/>。</para>
/// </summary>
public sealed class NetClientBuilder
{
    private readonly NodeId? _nodeId;
    private readonly IIdentitySource? _identitySource;
    private NodeIdentity? _identity;
    private uint _clusterTag = TransportOptions.DefaultClusterTag;
    private IPEndPoint? _connect;
    private IPEndPoint? _listen;
    private IPEndPoint? _udpListen;
    private readonly Dictionary<byte, (IDatagramHandler Handler, DatagramBearer Bearer)> _datagramRegs = [];
    private readonly Dictionary<byte, IRequestHandler> _requestRegs = [];
    private readonly Dictionary<byte, IStreamAcceptor> _streamRegs = [];
    private ILogger? _logger;
    private ObservabilityHub? _hub;
    private SecurityOptions? _security;   // 安全档（§8——服务端 ClusterBuilder 同款装配面）
    private (TimeSpan Initial, TimeSpan Max)? _autoReconnect;   // 二期-D7：自动重连（null = 关闭）
    private TransportOptions? _transportBase;   // 传输选项整份注入（显式方法未触达的位生效）
    private readonly List<Func<TransportOptions, TransportOptions>> _transportPipes = [];   // 管道（链尾依序执行、裁决一切）
    private bool _listenSet;
    private bool _clusterTagSet;

    private NetClientBuilder(NodeId? nodeId, IIdentitySource? source)
    {
        _nodeId = nodeId;
        _identitySource = source;
    }

    /// <summary>创建（显式节点 ID——配置注入形态）。</summary>
    /// <param name="nodeId">本端节点 ID（不可为 <see cref="NodeId.Empty"/> 哨兵）。</param>
    /// <returns>装配器实例（供链式配置）。</returns>
    /// <exception cref="ArgumentException"><paramref name="nodeId"/> 为 Empty 哨兵。</exception>
    public static NetClientBuilder Create(NodeId nodeId)
    {
        if (nodeId == NodeId.Empty) throw new ArgumentException("节点 ID 不可为 Empty 哨兵。", nameof(nodeId));
        return new NetClientBuilder(nodeId, null);
    }

    /// <summary>创建（身份供给端口——首次生成保存、此后加载；NodeId 跨重启稳定）。</summary>
    /// <param name="source">身份供给端口（非空）。</param>
    /// <returns>装配器实例（供链式配置）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> 为 null。</exception>
    public static NetClientBuilder Create(IIdentitySource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new NetClientBuilder(null, source);
    }

    /// <summary>配置：直连目标（地址制——有地址者拨号；对端身份握手后才得知）。</summary>
    /// <param name="endpoint">对端 TCP 端点（非空）。</param>
    /// <returns>同一装配器实例（供链式配置）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="endpoint"/> 为 null。</exception>
    public NetClientBuilder Connect(IPEndPoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        _connect = endpoint;
        return this;
    }

    /// <summary>配置：集群归属标签（握手期校验——拨入 tag≠0 集群必须匹配，§3.3 错集群 fail-fast）。</summary>
    /// <param name="clusterTag">目标集群归属标签。</param>
    /// <returns>同一装配器实例（供链式配置）。</returns>
    public NetClientBuilder ClusterTag(uint clusterTag)
    {
        _clusterTag = clusterTag;
        _clusterTagSet = true;
        return this;
    }

    /// <summary>配置：UDP 数据报端点（§4.5——数据报/请求回调的 UDP 承载；绑 Any，通告时以 TCP 连接本端地址替代）。</summary>
    /// <param name="port">UDP 监听端口。</param>
    /// <returns>同一装配器实例（供链式配置）。</returns>
    public NetClientBuilder WithUdp(int port)
    {
        _udpListen = new IPEndPoint(IPAddress.Any, port);
        return this;
    }

    /// <summary>配置：UDP 数据报端点（显式地址形态）。</summary>
    /// <param name="endpoint">UDP 监听端点（显式地址）。</param>
    /// <returns>同一装配器实例（供链式配置）。</returns>
    public NetClientBuilder WithUdp(IPEndPoint endpoint)
    {
        _udpListen = endpoint;
        return this;
    }

    /// <summary>配置：传输选项整份注入（spec-12 §4.5 全旋钮可调口）。
    /// <para>优先级三段式（TierFs×options 合流同构）：显式方法 > 注入 > Default——
    /// <see cref="Listen(IPEndPoint)"/>, <see cref="ClusterTag"/>, <see cref="WithUdp(int)"/>
    /// 显式触达的位仍归方法裁决，其余旋钮以注入值为准。</para></summary>
    /// <param name="options">传输选项基线（非空）。</param>
    /// <returns>同一装配器实例（供链式配置）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> 为 null。</exception>
    public NetClientBuilder WithTransport(TransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _transportBase = options;
        return this;
    }

    /// <summary>配置：传输选项管道（StartAsync 链尾依序执行、裁决一切——全部旋钮的通用改写口，可多次调用）。</summary>
    /// <param name="configure">选项改写函数（非空；链尾依序执行，后执行者覆盖先执行者）。</param>
    /// <returns>同一装配器实例（供链式配置）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> 为 null。</exception>
    public NetClientBuilder WithTransport(Func<TransportOptions, TransportOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _transportPipes.Add(configure);
        return this;
    }

    /// <summary>配置：直连目标（host 支持 IP 或域名——§4.1 DnsEndPoint）。</summary>
    /// <param name="host">对端主机（IP 字面量或可解析域名；解析失败即抛）。</param>
    /// <param name="port">对端 TCP 端口。</param>
    /// <returns>同一装配器实例（供链式配置）。</returns>
    /// <exception cref="ArgumentException">host 为空或域名解析失败。</exception>
    public NetClientBuilder Connect(string host, int port)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);
        var address = IPAddress.TryParse(host, out var parsed)
            ? parsed
            : Dns.GetHostAddresses(host).FirstOrDefault()
                ?? throw new ArgumentException($"主机名解析失败：{host}", nameof(host));
        return Connect(new IPEndPoint(address, port));
    }

    /// <summary>配置：协议域数据报入站处理器（启动前注册——对端推送到达即服务）。</summary>
    /// <param name="protocolId">协议域 ID（同 ID 重复注册覆盖前者）。</param>
    /// <param name="handler">数据报入站处理器（非空）。</param>
    /// <param name="bearer">该域数据报的承载介质（默认 TCP）。</param>
    /// <returns>同一装配器实例（供链式配置）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/> 为 null。</exception>
    public NetClientBuilder RegisterProtocol(byte protocolId, IDatagramHandler handler, DatagramBearer bearer = DatagramBearer.Tcp)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _datagramRegs[protocolId] = (handler, bearer);
        return this;
    }

    /// <summary>配置：请求回调入站处理器（§5.2）。</summary>
    /// <param name="protocolId">协议域 ID（同 ID 重复注册覆盖前者）。</param>
    /// <param name="handler">请求回调入站处理器（非空）。</param>
    /// <returns>同一装配器实例（供链式配置）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/> 为 null。</exception>
    public NetClientBuilder RegisterRequestHandler(byte protocolId, IRequestHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _requestRegs[protocolId] = handler;
        return this;
    }

    /// <summary>配置：流式会话接受面（§5.3——对端开流即服务）。</summary>
    /// <param name="protocolId">协议域 ID（同 ID 重复注册覆盖前者）。</param>
    /// <param name="acceptor">流接受器（非空）。</param>
    /// <returns>同一装配器实例（供链式配置）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="acceptor"/> 为 null。</exception>
    public NetClientBuilder RegisterStreamAcceptor(byte protocolId, IStreamAcceptor acceptor)
    {
        ArgumentNullException.ThrowIfNull(acceptor);
        _streamRegs[protocolId] = acceptor;
        return this;
    }

    /// <summary>配置：开监听（可选——对称节点：服务端可直接推流，客户端/服务器无固定模式，§4.2）。</summary>
    /// <param name="port">TCP 监听端口（0 = 系统分配）。</param>
    /// <returns>同一装配器实例（供链式配置）。</returns>
    public NetClientBuilder Listen(int port)
    {
        _listen = new IPEndPoint(IPAddress.Any, port);
        _listenSet = true;
        return this;
    }

    /// <summary>配置：开监听（显式地址形态）。</summary>
    /// <param name="endpoint">TCP 监听端点。</param>
    /// <returns>同一装配器实例（供链式配置）。</returns>
    public NetClientBuilder Listen(IPEndPoint endpoint)
    {
        _listen = endpoint;
        _listenSet = true;
        return this;
    }

    /// <summary>配置：身份注入（§8.3 配置供给——优先于 Create 形态）。</summary>
    /// <param name="identity">节点身份（非空；其 <c>Id</c> 作为本端 NodeId）。</param>
    /// <returns>同一装配器实例（供链式配置）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="identity"/> 为 null。</exception>
    public NetClientBuilder WithIdentity(NodeIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        _identity = identity;
        return this;
    }

    /// <summary>安全档：KeyPair（推荐缺省档——签名+ECDH+AEAD/MAC，信任锚钉扎/TOFU；错档 fail-fast，
    /// 绝不静默降级为明文——§3.4 防降级）。装配同 <see cref="ClusterBuilder.WithKeyPair"/>。</summary>
    /// <param name="security">KeyPair 档配置（非 KeyPair 档抛——错档 fail-fast）。</param>
    /// <returns>同一装配器实例（供链式配置）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="security"/> 为 null。</exception>
    /// <exception cref="ArgumentException"><paramref name="security"/> 非 KeyPair 档。</exception>
    public NetClientBuilder WithKeyPair(SecurityOptions security)
    {
        ArgumentNullException.ThrowIfNull(security);
        if (security.Mode != SecurityMode.KeyPair)
            throw new ArgumentException($"WithKeyPair 须 KeyPair 档配置（收到 {security.Mode}）。", nameof(security));
        _security = security;
        return this;
    }

    /// <summary>安全档：MutualTls（企业 CA/合规场景——TLS1.3 相互认证 + SAN <c>nid:</c> 绑定）。</summary>
    /// <param name="certificate">本端私钥证书（SAN 含 nid:&lt;hex32&gt; 条目）。</param>
    /// <param name="ca">对端 CA（null = 系统信任库；自签形态传自签 CA）。</param>
    /// <returns>同一装配器实例（供链式配置）。</returns>
    public NetClientBuilder WithMutualTls(System.Security.Cryptography.X509Certificates.X509Certificate2 certificate,
        System.Security.Cryptography.X509Certificates.X509Certificate2Collection? ca = null)
    {
        _security = SecurityOptions.MutualTls(certificate, ca);
        return this;
    }

    /// <summary>自动重连（二期-D7——地址制客户端断链自决重拨）：对端断链（PeerGone）后按退避
    /// （初值 ×2 封顶）重拨原端点直至恢复；缺省关闭（既有语义——调用方自决）。
    /// 仅 <see cref="Connect(IPEndPoint)"/> 地址制形态生效。</summary>
    /// <param name="initialDelay">重拨退避初值（缺省 500ms；须 ≥ 零）。</param>
    /// <param name="maxDelay">重拨退避封顶（缺省 10s；须 ≥ 初值）。</param>
    /// <returns>同一装配器实例（供链式配置）。</returns>
    /// <exception cref="ArgumentOutOfRangeException">初值 &lt; 零，或封顶 &lt; 初值。</exception>
    public NetClientBuilder WithAutoReconnect(TimeSpan? initialDelay = null, TimeSpan? maxDelay = null)
    {
        var initial = initialDelay ?? TimeSpan.FromMilliseconds(500);
        var max = maxDelay ?? TimeSpan.FromSeconds(10);
        ArgumentOutOfRangeException.ThrowIfLessThan(initial, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(max, initial);
        _autoReconnect = (initial, max);
        return this;
    }

    /// <summary>配置：日志。</summary>
    /// <param name="logger">日志器（null = 不输出日志）。</param>
    /// <returns>同一装配器实例（供链式配置）。</returns>
    public NetClientBuilder WithLogger(ILogger logger)
    {
        _logger = logger;
        return this;
    }

    /// <summary>配置：可观测枢纽（null = Disabled——§9.1 视图接入）。</summary>
    /// <param name="hub">可观测枢纽；null 表示禁用（Disabled）。</param>
    /// <returns>同一装配器实例（供链式配置）。</returns>
    public NetClientBuilder WithObservability(ObservabilityHub? hub)
    {
        _hub = hub;
        return this;
    }

    /// <summary>启动（一次成型）：解析身份 → 构建传输 → 注册域（先注册后拨号——对端推送到达即服务）
    /// → 地址制直连（<see cref="Connect(IPEndPoint)"/> 配置时；握手即得对端身份 → RemoteId；
    ///   直连失败传输一并清理——装配失败不残留半启动端点）。</summary>
    /// <param name="ct">取消令牌（取消身份解析/直连拨号）。</param>
    /// <returns>完成后的节点端点（未配置 <see cref="Connect(IPEndPoint)"/> 时 RemoteId 为 null；配置了则握手即得对端身份）。</returns>
    public async Task<NodeEndpoint> StartAsync(CancellationToken ct = default)
    {
        var id = await ResolveIdAsync(ct).ConfigureAwait(false);
        var options = ResolveOptions();
        var transport = new ClusterTransport(id, options, security: _security, logger: _logger, hub: _hub);
        transport.Start();

        foreach (var (protocolId, (handler, bearer)) in _datagramRegs)
            transport.RegisterProtocol(protocolId, handler, bearer);
        foreach (var (protocolId, handler) in _requestRegs)
            transport.RegisterRequestHandler(protocolId, handler);
        foreach (var (protocolId, acceptor) in _streamRegs)
            transport.RegisterStreamAcceptor(protocolId, acceptor);

        NodeId? remote = null;
        try
        {
            if (_connect is not null)
                remote = await transport.ConnectAsync(_connect, ct).ConfigureAwait(false);
        }
        catch
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        // ★ 二期-D7：自动重连代理挂载（机制形态——随端点释放；对端断链即按退避重拨原端点）
        ClientReconnectAgent? agent = null;
        if (_autoReconnect is { } ar && remote is { } remoteId && _connect is { } connectEp)
        {
            agent = new ClientReconnectAgent(transport,
                (ep, ctk) => transport.ConnectAsync(ep, ctk), connectEp, ar.Initial, ar.Max);
            agent.Arm(remoteId);
        }
        INodeMechanism[] mounted = agent is null ? [] : [agent];
        return new NodeEndpoint(transport, mounted, remote, options);
    }

    /// <summary>传输选项裁决（优先级三段式：显式方法 > 注入 > Default；管道链尾依序执行）。</summary>
    private TransportOptions ResolveOptions()
    {
        var options = _transportBase ?? TransportOptions.Default(null, new Dictionary<NodeId, IPEndPoint>());
        if (_listenSet) options = options.WithListen(_listen);
        if (_clusterTagSet) options = options.WithClusterTag(_clusterTag);
        if (_udpListen is not null) options = options.WithUdp(_udpListen);
        foreach (var pipe in _transportPipes) options = pipe(options);
        return options;
    }

    /// <summary>身份裁决（WithIdentity 配置注入优先 → Create(nodeId) → Create(source)）。</summary>
    private async ValueTask<NodeId> ResolveIdAsync(CancellationToken ct)
    {
        if (_identity is { } injected) return injected.Id;
        if (_nodeId is { } explicitId) return explicitId;
        if (_identitySource is { } source)
            return (await source.LoadOrCreateAsync(ct).ConfigureAwait(false)).Id;
        throw new InvalidOperationException(
            "身份未供给——Create(nodeId)/Create(IIdentitySource)/WithIdentity 三选一（spec-12 §8.3）。");
    }
}
