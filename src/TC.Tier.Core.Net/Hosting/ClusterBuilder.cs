using System.Net;
using TC.Tier.Core.Logging;
using TC.Tier.Core.Observability;
using TC.Tier.Core.Net.Channels;
using TC.Tier.Core.Net.Ports;
using TC.Tier.Core.Net.Security;
using TC.Tier.Core.Net.Transport;
using TC.Tier.Core.Net.Transport.Tcp;

namespace TC.Tier.Core.Net.Hosting;

/// <summary>
/// 成员制节点装配器（spec-12 §11——服务端预设：监听+拨号+机制显式 opt-in，永不默认挂载）。
/// <para>★ 三段式（装配面惯例）：Create → 链式配置 → StartAsync 一次成型——
/// 返回 <see cref="NodeEndpoint"/>（传输完整面 + 机制生命周期归位）。</para>
/// <para>★ 身份三供给（§8.3）：Create(nodeId) 显式 ∥ Create(IIdentitySource) 缺省便利
/// （首次生成保存、此后加载——NodeId 跨重启稳定）∥ WithIdentity(identity) 配置注入
/// （K8s secret）；三选一，WithIdentity 优先。</para>
/// <para>★ 安全档（§3.4 防降级 fail-closed）：缺省 Plaintext；KeyPair/mTLS 档随 W-Security
/// 波次落地——请求未落地安全档 = 立即抛，绝不静默降级为明文。</para>
/// </summary>
public sealed class ClusterBuilder
{
    private readonly NodeId? _nodeId;
    private readonly IIdentitySource? _identitySource;
    private NodeIdentity? _identity;
    private uint _clusterTag = TransportOptions.DefaultClusterTag;
    private IPEndPoint? _listen;
    private IPEndPoint? _udpListen;
    private readonly Dictionary<NodeId, IPEndPoint> _peers = [];
    private readonly List<INodeMechanism> _mechanisms = [];
    private SecurityOptions? _security;   // 安全档（null = 明文——spec-12 §3.4）
    private ILogger? _logger;
    private ObservabilityHub? _hub;
    private INodeAuthorizer? _authorizer;   // 二期-H4：授权钩子（策略模型归产品）
    private IAuditSink? _audit;             // 二期-H3：审计 sink
    private TransportOptions? _transportBase;   // 传输选项整份注入（显式方法未触达的位生效）
    private readonly List<Func<TransportOptions, TransportOptions>> _transportPipes = [];   // 管道（链尾依序执行、裁决一切）
    private bool _listenSet;        // 显式标记——Listen(null)（不监听）与"未调用"语义不同
    private bool _peersSet;
    private bool _clusterTagSet;

    private ClusterBuilder(NodeId? nodeId, IIdentitySource? source)
    {
        _nodeId = nodeId;
        _identitySource = source;
    }

    /// <summary>创建（显式节点 ID——配置注入形态）。</summary>
    /// <param name="nodeId">本端节点 ID（不可为 <see cref="NodeId.Empty"/> 哨兵）。</param>
    /// <returns>装配器实例（供链式配置）。</returns>
    /// <exception cref="ArgumentException"><paramref name="nodeId"/> 为 Empty 哨兵。</exception>
    public static ClusterBuilder Create(NodeId nodeId)
    {
        if (nodeId == NodeId.Empty) throw new ArgumentException("节点 ID 不可为 Empty 哨兵。", nameof(nodeId));
        return new ClusterBuilder(nodeId, null);
    }

    /// <summary>创建（身份供给端口——首次生成保存、此后加载；NodeId 跨重启稳定）。</summary>
    /// <param name="source">身份供给端口（非空）。</param>
    /// <returns>装配器实例（供链式配置）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> 为 null。</exception>
    public static ClusterBuilder Create(IIdentitySource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new ClusterBuilder(null, source);
    }

    /// <summary>配置：集群归属标签（握手期校验——错集群 fail-fast，§3.3）。</summary>
    /// <param name="clusterTag">集群归属标签（握手期与对端核对）。</param>
    /// <returns>同一装配器实例（供链式配置）。</returns>
    public ClusterBuilder ClusterTag(uint clusterTag)
    {
        _clusterTag = clusterTag;
        _clusterTagSet = true;
        return this;
    }

    /// <summary>配置：本端 TCP 监听端点（null = 不监听——纯拨号方；对称节点可随时开监听）。</summary>
    /// <param name="endpoint">TCP 监听端点；null 表示不监听（语义与"未调用"不同）。</param>
    /// <returns>同一装配器实例（供链式配置）。</returns>
    public ClusterBuilder Listen(IPEndPoint? endpoint)
    {
        _listen = endpoint;
        _listenSet = true;
        return this;
    }

