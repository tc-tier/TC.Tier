using System.Collections.Concurrent;
using TC.Tier.Core.Net.Channels;
using TC.Tier.Core.Net.Wire;

namespace TC.Tier.Core.Net.Transport.InProcess;

/// <summary>
/// 进程内节点传输端点（spec-12 §7 InProcess 介质——<see cref="IProtocolTransport"/> 完整面实现，
/// 与 <see cref="Tcp.ClusterTransport"/>（TCP）消费面零差别）：协议域注册/数据报收发/对端观测。
/// <para>★ 派发：发送方上下文直排（handler 同步回调 + 异常隔离——回调异常不外泄到发送方，
///   尽力送达语义与 TCP 读循环同构）。</para>
/// <para>★ 生命周期：注册即活（<see cref="ITransport.Start"/> 为形态面幂等占位）；Dispose = 从枢纽注销。</para>
/// </summary>
public sealed class InProcessNode : IProtocolTransport, ICoreProtocolPort
{
    private readonly InProcessTransportHub _hub;
    private readonly DatagramDispatcher _dispatcher;   // 注册/分发单源（可观测钩子接枢纽 Net 视图）
    private readonly RequestBroker _requests;          // 请求回调单源（容量随枢纽配置）
    private int _closed;

    internal InProcessNode(NodeId self, InProcessTransportHub hub)
    {
        Self = self;
        _hub = hub;
        _requests = new RequestBroker(hub.RequestPendingCapacity, hub.RequestDedupCapacity, hub.RequestDedupMaxBytes);
        _dispatcher = new DatagramDispatcher(hub.SlowDispatchThreshold, OnUnknownProtocolDropped, OnSlowDispatch);
    }

    private void OnUnknownProtocolDropped(byte protocolId)
        => _hub.NetView?.OnDatagramDropped(protocolId, "unknown_protocol");

    private void OnSlowDispatch(byte protocolId)
        => _hub.NetView?.OnSlowDispatch(protocolId);

    /// <summary>本端节点 ID。</summary>
    public NodeId Self { get; }

    /// <summary>对端接入（同枢纽节点注册——经 <see cref="InProcessTransportHub.Register"/> 触发）。</summary>
    public event Action<NodeId>? PeerConnected;

    /// <summary>对端离线（同枢纽节点注销）。</summary>
    public event Action<NodeId>? PeerGone;

    /// <summary>故障注入（枢纽级矩阵——节点对定向）。</summary>
    public ITransportFaultInjector Faults => _hub.Faults;

