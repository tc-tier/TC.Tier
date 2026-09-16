using TC.Tier.Core.Net.Channels;
using TC.Tier.Core.Net.Transport;
using TC.Tier.Core.Net.Wire;

namespace TC.Tier.Core.Net.Raft;

/// <summary>
/// 组作用域传输视图（二期-C1 §4.3——每组一枚，引擎经其挂载三域而**零组感知**）：
/// <list type="bullet">
/// <item>出站加前缀：请求回调（0x01/0x03）物理发送 <c>[GroupId][inner]</c>，返回剥前缀并校验本组
///   （不符 = 畸形应答——跨组串包防御）；流式（0x04）open payload = <c>[GroupId][调用方载荷]</c>。</item>
/// <item>入站面：核心注册口写入宿主组绑定表（三域各自至多一个——重复抛）；公开注册口/数据报面
///   一律抛（Raft 家族零数据报——已验证；产品协议域注册在物理传输上）。</item>
/// <item>Self/PeerConnected/PeerGone/Faults 委托共享传输；Start/DisposeAsync no-op（生命周期归
///   装配层/宿主——§2.3 不变量④）。</item>
/// </list>
/// </summary>
public sealed class RaftGroupChannel : IProtocolTransport, ICoreProtocolPort
{
    private readonly RaftGroupHost _host;
    private readonly RaftGroupHost.GroupBinding _binding;

    internal RaftGroupChannel(RaftGroupHost host, RaftGroupHost.GroupBinding binding)
    {
        _host = host;
        _binding = binding;
    }

    /// <summary>组 ID。</summary>
    public RaftGroupId GroupId => _binding.Id;

    /// <summary>宿主挂载的物理传输（装配面透传——产品域注册仍走物理传输）。</summary>
    public IProtocolTransport Transport => _host.Transport;

    // ═══ ITransport（委托共享传输；生命周期 no-op）═══

    /// <inheritdoc/>
    public NodeId Self => _host.Transport.Self;

    /// <inheritdoc/>
    public event Action<NodeId>? PeerConnected
    {
        add => _host.Transport.PeerConnected += value;
        remove => _host.Transport.PeerConnected -= value;
    }

    /// <inheritdoc/>
    public event Action<NodeId>? PeerGone
    {
        add => _host.Transport.PeerGone += value;
        remove => _host.Transport.PeerGone -= value;
    }

    /// <inheritdoc/>
    public ITransportFaultInjector Faults => _host.Transport.Faults;

    /// <inheritdoc/>
    public void Start()
    {
        // no-op——生命周期归装配层/宿主（§4.3：宿主 StartAsync 承担物理注册）
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        // no-op——组通道不拥有传输生命周期；组注销走宿主 RemoveGroup
        return ValueTask.CompletedTask;
    }

    // ═══ IProtocol——公开面（Raft 家族只走核心口；产品域走物理传输）═══

    /// <inheritdoc/>
    public void RegisterProtocol(byte protocolId, IDatagramHandler handler, DatagramBearer bearer = DatagramBearer.Tcp)
        => throw new NotSupportedException("组通道只承载 Raft 家族核心域——产品协议域注册在物理传输（Transport 属性）上。");