    /// <summary>配置：本端 TCP 监听（host 支持 IP 或域名）。</summary>
    /// <param name="host">监听地址（IP 字面量——经 <see cref="IPAddress.Parse(string)"/> 解析）。</param>
    /// <param name="port">监听端口（0 = 系统分配）。</param>
    /// <returns>同一装配器实例（供链式配置）。</returns>
    public ClusterBuilder Listen(string host, int port)
        => Listen(new IPEndPoint(IPAddress.Parse(host), port));

    /// <summary>配置：UDP 数据报端点（§4.5——数据报/请求回调承载；绑 Any，通告时以 TCP 连接本端地址替代）。</summary>
    /// <param name="port">UDP 监听端口。</param>
    /// <returns>同一装配器实例（供链式配置）。</returns>
    public ClusterBuilder WithUdp(int port)
    {
        _udpListen = new IPEndPoint(IPAddress.Any, port);
        return this;
    }

    /// <summary>配置：UDP 数据报端点（显式地址形态）。</summary>
    /// <param name="endpoint">UDP 监听端点（显式地址）。</param>
    /// <returns>同一装配器实例（供链式配置）。</returns>
    public ClusterBuilder WithUdp(IPEndPoint endpoint)
    {
        _udpListen = endpoint;
        return this;
    }

    /// <summary>配置：对端地址表（装配期静态——成员制拨号目标；§4.1 地址表=配置即持久化）。</summary>
    /// <param name="peers">对端地址表（NodeId → 端点；替换既有整表，非空）。</param>
    /// <returns>同一装配器实例（供链式配置）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="peers"/> 为 null。</exception>
    public ClusterBuilder Peers(IReadOnlyDictionary<NodeId, IPEndPoint> peers)
    {
        ArgumentNullException.ThrowIfNull(peers);
        _peers.Clear();
        foreach (var (peer, endpoint) in peers) _peers[peer] = endpoint;
        _peersSet = true;
        return this;
    }

    /// <summary>配置：传输选项整份注入（spec-12 §4.5 全旋钮可调口）。
    /// <para>优先级三段式（TierFs×options 合流同构）：显式方法 > 注入 > Default——
    /// <c>Listen</c>, <see cref="Peers"/>, <see cref="ClusterTag"/>, <c>WithUdp</c>
    /// 显式触达的位仍归方法裁决，其余旋钮（握手超时/重连退避/保活/请求回调/流窗口/写队列等）
    /// 以注入值为准。</para></summary>
    /// <param name="options">传输选项基线（非空）。</param>
    /// <returns>同一装配器实例（供链式配置）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> 为 null。</exception>
    public ClusterBuilder WithTransport(TransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _transportBase = options;
        return this;
    }

    /// <summary>配置：传输选项管道（StartAsync 链尾依序执行、裁决一切——全部旋钮的通用改写口，可多次调用）。
    /// <para>典型：重连退避（raft 消费方按 ElectionTimeoutMin/2 覆盖）、保活、请求回调容量、
    /// UDP 单报预算、流窗口/写队列背压深度。</para></summary>
    /// <param name="configure">选项改写函数（非空；链尾依序执行，后执行者覆盖先执行者）。</param>
    /// <returns>同一装配器实例（供链式配置）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> 为 null。</exception>
    public ClusterBuilder WithTransport(Func<TransportOptions, TransportOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _transportPipes.Add(configure);
        return this;
    }

    /// <summary>配置：身份注入（§8.3 配置供给——部署/secret 来；优先于 Create 形态）。</summary>
    /// <param name="identity">节点身份（非空；其 <c>Id</c> 作为本端 NodeId）。</param>
    /// <returns>同一装配器实例（供链式配置）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="identity"/> 为 null。</exception>
    public ClusterBuilder WithIdentity(NodeIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        _identity = identity;
        return this;
    }

    /// <summary>安全档：KeyPair（推荐缺省档——W-Security 波次落地；请求未落地档 = fail-closed 拒绝，
    /// 绝不静默降级为明文——§3.4 防降级）。</summary>
    /// <param name="security">KeyPair 档安全配置（非空；<c>Mode</c> 必须为 <see cref="SecurityMode.KeyPair"/>）。</param>
    /// <returns>同一装配器实例（供链式配置）。</returns>
    /// <exception cref="ArgumentException"><paramref name="security"/> 非 KeyPair 档。</exception>
    public ClusterBuilder WithKeyPair(SecurityOptions security)
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
    public ClusterBuilder WithMutualTls(System.Security.Cryptography.X509Certificates.X509Certificate2 certificate,
        System.Security.Cryptography.X509Certificates.X509Certificate2Collection? ca = null)
    {
        _security = SecurityOptions.MutualTls(certificate, ca);
        return this;
    }

