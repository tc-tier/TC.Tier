using TC.Tier.Core.Logging;
using TC.Tier.Core.Net.Channels;
using TC.Tier.Core.Net.Wire;
using System.Runtime.CompilerServices;
using TC.Tier.Core.Execution;

namespace TC.Tier.Core.Net.Raft;

/// <summary>
/// 复制引擎（spec-02 Leader 侧全量）——nextIndex/matchIndex 管理、AppendEntries 构造与应答处理、
/// 冲突回退（hint 加速）、commitIndex 推进（Figure 8 约束）、心跳循环。
/// <para>★ per-peer 续体链（spec-10 D1）：复制发送从共识循环线程抽出，每 peer 一条独立续体链
/// （线程池续体——不占专属线程，peer 间并行：发 A 时可处理 B 的 ack）；peer 状态
/// （nextIndex/matchIndex/在途）只被本链读写（raft 本要求每 peer 单在途 AppendEntries——
/// 串行是协议本性，单写者零锁）。信号驱动：新条目（<see cref="NotifyAppended"/>）/
/// 应答投槽（<see cref="HandleRespAsync"/>）/ 心跳 tick（<see cref="HandleTick"/>）→
/// lane 唤醒 → 消费应答槽 → 发送判定（滞后 / 心跳到期 / 在途超时重试）→ 回挂等待。</para>
/// <para>★ 请求回调形态（spec-12 §6）：发送 = 出站请求任务（per-call 超时对齐在途重试窗——
/// 应答由传输 CorrId 配对）；应答到达经 <see cref="_onRespArrived"/> 路由（状态机 term/ReadIndex
/// 前处理）→ <see cref="HandleRespAsync"/> 投槽/直跑——lane 单写者不变式与旧事件面同构。</para>
/// <para>★ 快照判定：nextIndex ≤ 本地快照覆盖点 → 经 <see cref="_onSnapshotNeeded"/> 通知状态机
/// 改走快照安装（spec-03 §2 触发条件——增量复制无法接续）。</para>
/// </summary>
public sealed class ReplicationProcess : IAsyncDisposable
{
    private readonly NodeId _self;
    private readonly IRaftStore _store;
    private readonly IProtocolTransport _transport;
    private readonly Func<long> _commitIndexProvider;
    private readonly Action<long> _onCommitAdvanced;
    private readonly Func<NodeId, AppendEntriesResp, ValueTask> _onRespArrived;   // 应答路由（状态机 term/ReadIndex 前处理 + 回投 HandleRespAsync）
    private readonly Action<NodeId>? _onFollowerAck;   // follower 成功应答回调（租约读多数派确认——成功分支调用，可空 = 不订阅）
    private readonly Func<NodeId, ValueTask> _onSnapshotNeeded;
    private readonly ReplicationPolicy _policy;
    private readonly TimeSpan _heartbeatInterval;
    private readonly TimeSpan _heartbeatRetryAfter;   // 在途超时 = 重试窗口（2× 心跳周期）——出站请求 per-call 超时同源
    private readonly RequestOptions _rpcOptions;      // ★ RPC 发送选项（实例只读——每批发送免 RequestOptions 分配）
    private readonly int _maxInFlightPerPeer;          // ★ 窗口 N 上限（多批在途——构造赋值，默认 8）
    private readonly ILogger? _logger;
    private readonly CancellationToken _lifecycleCt;
    private readonly TaskSink _laneTasks;   // ★ lane 链+出站请求任务组（结构化并发——禁用裸 fire-and-forget）

    // 攒批未发（窗口/条数/字节三维度）——★ 2026-09-04 起状态经 _windowLock 串行化：
    // 窗到期 one-shot Timer 与状态机心跳 tick（HandleTickAsync）双路径并发 flush，计数器互斥。
    // ★ 架构裁决（双实验证伪后）：窗到期为何用 TimerQueue 而非 DeadlineRegistry 拉取模型——
    //   registry pacer 的唤醒延迟 = 睡眠余量（其迭代按"入睡时点的最早 deadline"定睡，epoch
    //   在睡眠期间挂载要等下一轮醒——数十 ms 级）。心跳/选举量级（50ms+）可接受；亚 tick
    //   批窗（1-5ms）物理不可达（实测停顿轮复现）。one-shot Timer 每 epoch 精确重挂 = 唯一
    //   达标的到期泵。活性安全边界：Timer 不在共识活性路径上（心跳 tick = 活性源 + 窗到期
    //   兜底——Timer 停摆/精度偏差最坏退回 50ms 心跳粒度，无冻结风险；TimerQueue 孤儿判例
    //   针对的是 PeriodicTimer 当共识活性源，不适用）。
    private readonly object _windowLock = new();
    private readonly ITimer? _windowFlushTimer;   // ★ 窗到期定时器（window>0 才建——时钟缝供给源驱动）
    private readonly TimeProvider _clock;         // 时钟供给源（单调戳/定时器/延迟统一——缺省 System）
    private long _pendingCount;
    private long _pendingBytes;
    private long _pendingSince;

    private volatile ClusterConfig _config = new(NodeId.Empty);
    private volatile Dictionary<NodeId, PeerLane> _lanes = [];
    private int _isLeaderFlag;

    /// <summary>当前活动配置（leader 期成员集——选举/复制目标）。</summary>
    public ClusterConfig Config => _config;

