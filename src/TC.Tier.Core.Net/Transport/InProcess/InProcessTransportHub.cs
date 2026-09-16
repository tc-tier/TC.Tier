using System.Collections.Concurrent;
using TC.Tier.Core.Net.Channels;
using TC.Tier.Core.Observability;

namespace TC.Tier.Core.Net.Transport.InProcess;

/// <summary>
/// 进程内传输枢纽（spec-12 §7——InProcess 介质 = 同构基准/测试主场：与网络介质零差别验收的锚；
/// 同进程恒可信——安全档不适用（恒明文语义），无握手/连接概念）。
/// <para>★ 装配：一个枢纽 = 一个集群域（同枢纽内节点互通）；节点经 <see cref="Register"/> 注册
///   即可达（对既有节点双向 PeerConnected）；<see cref="Unregister"/> 即离线（PeerGone 广播）。</para>
/// <para>★ 投递：发送方上下文直排派发（同步回调 + 异常隔离——背压 = handler 慢直接传导到发送方，
///   spec-12 §7 背压模型的内存形态）。</para>
/// <para>★ 注入矩阵内建（§9.2——延迟/分区/丢包/乱序定向注入，TCP 介质等价语义；
///   随机源可注入固定种子保证测试确定性）。</para>
/// </summary>
public sealed class InProcessTransportHub : IAsyncDisposable
{
    private readonly ConcurrentDictionary<NodeId, InProcessNode> _nodes = new();
    private readonly ConcurrentDictionary<(NodeId From, NodeId To), TimeSpan> _latency = new();
    private readonly ConcurrentDictionary<(NodeId From, NodeId To), double> _dropRate = new();
    private readonly ConcurrentDictionary<(NodeId From, NodeId To), byte> _partitioned = new();
    private readonly ConcurrentDictionary<(NodeId From, NodeId To), byte> _reorder = new();
    private readonly Random _random;
    private readonly FaultInjector _faults;
    private int _disposed;

    /// <summary>构造。</summary>
    /// <param name="options">传输选项（内存介质取请求回调域+慢回调阈值——参数面与 TCP 同源；
    ///   null = 缺省。测试主场可调小超时驱动超时路径）。</param>
    /// <param name="random">随机源（丢包判定——测试可注入固定种子保证确定性）。</param>
    /// <param name="hub">可观测中心（可选——null = Disabled；spec-12 §9.1 唯一接入点）。</param>
    public InProcessTransportHub(TransportOptions? options = null, Random? random = null, ObservabilityHub? hub = null)
    {
        RequestTimeout = options?.EffectiveRequestTimeout ?? TransportOptions.DefaultRequestTimeout;
        RequestPendingCapacity = options?.RequestPendingCapacity ?? RequestBroker.DefaultCapacity;
        RequestDedupCapacity = options?.RequestDedupCapacity ?? RequestBroker.DefaultDedupCapacity;
        RequestDedupMaxBytes = options?.RequestDedupMaxBytes ?? RequestBroker.DefaultDedupMaxBytes;
        StreamWindowFrames = options?.StreamWindowFrames ?? 16;
        SlowDispatchThreshold = options?.EffectiveSlowDispatchThreshold;
        NetView = hub?.Net;
        _random = random ?? Random.Shared;
        _faults = new FaultInjector(this);
    }

    /// <summary>Net 维度视图（null = Disabled——节点经枢纽共享）。</summary>
    public ObservabilityHub.NetView? NetView { get; }

    /// <summary>分发慢回调阈值（null = 不检测——直排热路径零计时开销）。</summary>
    public TimeSpan? SlowDispatchThreshold { get; }

    /// <summary>请求回调缺省超时（本枢纽节点共用——SendRequestAsync 无 per-call 选项时）。</summary>
    public TimeSpan RequestTimeout { get; }

    /// <summary>请求回调 pending 关联表容量。</summary>
    public int RequestPendingCapacity { get; }

    /// <summary>应答端 CorrId 去重窗口容量。</summary>
    public int RequestDedupCapacity { get; }

    /// <summary>应答端去重窗口字节软预算。</summary>
    public long RequestDedupMaxBytes { get; }

