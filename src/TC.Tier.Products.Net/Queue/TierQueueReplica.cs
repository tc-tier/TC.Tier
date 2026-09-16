using System.Collections.Concurrent;
using TC.Tier.Contracts.Lifecycle;
using TC.Tier.Contracts.Storage;
using TC.Tier.Core.Execution;
using TC.Tier.Core.Logging;
using TC.Tier.Core.Net.Raft;
using TC.Tier.Products.Net.Host;
using TC.Tier.Products.Net.Node;
using TC.Tier.Products.Queue;
using TC.Tier.Runtime.Transactions;

namespace TC.Tier.Products.Net.Queue;

/// <summary>
/// TierQueueReplica——TierQueue 的集群模式（tierqueue-replicated-spec：一队列实例 = 一 raft 组）。
/// <para>★ 分工：状态变更（Enqueue/Ack/组生命周期/到期记账/辖权/回收）经共识全序复制
/// （apply 确定性 ⇒ 全组同地址，消息 ID = LogicalAddress）；消费面经组辖权租约仲裁
/// （HomeCmd 经共识、GroupEpoch fencing——接管即 +1，旧辖权迟交确定性拒绝）；Dequeue 纯本地读零共识。</para>
/// <para>★ 提案路由：本端为 raft leader 直提；否则 NotLeaderException 携 leader 经转发路由送达 leader
/// （客户端零感知——"任意节点可写"读面）；非辖权 Dequeue/Ack 抛 <see cref="NotGroupHomeException"/>
/// （异常驱动重定向——客户端策略缺省）。</para>
/// <para>★ 恢复语义：副本队列引擎为易失物化（raft 日志 = 持久真相）——冷启动经业务快照导入
/// （ring 检查点帧 + 组 meta 镜像）+ 增量重放续接；Enqueue 未 applied = 不可见（提交前不可见——
/// 比本地 FireAndForget 更强）；pending 纯内存 = at-least-once 重放窗继承。</para>
/// </summary>
public sealed class TierQueueReplica : ITierQueueReplica, IGroupProposalHandler
{
    /// <summary>本地核心（确定性核心宿主——ring/组状态域/索引装配）。</summary>
    private readonly TierQueue _core;
    private readonly ITierQueueReplicationPort _port;
    private readonly QueueStateMachine _machine;
    private readonly ITierRaftGroup _group;
    private readonly TierRaftNode _node;
    private readonly GroupReplicaRouter _router;
    private readonly NodeId _self;
    private readonly RaftGroupId _groupId;
    private readonly TierQueueReplicaOptions _options;
    private readonly TimeProvider _clock;   // 时钟供给源（时钟缝 件一 P1——扫描循环/退避统一）
    private readonly ILogger? _logger;
    // === 辖权缓冲确认（辖权本地簿记——批满/flush 提案 AckCmd；崩溃丢失 = at-least-once 重投）===
    private readonly ConcurrentDictionary<string, BufferedAcks> _buffers = new();

    private readonly CancellationTokenSource _cts = new();
    private ulong _correlation;   // 命令关联计数（等待者键——节点内在途唯一即可）
    private readonly TaskSink _snapshotExports;   // 快照导出任务组（压缩钩子提交——随副本 drain）
    private Task? _homeLoop;   // 辖权后台循环（到期记账扫描 + retention 提案——仅辖权组生效）
    private Task? _bootstrap;   // 引导提案任务（持有引用——受控异步，异常体内已吞）
    private int _disposed;
    private bool _ready;   // Builder warmup 完成置位（ops 前置门）

    internal TierQueueReplica(TierQueue core, ITierQueueReplicationPort port, QueueStateMachine machine,
        ITierRaftGroup group, TierRaftNode node, GroupReplicaRouter router, NodeId self,
        RaftGroupId groupId, TierQueueReplicaOptions options, TaskSink snapshotExports, ILogger? logger)
    {
        _clock = options.Clock;   // 时钟供给源（时钟缝 件一 P1）
        _core = core;
        _port = port;
        _machine = machine;
        _group = group;
        _node = node;
        _router = router;
        _self = self;
        _groupId = groupId;
        _options = options;
        _snapshotExports = snapshotExports;
        _logger = logger;
        _machine.GroupHomeChanged += OnHomeChanged;
        _node.Raft.LeaderChanged += OnLeaderChanged;
    }

    /// <summary>跨组死信目标（另一 raft 组的副本实例——builder 接线；null = DLQ 关闭档）。</summary>
    public ITierQueue? DeadLetterTarget { get; internal set; }

    /// <summary>raft 组句柄（读面——管理/诊断/测试触发快照压缩）。</summary>
    public ITierRaftGroup RaftGroup => _group;

    /// <summary>复制状态机（辖权表/结果表/快照——测试与运维读面）。</summary>
    public QueueStateMachine Machine => _machine;

    /// <inheritdoc/>
    public NodeId GroupHome(string group) => _machine.GetHome(group);

    /// <inheritdoc/>
    public event Action<string, NodeId>? GroupHomeChanged;

    /// <inheritdoc/>
    public async ValueTask MoveGroupHomeAsync(string group, NodeId target, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        // 组存在性不本地预检（滞后副本视图会误拒）——确定性拒绝归 apply 侧终判
        var result = await ProposeAsync(new QueueHomeCommand
        {
            Correlation = NextCorrelation(),
            Name = NameBytes(group),
            NewHome = target,
        }, ct).ConfigureAwait(false);
        await ThrowIfFailedAsync(result).ConfigureAwait(false);
    }

    /// <summary>接管组辖权（target = 本端——运维/测试驱动租约迁移的便捷面；组代次 +1 fencing 递增）。</summary>
    /// <param name="group">消费组名。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>接管（HomeCmd applied）后完成。</returns>
    public ValueTask TakeoverGroupHomeAsync(string group, CancellationToken ct)
        => MoveGroupHomeAsync(group, _self, ct);