    /// <summary>配置：挂载节点机制（§1 机制零特权——内建与第三方/私有域同一挂载路径；
    /// 显式 opt-in，永不默认挂载）。</summary>
    /// <param name="mechanism">节点机制（非空；StartAsync 时按挂载序挂载，生命周期随端点释放）。</param>
    /// <returns>同一装配器实例（供链式配置）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mechanism"/> 为 null。</exception>
    public ClusterBuilder WithMechanism(INodeMechanism mechanism)
    {
        ArgumentNullException.ThrowIfNull(mechanism);
        _mechanisms.Add(mechanism);
        return this;
    }

    /// <summary>授权钩子（二期-H4——链路/域级请求准入判定；策略模型归产品）。</summary>
    /// <param name="authorizer">节点授权器（非空——准入判定实现；策略模型归产品）。</param>
    /// <returns>同一装配器实例（供链式配置）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="authorizer"/> 为 null。</exception>
    public ClusterBuilder WithAuthorizer(INodeAuthorizer authorizer)
    {
        ArgumentNullException.ThrowIfNull(authorizer);
        _authorizer = authorizer;
        return this;
    }

    /// <summary>审计 sink（二期-H3——安全事件/管理动作上报；实现归外部）。</summary>
    /// <param name="audit">审计 sink（非空——安全事件/管理动作上报实现）。</param>
    /// <returns>同一装配器实例（供链式配置）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="audit"/> 为 null。</exception>
    public ClusterBuilder WithAudit(IAuditSink audit)
    {
        ArgumentNullException.ThrowIfNull(audit);
        _audit = audit;
        return this;
    }

    /// <summary>配置：日志。</summary>
    /// <param name="logger">日志器（null = 不输出日志）。</param>
    /// <returns>同一装配器实例（供链式配置）。</returns>
    public ClusterBuilder WithLogger(ILogger logger)
    {
        _logger = logger;
        return this;
    }

    /// <summary>配置：可观测枢纽（null = Disabled——§9.1 视图接入）。</summary>
    /// <param name="hub">可观测枢纽；null 表示禁用（Disabled）。</param>
    /// <returns>同一装配器实例（供链式配置）。</returns>
    public ClusterBuilder WithObservability(ObservabilityHub? hub)
    {
        _hub = hub;
        return this;
    }

    /// <summary>启动（一次成型）：解析身份 → 构建传输（选项校验 fail-fast）→ 启动 → 按序挂载机制
    /// → 包成 <see cref="NodeEndpoint"/>（机制生命周期随端点释放；中途失败已挂载件与传输一并清理）。</summary>
    /// <param name="cancellationToken">取消令牌（取消启动序列——已启动部分被清理后抛出）。</param>
    /// <returns>完成后的节点端点（传输 + 已挂载机制——通信入口）。</returns>
    public async Task<NodeEndpoint> StartAsync(CancellationToken cancellationToken = default)
    {
        var id = await ResolveIdAsync(cancellationToken).ConfigureAwait(false);
        var options = ResolveOptions();

        var transport = new ClusterTransport(id, options, _security, logger: _logger, hub: _hub,
            authorizer: _authorizer, audit: _audit);
        transport.Start();
        var mounted = new List<INodeMechanism>();
        try
        {
            foreach (var mechanism in _mechanisms)
            {
                await mechanism.MountAsync(transport, cancellationToken).ConfigureAwait(false);
                mounted.Add(mechanism);
            }
        }
        catch
        {
            foreach (var mechanism in mounted.AsEnumerable().Reverse())
            {
                try { await mechanism.DisposeAsync().ConfigureAwait(false); } catch { /* 尽力清理 */ }
            }
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return new NodeEndpoint(transport, [.. _mechanisms], options: options);
    }

    /// <summary>传输选项裁决（优先级三段式：显式方法 > 注入 > Default；管道链尾依序执行）。</summary>
    private TransportOptions ResolveOptions()
    {
        var options = _transportBase ?? TransportOptions.Default(null, new Dictionary<NodeId, IPEndPoint>());
        if (_listenSet) options = options.WithListen(_listen);
        if (_peersSet) options = options.WithPeers(_peers);
        if (_clusterTagSet) options = options.WithClusterTag(_clusterTag);
        if (_udpListen is not null) options = options.WithUdp(_udpListen);
        foreach (var pipe in _transportPipes) options = pipe(options);
        return options;
    }

    /// <summary>身份裁决（WithIdentity 配置注入优先 → Create(nodeId) → Create(source) 加载或首次创建）。</summary>
    private async ValueTask<NodeId> ResolveIdAsync(CancellationToken cancellationToken)
    {
        if (_identity is { } injected) return injected.Id;
        if (_nodeId is { } explicitId) return explicitId;
        if (_identitySource is { } source)
        {
            var loaded = await source.LoadOrCreateAsync(cancellationToken).ConfigureAwait(false);
            return loaded.Id;
        }
        throw new InvalidOperationException(
            "身份未供给——Create(nodeId)/Create(IIdentitySource)/WithIdentity 三选一（spec-12 §8.3）。");
    }
}
