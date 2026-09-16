using System.Threading.Channels;
using TC.Tier.Core.Logging;
using TC.Tier.Core.Net.Channels;
using TC.Tier.Core.Net.Wire;
using TC.Tier.Core.Execution;

namespace TC.Tier.Core.Net.P2P;

/// <summary>
/// HyParView 会员管理器（spec-06 全量 × spec-12 §6 机制面形态映射——视图维护/随机游走 JOIN/
/// PhiAccrual 故障检测；gossip/成员消息走数据报形态，UDP bearer 天然）。
/// <para>★ 挂载（§3.5）：构造收 <see cref="IProtocolTransport"/> 端点，<see cref="StartAsync"/> 经
///   <see cref="ICoreProtocolPort"/> 内部口注册 <see cref="ProtocolIds.HyParView"/>（0x02）——
///   与使用方协议域同一分发路径（机制零特权）；传输生命周期归装配层。</para>
/// <para>★ 语义纪律（spec-06 审查定案）：HyParView = 会员管理，不含广播——广播是独立
///   dissemination 组件（<see cref="IBroadcast"/>）。</para>
/// <para>★ 线程契约：消息处理（介质分发上下文，多路）+ 周期循环（心跳/shuffle 单 worker）——
///   视图变更全部经 <see cref="_lock"/> 串行化（内存操作 µs 级，锁短临界）；
///   视图维护的尽力发送经 <see cref="_sends"/>（TaskSink——同步完成零分配快路径，异常组内观测，
///   Dispose 有界排空）。</para>
/// </summary>
public sealed class PeerController : IAsyncDisposable
{
    private readonly NodeId _self;
    private readonly IProtocolTransport _transport;
    private readonly PeerOptions _options;
    private readonly TimeProvider _clock;   // 时钟供给源（时钟缝 件一 P1——心跳/shuffle 节拍）
    private readonly Random _random;
    private readonly ILogger? _logger;
    private readonly TaskSink _sends;   // 视图维护发送（尽力送达——fire-and-forget 的受控形态）
    private readonly object _lock = new();
    private readonly Dictionary<NodeId, long> _active = [];      // 活跃视图（对称邻居）→ 建立时间
    private readonly LinkedList<NodeId> _passive = [];          // 被动视图（头 = 最新——补位优先）
    private readonly Dictionary<NodeId, PhiAccrualDetector> _phiByPeer = [];   // ★ 每 peer 独立 φ 采样（到达间隔因 peer 而异）
    private readonly HandlerBridge _bridge;
    private readonly DeadlineRegistry _registry;   // 节拍注册表（null 构造参 = Shared——心跳去 TimerQueue）
    private TaskCompletionSource? _joinPending;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private IDisposable? _pingSubscription;   // 节拍订阅（StartAsync 建、StopAsync 退——句柄幂等）
    private Channel<byte> _pings = null!;   // 心跳脉冲（容量 1 DropOldest——脉冲语义，堆积无意义）
    private long _nextPingTicks;   // 下一次心跳（TickCount64——节拍线程读，wake 回调自推进）
    private int _started;
    private int _stopped;
    private int _disposed;

    /// <summary>本节点 ID。</summary>
    public NodeId Self => _self;

    /// <summary>活跃视图快照（对称邻居——会员连通性的地基）。</summary>
    public IReadOnlyList<NodeId> ActiveView { get { lock (_lock) return [.. _active.Keys]; } }

    /// <summary>被动视图快照（备用池——故障补位来源）。</summary>
    public IReadOnlyList<NodeId> PassiveView { get { lock (_lock) return [.. _passive]; } }

    /// <summary>活跃视图大小（k——有界收敛断言）。</summary>
    public int ActiveCount { get { lock (_lock) return _active.Count; } }

    /// <summary>成员加入事件（本节点被对端建立活跃关系）。</summary>
    public event Action<NodeId>? PeerJoined;

    /// <summary>故障检出事件（φ 判定——本地检测）。</summary>
    public event Action<NodeId>? PeerFailed;

