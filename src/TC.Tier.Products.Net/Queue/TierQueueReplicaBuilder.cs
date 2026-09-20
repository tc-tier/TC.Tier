using TC.Tier.Contracts.Meta;
using TC.Tier.Core.IO;
using TC.Tier.Core.Epochs;
using TC.Tier.Core.Execution;
using TC.Tier.Core.Logging;
using TC.Tier.Core.Net;
using TC.Tier.Core.Net.Raft;
using TC.Tier.Core.Net.Swarm;
using TC.Tier.Products.Net.Host;
using TC.Tier.Runtime.Structures.Ring.Contracts;
using TC.Tier.Products.Queue;

namespace TC.Tier.Products.Net.Queue;

/// <summary>
/// TierQueueReplica 装配配置（不可变 record——raft 面 + 副本行为面旋钮；缺省即产品形态）。
/// </summary>
public sealed record TierQueueReplicaOptions
{
    /// <summary>缺省配置。</summary>
    public static TierQueueReplicaOptions Default { get; } = new();

    /// <summary>辖权后台循环周期（到期记账扫描 + retention 提案的公共 tick）。</summary>
    public TimeSpan HomeSweepInterval { get; private init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>提案 NotLeader 重试上限（换届风暴下有界退避——超限抛终局）。</summary>
    public int ForwardRetryLimit { get; private init; } = 64;

    /// <summary>缓冲确认提交阈值（辖权本地簿记批满即提案 AckCmd；1 = 禁用缓冲）。</summary>
    public int BufferedAckBatchSize { get; private init; } = 1;

    /// <summary>With 链——辖权循环周期。</summary>
    /// <param name="interval">辖权后台循环周期。</param>
    /// <returns>新配置实例。</returns>
    public TierQueueReplicaOptions WithHomeSweepInterval(TimeSpan interval)
        => this with { HomeSweepInterval = interval };

    /// <summary>时钟供给源（故障注入面 件一——时钟缝 P1 落点；缺省 <see cref="TimeProvider.System"/> 行为零变化）。
    /// <para>辖权引导/提案退避/到期与保留扫描循环经本源驱动——假钟下由快进确定性触发。</para></summary>
    public TimeProvider Clock { get; private init; } = TimeProvider.System;

    /// <summary>With 链——时钟供给源。</summary>
    /// <param name="clock">时钟供给源（非空）。</param>
    /// <returns>替换 Clock 后的新实例。</returns>
    public TierQueueReplicaOptions WithClock(TimeProvider clock) => this with { Clock = clock };

    /// <summary>With 链——缓冲确认阈值。</summary>
    /// <param name="batchSize">缓冲确认提交阈值（1 = 禁用缓冲）。</param>
    /// <returns>新配置实例。</returns>
    public TierQueueReplicaOptions WithBufferedAckBatchSize(int batchSize)
        => this with { BufferedAckBatchSize = Math.Max(1, batchSize) };
}

/// <summary>
/// TierQueueReplica raft 装配参数（WithRaft 注入——组身份/初始配置/组持久卷）。
/// </summary>
public sealed record TierQueueReplicaRaftOptions
{
    /// <summary>raft 组 ID（一队列实例 = 一组——TierRaftHost 多组挂载）。</summary>
    public required RaftGroupId GroupId { get; init; }

    /// <summary>组初始集群配置（组内成员集合的事实源——三副本 HA 即三成员）。</summary>
    public required ClusterConfig Config { get; init; }

    /// <summary>组持久卷（raft WAL + 业务快照——重启后持久真相所在；队列引擎卷独立于构造器给定）。</summary>
    public required IFileSystem StateFs { get; init; }

    /// <summary>节点装配配置（null = 产品默认——本 Builder 追加 apply 节流关闭与快照导出钩子）。</summary>
    public TierRaftNodeOptions? NodeOptions { get; init; }