    /// <summary>启动（幂等占位——注册即活，形态面同构；无监听/拨号概念）。</summary>
    public void Start() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);

    /// <summary>
    /// 协议域注册·公开口（§3.5 分流——注册区 0x60-0xAF；与 TCP 同一
    /// <see cref="DatagramDispatcher"/> 单源）。
    /// </summary>
    /// <param name="protocolId">协议域 ID（注册区 0x60-0xAF）。</param>
    /// <param name="handler">入站数据报处理器。</param>
    /// <param name="bearer">承载声明（内存介质恒承载——UDP 回落语义在进程内退化为直派，尽力语义不变）。</param>
    public void RegisterProtocol(byte protocolId, IDatagramHandler handler, DatagramBearer bearer = DatagramBearer.Tcp)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        _dispatcher.RegisterUser(protocolId, handler, bearer);
    }

    /// <summary>协议域注册·内部口（机制面专用——只放行内部核心区 0x00-0x4F）。</summary>
    /// <param name="protocolId">协议域 ID（内部核心区）。</param>
    /// <param name="handler">入站数据报处理器。</param>
    /// <param name="bearer">承载声明（内存介质恒承载——UDP 回落语义在进程内退化为直派，尽力语义不变）。</param>
    internal void RegisterCoreProtocol(byte protocolId, IDatagramHandler handler, DatagramBearer bearer = DatagramBearer.Tcp)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        _dispatcher.RegisterCore(protocolId, handler);
    }

    void ICoreProtocolPort.RegisterCoreProtocol(byte protocolId, IDatagramHandler handler, DatagramBearer bearer)
        => RegisterCoreProtocol(protocolId, handler, bearer);

    void ICoreProtocolPort.RegisterCoreRequestHandler(byte protocolId, IRequestHandler handler)
        => RegisterCoreRequestHandler(protocolId, handler);

    void ICoreProtocolPort.RegisterCoreStreamAcceptor(byte protocolId, IStreamAcceptor acceptor)
        => RegisterCoreStreamAcceptor(protocolId, acceptor);

    /// <summary>
    /// 数据报发送（尽力送达——目标未注册/注入丢弃 = 静默丢弃；载荷上限 = 帧协议常量同构
    /// （大块走流式通道——契约与介质无关））。
    /// </summary>
    /// <param name="target">目标节点。</param>
    /// <param name="protocolId">协议域 ID。</param>
    /// <param name="payload">载荷。</param>
    /// <param name="ct">取消令牌（注入延迟等待可取消）。</param>
    /// <returns>完成时数据报已交付枢纽（尽力送达——目标未注册/注入丢弃 = 静默丢弃，不抛）。</returns>
    public ValueTask SendDatagramAsync(NodeId target, byte protocolId, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        if (payload.Length > FrameCodec.MaxPayloadLength)
            throw new ArgumentOutOfRangeException(nameof(payload),
                $"载荷 {payload.Length} 超上限 {FrameCodec.MaxPayloadLength}（大块走流式通道——spec-12 §3.1）。");
        return _hub.DeliverAsync(Self, target, protocolId, payload, ct);
    }

    /// <summary>入站数据报分发（Channels/ 单源组件——异常隔离/未注册协议静默丢弃）。</summary>
    internal void DispatchDatagram(NodeId from, byte protocolId, ReadOnlyMemory<byte> payload) => _dispatcher.Dispatch(from, protocolId, payload);

    // ══ 请求回调（spec-12 §5.2——无帧直投，CorrId/超时/关联表语义与 TCP 同构）══

    /// <summary>
    /// 请求回调 handler 注册·公开口（注册区 0x60-0xAF——与 TCP 同一 <see cref="RequestBroker"/> 单源）。
    /// </summary>
    /// <param name="protocolId">协议域 ID（注册区）。</param>
    /// <param name="handler">入站请求处理器。</param>
    public void RegisterRequestHandler(byte protocolId, IRequestHandler handler)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        _requests.RegisterUserHandler(protocolId, handler);
    }

    /// <summary>请求回调 handler 注册·内部口（机制面专用——核心区）。</summary>
    /// <param name="protocolId">协议域 ID（内部核心区）。</param>
    /// <param name="handler">入站请求处理器。</param>
    internal void RegisterCoreRequestHandler(byte protocolId, IRequestHandler handler)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        _requests.RegisterCoreHandler(protocolId, handler);
    }

    /// <summary>
    /// 请求回调发送（§5.2 at-most-once 缺省）：目标未注册 = 抛 <see cref="NetIOException"/>；
    /// 注入丢弃/无应答 = 超时抛 <see cref="TimeoutException"/>；取消传播。
    /// </summary>
    /// <param name="target">目标节点。</param>
    /// <param name="protocolId">协议域 ID。</param>
    /// <param name="payload">请求载荷。</param>
    /// <param name="options">per-call 选项（null = 缺省超时——同 TCP 缺省量级）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>应答载荷。</returns>
    public ValueTask<byte[]> SendRequestAsync(NodeId target, byte protocolId, ReadOnlyMemory<byte> payload,
        RequestOptions? options = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        if (payload.Length > FrameCodec.MaxPayloadLength - RequestCodec.PrefixSize)
            throw new ArgumentOutOfRangeException(nameof(payload),
                $"请求载荷 {payload.Length} 超上限（帧上限扣除 CorrId 前缀——契约与介质无关）。");

        // ★ 直通 broker 等待态（池化源背书——本层零箱零 Task；校验在调用点同步抛）
        return _requests.SendAsync(
            async (corrId, ct) =>
            {
                if (!await _hub.DeliverRequestAsync(Self, target, protocolId, corrId, payload, ct).ConfigureAwait(false))
                    throw new NetIOException($"目标未注册：{target}（请求回调不静默——调用方有应答期待）。");
            },
            options?.Timeout ?? _hub.RequestTimeout, options?.Retry, ct);
    }

    /// <summary>入站请求分发（hub 直投——回程上下文=同枢纽定向投递）。</summary>
    internal void DispatchRequest(NodeId from, byte protocolId, ulong corrId, ReadOnlyMemory<byte> payload)
        => _requests.DispatchRequest(from, protocolId, corrId, payload, new HubReplyContext(this, from, protocolId, corrId));

    /// <summary>应答到达（hub 直投——完成 pending）。</summary>
    internal void OnResponseArrived(ulong corrId, ReadOnlyMemory<byte> payload) => _requests.OnResponse(corrId, payload);

    // ══ 流式会话（spec-12 §5.3——内存 Channel 对直连，语义与 TCP 会话同构）══

    private readonly ConcurrentDictionary<byte, IStreamAcceptor> _acceptors = new();

    /// <summary>
    /// 流接受面注册·公开口（注册区 0x60-0xAF——与 TCP 同一分流规则）。
    /// </summary>
    /// <param name="protocolId">协议域 ID（注册区）。</param>
    /// <param name="acceptor">入站会话接受面。</param>
    public void RegisterStreamAcceptor(byte protocolId, IStreamAcceptor acceptor)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        ProtocolRegistration.ValidateUserPort(protocolId);
        ProtocolRegistration.Add(_acceptors, protocolId, acceptor);
    }

    /// <summary>流接受面注册·内部口（机制面专用——核心区）。</summary>
    /// <param name="protocolId">协议域 ID（内部核心区）。</param>
    /// <param name="acceptor">入站会话接受面。</param>
    internal void RegisterCoreStreamAcceptor(byte protocolId, IStreamAcceptor acceptor)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        ProtocolRegistration.ValidateCorePort(protocolId);
        ProtocolRegistration.Add(_acceptors, protocolId, acceptor);
    }

    /// <summary>
    /// 打开流式会话（hub 直路由；对端未注册/不接受 = 抛 <see cref="NetIOException"/>）。
    /// </summary>
    /// <param name="target">目标节点。</param>
    /// <param name="protocolId">协议域 ID。</param>
    /// <param name="ct">取消令牌（未用——内存直路由零等待；形态面签名同构）。</param>
    /// <param name="openPayload">开流载荷（随会话建立透传给对端 acceptor；组路由场景 = host 剥前缀后的内层载荷——二期-C1）。</param>
    /// <returns>会话。</returns>
    public ValueTask<IWireStream> OpenStreamAsync(NodeId target, byte protocolId, ReadOnlyMemory<byte> openPayload = default, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        var stream = _hub.OpenStream(Self, target, protocolId, openPayload)
            ?? throw new NetIOException($"对端不接受流：{target}（协议域 0x{protocolId:X2} 未注册 acceptor 或节点未注册）。");
        return ValueTask.FromResult<IWireStream>(stream);
    }

    /// <summary>入站开流（hub 直路由——构造会话对+acceptor 回调；null = 不接受）。</summary>
    internal IWireStream? AcceptStream(NodeId from, byte protocolId, ReadOnlyMemory<byte> openPayload)
    {
        if (!_acceptors.TryGetValue(protocolId, out var acceptor)) return null;
        var (initiatorStream, acceptorStream) = InProcessWireStream.CreatePair(protocolId, from, Self, _hub.StreamWindowFrames, openPayload);
        try
        {
            acceptor.OnStream(from, acceptorStream, openPayload);
        }
        catch (Exception)
        {
            // acceptor（使用方代码）异常不外泄、不致命——会话清理由使用方 Dispose 规则兜底
        }
        return initiatorStream;
    }

    /// <summary>请求回调应答上下文（回程 = 同枢纽定向投递 Response）。</summary>
    private sealed class HubReplyContext(InProcessNode node, NodeId peer, byte protocolId, ulong corrId) : IReplyContext
    {
        public NodeId Peer => peer;
        public byte ProtocolId => protocolId;
        public ulong CorrelationId => corrId;

        /// <summary>回发请求应答（经枢纽定向投递 Response——完成发起端 pending 关联）。</summary>
        /// <param name="payload">应答载荷（空应答合法）。</param>
        /// <param name="ct">取消令牌（注入延迟等待可取消）。</param>
        /// <returns>完成时应答已投递枢纽。</returns>
        public ValueTask ReplyAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default)
            => node._hub.DeliverResponseAsync(node.Self, peer, protocolId, corrId, payload, ct);
    }

    internal void Close() => Interlocked.Exchange(ref _closed, 1);

    internal void RaisePeerConnected(NodeId other) => PeerConnected?.Invoke(other);

    internal void RaisePeerGone(NodeId other) => PeerGone?.Invoke(other);

    /// <summary>端点释放（从枢纽注销——既有节点触发 PeerGone）。</summary>
    /// <returns>完成时本节点已从枢纽注销。</returns>
    public ValueTask DisposeAsync()
    {
        _hub.Unregister(Self);
        return ValueTask.CompletedTask;
    }
}