    /// <summary>
    /// 构造（零 IO——挂载与启动经 <see cref="StartAsync"/>；传输端点生命周期归装配层）。
    /// </summary>
    /// <param name="self">本节点 ID。</param>
    /// <param name="transport">节点端点完整面（任一介质——须实现 <see cref="ICoreProtocolPort"/>）。</param>
    /// <param name="options">会员参数。</param>
    /// <param name="logger">日志（可选）。</param>
    /// <param name="registry">节拍注册表（null = 进程级 <see cref="DeadlineRegistry.Shared"/>）。</param>
    public PeerController(NodeId self, IProtocolTransport transport, PeerOptions options, ILogger? logger = null,
        DeadlineRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(options);
        _self = self;
        _transport = transport;
        _options = options;
        _clock = options.Clock;   // 时钟供给源（时钟缝 件一 P1）
        _random = options.Random ?? Random.Shared;
        _logger = logger;
        _registry = registry ?? DeadlineRegistry.Shared;
        _sends = new TaskSink(name: $"HyParView[{self}]", logger: logger);
        _bridge = new HandlerBridge(this);
    }

    /// <summary>
    /// 启动：经内部口注册 HyParView 协议域（数据报 handler + UDP bearer 声明）+ 订阅对端离线 +
    /// 周期循环（心跳广播 + φ 故障检测 + shuffle）。只可一次；重启 = 新实例（注册面一次性）。
    /// </summary>
    /// <returns>完成时启动序列已提交（协议域注册/事件订阅/周期循环已就绪——启动同步完成）。</returns>
    /// <exception cref="InvalidOperationException">重复启动或传输未实现内部挂载口。</exception>
    public Task StartAsync()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("PeerController 已启动（StartAsync 只可一次）。");
        if (_transport is not ICoreProtocolPort core)
            throw new InvalidOperationException(
                $"机制挂载须内部注册口（ICoreProtocolPort）——介质 {_transport.GetType().Name} 未实现；机制宿主归 Core.Net（spec-12 §3.5）。");
        core.RegisterCoreProtocol(ProtocolIds.HyParView, _bridge, DatagramBearer.Udp);
        _transport.PeerGone += OnPeerGone;
        _cts = new CancellationTokenSource();
        // ★ 心跳节拍去 TimerQueue（2026-09-03 冻结判例）：固定周期形态——wake 回调自推进 deadline
        //   （回调在节拍线程执行，Interlocked 安全）+ 写脉冲（容量 1 丢弃——堆积无意义）。
        //   初始 = now + HeartbeatInterval（首拍不立即打）。Task.Delay 走 TimerQueue——高频窗口
        //   丢表项病理（销案记录 §1）在周期形态上已实锤。
        _pings = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        _nextPingTicks = _clock.GetMsTimestamp() + (long)_options.HeartbeatInterval.TotalMilliseconds;
        _pingSubscription = _registry.Subscribe(
            deadlineTicks: () => Volatile.Read(ref _nextPingTicks),
            wake: () =>
            {
                Volatile.Write(ref _nextPingTicks,
                    _clock.GetMsTimestamp() + (long)_options.HeartbeatInterval.TotalMilliseconds);
                _pings.Writer.TryWrite(0);
            });
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    /// <summary>
    /// JOIN（spec-06 §3）：把种子加入活跃视图 → 发 JOIN 随机游走（TTL 递减，沿途收藏被动视图、
    /// 终点回送视图）→ 等优先 NEIGHBOR 应答（<see cref="PeerOptions.JoinWaitTimeout"/> 有界等待——
    /// 应答丢失时种子已在活跃视图即会员关系建立，视图收敛交给 shuffle/周期维护）。
    /// <para>★ JOIN 是一次性消息（协议层不重发）——丢包介质下的收敛由使用方周期重 JOIN 驱动。</para>
    /// </summary>
    /// <param name="seed">种子节点（已注册的既有成员）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>完成时 JOIN 序列已走完（等 NEIGHBOR 应答或超时——应答丢失时种子已在活跃视图即会员关系已建立）。</returns>
    /// <exception cref="InvalidOperationException">未启动（先 <see cref="StartAsync"/>）。</exception>
    public async Task JoinAsync(NodeId seed, CancellationToken ct = default)
    {
        if (_started == 0)
            throw new InvalidOperationException("PeerController 未启动（先 StartAsync）。");
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        NodeId? evicted;
        lock (_lock)
        {
            _joinPending = pending;
            evicted = AddActiveLocked(seed);   // ★ 种子直接入活跃视图（论文 JOIN 第一步）
        }
        NotifyEvicted(evicted);
        _logger?.LogInformation("P2P JOIN 发起：self={Self} seed={Seed}", _self, seed);
        await SendAsync(seed, new JoinMsg { Origin = _self, Ttl = _options.JoinTtl }, ct).ConfigureAwait(false);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_options.JoinWaitTimeout);
            await pending.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // ★ 应答丢失——种子已在活跃视图（会员关系已建立），视图收敛交给 shuffle/周期维护
        }
    }

    /// <summary>主动离开（spec-06 §3——DISCONNECT 广播下电前调用）。
    /// <para>★ 广播尽力语义（flaky 判例）：InProcess 直排下 handler 同步执行
    /// （HandleDisconnect→Refill→Neighbor 内联再发=递归链），单个发送的异常若中断循环
    /// 则剩余成员永远收不到离开通知（视图悬挂）——逐成员隔离，单个失败不中断余下。</para></summary>
    /// <param name="ct">取消令牌（DISCONNECT 发送可取消）。</param>
    /// <returns>完成时 DISCONNECT 已尽力广播至全部活跃成员（本端视图已清空）。</returns>
    public async Task LeaveAsync(CancellationToken ct = default)
    {
        NodeId[] peers;
        lock (_lock)
        {
            peers = [.. _active.Keys];
            _active.Clear();
            _passive.Clear();
        }
        foreach (var p in peers)
        {
            try
            {
                await SendAsync(p, new DisconnectMsg(), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "DISCONNECT 广播单点失败（尽力继续）：peer={Peer}", p);
            }
        }
    }

    /// <summary>停止周期循环 + 退订对端事件（协议域注册面一次性——不注销）。</summary>
    /// <param name="ct">取消令牌（等待循环退出可超时/取消）。</param>
    /// <returns>完成时周期循环已停止、节拍与对端事件已退订（幂等——重复调用立即完成）。</returns>
    public async Task StopAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        _cts?.Cancel();
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(ct).ConfigureAwait(false); }
            catch (TimeoutException) { }
        }
        // ★ 节拍退订（同步退订、节拍线程共享无需等——心跳节拍随 TimerQueue 去 Task.Delay 一起换源）
        _pingSubscription?.Dispose();
        _pingSubscription = null;
        _transport.PeerGone -= OnPeerGone;
    }

    /// <inheritdoc/>
    /// <returns>完成时控制器已停止（未显式 StopAsync 则随释放停止）且在途发送已排空（幂等）。</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_started != 0 && _stopped == 0)
            await StopAsync().ConfigureAwait(false);
        await _sends.DisposeAsync().ConfigureAwait(false);   // 有界排空在途发送
        _cts?.Dispose();
    }

    // ═══ 入站分发（介质 handler——解码后路由）═══

    /// <summary>入站数据报入口（注册的 IDatagramHandler——介质分发上下文回调）。</summary>
    /// <param name="from">来源节点（分发即身份——spec-12 §4）。</param>
    /// <param name="payload">线格式载荷。</param>
    internal void OnDatagram(NodeId from, ReadOnlyMemory<byte> payload)
    {
        if (!HyParViewMessageCodec.TryDecode(payload.Span, out var msg))
        {
            _logger?.LogWarning("HyParView 载荷解码失败（协议域格式违规——丢弃）：from={From} len={Length}", from, payload.Length);
            return;
        }
        OnMessage(from, msg);
    }

    /// <summary>入站消息处理（解码后——测试可直调）。</summary>
    /// <param name="from">来源节点（分发即身份）。</param>
    /// <param name="msg">消息。</param>
    internal void OnMessage(NodeId from, HyParViewMessage msg)
    {
        switch (msg)
        {
            case JoinMsg join: HandleJoin(from, join); break;
            case NeighborMsg n: HandleNeighbor(from, n); break;
            case DisconnectMsg: HandleDisconnect(from); break;
            case FailMsg fail: HandleFail(fail.Failed); break;
            case ShuffleMsg sh: HandleShuffle(from, sh); break;
            case HeartbeatMsg:
                lock (_lock)
                {
                    if (!_phiByPeer.TryGetValue(from, out var phi))
                    {
                        phi = CreatePhiDetector();
                        _phiByPeer[from] = phi;
                    }
                    phi.RecordHeartbeat();
                }
                break;
        }
    }

    // ═══ 消息处理（介质分发上下文——视图变更锁内）═══

    private void OnPeerGone(NodeId gone)
    {
        lock (_lock)
        {
            RemovePassiveLocked(gone);   // ★ 离开者出备用池——补位不再引入
            if (RemoveActiveLocked(gone)) RefillFromPassiveLocked();
        }
    }

    private void HandleJoin(NodeId from, JoinMsg join)
    {
        NodeId? evicted;
        lock (_lock)
        {
            if (join.Ttl == 0 || _active.Count < _options.ActiveViewSize)
            {
                // ★ 终点/有空间：直接建立活跃关系 + 优先 NEIGHBOR 回送视图（论文 JOIN 第三步）
                evicted = AddActiveLocked(join.Origin);
            }
            else
            {
                // ★ 游走中：收藏入被动视图 + 转发（TTL 递减——随机游走）
                AddPassiveLocked(join.Origin);
                var forward = _active.Keys.ElementAt(_random.Next(_active.Count));
                SubmitSend(forward, new JoinMsg { Origin = join.Origin, Ttl = join.Ttl - 1 });
                return;
            }
        }
        NotifyEvicted(evicted);
        SubmitSend(join.Origin, new NeighborMsg
        {
            Priority = true,
            View = ActiveViewSnapshot(),
        });
    }

    private void HandleNeighbor(NodeId from, NeighborMsg neighbor)
    {
        bool joined;
        NodeId? evicted;
        lock (_lock)
        {
            joined = !_active.ContainsKey(from);
            evicted = AddActiveLocked(from);   // ★ 对称性：应答方进活跃视图
            foreach (var m in neighbor.View)
                if (m != _self && !_active.ContainsKey(m))
                    AddPassiveLocked(m);
            if (neighbor.Priority && _joinPending is { } p)
            {
                _joinPending = null;
                p.TrySetResult();
            }
        }
        NotifyEvicted(evicted);
        if (joined)
        {
            PeerJoined?.Invoke(from);
            _logger?.LogInformation("P2P 邻居建立：self={Self} peer={Peer} active={Count}", _self, from, ActiveCount);
        }
    }

    private void HandleDisconnect(NodeId from)
    {
        lock (_lock)
        {
            RemovePassiveLocked(from);   // ★ 离开者移出备用池（补位不再引入——DISCONNECT 语义：全视图移除）
            if (RemoveActiveLocked(from)) RefillFromPassiveLocked();
        }
    }

    private void HandleFail(NodeId failed)
    {
        lock (_lock)
        {
            RemovePassiveLocked(failed);   // ★ 故障者移出备用池（补位不再引入）
            if (RemoveActiveLocked(failed)) RefillFromPassiveLocked();
        }
    }

    private void HandleShuffle(NodeId from, ShuffleMsg shuffle)
    {
        lock (_lock)
        {
            foreach (var m in shuffle.Sample)
                if (m != _self && !_active.ContainsKey(m))
                    AddPassiveLocked(m);
        }
    }

    // ═══ 发送（数据报形态——协议域 0x02）═══

    /// <summary>直发（await 形态——周期循环/用户路径，背压传导）。★ 0-Copy：池化编码直写传输帧缓冲。</summary>
    private async ValueTask SendAsync(NodeId target, HyParViewMessage msg, CancellationToken ct)
    {
        var len = HyParViewMessageCodec.EncodePooled(msg, out var buffer);
        try
        {
            await _transport.SendDatagramAsync(target, ProtocolIds.HyParView, buffer.AsMemory(0, len), ct)
                .ConfigureAwait(false);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// 视图维护发送（锁内/不 await 路径——尽力送达）：SubmitFast 同步完成零分配快路径、
    /// 未同步完成注册跟踪（异常组内观测不黑洞、Dispose 有界排空）——fire-and-forget 的受控形态。
    /// </summary>
    private void SubmitSend(NodeId target, HyParViewMessage msg)
        => _sends.SubmitFast(ct => SendAsync(target, msg, ct));

    private NodeId[] ActiveViewSnapshot() { lock (_lock) return [.. _active.Keys]; }

    // ═══ 视图维护（锁内原语）═══

    /// <summary>加活跃成员（满则挤掉最旧一名——论文 JOIN/NEIGHBOR 满视图行为 + ★ 挤掉通知保对称）。
    /// <para>★ 返回被挤掉者——通知须由调用方在<b>锁外</b>发送（视图超界判例 2026-09-02）：锁内
    /// SubmitSend 直排派发（InProcess 同步回调）立即进入被挤者 handler → 其补位 Neighbor 递归
    /// 回本控制器 → 重入 AddActiveLocked 落在"挤掉后、插入前"窗口再插一员 → 视图 5/4 超界。
    /// 挤掉+插入原子完成后通知移锁外，任意递归链下每次 add 均净 -1+1 = 有界。</para></summary>
    private NodeId? AddActiveLocked(NodeId id)
    {
        if (id == _self) return null;
        if (_active.ContainsKey(id))
        {
            _active[id] = _clock.GetMsTimestamp();
            return null;
        }
        NodeId? evicted = null;
        if (_active.Count >= _options.ActiveViewSize)
        {
            evicted = _active.OrderBy(kv => kv.Value).First().Key;
            RemoveActiveLocked(evicted.Value);
        }
        _active[id] = _clock.GetMsTimestamp();
        return evicted;
    }

    /// <summary>挤掉通知（锁外发送——见 <see cref="AddActiveLocked"/> 判例注释）。被挤者收到
    /// DISCONNECT 移除本端；否则它单边持有本端（且从未收到本端心跳 → 无 φ 检测器）→
    /// 视图残留永不清理（实测形态：单边持有 + 零检测 = 永驻活跃视图）。</summary>
    private void NotifyEvicted(NodeId? evicted)
    {
        if (evicted is not { } oldest) return;
        SubmitSend(oldest, new DisconnectMsg());
        _logger?.LogInformation("P2P 活跃视图满——挤掉最旧成员并通知：{Peer}", oldest);
    }

    /// <summary>加被动成员（上限裁剪——去掉最旧即链表尾）。</summary>
    private void AddPassiveLocked(NodeId id)
    {
        if (id == _self) return;
        var node = _passive.Find(id);
        if (node is not null) _passive.Remove(node);
        _passive.AddFirst(id);   // 头 = 最新
        while (_passive.Count > _options.PassiveViewSize)
            _passive.RemoveLast();
    }

    /// <summary>移除活跃成员 + φ 采样器清理（锁内原语）。</summary>
    private bool RemoveActiveLocked(NodeId id)
    {
        _phiByPeer.Remove(id);
        return _active.Remove(id);
    }

    /// <summary>移除被动成员（锁内原语）。</summary>
    private void RemovePassiveLocked(NodeId id)
    {
        var node = _passive.Find(id);
        if (node is not null) _passive.Remove(node);
    }

    /// <summary>
    /// 被动视图补位（故障/断开后——年轻者优先，spec-06 §4）。
    /// ★ 先快照候选再动作：SubmitSend 在直排派发介质（InProcess）同步进入对端 handler，
    /// 可能重入本控制器变异 <see cref="_passive"/>——边遍历链表边发送会踩已摘除节点。
    /// </summary>
    private void RefillFromPassiveLocked()
    {
        List<NodeId>? promote = null;
        var next = _passive.First;
        while (next is not null && _active.Count + (promote?.Count ?? 0) < _options.ActiveViewSize)
        {
            var cand = next.Value;
            next = next.Next;
            if (_active.ContainsKey(cand)) continue;
            (promote ??= []).Add(cand);
        }
        foreach (var cand in promote ?? [])
        {
            _passive.Remove(cand);
            _active[cand] = _clock.GetMsTimestamp();
            SubmitSend(cand, new NeighborMsg { Priority = false, View = [.. _active.Keys] });
        }
    }

    // ═══ 周期循环（心跳 + φ 故障检测 + shuffle）═══

    /// <summary>测试白盒：指定 peer 的当前 φ 值（无检测器 = 0）。</summary>
    internal double PhiForTest(NodeId peer)
    {
        lock (_lock)
            return _phiByPeer.TryGetValue(peer, out var phi) ? phi.Phi() : 0;
    }

    /// <summary>按当前参数创建 φ 检测器（每 peer 独立实例）。</summary>
    private PhiAccrualDetector CreatePhiDetector() => new(_options.PhiThreshold,
        Math.Max(3, (int)(TimeSpan.FromSeconds(60) / _options.HeartbeatInterval)));

    /// <summary>心跳广播 + φ 故障检测（每 peer 独立采样——spec-06 §5）。</summary>
    private async ValueTask DetectFailuresAsync(CancellationToken ct)
    {
        NodeId[] active;
        lock (_lock) active = [.. _active.Keys];
        var failed = new List<NodeId>();
        foreach (var peer in active)
        {
            await SendAsync(peer, new HeartbeatMsg(), ct).ConfigureAwait(false);
            PhiAccrualDetector? phi;
            lock (_lock)
            {
                if (!_phiByPeer.TryGetValue(peer, out phi))
                {
                    phi = CreatePhiDetector();
                    _phiByPeer[peer] = phi;
                }
            }
            // ★ 单向边判死（零样本超龄）：对方不认为我是邻居（对称视图破坏——满员挤掉/单向补位）
            //   则永不回发心跳 → 我方 φ 零样本恒 0（minSamples 宽限）→ 视图悬挂占坑。
            //   宽限 = 心跳 ×8（覆盖调度抖动；50ms→400ms）——超龄剔除。
            if (phi.IsFailed()
                || phi.IsZeroSampleExpired((long)_options.HeartbeatInterval.TotalMilliseconds * 8))
            {
                failed.Add(peer);
            }
        }
        if (failed.Count == 0) return;

        lock (_lock)
        {
            foreach (var f in failed)
            {
                RemovePassiveLocked(f);   // ★ 故障者移出备用池（否则补位立刻拉回——DISCONNECT 同款楔死）
                if (RemoveActiveLocked(f)) RefillFromPassiveLocked();
            }
        }
        foreach (var f in failed)
        {
            PeerFailed?.Invoke(f);
            _logger?.LogWarning("P2P 故障检出：peer={Peer}（φ 判定）", f);
            // ★ FAIL 扩散（spec-06 §3——通知其余邻居）
            lock (_lock) active = [.. _active.Keys];
            foreach (var p in active)
                await SendAsync(p, new FailMsg { Failed = f }, ct).ConfigureAwait(false);
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var nextShuffle = _clock.GetMsTimestamp() + (long)_options.ShuffleInterval.TotalMilliseconds;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // ★ 心跳等待 = channel 驱动（节拍线程到期写脉冲）——不再 Task.Delay（TimerQueue）
                await _pings.Reader.ReadAsync(ct).ConfigureAwait(false);

                // 1. 心跳广播 + φ 故障检测（spec-06 §5——每 peer 独立采样）
                await DetectFailuresAsync(ct).ConfigureAwait(false);

                // 2. Shuffle（被动视图定期交换——成员搅动收敛，spec-06 §2）
                if (_clock.GetMsTimestamp() >= nextShuffle)
                {
                    nextShuffle = _clock.GetMsTimestamp() + (long)_options.ShuffleInterval.TotalMilliseconds;
                    NodeId target;
                    NodeId[] sample;
                    lock (_lock)
                    {
                        if (_active.Count == 0) continue;
                        target = _active.Keys.ElementAt(_random.Next(_active.Count));
                        var sampleCount = Math.Min(_options.Prwl, _passive.Count);
                        sample = [.. _passive.Take(sampleCount)];
                    }
                    await SendAsync(target, new ShuffleMsg { Sample = sample }, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // ★ 周期循环必须活着（flaky 判例）：心跳/φ 检测/shuffle 全在此循环——静默死亡=
            //   视图悬挂永不自愈（离开广播漏发者、单向边全部卡死）。单轮失败只记日志继续。
            _logger?.LogError(ex, "HyParView 周期循环异常（隔离继续）：self={Self}", _self);
        }
    }

    /// <summary>协议域 handler 桥（介质分发 → OnDatagram——异常隔离归分发器）。</summary>
    private sealed class HandlerBridge(PeerController owner) : IDatagramHandler
    {
        /// <summary>入站数据报转发（直通 owner.<see cref="OnDatagram"/>——解码/路由归控制器）。</summary>
        /// <param name="from">来源节点。</param>
        /// <param name="payload">线格式载荷。</param>
        public void OnDatagram(NodeId from, ReadOnlyMemory<byte> payload) => owner.OnDatagram(from, payload);
    }
}
