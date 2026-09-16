using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Image;
using TC.Tier.Core.Execution;
using TC.Tier.Core.Logging;
using TC.Tier.Core.Net.Raft;
using TC.Tier.Core.Net.Swarm;
using TC.Tier.Products.Wal;

namespace TC.Tier.Products.Net.Node;

/// <summary>
/// TierRaftNode——raft × Tier 产品装配节点（D6 产品接线收口件）：存储（TierWal → TierWalRaftStore）、
/// 协议（RaftStateMachine + ApplyPipeline）、多源与反熵（SwarmSync + SwarmAntiEntropy）、
/// 宿主调度（快照压缩 + 反熵周期 + 换届接线）——一个入口装配完整产品节点。
/// <para>★ 装配链：TierWal.Builder → TierWalRaftStore → ApplyPipeline(业务状态机) → RaftStateMachine →
///   传输（调用方组装根——ClusterTransport/InProcess 皆可）；宿主循环承载三件调度：</para>
/// <para> ① 快照压缩（全角色）：日志增长 ≥ <see cref="TierRaftNodeOptions.SnapshotGrowthThresholdEntries"/>
///   → <see cref="TierWal.SnapshotAsync"/>（一体：镜像 [Head..N₀] + 截头——raft 日志压缩惯例）；</para>
/// <para> ② 快照发布（全角色，Swarm 装配时）：N₀ 变更 → 块化内容挂源（holder 侧——供给拉取与对账）；</para>
/// <para> ③ 反熵（仅 leader 发起、对端轮转——SwarmOptions.AntiEntropyInterval 契约）：
///   与对端比对已发布快照内容 → 漂移块定向修复（机制 = <see cref="SwarmAntiEntropy.RunOnceAsync"/>）；</para>
/// <para>★ 换届接线：<see cref="RaftStateMachine.LeaderChanged"/> false → <see cref="SwarmSync.OnLeaderLost"/>
/// （持有表清空）+ 反熵停发；true → 恢复发起资格。非 leader 持基线时向已知 leader 上报（启动/换届触发③）。</para>
/// </summary>
public sealed class TierRaftNode : IAsyncDisposable
{
    private readonly TierWal _wal;
    private readonly TierWalRaftStore _store;
    private readonly ApplyPipeline _apply;
    private readonly RaftStateMachine _raft;
    private readonly SwarmSync? _swarm;
    private readonly SnapshotSwarmSync? _snapshotSwarm;
    private readonly SwarmAntiEntropy? _antiEntropy;
    private readonly ClusterConfig _config;
    private readonly Membership _membership;   // 二期-D8：退役编排的配置移除面
    private readonly TierRaftNodeOptions _options;
    private readonly TimeProvider _clock;   // 时钟供给源（时钟缝 件一 P1——宿主循环/快照限速窗）
    private readonly NodeId[] _peers;
    private readonly ILogger? _logger;
    private readonly IFileSystem _fs;   // 二期-F10：组合根 fs（备份数据面入口——RootSpaceImage 采集）

    private readonly CancellationTokenSource _hostCts = new();
    private Task? _hostLoop;
    private readonly IAsyncDisposable? _transportOwner;   // 装配器内建传输（随节点链尾收尾——调用方注入 = null）
    private int _isLeader;
    private int _disposed;   // DisposeAsync 幂等守卫
    private long _publishedSnapshotIndex = -1;   // 已发布内容的覆盖点（-1 = 未发布）
    private ISwarmBlockSource? _publishedSource;
    private SwarmManifest? _publishedManifest;
    private int _peerCursor;
    private NodeId? _announcedLeader;            // 基线已上报的 leader（换届变更即重报）
    private long _lastAntiEntropyTicks;
    private long _lastSnapshotTicks;   // 二期-F4：快照限速窗起算（TickCount64 ms）

