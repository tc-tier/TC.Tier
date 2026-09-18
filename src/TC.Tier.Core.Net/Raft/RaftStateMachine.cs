using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using TC.Tier.Core.Logging;
using TC.Tier.Core.Observability;
using TC.Tier.Core.Net.Channels;
using TC.Tier.Core.Net.Wire;
using TC.Tier.Core.Primitives;
using TC.Tier.Core.Execution;

namespace TC.Tier.Core.Net.Raft;

/// <summary>
/// Raft 状态机（spec-01 全量——状态集合/转换/任期语义/选举含 PreVote/超时随机化/事件循环骨架；
/// spec-02 follower 侧——AppendEntries 应用与 commitIndex 推进；spec-12 §6 形态映射——RPC 族走
/// 请求回调（CorrId 传输承载、at-most-once 缺省 + raft 定时器自愈）。
/// <para>★ Actor 形态（spec-01 §6.2）：单 worker 循环——取事件 → await handler → 取下一事件；
///   同一时刻一个状态转换在处理（状态机串行化 + 存储端口单写者契约天然满足）。
///   handler 只做快路径（状态判定 + 本地持久化 await）；网络应答/定时器 = 新事件入队或请求任务回投，
///   handler 从不 await 网络应答。</para>
/// <para>★ 挂载（spec-12 §3.5）：StartAsync 经 <see cref="ICoreProtocolPort"/> 内部口注册
///   <see cref="ProtocolIds.Raft"/>（0x01）请求 handler——与使用方协议域同一分发路径（机制零特权）；
///   传输端点生命周期归装配层。</para>
/// <para>★ 复制发送已出循环（spec-10 D1）：Leader 侧 AppendEntries 发送/应答处理/心跳在
///   <see cref="ReplicationProcess"/> per-peer 续体链（peer 间并行）；commit 推进经
///   <see cref="AdvanceCommit"/> 并发单调化回投本机。</para>
/// <para>★ 定时器（spec-01 §6.3）：每状态至多一个活动定时器——循环按最近截止 await（选举超时/
///   心跳），到期入队对应事件；每次触发后重掷随机超时（spec-01 §4.1 规则 4）。</para>
/// <para>★ 降级统一规则（spec-01 §3.2）：任何状态收到更高 term 的 RPC → 先落盘（term, votedFor=∅）
///   → 再应答/转换；应答恒回带 currentTerm。</para>
/// <para>★ 持久化经 <see cref="IRaftStore"/> 端口（spec-12 §8.1——条目 = (Term, Kind, Content)
///   三元组（wire v3 信封之死——kind 结构字段），双水位模型：追加即分配 +
///   <see cref="IRaftStore.WaitForPersistedAsync"/> 应答前同步点）。</para>
/// </summary>
public sealed partial class RaftStateMachine : IAsyncDisposable
{
    private readonly NodeId _self;
    private readonly IRaftStore _store;
    private readonly IProtocolTransport _transport;
    private readonly IApplySink _apply;
    private readonly RaftOptions _options;
    private readonly ISnapshotTransfer? _snapshotTransfer;   // 快照传输协调（spec-03——装配层注入）
    private readonly ILogger? _logger;
    private readonly RaftGroupId _groupId;   // ★ 二期-I6：组维度标签
    private readonly ObservabilityHub.RaftView? _raftView;   // 二期-I2：共识维度视图（null = Disabled）
    private readonly Random _random;
    private readonly TimeProvider _clock;   // 时钟供给源（时钟缝 件一——单调戳/延迟统一；缺省 System 零变化）
    private readonly DeadlineRegistry _registry;   // 节拍注册表（null 构造参 = Shared/假钟自动新建——tick 去 TimerQueue）
    private readonly Channel<RaftEvent> _events;
    private readonly TaskCompletionSource _loopStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // 循环线程状态（单写者——仅循环线程访问）
    private RaftRole _role = RaftRole.Follower;
    private long _electionDeadlineTicks;
    private long _heartbeatDeadlineTicks;
    private ClusterConfig _config = new(NodeId.Empty);
    private readonly HashSet<NodeId> _preVoteGranted = [];
    private readonly HashSet<NodeId> _voteGranted = [];
    private readonly Dictionary<long, TaskCompletionSource<long>> _pending = [];   // TCS 直通路径（取消语义/窗口打满兜底——applied 档）
    private readonly Dictionary<long, TaskCompletionSource<long>> _pendingCommitted = [];   // TCS 直通路径 committed 档（AdvanceCommit 前缀完成）
    private readonly Dictionary<long, TaskCompletionSource<long>> _pendingLeaderLocal = [];   // TCS 直通路径 LeaderLocal 档（FlushPendingAppends fsync 组提交后完成）
    private readonly List<long> _completedKeys = [];   // CompleteApplied 可复用收集（apply 快道热路径零稳定分配——_completionLock 内单线程访问）
    private readonly List<long> _completedCommittedKeys = [];   // CompleteCommitted 可复用收集（同 _completedKeys）
    private readonly List<long> _completedLeaderLocalKeys = [];   // DrainLeaderLocal 可复用收集（同 _completedKeys）
    private readonly InFlightWindow _window;   // 池化完成源窗口（热路径零分配）
    /// <summary>★ 完成结构锁：注册=共识循环线程、完成=apply worker 线程——
    /// _window._registered（List）与 _pending（Dictionary）的跨线程互斥（临界区摊还 O(1)）。</summary>
    private readonly object _completionLock = new();
    private HashSet<NodeId> _readIndexConfirmations = [];
    // ★ 二期-B §6.1：单槽 → 批量（并发 ReadIndex 相互覆盖挂死缺陷根治）——waiter = 本地 TCS
    //   或转发的 IReplyContext + 来源；轮次在途标志 + 多数派确认集（_readIndexLock 串行——
    //   循环线程入队 / 应答到达线程计票完成）。
    private sealed class ReadIndexWaiter(TaskCompletionSource<long>? local, IReplyContext? forwarded, NodeId from)
    {
        public readonly TaskCompletionSource<long>? Local = local;
        public readonly IReplyContext? Forwarded = forwarded;
        public readonly NodeId From = from;
    }
    private readonly List<ReadIndexWaiter> _readIndexWaiters = [];
    private bool _readIndexRoundActive;
    /// <summary>本任期锚点 no-op 的 index（二期-B §6.4——BecomeLeader 追加；锚点未提交前 ReadIndex
    /// 轮次/租约快路径挂起，防新 leader 以旧任期 commit 水位应答线性读。0 = 无锚点/非 leader）。</summary>
    private long _readIndexHoldIndex;
    /// <summary>转发线性读重试上限（§6.2 有界次数——失败路径 50ms 节流，总时限主导收敛等待）。</summary>
    private const int MaxReadIndexForwardAttempts = 64;
    private static readonly TimeSpan ReadIndexForwardRetryDelay = TimeSpan.FromMilliseconds(50);
    /// <summary>applied waiter（二期-B §6.3 WaitForAppliedAsync——_completionLock 内单线程完成）。</summary>
    private readonly List<(long Index, TaskCompletionSource Done)> _pendingAppliedWaiters = [];
    // ★ 二期-D2：领导权定向转让——目标（装箱 NodeId?；null = 无在途转让）+ TimeoutNow 单发旗标
    private object? _transferState;
    private int _transferTimeoutNowSent;
    /// <summary>优雅 drain（二期-D3——置位后拒绝新提案，客户端重路由语义）。</summary>
    private int _draining;
    /// <summary>快照安装进行中（防重复导出——lane 链线程并发调用，并发安全集合）。</summary>
    private readonly ConcurrentDictionary<NodeId, byte> _installingSnapshots = new();
    /// <summary>快照安装重试冷却（per-peer 下次可尝试时刻，ms 单调戳——安装导出=重活，
    /// 无冷却=lane 周期级满速重跑烧 CPU；冷却窗内边界未解除由心跳节拍兜底）。</summary>
    private const int InstallRetryIntervalMs = 1_000;
    private readonly ConcurrentDictionary<NodeId, long> _installRetryAfter = new();
    private bool _removedFromConfig;
    private ReplicationProcess? _replication;