    /// <summary>复制恢复追平等待上限（Builder StartAsync 的 warmup 门——超时抛）。</summary>
    public TimeSpan WarmupTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// TierQueueReplica 构建者（tierqueue-replicated-spec §3——QueueBuilder 全注入面 +
/// .WithRaft(组/传输/peers)（TierRaftHost 多组挂载）三段式装配）。
/// <para>★ 队列引擎介质由调用方给定（<b>缺省建议 MemoryFileSystem</b>——复制版队列引擎为易失物化，
/// raft 日志 = 持久真相；重启经业务快照导入 + 增量重放重建）。幂等索引快照窗口说明：快照导入后
/// 判重窗口自导入点重建（快照前的幂等条目不进索引——at-least-once 契约不变，首地址语义窗口收窄）。</para>
/// </summary>
public sealed class TierQueueReplicaBuilder : IDisposable, IAsyncDisposable
{
    private readonly IFileSystem _queueFs;
    private readonly TierQueueOptions _options;
    private readonly TierQueueBuilder _coreBuilder;

    private TierQueueReplicaRaftOptions? _raft;
    private ITierQueue? _deadLetterTarget;
    private ILogger? _logger;
    private TierQueue? _core;
    private TaskSink? _snapshotExports;   // 快照导出任务组（压缩钩子受控异步——fire-and-forget 纪律）
    private bool _ownershipTransferred;

    /// <summary>构造（= 配置，零 IO）。</summary>
    /// <param name="queueFs">队列引擎介质（缺省建议 MemoryFileSystem——易失物化，持久真相在 raft 日志）。</param>
    /// <param name="options">TierQueue 选项（队列名/容量/治理/索引开关——语义面同本地档）。</param>
    public TierQueueReplicaBuilder(IFileSystem queueFs, TierQueueOptions options)
    {
        ArgumentNullException.ThrowIfNull(queueFs);
        ArgumentNullException.ThrowIfNull(options);
        _queueFs = queueFs;
        _options = options;
        _coreBuilder = new TierQueueBuilder(queueFs, options);
    }

    /// <summary>注入日志器（透传本地核心 + 副本面）。</summary>
    /// <param name="logger">日志器实例。</param>
    /// <returns>TierQueueReplicaBuilder（链式）。</returns>
    public TierQueueReplicaBuilder WithLogger(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _coreBuilder.WithLogger(logger);
        return this;
    }

    /// <summary>注入数据 Ring 装配工厂（QueueBuilder 全注入面透传）。</summary>
    /// <param name="factory">Ring 工厂。</param>
    /// <returns>TierQueueReplicaBuilder（链式）。</returns>
    public TierQueueReplicaBuilder WithRingFactory(TierQueueRingFactory factory)
    {
        _coreBuilder.WithRingFactory(factory);
        return this;
    }

    /// <summary>注入组状态 meta 装配工厂。</summary>
    /// <param name="factory">组 meta 工厂。</param>
    /// <returns>TierQueueReplicaBuilder（链式）。</returns>
    public TierQueueReplicaBuilder WithGroupMetaFactory(TierQueueGroupMetaFactory factory)
    {
        _coreBuilder.WithGroupMetaFactory(factory);
        return this;
    }

    /// <summary>注入组注册表 metadata 工厂。</summary>
    /// <param name="factory">注册表 meta 工厂。</param>
    /// <returns>TierQueueReplicaBuilder（链式）。</returns>
    public TierQueueReplicaBuilder WithRegistryMetaFactory(TierQueueRegistryMetaFactory factory)
    {
        _coreBuilder.WithRegistryMetaFactory(factory);
        return this;
    }

    /// <summary>注入延迟 BTree 工厂。</summary>
    /// <param name="factory">延迟索引工厂。</param>
    /// <returns>TierQueueReplicaBuilder（链式）。</returns>
    public TierQueueReplicaBuilder WithDelayedIndexFactory(TierQueueDelayedIndexFactory factory)
    {
        _coreBuilder.WithDelayedIndexFactory(factory);
        return this;
    }

    /// <summary>注入幂等 HashIndex 工厂。</summary>
    /// <param name="factory">幂等索引工厂。</param>
    /// <returns>TierQueueReplicaBuilder（链式）。</returns>
    public TierQueueReplicaBuilder WithIdempotencyIndexFactory(TierQueueIdempotencyIndexFactory factory)
    {
        _coreBuilder.WithIdempotencyIndexFactory(factory);
        return this;
    }