    private TierRaftNode(NodeId id, TierWal wal, TierWalRaftStore store, ApplyPipeline apply, RaftStateMachine raft,
        SwarmSync? swarm, SnapshotSwarmSync? snapshotSwarm, ClusterConfig config,
        TierRaftNodeOptions options, ILogger? logger, IAsyncDisposable? transportOwner, IFileSystem fs,
        RaftGroupId groupId = default)
    {
        _wal = wal;
        _store = store;
        _apply = apply;
        _raft = raft;
        _swarm = swarm;
        _snapshotSwarm = snapshotSwarm;
        _antiEntropy = swarm is not null ? new SwarmAntiEntropy(swarm, logger) : null;
        _config = config;
        _options = options;
        _clock = options.Clock;   // 时钟供给源（时钟缝 件一 P1）
        _peers = config.Members.Where(m => m.Id != id).Select(m => m.Id).ToArray();
        _logger = logger;
        _transportOwner = transportOwner;
        _fs = fs;
        GroupId = groupId;
        Id = id;
        Wal = wal;
        Raft = raft;
        Apply = apply;
        _membership = new Membership(raft);
        _lastSnapshotTicks = _clock.GetMsTimestamp();   // 二期-F4：限速窗自启动起算（首个自动压缩也受窗约束）
        raft.LeaderChanged += OnLeaderChanged;
    }

    /// <summary>节点标识。</summary>
    public NodeId Id { get; }

    /// <summary>组标识（二期-I6——多组装配区分；单组 = Empty）。</summary>
    public RaftGroupId GroupId { get; }

    /// <summary>TierWal 存储（读面/快照/截断进阶操作入口）。</summary>
    public TierWal Wal { get; }

    /// <summary>raft 状态机（复制/提交/角色面）。</summary>
    public RaftStateMachine Raft { get; }

    /// <summary>apply 管道（状态机重建/位点推进）。</summary>
    public ApplyPipeline Apply { get; }

    /// <summary>多源同步组件（null = 未装配）。</summary>
    public SwarmSync? Swarm => _swarm;

    /// <summary>组合根文件系统（二期-F10 备份数据面入口——RootSpaceImage 采集/网络镜像传输）。</summary>
    public IFileSystem FileSystem { get; }

    /// <summary>
    /// 备份（二期-F10——一致性点冻结内 Fs 根空间镜像采集）：append 门内冻结写入 →
    /// <see cref="RootSpaceImage.Capture"/>（TCA1 帧 + 摘要对账）写入归档流——数据面走 Fs（裁定③）。
    /// </summary>
    /// <param name="archive">归档流（调用方提供——文件/网络镜像传输均骨架兼容）。</param>
    /// <param name="ct">取消令牌（append 门内——取消即放弃本次备份，节点不受影响）。</param>
    /// <returns>镜像摘要（条目/帧/CRC——恢复侧对账校验用）。</returns>
    public async ValueTask<ImageSummary> BackupToAsync(Stream archive, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(archive);
        // ★ 一致性点冻结：append 门内无并发写——镜像即持久化尾时刻的一致性切片
        var summary = await _raft.RunUnderAppendGateAsync((ct0) =>
            ValueTask.FromResult(RootSpaceImage.Capture(_fs, archive, new ImageOptions())), ct).ConfigureAwait(false);
        _logger?.LogInformation("TierRaftNode {Id} 备份完成：entries={Entries} frames={Frames} bytes={Bytes}",
            Id, summary.EntryCount, summary.FrameCount, summary.RawBytes);
        return summary;
    }

