using System.Collections.Concurrent;
using System.Net;
using TC.Tier.Core.IO;
using TC.Tier.Core.Logging;
using TC.Tier.Core.Net.Channels;
using TC.Tier.Core.Net.Raft;
using TC.Tier.Core.Net.Transport;
using TC.Tier.Core.Net.Transport;
using TC.Tier.Products.Wal;

namespace TC.Tier.Products.Net.Host;

/// <summary>
/// TierRaftHost——Multi-Raft 多组装配宿主（二期-C3 §8，裁定②-4 双形态）：
/// 一个 <see cref="IProtocolTransport"/> 上 N 组，每组一套完整产品装配链
/// （TierWal → TierWalRaftStore → ApplyPipeline → RaftStateMachine + 可选 Swarm/SnapshotSwarm*，
/// 经 <see cref="RaftGroupChannel"/> 组作用域视图挂载——引擎零组感知）。
/// <para>★ 传输所有权双形态（裁定②-4）：(a) <see cref="Create"/> 自建工厂——内部组装
/// ClusterTransport 并 owned（缺省便利形态）；(b) 外部传输构造器——装配层拥有（注入/多介质）。
/// 两形态 <see cref="Transport"/> 属性均暴露（产品协议域 0x60+ 注册面，与组路由正交）。</para>
/// <para>★ 成员对账（§8.2）：周期取全体组配置成员并集 ↔ 物理对端表（IPeerRegistry）收敛——
/// 新成员 AddPeer、不再被任何组引用者 RemovePeer(closeLink)；组内成员集合始终以各组
/// ClusterConfig 为准（§2.3 不变量⑤），物理表只是可达性超集。</para>
/// <para>★ 生命周期（§8.3）：Dispose = 对账循环停 → 各组（raft→apply→swarm→wal，分段有界）
/// → 内部组宿主 → 传输（若 owned）。</para>
/// </summary>
public sealed class TierRaftHost : IAsyncDisposable
{
    private readonly NodeId _self;
    private readonly TierRaftHostOptions _options;
    private readonly ILogger? _logger;
    private readonly RaftGroupHost _groupHost;               // Core.Net 组路由（三域独占注册）
    private readonly ConcurrentDictionary<RaftGroupId, TierRaftGroup> _groups = new();
    private readonly CancellationTokenSource _reconcileCts = new();
    private Task? _reconcileLoop;
    private PeerDiscoveryService? _discovery;   // 二期-F9：发现汇聚循环
    private readonly IAsyncDisposable? _transportOwner;      // 自建传输所有权（Create 形态非空）
    private int _started;
    private int _disposed;

    private TierRaftHost(IProtocolTransport transport, TierRaftHostOptions options,
        NodeId self, IAsyncDisposable? transportOwner, RaftGroupHost groupHost)
    {
        Transport = transport;
        _self = self;
        _options = options;
        _logger = options.Logger;
        _transportOwner = transportOwner;
        _groupHost = groupHost;
    }