    /// <summary>流式会话背压窗口帧数。</summary>
    public int StreamWindowFrames { get; }

    /// <summary>
    /// 流式会话打开（内存直路由——目标未注册/不接受 = null）。
    /// </summary>
    internal IWireStream? OpenStream(NodeId from, NodeId to, byte protocolId, ReadOnlyMemory<byte> openPayload)
    {
        return _nodes.TryGetValue(to, out var node) ? node.AcceptStream(from, protocolId, openPayload) : null;
    }

    /// <summary>故障注入器（枢纽级——节点对定向，全节点共享一副矩阵）。</summary>
    public ITransportFaultInjector Faults => _faults;

    /// <summary>已注册节点数（测试诊断）。</summary>
    public int NodeCount => _nodes.Count;

    /// <summary>
    /// 注册节点端点（幂等——重复注册返回同一端点；新节点与既有节点互相触发 PeerConnected）。
    /// </summary>
    /// <param name="id">节点 ID（Empty 哨兵非法）。</param>
    /// <returns>该节点的传输端点（重复注册返回同一端点实例）。</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> 为 Empty 哨兵。</exception>
    public InProcessNode Register(NodeId id)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (id == NodeId.Empty) throw new ArgumentException("节点 ID 不能为 Empty 哨兵。", nameof(id));
        if (_nodes.TryGetValue(id, out var existing)) return existing;