    /// <summary>注入各组件的 StorageEngineOptions 变换器。</summary>
    /// <param name="factory">引擎选项变换器。</param>
    /// <returns>TierQueueReplicaBuilder（链式）。</returns>
    public TierQueueReplicaBuilder WithStorageOptionsFactory(TierQueueStorageOptionsFactory factory)
    {
        _coreBuilder.WithStorageOptionsFactory(factory);
        return this;
    }

    /// <summary>注入 Ring meta 策略工厂。</summary>
    /// <param name="factory">meta 策略工厂。</param>
    /// <returns>TierQueueReplicaBuilder（链式）。</returns>
    public TierQueueReplicaBuilder WithMetaPolicyFactory(
        MetaPolicyFactory<RingMetaHeader,
            RingMetaPayload> factory)
    {
        _coreBuilder.WithMetaPolicyFactory(factory);
        return this;
    }

    /// <summary>注入共享 epoch（组合域多结构共用读保护域）。</summary>
    /// <param name="epoch">共享 epoch 实例。</param>
    /// <returns>TierQueueReplicaBuilder（链式）。</returns>
    public TierQueueReplicaBuilder WithEpoch(LightEpoch epoch)
    {
        _coreBuilder.WithEpoch(epoch);
        return this;
    }

    /// <summary>注入引擎 worker 调度器共享实例（#505——本地核心全部引擎共用一组线程；
    /// 多副本同进程/测试拓扑线程数恒定；所有权归注入方）。</summary>
    /// <param name="scheduler">共享调度器实例（如 <see cref="TC.Tier.Core.Execution.IsolatedTaskScheduler.Shared"/>）。</param>
    /// <returns>TierQueueReplicaBuilder（链式）。</returns>
    public TierQueueReplicaBuilder WithWorkerScheduler(TC.Tier.Core.Execution.IsolatedTaskScheduler scheduler)
    {
        _coreBuilder.WithWorkerScheduler(scheduler);
        return this;
    }

    /// <summary>注入 raft 装配参数（组身份/初始配置/组持久卷——必填才能 StartAsync）。</summary>
    /// <param name="raftOptions">raft 装配参数。</param>
    /// <returns>TierQueueReplicaBuilder（链式）。</returns>
    public TierQueueReplicaBuilder WithRaft(TierQueueReplicaRaftOptions raftOptions)
    {
        ArgumentNullException.ThrowIfNull(raftOptions);
        _raft = raftOptions;
        return this;
    }

    /// <summary>注入跨组死信目标（另一 raft 组的副本实例——死信写入经该组共识复制）。</summary>
    /// <param name="target">死信目标队列（null = 关闭档：达上限仅推进位点 + 告警）。</param>
    /// <returns>TierQueueReplicaBuilder（链式）。</returns>
    public TierQueueReplicaBuilder WithDeadLetterTarget(ITierQueue? target)
    {
        _deadLetterTarget = target;
        return this;
    }

