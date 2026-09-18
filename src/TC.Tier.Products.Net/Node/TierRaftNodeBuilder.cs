using System.Net;
using System.Net.Sockets;
using TC.Tier.Core.Logging;
using TC.Tier.Core.Net;
using TC.Tier.Core.Net.Channels;
using TC.Tier.Core.Net.Hosting;
using TC.Tier.Core.Net.Raft;
using TC.Tier.Core.Net.Transport;
using TC.Tier.Core.IO;

namespace TC.Tier.Products.Net.Node;

/// <summary>
/// TierRaftNode 装配器（D6 产品接线收口——三段式惯例：Create → 链式配置 → StartAsync 一次成型）：
/// 传输二选一——①注入现成传输（<see cref="WithTransport"/>——嵌入式同进程/调用方自组装）∥
/// ②内建 <see cref="ClusterBuilder"/> 组装（<see cref="WithClusterTransport"/>——TCP 真部署形态，
/// 传输配置走 B 方案全旋钮口）；都不给 = fail-fast 教用法，不给默认形态（装配形态显式）。
/// <para>★ 组合而非复制：内建形态经 Core.Net 装配器组装传输（NodeEndpoint 承载——机制/传输
/// 生命周期随之归节点），产品层不重写传输拼装样板。</para>
/// <para>★ 兼容：静态 <see cref="TierRaftNode.StartAsync"/> 保留为薄壳（签名不变——存量
/// 调用/测试/探针零改动），内部走本装配器。</para>
/// </summary>
public sealed class TierRaftNodeBuilder
{
    private readonly NodeId _id;
    private readonly IFileSystem _fs;
    private readonly ClusterConfig _config;
    private readonly IStateMachine _machine;
    private TierRaftNodeOptions _options = TierRaftNodeOptions.Default;
    private ILogger? _logger;
    private IProtocolTransport? _transport;            // 形态①：注入现成传输
    private ClusterBuilder? _clusterBuilder;           // 形态②：内建 TCP 组装
    private NodeId[]? _joinPeers;                      // 引导同伴（进程内形态）
    private IPEndPoint[]? _joinEndpoints;              // 引导端点（TCP 形态）
    private bool _joinAsLearner;                       // learner 永久只读引导（#441——DP 形态：入组不晋级）
    private bool _joinAsWitness;                       // witness 引导（三期-F2——投票不存数据，永不晋级）

    private TierRaftNodeBuilder(NodeId id, IFileSystem fs, ClusterConfig config, IStateMachine machine)
    {
        _id = id;
        _fs = fs;
        _config = config;
        _machine = machine;
    }

    /// <summary>创建（三段式入口）：节点标识 / 组合根文件系统（每节点私有卷）/ 集群配置 / 业务状态机。</summary>
    /// <param name="id">节点标识（须 ∈ config.Members）。</param>
    /// <param name="fs">组合根文件系统。</param>
    /// <param name="config">集群配置。</param>
    /// <param name="machine">业务状态机（ApplyAsync——日志即状态机）。</param>
    /// <returns>装配器实例（后续链式配置传输/选项后调用 StartAsync 成型）。</returns>
    public static TierRaftNodeBuilder Create(NodeId id, IFileSystem fs, ClusterConfig config, IStateMachine machine)
    {
        if (id == NodeId.Empty) throw new ArgumentException("节点 ID 不可为 Empty 哨兵。", nameof(id));
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(machine);
        return new TierRaftNodeBuilder(id, fs, config, machine);
    }

    /// <summary>形态①：注入现成传输（嵌入式同进程 = InProcessTransportHub 注册代理；
    /// 调用方自组装 = 自建 ClusterTransport/NodeEndpoint）——与形态②二选一。</summary>
    /// <param name="transport">现成传输实例（ClusterTransport / InProcess 注册代理 / NodeEndpoint）。</param>
    /// <returns>本装配器实例（链式）。</returns>
    /// <exception cref="InvalidOperationException">与 <see cref="WithClusterTransport"/> 同时供给（双供给 fail-fast）。</exception>
    public TierRaftNodeBuilder WithTransport(IProtocolTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _transport = transport;
        return this;
    }