    private void OnHomeChanged(string group, NodeId home) => GroupHomeChanged?.Invoke(group, home);

    /// <summary>换届接线：当选 leader 且 $default 辖权未定 → 提案自领（引导唯一提案方——leader 恒一）。
    /// ★ 延迟复检：新 leader 的 replay 可能滞后（本地视图误判辖权为空）——追平后复查再提案，
    /// 防对已设辖权的组发起抢夺搬迁（HomeCmd 恒 epoch+1，误搬 = 无谓 fencing 消费者）。</summary>
    /// <param name="isLeader">本端是否当选。</param>
    private void OnLeaderChanged(bool isLeader)
    {
        if (!isLeader || Volatile.Read(ref _disposed) != 0)
            return;
        Volatile.Write(ref _bootstrap,
            Task.Run(async () =>
            {
                try
                {
                    await _clock.Delay(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
                    if (Volatile.Read(ref _disposed) != 0)
                        return;
                    if (_machine.GetHome(TierQueue.DefaultGroupName) != NodeId.Empty)
                        return;   // 辖权已定（追平后复查）——不抢夺
                    if (!_port.GetGroupNames().Contains(TierQueue.DefaultGroupName))
                        return;
                    var cmd = new QueueHomeCommand
                    {
                        Correlation = NextCorrelation(),
                        Name = NameBytes(TierQueue.DefaultGroupName),
                        NewHome = _self,
                    };
                    await ProposeAsync(cmd, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10))
                        .ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 引导尽力——换届/竞争下新 leader 会重试（leader 恒一 = 恰一提案方）
                }
            }));
    }

    // ═══════════════════════════════════════════════════════════════════
    // 提案路由（状态变更唯一入口——leader 直提 / 转发 leader）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>命令提案核心（结果表领取 + leader 路由——转发路由器同入口）：命令编码经
    /// [WireMessage] 生成 Codec 单点，CorrId = 节点内单调计数（等待者键节点内在途唯一即可）。</summary>
    /// <param name="message">命令消息。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>apply 终局结果。</returns>
    internal async ValueTask<GroupApplyResult> ProposeCoreAsync(QueueCommand message, CancellationToken ct)
    {
        var waiter = _machine.RegisterWaiter(message.Correlation);
        try
        {
            var index = await _group.Raft.ReplicateAsync(QueueCommandCodec.Encode(message), ct)
                .ConfigureAwait(false);
            var result = await waiter.Task.WaitAsync(ct).ConfigureAwait(false);
            // 入口 read-your-writes（spec ④——apply 后本地即见）：leader 直提时 index 已随 waiter 完成；
            // 本端为 follower 时 apply 滞后于 leader——本地水位越过后再返回
            await _machine.WaitForAppliedAsync(index, ct).ConfigureAwait(false);
            return result;
        }
        catch
        {
            _machine.ForgetWaiter(message.Correlation);
            throw;
        }
    }

    /// <summary>组提案受理面（IGroupProposalHandler——路由器入站按组分发到此）：入站命令经生成
    /// Codec 防御式解码（未知 tag/截断/超上限 = 确定性拒绝——对端命令畸形不拖入超时）。</summary>
    /// <param name="command">命令字节。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>apply 终局结果。</returns>
    ValueTask<GroupApplyResult> IGroupProposalHandler.ProposeAsync(ReadOnlyMemory<byte> command, CancellationToken ct)
    {
        if (!QueueCommandCodec.TryDecode(command.Span, out var message))
            return ValueTask.FromResult(new GroupApplyResult(GroupApplyStatus.Rejected, default, 0,
                "复制命令畸形（未知 tag/截断/超上限）"));
        return ProposeCoreAsync(message, ct);
    }

    /// <summary>提案（leader 直提 + 非 leader 经转发路由重定向——有界重试防换届风暴热循环）。</summary>
    /// <param name="message">命令消息。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>apply 终局结果。</returns>
    private async ValueTask<GroupApplyResult> ProposeAsync(QueueCommand message, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await ProposeCoreAsync(message, ct).ConfigureAwait(false);
            }
            catch (NotLeaderException e) when (attempt < _options.ForwardRetryLimit && !ct.IsCancellationRequested)
            {
                var redirected = false;
                if (e.Leader is { } leader && leader != _self)
                {
                    try
                    {
                        var forwarded = await _router.ForwardAsync(leader, _groupId, QueueCommandCodec.Encode(message), ct).ConfigureAwait(false);
                        // 入口 read-your-writes（转发链路——应答回传 index，入口本地水位越过后再返回）
                        if (forwarded.AppliedIndex > 0)
                            await _machine.WaitForAppliedAsync(forwarded.AppliedIndex, ct).ConfigureAwait(false);
                        return forwarded;
                    }
                    catch (NetIOException)
                    {
                        redirected = true;   // 旧视图 leader 已不可达（换届中）——短退避重选
                    }
                    catch (TimeoutException)
                    {
                        redirected = true;
                    }
                }
                if (!redirected)
                    await _clock.Delay(50, ct).ConfigureAwait(false);   // leader 未知/选举中——短退避重试
                else
                    await _clock.Delay(50, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>失败结果转异常（Ok = null；StaleEpoch = 迟交拒绝——产品异常形态归队列层）。</summary>
    /// <param name="result">apply 结果。</param>
    /// <returns>Ok = null；失败 = 对应异常实例。</returns>
    private static InvalidOperationException? ToException(GroupApplyResult result) => result.Status switch
    {
        GroupApplyStatus.Ok => null,
        GroupApplyStatus.StaleEpoch => new StaleDeliveryException(result.Address, 0, 0),
        _ => new InvalidOperationException(result.Error ?? "命令被确定性拒绝"),
    };

    /// <summary>失败结果抛出（Ok = no-op）。</summary>
    /// <param name="result">apply 结果。</param>
    /// <returns>结果异常时抛出（StaleDelivery/确定性拒绝）。</returns>
    private static ValueTask ThrowIfFailedAsync(GroupApplyResult result)
    {
        if (ToException(result) is { } e)
            throw e;
        return ValueTask.CompletedTask;
    }

    /// <summary>分配命令关联 ID（单调递增——重放在 warmup 前完成，无碰撞窗口）。</summary>
    private ulong NextCorrelation() => Interlocked.Increment(ref _correlation);

    /// <summary>组名 → UTF8 blob。</summary>
    private static ReadOnlyMemory<byte> NameBytes(string name) => System.Text.Encoding.UTF8.GetBytes(name);

    /// <summary>就绪与释放守卫（ops 前置）。</summary>
    private void EnsureReady()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!Volatile.Read(ref _ready))
            throw new InvalidOperationException("TierQueueReplica 未完成 warmup（复制恢复未追平）——Builder StartAsync 返回后再操作。");
    }

