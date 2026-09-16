using TC.Tier.Core.Net.Channels;
using TC.Tier.Core.Net.Ports;
using TC.Tier.Core.Net.Transport;

namespace TC.Tier.Core.Net.Hosting;

/// <summary>
/// 节点端点（spec-12 §11 装配产物——builder StartAsync 的返回值）：
/// 传输完整面委托 + 机制生命周期归位（装配方持机制、随端点释放）+ 地址制直连的
/// 对端身份（<see cref="RemoteId"/>——握手后才得知，§4.1）。
/// <para>★ 完整面含内部挂载口（<see cref="ICoreProtocolPort"/> 显式转发——raft/机制面
/// 经端点挂载与直连传输同路径）；内层未实现 = 快速失败（机制宿主归 Core.Net）。</para>
/// <para>★ 生命周期：DisposeAsync 先机制（逆挂载序）后传输——单一收口。</para>
/// </summary>
public sealed class NodeEndpoint : IProtocolTransport, ICoreProtocolPort
{
    private readonly IProtocolTransport _inner;
    private readonly INodeMechanism[] _mechanisms;
    private int _disposed;

    /// <summary>构造（装配内建——builder 组装；机制挂载针对内层传输）。</summary>
    internal NodeEndpoint(IProtocolTransport inner, INodeMechanism[] mechanisms, NodeId? remoteId = null,
        TransportOptions? options = null)
    {
        _inner = inner;
        _mechanisms = mechanisms;
        RemoteId = remoteId;
        Options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>地址制直连的对端身份（客户端预设 Connect 后得知；成员制/纯监听 = null）。</summary>
    public NodeId? RemoteId { get; }

    /// <summary>生效传输配置（装配自证——显式方法/注入/管道裁决后的最终值，§4.5 诊断面）。</summary>
    public TransportOptions Options { get; }

    /// <summary>挂载的机制表（诊断/测试观测面）。</summary>
    public IReadOnlyList<INodeMechanism> Mechanisms => _mechanisms;

    /// <inheritdoc/>
    public NodeId Self => _inner.Self;

    /// <inheritdoc/>
    public void Start() => _inner.Start();

    /// <inheritdoc/>
    public event Action<NodeId>? PeerConnected
    {
        add => _inner.PeerConnected += value;
        remove => _inner.PeerConnected -= value;
    }

    /// <inheritdoc/>
    public event Action<NodeId>? PeerGone
    {
        add => _inner.PeerGone += value;
        remove => _inner.PeerGone -= value;
    }

    /// <inheritdoc/>
    public ITransportFaultInjector Faults => _inner.Faults;

    /// <inheritdoc/>
    /// <param name="protocolId">协议域 ID（注册区 0x60-0xAF）。</param>
    /// <param name="handler">入站数据报处理器。</param>
    /// <param name="bearer">发送承载声明（默认 TCP 帧流）。</param>
    public void RegisterProtocol(byte protocolId, IDatagramHandler handler, DatagramBearer bearer = DatagramBearer.Tcp)
        => _inner.RegisterProtocol(protocolId, handler, bearer);

    /// <inheritdoc/>
    /// <param name="target">目标节点。</param>
    /// <param name="protocolId">协议域 ID。</param>
    /// <param name="payload">载荷（≤帧协议上限——大块走流式通道）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>完成时数据报已交付底层传输（尽力送达——对端未连/注入丢弃 = 静默丢弃，不抛）。</returns>
    public ValueTask SendDatagramAsync(NodeId target, byte protocolId, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
        => _inner.SendDatagramAsync(target, protocolId, payload, ct);

    /// <inheritdoc/>
    /// <param name="protocolId">协议域 ID（注册区 0x60-0xAF）。</param>
    /// <param name="handler">入站请求处理器。</param>
    public void RegisterRequestHandler(byte protocolId, IRequestHandler handler)
        => _inner.RegisterRequestHandler(protocolId, handler);

    /// <inheritdoc/>
    /// <param name="target">目标节点。</param>
    /// <param name="protocolId">协议域 ID。</param>
    /// <param name="payload">请求载荷。</param>
    /// <param name="options">per-call 选项（null = 传输缺省）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>完成后的应答载荷（目标未连抛 <see cref="NetIOException"/>；超时/取消按选项）。</returns>
    public ValueTask<byte[]> SendRequestAsync(NodeId target, byte protocolId, ReadOnlyMemory<byte> payload,
        RequestOptions? options = null, CancellationToken ct = default)
        => _inner.SendRequestAsync(target, protocolId, payload, options, ct);

    /// <summary>域级背压策略声明（二期-D5——转发内层传输；内层非 TCP 介质 = 不支持抛）。</summary>
    /// <param name="protocolId">协议域 ID。</param>
    /// <param name="policy">背压策略（队列等待/优先级预留参数归传输配置）。</param>
    /// <exception cref="NotSupportedException">内层非 TCP 介质（背压声明仅 TCP 支持）。</exception>
    public void SetBackpressurePolicy(byte protocolId, BackpressurePolicy policy)
    {
        if (_inner is Transport.Tcp.ClusterTransport tcp) tcp.SetBackpressurePolicy(protocolId, policy);
        else throw new NotSupportedException($"背压策略声明仅 TCP 介质支持（当前 {_inner.GetType().Name}）。");
    }

    /// <inheritdoc/>
    /// <param name="protocolId">协议域 ID（注册区）。</param>
    /// <param name="acceptor">入站会话接受面。</param>
    public void RegisterStreamAcceptor(byte protocolId, IStreamAcceptor acceptor)
        => _inner.RegisterStreamAcceptor(protocolId, acceptor);

    /// <inheritdoc/>
    /// <param name="target">目标节点。</param>
    /// <param name="protocolId">协议域 ID。</param>
    /// <param name="openPayload">开流载荷（不透明字节——协议域自解释；空 = 无载荷）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>完成后的会话（目标未连/对端不接受抛 <see cref="NetIOException"/>；等待超时抛 <see cref="TimeoutException"/>）。</returns>
    public ValueTask<IWireStream> OpenStreamAsync(NodeId target, byte protocolId, ReadOnlyMemory<byte> openPayload = default, CancellationToken ct = default)
        => _inner.OpenStreamAsync(target, protocolId, openPayload, ct);

    // ── 内部挂载口转发（完整面含机制宿主路径——内层未实现 = 快速失败）──

    /// <inheritdoc/>
    void ICoreProtocolPort.RegisterCoreProtocol(byte protocolId, IDatagramHandler handler, DatagramBearer bearer)
        => CorePort().RegisterCoreProtocol(protocolId, handler, bearer);

    /// <inheritdoc/>
    void ICoreProtocolPort.RegisterCoreRequestHandler(byte protocolId, IRequestHandler handler)
        => CorePort().RegisterCoreRequestHandler(protocolId, handler);

    /// <inheritdoc/>
    void ICoreProtocolPort.RegisterCoreStreamAcceptor(byte protocolId, IStreamAcceptor acceptor)
        => CorePort().RegisterCoreStreamAcceptor(protocolId, acceptor);

    private ICoreProtocolPort CorePort()
        => _inner as ICoreProtocolPort
           ?? throw new InvalidOperationException(
               $"机制挂载须内部注册口（ICoreProtocolPort）——介质 {_inner.GetType().Name} 未实现；机制宿主归 Core.Net（spec-12 §3.5）。");

    /// <summary>释放（逆挂载序机制 → 传输；幂等）。</summary>
    /// <returns>完成时机制与传输均已释放（幂等——重复调用立即完成）。</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var mechanism in _mechanisms.Reverse())
        {
            try { await mechanism.DisposeAsync().ConfigureAwait(false); } catch { /* 尽力清理 */ }
        }
        await _inner.DisposeAsync().ConfigureAwait(false);
    }
}