    /// <summary>自建工厂（裁定②-4 缺省便利形态）：内部组装 <see cref="ClusterTransport"/> 并 owned——
    /// Dispose 链尾收尾。需 <see cref="TierRaftHostOptions.ListenEndPoint"/> 监听（多组装配须可入站）。</summary>
    /// <param name="self">本端节点 ID（传输身份）。</param>
    /// <param name="options">宿主装配配置（<see cref="TierRaftHostOptions.ListenEndPoint"/> 必填——缺失抛 <see cref="ArgumentException"/>）。</param>
    /// <returns>传输已启动的多组宿主（组零装配——经 <see cref="CreateGroupAsync"/> 逐组建）。</returns>
    public static TierRaftHost Create(NodeId self, TierRaftHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.ListenEndPoint is null)
            throw new ArgumentException("自建形态须配置 ListenEndPoint（多组装配须可入站）——或改用外部传输构造器。", nameof(options));
        var peers = options.Peers as Dictionary<NodeId, IPEndPoint>
            ?? options.Peers?.ToDictionary(kv => kv.Key, kv => kv.Value)
            ?? new Dictionary<NodeId, IPEndPoint>();
        var transport = new ClusterTransport(self,
            TransportOptions.Default(options.ListenEndPoint, peers),
            security: options.Security, logger: options.Logger, hub: options.Hub);
        transport.Start();
        return new TierRaftHost(transport, options, self, transportOwner: transport,
            groupHost: new RaftGroupHost(transport));
    }

    /// <summary>外部传输构造（装配层拥有——注入/多介质；Dispose 不释放传输）。</summary>
    public TierRaftHost(IProtocolTransport transport, TierRaftHostOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        Transport = transport;
        _options = options ?? new TierRaftHostOptions();
        _logger = _options.Logger;
        _self = transport.Self;
        _groupHost = new RaftGroupHost(transport);
    }

    /// <summary>物理传输（装配层拥有/暴露——产品协议域 0x60+ 注册面，与组路由正交）。</summary>
    public IProtocolTransport Transport { get; }

    /// <summary>本端节点 ID。</summary>
    public NodeId Self => _self;

    /// <summary>拓扑感知面（二期-F1——位置标签/反亲和/故障域判定；null = 未供给）。</summary>
    public TopologyMap? Topology => _options.Topology;

    /// <summary>已装配组集合（快照）。</summary>
    public IReadOnlyCollection<RaftGroupId> Groups => [.. _groups.Keys];

    /// <summary>启动：内部组宿主挂载三域（0x01/0x03/0x04 各一次）+ 对账循环起线。</summary>
    /// <param name="ct">取消令牌（组宿主启动阶段生效）。</param>
    /// <returns>宿主就绪（组路由三域挂载 + 发现源/成员对账循环起线）后完成；重复启动抛 <see cref="InvalidOperationException"/>。</returns>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("TierRaftHost 已启动（StartAsync 只可一次）。");
        await _groupHost.StartAsync(ct).ConfigureAwait(false);
        // ★ 二期-F9：发现源挂载（add-only 汇聚——DNS/文件源漂移跟随）
        if (_options.DiscoverySources is { } sources && sources.Count > 0 && Transport is IPeerRegistry)
        {
            _discovery = new PeerDiscoveryService((IPeerRegistry)Transport, sources, _options.DiscoveryInterval);
            _discovery.Start();
        }
        if (_options.ReconcileInterval > TimeSpan.Zero && Transport is IPeerRegistry)
            _reconcileLoop = Task.Run(() => ReconcileLoopAsync(_reconcileCts.Token), CancellationToken.None);
    }

    /// <summary>装配并启动一组（组 ID 已存在抛；宿主未启动 = 抛——先 StartAsync）。
    /// 组存储命名空间：<paramref name="fs"/> 显式供给 ∥ options.GroupFileSystemFactory(id) 派生。</summary>
    /// <param name="id">组 ID（已装配 = 抛）。</param>
    /// <param name="config">组初始集群配置（组内成员集合的事实源）。</param>
    /// <param name="machine">业务状态机（该组 apply 管道尾——日志即状态机）。</param>
    /// <param name="nodeOptions">节点装配配置（null = options.GroupDefaults）。</param>
    /// <param name="fs">组存储命名空间根（null = options.GroupFileSystemFactory(id) 派生；两者皆无 = 抛）。</param>
    /// <param name="ct">取消令牌（装配阶段生效）。</param>
    /// <returns>已装配并启动的组句柄（TierRaftNode 就绪——复制/apply 运行中）。</returns>
    public async Task<ITierRaftGroup> CreateGroupAsync(RaftGroupId id, ClusterConfig config, IStateMachine machine,
        TierRaftNodeOptions? nodeOptions = null, IFileSystem? fs = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(machine);
        var groupFs = fs ?? _options.GroupFileSystemFactory?.Invoke(id)
            ?? throw new InvalidOperationException(
                $"组 {id} 存储命名空间未供给——CreateGroupAsync 传 fs 或 options.GroupFileSystemFactory（§8.1 每组独立 TierWal 域）。");

        // ★ 二期-F1：反亲和约束（opt-in——EnforceAntiAffinity 开启时，组配置成员同故障域 > 1 即拒）
        if (_options.EnforceAntiAffinity && _options.Topology is { } topo)
        {
            var members = config.Members.Select(m => m.Id).ToArray();
            if (!topo.SatisfiesAntiAffinity(members))
                throw new InvalidOperationException(
                    $"组 {id} 反亲和约束违反：配置成员存在同故障域共置（{string.Join(",", members.Select(m => topo.ZoneOf(m) ?? "unlabeled"))}）。");
        }
        var channel = _groupHost.CreateGroup(id);
        var node = await TierRaftNode.StartCoreAsync(_self, groupFs, channel, config, machine,
            nodeOptions ?? _options.GroupDefaults, _logger, transportOwner: null, groupId: id).ConfigureAwait(false);
        var group = new TierRaftGroup(id, node);
        _groups[id] = group;
        _logger?.LogInformation("TierRaftHost 组装配完成：{Group} members={Count}", id, config.Count);
        return group;
    }

    /// <summary>取组（未装配 = KeyNotFound）。</summary>
    /// <param name="id">组 ID。</param>
    /// <returns>组句柄（读面透传 TierRaftNode）；未装配抛 <see cref="KeyNotFoundException"/>。</returns>
    public ITierRaftGroup GetGroup(RaftGroupId id)
        => _groups.TryGetValue(id, out var g) ? g : throw new KeyNotFoundException($"组 {id} 未装配。");

    /// <summary>取组（Try 语义）。</summary>
    /// <param name="id">组 ID。</param>
    /// <param name="group">输出：命中的组句柄；未命中 = null。</param>
    /// <returns>true = 已装配（<paramref name="group"/> 有效）；false = 未装配（group = null）。</returns>
    public bool TryGetGroup(RaftGroupId id, out ITierRaftGroup? group)
    {
        if (_groups.TryGetValue(id, out var g)) { group = g; return true; }
        group = null;
        return false;
    }

    /// <summary>移除组（注销路由 + 释放该组装配链；有界——不因单组滞留挂死 host）。</summary>
    /// <param name="id">要移除的组 ID。</param>
    /// <returns>路由注销 + 组装配链释放（有界）后完成；组未装配 = 无操作立即完成。</returns>
    public async ValueTask RemoveGroupAsync(RaftGroupId id)
    {
        if (_groups.TryRemove(id, out var group))
        {
            _groupHost.RemoveGroup(id);
            await group.Node.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ═══ 成员对账（§8.2——全体组配置成员并集 ↔ 物理对端表）══

    private async Task ReconcileLoopAsync(CancellationToken ct)
    {
        var registry = Transport as IPeerRegistry;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.ReconcileInterval, ct).ConfigureAwait(false);
                Reconcile(registry);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning("TierRaftHost 成员对账单轮异常（续跑）：{Message}", ex.Message);
            }
        }
    }

    /// <summary>并集收敛：union(各组 Raft.Config 成员端点) − registry 现有 = Add；
    /// registry 现有 − union = RemovePeer(closeLink)。端点解析：IP 字面量（域名归装配层——§8.2）。</summary>
    private void Reconcile(IPeerRegistry? registry)
    {
        if (registry is null) return;
        var union = new Dictionary<NodeId, IPEndPoint>();
        foreach (var group in _groups.Values)
        {
            foreach (var m in group.Config.Members)
            {
                if (m.Id == _self || string.IsNullOrEmpty(m.EndPoint)) continue;
                if (IPEndPoint.TryParse(m.EndPoint, out var ep))
                    union[m.Id] = ep;   // 同 ID 多组端点不一致 = 后者覆盖（组间一致性归产品编排）
            }
        }

        foreach (var (id, ep) in union)
            if (!registry.Peers.TryGetValue(id, out var current) || !current.Equals(ep))
                registry.AddPeer(id, ep);

        foreach (var (id, _) in registry.Peers)
            if (!union.ContainsKey(id))
                registry.RemovePeer(id);   // 默认断链 + 黑名单（复入经 AddPeer 解禁）
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _reconcileCts.Cancel();
        if (_reconcileLoop is { } loop)
        {
            try { await loop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (Exception) { /* 对账循环退出超时——继续资源释放 */ }
        }
        _reconcileCts.Dispose();
        if (_discovery is not null)
        {
            try { await _discovery.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger?.LogWarning("TierRaftHost 发现循环释放异常：{Message}", ex.Message); }
        }

        // 各组装配链（每组内部分段有界——raft→apply→swarm→wal）
        foreach (var group in _groups.Values)
        {
            try { await group.Node.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger?.LogWarning("TierRaftHost 组 {Group} 释放异常：{Message}", group.GroupId, ex.Message); }
        }
        _groups.Clear();
        await _groupHost.DisposeAsync().ConfigureAwait(false);   // 组路由注销（§8.3：传输晚于宿主）
        if (_transportOwner is not null)
            await _transportOwner.DisposeAsync().ConfigureAwait(false);   // owned 传输链尾（外部传输不释放）
    }

    /// <summary>组句柄实现（读面透传）。</summary>
    private sealed class TierRaftGroup(RaftGroupId id, TierRaftNode node) : ITierRaftGroup
    {
        public RaftGroupId GroupId { get; } = id;
        public TierRaftNode Node { get; } = node;
        public RaftStateMachine Raft => Node.Raft;
        public ClusterConfig Config => Node.Raft.Config;
    }
}