    /// <summary>形态②：内建 TCP 组装（内部经 <see cref="ClusterBuilder"/>——传输配置全旋钮可调，
    /// 与形态①二选一）。</summary>
    /// <param name="listen">本端 TCP 监听端点（null = 不监听——纯拨号方）。</param>
    /// <param name="peers">对端地址表（成员制拨号目标）。</param>
    /// <param name="clusterTag">集群归属标签（0 = 单集群零配置起步）。</param>
    /// <param name="tune">传输配置管道（重连退避/保活/请求回调等——链尾执行裁决一切，可空）。</param>
    /// <returns>本装配器实例（链式）。</returns>
    /// <exception cref="InvalidOperationException">与 <see cref="WithTransport"/> 同时供给（双供给 fail-fast）。</exception>
    public TierRaftNodeBuilder WithClusterTransport(IPEndPoint? listen,
        IReadOnlyDictionary<NodeId, IPEndPoint> peers,
        uint clusterTag = 0,
        Func<TransportOptions, TransportOptions>? tune = null)
    {
        ArgumentNullException.ThrowIfNull(peers);
        var builder = ClusterBuilder.Create(_id)
            .Listen(listen)
            .Peers(peers);
        if (clusterTag != 0) builder.ClusterTag(clusterTag);
        if (tune is not null) builder.WithTransport(tune);
        _clusterBuilder = builder;
        return this;
    }

    /// <summary>配置：产品装配选项（TierWal/Raft/Apply/Swarm/宿主调度——null = 产品默认）。</summary>
    /// <param name="options">产品装配选项（TierWal/Raft/Apply/Swarm/宿主调度）。</param>
    /// <returns>本装配器实例（链式）。</returns>
    public TierRaftNodeBuilder WithOptions(TierRaftNodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        return this;
    }

    /// <summary>加入既有集群（Standby 引导形态）：本节点以 learner 身份启动（本地配置仅引导用——
    /// 真实配置随复制收敛；重启节点走 WAL 恢复的真实配置，宣告幂等），启动后向引导同伴轮转递
    /// 加入请求，追平后自动晋级 voter——<see cref="TierRaftNode.Raft"/> 的
    /// <c>IsVoter</c> 翻真即就绪信号（调用方轮询或经 LeaderChanged 编排）。</summary>
    /// <param name="bootstrapPeers">引导同伴节点 ID（进程内/已互联传输形态——至少一个当前集群成员）。</param>
    /// <returns>本装配器实例（链式）。</returns>
    public TierRaftNodeBuilder WithJoin(params NodeId[] bootstrapPeers)
    {
        ArgumentNullException.ThrowIfNull(bootstrapPeers);
        ArgumentOutOfRangeException.ThrowIfZero(bootstrapPeers.Length);
        _joinPeers = bootstrapPeers;
        _joinEndpoints = null;
        return this;
    }

    /// <summary>加入既有集群（Standby 引导形态，TCP）——端点拨号形态，语义同上。</summary>
    /// <param name="bootstrapEndpoints">引导端点（至少一个当前集群成员的监听地址；建议覆盖全部成员）。</param>
    /// <returns>本装配器实例（链式）。</returns>
    public TierRaftNodeBuilder WithJoin(params IPEndPoint[] bootstrapEndpoints)
    {
        ArgumentNullException.ThrowIfNull(bootstrapEndpoints);
        ArgumentOutOfRangeException.ThrowIfZero(bootstrapEndpoints.Length);
        _joinEndpoints = bootstrapEndpoints;
        _joinPeers = null;
        return this;
    }

    /// <summary>加入既有集群并保持 learner 永久只读（#441——DP 节点引导形态）：以 learner 入组、
    /// 日志/快照全路径复制，<b>永不晋级 voter、不计入多数派</b>——扩 DP 不影响选举面。
    /// 就绪信号 = 活动配置收敛含自身（<see cref="TierRaftNode.Raft"/> 的 Config.Contains(self)）。</summary>
    /// <param name="bootstrapPeers">引导同伴节点 ID（进程内/已互联传输形态）。</param>
    /// <returns>本装配器实例（链式）。</returns>
    public TierRaftNodeBuilder WithJoinAsLearner(params NodeId[] bootstrapPeers)
    {
        WithJoin(bootstrapPeers);
        _joinAsLearner = true;
        return this;
    }

    /// <summary>加入既有集群并保持 learner 永久只读（TCP 端点形态）——语义同上。</summary>
    /// <param name="bootstrapEndpoints">引导端点（至少一个当前集群成员的监听地址）。</param>
    /// <returns>本装配器实例（链式）。</returns>
    public TierRaftNodeBuilder WithJoinAsLearner(params IPEndPoint[] bootstrapEndpoints)
    {
        WithJoin(bootstrapEndpoints);
        _joinAsLearner = true;
        return this;
    }