        var created = new InProcessNode(id, this);
        var actual = _nodes.GetOrAdd(id, created);
        if (ReferenceEquals(actual, created))
        {
            foreach (var other in _nodes)
            {
                if (other.Key == id) continue;
                created.RaisePeerConnected(other.Key);
                other.Value.RaisePeerConnected(id);
            }
        }
        return actual;
    }

    /// <summary>注销节点端点（既有节点触发 PeerGone；此后发往该节点 = 静默丢弃）。</summary>
    /// <param name="id">要注销的节点 ID（未注册 = 无操作）。</param>
    public void Unregister(NodeId id)
    {
        if (_nodes.TryRemove(id, out var node))
        {
            node.Close();
            foreach (var other in _nodes)
                other.Value.RaisePeerGone(id);
        }
    }

    // 内部路由（InProcessNode.SendDatagramAsync/SendRequestAsync → 此处——尽力送达语义）

    internal async ValueTask DeliverAsync(NodeId from, NodeId to, byte protocolId, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        await DeliverCoreAsync(from, to, ct, static (node, from, protocolId, payload, _) =>
            node.DispatchDatagram(from, protocolId, payload), protocolId, payload).ConfigureAwait(false);   // 尽力——结果丢弃
    }

    internal async ValueTask DeliverResponseAsync(NodeId from, NodeId to, byte protocolId, ulong corrId, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        await DeliverCoreAsync(from, to, ct, static (node, from, protocolId, payload, corrId) =>
            node.OnResponseArrived(corrId, payload), protocolId, payload, corrId).ConfigureAwait(false);   // 应答迟到/丢失——尽力
    }

    /// <summary>请求投递（返回 false = 目标未注册——调用方快速失败语义；注入丢弃静默 = 对端超时）。</summary>
    internal ValueTask<bool> DeliverRequestAsync(NodeId from, NodeId to, byte protocolId, ulong corrId, ReadOnlyMemory<byte> payload, CancellationToken ct)
        => DeliverCoreAsync(from, to, ct, static (node, from, protocolId, payload, corrId) =>
            node.DispatchRequest(from, protocolId, corrId, payload), protocolId, payload, corrId);

    private delegate void DeliverAction(InProcessNode node, NodeId from, byte protocolId, ReadOnlyMemory<byte> payload, ulong corrId);

    /// <summary>投递核心（数据报/请求/应答三面共路——注入矩阵 + 直排派发）。
    /// 数据报/应答 = 静默丢弃（尽力）；请求 = 未注册目标返回 false（快速失败），注入丢弃静默（对端超时）。</summary>
    private async ValueTask<bool> DeliverCoreAsync(NodeId from, NodeId to, CancellationToken ct, DeliverAction deliver,
        byte protocolId, ReadOnlyMemory<byte> payload, ulong corrId = 0)
    {
        if (Volatile.Read(ref _disposed) != 0) return true;   // 已释放：静默（尽力语义）——数据报面
        if (!_nodes.TryGetValue(to, out var node)) return false;   // 目标未注册（请求面据此快速失败）

        var key = (from, to);
        if (_partitioned.ContainsKey(key)) return true;           // 分区 = 双向断（静默）
        if (_dropRate.TryGetValue(key, out var rate) && rate > 0 && _random.NextDouble() < rate) return true;
        if (_latency.TryGetValue(key, out var latency) && latency > TimeSpan.Zero)
        {
            try { await Task.Delay(latency, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return true; }
        }
        if (_reorder.ContainsKey(key))                       // 乱序：随机抖动破坏到达顺序（0–20ms——对齐 TCP 形态）
        {
            var jitter = TimeSpan.FromMilliseconds(_random.Next(0, 21));
            try { await Task.Delay(jitter, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return true; }
        }
        deliver(node, from, protocolId, payload, corrId);   // 直排派发（发送方上下文同步回调）
        return true;
    }

    /// <inheritdoc/>
    /// <returns>完成时全部节点已关闭并清空。</returns>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        foreach (var node in _nodes.Values) node.Close();
        _nodes.Clear();
        return ValueTask.CompletedTask;
    }

    // ══ 注入器实现（§9.2——TCP 介质等价语义）══

    private sealed class FaultInjector(InProcessTransportHub owner) : ITransportFaultInjector
    {
        /// <summary>注入节点对定向延迟（null/非正 = 清除）。</summary>
        /// <param name="a">端点 A（方向 a→b 生效）。</param>
        /// <param name="b">端点 B。</param>
        /// <param name="latency">单向延迟（非正或 null = 清除该定向延迟）。</param>
        public void SetLatency(NodeId a, NodeId b, TimeSpan? latency)
        {
            var key = (a, b);
            if (latency is null || latency.Value <= TimeSpan.Zero) owner._latency.TryRemove(key, out _);
            else owner._latency[key] = latency.Value;
        }

        /// <summary>注入分区（两组节点间双向断——组内互通不受影响）。</summary>
        /// <param name="groupA">分区组 A。</param>
        /// <param name="groupB">分区组 B（与组 A 之间双向断）。</param>
        public void Partition(IEnumerable<NodeId> groupA, IEnumerable<NodeId> groupB)
        {
            ArgumentNullException.ThrowIfNull(groupA);
            ArgumentNullException.ThrowIfNull(groupB);
            foreach (var a in groupA)
                foreach (var b in groupB)
                {
                    owner._partitioned[(a, b)] = 0;   // 双向断
                    owner._partitioned[(b, a)] = 0;
                }
        }

        /// <summary>注入定向丢包（按概率静默丢弃）。</summary>
        /// <param name="from">发送方节点。</param>
        /// <param name="to">接收方节点。</param>
        /// <param name="rate">丢包率（0..1；0 = 清除该定向丢包）。</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="rate"/> 不在 0..1。</exception>
        public void Drop(NodeId from, NodeId to, double rate)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(rate);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(rate, 1.0);
            var key = (from, to);
            if (rate <= 0) owner._dropRate.TryRemove(key, out _);
            else owner._dropRate[key] = rate;
        }

        /// <summary>注入定向乱序（随机 0–20ms 抖动破坏到达顺序——对齐 TCP 形态）。</summary>
        /// <param name="from">发送方节点。</param>
        /// <param name="to">接收方节点。</param>
        /// <param name="enable">true = 启用乱序注入；false = 清除。</param>
        public void Reorder(NodeId from, NodeId to, bool enable)
        {
            var key = (from, to);
            if (!enable) owner._reorder.TryRemove(key, out _);
            else owner._reorder[key] = 0;
        }

        /// <summary>清空全部注入矩阵（延迟/分区/丢包/乱序归零）。</summary>
        public void Reset()
        {
            owner._latency.Clear();
            owner._dropRate.Clear();
            owner._partitioned.Clear();
            owner._reorder.Clear();
        }
    }
}