    /// <summary>
    /// 一步到位：本地核心启动 → 复制状态机装配 → TierRaftHost 组挂载 → warmup（复制恢复追平）→ 就绪。
    /// </summary>
    /// <param name="host">已启动的多组宿主（TierRaftHost.StartAsync 已完成）。</param>
    /// <param name="replicaOptions">副本行为面配置（null = 缺省）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>就绪的 TierQueueReplica（提案/辖权/复制面全部可用）。</returns>
    /// <exception cref="InvalidOperationException">WithRaft 未注入 / 宿主未启动 / 重复启动。</exception>
    public async Task<TierQueueReplica> StartAsync(TierRaftHost host, TierQueueReplicaOptions? replicaOptions = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        var raft = _raft ?? throw new InvalidOperationException("WithRaft 未注入——复制版装配必须提供组身份/初始配置/组持久卷。");
        replicaOptions ??= TierQueueReplicaOptions.Default;
        _logger?.LogInformation("TierQueueReplicaBuilder 启动开始：queue={Queue} group={Group}",
            _options.QueueName, raft.GroupId);

        // ① 本地核心（确定性核心宿主——ring/组状态域/索引；易失物化介质由调用方给定）
        var core = await _coreBuilder.StartAsync(ct: ct).ConfigureAwait(false);
        _core = core;
        _ownershipTransferred = true;
        var port = (ITierQueueReplicationPort)core;

        // ② 复制状态机（业务快照落组持久卷——重启懒导入）
        var machine = new QueueStateMachine(port, raft.StateFs, "queue-state.snap", _logger);

        // ③ 节点装配：apply 节流关闭（appliedIndex 持久化归快照边界 S 自管——重启全量重放 or 懒导入跳过）
        //    + Swarm 快照安装面启用（各节点压缩点 N₀ 参差——落后副本越界后必须走安装追赶，
        //    未装配 = nextIndex 停滞在边界外永不收敛，MultiNode 实测形态）
        //    + 压缩完成钩子接快照导出（宿主循环保证 applied ≥ N₀ 后才压缩——导出边界 ≥ 压缩边界）。
        var nodeOptions = raft.NodeOptions ?? TierRaftNodeOptions.Default;
        if (nodeOptions.Swarm is null)
            nodeOptions = nodeOptions.WithSwarm(SwarmOptions.Default);
        var userCompletedHook = nodeOptions.SnapshotCompletedHook;   // 原钩子捕获（闭包防自引用）
        _snapshotExports = new TaskSink("queue-replica-snapshot-export", logger: _logger);
        nodeOptions = nodeOptions
            .WithApply(ApplyPipelineOptions.Default
                .WithPersistEvery(int.MaxValue)
                .WithPersistInterval(TimeSpan.FromDays(36500)))
            .WithSnapshotSchedule(nodeOptions.SnapshotMinInterval, nodeOptions.SnapshotShouldCompactHook,
                n0 =>
                {
                    _snapshotExports.SubmitFast((Func<CancellationToken, ValueTask>)(async ct
                        => await machine.ExportSnapshotAsync(ct).ConfigureAwait(false)));
                    userCompletedHook?.Invoke(n0);
                });

        // ④ TierRaftHost 组挂载（一队列实例 = 一 raft 组——spec 定案①）+ 组路由挂载
        //    （GroupReplicaRouter 通用面——传输绑定/组分发/提案转发归公共路由器，域号 0x60 队列）
        var group = await host.CreateGroupAsync(raft.GroupId, raft.Config, machine, nodeOptions, raft.StateFs, ct)
            .ConfigureAwait(false);
        var router = GroupReplicaRouter.GetOrCreate(host.Transport, QueueReplicaProtocol.Domain, _logger);

        var replica = new TierQueueReplica(core, port, machine, group, group.Node, router, host.Self,
            raft.GroupId, replicaOptions, _snapshotExports, _logger)
        {
            DeadLetterTarget = _deadLetterTarget,
        };
        router.Register(raft.GroupId, replica);

        // ⑤ warmup（复制恢复追平门——applied ≥ raft 恢复的提交水位；过后 ops 开放）
        //    ★ 标尺 = raft 自身 CommitIndex（非日志持久尾——重启后未提交尾部须等新 leader 心跳
        //    才提交，lone/follower 节点上等 PersistedIndex 是永不成真的等待）。
        //    ★ 先显式导入：快照在而零新日志时，懒导入永不触发——导入先行 + 幂等。
        await machine.ImportIfPendingAsync(ct).ConfigureAwait(false);
        var deadline = DateTime.UtcNow + raft.WarmupTimeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var commit = group.Raft.GetStateSnapshot().CommitIndex;
            if (machine.AppliedThrough >= commit)
                break;
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException(
                    $"复制恢复追平超时（applied={machine.AppliedThrough} ≥ commit={commit} 未达成）——WarmupTimeout={raft.WarmupTimeout}。");
            await Task.Delay(20, ct).ConfigureAwait(false);
        }
        replica.MarkReady();
        replica.StartHomeLoop();
        _logger?.LogInformation("TierQueueReplicaBuilder 启动完成：queue={Queue} group={Group} applied={Applied}",
            _options.QueueName, raft.GroupId, machine.AppliedThrough);
        return replica;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!_ownershipTransferred) _core?.Dispose();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (!_ownershipTransferred && _core is not null)
            await _core.DisposeAsync().ConfigureAwait(false);
    }
}