    /// <summary>辖权守卫（消费面 ops 前置——非辖权确定性重定向）。</summary>
    /// <param name="group">消费组名。</param>
    private void EnsureGroupHome(string group)
    {
        var home = _machine.GetHome(group);
        if (home != _self)
            throw new NotGroupHomeException(group, home);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 生产（EnqueueCmd 复制——提案侧策略 + 命令定案，apply 确定性）
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public ValueTask<EnqueueResult> EnqueueAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
        => EnqueueAsync(new EnqueueOptions(), payload, ct);

    /// <inheritdoc/>
    public async ValueTask<EnqueueResult> EnqueueAsync(EnqueueOptions opt, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(opt);
        EnsureReady();
        await _port.EnforceBacklogAsync(ct).ConfigureAwait(false);   // 积压治理（本地视图——Block/Reject 语义继承）

        bool delayed = false;
        long due = 0;
        if (opt.DueTime is { } dt && dt > DateTime.UtcNow.Ticks)
        {
            if (_port.DelayWindow is not { } maxDelay)
                throw new InvalidOperationException("延迟入队需要 Options.Delayed 开启（就绪索引装配前提）");
            if (dt - DateTime.UtcNow.Ticks > maxDelay.Ticks)
                throw new InvalidOperationException(
                    $"延迟跨度超 MaxDelay（{maxDelay}——未就绪消息钉住截断的锚定治理，spec §4.3）；超长延迟用独立队列实例隔离");
            delayed = true;
            due = dt;
        }

        bool idempotent = false;
        long pid = 0, seq = 0;
        if (opt.ProducerId.HasValue || opt.Seq.HasValue)
        {
            if (!_port.IdempotencyEnabled)
                throw new InvalidOperationException("幂等生产需要 Options.Idempotency 开启（(ProducerId, Seq) 判重）");
            if (!opt.ProducerId.HasValue || !opt.Seq.HasValue)
                throw new InvalidOperationException("幂等生产需要 ProducerId 与 Seq 成对给出");
            pid = opt.ProducerId.Value;
            seq = opt.Seq.Value;
            idempotent = true;
            // 入口快路径（本地已应用视图判重——未命中仍由 apply 终判，确定性归状态机）
            if (_port.TryFindIdempotent(pid, seq, out var existing))
                return new EnqueueResult(existing);
        }

        var command = new QueueEnqueueCommand
        {
            Correlation = NextCorrelation(),
            Delayed = delayed,
            DueTime = due,
            Idempotent = idempotent,
            ProducerId = pid,
            Seq = seq,
            Payload = payload,
        };
        _logger?.LogInformation("[tqr-diag] Enqueue: gid={Gid:X16} len={Len}", _groupId.Value, payload.Length);
        var result = await ProposeAsync(command, ct).ConfigureAwait(false);
        if (result.Status != GroupApplyStatus.Ok)
            await ThrowIfFailedAsync(result).ConfigureAwait(false);
        return new EnqueueResult(result.Address);
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<EnqueueResult>> EnqueueBatchAsync(
        IReadOnlyList<ReadOnlyMemory<byte>> payloads, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        var results = new EnqueueResult[payloads.Count];
        for (var i = 0; i < payloads.Count; i++)
            results[i] = await EnqueueAsync(payloads[i], ct).ConfigureAwait(false);
        return results;
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<EnqueueResult>> EnqueueBatchAsync(
        EnqueueOptions opt, IReadOnlyList<ReadOnlyMemory<byte>> payloads, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(opt);
        ArgumentNullException.ThrowIfNull(payloads);
        var results = new EnqueueResult[payloads.Count];
        for (var i = 0; i < payloads.Count; i++)
        {
            var itemOpt = opt.Seq.HasValue ? opt with { Seq = checked(opt.Seq.Value + i) } : opt;
            results[i] = await EnqueueAsync(itemOpt, payloads[i], ct).ConfigureAwait(false);
        }
        return results;
    }

    // ═══════════════════════════════════════════════════════════════════
    // 消费（辖权租约——Dequeue 纯本地读；Ack 经 AckCmd 复制）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>辖权出队（扫描 + pending 登记——无 inline 到期：记账经 ExpireCmd 走共识）。</summary>
    /// <param name="group">组名。</param>
    /// <param name="maxCount">单批最大条数。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>投递列表。</returns>
    internal async ValueTask<IReadOnlyList<QueueDelivery>> DequeueGroupAsync(string group, int maxCount, CancellationToken ct)
    {
        EnsureReady();
        EnsureGroupHome(group);
        return await _port.DequeueLocalAsync(group, maxCount, Guid.NewGuid(), ct).ConfigureAwait(false);
    }

    /// <summary>辖权确认（AckCmd 复制；tokens 非空 = token epoch 前置校验，null = 地址档基线跳过校验）。</summary>
    /// <param name="group">组名。</param>
    /// <param name="addresses">确认地址批。</param>
    /// <param name="tokens">与地址对应的令牌（null = 跳过 fencing 前置校验）。</param>
    /// <param name="ct">取消令牌。</param>
    internal async ValueTask AckGroupAsync(string group, IReadOnlyList<LogicalAddress> addresses,
        IReadOnlyList<DeliveryToken>? tokens, CancellationToken ct)
    {
        EnsureReady();
        EnsureGroupHome(group);
        long epoch = _port.GetGroupEpoch(group);
        if (tokens is not null)
        {
            for (var i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].Epoch < epoch)
                    throw new StaleDeliveryException(addresses[i], tokens[i].Epoch, epoch);
            }
        }
        var command = new QueueAckCommand
        {
            Correlation = NextCorrelation(),
            Name = NameBytes(group),
            Epoch = epoch,
            Addresses = addresses,
        };
        await ThrowIfFailedAsync(await ProposeAsync(command, ct).ConfigureAwait(false)).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<QueueDelivery>> DequeueAsync(int maxCount, CancellationToken ct)
        => DequeueGroupAsync(TierQueue.DefaultGroupName, maxCount, ct);

    /// <inheritdoc/>
    public ValueTask AckAsync(IReadOnlyList<LogicalAddress> acked, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(acked);
        return AckGroupAsync(TierQueue.DefaultGroupName, acked, tokens: null, ct);   // 地址档基线（epoch 前置校验跳过）
    }

    /// <inheritdoc/>
    public ValueTask AckBufferedAsync(IReadOnlyList<LogicalAddress> acked, CancellationToken ct)
        => BufferedAckAsync(TierQueue.DefaultGroupName, acked, ct);

    /// <inheritdoc/>
    public ValueTask FlushAcknowledgementsAsync(CancellationToken ct)
        => FlushBufferedAckAsync(TierQueue.DefaultGroupName, ct);

    /// <summary>缓冲确认（辖权本地簿记——批满提案 AckCmd；崩溃丢失 = at-least-once 重投继承）。</summary>
    /// <param name="group">组名。</param>
    /// <param name="acked">确认地址批。</param>
    /// <param name="ct">取消令牌。</param>
    internal async ValueTask BufferedAckAsync(string group, IReadOnlyList<LogicalAddress> acked, CancellationToken ct)
    {
        EnsureReady();
        EnsureGroupHome(group);
        var buffer = _buffers.GetOrAdd(group, _ => new BufferedAcks());
        List<LogicalAddress>? toFlush = buffer.Add(acked, _options.BufferedAckBatchSize);
        if (toFlush is not null)
            await AckGroupAsync(group, toFlush, tokens: null, ct).ConfigureAwait(false);
    }

    /// <summary>持久化缓冲确认（显式 flush 提案）。</summary>
    /// <param name="group">组名。</param>
    /// <param name="ct">取消令牌。</param>
    internal async ValueTask FlushBufferedAckAsync(string group, CancellationToken ct)
    {
        EnsureReady();
        EnsureGroupHome(group);
        var buffer = _buffers.GetOrAdd(group, _ => new BufferedAcks());
        List<LogicalAddress>? toFlush = buffer.Drain();
        if (toFlush is not null)
            await AckGroupAsync(group, toFlush, tokens: null, ct).ConfigureAwait(false);
    }

    /// <summary>显式否认（辖权面——计数 +1/死信判定经 ExpireCmd 复制）。</summary>
    /// <param name="group">组名。</param>
    /// <param name="addresses">否认地址列表。</param>
    /// <param name="ct">取消令牌。</param>
    internal async ValueTask NackGroupAsync(string group, IReadOnlyList<LogicalAddress> addresses, CancellationToken ct)
    {
        EnsureReady();
        EnsureGroupHome(group);
        var config = _port.TryGetGroupConfig(group)
            ?? throw new InvalidOperationException($"消费组不存在：{group}");
        var entries = new List<QueueExpireEntry>(addresses.Count);
        foreach (var addr in addresses)
        {
            int count = await DiagnosticRedeliveryCountAsync(group, addr, ct).ConfigureAwait(false);
            int newCount = count + 1;
            bool dead = newCount > config.MaxRedeliveries;
            if (dead)
                await DeadLetterWriteAsync(group, addr, newCount, ct).ConfigureAwait(false);
            entries.Add(new QueueExpireEntry(addr, newCount, dead));
        }
        var command = new QueueExpireCommand
        {
            Correlation = NextCorrelation(),
            Op = (byte)QueueExpireOp.UpdateEntries,
            Name = NameBytes(group),
            Entries = entries.Select(e => new QueueExpireEntryFrame(e.Address, e.RedeliveryCount, e.DeadLettered))
                .ToArray(),
        };
        await ThrowIfFailedAsync(await ProposeAsync(command, ct).ConfigureAwait(false)).ConfigureAwait(false);
        await _port.RemovePendingLocalAsync(group, addresses, ct).ConfigureAwait(false);
    }

    /// <summary>Claim 抢占的代次递增提案（fencing 复制面）。</summary>
    /// <param name="group">组名。</param>
    /// <param name="ct">取消令牌。</param>
    internal async ValueTask ClaimEpochBumpAsync(string group, CancellationToken ct)
    {
        EnsureReady();
        EnsureGroupHome(group);
        var command = new QueueExpireCommand
        {
            Correlation = NextCorrelation(),
            Op = (byte)QueueExpireOp.Claim,
            Name = NameBytes(group),
        };
        await ThrowIfFailedAsync(await ProposeAsync(command, ct).ConfigureAwait(false)).ConfigureAwait(false);
    }

    // ═══ 消费句柄内部面（组域——consumer 白盒通道）═══

    /// <summary>本端节点 ID（句柄辖权判定用）。</summary>
    internal NodeId SelfId => _self;

    /// <summary>组 pending 窥视。</summary>
    /// <param name="group">组名。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>在途项列表。</returns>
    internal ValueTask<IReadOnlyList<PendingInfo>> DiagnosticPendingAsync(string group, CancellationToken ct)
    {
        EnsureReady();
        return _port.PendingLocalAsync(group, ct);
    }

    /// <summary>组游标读面。</summary>
    /// <param name="group">组名。</param>
    /// <returns>组游标。</returns>
    internal LogicalAddress DiagnosticGroupCursor(string group)
    {
        EnsureReady();
        return _port.GetGroupCursor(group);
    }

    /// <summary>组域缓冲确认（句柄通道）。</summary>
    /// <param name="group">组名。</param>
    /// <param name="addresses">确认地址批。</param>
    /// <param name="ct">取消令牌。</param>
    internal ValueTask DiagnosticBufferedAckAsync(string group, IReadOnlyList<LogicalAddress> addresses, CancellationToken ct)
        => BufferedAckAsync(group, addresses, ct);

    /// <summary>组域缓冲确认 flush（句柄通道）。</summary>
    /// <param name="group">组名。</param>
    /// <param name="ct">取消令牌。</param>
    internal ValueTask DiagnosticFlushBufferedAckAsync(string group, CancellationToken ct)
        => FlushBufferedAckAsync(group, ct);

    /// <summary>组域 pending 摘除（句柄通道）。</summary>
    /// <param name="group">组名。</param>
    /// <param name="addresses">已终局地址。</param>
    /// <param name="ct">取消令牌。</param>
    internal ValueTask DiagnosticRemovePendingAsync(string group, IReadOnlyList<LogicalAddress> addresses, CancellationToken ct)
    {
        EnsureReady();
        return _port.RemovePendingLocalAsync(group, addresses, ct);
    }

    /// <summary>重投计数窥视（Nack 计账数据源——已应用视图）。</summary>
    /// <param name="group">组名。</param>
    /// <param name="address">消息地址。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>当前重投计数（不在途 = 0）。</returns>
    private async ValueTask<int> DiagnosticRedeliveryCountAsync(string group, LogicalAddress address, CancellationToken ct)
    {
        var pending = await _port.PendingLocalAsync(group, ct).ConfigureAwait(false);
        foreach (var p in pending)
        {
            if (p.Address == address)
                return p.RedeliveryCount;
        }
        return 0;
    }

    /// <inheritdoc/>
    public LogicalAddress GroupCursor => _core.GroupCursor;

    // ═══════════════════════════════════════════════════════════════════
    // 消费组（GroupCmd 复制——创建/删除/复位全序）
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public async ValueTask<ITierQueueConsumer> CreateGroupAsync(GroupOptions opt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(opt);
        EnsureReady();
        if (_port.GetGroupNames().Contains(opt.Name))
            throw new InvalidOperationException($"消费组已存在：{opt.Name}");   // 客户端面确定性拒绝（apply 侧幂等——重放窗口 no-op）
        var command = new QueueGroupCommand
        {
            Correlation = NextCorrelation(),
            Op = (byte)QueueGroupOp.Create,
            Name = NameBytes(opt.Name),
            StartAt = (byte)opt.StartAt,
            StartAddress = opt.StartAddress,
            VisibilityTimeoutMs = (long)opt.VisibilityTimeout.TotalMilliseconds,
            MaxRedeliveries = opt.MaxRedeliveries,
            Home = _self,
        };
        await ThrowIfFailedAsync(await ProposeAsync(command, ct).ConfigureAwait(false)).ConfigureAwait(false);
        return new TierQueueReplicaConsumer(this, opt.Name);
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<string>> ListGroupsAsync(CancellationToken ct)
    {
        EnsureReady();
        return ValueTask.FromResult<IReadOnlyList<string>>(_port.GetGroupNames());
    }

    /// <inheritdoc/>
    public async ValueTask DeleteGroupAsync(string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name == TierQueue.DefaultGroupName)
            throw new InvalidOperationException($"内建组 {TierQueue.DefaultGroupName} 不可删除——它是队列级兼容面的宿主");
        EnsureReady();
        var command = new QueueGroupCommand
        {
            Correlation = NextCorrelation(),
            Op = (byte)QueueGroupOp.Delete,
            Name = NameBytes(name),
        };
        await ThrowIfFailedAsync(await ProposeAsync(command, ct).ConfigureAwait(false)).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask ResetGroupAsync(string name, GroupStartAt startAt, LogicalAddress startAddress,
        CancellationToken ct)
    {
        EnsureReady();
        var command = new QueueGroupCommand
        {
            Correlation = NextCorrelation(),
            Op = (byte)QueueGroupOp.Reset,
            Name = NameBytes(name),
            StartAt = (byte)startAt,
            StartAddress = startAddress,
        };
        await ThrowIfFailedAsync(await ProposeAsync(command, ct).ConfigureAwait(false)).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public ITierQueueConsumer CreateConsumerAsync(string groupName)
        => new TierQueueReplicaConsumer(this, groupName);

    /// <inheritdoc/>
    public Contracts.Transactions.ITransactionParticipant GetGroupParticipant(string groupName)
        => throw new NotSupportedException(
            "复制版不支持 Session 域 2PC（跨节点恰好一次归规模化候选）——用 AckAsync/AckBufferedAsync 的 at-least-once 面。");

    /// <inheritdoc/>
    /// <returns>有界同步收尾（循环不等待——完整等待归 DisposeAsync；TCSG137 禁同步阻塞后台句柄）。</returns>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _machine.GroupHomeChanged -= OnHomeChanged;
        _node.Raft.LeaderChanged -= OnLeaderChanged;
        _cts.Cancel();
        _cts.Dispose();
        _router.Unregister(_groupId);
        _core.Dispose();
        _snapshotExports.Dispose();
    }

    /// <inheritdoc/>
    public ValueTask<QueueStats> GetStatsAsync(CancellationToken ct)
    {
        EnsureReady();
        return _core.GetStatsAsync(ct);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 死信/延迟/持久化/回收（差异面显形）
    // ═══════════════════════════════════════════════════════════════════

    ITierQueue? ITierQueue.DeadLetterQueue => DeadLetterTarget;

    /// <inheritdoc/>
    public async ValueTask<EnqueueResult> ReplayAsync(LogicalAddress deadLetterAddress, ITierQueue target,
        CancellationToken ct)
    {
        EnsureReady();
        ArgumentNullException.ThrowIfNull(target);
        byte[] raw = await _port.ReadPayloadAsync(deadLetterAddress, ct).ConfigureAwait(false);
        if (!DeadLetterEnvelope.TryUnwrap(raw, out _, out _, out _, out var payload))
            throw new InvalidDataException($"地址 {deadLetterAddress} 不是死信 envelope（magic 不符）");
        var result = await target.EnqueueAsync(payload, ct).ConfigureAwait(false);
        await AckAsync([deadLetterAddress], ct).ConfigureAwait(false);
        return result;
    }

    /// <inheritdoc/>
    public async ValueTask<int> ReplayAllAsync(ITierQueue target, CancellationToken ct)
    {
        EnsureReady();
        ArgumentNullException.ThrowIfNull(target);
        int replayed = 0;
        while (true)
        {
            var batch = await DequeueAsync(64, ct).ConfigureAwait(false);
            if (batch.Count == 0)
                break;
            var payloads = new List<ReadOnlyMemory<byte>>(batch.Count);
            var addresses = new List<LogicalAddress>(batch.Count);
            foreach (var d in batch)
            {
                if (!DeadLetterEnvelope.TryUnwrap(d.Payload, out _, out _, out _, out var payload))
                    throw new InvalidDataException("DLQ 含非 envelope 条目（混入普通消息？）");
                payloads.Add(payload);
                addresses.Add(d.Address);
            }
            await target.EnqueueBatchAsync(payloads, ct).ConfigureAwait(false);
            await AckAsync(addresses, ct).ConfigureAwait(false);
            replayed += batch.Count;
        }
        return replayed;
    }

    /// <inheritdoc/>
    public async ValueTask<int> CancelDelayedAsync(CancelDelayedFilter filter, CancellationToken ct)
    {
        EnsureReady();
        var addresses = await _port.ResolveDelayedAddressesAsync(filter, ct).ConfigureAwait(false);
        if (addresses.Count == 0)
            return 0;
        foreach (var group in _port.GetGroupNames())
        {
            long epoch = _port.GetGroupEpoch(group);
            var command = new QueueAckCommand
            {
                Correlation = NextCorrelation(),
                Name = NameBytes(group),
                Epoch = epoch,
                Addresses = addresses,
            };
            await ThrowIfFailedAsync(await ProposeAsync(command, ct).ConfigureAwait(false)).ConfigureAwait(false);
        }
        return addresses.Count;
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<DelayedInfo>> ListDelayedAsync(CancellationToken ct)
    {
        EnsureReady();
        return _core.ListDelayedAsync(ct);
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<DelayedInfo>> ListDelayedAsync(DelayedQuery query, CancellationToken ct)
    {
        EnsureReady();
        return _core.ListDelayedAsync(query, ct);
    }

    /// <inheritdoc/>
    public ValueTask FlushAsync(CancellationToken ct)
    {
        EnsureReady();
        return _core.FlushAsync(ct);   // 本地读可见性（raft 多数派持久 = Enqueue 返回即达）
    }

    /// <inheritdoc/>
    public ValueTask WaitForDurableAsync(LogicalAddress address, CancellationToken ct)
    {
        EnsureReady();
        return _core.WaitForDurableAsync(address, ct);   // applied 档（spec ④——返回即多数派已应用）
    }

    /// <inheritdoc/>
    public LogicalAddress HeadAddress => _core.HeadAddress;

    /// <inheritdoc/>
    public LogicalAddress TailAddress => _core.TailAddress;

    /// <inheritdoc/>
    public LogicalAddress DurableTail => _core.DurableTail;

    /// <inheritdoc/>
    public async ValueTask<LogicalAddress> TruncateAsync(LogicalAddress uptoExclusive, CancellationToken ct)
    {
        EnsureReady();
        var command = new QueueRetentionCommand
        {
            Correlation = NextCorrelation(),
            HasTarget = true,
            Target = uptoExclusive,
        };
        await ThrowIfFailedAsync(await ProposeAsync(command, ct).ConfigureAwait(false)).ConfigureAwait(false);
        return HeadAddress;
    }

    // ═══════════════════════════════════════════════════════════════════
    // 生命周期（ILifecycle 透传本地核心 + warmup 门）
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public bool IsReady => Volatile.Read(ref _ready) && _core.IsReady;

    /// <inheritdoc/>
    public RecoveryState RecoveryState => _core.RecoveryState;

    /// <inheritdoc/>
    public event Action<RecoveryProgress>? RecoveryProgressChanged
    {
        add => _core.RecoveryProgressChanged += value;
        remove => _core.RecoveryProgressChanged -= value;
    }

    /// <inheritdoc/>
    public void WaitForReady() => _core.WaitForReady();

    /// <inheritdoc/>
    public bool WaitForReady(int timeoutMilliseconds) => _core.WaitForReady(timeoutMilliseconds);

    /// <inheritdoc/>
    public Task WaitForReadyAsync(CancellationToken ct = default) => _core.WaitForReadyAsync(ct);

    /// <inheritdoc/>
    public void CancelRecovery() => _core.CancelRecovery();

    /// <summary>Warmup 完成（Builder 追平等待后置位——ops 门开放）。</summary>
    internal void MarkReady() => Volatile.Write(ref _ready, true);

    /// <summary>启动辖权后台循环（到期记账 + retention 提案——仅辖权组生效）。</summary>
    internal void StartHomeLoop()
    {
        _homeLoop = Task.Run(() => HomeLoopAsync(_cts.Token));
    }

    /// <summary>辖权后台循环：周期扫描辖权组 pending 到期项 → ExpireCmd（死信写入先行）→ 摘除簿记；
    /// retention → RetentionCmd（回收线全组一致语义由 apply 守卫承接）。</summary>
    /// <param name="ct">取消令牌。</param>
    private async Task HomeLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _clock.Delay(_options.HomeSweepInterval, ct).ConfigureAwait(false);
                if (_machine.GetHome(TierQueue.DefaultGroupName) == _self)
                    await SweepRetentionAsync(ct).ConfigureAwait(false);
                foreach (var group in _port.GetGroupNames())
                {
                    if (ct.IsCancellationRequested)
                        return;
                    if (_machine.GetHome(group) == _self)
                        await SweepExpiredAsync(group, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning("TierQueueReplica 辖权循环单轮异常（续跑）：{Message}", ex.Message);
            }
        }
    }

    /// <summary>到期记账扫描（辖权组）：pending 超可见性项 → 计数终值 + 死信判定 → DLQ 写入先行 → ExpireCmd。</summary>
    /// <param name="group">组名。</param>
    /// <param name="ct">取消令牌。</param>
    private async ValueTask SweepExpiredAsync(string group, CancellationToken ct)
    {
        var config = _port.TryGetGroupConfig(group);
        if (config is not { } cfg)
            return;
        long visibilityMs = (long)cfg.VisibilityTimeout.TotalMilliseconds;
        var pending = await _port.PendingLocalAsync(group, ct).ConfigureAwait(false);
        var entries = new List<QueueExpireEntry>();
        foreach (var p in pending)
        {
            if (p.IsBackoff)
                continue;   // 退避窗：不计数不终结（到期即重投——本地 ExpireOverdue 同源语义）
            if (p.IdleMs < visibilityMs)
                continue;
            int newCount = p.RedeliveryCount + 1;
            bool dead = newCount > cfg.MaxRedeliveries;
            if (dead)
                await DeadLetterWriteAsync(group, p.Address, newCount, ct).ConfigureAwait(false);
            entries.Add(new QueueExpireEntry(p.Address, newCount, dead));
        }
        if (entries.Count == 0)
            return;

        var command = new QueueExpireCommand
        {
            Correlation = NextCorrelation(),
            Op = (byte)QueueExpireOp.UpdateEntries,
            Name = NameBytes(group),
            Entries = entries.Select(e => new QueueExpireEntryFrame(e.Address, e.RedeliveryCount, e.DeadLettered))
                .ToArray(),
        };
        await ThrowIfFailedAsync(await ProposeAsync(command, ct).ConfigureAwait(false)).ConfigureAwait(false);
        var addresses = entries.Select(e => e.Address).ToArray();
        await _port.RemovePendingLocalAsync(group, addresses, ct).ConfigureAwait(false);
    }

    /// <summary>死信写入（DLQ 复制面——写入 durable 先行、失败留原位抛出：丢信不可能，spec §7.2 顺序契约）。</summary>
    /// <param name="group">源组名。</param>
    /// <param name="address">死信地址。</param>
    /// <param name="count">重投计数终值（入 envelope 溯源）。</param>
    /// <param name="ct">取消令牌。</param>
    private async ValueTask DeadLetterWriteAsync(string group, LogicalAddress address, int count, CancellationToken ct)
    {
        if (DeadLetterTarget is not { } dlq)
            return;   // 关闭档：仅推进位点 + 循环外告警（本地语义继承）
        byte[] payload = await _port.ReadPayloadAsync(address, ct).ConfigureAwait(false);
        var envelope = DeadLetterEnvelope.Wrap(address, group, count, payload);
        await dlq.EnqueueAsync(envelope, ct).ConfigureAwait(false);
        await dlq.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>retention 推进（$default 辖权节点周期提案——apply 侧 min(组游标) 守卫承接）。</summary>
    /// <param name="ct">取消令牌。</param>
    private async ValueTask SweepRetentionAsync(CancellationToken ct)
    {
        var command = new QueueRetentionCommand
        {
            Correlation = NextCorrelation(),
        };
        await ProposeAsync(command, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <returns>完成时后台循环已停、路由注销、本地核心已释放（raft 组归宿主——调用方经 host 收尾）。</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _machine.GroupHomeChanged -= OnHomeChanged;
        _node.Raft.LeaderChanged -= OnLeaderChanged;
        _cts.Cancel();
        if (_homeLoop is { } loop)
        {
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 循环退出超时——继续资源释放
            }
        }
        _cts.Dispose();
        _router.Unregister(_groupId);
        await _core.DisposeAsync().ConfigureAwait(false);
        await _snapshotExports.DisposeAsync().ConfigureAwait(false);   // 导出任务组 drain（压缩钩子不再提交）
    }

    /// <summary>辖权缓冲确认簿记（线程安全——批满返回整批待提案）。</summary>
    private sealed class BufferedAcks
    {
        private readonly object _lock = new();
        private readonly List<LogicalAddress> _items = [];

        /// <summary>加入一批缓冲确认。</summary>
        /// <param name="acked">确认地址。</param>
        /// <param name="batchSize">提交阈值。</param>
        /// <returns>达阈值 = 待提案整批（含本次）；否则 null。</returns>
        public List<LogicalAddress>? Add(IReadOnlyList<LogicalAddress> acked, int batchSize)
        {
            lock (_lock)
            {
                foreach (var a in acked)
                    if (a.IsValid)
                        _items.Add(a);
                if (_items.Count < batchSize)
                    return null;
                var batch = new List<LogicalAddress>(_items);
                _items.Clear();
                return batch;
            }
        }

        /// <summary>清空缓冲（显式 flush）。</summary>
        /// <returns>待提案批（空 = null）。</returns>
        public List<LogicalAddress>? Drain()
        {
            lock (_lock)
            {
                if (_items.Count == 0)
                    return null;
                var batch = new List<LogicalAddress>(_items);
                _items.Clear();
                return batch;
            }
        }
    }
}

/// <summary>TierQueue 复制版（tierqueue-replicated-spec §3——ITierQueue 同面继承 + 辖权差异面显形）。</summary>
public interface ITierQueueReplica : ITierQueue
{
    /// <summary>指定消费组当前辖权节点（本地已应用视图；Empty = 辖权未定）。</summary>
    /// <param name="group">消费组名。</param>
    /// <returns>辖权节点。</returns>
    NodeId GroupHome(string group);

    /// <summary>计划移交组辖权（运维/均衡——HomeCmd 经共识，组代次 +1 fencing 递增）。</summary>
    /// <param name="group">消费组名。</param>
    /// <param name="target">目标节点。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>移交（HomeCmd applied）后完成。</returns>
    ValueTask MoveGroupHomeAsync(string group, NodeId target, CancellationToken ct);

    /// <summary>辖权变更事件（接管/移交/授予通知——订阅方重连）。</summary>
    event Action<string, NodeId>? GroupHomeChanged;
}

/// <summary>
/// 复制版消费组句柄（ITierQueueConsumer 同面——消费 ops 经辖权租约仲裁；token/Ack 语义对齐本地档）。
/// </summary>
/// <param name="replica">宿主副本。</param>
/// <param name="group">组名。</param>
public sealed class TierQueueReplicaConsumer(TierQueueReplica replica, string group) : ITierQueueConsumer
{
    /// <inheritdoc/>
    public string Group => group;

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<QueueDelivery>> DequeueAsync(int maxCount, CancellationToken ct)
        => replica.DequeueGroupAsync(group, maxCount, ct);

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<QueueDelivery>> ClaimAsync(ClaimFilter filter, CancellationToken ct)
    {
        // 抢占 = 辖权本地簿记摘除 + ExpireCmd(Claim) 代次递增 + 重新出队（新 token 直接可 Ack）
        var home = replica.GroupHome(group);
        if (home != replica.SelfId)
            throw new NotGroupHomeException(group, home);
        var pending = await replica.DiagnosticPendingAsync(group, ct).ConfigureAwait(false);
        long minIdleMs = (long)filter.MinIdle.TotalMilliseconds;
        var expired = new List<LogicalAddress>();
        foreach (var p in pending)
        {
            if (expired.Count >= filter.MaxCount)
                break;
            if (p.IdleMs >= minIdleMs)
                expired.Add(p.Address);
        }
        if (expired.Count == 0)
            return [];
        await replica.DiagnosticRemovePendingAsync(group, expired, ct).ConfigureAwait(false);
        await replica.ClaimEpochBumpAsync(group, ct).ConfigureAwait(false);
        return await replica.DequeueGroupAsync(group, filter.MaxCount, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask AckAsync(IReadOnlyList<DeliveryReceipt> receipts, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(receipts);
        var addresses = new LogicalAddress[receipts.Count];
        var tokens = new DeliveryToken[receipts.Count];
        for (var i = 0; i < receipts.Count; i++)
        {
            addresses[i] = receipts[i].Address;
            tokens[i] = receipts[i].Token;
        }
        await replica.AckGroupAsync(group, addresses, tokens, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask AckBufferedAsync(IReadOnlyList<DeliveryReceipt> receipts, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(receipts);
        // token 校验在辖权侧 AckGroupAsync 前置——缓冲面直接累积地址（fencing 在 flush 提案时统一校验）
        var addresses = new LogicalAddress[receipts.Count];
        for (var i = 0; i < receipts.Count; i++)
            addresses[i] = receipts[i].Address;
        await replica.DiagnosticBufferedAckAsync(group, addresses, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask FlushAcknowledgementsAsync(CancellationToken ct)
        => await replica.DiagnosticFlushBufferedAckAsync(group, ct).ConfigureAwait(false);

    /// <inheritdoc/>
    public ValueTask AckInRoundAsync(TierSession session, IReadOnlyList<DeliveryReceipt> receipts, CancellationToken ct)
        => throw new NotSupportedException(
            "复制版不支持 Session 域 2PC（跨节点恰好一次归规模化候选）——用 AckAsync/AckBufferedAsync 的 at-least-once 面。");

    /// <inheritdoc/>
    public async ValueTask NackAsync(IReadOnlyList<LogicalAddress> addresses, CancellationToken ct)
        => await replica.NackGroupAsync(group, addresses, ct).ConfigureAwait(false);

    /// <inheritdoc/>
    public LogicalAddress GroupCursor => replica.DiagnosticGroupCursor(group);

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<PendingInfo>> PendingAsync(CancellationToken ct)
        => replica.DiagnosticPendingAsync(group, ct);

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;   // 组归复制状态机——句柄轻量引用
}