    // 跨线程观测（volatile/原子发布——用户面/测试）
    private int _roleValue = (int)RaftRole.Follower;
    private int _isLeaderFlag;
    private static readonly CancellationToken LeaderLostToken = new CancellationToken(canceled: true);
    private CancellationTokenSource? _leadershipCts;   // 执政期令牌源（每任期一枚——降级即取消；volatile 引用换新）
    private readonly object _leaseLock = new();                            // 租约读确认态互斥（lane 线程并发回写）
    private readonly Dictionary<NodeId, long> _followerAckTicks = [];      // follower 最近成功应答时刻（ms 单调戳——新鲜度判定）
    private long _quorumAckTicks = long.MinValue;                          // 最近一次多数派确认时刻（选举票/心跳应答——租约窗起点）
    private readonly Dictionary<NodeId, byte> _pendingPromote = [];        // 追平晋级登记（JoinReq.AutoPromote——learner→voter）
    private int _pendingPromoteCount;                                      // 登记计数快门（应答热路径免锁空转）
    private Exception? _loopException;
    // ★ 循环活性诊断（选举冻结取证——写入者=循环/tick 任务线程 Volatile 口径，读者=诊断快照任意线程；
    //   判别：LoopLagMs 大=tick 未达或循环卡 handler / TickLagMs 大=tick 链断 / QueueDepth 大=事件积压
    //   不消费 / DeadlineInMs 负=选举窗已过而循环未处理）
    private long _lastLoopProgressTicks;   // 主循环最近一次迭代完成时刻（TickCount64 ms）
    private long _lastTickQueuedTicks;     // tick 任务最近一次投递尝试时刻
    private long _tickWriteFailures;       // tick 投递失败累计（队列满——纯计数）
    /// <summary>★ commit 安全对：(commitIndex, term) 复合 CAS——Raft §5.4.2
    /// 不能提交旧 term 条目，对整体原子替换（推进瞬间 term 快照入对；期间换届则 CAS 失败重读）。
    /// 多 lane 并发上报安全（CAS 循环只让赢家 Submit/触发事件）。</summary>
    private readonly Atomic128<CommitPair> _commitPair = new(new CommitPair(0, 0));
    /// <summary>★ leader 快照：(leaderSeq 8B, term 8B) 原子发布——
    /// <see cref="LeaderId"/> 快速失败路径任意线程高频读；seq = 成员表序
    /// （<see cref="_memberOrder"/>，0 = 未知/选举中），Guid 仅装配/日志面（热值 16B→8B）。</summary>
    private readonly Atomic128<LeaderSlot> _leaderState = new(new LeaderSlot(0, 0));
    /// <summary>成员序表（seq = i+1；_clusterLock 内随配置切换重建，volatile 读——copy-on-write）。</summary>
    private volatile NodeId[] _memberOrder = [];

    /// <summary>commit 安全对载荷（16B blittable——Atomic128 背板）。</summary>
    private readonly struct CommitPair(long index, long term)
    {
        public readonly long Index = index;
        public readonly long Term = term;
    }

    /// <summary>leader 快照载荷（16B blittable——Atomic128 背板；Seq=0 表示未知）。</summary>
    private readonly struct LeaderSlot(long seq, long term)
    {
        public readonly long Seq = seq;
        public readonly long Term = term;
    }

    private CancellationTokenSource? _loopCts;
    private static readonly TimeSpan LoopExitTimeout = TimeSpan.FromSeconds(5);   // 循环线程有界等退（registry 同款）
    private IDisposable? _tickSubscription;   // 节拍订阅（StartAsync 建、StopAsync 退——句柄幂等）
    private readonly AsyncPump _pump;   // 共识循环泵（单线程亲和——域内续体回流泵线程，池不在关键路径）
    private Task? _loopThread;   // 共识循环专用线程（StartAsync 起、StopAsync 有界等退）
    private readonly TaskSink _loops;      // ★ 后台任务组（投票发送/直排 append 等 fire-and-forget 消费面——可丢失自愈路径）
    /// <summary>已知 leader 心跳最近续约时刻（ms 单调戳——PreVote 在位否决判活用，独立于
    /// 选举窗：选举超时路径会重掷选举窗，挂其上会让否决永不解除）。</summary>
    private long _lastLeaderContactTicks;
    /// <summary>★ 选举轨迹环形记录（诊断常设仪器——spec-09 可观测；锁内单写多读，_traceLock）：
    /// 收敛停滞时随失败快照倾倒，钉死卡住的转换步骤。容量 96 覆盖多轮选举。</summary>
    private readonly (long Ticks, string Text)[] _trace = new (long, string)[96];
    private int _traceCount;
    private readonly object _traceLock = new();
    /// <summary>已应用水位（_completionLock 内更新——直排注册的"注册前已完成"检查）。</summary>
    private long _appliedWatermark;

    // ★ Replicate 排空攒批缓冲（循环线程单写者——懒分配一次，容量=复制策略 BatchSize）
    private RaftEvent.Replicate[]? _drainEvents;
    // ★ append 批缓冲复用（降分配/GC 抖动——leader/follower 各一，分别单写者：循环线程 / gate 串行线程）；
    //   条目 = (Term, Kind, Content) 三元组（wire v3 信封之死——kind 结构字段，命令零拷贝直入批）
    private readonly List<(long Term, byte Kind, ReadOnlyMemory<byte> Content)> _leaderBatch = new(64);
    private readonly List<(long Term, byte Kind, ReadOnlyMemory<byte> Content)> _followerBatch = new(256);
    private static readonly RaftEvent.Tick TickEvent = new();   // 无状态节拍事件单例（每 5ms 投递——零分配）
    private static readonly RaftEvent.PersistCompleted PersistCompletedEvent = new();   // 无状态完工唤醒单例（W4 下环——零分配）
    /// <summary>★ 在途组提交（W4 persist 下环——循环线程唯一发起/观察；任务自投完工唤醒事件，
    /// fsync 期间事件循环不阻塞：心跳/选举/复制不受阻）。循环 finally 有界 drain。</summary>
    private Task? _persistCommitTask;

    /// <summary>★ 冷状态互斥：角色转换/term 变更串行化（StepDown/选举/当选/让位/
    /// 配置切换——锁内含 await 落盘故用 SemaphoreSlim；锁序恒 <see cref="_followerAppendGate"/>）。</summary>
    private readonly SemaphoreSlim _clusterLock = new(1, 1);
    /// <summary>★ follower 日志段串行化：AppendEntries/快照安装的日志操作
    /// （term 重查 + prevLog 检查 + 追加 + 提交）单写者——请求回调线程并发到达，
    /// 经此门串行（存储内部写锁为最后防线，本门保证 prevLog-追加-提交的检查序列不交错）。</summary>
    private readonly SemaphoreSlim _followerAppendGate = new(1, 1);
    /// <summary>ReadIndex 确认计数锁（低频——pending 为 null 时零锁快路径）。</summary>
    private readonly object _readIndexLock = new();

    private readonly RpcHandler _rpcHandler;

    private int _started;
    private int _stopped;
    private int _disposed;

    /// <summary>本节点 ID。</summary>
    public NodeId Self => _self;

    /// <summary>当前角色（跨线程观测——volatile 发布）。</summary>
    public RaftRole Role => (RaftRole)Volatile.Read(ref _roleValue);

    /// <summary>当前 commitIndex（本地已确认提交水位——复合对裸读，16B 对齐不撕裂）。</summary>
    public long CommitIndex => _commitPair.Read().Index;

    /// <summary>是否 Leader（当选发布——用户面快速失败判定）。</summary>
    public bool IsLeader => Volatile.Read(ref _isLeaderFlag) != 0;

    /// <summary>leadership 感知取消令牌：执政期有效（丢失 leadership 即取消）、非 leader = 已取消
    /// 令牌、每任期一枚（重新当选换新）。随执政期运行的后台任务用它挂取消——替代手工
    /// <see cref="IsLeader"/> 轮询。</summary>
    public CancellationToken LeadershipToken => Volatile.Read(ref _leadershipCts)?.Token ?? LeaderLostToken;

    /// <summary>本节点在当前活动配置中是否投票成员（learner = false——引导/观察副本的就绪信号）。</summary>
    public bool IsVoter => _config.IsVoter(_self);

    /// <summary>当前已知 leader（null = 未知/选举中——快照原子读 + 成员序表反查，任意线程高频）。</summary>
    public NodeId? LeaderId
    {
        get
        {
            var slot = _leaderState.Read();
            if (slot.Seq == 0) return null;
            var order = _memberOrder;
            return slot.Seq <= order.Length ? order[slot.Seq - 1] : null;   // 配置切换窗口 seq 失配 → 未知
        }
    }