    /// <summary>构造。</summary>
    /// <param name="self">本节点 ID。</param>
    /// <param name="store">存储端口。</param>
    /// <param name="transport">节点端点完整面（请求回调发送 AppendEntries）。</param>
    /// <param name="commitIndexProvider">当前 commitIndex 读取（状态机持有）。</param>
    /// <param name="onCommitAdvanced">commitIndex 推进回调（状态机 AdvanceCommit——投递 apply；多 lane 并发调用，状态机侧单调化）。</param>
    /// <param name="onRespArrived">应答到达路由（状态机：更高 term 降级/ReadIndex 计数 → 本类 <see cref="HandleRespAsync"/>——lane 投槽/直跑）。</param>
    /// <param name="onSnapshotNeeded">快照安装请求（nextIndex ≤ 本地快照覆盖点——增量无法接续；lane 链线程调用）。</param>
    /// <param name="onFollowerAck"></param>
    /// <param name="policy">复制策略（条数/字节/时间三维度攒批；默认全零 = 立即推送）。</param>
    /// <param name="heartbeatInterval">心跳间隔（lane 空批心跳节流——RaftOptions.HeartbeatInterval）。</param>
    /// <param name="lifecycleCt">生命周期取消（状态机停止——lane 链退出）。</param>
    /// <param name="logger">日志。</param>
    /// <param name="clock">时钟供给源（时钟缝 件一——缺省 <see cref="TimeProvider.System"/> 行为不变）。</param>
    public ReplicationProcess(
        NodeId self,
        IRaftStore store,
        IProtocolTransport transport,
        Func<long> commitIndexProvider,
        Action<long> onCommitAdvanced,
        Func<NodeId, AppendEntriesResp, ValueTask> onRespArrived,
        Func<NodeId, ValueTask> onSnapshotNeeded,
        Action<NodeId>? onFollowerAck = null,
        ReplicationPolicy? policy = null,
        TimeSpan heartbeatInterval = default,
        ILogger? logger = null,
        CancellationToken lifecycleCt = default,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(commitIndexProvider);
        ArgumentNullException.ThrowIfNull(onCommitAdvanced);
        ArgumentNullException.ThrowIfNull(onRespArrived);
        ArgumentNullException.ThrowIfNull(onSnapshotNeeded);
        _clock = clock ?? TimeProvider.System;
        _self = self;
        _store = store;
        _transport = transport;
        _commitIndexProvider = commitIndexProvider;
        _onCommitAdvanced = onCommitAdvanced;
        _onRespArrived = onRespArrived;
        _onFollowerAck = onFollowerAck;
        _onSnapshotNeeded = onSnapshotNeeded;
        _policy = policy ?? new ReplicationPolicy();
        _laneTasks = new TaskSink($"raft-repl-{self}", logger: logger);
        _heartbeatInterval = heartbeatInterval <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(50) : heartbeatInterval;
        _heartbeatRetryAfter = TimeSpan.FromMilliseconds(_heartbeatInterval.TotalMilliseconds * 2);
        _rpcOptions = new RequestOptions(_heartbeatRetryAfter);
        _maxInFlightPerPeer = 8;   // ★ 窗口 N 缺省（多批在途上限；实测 64 无收益——发布速率受 append→persist 批尾链约束，非窗口）
        _logger = logger;
        _lifecycleCt = lifecycleCt;
        // ★ 窗到期 one-shot Timer（判例 2026-09-04——时间维不骑心跳 tick）：window>0 才建。
        //   Timer 回调 = 第二 flush 路径（与 HandleTickAsync 心跳兜底并发安全——_windowLock 互斥）；
        //   时钟缝 件一——定时器经注入时钟建立（假钟下由快进确定性触发，System 下行为不变）。
        if (_policy.BatchWindow > TimeSpan.Zero)
        {
            _windowFlushTimer = _clock.CreateTimer(
                _ => FlushWindowIfDue(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>per-peer 复制链状态（单链读写——peer 间并行，spec-10 D1）。</summary>
    private sealed class PeerLane
    {
        public required NodeId Id;
        public long NextIndex;
        public long MatchIndex;
        public long InFlightSince;              // 最早在途时刻（ms 单调戳；0 = 无在途）
        public int InFlightCount;               // ★ 在途批数（窗口 N——多批在途：批间串行化消除）
        public long LastSendAt;                 // 上次发送时刻（空批心跳节流判定）
        public volatile bool Active;            // lane 生命周期（false = 链退出——降级/配置移除）
        public bool IsWitness;                  // ★ 二期-F2：witness lane——断言流（无日志体，DDR-F2）
        public AppendEntriesResp? PendingResp;  // 应答槽（投递方写 / 本链消费——窗口 1 下单槽无竞争）
        public int Running;                     // 链逻辑执行互斥（0 = 空闲可被应答线程直跑入侵；1 = 执行中）
        public int DriverId;                    // 当前驱动线程 id（Running=1 时有效；同线程自投递免唤醒）
        public long[]? TermsExact;                // ★ Terms 精确数组（WriteEntriesTo 回填目标——批尺寸稳态恒定时零分配）
        public long CachedPrevIndex;              // ★ 空批心跳快速路径锚：上次读批的 prevLogIndex（term 缓存有效性）
        public long CachedPrevTerm;               //   对应 term（日志 entry term 不可变——index 锚比对 + SnapshotIndex 护栏）
        public PooledBufferWriter? RegionWriter;   // ★ 条目区直写缓冲（lane 复用——SubmitAppend 同步消费完即复位）
        public byte[]? PendingFrame;               // ★ 待发送帧（SubmitAppend 编码写 / SendAppendRpcAsync 首段读——同线程串行）
        public int PendingFrameLength;             // 待发送帧有效字节（租借数组 ≥ len）
        /// <summary>发送帧委托（缓存方法组直指 lane 发送体——每批复用，免闭包 + display class 分配）。</summary>
        public Func<CancellationToken, ValueTask>? SendFrameFunc;   // ★ 缓存委托（lane 创建期一份——每批免闭包+display class 分配）
        public readonly AsyncManualResetEvent Wakeup = new();
        public Task? Worker;                    // ★ lane 专用线程（LongRunning+泵域——长循环不进 TaskSink，2026-09-03）
    }

    // ═══ 角色生命周期 ═══

    /// <summary>当选 Leader：初始化复制表（nextIndex = 日志尾+1、matchIndex = 0）+ 每 peer 起链
    /// （链首拍立即发空 AppendEntries——确立权威，spec-01 §5.3）。</summary>
    /// <param name="config">当选时的活动配置（leader 期成员集）。</param>
    public void BecomeLeader(ClusterConfig config)
    {
        var lanes = new Dictionary<NodeId, PeerLane>();
        foreach (var m in config.Members)
        {
            if (m.Id == _self) continue;
            lanes[m.Id] = new PeerLane
            {
                Id = m.Id,
                NextIndex = _store.LastLogIndex + 1,
                MatchIndex = 0,
                Active = true,
                IsWitness = config.IsWitness(m.Id),
            };
        }
        _config = config;
        _lanes = lanes;
        Volatile.Write(ref _isLeaderFlag, 1);
        foreach (var lane in lanes.Values)
        {
            lane.Wakeup.Set();   // 首拍信号（链立即确立权威广播）
            // ★ 起 lane 专用线程（同 peer 单链——lane 生命周期即防重复）；同步段脱离调用者锁域
            //   （BecomeLeader 在 _clusterLock 内——锁内直跑发送链会与"gate→clusterLock"反向死锁）
            StartLane(lane);
        }
        _logger?.LogInformation("Replication 当选：term={Term} peers={Peers}", _store.Term, lanes.Count);
    }

    /// <summary>降级 Follower：停全部链（唤醒退出）+ 清复制表。</summary>
    public void BecomeFollower()
    {
        Volatile.Write(ref _isLeaderFlag, 0);
        _windowFlushTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);   // 窗到期停表（重新当选后新 epoch 重挂）
        StopLanes(_lanes);
        _lanes = [];
        lock (_windowLock)
        {
            _pendingCount = 0;
            _pendingBytes = 0;
        }
    }

    /// <summary>
    /// 活动配置切换（apply 产物——spec-04）：重建复制表——新成员 nextIndex = 日志尾+1（快照/增量追赶）
    /// 并起新链，移除成员停链；保留成员链与在途批不动（应答仍会到达——窗口 1 语义连续）。
    /// </summary>
    /// <param name="config">新活动配置（apply 产物——含新/移除成员）。</param>
    public void ApplyConfig(ClusterConfig config)
    {
        var old = _lanes;
        var lanes = new Dictionary<NodeId, PeerLane>(old);
        foreach (var m in config.Members)
        {
            if (m.Id == _self || lanes.ContainsKey(m.Id)) continue;
            lanes[m.Id] = new PeerLane
            {
                Id = m.Id,
                NextIndex = _store.LastLogIndex + 1,
                MatchIndex = 0,
                Active = true,
                IsWitness = config.IsWitness(m.Id),
            };
        }
        var removed = old.Values.Where(l => !config.Contains(l.Id)).ToArray();
        _config = config;
        _lanes = lanes;
        foreach (var lane in removed) StopLane(lane);
        foreach (var m in config.Members)
        {
            if (m.Id == _self || old.ContainsKey(m.Id)) continue;
            if (lanes.TryGetValue(m.Id, out var lane))
            {
                lane.Wakeup.Set();
                StartLane(lane);   // ★ 起 lane 专用线程——同 BecomeLeader 锁序注释
            }
        }
        _logger?.LogInformation("Replication 配置切换：members={Count}", config.Count);
    }

    /// <summary>停链（唤醒等待中的链——检查 Active 后退出）。</summary>
    private static void StopLanes(Dictionary<NodeId, PeerLane> lanes)
    {
        foreach (var lane in lanes.Values) StopLane(lane);
    }

    /// <summary>任务组 drain（状态机 Dispose 路径——lane 链与出站请求全部退出后存储层才释放）。</summary>
    /// <returns>完成时窗口定时器已停、lane 任务组已 drain（幂等）。</returns>
    public async ValueTask DisposeAsync()
    {
        _windowFlushTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _windowFlushTimer?.Dispose();
        await _laneTasks.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>停全部 lane 并等待退出（leader 卸任路径——与 DisposeAsync 的差别：不释放任务组，可重新当选）。</summary>
    /// <returns>完成时全部 lane 已停止（2s 总预算内等待退出——超时残留由进程收尾）。</returns>
    public async ValueTask StopAllLanesAndWaitAsync()
    {
        Volatile.Write(ref _isLeaderFlag, 0);
        _windowFlushTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);   // 窗到期停表
        var lanes = _lanes;
        StopLanes(lanes);
        _lanes = [];
        lock (_windowLock)
        {
            _pendingCount = 0;
            _pendingBytes = 0;
        }
        // ★ lane 线程有界等退（Worker 句柄逐 lane 等待——总预算 2s；超时链残留由进程收尾，
        //   存储层 Dispose 在节点各自 StopAsync 之后才执行）
        var deadline = _clock.GetMsTimestamp() + 2000;
        foreach (var lane in lanes.Values)
        {
            var remain = (int)Math.Max(0, deadline - _clock.GetMsTimestamp());
            if (remain == 0) break;
            if (lane.Worker is not { } w) continue;
            try { await w.WaitAsync(TimeSpan.FromMilliseconds(remain), _lifecycleCt).ConfigureAwait(false); }
            catch (TimeoutException) { }
            catch (OperationCanceledException) { }
        }
    }

    // ═══ lane 生命周期（长稳定循环——专用线程泵域，不进 TaskSink）═══

    /// <summary>lane 是长稳定循环（节点 leader 任期级），不属 fire-and-forget——不进 TaskSink：
    /// 专用 LongRunning 线程 + AsyncPump 泵域（续体回流 lane 自己的线程，池不在复制关键路径，
    /// 2026-09-03 活性判例：池续体丢失 = 心跳停发 = 全 follower 选举超时）。线程句柄挂
    /// <see cref="PeerLane.Worker"/>，卸任/Dispose 由 <see cref="StopAllLanesAndWaitAsync"/> 有界等退。</summary>
    private void StartLane(PeerLane lane)
    {
        var pump = new AsyncPump($"raft-lane-{lane.Id}");
        lane.Worker = Task.Factory.StartNew(
            () =>
            {
                try { pump.Run(() => RunLaneAsync(lane), _lifecycleCt); }
                catch (Exception ex) { _logger?.LogError(ex, "Replication lane 泵外逃逸异常：peer={Peer}", lane.Id); }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
    }

    private static void StopLane(PeerLane lane)
    {
        lane.Active = false;
        lane.Wakeup.Set();
    }

    // ═══ Leader 主路径（信号入口——循环/接收线程调用，只投信号不发送）═══

    /// <summary>本地有新条目（append 线程批尾 flush 后调用）——三维度攒批判定：
    /// 窗口 0（默认）即时唤醒滞后 lane；窗口 &gt; 0 攒批等 tick 触发（条数/字节即时触发）。</summary>
    /// <param name="count">新增条目数（累计达 BatchSize 即时触发）。</param>
    /// <param name="bytes">新增字节数（累计达 MaxBatchBytes 即时触发；0 = 不约束字节维度）。</param>
    public void NotifyAppended(int count = 1, long bytes = 0)
    {
        if (Volatile.Read(ref _isLeaderFlag) == 0) return;
        if (_policy.BatchWindow <= TimeSpan.Zero)
        {
            NotifyLanesDirect();
            return;
        }
        // 攒批：累计 + 即时维度判定（条数/字节）；窗 epoch 起点 = 首条 pending（0→1）——
        // 启动窗到期定时器（真实窗口粒度，不骑心跳 tick）
        lock (_windowLock)
        {
            if (_pendingCount == 0)
            {
                _pendingSince = _clock.GetMsTimestamp();
                _windowFlushTimer?.Change(_policy.BatchWindow, Timeout.InfiniteTimeSpan);
            }
            _pendingCount += count;
            _pendingBytes += bytes;
            if (_pendingCount >= _policy.BatchSize
                || (_policy.MaxBatchBytes > 0 && _pendingBytes >= _policy.MaxBatchBytes))
            {
                _pendingCount = 0;
                _pendingBytes = 0;
            }
            else return ;   // 锁内未达即时阈值——等窗到期/心跳兜底
        }
        NotifyLanesDirect();
    }

    /// <summary>★ 窗到期 flush（one-shot Timer 回调——判例 2026-09-04）：时间维到期独立触发，
    /// 与心跳 tick 的 <see cref="HandleTick"/> 并发安全（锁内判定幂等——先到者清，后到者见
    /// pending==0 无操作）。未到期早醒（Change 竞态/心跳 tick 先清）→ 按剩余时间重挂。</summary>
    private void FlushWindowIfDue()
    {
        if (_policy.BatchWindow <= TimeSpan.Zero) return;
        lock (_windowLock)
        {
            if (_pendingCount <= 0) return;
            var elapsed = _clock.GetMsTimestamp() - _pendingSince;
            if (elapsed < (long)_policy.BatchWindow.TotalMilliseconds)
            {
                // 早醒（tick 清 0→新 epoch 又起 / Change 精度）——按真实剩余重挂
                _windowFlushTimer?.Change(TimeSpan.FromMilliseconds(_policy.BatchWindow.TotalMilliseconds - elapsed), Timeout.InfiniteTimeSpan);
                return;
            }
            _pendingCount = 0;
            _pendingBytes = 0;
        }
        NotifyLanesDirect();
    }

    /// <summary>驱动滞后 lane（滞后 = 有已提交数据可发）：★ 纯唤醒信号（分配波④ W2 驱动面
    /// 去池跳——空闲 lane 的池任务直驱取消：每命令每 lane 一次 TaskSink 任务 + 闭包 +
    /// state machine box 的分配与调度跳消灭，链循环在 lane 专用泵线程被事件唤醒后驱动，
    /// 泵唤醒 ~µs 级承接单命令延迟）。
    /// <para>★ 执行域隔离语义保持（旧"禁内联"判例）：信号只唤醒不在调用方线程执行——
    ///   leader 循环线程（本方法调用方）绝不穿透 follower 日志门/持久化（真磁盘 26ms+/轮
    ///   占用共识循环会心跳饥饿），驱动域恒在 lane 泵线程。</para></summary>
    private void NotifyLanesDirect()
    {
        foreach (var lane in _lanes.Values)
        {
            if (Volatile.Read(ref lane.NextIndex) > _store.PersistedIndex) continue;   // 无已提交滞后量
            lane.Wakeup.Set();
        }
    }

    /// <summary>★ 健康 leader 心跳直唤 lane（判例 2026-09-04——饥饿不对称修复）：节点心跳
    /// deadline 到期（registry pacer 线程回调）时，若 leader 且<b>本地无持久化债</b>
    /// （PersistedIndex == AllocatedIndex——无未落盘条目）→ 直唤各 lane（跳过 raft 主循环
    /// 一跳——循环被调度饥饿/事件积压延迟时心跳不再连带断供，健康 leader 挺过 ≤心跳窗的
    /// 饥饿）。★ 病 leader 红线保持：有持久化债（allocated > persisted——fsync 门挂住/
    /// 真盘慢写）→ 不直唤——心跳静默 → follower 150-300ms 自发换届（病 leader 活性自愈
    /// 契约不变，LeaderLocal 在途等待者换届取消路径照旧）。主循环 HandleTickAsync 保留
    /// 为兜底（债消后的 flush 路径/批窗到期照常经循环驱动）。</summary>
    public void WakeLanesIfCaughtUp()
    {
        if (Volatile.Read(ref _isLeaderFlag) == 0) return;
        if (_store.PersistedIndex < _store.AllocatedIndex) return;   // 本地持久化债——静默（病 leader 交由换届）
        foreach (var lane in _lanes.Values)
            lane.Wakeup.Set();
    }

    /// <summary>
    /// 心跳 tick（spec-02 §6——周期循环；空批携带 leaderCommit 推进 follower 提交）。
    /// 链形态：只广播唤醒信号——心跳发送/在途超时重试检查在链内（每 lane 独立节流）。
    /// </summary>
    public void HandleTick()
    {
        if (Volatile.Read(ref _isLeaderFlag) == 0) return;
        // ★ 攒批时间维度（窗口 > 0 且到期 → 批量唤醒未决条目）——锁内与窗到期 Timer 并发安全
        //   （心跳 tick 是窗到期的兜底路径——Timer 停摆/精度偏差时仍按期 flush）
        if (_policy.BatchWindow > TimeSpan.Zero)
        {
            lock (_windowLock)
            {
                if (_pendingCount > 0
                    && _clock.GetMsTimestamp() - _pendingSince >= (long)_policy.BatchWindow.TotalMilliseconds)
                {
                    _pendingCount = 0;
                    _pendingBytes = 0;
                }
            }
        }
        foreach (var lane in _lanes.Values)
            lane.Wakeup.Set();
    }

    /// <summary>
    /// AppendEntries 应答投递（spec-02 §4/§5）：★ 可入侵直跑——lane 空闲时在应答
    /// 到达线程直接续跑链逻辑（消费应答 + commit 判定 + 续发，省一次线程池续体调度）；
    /// lane 忙（在途发送/链循环驱动中）→ 写槽 + 唤醒信号。
    /// <para>★ 同线程自投递免唤醒：应答在<b>驱动线程自己的发送栈内</b>同步回投（InProcess 直排）
    ///   时——驱动循环顶即将消费槽，Set 只会触发链循环空转唤醒（每条一跳线程池调度的系统性空转源）。
    ///   经 <see cref="PeerLane.DriverId"/> 比对免除。</para>
    /// <para>★ 入口 = <see cref="_onRespArrived"/> 路由（状态机 term/ReadIndex 前处理后转投）。</para>
    /// </summary>
    /// <param name="peer">应答来源节点 ID。</param>
    /// <param name="resp">AppendEntries 应答（成功/失败 hint）。</param>
    /// <param name="ct">取消令牌（lane 驱动循环内取消感知）。</param>
    /// <returns>完成时投递已受理（直跑续跑结束或槽投递完成——非 leader/无 lane 时立即完成忽略）。</returns>
    public ValueTask HandleRespAsync(NodeId peer, AppendEntriesResp resp, CancellationToken ct = default)
    {
        if (Volatile.Read(ref _isLeaderFlag) == 0) return ValueTask.CompletedTask;
        if (!_lanes.TryGetValue(peer, out var lane) || !_config.Contains(peer)) return ValueTask.CompletedTask;
        Volatile.Write(ref lane.PendingResp, resp);
        if (Volatile.Read(ref lane.Running) != 0)
        {
            if (Volatile.Read(ref lane.DriverId) == Environment.CurrentManagedThreadId)
                return ValueTask.CompletedTask;   // 同线程自投递——驱动循环顶消费（免唤醒）
            lane.Wakeup.Set();   // 其他线程驱动中——槽投递 + 唤醒（驱动循环消费）
            return ValueTask.CompletedTask;
        }
        if (Interlocked.CompareExchange(ref lane.Running, 1, 0) != 0)
        {
            lane.Wakeup.Set();   // 抢占失败（并发驱动者）——信号兜底
            return ValueTask.CompletedTask;
        }
        return DriveCycleAsync(lane);   // ★ 空闲——应答线程直跑
    }

    /// <summary>
    /// 快照安装完成（spec-03 §4）：投伪应答（Success / MatchIndex = N₀）到 lane 槽——链内消费后
    /// nextIndex = N₀+1（快照已覆盖 [Head..N₀]——增量从 N₀+1 接续）+ 在途清 + 立即推增量。
    /// <para>★ 槽投递形态（lane 单写者契约）：nextIndex/matchIndex/在途全部由链写——本方法只在
    ///   投递点写槽（快照安装期间无真实在途批应答，无覆盖风险）。</para>
    /// </summary>
    /// <param name="peer">快照安装目标节点 ID。</param>
    /// <param name="n0">快照覆盖点 N₀（投伪应答 MatchIndex = 此值——nextIndex 续接 N₀+1）。</param>
    public void CompleteInstall(NodeId peer, long n0)
    {
        if (Volatile.Read(ref _isLeaderFlag) == 0 || !_config.Contains(peer)) return;
        if (!_lanes.TryGetValue(peer, out var lane)) return;
        Volatile.Write(ref lane.PendingResp, new AppendEntriesResp
        {
            Term = _store.Term,
            Success = true,
            MatchIndex = n0,
            ConflictTerm = 0,
            ConflictIndex = 0,
            SnapshotIndex = n0,
        });
        lane.Wakeup.Set();
        _logger?.LogInformation("Replication 快照安装完成：peer={Peer} next={Next}", peer, n0 + 1);
    }

    // ═══ per-peer 续体链（spec-10 D1——单写者：lane 状态只被本链写）═══

    /// <summary>
    /// lane 主循环——信号唤醒 → 抢执行权 → 驱动一步或多步 → 回挂。
    /// <para>★ 执行互斥（<see cref="PeerLane.Running"/>）：链循环与应答线程直跑
    /// （<see cref="HandleRespAsync"/>）竞争同一逻辑链的执行权——CAS 抢占，输家回挂；
    ///   直跑结束检查应答槽补唤醒（释放间隙的投递不丢）。</para>
    /// </summary>
    private async Task RunLaneAsync(PeerLane lane)
    {
        try
        {
            while (lane.Active && !_lifecycleCt.IsCancellationRequested)
            {
                await lane.Wakeup.WaitAsync(_lifecycleCt);
                lane.Wakeup.Reset();
                if (!lane.Active || Volatile.Read(ref _isLeaderFlag) == 0) break;
                if (Interlocked.CompareExchange(ref lane.Running, 1, 0) != 0)
                    continue;   // 直跑驱动在跑——回挂（其结束在有待处理项时补 Set）
                await DriveCycleAsync(lane);
            }
        }
        catch (OperationCanceledException) { /* 生命周期取消——链退出 */ }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Replication lane 链异常终止（peer={Peer}）——复制停滞，等心跳 tick 兜底", lane.Id);
        }
    }

    /// <summary>
    /// 链驱动循环状态机（W3 剩余面——原 DriveAndReleaseAsync/DriveLaneAsync/HandleRespCoreAsync/
    /// SendAppendCoreAsync 四状态机箱合一，单箱/驱动周期）：消费应答槽 → 发送判定 → 发送段，
    /// 直到无事可做，finally 释放执行权（释放间隙投递的应答补唤醒链循环）。
    /// <para>★ 泵域亲和：全链 await 均在调用方上下文（lane 泵线程域/应答线程直跑）——续体回流不变。</para>
    /// <para>发送判定：在途超时（应答丢失自愈，论文 §5.3 持续重试）→ 清在途重发；无在途且有滞后量
    /// → 立即推批；无在途且距上次发送 ≥ 心跳间隔 → 空批心跳（携带 leaderCommit）。</para>
    /// <para>异常隔离（含 fire-and-forget 直跑路径——未观察异常静默丢失，记日志由心跳 tick 兜底重驱动）。</para>
    /// </summary>
    private async ValueTask DriveCycleAsync(PeerLane lane)
    {
        Volatile.Write(ref lane.DriverId, Environment.CurrentManagedThreadId);
        try
        {
            // ★ 微攒批 linger（ReplicationPolicy.LingerMilliseconds——亚 tick 粒度）：首轮发送前
            //    等片刻让在途 append 落进同一批，摊薄 per-RPC 开销（TCP/真实网络 1-2ms 档）。
            //    仅在"无在途 + 有已提交滞后"时停留（心跳/追平路径零延迟）；linger 与本循环的
            //    5ms BatchWindow 判定窗无关（不占状态机 tick）。
            if (_policy.LingerMilliseconds > 0
                && Volatile.Read(ref lane.InFlightCount) == 0
                && Volatile.Read(ref lane.NextIndex) <= _store.PersistedIndex)
                await _clock.Delay(_policy.LingerMilliseconds, _lifecycleCt);   // 泵域内不写 CA(false)——续体回流 lane 线程

            while (true)
            {
                // 1) 消费应答槽（投递方先写槽后 Set——Reset 前写入的本轮消费，Reset 后写入的下轮醒）。
                //    ★ 应答处理内联（spec-02 §4/§5）：成功 → matchIndex/nextIndex 推进 + commitIndex
                //    判定（空闲即推由发送判定覆盖——仍有滞后量则本轮发送）；失败 → conflict hint
                //    回退 + 立即重试旗标（发送段直落）。
                var conflictRetry = false;
                if (Interlocked.Exchange(ref lane.PendingResp, null) is { } resp)
                {
                    // ★ 窗口 N：每应答减一；归零才清时刻（乱序应答按批计数）
                    var cnt = Volatile.Read(ref lane.InFlightCount);
                    if (cnt > 1) Volatile.Write(ref lane.InFlightCount, cnt - 1);
                    else { Volatile.Write(ref lane.InFlightCount, 0); Volatile.Write(ref lane.InFlightSince, 0); }
                    if (resp.Success)
                    {
                        var matchIndex = resp.MatchIndex;
                        // ★ 二期-F2：witness 上报的是断言高水位——clamp 到 Leader 自身日志尾
                        //   （高水位可能高于本 Leader 前沿的陈旧区间；计入 commit 需本 Leader
                        //   真正持有该条目——log[n].term==currentTerm 检查对不存在的 n 无意义）
                        if (lane.IsWitness && matchIndex > _store.LastLogIndex)
                            matchIndex = _store.LastLogIndex;
                        if (matchIndex > lane.MatchIndex)
                        {
                            Volatile.Write(ref lane.MatchIndex, matchIndex);
                            Volatile.Write(ref lane.NextIndex, matchIndex + 1);
                        }
                        _onFollowerAck?.Invoke(lane.Id);   // 成功应答（租约读多数派确认/learner 追平晋级订阅面）
                        await TryAdvanceCommitAsync(_lifecycleCt);
                    }
                    else if (lane.IsWitness)
                    {
                        // ★ F2：断言流无回退语义（witness 不做 PrevLog 校验——拒绝只可能来自
                        //   任期旧；等下一拍断言自愈）
                    }
                    else if (resp.ConflictIndex >= Volatile.Read(ref lane.NextIndex))
                    {
                        // ★ 前向 hint（ConflictIndex ≥ next）：follower 断言 ConflictIndex-1 已持久化
                        //   （快照边界 hint=N₀+1——"该走快照衔接"；异常路径 hint=PersistedIndex+1——
                        //   "近尾续传"）。跳前收敛 + matchIndex 前推计票。★ min 回退公式对前向 hint
                        //   是不动点（min(next-1, hint) 恒 ≤ next-1 < hint——重发同区→再拒→死循环，
                        //   MultiNode@20 实测 261 万次/13 分钟零进展实锤）
                        var fwd = resp.ConflictIndex;
                        Volatile.Write(ref lane.NextIndex, Math.Max(fwd, 1));
                        if (fwd - 1 > lane.MatchIndex)
                            Volatile.Write(ref lane.MatchIndex, fwd - 1);
                        await TryAdvanceCommitAsync(_lifecycleCt);
                        conflictRetry = true;   // 立即重试（下一轮 RPC）
                    }
                    else
                    {
                        // ★ 冲突回退（spec-02 §4 定案：hint 加速——一次跳到 min(nextIndex-1, 该 term 首条)；
                        //   拒绝一次回退一格是正确性基石——min 保证单调递减）
                        var next = Volatile.Read(ref lane.NextIndex);
                        var target = resp.ConflictIndex > 0
                            ? Math.Min(next - 1, resp.ConflictIndex)
                            : next - 1;
                        Volatile.Write(ref lane.NextIndex, Math.Max(target, 1));
                        _logger?.LogDebug("Replication 冲突回退：peer={Peer} next={Next} conflictTerm={Term} conflictIndex={Index}",
                            lane.Id, Math.Max(target, 1), resp.ConflictTerm, resp.ConflictIndex);
                        conflictRetry = true;   // 立即重试（下一轮 RPC）
                    }
                }

                // 2) 发送判定（冲突重试绕过——原冲突路径在活性终检前直发，发送段护栏只拦 leader 标志）
                var now = _clock.GetMsTimestamp();
                if (!conflictRetry)
                {
                    if (!lane.Active || Volatile.Read(ref _isLeaderFlag) == 0) return;
                    var inFlight = Volatile.Read(ref lane.InFlightSince);
                    if (inFlight != 0)
                    {
                        if (now - inFlight <= (long)_heartbeatRetryAfter.TotalMilliseconds)
                        {
                            // ★ 窗口 N（多批在途）：未满窗口且仍有滞后量 → 继续发批（批间不等待应——
                            //   流水线吞吐主路）；窗口满/无滞后 → 等应答
                            if (!(Volatile.Read(ref lane.InFlightCount) < _maxInFlightPerPeer
                                && Volatile.Read(ref lane.NextIndex) <= _store.PersistedIndex))
                                return;
                        }
                        else
                        {
                            // ★ 在途超时 = 重试窗口（应答丢失自愈，论文 §5.3 持续重试）——清在途后直落判定
                            Volatile.Write(ref lane.InFlightSince, 0);
                            Volatile.Write(ref lane.InFlightCount, 0);
                        }
                    }
                    // ★ 滞后判定：滞后 = 有<b>已提交</b>数据可发——未提交部分等 append 线程批尾提交后
                    //    下一轮信号；若按 AllocatedIndex 判滞后，未提交窗口会驱动空批连环——
                    //    resp 清在途、滞后仍成立、读批仍空，同步循环活锁
                    if (!(Volatile.Read(ref lane.NextIndex) <= _store.PersistedIndex
                        || now - Volatile.Read(ref lane.LastSendAt) >= (long)_heartbeatInterval.TotalMilliseconds))
                        return;   // 无事可做
                }
                else
                {
                    now = _clock.GetMsTimestamp();   // 冲突重试时刻（时戳直读语义不变）
                }

                // 3) 发送段（prevLog 检查 + 本地持久化等待——发送内容必须已持久化 + 读批 + 出站
                //    请求提交，per-call 超时 = 在途重试窗；空批 = 心跳）。
                //    ★ 在途登记先行：异常/护栏早退路径逐点减计数（归零清时刻）——下次唤醒重试。
                try
                {
                    // ★ 降级竞态护栏（伪同任期 AE 封堵判例 2026-09-02）：标志已清 = 本链已非 leader 发送域——
                    //   早退（BecomeFollower 已停链，此处拦截在途任务残留段）
                    if (Volatile.Read(ref _isLeaderFlag) == 0) continue;
                    if (Volatile.Read(ref lane.InFlightSince) == 0)
                        Volatile.Write(ref lane.InFlightSince, now);
                    Volatile.Write(ref lane.InFlightCount, Volatile.Read(ref lane.InFlightCount) + 1);
                    Volatile.Write(ref lane.LastSendAt, now);

                    var next = Volatile.Read(ref lane.NextIndex);
                    // ★ nextIndex 超前本地分配区（换届残留/本节点分叉尾被截后 allocated 回退——乐观初始化值
                    //   或迟到应答推高）：prevLogTerm 读取会越界抛——每次心跳都断（该 peer 心跳永久丢失
                    //   → 选举风暴）。夹回 allocated+1 自愈（下一条要发的 index 不可能超过自己日志尾+1）。
                    if (next > _store.AllocatedIndex + 1)
                    {
                        next = _store.AllocatedIndex + 1;
                        Volatile.Write(ref lane.NextIndex, next);
                    }

                    // ★ 二期-F2（DDR-F2）：witness 断言流——恒断言 Leader 日志前沿
                    //   （空 entries + prevLog=前沿；不回退、不发体、不触发快照安装）。
                    //   Witness 无日志体，nextIndex/批复制全族语义不适用；MatchIndex 由
                    //   应答（高水位）上报，commit 统计侧 clamp（见应答段）。
                    if (lane.IsWitness)
                    {
                        var wTerm = _store.Term;
                        if (Volatile.Read(ref _isLeaderFlag) == 0)
                            continue;
                        var witnessReq = new AppendEntriesReq
                        {
                            Term = wTerm,
                            LeaderId = _self,
                            PrevLogIndex = _store.LastLogIndex,
                            PrevLogTerm = _store.LastLogTerm,
                            EntriesRegion = default,
                            LeaderCommit = _commitIndexProvider(),
                        };
                        SubmitAppend(lane, witnessReq);
                        continue;
                    }

                    // ★ 快照判定（spec-03 §2）：nextIndex ≤ 本地快照覆盖点——prevLog 无法用主数据衔接 → 快照安装
                    if (next <= _store.SnapshotIndex)
                    {
                        _logger?.LogInformation("Replication 触发快照安装：peer={Peer} next={Next} snapshot={Snap}",
                            lane.Id, next, _store.SnapshotIndex);
                        await _onSnapshotNeeded(lane.Id);
                        continue;
                    }

                    var prevLogIndex = next - 1;
                    // ★ 只读已提交区：leader 本地提交单点在 append 线程批尾——链不碰写路径
                    //   （消除跨线程写锁竞争 park 税）；未提交区间的条目等 append 线程批尾提交后
                    //   下一轮信号再发（心跳 tick 兜底唤醒）
                    long prevLogTerm = 0;
                    // ★ 快照边界 prev（== N₀）也走真 term 读取（适配器从压缩/导入面供边界 term）——
                    //   发 prevLogTerm=0 会被无快照 follower 的 term 比对拒绝（hint 退→安装→换届风暴，
                    //   HDD DIO+WD MultiNode 实测 t2→134）——"follower 信任边界"仅对其已有快照成立。
                    if (prevLogIndex > 0 && prevLogIndex >= _store.SnapshotIndex)
                    {
                        // ★ 空批心跳快速路径（判例 2026-09-04——心跳零 WAL 读）：无滞后（next >
                        //   PersistedIndex = 纯心跳、无新条目可发）且 prevLogIndex == 缓存锚——term
                        //   直接取缓存（entry term 不可变；index 锚比对排除截断/回退/快照变动）。
                        //   免 WaitForPersistedAsync + ReadLogTermAsync——存储锁竞争（宿主快照压缩）
                        //   不再卡心跳——150-300ms 选举窗活性关键路径与 WAL 解耦。
                        if (next <= _store.PersistedIndex || prevLogIndex != lane.CachedPrevIndex)
                        {
                            await _store.WaitForPersistedAsync(prevLogIndex, _lifecycleCt);
                            prevLogTerm = await _store.ReadLogTermAsync(prevLogIndex, _lifecycleCt);
                            lane.CachedPrevIndex = prevLogIndex;
                            lane.CachedPrevTerm = prevLogTerm;
                        }
                        else
                        {
                            prevLogTerm = lane.CachedPrevTerm;
                        }
                    }

                    // ★ lane 域条目区直写（wire v3——分配/拷贝双杀）：CountEntries 前置计数 →
                    //   store 按 RaftEntriesRegion 布局直写条目区（单拷贝存储→帧），term 逐条
                    //   回填 TermsExact。writer 为 lane 复用缓冲：SubmitAppend 在 SubmitFast 首段
                    //   同步编码完 EntriesRegion（帧租借数组），返回后本段即可 Reset 复用
                    //   （单链串行，无并发复用）。
                    var writer = lane.RegionWriter ??= new PooledBufferWriter();
                    writer.Reset();
                    var terms = lane.TermsExact;
                    if (next <= _store.PersistedIndex)
                    {
                        var n = _store.CountEntries(next, _policy.BatchSize);
                        if (n > 0)
                        {
                            if (terms is not { } buf || buf.Length < n)
                                terms = lane.TermsExact = new long[n];
                            n = await _store.WriteEntriesToAsync(next, n, writer, terms, _lifecycleCt);
                        }
                        // ★ 空读护栏（活锁家族结构性收口）：滞后判定成立（next ≤ PersistedIndex）却零产出
                        //   ——状态假设被违反（读路径与提交水位/锚点的任何竞态形态）。旧形态此处仍构建
                        //   空批发送→应答清在途→滞后仍成立→立即重发——零进展热循环烧 CPU（两轮 dotnet-stack
                        //   实锤判例：lane 在读批循环无限打转、进程膨胀 GB 级）。护栏形态：清在途计数后回挂
                        //   ——下一信号/tick（5ms）重驱，活锁变有界自愈。
                        if (n == 0)
                        {
                            writer.Reset();
                            var c0 = Volatile.Read(ref lane.InFlightCount);
                            if (c0 > 1) Volatile.Write(ref lane.InFlightCount, c0 - 1);
                            else { Volatile.Write(ref lane.InFlightCount, 0); Volatile.Write(ref lane.InFlightSince, 0); }
                            _logger?.LogWarning("Replication 空读护栏：next={Next} ≤ persisted={Persisted} 但读零条（回挂等重驱）",
                                next, _store.PersistedIndex);
                            continue;
                        }
                        // ★ 乐观推进（窗口 N 流水线）：批区间随发送前移——下一批不重叠
                        //   （旧窗口 1 形态 nextIndex 只在应答推进，窗口 N 下发重复批 + follower
                        //   幂等截尾开销；应答失败经 conflict hint 回退、乱序应答经 matchIndex
                        //   Max 判定防回退）
                        Volatile.Write(ref lane.NextIndex, next + n);
                    }

                    // ★ 任期快照 + 终检（顺序关键）：先读任期后读标志——StepDown 已按"标志清零先于
                    //   任期抬升"排序，故"读到新任期"必然伴随"标志已零"→ 终检拦截；读到旧任期则
                    //   发送旧任期批——对端高任期直接拒绝（无害）。拦截则废弃本批并清在途计数回挂。
                    var term = _store.Term;
                    if (Volatile.Read(ref _isLeaderFlag) == 0)
                    {
                        var c3 = Volatile.Read(ref lane.InFlightCount);
                        if (c3 > 1) Volatile.Write(ref lane.InFlightCount, c3 - 1);
                        else { Volatile.Write(ref lane.InFlightCount, 0); Volatile.Write(ref lane.InFlightSince, 0); }
                        continue;
                    }
                    var req = new AppendEntriesReq
                    {
                        Term = term,
                        LeaderId = _self,
                        PrevLogIndex = prevLogIndex,
                        PrevLogTerm = prevLogTerm,
                        EntriesRegion = writer.WrittenMemory,
                        LeaderCommit = _commitIndexProvider(),
                    };
                    SubmitAppend(lane, req);
                }
                catch (Exception ex)
                {
                    // 发送失败 = 尽力送达（传输丢弃）——减计数（归零清时刻），下次唤醒重试
                    var c2 = Volatile.Read(ref lane.InFlightCount);
                    if (c2 > 1) Volatile.Write(ref lane.InFlightCount, c2 - 1);
                    else { Volatile.Write(ref lane.InFlightCount, 0); Volatile.Write(ref lane.InFlightSince, 0); }
                    _logger?.LogWarning(ex, "Replication 发送失败：peer={Peer}", lane.Id);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Replication lane 驱动异常（peer={Peer}）——等下一信号/心跳 tick 重驱动", lane.Id);
        }
        finally
        {
            Volatile.Write(ref lane.DriverId, 0);
            Volatile.Write(ref lane.Running, 0);
            if (Volatile.Read(ref lane.PendingResp) is not null)
                lane.Wakeup.Set();   // 释放间隙投递——补唤醒（槽空则链挂起等下一外部信号）
        }
    }

    /// <summary>
    /// 出站 AppendEntries 帧编码 + 发送提交（fire-and-forget 受控形态）：
    /// ★ 编码在提交线程同步完成（SubmitFast 首段内联语义的显式化——帧租借数组在
    ///   <see cref="SubmitAppend"/> 内编码满，<see cref="SendAppendRpcAsync"/> 只做发送/应答路由），
    ///   RegionWriter/TermsExact 随 SubmitAppend 返回即可复用（单链串行）。
    /// <para>★ 应答到达经 <see cref="_onRespArrived"/> 路由（状态机 term/ReadIndex 前处理 →
    ///   HandleRespAsync 投槽/直跑）；失败/超时静默（per-call 超时 = 在途重试窗——lane 窗口
    ///   超时机制自愈）。帧数组发送完成后归还池（每批 KB 级分配归零）。</para>
    /// </summary>
    private void SubmitAppend(PeerLane lane, AppendEntriesReq req)
    {
        var len = RaftRpcCodec.ComputeLength(req);
        var frame = System.Buffers.ArrayPool<byte>.Shared.Rent(len);
        RaftRpcCodec.EncodeInto(frame, req);   // ★ 同步消费 req.EntriesRegion（RegionWriter 缓冲）——返回后 writer 可复位
        lane.PendingFrame = frame;
        lane.PendingFrameLength = len;
        // ★ 缓存委托（lane 惰性初始化一份）——每批免闭包 display class 分配
        lane.SendFrameFunc ??= ct => SendAppendRpcAsync(lane, ct);
        _laneTasks.SubmitFast(lane.SendFrameFunc);
    }

    /// <summary>出站 RPC 发送本体（帧经 <see cref="PeerLane.PendingFrame"/> 槽传递——首段内联读，
    /// 同 lane 泵线程串行提交；发送完成后帧归还池）。
    /// <para>★ W3 乙案手写状态机（去 async 箱）：零挂起/单挂起纯同步推进，真挂起经池化
    ///   <see cref="PooledValueTaskSource"/> 挂起（每批每 lane 一个 state machine box 消灭）。
    ///   两挂起点：① 传输发送（常态——真 RTT）；② 应答路由（低频——lane 忙时同步完成）。
    ///   ★ 源级取消不武装——取消经传输层 OCE 以尽力送达语义自然归一（规避池化 builder 的
    ///   取消/完成竞态雷区；组 Dispose drain 语义不变：取消即时收敛）。</para></summary>
    private ValueTask SendAppendRpcAsync(PeerLane lane, CancellationToken ct)
    {
        var frame = lane.PendingFrame!;
        var len = lane.PendingFrameLength;
        lane.PendingFrame = null;   // 消费即清——帧生命周期归本状态机
        ValueTask<byte[]> send;
        try
        {
            send = _transport.SendRequestAsync(lane.Id, ProtocolIds.Raft,
                frame.AsMemory(0, len), _rpcOptions, ct);
        }
        catch (Exception ex) when (IsBenignSendFault(ex))
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(frame);   // 尽力送达
            return ValueTask.CompletedTask;
        }
        if (send.IsCompletedSuccessfully)
        {
            ValueTask? route;
            try
            {
                route = RouteSendResult(lane, send.Result);
            }
            catch (Exception ex)
            {
                ReturnFrame(frame);   // 路由同步抛（非尽力面）——帧归还后上抛（任务组观测）
                if (!IsBenignSendFault(ex)) throw;
                return ValueTask.CompletedTask;
            }
            if (route is null)
            {
                ReturnFrame(frame);
                return ValueTask.CompletedTask;
            }
            // 发送同步完成、路由真挂起——状态机从阶段 1 接管（帧所有权移交状态）
            var state0 = SendRpcState.Rent(this, lane, frame);
            state0.AwaitRoute(route.Value);
            return new ValueTask(state0.Source, state0.Source.Version);
        }
        if (send.IsFaulted)
        {
            var ex = ExtractSendFault(send);
            System.Buffers.ArrayPool<byte>.Shared.Return(frame);
            if (!IsBenignSendFault(ex)) throw ex;   // 非尽力语义异常上抛（任务组观测）
            return ValueTask.CompletedTask;
        }
        // 真挂起——池化完成源 + 池化状态（零箱）
        var state = SendRpcState.Rent(this, lane, frame);
        state.AwaitSend(send);
        return new ValueTask(state.Source, state.Source.Version);
    }

    /// <summary>尽力送达异常面（IO/超时/取消——lane 在途超时窗清计数重试，spec-02 §6 自愈）。</summary>
    private static bool IsBenignSendFault(Exception ex)
        => ex is IOException or TimeoutException or OperationCanceledException;

    /// <summary>同步完成路径的异常提取（IsFaulted 态——异常在聚合 Task 里）。</summary>
    private static Exception ExtractSendFault(ValueTask<byte[]> send)
    {
        try
        {
            _ = send.Result;
        }
        catch (Exception ex)
        {
            return ex;
        }
        return new InvalidOperationException("发送任务已故障但异常不可达。");
    }

    private static void ReturnFrame(byte[] frame)
        => System.Buffers.ArrayPool<byte>.Shared.Return(frame);

    /// <summary>应答消费 + 路由（发送完成的两路共用尾段）。
    /// ★ 帧所有权归调用方（本方法零归还——双归还判例：帧被二次入池 = ArrayPool 跨租户污染，
    ///   载荷长度错乱 → 分配爆炸）。返回 null = 全部完成；非 null = 路由真挂起（续体 Finish 收口）。
    /// 非尽力面同步异常上抛（任务组观测——调用方归还帧）。</summary>
    private ValueTask? RouteSendResult(PeerLane lane, byte[] respBytes)
    {
        try
        {
            if (RaftRpcCodec.TryDecode(respBytes, out var rpc) && rpc is AppendEntriesResp resp)
            {
                var route = _onRespArrived(lane.Id, resp);
                if (route.IsCompletedSuccessfully)
                {
#pragma warning disable TCSG137 // 设计必需：已完成 ValueTask 的消费（IsCompletedSuccessfully 已判——同步取结果零阻塞），不取结果则底层箱/源不归还
                    route.GetAwaiter().GetResult();
#pragma warning restore TCSG137
                    return null;
                }
                return route;
            }
            return null;   // 畸形应答——尽力语义
        }
        catch (Exception ex) when (IsBenignSendFault(ex))
        {
            return null;
        }
    }

    /// <summary>出站发送在途状态（池化——每在途批一个，消灭 async 状态机箱）：
    /// 两阶段续驱（发送 → 应答路由），完成时经 <see cref="PooledValueTaskSource.OnCleanup"/> 归还。
    /// ★ 续体链单线程驱动（同一时刻至多一个挂起注册）——帧归还恰一次由阶段纪律保证。</summary>
    private sealed class SendRpcState
    {
        private static readonly System.Collections.Concurrent.ConcurrentBag<SendRpcState> Pool = new();
        private static readonly Action<object?, PooledValueTaskSource> OnSourceCleanup = Cleanup;

        private ReplicationProcess _owner = null!;
        private PeerLane _lane = null!;
        private byte[]? _frame;
        private ValueTaskAwaiter<byte[]> _sendAwaiter;

        /// <summary>任务组观察的完成源（每次操作租借——★ 判例：禁与池化状态常驻配对，
        /// 否则状态归还后 Source 入全局源池可被他用租走，双重所有权 → 版本错乱崩溃）。</summary>
        public PooledValueTaskSource Source = null!;

        /// <summary>发送完成续体（构造期缓存一份——租还复用零委托分配）。</summary>
        private readonly Action _continueSend;
        /// <summary>路由完成续体（同上）。</summary>
        private readonly Action _continueRoute;

        private SendRpcState()
        {
            _continueSend = OnSendCompleted;
            _continueRoute = OnRouteCompleted;
        }

        /// <summary>租借在途发送状态（池化——空池新建；字段装配 + 完成源成对租借）。</summary>
        /// <param name="owner">所属复制进程（应答路由回调宿主）。</param>
        /// <param name="lane">目标对端复制链（发送完成后路由应答）。</param>
        /// <param name="frame">待发送帧（租借数组——所有权移交本状态）。</param>
        /// <returns>就绪的在途发送状态（用毕经完成清理归还池）。</returns>
        public static SendRpcState Rent(ReplicationProcess owner, PeerLane lane, byte[] frame)
        {
            if (!Pool.TryTake(out var s))
                s = new SendRpcState();
            s._owner = owner;
            s._lane = lane;
            s._frame = frame;
            s.Source = PooledValueTaskSource.Rent();   // 每操作一对租借/归还——所有权单线
            s.Source.OnCleanup = OnSourceCleanup;
            s.Source.CleanupState = s;
            return s;
        }

        /// <summary>源完成态清理（GetResult 尾触——状态归还池；帧已由 Finish/Fault 归还）。</summary>
        private static void Cleanup(object? state, PooledValueTaskSource source)
        {
            var s = (SendRpcState)state!;
            source.OnCleanup = null;
            source.CleanupState = null;
            PooledValueTaskSource.Return(source);
            s.Source = null!;
            s._owner = null!;
            s._lane = null!;
            s._frame = null;
            s._sendAwaiter = default;
            Pool.Add(s);
        }

        /// <summary>阶段 0 续体：发送完成 → 消费/路由（真挂起转阶段 1，否则收尾）。</summary>
        private void OnSendCompleted()
        {
            byte[] respBytes;
            try
            {
                respBytes = _sendAwaiter.GetResult();
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
            {
                Finish();   // 尽力送达
                return;
            }
            catch (Exception ex)
            {
                Fault(ex);   // 非尽力语义——源故障上抛（任务组观测）
                return;
            }
            ValueTask? route;
            try
            {
                route = _owner.RouteSendResult(_lane, respBytes);
            }
            catch (Exception ex)
            {
                Fault(ex);   // 路由同步抛（非尽力面）——帧归还后源故障（任务组观测）
                return;
            }
            if (route is null)
            {
                Finish();
                return;
            }
            // 路由真挂起——阶段 1（帧归还随路由完成）
            _frame = null;   // 帧移交路由续体所有权
            var awaiter = route.Value.GetAwaiter();
            awaiter.UnsafeOnCompleted(_continueRoute);
        }

        /// <summary>阶段 1 续体：路由完成 → 收尾（尽力面吞异常 / 非尽力面源故障；帧由状态收回归还）。</summary>
        private void OnRouteCompleted()
        {
            var frame = _frame;
            _frame = null;
            try
            {
#pragma warning disable TCSG137 // 设计必需：续体已由完成事件驱动（UnsafeOnCompleted 回调）——取已完成结果零阻塞，不取结果则底层箱/源不归还
                _routeAwaiterValue.GetResult();
#pragma warning restore TCSG137
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
            {
                FinishWith(frame);
                return;
            }
            catch (Exception ex)
            {
                FaultWith(frame, ex);
                return;
            }
            FinishWith(frame);
        }

        private ValueTaskAwaiter _routeAwaiterValue;
        internal void AwaitSend(ValueTask<byte[]> send)
        {
            _sendAwaiter = send.GetAwaiter();
            _sendAwaiter.UnsafeOnCompleted(_continueSend);
        }

        /// <summary>路由挂起点注册（发送同步完成、路由真挂起的接管路径）。</summary>
        internal void AwaitRoute(ValueTask route)
        {
            _routeAwaiterValue = route.GetAwaiter();
            _routeAwaiterValue.UnsafeOnCompleted(_continueRoute);
        }

        /// <summary>收尾：帧归还（恰一次——续体链单线程驱动）+ 完成源标记（未注册时由
        /// OnCompleted 转发兜底——完成先于注册协议）。</summary>
        private void Finish()
        {
            var frame = _frame;
            _frame = null;
            if (frame is not null)
                ReturnFrame(frame);
            Source.MarkOrComplete();
        }

        private void FinishWith(byte[]? frame)
        {
            if (frame is not null)
                ReturnFrame(frame);
            Source.MarkOrComplete();
        }

        /// <summary>非尽力异常收尾：帧归还 + 源故障（任务组 HandleFault 观测）。</summary>
        private void Fault(Exception ex)
        {
            var frame = _frame;
            _frame = null;
            if (frame is not null)
                ReturnFrame(frame);
            Source.MarkOrFault(ex);
        }

        private void FaultWith(byte[]? frame, Exception ex)
        {
            if (frame is not null)
                ReturnFrame(frame);
            Source.MarkOrFault(ex);
        }
    }

    // ═══ commitIndex 推进（spec-02 §5 + Figure 8 约束——多 lane 并发调用）═══

    /// <summary>多数派 matchIndex（第 majority 大的 matchIndex——含自己）；投票不足 = -1。
    /// 同步方法（栈数组——每应答调用一次，免 List 堆分配）。</summary>
    private long MajorityMatchIndex(ClusterConfig config)
    {
        var matches = config.Count <= 16 ? stackalloc long[16] : new long[config.Count];
        var voterCount = 0;
        foreach (var m in config.Members)
        {
            // ★ learner 的 matchIndex 照常维护（复制进度观测）但不进多数派数组（件 B——
            //   learner 不影响 commitIndex 推进）；★ 二期-F2：witness 计入（投票成员——
            //   Leader 自身 match + witness 断言高水位 clamp 后的 MatchIndex——多数派口径 = voter+witness）
            if (m.Id == _self) { if (m.Role is ClusterMemberRole.Voter or ClusterMemberRole.Witness) matches[voterCount++] = _store.PersistedIndex; }
            else if (m.Role is ClusterMemberRole.Voter or ClusterMemberRole.Witness && _lanes.TryGetValue(m.Id, out var lane)) matches[voterCount++] = Volatile.Read(ref lane.MatchIndex);
        }
        if (voterCount < config.MajorityThreshold) return -1;
        matches[..voterCount].Sort();   // 升序——第 majority 大 = 倒数第 majority 个
        return matches[voterCount - config.MajorityThreshold];
    }

    /// <summary>读 peer 当前 matchIndex（learner 追平晋级判定面；无链路 = false）。</summary>
    /// <param name="peer">查询的 peer 节点 ID。</param>
    /// <param name="matchIndex">输出：peer 的 matchIndex（无链路 = 0）。</param>
    /// <returns>true = peer 在复制表中（matchIndex 有效）；false = 无链路（matchIndex=0）。</returns>
    public bool TryGetMatchIndex(NodeId peer, out long matchIndex)
    {
        if (_lanes.TryGetValue(peer, out var lane))
        {
            matchIndex = Volatile.Read(ref lane.MatchIndex);
            return true;
        }
        matchIndex = 0;
        return false;
    }


    /// <summary>
    /// 多数派 matchIndex 最大值 N（含自己——自己 matchIndex = 本地 PersistedIndex）；
    /// N &gt; commitIndex 且 log[N].term == currentTerm → 推进（★ 只能经当前任期条目直接提交——
    /// 旧任期条目随该次提交间接提交，防"已提交条目被新 leader 覆盖"，spec-02 §5）。
    /// <para>多 lane 并发调用：matchIndex 快照读（lane 单写者——读到旧值只保守推迟推进，
    ///   下次应答再触发）；推进回调（<see cref="_onCommitAdvanced"/>）由状态机侧单调化。</para>
    /// </summary>
    /// <param name="cancellationToken">取消令牌（term 读取 await——取消感知）。</param>
    /// <returns>完成时已按多数派 matchIndex 判定并推进 commit（不满足推进条件时立即完成无副作用）。</returns>
    public async ValueTask TryAdvanceCommitAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _isLeaderFlag) == 0) return;
        var config = _config;
        var n = MajorityMatchIndex(config);
        if (n < 0) return;

        var commit = _commitIndexProvider();
        if (n <= commit) return;
        if (n > _store.SnapshotIndex)
        {
            // ★ Figure 8 约束（spec-02 §5）：只能经当前任期条目的多数派复制直接提交——
            //   旧任期条目随该次提交间接提交（n 在主数据区——读 term 判定）
            var termAt = await _store.ReadLogTermAsync(n, cancellationToken).ConfigureAwait(false);
            if (termAt != _store.Term) return;
        }
        // n ≤ SnapshotIndex → 快照区：快照 = leader 日志权威镜像（N₀ = 快照时刻 PersistedIndex）——
        // 快照覆盖即提交（论文 InstallSnapshot 语义），无 Figure 8 覆盖风险（新 leader 自快照恢复）
        _logger?.LogInformation("Replication 提交推进：commit={Commit}（多数派 match={N}）", n, n);
        _onCommitAdvanced(n);
    }
}