    /// <summary>加入既有集群为 witness（三期-F2——见证者引导形态）：以 witness 入组——投票计入
    /// 选主/提交多数派，不存全量数据（高水位断言流）、<b>永不晋级 voter、不自荐</b>。
    /// 启动走 <see cref="StartWitnessAsync"/>（witness 无日志体/状态机——非 <see cref="TierRaftNode"/> 形态）。</summary>
    /// <param name="bootstrapPeers">引导同伴节点 ID（进程内/已互联传输形态）。</param>
    /// <returns>本装配器实例（链式）。</returns>
    public TierRaftNodeBuilder WithJoinAsWitness(params NodeId[] bootstrapPeers)
    {
        WithJoin(bootstrapPeers);
        _joinAsWitness = true;
        return this;
    }

    /// <summary>加入既有集群为 witness（TCP 端点形态）——语义同上。</summary>
    /// <param name="bootstrapEndpoints">引导端点（至少一个当前集群成员的监听地址）。</param>
    /// <returns>本装配器实例（链式）。</returns>
    public TierRaftNodeBuilder WithJoinAsWitness(params IPEndPoint[] bootstrapEndpoints)
    {
        WithJoin(bootstrapEndpoints);
        _joinAsWitness = true;
        return this;
    }

    /// <summary>配置：日志。</summary>
    /// <param name="logger">日志实例。</param>
    /// <returns>本装配器实例（链式）。</returns>
    public TierRaftNodeBuilder WithLogger(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        return this;
    }

    /// <summary>启动（一次成型）：解析传输（双供给/零供给 fail-fast）→ 完整产品节点装配
    /// （TierWal 恢复 → 存储适配 → apply → raft → 宿主调度）。内建 TCP 形态下传输生命周期
    /// 随节点（DisposeAsync 一并收尾）。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已启动的完整产品节点（装配完毕，宿主调度循环已运行）。</returns>
    /// <exception cref="InvalidOperationException">传输双供给或零供给（fail-fast——形态须显式二选一）；
    /// witness 引导档（<c>WithJoinAsWitness</c>——须走 <see cref="StartWitnessAsync"/>）。</exception>
    public async Task<TierRaftNode> StartAsync(CancellationToken cancellationToken = default)
    {
        if (_joinAsWitness)
            throw new InvalidOperationException(
                "witness 引导档须走 StartWitnessAsync——witness 无日志体/状态机，非 TierRaftNode 形态。");
        if (_transport is not null && _clusterBuilder is not null)
            throw new InvalidOperationException("传输双供给——WithTransport（注入）与 WithClusterTransport（内建组装）二选一。");
        if (_transport is null && _clusterBuilder is null)
            throw new InvalidOperationException(
                "传输未供给——WithTransport（注入现成传输，嵌入式同进程）或 WithClusterTransport（内建 TCP 组装）二选一。");

        NodeEndpoint? owned = null;
        try
        {
            var transport = _transport;
            if (transport is null)
            {
                owned = await _clusterBuilder!.StartAsync(cancellationToken).ConfigureAwait(false);
                transport = owned;   // NodeEndpoint 实现 IProtocolTransport 完整面
            }
            // Standby 引导——本地配置强制 [self learner]（Create 传入的 config 为既有集群视角时
            // 引导面不可用；learner 不投票不破坏多数派，追平晋级由集群侧完成）
            var config = _joinPeers is not null || _joinEndpoints is not null
                ? new ClusterConfig([new ClusterMember(_id, "", ClusterMemberRole.Learner)])
                : _config;
            var node = await TierRaftNode.StartCoreAsync(_id, _fs, transport, config, _machine, _options, _logger, owned)
                .ConfigureAwait(false);
            owned = null;   // 所有权移交节点（DisposeAsync 链尾收尾）
            // TCP 成员表拨号加入：通告本端监听地址（leader 注册拨号表回连复制——不通告则
            // leader 无法回连，配置不收敛、Join 超时）；进程内/已互联形态解析不出 = 不通告
            var announce = ResolveAnnounceEndPoint(transport);
            if (_joinPeers is not null)
                await node.Raft.JoinAsync(_joinPeers, cancellationToken: cancellationToken, autoPromote: !_joinAsLearner,
                    listenEndPoint: announce).ConfigureAwait(false);
            else if (_joinEndpoints is not null)
                await node.Raft.JoinAsync(_joinEndpoints, cancellationToken: cancellationToken, autoPromote: !_joinAsLearner,
                    listenEndPoint: announce).ConfigureAwait(false);
            return node;
        }
        finally
        {
            if (owned is not null) await owned.DisposeAsync().ConfigureAwait(false);   // 装配失败——不残留半启动传输
        }
    }