    /// <summary>宿主维护操作串行化面（T6 结构修复——自压缩入 followerAppendGate）：压缩/截头等
    /// 日志变更维护操作与 AppendEntries/快照安装的日志操作互斥（同一 <see cref="_followerAppendGate"/>——
    /// 锁序恒 followerAppendGate → store 内部门）。压缩窗内入站 AE 排队等待：窗长须远小于选举窗
    /// （磁盘 DIO+WT 压缩档亚毫秒——超窗= follower 心跳处理停滞自发换届，宿主压缩节奏守卫兜底）。</summary>
    /// <param name="work">临界区内执行的异步工作（接收取消令牌）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>work 返回的结果。</returns>
    public async ValueTask<T> RunUnderAppendGateAsync<T>(Func<CancellationToken, ValueTask<T>> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        await _followerAppendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await work(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _followerAppendGate.Release();
        }
    }

    /// <summary>当前任期（存储端口直读）。</summary>
    public long CurrentTerm => _store.Term;

    /// <summary>当前活动配置（apply 产物——spec-04 配置与状态机同构）。</summary>
    public ClusterConfig Config => _config;

    /// <summary>当选/降级事件（true = 本节点当选——用户面）。</summary>
    public event Action<bool>? LeaderChanged;

    /// <summary>commitIndex 推进（含应用完成——用户面）。</summary>
    public event Action<long>? EntryCommitted;

    /// <summary>循环异常（fail-fast——节点停摆，上层观察决定重启）。</summary>
    public Exception? LoopException => Volatile.Read(ref _loopException);

    /// <summary>
    /// 循环活性诊断快照（internals——测试失败快照/运维探针；ms 单调戳口径）。
    /// <para>判别表：LoopLagMs 大 + TickLagMs 大 = tick 链断（TimerQueue/池化饥饿或 tick 任务亡）；
    /// LoopLagMs 大 + TickLagMs 小 + QueueDepth 大 = 事件积压循环不消费（卡 handler）；
    /// DeadlineInMs 负 + LoopLagMs 小 = 选举窗已过而循环未走定时器路径（deadline 逻辑缺陷）。</para>
    /// </summary>
    internal (long LoopLagMs, long TickLagMs, int QueueDepth, long TickWriteFailures, long DeadlineInMs) DiagnoseLoop()
    {
        var now = _clock.GetMsTimestamp();
        var lastTick = Volatile.Read(ref _lastTickQueuedTicks);
        return (
            LoopLagMs: now - Volatile.Read(ref _lastLoopProgressTicks),
            TickLagMs: lastTick == 0 ? -1 : now - lastTick,
            QueueDepth: _events.Reader.Count,
            TickWriteFailures: Interlocked.Read(ref _tickWriteFailures),
            DeadlineInMs: NextDeadlineTicks() - now);
    }

    /// <summary>节拍注册表取证快照（tick 链断判别——pacer 存活/每条目 last-wake/due 状态；测试失败快照用）。</summary>
    internal string DescribeRegistry() => _registry.Describe();

    /// <summary>外部配置切换入口（ApplyPipeline 配置条目 apply 回调——spec-04 配置与状态机同构）。
    /// <para>★ 产品装配公共面（D6）：状态机构造在 apply 管道之后——装配经
    /// <c>apply.SetConfigCallback(raft.PostConfigChanged)</c> 晚绑定。</para></summary>
    /// <param name="config">新的活动配置（apply 产物——已切换完毕）。</param>
    public void PostConfigChanged(ClusterConfig config)
        => _events.Writer.TryWrite(new RaftEvent.ConfigChanged(config));

    /// <summary>构造（零 IO——启动经 <see cref="StartAsync"/>）。</summary>
    /// <param name="self">本节点 ID。</param>
    /// <param name="store">存储端口（spec-12 §8.1——夹具或组装层适配器）。</param>
    /// <param name="transport">节点端点完整面（请求回调收发 RPC）。</param>
    /// <param name="apply">提交→应用管道（spec-05）。</param>
    /// <param name="options">超时/队列参数（spec-01 §8）。</param>
    /// <param name="snapshotTransfer">快照传输协调器（spec-03——null = 快照安装禁用）。</param>
    /// <param name="logger">日志（可选）。</param>
    /// <param name="registry">节拍注册表（null = 进程级 <see cref="DeadlineRegistry.Shared"/>——
    ///     共享节拍线程；测试可注入独立实例）。</param>
    /// <param name="hub">可观测枢纽（null = Disabled——共识维度视图 <see cref="ObservabilityHub.Raft"/> 热路径短路）。</param>
    /// <param name="groupId">组 ID（二期-I6 组维度标签——多组装配下状态导出/管理面区分组；default = 默认组）。</param>
    public RaftStateMachine(NodeId self, IRaftStore store, IProtocolTransport transport, IApplySink apply,
        RaftOptions options, ISnapshotTransfer? snapshotTransfer = null, ILogger? logger = null,
        DeadlineRegistry? registry = null, ObservabilityHub? hub = null, RaftGroupId groupId = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(apply);
        ArgumentNullException.ThrowIfNull(options);
        _self = self;
        _store = store;
        _transport = transport;
        _apply = apply;
        _options = options;
        _snapshotTransfer = snapshotTransfer;
        _logger = logger;
        _random = options.Random ?? Random.Shared;
        _clock = options.Clock;
        // ★ 假钟注入档（时钟缝 件一）：注入时钟未显式传注册表时，为本节点自动新建同钟注册表——
        //   节拍线程与 deadline 判定必须同钟（Shared 是 System 单例，假钟下共用会判乱）。
        _registry = registry ?? (_clock == TimeProvider.System
            ? DeadlineRegistry.Shared
            : new DeadlineRegistry(clock: _clock));
        _raftView = hub?.Raft;   // 二期-I2：共识维度视图（null = Disabled——热路径短路）
        _groupId = groupId;   // ★ 二期-I6：组维度标签（多组装配下状态导出/管理面可区分组）
        _window = new InFlightWindow(options.InFlightCapacity);
        _loops = new TaskSink($"raft-sm-{self}", onFaulted: OnLoopFaulted, logger: logger);
        _pump = new AsyncPump($"raft-loop-{self}", logger);
        _rpcHandler = new RpcHandler(this);
        _events = Channel.CreateBounded<RaftEvent>(new BoundedChannelOptions(options.EventQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,   // 满 = 投递方 await = 背压传导
            SingleReader = true,
            SingleWriter = false,
        });
    }

    // ═══ 生命周期 ═══