    /// <summary>恢复（二期-F10——归档流解封入目标 fs；摘要返回供调用方对账——随后即可
    /// 以目标 fs 装配节点/组）。静态数据面（Fs 能力透传——RootSpaceImage.Restore）。</summary>
    /// <param name="archive">归档流（<see cref="BackupToAsync"/> 产出的 TCA1 帧流——读取至尾）。</param>
    /// <param name="destinationFs">目标文件系统（解封落点——随后可据此装配节点/组）。</param>
    /// <returns>镜像摘要（条目/帧/CRC——与备份侧对账校验用）。</returns>
    public static ImageSummary RestoreArchive(Stream archive, IFileSystem destinationFs)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(destinationFs);
        return RootSpaceImage.Restore(archive, destinationFs);
    }

    /// <summary>成员资格编排（二期-D8——Add/Remove/Promote 的封装面）。</summary>
    public Membership Membership => _membership;

    /// <summary>统一状态导出（二期-D1 NETGAP-005——healthz/readyz/管理面数据源）：
    /// Role/LeaderId/Term/Commit/Applied/日志尾/快照覆盖点/成员复制进度 + 探针语义。</summary>
    /// <returns>当前 raft 状态快照（管理面/探针的数据源——字段语义见 <see cref="RaftStateSnapshot"/>）。</returns>
    public RaftStateSnapshot GetStateSnapshot() => _raft.GetStateSnapshot();

    /// <summary>启动完整节点：TierWal 恢复 → 存储适配 → apply 管道 → raft（选举/复制循环）→ 宿主调度循环。
    /// 薄壳（兼容存量调用/测试/探针——签名不变）；传输组装根收口走 <see cref="TierRaftNodeBuilder"/>。</summary>
    /// <param name="id">节点标识（须 ∈ config.Members）。</param>
    /// <param name="fs">组合根文件系统（每节点私有卷——TierFs 组合根）。</param>
    /// <param name="transport">本节点传输（ClusterTransport / InProcess 注册代理）。</param>
    /// <param name="config">集群配置。</param>
    /// <param name="machine">业务状态机（ApplyAsync——日志即状态机）。</param>
    /// <param name="options">装配配置（缺省 = 产品默认）。</param>
    /// <param name="logger">日志。</param>
    /// <returns>已启动的完整产品节点（装配完毕，宿主调度循环已运行）。</returns>
    public static Task<TierRaftNode> StartAsync(NodeId id, IFileSystem fs, IProtocolTransport transport,
        ClusterConfig config, IStateMachine machine, TierRaftNodeOptions? options = null, ILogger? logger = null)
        => StartCoreAsync(id, fs, transport, config, machine, options, logger, transportOwner: null);

    /// <summary>核心装配（装配器 <see cref="TierRaftNodeBuilder"/> 同入口——transportOwner 非空 =
    /// 传输由装配器内建组装，生命周期随节点 DisposeAsync 链尾收尾）。</summary>
    /// <param name="id">节点标识（须 ∈ config.Members）。</param>
    /// <param name="fs">组合根文件系统（每节点私有卷——TierFs 组合根）。</param>
    /// <param name="transport">本节点传输（ClusterTransport / InProcess 注册代理 / NodeEndpoint）。</param>
    /// <param name="config">集群配置。</param>
    /// <param name="machine">业务状态机（ApplyAsync——日志即状态机）。</param>
    /// <param name="options">装配配置（null = 产品默认）。</param>
    /// <param name="logger">日志（可空）。</param>
    /// <param name="groupId">raft 组标识（默认值仅供占位——装配面显式传入）。</param>
    /// <param name="transportOwner">装配器内建传输所有权（非空 = 随节点 DisposeAsync 链尾收尾；调用方注入 = null）。</param>
    /// <returns>已启动的完整产品节点（TierWal/apply/raft/宿主循环装配完毕）。</returns>
    internal static async Task<TierRaftNode> StartCoreAsync(NodeId id, IFileSystem fs, IProtocolTransport transport,
        ClusterConfig config, IStateMachine machine, TierRaftNodeOptions? options, ILogger? logger,
        IAsyncDisposable? transportOwner, RaftGroupId groupId = default)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(machine);
        options ??= TierRaftNodeOptions.Default;
        EnableHighResolutionTimer(options.HighResolutionTimer, logger);

        logger?.LogInformation("装配① TierWal 启动开始");
        var wal = await options.Wal.Builder(fs).StartAsync().ConfigureAwait(false);
        logger?.LogInformation("装配① TierWal 就绪");
        var store = new TierWalRaftStore(wal);
        await store.InitializeAsync().ConfigureAwait(false);

        // ★ 装配② Swarm 先于 raft——快照安装传输面须在引擎构造时注入（spec-03 §2）：
        //    leader 侧压缩推进 N₀ 后，落后 follower 越过快照边界必须走 InstallSnapshot 追赶——
        //    未装配 = 安装禁用 = nextIndex 停滞在边界外永不收敛（MultiNode@20 实测）。
        SwarmSync? swarm = null;
        SnapshotSwarmSync? snapshotSwarm = null;
        if (options.Swarm is { } swarmOptions)
        {
            logger?.LogInformation("装配② Swarm 构造开始");
            swarm = new SwarmSync(transport, swarmOptions, logger);
            await swarm.StartAsync().ConfigureAwait(false);
            snapshotSwarm = new SnapshotSwarmSync(swarm, store, logger);
            logger?.LogInformation("装配② Swarm 就绪");
        }
        var snapshotTransfer = snapshotSwarm is not null
            ? new SnapshotSwarmTransfer(transport, snapshotSwarm, store, options.SnapshotBlockSize, logger)
            : null;

        logger?.LogInformation("装配③ apply/raft 构造开始");
        var apply = new ApplyPipeline(store, machine, options.Apply);
        var raft = new RaftStateMachine(id, store, transport, apply, options.Raft, snapshotTransfer, logger,
            groupId: groupId);
        apply.SetConfigCallback(raft.PostConfigChanged);
        await apply.StartAsync().ConfigureAwait(false);
        logger?.LogInformation("装配③ apply 就绪");
        await raft.StartAsync(config).ConfigureAwait(false);
        logger?.LogInformation("装配④ raft 循环已启动");

        var node = new TierRaftNode(id, wal, store, apply, raft, swarm, snapshotSwarm, config, options, logger, transportOwner, fs);
        node.StartHostLoop();
        logger?.LogInformation("TierRaftNode {Id} started（swarm={Swarm} antiEntropy={AntiEntropy} snapshotThreshold={Snapshot}）",
            id, swarm is not null, options.AntiEntropyInterval, options.SnapshotGrowthThresholdEntries);
        return node;
    }

    // ★ 高精度定时环境（进程级旋钮，契约见 <see cref="TierRaftNodeOptions.HighResolutionTimer"/>）：
    //   生产拓扑 1 节点 = 1 进程，由装配入口承担进程环境责任；多节点同进程（测试拓扑）幂等只提一次。
    //   Windows 默认 15.6ms 定时器量子把全部时序敏感等待钉在节拍粒度上（Sleep/Delay/Timer 唤醒取整
    //   + TickCount64 步进——心跳 50ms 实发 ~62ms、选举窗下界 +5ms、组提交门 0→15.6 跳变），提 1ms
    //   后全部收敛 ~1ms。进程生命周期生效不配对 timeEndPeriod（引用计数无害，进程退出统一清理）；
    //   Linux 原生高精度，no-op。
    private static int _highResolutionTimerEnabled;

    private static void EnableHighResolutionTimer(bool enabled, ILogger? logger)
    {
        if (!enabled || !OperatingSystem.IsWindows()) return;
        if (Interlocked.Exchange(ref _highResolutionTimerEnabled, 1) == 1) return;
        var rc = timeBeginPeriod(1);
        logger?.LogInformation("timeBeginPeriod(1) rc={ReturnCode}（Windows 定时器分辨率 15.6ms → 1ms）", rc);
    }

    [System.Runtime.InteropServices.DllImport("winmm.dll", ExactSpelling = true)]
    private static extern int timeBeginPeriod(int ms);

    private void StartHostLoop()
    {
        _hostLoop = Task.Run(() => HostLoopAsync(_hostCts.Token));
    }

    /// <summary>换届接线：失去 leadership → 持有表清空（多源面）+ 反熵停发（循环内 role 门自然停）。</summary>
    /// <param name="isLeader">本节点是否当选 leader。</param>
    private void OnLeaderChanged(bool isLeader)
    {
        Volatile.Write(ref _isLeader, isLeader ? 1 : 0);
        if (!isLeader)
        {
            _swarm?.OnLeaderLost();
            _announcedLeader = null;   // 换届后向新 leader 重报基线（触发③）
        }
    }

    /// <summary>宿主调度循环：①快照压缩（全角色）②快照发布（全角色/Swarm 装配）③反熵（leader）④基线上报。
    /// 单轮异常吞并续跑（调度循环必须存活——错误经日志观测）。</summary>
    /// <param name="ct">取消令牌（DisposeAsync 触发——循环退出）。</param>
    private async Task HostLoopAsync(CancellationToken ct)
    {
        var tick = _options.HostLoopInterval;
        if (tick <= TimeSpan.Zero) tick = TimeSpan.FromSeconds(30);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _clock.Delay(tick, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await RunHostTickAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"TierRaftNode {{Id}} 宿主调度单轮异常（续跑）：{ex.Message}");
            }
        }
    }

    private async ValueTask RunHostTickAsync(CancellationToken ct)
    {
        // ① 快照压缩宿主调度（全角色）：日志增长 ≥ 阈值 → 一体快照+截头（N₀ = PersistedIndex）
        // ★ T6 追平守卫：apply 必须已追平持久化尾才允许压缩——N₀=persisted 越过 applied 时，
        //   快照区吞掉待 apply 条目（(applied, N₀] 只存于快照流、日志区已截去），apply 读越界
        //   收敛后跳过它们=永久停滞（取证实锤：p=25/n0=25/c=24/applied=24，入站 append 链
        //   随之冻结=心跳停=换届风暴）。追平守卫下 N₀ ≤ applied，结构无间隙；apply 滞后只把
        //   压缩推迟一个宿主 tick（apply 常态追平，等待亚秒级）。
        // ★ 二期-F4：限速窗（SnapshotMinInterval——两次快照最小间隔）+ 压缩准入钩子
        var cooldownOk = _options.SnapshotMinInterval <= TimeSpan.Zero
            || _clock.GetMsTimestamp() - Volatile.Read(ref _lastSnapshotTicks)
                >= (long)_options.SnapshotMinInterval.TotalMilliseconds;
        var growthCrossed = _options.SnapshotGrowthThresholdEntries > 0
            && _wal.AllocatedIndex - _wal.SnapshotIndex >= _options.SnapshotGrowthThresholdEntries;
        if (cooldownOk && growthCrossed && _apply.AppliedIndex >= _wal.PersistedIndex)
        {
            if (_options.SnapshotShouldCompactHook is { } hook
                && !hook(_wal.AllocatedIndex, _wal.SnapshotIndex, _apply.AppliedIndex))
            {
                _logger?.LogDebug("TierRaftNode {Id} 宿主快照压缩——准入钩子跳过本轮", Id);
                return;
            }
            // ★ T6 结构修复——压缩经 followerAppendGate + store 门双串行：直调 _wal.SnapshotAsync
            //   与 AE/快照安装的日志操作竞态（store 撞截断头抛异常→拒收→重发风暴）且绕过适配器
            //   _snapshotIndex 视图同步（快照覆盖钳位守卫失准——MultiNode@20 不动点实测）。
            var n0 = await _raft.RunUnderAppendGateAsync((ct0) => _store.SnapshotAsync(ct0), ct).ConfigureAwait(false);
            Volatile.Write(ref _lastSnapshotTicks, _clock.GetMsTimestamp());
            _options.SnapshotCompletedHook?.Invoke(n0);   // 二期-F4：完成回调（保留期/GC 钩子）
            _logger?.LogInformation("TierRaftNode {Id} 宿主快照压缩：N₀={N0}", Id, n0);
        }

        if (_swarm is null || _snapshotSwarm is null) return;

        // ② 快照发布（全角色）：N₀ 变更 → 块化挂源（holder 侧——供给拉取与对账）
        var n0Now = _store.SnapshotIndex;
        if (n0Now > 0 && n0Now != Volatile.Read(ref _publishedSnapshotIndex))
        {
            var content = await SnapshotBlockizer.BuildContentAsync(_store, n0Now, _options.SnapshotBlockSize, ct)
                .ConfigureAwait(false);
            var source = new SnapshotSwarmBlockSource(content);
            _swarm.SetSource(source);
            _publishedSource = source;
            _publishedManifest = content.Manifest;
            Volatile.Write(ref _publishedSnapshotIndex, n0Now);
            _logger?.LogDebug("TierRaftNode {Id} 快照发布：N₀={N0} blocks={Blocks}", Id, n0Now, content.Manifest.BlockCount);
        }

        // ④ 基线上报（非 leader 持基线——启动/换届触发②③）：leader 已知且未上报过 → 单边声明
        var leader = _raft.LeaderId;
        if (leader is { } leaderId && leaderId != Id && _store.SnapshotIndex > 0 && _announcedLeader != leaderId)
        {
            try
            {
                await _snapshotSwarm.AnnounceBaselineAsync(leaderId, ct).ConfigureAwait(false);
                _announcedLeader = leaderId;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"TierRaftNode {{Id}} 基线上报失败（下次 tick 重试）：{ex.Message}");
            }
        }

        // ③ 反熵（仅 leader 发起、对端轮转——周期门 + 换届门）
        if (Volatile.Read(ref _isLeader) != 1
            || _antiEntropy is null
            || _options.AntiEntropyInterval <= TimeSpan.Zero
            || _publishedSource is null || _publishedManifest is null
            || _peers.Length == 0) return;
        var now = _clock.GetMsTimestamp();
        var intervalMs = (long)_options.AntiEntropyInterval.TotalMilliseconds;
        var last = Interlocked.Read(ref _lastAntiEntropyTicks);
        if (now - last < intervalMs) return;
        Interlocked.Exchange(ref _lastAntiEntropyTicks, now);

        var index = Interlocked.Increment(ref _peerCursor);
        var peer = _peers[index % _peers.Length];
        var repaired = await _antiEntropy.RunOnceAsync(_publishedSource, peer, _publishedManifest, ct).ConfigureAwait(false);
        if (repaired.Count > 0)
            _logger?.LogInformation("TierRaftNode {Id} 反熵对账：peer={Peer} 修复 {Count} 块", Id, peer, repaired.Count);
    }

    /// <summary>
    /// 快照压缩公开触发（二期-F4——引擎层公开触发）：绕过增长阈值直接压缩（运维/工具面）；
    /// 仍走 apply 追平守卫（≤ 持久化尾才压缩——T6 结构契约）与 append 门串行。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>新快照覆盖点 N₀。</returns>
    public async ValueTask<long> TriggerSnapshotAsync(CancellationToken ct = default)
    {
        var deadline = _clock.GetMsTimestamp() + 10_000;
        while (_apply.AppliedIndex < _wal.PersistedIndex)
        {
            if (_clock.GetMsTimestamp() >= deadline)
                throw new TimeoutException("快照触发等待 apply 追平超时（10s）。");
            await _clock.Delay(20, ct).ConfigureAwait(false);
        }
        var n0 = await _raft.RunUnderAppendGateAsync((ct0) => _store.SnapshotAsync(ct0), ct).ConfigureAwait(false);
        Volatile.Write(ref _lastSnapshotTicks, _clock.GetMsTimestamp());
        _options.SnapshotCompletedHook?.Invoke(n0);
        _logger?.LogInformation("TierRaftNode {Id} 手动快照触发：N₀={N0}", Id, n0);
        return n0;
    }

    /// <summary>
    /// 节点退役编排（二期-D8——decommission）：①在位 leader = 停新/排空（D3）+ 可选定向转让（D2）；
    /// ②raft 配置移除自身（leader 任内提案；follower 退役 = 由 leader 侧 RemoveMember 编排——
    /// 幸存者对账循环自动黑名单+断链该节点，§8.2）；③物理面清理由调用方 DisposeAsync 有界收尾。
    /// </summary>
    /// <param name="transferTarget">转让目标（null = 自降级 Resign；仅 leader 任内生效）。</param>
    /// <param name="drainWindow">排空窗（缺省 500ms）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>编排步骤走完（leader 排空/转让 + 在位时配置移除自身）即完成；物理面清理由调用方随后 DisposeAsync 有界收尾。</returns>
    public async ValueTask DeprovisionAsync(NodeId? transferTarget = null,
        TimeSpan? drainWindow = null, CancellationToken ct = default)
    {
        // ① 停新 + 排空 + 让位/转让（在位 leader；follower 无新可停——跳过）
        if (_raft.IsLeader)
            await _raft.DrainAsync(transferTarget, drainWindow, ct).ConfigureAwait(false);

        // ② raft 配置移除自身——★ 顺序契约：无转让形态下 drain 后本端仍在位（未 resign），
        //    此刻提案移除自身 → 配置条目 apply 触发 HandleConfigChanged 自降级（spec-04 §4）→
        //    幸存者重选举。带转让形态 = 转让已完成、本端已是 follower——配置移除由新 leader
        //    侧编排（Membership.RemoveMember/admin 面），幸存者对账循环自动黑名单+断链本节点。
        if (_raft.IsLeader && _raft.Config.Contains(Id))
            await _membership.RemoveMemberAsync(Id, ct).ConfigureAwait(false);

        _logger?.LogInformation("TierRaftNode {Id} 退役编排完成（transfer={Transfer}）", Id, transferTarget is not null);
    }

    /// <summary>停宿主循环 → raft → apply → 多源 → TierWal。
    /// ★ 每段有界（超时=资源滞留换安全——registry Shard.DisposeAsync 同款）：换届风暴下
    /// 复制 lane 可能滞留在 WAL 提交路径上（W4 persist 下环前的已知形态），Dispose 不因
    /// 单段滞留挂死。</summary>
    /// <returns>任务在各段有界释放（单段超时不阻断后续段）全部走完后完成。</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;   // 幂等（显式 + await using 双路径）
        _hostCts.Cancel();
        if (_hostLoop is { } loop)
        {
            try { await loop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (Exception) { /* 循环退出超时——继续资源释放 */ }
        }
        _hostCts.Dispose();

        await DisposeStageAsync("raft", _raft.DisposeAsync().AsTask()).ConfigureAwait(false);
        _membership.Dispose();
        await DisposeStageAsync("apply", _apply.DisposeAsync().AsTask()).ConfigureAwait(false);
        if (_swarm is not null)
            await DisposeStageAsync("swarm", _swarm.DisposeAsync().AsTask()).ConfigureAwait(false);
        await DisposeStageAsync("wal", _wal.DisposeAsync().AsTask()).ConfigureAwait(false);
        if (_transportOwner is not null)
            await DisposeStageAsync("transport", _transportOwner.DisposeAsync().AsTask()).ConfigureAwait(false);
    }

    /// <summary>分段有界释放（超时 LogWarning——资源滞留观测点）。</summary>
    /// <param name="stage">段名（raft/apply/swarm/wal/transport——日志观测用）。</param>
    /// <param name="task">待等待的释放任务（超时 10s 截断）。</param>
    private async ValueTask DisposeStageAsync(string stage, Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger?.LogWarning($"TierRaftNode {{Id}} {stage} 释放超时（资源滞留——W4 已知形态）");
        }
    }
}