    /// <inheritdoc/>
    public ValueTask SendDatagramAsync(NodeId target, byte protocolId, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
        => throw new NotSupportedException("Raft 家族零数据报（§4.3 已验证）——组通道不支持数据报面。");

    /// <inheritdoc/>
    public void RegisterRequestHandler(byte protocolId, IRequestHandler handler)
        => throw new NotSupportedException("组通道只承载 Raft 家族核心域——产品请求域注册在物理传输（Transport 属性）上。");

    /// <inheritdoc/>
    public void RegisterStreamAcceptor(byte protocolId, IStreamAcceptor acceptor)
        => throw new NotSupportedException("组通道只承载 Raft 家族核心域——产品流域注册在物理传输（Transport 属性）上。");

    /// <inheritdoc/>
    public ValueTask<byte[]> SendRequestAsync(NodeId target, byte protocolId, ReadOnlyMemory<byte> payload,
        RequestOptions? options = null, CancellationToken ct = default)
    {
        if (protocolId is not (ProtocolIds.Raft or ProtocolIds.SwarmSync))
            throw new NotSupportedException($"组通道只承载 Raft 家族核心域（0x01/0x03）——收到 0x{protocolId:X2}。");
        return SendWithPrefixAsync(target, protocolId, payload, options, ct);
    }

    /// <inheritdoc/>
    public ValueTask<IWireStream> OpenStreamAsync(NodeId target, byte protocolId, ReadOnlyMemory<byte> openPayload = default, CancellationToken ct = default)
    {
        if (protocolId != ProtocolIds.SnapshotStream)
            throw new NotSupportedException($"组通道流式只承载快照域（0x04）——收到 0x{protocolId:X2}。");
        return _host.Transport.OpenStreamAsync(target, protocolId, WithPrefix(openPayload), ct);
    }

    // ═══ ICoreProtocolPort（核心口——写入宿主组绑定表）═══

    /// <inheritdoc/>
    public void RegisterCoreProtocol(byte protocolId, IDatagramHandler handler, DatagramBearer bearer = DatagramBearer.Tcp)
        => throw new NotSupportedException("Raft 家族零数据报——组通道不支持核心数据报注册。");

    /// <inheritdoc/>
    public void RegisterCoreRequestHandler(byte protocolId, IRequestHandler handler)
    {
        if (protocolId is not (ProtocolIds.Raft or ProtocolIds.SwarmSync))
            throw new ArgumentOutOfRangeException(nameof(protocolId), protocolId,
                $"组绑定只放行 Raft 家族请求域（0x01/0x03）——收到 0x{protocolId:X2}。");
        _host.BindHandler(_binding, protocolId, handler);
    }

    /// <inheritdoc/>
    public void RegisterCoreStreamAcceptor(byte protocolId, IStreamAcceptor acceptor)
    {
        if (protocolId != ProtocolIds.SnapshotStream)
            throw new ArgumentOutOfRangeException(nameof(protocolId), protocolId,
                $"组绑定流式只放行快照域（0x04）——收到 0x{protocolId:X2}。");
        _host.BindAcceptor(_binding, acceptor);
    }

    // ═══ 出站前缀（加/剥）═══

    private async ValueTask<byte[]> SendWithPrefixAsync(NodeId target, byte protocolId, ReadOnlyMemory<byte> payload,
        RequestOptions? options, CancellationToken ct)
    {
        var framed = WithPrefix(payload);
        var resp = await _host.Transport.SendRequestAsync(target, protocolId, framed, options, ct).ConfigureAwait(false);
        // 返回剥前缀 + 本组校验（跨组串包防御）——畸形应答沿引擎 null/异常契约上抛
        if (resp.Length < RaftGroupHost.GroupPrefixBytes)
            throw new NetIOException($"组应答畸形（< 8B 前缀）：protocol=0x{protocolId:X2} from={target}。");
        var respGroupId = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(resp.AsSpan(0, RaftGroupHost.GroupPrefixBytes));
        if (respGroupId != GroupId.Value)
            throw new NetIOException($"组应答组 ID 不符：期望 {GroupId}，收到 {new RaftGroupId(respGroupId)}（跨组串包防御）。");
        return resp[RaftGroupHost.GroupPrefixBytes..];
    }

    private byte[] WithPrefix(ReadOnlyMemory<byte> payload)
    {
        var framed = new byte[RaftGroupHost.GroupPrefixBytes + payload.Length];
        RaftGroupHost.WritePrefix(framed, GroupId);
        payload.Span.CopyTo(framed.AsSpan(RaftGroupHost.GroupPrefixBytes));
        return framed;
    }
}