    /// <summary>
    /// 启动状态机循环（初始配置装配 + 请求 handler 挂载；循环启动即进入 Follower 并掷选举超时）。
    /// </summary>
    /// <param name="initialConfig">初始活动配置（装配：恢复的配置或用户配置）。</param>
    /// <returns>完成时循环线程已启动并进入 Follower（Standalone 单成员配置直接就位 Leader）。</returns>
    /// <exception cref="InvalidOperationException">重复启动或传输未实现内部挂载口。</exception>
    public Task StartAsync(ClusterConfig initialConfig)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("RaftStateMachine 已启动（StartAsync 只可一次）。");
        ArgumentNullException.ThrowIfNull(initialConfig);
        if (_transport is not ICoreProtocolPort core)
            throw new InvalidOperationException(
                $"机制挂载须内部注册口（ICoreProtocolPort）——介质 {_transport.GetType().Name} 未实现；机制宿主归 Core.Net（spec-12 §3.5）。");
        _config = initialConfig;
        RebuildMemberOrder(initialConfig);
        _loopCts = new CancellationTokenSource();
        core.RegisterCoreRequestHandler(ProtocolIds.Raft, _rpcHandler);
        _apply.AppliedTo += OnAppliedTo;
        _replication = new ReplicationProcess(
            _self, _store, _transport,
            commitIndexProvider: () => _commitPair.Read().Index,
            onCommitAdvanced: AdvanceCommit,
            onRespArrived: HandleAppendEntriesRespAsync,
            onSnapshotNeeded: OnSnapshotNeededAsync,
            onFollowerAck: OnFollowerAck,
            policy: _options.Replication,
            heartbeatInterval: _options.HeartbeatInterval,
            lifecycleCt: _loopCts.Token,
            logger: _logger,
            clock: _clock);
        // ★ 专用线程 + AsyncPump 泵域（2026-09-03 活性判例）：共识循环 = 长稳定异步循环，
        //   载体为单线程泵（域内续体回流泵线程——池不在关键路径）。TaskSink 的
        //   fire-and-forget 语义（可丢失自愈）不容纳"续体丢失即循环死亡"的活性契约，故不入池。
        var loopCt = _loopCts.Token;
        _loopThread = Task.Factory.StartNew(
            () =>
            {
                try { _pump.Run(() => LoopAsync(loopCt), loopCt); }
                catch (Exception ex) { OnLoopFaulted(ex); }   // 泵外逃逸异常兜底（registry 节拍线程同款）
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
        StartTickLoop();
        return _loopStarted.Task;
    }

    /// <summary>★ 节拍订阅（2026-09-03 冻结判例——共识活性去 TimerQueue）：
    /// 拉取模型——deadline 委托直读本节点的角色感知双水位字段（<see cref="NextDeadlineTicks"/>，
    /// Interlocked，stale 读只致早/晚醒一个节拍无害自愈）；到期唤醒 = 写 Tick 事件经 channel 驱动
    /// 主循环（快速非阻塞 TryWrite 契约；满则丢=纯唤醒语义不变；ChannelClosed=停摆退订竞态，吞）。
    /// <para>背景：.NET 8 TimerQueue 高频触发窗口丢表项（堆转储实锤 TimerQueueTimer 孤儿化）使
    /// PeriodicTimer 静默停摆——tick 是共识循环的唯一活性源，随之中招集体冻结。节拍线程收口到
    /// <see cref="DeadlineRegistry"/>（专用线程 + WaitHandle 内核等待，零 TimerQueue）。</para></summary>
    private void StartTickLoop()
    {
        _tickSubscription = _registry.Subscribe(
            deadlineTicks: NextDeadlineTicks,
            wake: () =>
            {
                try
                {
                    if (!_events.Writer.TryWrite(TickEvent))
                        Interlocked.Increment(ref _tickWriteFailures);   // 队列满丢弃——纯唤醒，计数留痕
                }
                catch (ChannelClosedException) { }   // 循环 fail-fast 终态——退订竞态，吞
                // ★ 健康 leader 心跳直唤（判例 2026-09-04——饥饿不对称修复）：无本地持久化债时
                //   lane 直接醒（少一跳）；债在身不唤（病 leader 静默——换届自愈契约保持）。
                //   pacer 线程 µs 级回调（Set 事件）——不占节拍。
                _replication?.WakeLanesIfCaughtUp();
                Volatile.Write(ref _lastTickQueuedTicks, _clock.GetMsTimestamp());
            });
    }

    /// <summary>循环任务异常兜底（TaskSink onFaulted——记录到 <see cref="LoopException"/>）。</summary>
    private void OnLoopFaulted(Exception ex)
    {
        Volatile.Write(ref _loopException, ex);
        _logger?.LogError(ex, "Raft 状态机后台任务未捕获异常：node={Self}", _self);
    }

    /// <summary>
    /// 统一状态导出（二期-D1 NETGAP-005——healthz/readyz/管理面数据源）：
    /// Role/Term/LeaderId/Commit/Applied/日志尾/快照覆盖点/成员与复制进度 + 探针语义。
    /// 各字段尽力一致快照（诊断/探针语义——非临界一致）；复制进度仅 leader 视角有义。
    /// </summary>
    /// <returns>尽力一致的状态快照（healthy = 已启动/未停止/循环无异常；ready = healthy 且 leader 在位（含自身）；members 仅 leader 视角非空）。</returns>
    public RaftStateSnapshot GetStateSnapshot()
    {
        var role = Role;
        var leader = LeaderId;
        var commit = CommitIndex;
        var healthy = Volatile.Read(ref _started) != 0
            && Volatile.Read(ref _stopped) == 0
            && Volatile.Read(ref _loopException) is null;
        // readyz：leader 在位（含自身）——分区/选举中 = not ready（探针不返回过期承诺）
        var ready = healthy && (role == RaftRole.Leader || leader is not null);

        List<RaftMemberState>? members = null;
        if (role == RaftRole.Leader)
        {
            members = new List<RaftMemberState>();
            foreach (var m in _config.Members)
            {
                if (m.Id == _self) continue;
                long match = _replication is { } rep && rep.TryGetMatchIndex(m.Id, out var mi) ? mi : 0;
                members.Add(new RaftMemberState(m.Id, m.Role, match, Math.Max(0, commit - match)));
            }
        }

        return new RaftStateSnapshot(_self, role, CurrentTerm, leader, commit, Volatile.Read(ref _appliedWatermark),
            _store.LastLogIndex, _store.SnapshotIndex, IsVoter, healthy, ready, members ?? [], _groupId);
    }

    /// <summary>选举轨迹记录（诊断常设——环形覆盖，任意线程安全）。</summary>
    private void TraceElection(string text)
    {
        lock (_traceLock)
        {
            var i = _traceCount % _trace.Length;
            _trace[i] = (_clock.GetMsTimestamp(), text);
            _traceCount++;
        }
    }

    /// <summary>倾倒选举轨迹（失败快照用——时间升序）。</summary>
    internal string DumpElectionTrace()
    {
        lock (_traceLock)
        {
            if (_traceCount == 0) return "∅";
            var n = Math.Min(_traceCount, _trace.Length);
            var start = _traceCount - n;
            var lines = new string[n];
            for (var i = 0; i < n; i++)
            {
                var e = _trace[(start + i) % _trace.Length];
                lines[i] = $"{e.Ticks}:{e.Text}";
            }
            return string.Join(" ⇒ ", lines);
        }
    }

    /// <summary>
    /// 停止状态机循环（取消循环——循环退出后完成；循环已异常终止时直接返回）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌（停止过程等待的取消——循环线程退出有界兜底）。</param>
    /// <returns>完成时循环线程已退出、复制 lane 链已全退（幂等——重复调用立即完成）。</returns>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        // ★ 先取消循环：不依赖 Stop 事件投递/消费——事件队列积压时 Stop 事件在队尾饿死
        //   （done 永不完成）→ Dispose 挂死。Cancel 后循环从 ReadAsync 退出（或完成当前事件
        //   处理后退出）；循环卡在业务处理时 ct 有界兜底。
        if(_loopCts is not null && !_loopCts.IsCancellationRequested)
            await _loopCts.CancelAsync();
        // ★ 节拍退订（同步、无共享线程需等——TickKey 键控形态随 TimerQueue 去 Tick 一起退役）
        _tickSubscription?.Dispose();
        _tickSubscription = null;
        // 循环线程有界等退（registry Shard.DisposeAsync 同款——超时 LogWarning，资源滞留换安全）
        if (_loopThread is { } loop)
        {
            try { await loop.WaitAsync(LoopExitTimeout, cancellationToken).ConfigureAwait(false); }
            catch (TimeoutException) { _logger?.LogWarning("Raft 循环线程退出超时：node={Self}", _self); }
        }
        _apply.AppliedTo -= OnAppliedTo;
        // ★ 等复制 lane 链全退（窗口 N 在途更长——防存储释放后链仍访问 store：类间测试残留崩溃根因）
        if (_replication is not null)
            await _replication.StopAllLanesAndWaitAsync().ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <returns>完成时循环/复制/任务组/领导权令牌等资源均已停止并释放（幂等）。</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_started != 0 && _stopped == 0)
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        await _loops.DisposeAsync().ConfigureAwait(false);   // ★ 任务组 drain（取消+等待全退）
        if (_replication is not null)
            await _replication.DisposeAsync().ConfigureAwait(false);   // ★ lane 链任务组 drain
        if (_leadershipCts is not null)
            await _leadershipCts.CancelAsync();
        _leadershipCts?.Dispose();
        _loopCts?.Dispose();
        _commitPair.Dispose();
        _leaderState.Dispose();
    }

    // ═══ 用户面入口（spec-08 消费——装配在 Builder）═══

    /// <summary>
    /// 注册 ct → tcs 取消：注册必须与 tcs 生命周期绑定——同步返回的入口方法里
    /// `using var reg = ct.Register(...)` 随方法退出即释放，取消回调永不触发。
    /// <para>★ 注册清理走任务组（fire-and-forget 纪律——ContinueWith 任务不裸丢弃，
    /// 组内观测/Dispose 兜底）。</para>
    /// </summary>
    private void RegisterCancellation(TaskCompletionSource<long> tcs, CancellationToken ct)
    {
        if (!ct.CanBeCanceled) return;
        var reg = ct.Register(static state => ((TaskCompletionSource<long>)state!).TrySetCanceled(), tcs);
        _loops.SubmitFast(_ =>
            new ValueTask(tcs.Task.ContinueWith(static (_, state) => ((CancellationTokenRegistration)state!).Dispose(),
                reg, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default)));
    }

    /// <summary>
    /// 复制一条命令（spec-05 契约②）：返回 index = committed 且 applied（调用方拿到返回值即可
    /// 从业务存储读到效果——read-your-writes）。非 Leader 立即抛 <see cref="NotLeaderException"/>。
    /// <para>★ 事件路径：池化完成源 + 事件入队——循环线程单线程 append（存储端口单写者）
    ///   可取消/窗口打满走 TCS 兜底。</para>
    /// </summary>
    /// <param name="command">命令内容（帧 payload——业务方定义格式，零拷贝直入追加批）。</param>
    /// <param name="ct">取消令牌（换届/窗口打满/显式取消——调用方感知为 OperationCanceledException）。</param>
    /// <returns>命令落盘的日志 index（committed 且 applied——read-your-writes）。</returns>
    /// <exception cref="NotLeaderException">本节点非 Leader（携带当前已知 leader——调用方重路由）。</exception>
    public ValueTask<long> ReplicateAsync(ReadOnlyMemory<byte> command, CancellationToken ct = default)
    {
        // 快速失败（volatile 观测——非 Leader 立即抛，不等入队）
        if (Volatile.Read(ref _isLeaderFlag) == 0)
            return ValueTask.FromException<long>(new NotLeaderException(LeaderId));
        if (Volatile.Read(ref _draining) != 0)   // ★ 二期-D3：drain 中拒绝新提案（客户端重路由）
            return ValueTask.FromException<long>(new NotLeaderException(LeaderId));
        // 集群默认档 LeaderLocal：默认入口整体切弱档（本地持久化即返——Redis 类异步一致部署形态）
        if (_options.ReplicationAck == ReplicationAck.LeaderLocal)
            return ReplicateLeaderLocalCoreAsync(command, ct);
        if (!ct.CanBeCanceled && _window.TryAlloc() is { } slot)
        {
            var version = slot.Version;
            if (!_events.Writer.TryWrite(RaftEvent.Replicate.Rent(command, slot, null)))
                return WriteSlotAsync(command, slot, version, ct);   // 队列满——背压等待入队
            return new ValueTask<long>(slot, version);
        }
        // 兜底路径（可取消/窗口打满）：TCS 直通
        var tcs = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        RegisterCancellation(tcs, ct);
        if (!_events.Writer.TryWrite(RaftEvent.Replicate.Rent(command, null, tcs)))
            return WriteAsync(command, tcs, ct);   // 队列满——背压等待入队
        return new ValueTask<long>(tcs.Task);
    }

    /// <summary>
    /// 复制一条命令（LeaderLocal 完成档）：返回 index = leader 本地已持久化（fsync 组提交——
    /// 不等多数派复制、不等 commit/apply）。非 Leader 立即抛 <see cref="NotLeaderException"/>。
    /// <para>★ 弱档语义：返回后其他副本可能还没有此条目；leader 宕机时未复制到任何成员的
    /// 尾部写丢失、已复制部分经最高日志者当选保留——返回的 index 不保证最终在多数派日志中
    /// （可能随换届回退）。commitIndex 照旧由多数派推进——强一致需求用
    /// <see cref="ReplicateAsync"/>/<see cref="ReplicateCommittedAsync"/> 或 ReadIndex。</para>
    /// </summary>
    /// <param name="command">命令内容（帧 payload——业务方定义格式）。</param>
    /// <param name="cancellationToken">取消令牌（换届/显式取消——调用方感知为 OperationCanceledException）。</param>
    /// <returns>命令在 leader 本地持久化后的日志 index（不等多数派复制/commit/apply）。</returns>
    /// <exception cref="NotLeaderException">本节点非 Leader（携带当前已知 leader——调用方重路由）。</exception>
    public ValueTask<long> ReplicateLeaderLocalAsync(ReadOnlyMemory<byte> command, CancellationToken cancellationToken = default)
    {
        // 快速失败（volatile 观测——非 Leader 立即抛，不等入队）
        if (Volatile.Read(ref _isLeaderFlag) == 0)
            return ValueTask.FromException<long>(new NotLeaderException(LeaderId));
        if (Volatile.Read(ref _draining) != 0)   // ★ 二期-D3：drain 中拒绝新提案（客户端重路由）
            return ValueTask.FromException<long>(new NotLeaderException(LeaderId));
        return ReplicateLeaderLocalCoreAsync(command, cancellationToken);
    }

    /// <summary>LeaderLocal 核心路径（默认入口集群档分流 + 显式弱档调用共用——TCS 直通：
    /// 完成点 = fsync 组提交，不经 InFlightWindow 双水位窗口）。</summary>
    private ValueTask<long> ReplicateLeaderLocalCoreAsync(ReadOnlyMemory<byte> command, CancellationToken cancellationToken=default)
    {
        var tcs = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        RegisterCancellation(tcs, cancellationToken);
        if (!_events.Writer.TryWrite(RaftEvent.Replicate.Rent(command, null, tcs, leaderLocal: true)))
            return WriteLeaderLocalAsync(command, tcs, cancellationToken);   // 队列满——背压等待入队
        return new ValueTask<long>(tcs.Task);
    }

    private async ValueTask<long> WriteLeaderLocalAsync(ReadOnlyMemory<byte> command, TaskCompletionSource<long> tcs, CancellationToken cancellationToken=default)
    {
        await _events.Writer.WriteAsync(RaftEvent.Replicate.Rent(command, null, tcs, leaderLocal: true), cancellationToken).ConfigureAwait(false);
        return await tcs.Task.ConfigureAwait(false);
    }

    private async ValueTask<long> WriteAsync(ReadOnlyMemory<byte> command, TaskCompletionSource<long> tcs, CancellationToken cancellationToken=default)
    {
        await _events.Writer.WriteAsync(RaftEvent.Replicate.Rent(command, null, tcs), cancellationToken).ConfigureAwait(false);
        return await tcs.Task.ConfigureAwait(false);
    }

    private async ValueTask<long> WriteSlotAsync(ReadOnlyMemory<byte> command, InFlightSlot slot, short version, CancellationToken cancellationToken=default)
    {
        await _events.Writer.WriteAsync(RaftEvent.Replicate.Rent(command, slot, null), cancellationToken).ConfigureAwait(false);
        return await new ValueTask<long>(slot, version).ConfigureAwait(false);
    }

    /// <summary>
    /// 复制一条命令（committed 完成档）：返回 index = 多数派已提交（commitIndex 追上即完成——
    /// 不等 apply 管道，完成早于/equal <see cref="ReplicateAsync"/>）。非 Leader 立即抛
    /// <see cref="NotLeaderException"/>。
    /// <para>★ 档位语义：拿到返回值<b>不保证</b>业务存储可读到效果（apply 可能滞后）——
    ///   read-your-writes 场景用默认档 <see cref="ReplicateAsync"/>；本档面向吞吐优先
    ///   （纯复制/批量灌数据——省 apply 推进的完成等待一跳）。持久化语义不变（多数派落盘）。</para>
    /// </summary>
    /// <param name="command">命令内容（帧 payload——业务方定义格式）。</param>
    /// <param name="cancellationToken">取消令牌（换届/窗口打满/显式取消——调用方感知为 OperationCanceledException）。</param>
    /// <returns>命令的日志 index（多数派已提交——不等 apply）。</returns>
    /// <exception cref="NotLeaderException">本节点非 Leader（携带当前已知 leader——调用方重路由）。</exception>
    public ValueTask<long> ReplicateCommittedAsync(ReadOnlyMemory<byte> command, CancellationToken cancellationToken = default)
    {
        // 快速失败（volatile 观测——非 Leader 立即抛，不等入队）
        if (Volatile.Read(ref _isLeaderFlag) == 0)
            return ValueTask.FromException<long>(new NotLeaderException(LeaderId));
        if (Volatile.Read(ref _draining) != 0)   // ★ 二期-D3：drain 中拒绝新提案（客户端重路由）
            return ValueTask.FromException<long>(new NotLeaderException(LeaderId));
        if (!cancellationToken.CanBeCanceled && _window.TryAlloc() is { } slot)
        {
            slot.CommittedOnly = true;
            var version = slot.Version;
            if (!_events.Writer.TryWrite(RaftEvent.Replicate.Rent(command, slot, null)))
                return WriteSlotAsync(command, slot, version, cancellationToken);   // 队列满——背压等待入队
            return new ValueTask<long>(slot, version);
        }
        // 兜底路径（可取消/窗口打满）：TCS 直通
        var tcs = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        RegisterCancellation(tcs, cancellationToken);
        if (!_events.Writer.TryWrite(RaftEvent.Replicate.Rent(command, null, tcs, committed: true)))
            return WriteCommittedAsync(command, tcs, cancellationToken);   // 队列满——背压等待入队
        return new ValueTask<long>(tcs.Task);
    }

    private async ValueTask<long> WriteCommittedAsync(ReadOnlyMemory<byte> command, TaskCompletionSource<long> tcs, CancellationToken cancellationToken = default)
    {
        await _events.Writer.WriteAsync(RaftEvent.Replicate.Rent(command, null, tcs, committed: true), cancellationToken).ConfigureAwait(false);
        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>完成源注册（append 后——锁内）：
    /// <para>★ 注册前已完成检查：并发直排下 index 可能已被更快的组完成
    /// （commit/applied 水位越过本 index）——立即完成，防挂死。档位分流：committed 档吃
    /// commit 水位、applied 档吃 applied 水位。</para>
    /// <para>★ 换届取消防护（双窗口闭环）：slot 已被 StepDown 取消（State≠Registered）时跳过；
    /// append 期间已换届（isLeader=0——本条不会再被本任期提交）时直接取消——锁序保证
    /// CancelAll（completionLock）与注册（completionLock）互斥，SetRole 先于 CancelAll，
    /// 注册侧读 isLeader=1 的交错必被随后的 CancelAll 覆盖。</para></summary>
    private void RegisterCompletion(InFlightSlot slot, long index)
    {
        lock (_completionLock)
        {
            if (Volatile.Read(ref slot.State) != 1) return;   // 已被换届取消（caller 已收取消）
            if (Volatile.Read(ref _isLeaderFlag) == 0)
            {
                slot.SetException(new NotLeaderException(LeaderId));   // 调用方契约语义——重路由新 leader 重试
                Volatile.Write(ref slot.State, 2);
                return;
            }

            var watermark = slot.CommittedOnly ? _commitPair.Read().Index : _appliedWatermark;
            if (index <= watermark)
            {
                slot.SetResult(index);
                Volatile.Write(ref slot.State, 2);
            }
            else
            {
                _window.Register(slot, index);
            }
        }
    }

    /// <summary>TCS 完成源注册（事件路径——锁内含"注册前已完成"检查：直排组提交与事件路径的
    /// append/完成交错时 index 可能已 applied——立即完成防挂死）。</summary>
    private void RegisterTcsCompletion(TaskCompletionSource<long> tcs, long index)
    {
        lock (_completionLock)
        {
            if (index <= _appliedWatermark) tcs.TrySetResult(index);
            else _pending[index] = tcs;
        }
    }

    /// <summary>TCS 完成源注册（committed 档——注册前 commit 水位已越过则立即完成，
    /// 否则入 <see cref="_pendingCommitted"/> 等 AdvanceCommit 前缀完成）。</summary>
    private void RegisterTcsCommittedCompletion(TaskCompletionSource<long> tcs, long index)
    {
        lock (_completionLock)
        {
            if (index <= _commitPair.Read().Index) tcs.TrySetResult(index);
            else _pendingCommitted[index] = tcs;
        }
    }

    /// <summary>TCS 完成源注册（LeaderLocal 档——注册前持久化水位已越过则立即完成，
    /// 否则入 <see cref="_pendingLeaderLocal"/> 等 <see cref="FlushPendingAppendsAsync"/>
    /// 的 fsync 组提交完成——不等多数派复制与 apply）。</summary>
    private void RegisterTcsLeaderLocalCompletion(TaskCompletionSource<long> tcs, long index)
    {
        lock (_completionLock)
        {
            if (index <= _store.PersistedIndex) tcs.TrySetResult(index);
            else _pendingLeaderLocal[index] = tcs;
        }
    }

    /// <summary>
    /// 提案配置条目（spec-04 §2——single-server 变更第一步/第二步）：追加配置条目
    /// （Kind = <see cref="RaftEntryKind.Config"/>，Content = <see cref="ClusterConfig"/> 稳定序列化）
    /// → 返回 = committed 且 applied（apply 时活动配置已切换——配置切换 = apply 产物）。
    /// 非 Leader 立即抛 <see cref="NotLeaderException"/>。
    /// </summary>
    /// <param name="config">提案的新配置（活动配置切换 = apply 产物）。</param>
    /// <param name="cancellationToken">取消令牌（换届/显式取消——调用方感知为 OperationCanceledException）。</param>
    /// <returns>配置条目落盘的日志 index（committed 且 applied——配置已切换）。</returns>
    /// <exception cref="NotLeaderException">本节点非 Leader（携带当前已知 leader——调用方重路由）。</exception>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> 为 null。</exception>
    public ValueTask<long> ProposeConfigAsync(ClusterConfig config, CancellationToken cancellationToken = default)
    {
        // 快速失败（volatile 观测——非 Leader 立即抛，不等入队）
        if (Volatile.Read(ref _isLeaderFlag) == 0)
            return ValueTask.FromException<long>(new NotLeaderException(LeaderId));
        if (Volatile.Read(ref _draining) != 0)   // ★ 二期-D3：drain 中拒绝新提案（客户端重路由）
            return ValueTask.FromException<long>(new NotLeaderException(LeaderId));
        ArgumentNullException.ThrowIfNull(config);
        var tcs = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        RegisterCancellation(tcs, cancellationToken);
        if (!_events.Writer.TryWrite(new RaftEvent.ProposeConfig(config, tcs)))
            return ProposeConfigWriteAsync(config, tcs, cancellationToken);   // 队列满——背压等待入队
        return new ValueTask<long>(tcs.Task);
    }

    private async ValueTask<long> ProposeConfigWriteAsync(ClusterConfig config, TaskCompletionSource<long> tcs, CancellationToken cancellationToken=default)
    {
        await _events.Writer.WriteAsync(new RaftEvent.ProposeConfig(config, tcs), cancellationToken).ConfigureAwait(false);
        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// 批复制（spec-08 §1 ReplicateBatchAsync）：一批命令作为一次追加——
    /// 返回批尾 index = committed 且 applied（批内全部随批尾提交应用）。
    /// </summary>
    /// <param name="commands">命令批（顺序追加——批尾 index 即返回值）。</param>
    /// <param name="cancellationToken">取消令牌（换届/显式取消——调用方感知为 OperationCanceledException）。</param>
    /// <returns>批尾命令的日志 index（committed 且 applied——批内全部已应用）。</returns>
    /// <exception cref="NotLeaderException">本节点非 Leader（携带当前已知 leader——调用方重路由）。</exception>
    /// <exception cref="ArgumentNullException"><paramref name="commands"/> 为 null。</exception>
    /// <exception cref="ArgumentException"><paramref name="commands"/> 为空批。</exception>
    public ValueTask<long> ReplicateBatchAsync(IReadOnlyList<ReadOnlyMemory<byte>> commands, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _isLeaderFlag) == 0)
            return ValueTask.FromException<long>(new NotLeaderException(LeaderId));
        if (Volatile.Read(ref _draining) != 0)   // ★ 二期-D3：drain 中拒绝新提案（客户端重路由）
            return ValueTask.FromException<long>(new NotLeaderException(LeaderId));
        ArgumentNullException.ThrowIfNull(commands);
        if (commands.Count == 0)
            throw new ArgumentException("命令批不能为空。", nameof(commands));
        var tcs = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        RegisterCancellation(tcs, cancellationToken);
        if (!_events.Writer.TryWrite(new RaftEvent.ReplicateBatch(commands, tcs)))
            return ReplicateBatchWriteAsync(commands, tcs, cancellationToken);
        return new ValueTask<long>(tcs.Task);
    }

    private async ValueTask<long> ReplicateBatchWriteAsync(IReadOnlyList<ReadOnlyMemory<byte>> commands, TaskCompletionSource<long> tcs, CancellationToken cancellationToken = default)
    {
        await _events.Writer.WriteAsync(new RaftEvent.ReplicateBatch(commands, tcs), cancellationToken).ConfigureAwait(false);
        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// 线性读（spec-08 §4 定案）：leader 向多数派确认身份（心跳往返一轮）→ 返回
    /// readIndex = commitIndex——分区期拿不到多数派确认 = 超时/取消（不返回过期 index）。
    /// Standalone（N=1）立即返回（多数派 = 自己）。非 Leader 立即抛 <see cref="NotLeaderException"/>。
    /// </summary>
    /// <param name="cancellationToken">取消令牌（多数派确认超时/换届——调用方感知为 OperationCanceledException）。</param>
    /// <returns>线性读位点 readIndex（= 当前 commitIndex——读到的不低于此水位）。</returns>
    /// <exception cref="NotLeaderException">本节点非 Leader（携带当前已知 leader——调用方重路由）。</exception>
    public ValueTask<long> ReadIndexAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _isLeaderFlag) == 0)
            return ValueTask.FromException<long>(new NotLeaderException(LeaderId));
        // ★ 租约快路径（RaftOptions.WithLeaseReads opt-in）：多数派确认后 (选举窗下界 − 漂移界)
        //   窗口内——新 leader 最早也要整段选举窗后才可能诞生，窗内本 leader 的 commitIndex
        //   即线性读位点，零往返返回；窗外自动回落多数派心跳确认。
        //   ★ 锚点门（二期-B §6.4）：本任期锚点未提交前不走快路径——commit 停在前任期水位。
        cancellationToken.ThrowIfCancellationRequested();
        if (IsLeaseValid && _commitPair.Read().Index >= Volatile.Read(ref _readIndexHoldIndex))
            return ValueTask.FromResult(_commitPair.Read().Index);
        var tcs = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        RegisterCancellation(tcs, cancellationToken);
        if (!_events.Writer.TryWrite(new RaftEvent.ReadIndex(tcs)))
            return ReadIndexWriteAsync(tcs, cancellationToken);
        return new ValueTask<long>(tcs.Task);
    }

    /// <summary>
    /// 线性读·follower 转发（二期-B §6.2）：本端非 leader 时把 ReadIndex 转发给当前已知
    /// leader——leader 侧多数派确认后应答 readIndex，本端等待 applied ≥ readIndex 后返回
    /// （读本地状态机 = 线性读的本地读段）。本端 leader → 直接 <see cref="ReadIndexAsync"/>
    /// （含租约快路径）。leader 未知 = 立即 <see cref="NotLeaderException"/>；转发重试有界
    /// （次数 + 总时限——leader 切换/抬任期后等收敛，超限 <see cref="TimeoutException"/>）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌（覆盖转发全程——含本地 apply 等待段）。</param>
    /// <returns>线性读位点 readIndex（本地已 apply 到 ≥ 该位点）。</returns>
    /// <exception cref="NotLeaderException">本端非 leader 且无已知 leader（调用方重路由）。</exception>
    /// <exception cref="TimeoutException">有界重试超限（leader 切换/网络未收敛）。</exception>
    public async ValueTask<long> ReadIndexForwardedAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _isLeaderFlag) != 0)
            return await ReadIndexAsync(cancellationToken);
        _ = LeaderId ?? throw new NotLeaderException(LeaderId);
        // 总时限 ≥ 4 选举窗（换届/重选举收敛 ≥ 1 窗）且不低于 2s——期间失败路径 50ms 节流
        var deadline = _clock.GetMsTimestamp()
            + Math.Max((long)(4 * _options.ElectionTimeoutMax).TotalMilliseconds, 2000);
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attempt > MaxReadIndexForwardAttempts || _clock.GetMsTimestamp() >= deadline)
                throw new TimeoutException($"转发线性读重试超限（attempts={attempt}）——leader 切换/网络未收敛。");
            if (Volatile.Read(ref _isLeaderFlag) != 0)
                return await ReadIndexAsync(cancellationToken);   // 任内升位——直接本地读
            if (LeaderId is not { } current)
            {
                await _clock.Delay(ReadIndexForwardRetryDelay, cancellationToken);   // leader 未知——等心跳/选举收敛
                continue;
            }
            // 单次转发 per-call 超时 = 选举窗上界（AE 往返远小于此——不跨选举窗悬挂）
            var resp = await SendRpcAsync(current, new ReadIndexReq { Term = _store.Term },
                _options.ElectionTimeoutMax, cancellationToken);
            if (resp is not ReadIndexResp r)
            {
                await _clock.Delay(ReadIndexForwardRetryDelay, cancellationToken);   // 失败/超时/畸形——节流重试
                continue;
            }
            if (r.Term > _store.Term)
            {
                await AdvanceTermFromRespAsync(r.Term, cancellationToken);   // 抬任期（落盘契约①）
                continue;   // leader 已随任期作废——下轮等收敛
            }
            if (r.ReadIndex < 0)
            {
                await _clock.Delay(ReadIndexForwardRetryDelay, cancellationToken);   // 应答方非 leader——按 leader 变更重试
                continue;
            }
            await WaitForAppliedAsync(r.ReadIndex, cancellationToken);
            return r.ReadIndex;
        }
    }

    /// <summary>
    /// 等待本地应用水位推进到 ≥ index（二期-B §6.3）：返回时本地业务状态机已 apply 到
    /// ≥ index——转发线性读（<see cref="ReadIndexForwardedAsync"/>）的本地读段。快照重建
    /// 不发 AppliedTo——重建完成后由状态机显式完成 ≤ N₀ 的 waiter（<see cref="OnSnapshotRebuilt"/>）。
    /// </summary>
    /// <param name="index">目标 applied 位点。</param>
    /// <param name="cancellationToken">取消令牌（降级/停止 = 批量取消终态）。</param>
    /// <returns>完成 = 本地业务状态机已 apply 到 ≥ index（水位已达 = 同步完成；快照重建完成也显式完成 ≤ N₀ 的 waiter）；取消/降级/停止 = 任务取消终态。</returns>
    public ValueTask WaitForAppliedAsync(long index, CancellationToken cancellationToken = default)
    {
        lock (_completionLock)
        {
            if (index <= _appliedWatermark)
                return ValueTask.CompletedTask;
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingAppliedWaiters.Add((index, tcs));
            RegisterAppliedWaiterCancellation(tcs, cancellationToken);
            return new ValueTask(tcs.Task);
        }
    }

    /// <summary>applied waiter 取消登记（与 <see cref="RegisterCancellation"/> 同形——非泛型 TCS）。</summary>
    private void RegisterAppliedWaiterCancellation(TaskCompletionSource tcs, CancellationToken ct)
    {
        if (!ct.CanBeCanceled) return;
        var reg = ct.Register(static state => ((TaskCompletionSource)state!).TrySetCanceled(), tcs);
        _loops.SubmitFast(_ =>
            new ValueTask(tcs.Task.ContinueWith(static (_, state) => ((CancellationTokenRegistration)state!).Dispose(),
                reg, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default)));
    }

    /// <summary>快照重建完成（applied = N₀ 不经 AppliedTo 事件——显式推水位并完成 ≤ N₀ 的
    /// applied waiter）。【循环线程——快照安装事件尾】</summary>
    private void OnSnapshotRebuilt(long snapshotIndex)
    {
        lock (_completionLock)
        {
            if (snapshotIndex > _appliedWatermark) _appliedWatermark = snapshotIndex;
            for (var i = _pendingAppliedWaiters.Count - 1; i >= 0; i--)
            {
                if (_pendingAppliedWaiters[i].Index <= snapshotIndex)
                {
                    _pendingAppliedWaiters[i].Done.TrySetResult();
                    _pendingAppliedWaiters.RemoveAt(i);
                }
            }
        }
    }

    // ═══ 租约读（RaftOptions.WithLeaseReads 启用——缺省关）═══

    /// <summary>租约是否有效：多数派确认后 (选举窗下界 − 时钟漂移界) 窗口内——
    /// 只读观测（调优/测试面；<see cref="ReadIndexAsync"/> 内部走同一判定）。</summary>
    public bool IsLeaseValid
    {
        get
        {
            if (_options.LeaseClockDriftBound <= TimeSpan.Zero) return false;
            if (Volatile.Read(ref _isLeaderFlag) == 0) return false;
            var start = Interlocked.Read(ref _quorumAckTicks);
            if (start == long.MinValue) return false;
            var leaseMs = (long)(_options.ElectionTimeoutMin - _options.LeaseClockDriftBound).TotalMilliseconds;
            return _clock.GetMsTimestamp() - start < leaseMs;
        }
    }

    /// <summary>follower 成功应答（复制 lane 线程回调）：①learner 追平晋级判定（AutoPromote
    /// 登记——matchIndex 追上持久化尾即投递晋级提案）；②租约读确认态刷新（新鲜 majority 含
    /// leader 自身：本地追加落盘即确认）。租约关闭且无晋级登记时直返（lane 热路径零开销）。</summary>
    private void OnFollowerAck(NodeId peer)
    {
        if (Volatile.Read(ref _pendingPromoteCount) > 0)
        {
            var promote = false;
            lock (_leaseLock)
            {
                // 追平判定：matchIndex ≥ 持久化尾（learner 已收到 leader 全部已落盘条目）
                if (_pendingPromote.ContainsKey(peer)
                    && _replication is not null
                    && _replication.TryGetMatchIndex(peer, out var matchIndex)
                    && matchIndex >= _store.PersistedIndex)
                {
                    _pendingPromote.Remove(peer);
                    promote = true;
                }
            }
            if (promote)
            {
                Interlocked.Decrement(ref _pendingPromoteCount);
                if (!_events.Writer.TryWrite(new RaftEvent.PromoteLearner(peer)))
                    _loops.SubmitFast((Func<CancellationToken, ValueTask>)(async ct =>
                        await _events.Writer.WriteAsync(new RaftEvent.PromoteLearner(peer), ct).ConfigureAwait(false)));
            }
        }
        if (_options.LeaseClockDriftBound <= TimeSpan.Zero) return;
        var now = _clock.GetMsTimestamp();
        lock (_leaseLock)
        {
            _followerAckTicks[peer] = now;
            var fresh = 1;   // leader 自身
            var freshWindowMs = (long)_options.HeartbeatInterval.TotalMilliseconds * 2;
            foreach (var (_, ticks) in _followerAckTicks)
                if (now - ticks <= freshWindowMs) fresh++;
            if (fresh >= _config.MajorityThreshold)
                Volatile.Write(ref _quorumAckTicks, now);
        }
    }

    // ═══ Standby 引导（加入既有集群——learner 入组 + 追平晋级）═══

    /// <summary>加入既有集群（Standby 引导编排，进程内/已互联传输形态）：向 bootstrap 同伴
    /// 轮转递 <see cref="JoinReq"/>——缺席即以 learner 入组 + 登记追平晋级；已入组未晋级期间
    /// 周期性重递（换届/新 leader 场景自愈），<see cref="IsVoter"/> 翻真返回。
    /// 前置：本节点已以 [self learner] 配置 <see cref="StartAsync"/>（本地配置仅引导用——
    /// 真实配置随复制收敛；重启节点 WAL 已载真实配置，宣告幂等）。</summary>
    /// <param name="bootstrapPeers">引导同伴节点 ID（至少一个当前集群成员；本端自身忽略）。</param>
    /// <param name="timeout">总时限（缺省 30s——大基线追平的集群放宽）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="autoPromote">true = 追平后自动晋级 voter（缺省，Standby 形态）；false = 保持
    /// learner 永久只读（DP 永久只读节点引导——完成条件 = 已入组且配置收敛含自身，永不晋级）。</param>
    /// <returns>完成时本节点已入组（autoPromote 形态另需已晋级 voter；总时限到抛 <see cref="TimeoutException"/>）。</returns>
    public Task JoinAsync(IEnumerable<NodeId> bootstrapPeers, TimeSpan? timeout = null, CancellationToken cancellationToken = default, bool autoPromote = true)
    {
        ArgumentNullException.ThrowIfNull(bootstrapPeers);
        var peers = bootstrapPeers.Distinct().Where(p => p != _self).ToArray();
        ArgumentOutOfRangeException.ThrowIfZero(peers.Length);
        return JoinLoopAsync(peers.Length, (i, token) => new ValueTask<NodeId>(peers[i % peers.Length]), timeout, cancellationToken, autoPromote);
    }

    /// <summary>加入既有集群（TCP 形态）：向 bootstrap 端点拨号（身份握手得知）后同
    /// <see cref="JoinAsync(IEnumerable{NodeId}, TimeSpan?, CancellationToken, bool)"/>。
    /// bootstrap 端点非 leader = 应答提示 + 轮转下一端点——建议覆盖全部成员。</summary>
    /// <param name="bootstrapEndpoints">引导端点（至少一个当前集群成员的监听地址）。</param>
    /// <param name="timeout">总时限（缺省 30s）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="autoPromote">true = 追平后自动晋级 voter（缺省）；false = 保持 learner 永久只读。</param>
    /// <returns>完成时本节点已入组且已晋级 voter（总时限到抛 <see cref="TimeoutException"/>）。</returns>
    /// <exception cref="NotSupportedException">传输非 TCP 介质（端点拨号加入不可用）。</exception>
    public Task JoinAsync(IEnumerable<IPEndPoint> bootstrapEndpoints, TimeSpan? timeout = null, CancellationToken cancellationToken = default, bool autoPromote = true)
    {
        ArgumentNullException.ThrowIfNull(bootstrapEndpoints);
        if (_transport is not Transport.Tcp.ClusterTransport dialer)
            throw new NotSupportedException(
                $"端点拨号加入需 ClusterTransport 介质（当前 {_transport.GetType().Name}）——进程内/已互联形态用 JoinAsync(bootstrapPeers)。");
        var endpoints = bootstrapEndpoints.Distinct().ToArray();
        ArgumentOutOfRangeException.ThrowIfZero(endpoints.Length);
        return JoinLoopAsync(endpoints.Length,
            (i, token) => dialer.ConnectAsync(endpoints[i % endpoints.Length], token), timeout, cancellationToken, autoPromote);
    }

    /// <summary>加入循环：轮转递 JoinReq（单端点 2s 探测超时）→ 受理后轮询 IsVoter；
    /// 周期 250ms；总时限到 = TimeoutException。</summary>
    private async Task JoinLoopAsync(int peerCount, Func<int, CancellationToken, ValueTask<NodeId>> resolvePeerAsync,
        TimeSpan? timeout, CancellationToken cancellationToken=default, bool autoPromote = true)
    {
        var deadline = _clock.GetMsTimestamp() + (long)(timeout ?? TimeSpan.FromSeconds(30)).TotalMilliseconds;
        var round = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_clock.GetMsTimestamp() >= deadline)
                throw new TimeoutException($"加入集群超时（bootstrap {peerCount} 端点）——追平未完成或引导同伴不可达。");
            var index = round % peerCount;
            try
            {
                var remote = await resolvePeerAsync(index, cancellationToken).ConfigureAwait(false);
                var len = RaftRpcCodec.EncodePooled(new JoinReq { Term = _store.Term, CandidateId = _self, AutoPromote = autoPromote }, out var buffer);
                try
                {
                    var respBytes = await _transport.SendRequestAsync(remote, ProtocolIds.Raft,
                        buffer.AsMemory(0, len),
                        new RequestOptions(TimeSpan.FromSeconds(2)), cancellationToken).ConfigureAwait(false);
                    if (RaftRpcCodec.TryDecode(respBytes, out var rpc) && rpc is JoinResp { Accepted: true })
                    {
                        // 完成条件分流（#441）：autoPromote = 晋级 voter 翻真；learner 永久只读形态 =
                        // 复制收敛配置含自身——本地引导配置 [self learner] 恒含自身，不足以证收敛；
                        // 真实配置（集群成员 + 本端 learner）随复制到达时成员数必然 >1，
                        // 以此证引导配置已被复制产物替换（Accepted 只代表 leader 已受理，配置提交另序）
                        if (autoPromote ? IsVoter : Config.Contains(_self) && Config.Count > 1)
                            return;
                    }
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
                _logger?.LogDebug("Join 重试：bootstrap#{Index} 原因={Reason}", index, ex.Message);
            }
            round++;
            await _clock.Delay(250, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<long> ReadIndexWriteAsync(TaskCompletionSource<long> tcs, CancellationToken cancellationToken=default)
    {
        await _events.Writer.WriteAsync(new RaftEvent.ReadIndex(tcs), cancellationToken).ConfigureAwait(false);
        return await tcs.Task.ConfigureAwait(false);
    }
}