    /// <summary>启动 witness 节点（三期-F2——<c>WithJoinAsWitness</c> 引导档或静态
    /// witness 配置形态）：高水位断言流（<see cref="WitnessHighWaterStore"/>）代替日志体——
    /// 无 TierWal/apply 管道/Swarm/宿主调度，投票计多数派、永不晋级不自荐。内建 TCP 形态下
    /// 传输生命周期随节点（DisposeAsync 链尾收尾）。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已启动的 witness 节点（引导档完成时已入组——leader 活动配置含本端）。</returns>
    /// <exception cref="InvalidOperationException">传输双供给或零供给；静态配置形态本端非 witness 角色。</exception>
    public async Task<TierRaftWitnessNode> StartWitnessAsync(CancellationToken cancellationToken = default)
    {
        if (!_joinAsWitness && (_joinPeers is not null || _joinEndpoints is not null))
            throw new InvalidOperationException("join 引导档与 witness 节点不匹配——witness 引导用 WithJoinAsWitness。");
        if (_transport is not null && _clusterBuilder is not null)
            throw new InvalidOperationException("传输双供给——WithTransport（注入）与 WithClusterTransport（内建组装）二选一。");
        if (_transport is null && _clusterBuilder is null)
            throw new InvalidOperationException(
                "传输未供给——WithTransport（注入现成传输，嵌入式同进程）或 WithClusterTransport（内建 TCP 组装）二选一。");

        NodeEndpoint? owned = null;
        try
        {
            var transport = _transport;
            if (transport is null)
            {
                owned = await _clusterBuilder!.StartAsync(cancellationToken).ConfigureAwait(false);
                transport = owned;
            }
            // 引导档——本地配置强制 [self witness]（引擎 witness 门依据：内容不落盘/投票不自荐）；
            // 静态配置形态——本端须已以 witness 角色登记（校验 fail-fast，防误装配成全量节点口径）
            var config = _joinPeers is not null || _joinEndpoints is not null
                ? new ClusterConfig([new ClusterMember(_id, "", ClusterMemberRole.Witness)])
                : ValidateWitnessConfig(_config);
            TierRaftNode.EnableHighResolutionTimer(_options.HighResolutionTimer, _logger);
            var store = new WitnessHighWaterStore();
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var apply = new WitnessApplySink();   // witness 无状态机——commit 恒不推进，sink 永不触发
            var raft = new RaftStateMachine(_id, store, transport, apply, _options.Raft, logger: _logger);
            await raft.StartAsync(config).ConfigureAwait(false);
            var node = new TierRaftWitnessNode(_id, raft, store, owned, _logger);
            owned = null;   // 所有权移交节点
            if (_joinPeers is not null)
                await raft.JoinAsync(_joinPeers, cancellationToken: cancellationToken, autoPromote: false, asWitness: true,
                    listenEndPoint: ResolveAnnounceEndPoint(transport)).ConfigureAwait(false);
            else if (_joinEndpoints is not null)
                await raft.JoinAsync(_joinEndpoints, cancellationToken: cancellationToken, autoPromote: false, asWitness: true,
                    listenEndPoint: ResolveAnnounceEndPoint(transport)).ConfigureAwait(false);
            return node;
        }
        finally
        {
            if (owned is not null) await owned.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>静态配置形态校验——本端须以 witness 角色在配置中（防误装配：非 witness 角色
    /// 会走全量日志体路径，与高水位存储矛盾）。</summary>
    private ClusterConfig ValidateWitnessConfig(ClusterConfig config)
        => config.IsWitness(_id)
            ? config
            : throw new InvalidOperationException(
                "静态配置形态本端须为 witness 角色（ClusterMemberRole.Witness）——全量节点走 StartAsync。");

    /// <summary>本端通告地址（TCP 形态从传输监听配置解析——随 JoinReq 通告 leader 回连复制；
    /// 进程内/已互联形态解析不出 = null 不通告）。</summary>
    private static string? ResolveAnnounceEndPoint(IProtocolTransport transport)
        => transport switch
        {
            NodeEndpoint ep => ep.Options.ListenEndPoint?.ToString(),
            Core.Net.Transport.Tcp.ClusterTransport tcp => tcp.Options.ListenEndPoint?.ToString(),
            _ => null,
        };

    /// <summary>witness 空应用槽（<see cref="IApplySink"/> no-op）：witness 分支不推进 commit
    /// （无状态机——高水位即其持久化语义），Submit/AppliedTo 永不触发。</summary>
    private sealed class WitnessApplySink : IApplySink
    {
        public void Submit(long commitIndex) { }
#pragma warning disable CS0067 // 契约事件——witness 永不触发（无状态机）
        public event Action<long>? AppliedTo;
#pragma warning restore CS0067
    }
}
