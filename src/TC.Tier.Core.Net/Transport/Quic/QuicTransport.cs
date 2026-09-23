using System.Collections.Concurrent;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.Versioning;
using TC.Tier.Core.Execution;
using TC.Tier.Core.Logging;
using TC.Tier.Core.Net;
using TC.Tier.Core.Net.Channels;
using TC.Tier.Core.Net.Security;
using TC.Tier.Core.Net.Wire;
using TC.Tier.Core.Net.Transport;

using System.Runtime.InteropServices;
using TC.Tier.CodeGen;
namespace TC.Tier.Core.Net.Transport.Quic;

/// <summary>QUIC 握手头（21B：[Kind 1B][Self 16B][ClusterTag 4B]——[BinaryLayout] 声明式生成）。</summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 21)]
internal struct QuicHandshakeHead
{
    /// <summary>握手 kind。</summary>
    [FieldOffset(0)] public byte Kind;

    /// <summary>本端身份。</summary>
    [FieldOffset(1)] public NodeId Self;

    /// <summary>集群标签（错集群 fail-fast）。</summary>
    [FieldOffset(17)] public uint ClusterTag;
}


/// <summary>
/// QUIC 介质传输（spec-11 W5 / spec-12 W-QUIC 波——System.Net.Quic 直映射，消费面与
/// InProcess/TCP/UDP 零差别）。运行依赖 MsQuic（<see cref="QuicCertificates.IsSupported"/>——
/// Windows 11/Server 2022+ 内置，Linux 须 libmsquic）；TLS1.3 强制 = 加密+完整性免费。
/// <para>★ 三形态映射（.NET 8 System.Net.Quic 仅 stream 面——无 datagram API，映射语义差异见下）：</para>
/// <list type="bullet">
/// <item>请求回调 = 每请求一条<b>双向短命 stream</b>：stream 即关联（无需 CorrId 关联表）；
///   写载荷 → half-close → 读 [4B len] 应答 → 关。</item>
/// <item>流式会话 = 双向长命 stream（<see cref="QuicStream"/> 流控 = 背压天然传导）。</item>
/// <item>数据报 = <b>单向短命 stream</b>（send-and-forget）。★语义差异（明示非静默）：
///   .NET 8 无 QUIC datagram（RFC 9221）API——spec-12 §5"datagram 帧"映射不可行，以单流承载；
///   丢弃面 = 连接死/流失败（流内可靠有序、流控满时发送 await——非 UDP 级无序尽力）。</item>
/// </list>
/// <para>★ 应用层流头（每条 stream 首字节起）：<c>[1B kind][1B protocolId][载荷]</c>——
/// kind 0x00 请求 / 0x01 流式 / 0x02 数据报 / 0xFE 握手。</para>
/// <para>★ 身份与信任：对称握手 stream（0xFE）交换 <c>[NodeId 16B][ClusterTag 4B]</c>——
/// 错集群 fail-fast、拨号方校验对端身份；证书 = 运行时自签（内网模式，证书链/SAN nid
/// 防伪造随 mTLS 体系后置集成）。连接 = 节点对单连接（惰性拨号 + 断连清理，无后台重连循环——
/// 发送时按需重建）。</para>
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public sealed class QuicTransport : IProtocolTransport, ICoreProtocolPort, IPeerRegistry
{
    private const byte KindRequest = 0x00;
    private const byte KindStream = 0x01;
    private const byte KindDatagram = 0x02;
    private const byte KindHandshake = 0xFE;
    private const byte FlagCorrelation = 0x01;   // 流头 flags bit0：请求携带 CorrId（at-least-once 重发/去重面）
    private const byte FlagNone = 0x00;          // 无旗标位（数据报/流/请求不带 CorrId——无相关性的尽力面）
    private const byte AckAccept = 0x01;         // 开流确认字节（accept 分派成功由传输回写，帧数据之外）
    private const int AbortCodeRefused = 0x0C;   // 拒绝开流（未注册协议域）

    /// <summary>单连接入站流容量（请求回调并发 + 数据报流——.NET 8 缺省 0 = 全拒，必须显式）。</summary>
    private const int InboundStreamCapacity = 4096;

    private readonly X509Certificate2 _certificate;
    private readonly X509Certificate2Collection? _caBundle;   // 二期-E6：mTLS CA 信任集
    private readonly bool _mutualTls;                          // 二期-E6：mTLS 模式
    private readonly ConcurrentDictionary<byte, IDatagramHandler> _datagramHandlers = new();
    private readonly ConcurrentDictionary<byte, IRequestHandler> _requestHandlers = new();
    private readonly ConcurrentDictionary<byte, IStreamAcceptor> _streamAcceptors = new();
    private readonly ConcurrentDictionary<NodeId, Connection> _connections = new();
    // ★ 二期-C2 §5.3：动态对端端点表（惰性拨号源）+ 入站黑名单（运行时易失——裁定②）
    private readonly ConcurrentDictionary<NodeId, IPEndPoint> _peerEndpoints = new();
    private readonly ConcurrentDictionary<NodeId, byte> _inboundDeny = new();
    private readonly TaskSink _closeSink;   // RemovePeer 连接资源受控异步收尾（fire-and-forget 纪律）
    private readonly ConcurrentDictionary<NodeId, byte> _connectAttempts = new();   // 拨号单飞（per-peer 锁）
    private readonly DedupWindow _dedup = new(1024);   // at-least-once 应答缓存（CorrId → 应答/null=进行中）
    private readonly FaultsImpl _faultsImpl;
    private readonly TaskSink _loops;
    private QuicListener? _listener;
    private IPEndPoint? _localEndPoint;
    private readonly CancellationTokenSource _cts = new();   // 传输级取消（长循环退出信号——Dispose 触发）
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Task, byte> _loopThreads = new();   // 长循环专用线程跟踪（Dispose 有界等退）
    private int _started;
    private int _disposed;

    /// <summary>构造（零 IO——监听/拨号经 <see cref="Start"/>；MsQuic 不可用 = 构造即抛 fail-fast）。</summary>
    /// <param name="self">本端节点 ID。</param>
    /// <param name="options">传输参数（复用 TransportOptions——ListenEndPoint/Peers/ClusterTag/超时面同 TCP）。</param>
    /// <param name="logger">日志（可选）。</param>
    /// <param name="caBundle">CA 信任集（mTLS 链校验用——null = 系统信任库；默认 null）。</param>
    /// <param name="mutualTls">是否启用 mTLS（true = 要求对端证书 + CA/SAN nid 校验——二期-E6；默认 false）。</param>
    public QuicTransport(NodeId self, TransportOptions options, ILogger? logger = null,
        X509Certificate2Collection? caBundle = null, bool mutualTls = false)
    {
        if (self == NodeId.Empty) throw new ArgumentException("本端节点 ID 不能为 Empty 哨兵。", nameof(self));
        ArgumentNullException.ThrowIfNull(options);
        if (!QuicListener.IsSupported)
            throw new PlatformNotSupportedException(
                "MsQuic 运行时不可用（QuicListener.IsSupported = false）——Windows 11/Server 2022+ 内置，Linux 须安装 libmsquic。");
        Self = self;
        Options = options;
        Logger = logger;
        _certificate = QuicCertificates.CreateSelfSigned(self);
        _caBundle = caBundle;
        _mutualTls = mutualTls;   // ★ 二期-E6：mTLS——要求对端证书 + CA/SAN 校验
        _faultsImpl = new FaultsImpl(this);
        _closeSink = new TaskSink($"quic-close-{self}", logger: logger);
        _loops = new TaskSink(name: $"quic-{self}", logger: logger);
    }

    /// <summary>本端节点 ID。</summary>
    public NodeId Self { get; }

    /// <summary>传输参数。</summary>
    public TransportOptions Options { get; }

    /// <summary>日志（测试观测）。</summary>
    private ILogger? Logger { get; }

    /// <summary>QUIC 监听端点（实际绑定端口——0 端口装配后回读；未监听 = null）。</summary>
    public IPEndPoint? LocalEndPoint => _localEndPoint;

    /// <summary>对端可达（应用层握手完成——身份交换+ClusterTag 校验通过）。</summary>
    public event Action<NodeId>? PeerConnected;

    /// <summary>对端离线（连接关闭/异常——事件链与 TCP 同型）。</summary>
    public event Action<NodeId>? PeerGone;

    /// <summary>故障注入（常设面——延迟/分区/丢包；乱序对 QUIC 不适用=有意 no-op）。</summary>
    public ITransportFaultInjector Faults => _faultsImpl;

    /// <summary>
    /// 长稳定循环起线（专用 LongRunning 线程 + AsyncPump 泵域——2026-09-03 活性判例：池续体丢失
    /// = accept/接收循环停转 = 连接僵死/聋死）。循环体契约：域内 await 不写
    /// <c>ConfigureAwait(false)</c>——续体回流泵线程。线程句柄入 <see cref="_loopThreads"/>，
    /// <see cref="DisposeAsync"/> 有界等退。
    /// </summary>
    private void StartLoopThread(string name, Func<CancellationToken, Task> loop)
    {
        var pump = new AsyncPump(name, Logger);
        var ct = _cts.Token;
        var thread = Task.Factory.StartNew(
            () =>
            {
                try { pump.Run(() => loop(ct), ct); }
                catch (OperationCanceledException) { }   // Dispose 取消——常态退出
                catch (Exception ex) { Logger?.LogError(ex, "QUIC 循环泵外逃逸：{Loop}", name); }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
        _loopThreads[thread] = 0;
        thread.ContinueWith(static (t, s) => ((System.Collections.Concurrent.ConcurrentDictionary<Task, byte>)s!).TryRemove(t, out _),
            _loopThreads, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>启动：监听（配置非空时）+ 证书就绪。幂等。</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        // ★ 二期-C2：静态地址表 → 动态端点表（惰性拨号源）
        foreach (var (peerId, peerEndpoint) in Options.Peers)
            _peerEndpoints[peerId] = peerEndpoint;
        if (Options.ListenEndPoint is { } listen)
        {
            // .NET 8 QuicListener 不回读绑定端点——0 端口装配先预探测实际端口（测试装配读回用）
            if (listen.Port == 0)
            {
                using var probe = new System.Net.Sockets.UdpClient(listen);
                listen = (IPEndPoint)probe.Client.LocalEndPoint!;
            }
            _localEndPoint = listen;
            // ★ 装配同步点（Start() 契约——LocalEndPoint 须在返回前可回读；TCP TcpListener.Listen 同款语义，
            //   调用方为装配线程，无同步上下文死锁面）
#pragma warning disable TCSG137 // 设计必需：装配期同步监听建立——Start() 同步契约 + LocalEndPoint 回读依赖
            _listener = QuicListener.ListenAsync(new QuicListenerOptions
            {
                ListenEndPoint = listen,
                ApplicationProtocols = [new SslApplicationProtocol(QuicCertificates.AlpnProtocol)],
                ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
                {
                    DefaultStreamErrorCode = 0x0A,
                    DefaultCloseErrorCode = 0x0B,
                    // ★ .NET 8 缺省 0 = 拒绝对端开任何流（"not accept any streams"）——必须显式放开
                    MaxInboundBidirectionalStreams = InboundStreamCapacity,
                    MaxInboundUnidirectionalStreams = InboundStreamCapacity,
                    ServerAuthenticationOptions = _mutualTls
                        ? QuicCertificates.ServerAuthenticationMtls(_certificate!, _caBundle)
                        : QuicCertificates.ServerAuthentication(_certificate!),
                }),
            }).AsTask().GetAwaiter().GetResult();
#pragma warning restore TCSG137
            StartLoopThread($"quic-accept-{Self}", async ct =>
            {
                var listener = _listener!;
                while (!ct.IsCancellationRequested)
                {
                    var connection = await listener.AcceptConnectionAsync(ct);
                    _loops.Submit(ct2 => AcceptConnectionAsync(connection, ct2).AsTask());   // 每连接握手 = 有界短任务（超时自愈）留池
                }
            });
        }
        Logger?.LogInformation("QUIC 传输启动：listen={Listen} peers={Peers}", Options.ListenEndPoint, Options.Peers.Count);
    }

    /// <inheritdoc/>
    /// <returns>完成时 listener/连接/循环线程/证书均已释放（幂等——重复调用立即完成）。</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();   // 长循环退出信号先行（listener/连接 Dispose 随后兜底解除 IO 挂起）
        if (_listener is not null) await _listener.DisposeAsync().ConfigureAwait(false);
        foreach (var conn in _connections.Values)
            await conn.DisposeAsync().ConfigureAwait(false);
        _connections.Clear();
        // ★ 循环线程有界等退（总预算 2s；超时残留由进程收尾——退出条件已就位）
        var deadline = Environment.TickCount64 + 2000;
        foreach (var loopThread in _loopThreads.Keys)
        {
            var remain = (int)Math.Max(0, deadline - Environment.TickCount64);
            if (remain == 0) break;
            try { await loopThread.WaitAsync(TimeSpan.FromMilliseconds(remain)).ConfigureAwait(false); }
            catch (TimeoutException) { }
            catch (Exception) { }   // 循环线程不外泄异常（泵外逃逸已日志兜底）
        }
        await _loops.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
        _certificate.Dispose();
    }

    // ═══ 连接面（惰性拨号 + 入站接受——节点对单连接）═══

    private async ValueTask AcceptConnectionAsync(QuicConnection connection, CancellationToken ct)
    {
        // 入站方向：等待拨号方的握手 stream（对称回写）——首 stream 非 0xFE = 拒收
        try
        {
            var first = await connection.AcceptInboundStreamAsync(ct).ConfigureAwait(false);
            var remote = await PerformHandshakeAsync(connection, first, expected: null, ct).ConfigureAwait(false);
            // ★ 入站黑名单（二期-C2——被移除成员重连拒）
            if (_inboundDeny.ContainsKey(remote))
            {
                Logger?.LogWarning("QUIC 入站连接被拒（黑名单）：peer={Remote}", remote);
                await connection.DisposeAsync().ConfigureAwait(false);
                return;
            }
            var state = new Connection(this, connection, remote);
            if (_connections.TryAdd(remote, state))
            {
                state.StartReceiveLoop();
                PeerConnected?.Invoke(remote);
                Logger?.LogDebug("QUIC 入站连接就绪：peer={Peer}", remote);
            }
            else
            {
                await state.DisposeAsync().ConfigureAwait(false);   // 重复连接——保留既有
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger?.LogWarning(ex, "QUIC 入站连接失败：{Remote}", connection.RemoteEndPoint);
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>对端连接（活连接复用；断连后惰性重建——单飞锁防并发拨号）。</summary>
    private async ValueTask<Connection> GetOrConnectAsync(NodeId peer, CancellationToken ct)
    {
        if (_connections.TryGetValue(peer, out var existing) && !existing.IsDisposed) return existing;
        // ★ 二期-C2：端点动态表（AddPeer/RemovePeer 面）
        if (!_peerEndpoints.TryGetValue(peer, out var endpoint))
            throw new NetIOException($"节点 {peer} 不在对端地址表（QUIC 成员制拨号）。");
        if (_faultsImpl.IsPartitioned(Self, peer))
            throw new NetIOException($"对端 {peer} 已分区（注入）。");

        Connection? state;
        while (!_connections.TryGetValue(peer, out state))
        {
            if (!_connectAttempts.TryAdd(peer, 0))
            {
                await Task.Yield();   // 他拨正在拨——让出重查
                continue;
            }
            try
            {
                var connection = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
                {
                    RemoteEndPoint = endpoint,
                    DefaultStreamErrorCode = 0x0A,
                    DefaultCloseErrorCode = 0x0B,
                    // ★ .NET 8 缺省 0 = 拒绝对端开流——显式放开（同服务端）
                    MaxInboundBidirectionalStreams = InboundStreamCapacity,
                    MaxInboundUnidirectionalStreams = InboundStreamCapacity,
                    ClientAuthenticationOptions = _mutualTls
                        ? QuicCertificates.ClientAuthenticationMtls(_certificate!, _caBundle)
                        : QuicCertificates.ClientAuthentication(),
                }, ct).ConfigureAwait(false);

                // 应用层对称握手：我写身份 → 读对端身份 → 校验
                var stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, ct).ConfigureAwait(false);
                var remote = await PerformHandshakeAsync(connection, stream, expected: peer, ct).ConfigureAwait(false);
                state = new Connection(this, connection, remote);
                _connections[peer] = state;
                state.StartReceiveLoop();
                PeerConnected?.Invoke(peer);
                Logger?.LogDebug("QUIC 出站连接就绪：peer={Peer}", peer);
                return state;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new NetIOException($"QUIC 拨号失败：{endpoint}。", ex);
            }
            finally
            {
                _connectAttempts.TryRemove(peer, out _);
            }
        }
        return state;
    }

    // 对称握手（0xFE stream 双向）：写 [NodeId 16B][ClusterTag 4B] → 读对端 → 校验
    // ═══ 动态成员（二期-C2 §5.3——IPeerRegistry；QUIC = 惰性连接表，无拨号循环）═══

    /// <inheritdoc/>
    public void AddPeer(NodeId id, IPEndPoint endpoint)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, nameof(QuicTransport));
        ArgumentNullException.ThrowIfNull(endpoint);
        if (id == Self) throw new ArgumentException("不能将本端加入对端表。", nameof(id));
        _inboundDeny.TryRemove(id, out _);   // 复入解禁
        _peerEndpoints[id] = endpoint;
    }

    /// <inheritdoc/>
    public void RemovePeer(NodeId id, bool closeLink = true)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, nameof(QuicTransport));
        if (!_peerEndpoints.TryRemove(id, out _)) return;   // 未知 ID 幂等 no-op
        _inboundDeny[id] = 1;   // 入站黑名单（运行时易失）
        if (closeLink && _connections.TryRemove(id, out var conn))
        {
            conn.Close();   // 同步段：取消接收循环 + 断持有流
            _closeSink.SubmitFast((Func<CancellationToken, Task>)(async ct => await conn.DisposeAsync().AsTask()));   // 连接资源受控异步收尾
        }
    }

    /// <summary>对端表快照（IPeerRegistry）。</summary>
    public IReadOnlyDictionary<NodeId, IPEndPoint> Peers
        => _peerEndpoints.ToDictionary(kv => kv.Key, kv => kv.Value);

    private async ValueTask<NodeId> PerformHandshakeAsync(QuicConnection connection, QuicStream stream, NodeId? expected, CancellationToken ct)
    {
        using var _ = stream;
        var timeout = TimeSpan.FromMilliseconds(Options.HandshakeTimeout.TotalMilliseconds);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        var header = new byte[21];
        QuicHandshakeHeadCodec.Write(header, new QuicHandshakeHead
        {
            Kind = KindHandshake,
            Self = Self,
            ClusterTag = Options.ClusterTag,
        });
        await stream.WriteAsync(header, timeoutCts.Token).ConfigureAwait(false);
        stream.CompleteWrites();   // half-close（写向关闭，读向仍开——对端身份回写）

        var echoed = await ReadExactlyAsync(stream, 21, timeoutCts.Token).ConfigureAwait(false);
        var head = QuicHandshakeHeadCodec.Read(echoed);
        if (head.Kind != KindHandshake)
            throw new NetIOException("QUIC 握手响应 kind 非法。");
        var remote = head.Self;
        var tag = head.ClusterTag;
        if (tag != Options.ClusterTag)
            throw new NetIOException($"QUIC 握手集群标签不匹配（{tag} ≠ {Options.ClusterTag}）——错集群 fail-fast。");
        if (expected is { } want && remote != want)
            throw new NetIOException($"QUIC 握手身份不匹配（{remote} ≠ {want}）。");

        // ★ 二期-E6：mTLS SAN 绑定——TLS 层对端证书的 SAN nid 必须声明应用层身份
        if (_mutualTls && connection.RemoteCertificate is { } tlsCert)
        {
            var declared = CertificateNodeId.ExtractNodeIds(tlsCert);
            if (!declared.Contains(remote))
                throw new NetIOException($"QUIC mTLS SAN 绑定违规：证书声明 {string.Join("/", declared)} ≠ 握手身份 {remote}。");
        }
        return remote;
    }

    private void OnConnectionClosed(NodeId remote)
    {
        if (_connections.TryRemove(remote, out _))
        {
            PeerGone?.Invoke(remote);
            Logger?.LogDebug("QUIC 连接关闭：peer={Peer}", remote);
        }

        // ★ 二期-E6 后台重连：成员表内的对端断链 → 有界后台重拨（GetOrConnectAsync 单飞防重）
        if (_peerEndpoints.ContainsKey(remote) && !_inboundDeny.ContainsKey(remote))
        {
            _loops.SubmitFast((Func<CancellationToken, Task>)(async ct =>
            {
                for (var attempt = 0; attempt < 3 && !ct.IsCancellationRequested; attempt++)
                {
                    try
                    {
                        await GetOrConnectAsync(remote, ct).ConfigureAwait(false);
                        Logger?.LogDebug("QUIC 后台重连成功：peer={Peer}", remote);
                        return;
                    }
                    catch (Exception ex) { Logger?.LogDebug("QUIC 后台重连重试：peer={Peer} {Message}", remote, ex.Message); }
                    try { await Task.Delay(250, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                }
            }));
        }
    }

    // ═══ 数据报（单向短命 stream——send-and-forget）═══

    /// <inheritdoc/>
    /// <param name="protocolId">协议域 ID（注册区 0x60-0xAF）。</param>
    /// <param name="handler">入站数据报处理器。</param>
    /// <param name="bearer">发送承载声明（QUIC 介质恒为单向短命 stream 承载）。</param>
    public void RegisterProtocol(byte protocolId, IDatagramHandler handler, DatagramBearer bearer = DatagramBearer.Tcp)
    {
        if (!ProtocolIds.IsUserRegistrable(protocolId))
            throw new ArgumentOutOfRangeException(nameof(protocolId), protocolId, "只放行注册区 0x60-0xAF。");
        if (!_datagramHandlers.TryAdd(protocolId, handler))
            throw new InvalidOperationException($"协议域 {protocolId} 已注册。");
    }

    /// <inheritdoc/>
    /// <param name="target">目标节点（须在对端地址表）。</param>
    /// <param name="protocolId">协议域 ID。</param>
    /// <param name="payload">载荷（≤帧协议上限——大块走流式通道）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>完成时数据报已写入单向短命 stream（尽力送达——对端不可达/流级失败 = 静默丢弃，不抛；参数错误/取消抛）。</returns>
    public async ValueTask SendDatagramAsync(NodeId target, byte protocolId, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, nameof(QuicTransport));
        if (payload.Length > FrameCodec.MaxPayloadLength)
            throw new ArgumentOutOfRangeException(nameof(payload), payload.Length, "载荷超帧上限（大块走流式通道）。");
        Connection state;
        try
        {
            state = await GetOrConnectAsync(target, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return;   // 尽力送达——对端不可达 = 静默丢弃（同构语义）
        }
        if (!await _faultsImpl.ApplyDeliveryAsync(Self, target, ct).ConfigureAwait(false)) return;   // 注入丢弃

        System.Net.Quic.QuicStream stream;
        try
        {
            stream = await state.Inner.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;   // 取消传播（同构语义）
        }
        catch (Exception)
        {
            // 分区断连竞态（连接在拨号与开流间被关闭）与链路级异常 = 不可达——尽力送达静默丢弃
            //（同 connect 失败路径语义；SendRequestAsync 同款竞态已按链路断映射——判例 2026-09-03）
            return;
        }
        try
        {
            var header = (byte[])[KindDatagram, protocolId, FlagNone];
            await stream.WriteAsync(header, ct).ConfigureAwait(false);
            await stream.WriteAsync(payload, ct).ConfigureAwait(false);
            stream.CompleteWrites();
        }
        catch (Exception)
        {
            // 流级失败 = 报文丢弃（尽力语义）
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ═══ 请求回调（双向短命 stream——stream 即关联）═══

    /// <summary>流接受面注册（只放行注册区 0x60-0xAF；未注册协议域的开流 = 对端 Reset 拒绝）。</summary>
    /// <param name="protocolId">协议域 ID（注册区 0x60-0xAF）。</param>
    /// <param name="acceptor">入站会话接受面。</param>
    /// <exception cref="ArgumentOutOfRangeException">协议域不在注册区。</exception>
    /// <exception cref="InvalidOperationException">该协议域已注册。</exception>
    public void RegisterStreamAcceptor(byte protocolId, IStreamAcceptor acceptor)
    {
        if (!ProtocolIds.IsUserRegistrable(protocolId))
            throw new ArgumentOutOfRangeException(nameof(protocolId), protocolId, "只放行注册区 0x60-0xAF。");
        if (!_streamAcceptors.TryAdd(protocolId, acceptor))
            throw new InvalidOperationException($"协议域 {protocolId} 已注册流接受面。");
    }

    /// <summary>打开流式会话（双向 stream 直映射——帧边界 [4B len] 由 <see cref="QuicWireStream"/> 承载）。
    /// <para>★ 开流确认：写头后读对端 1B ack（[0x01]——accept 分派成功由传输回写，帧数据之外）；
    ///   未注册 acceptor = 对端 Abort → 此处抛 <see cref="NetIOException"/>（同构语义：开流失败）。</para></summary>
    /// <param name="target">目标节点（须在对端地址表）。</param>
    /// <param name="protocolId">协议域 ID。</param>
    /// <param name="openPayload">开流载荷（随流建立透传给对端 acceptor；组路由场景 = host 剥前缀后的内层载荷——二期-C1）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>完成后的流式会话（双向 QUIC stream 直映射；对端拒绝/链路异常抛 <see cref="NetIOException"/>）。</returns>
    public async ValueTask<IWireStream> OpenStreamAsync(NodeId target, byte protocolId, ReadOnlyMemory<byte> openPayload = default, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, nameof(QuicTransport));
        var state = await GetOrConnectAsync(target, ct).ConfigureAwait(false);
        System.Net.Quic.QuicStream stream;
        try
        {
            stream = await state.Inner.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;   // 取消传播（同构语义）
        }
        catch (Exception ex)
        {
            // 分区断连竞态（连接在拨号与开流间被关闭）——链路断语义（同 SendRequestAsync 判例）：
            // 开流失败 = NetIOException，不外泄裸 ObjectDisposedException/QuicException
            throw new NetIOException($"开流失败（协议域 {protocolId}——连接已关闭/链路异常）。", ex);
        }
        try
        {
            var header = (byte[])[KindStream, protocolId, FlagNone];
            await stream.WriteAsync(header, ct).ConfigureAwait(false);
            // ★ 二期-C1：openPayload 作为流首段 [4B len][payload]（帧界惯例；空载荷 = len 0）
            var len = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(len, openPayload.Length);
            await stream.WriteAsync(len, ct).ConfigureAwait(false);
            if (openPayload.Length > 0)
                await stream.WriteAsync(openPayload.ToArray(), ct).ConfigureAwait(false);
            var ack = await ReadExactlyAsync(stream, 1, ct).ConfigureAwait(false);
            if (ack[0] != AckAccept)
                throw new NetIOException($"对端拒绝开流（协议域 {protocolId}）。");
            return new QuicWireStream(stream, protocolId, target, openPayload);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw new NetIOException($"开流失败（协议域 {protocolId}——对端拒绝/链路异常）。", ex);
        }
    }

    /// <inheritdoc/>
    /// <param name="protocolId">协议域 ID（注册区 0x60-0xAF）。</param>
    /// <param name="handler">入站请求处理器。</param>
    public void RegisterRequestHandler(byte protocolId, IRequestHandler handler)
    {
        if (!ProtocolIds.IsUserRegistrable(protocolId))
            throw new ArgumentOutOfRangeException(nameof(protocolId), protocolId, "只放行注册区 0x60-0xAF。");
        if (!_requestHandlers.TryAdd(protocolId, handler))
            throw new InvalidOperationException($"协议域 {protocolId} 已注册请求 handler。");
    }

    // ═══ 内部注册口（二期-C1 §2.2①——QUIC 一等介质：核心区 0x00-0x4F 放行，镜像公开口语义）═══

    /// <inheritdoc/>
    public void RegisterCoreProtocol(byte protocolId, IDatagramHandler handler, DatagramBearer bearer = DatagramBearer.Tcp)
    {
        ProtocolRegistration.ValidateCorePort(protocolId);
        if (!_datagramHandlers.TryAdd(protocolId, handler))
            throw new InvalidOperationException($"协议域 {protocolId} 已注册数据报 handler。");
    }

    /// <inheritdoc/>
    public void RegisterCoreRequestHandler(byte protocolId, IRequestHandler handler)
    {
        ProtocolRegistration.ValidateCorePort(protocolId);
        if (!_requestHandlers.TryAdd(protocolId, handler))
            throw new InvalidOperationException($"协议域 {protocolId} 已注册请求 handler。");
    }

    /// <inheritdoc/>
    public void RegisterCoreStreamAcceptor(byte protocolId, IStreamAcceptor acceptor)
    {
        ProtocolRegistration.ValidateCorePort(protocolId);
        if (!_streamAcceptors.TryAdd(protocolId, acceptor))
            throw new InvalidOperationException($"协议域 {protocolId} 已注册流 acceptor。");
    }

    /// <inheritdoc/>
    /// <param name="target">目标节点（须在对端地址表）。</param>
    /// <param name="protocolId">协议域 ID。</param>
    /// <param name="payload">请求载荷。</param>
    /// <param name="options">per-call 选项（null = 传输缺省）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>完成后的应答载荷（链路断/请求耗尽抛 <see cref="NetIOException"/>/<see cref="TimeoutException"/>）。</returns>
    public async ValueTask<byte[]> SendRequestAsync(NodeId target, byte protocolId, ReadOnlyMemory<byte> payload,
        RequestOptions? options = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, nameof(QuicTransport));
        try
        {
            return await SendRequestCoreAsync(target, protocolId, payload, options, ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // 分区断连竞态：连接在拨号与使用间被关闭——链路断语义（调用方重试=重拨）
            throw new NetIOException($"QUIC 连接已关闭（{target}）——重试重建。");
        }
        catch (QuicException ex) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException("请求取消（伴随链路异常）。", ex, ct);
        }
        catch (QuicException ex)
        {
            throw new NetIOException($"QUIC 链路异常：{target}。", ex);
        }
    }

    private async Task<byte[]> SendRequestCoreAsync(NodeId target, byte protocolId, ReadOnlyMemory<byte> payload,
        RequestOptions? options, CancellationToken ct)
    {
        var retry = options?.Retry;
        var state = await GetOrConnectAsync(target, ct).ConfigureAwait(false);
        if (!await _faultsImpl.ApplyDeliveryAsync(Self, target, ct).ConfigureAwait(false))
            throw new TimeoutException($"请求被注入丢弃（{target}）。");

        var timeout = options?.Timeout ?? Options.EffectiveRequestTimeout;
        // at-least-once：CorrId 贯穿 + 应答端去重/缓存重放；at-most-once（缺省）：单发 flags=0
        if (retry is { } policy)
        {
            var correlationId = (ulong)Random.Shared.NextInt64(1, long.MaxValue);
            var header = (byte[])[KindRequest, protocolId, FlagCorrelation];
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return await SendRequestOnceAsync(state, header, correlationId, payload, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                catch (EndOfStreamException) { }
                catch (NetIOException) { }
                catch (QuicException) { }   // 链路级异常——重发下一 attempt
                if (attempt >= policy.MaxAttempts)
                    throw new TimeoutException($"请求重发 {policy.MaxAttempts} 次耗尽：{target}。");
                await Task.Delay(policy.Backoff, ct).ConfigureAwait(false);
            }
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            return await SendRequestOnceAsync(state, (byte[])[KindRequest, protocolId, FlagNone], null, payload, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"请求应答超时（{timeout}）：{target}。");
        }
        catch (EndOfStreamException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException("请求取消（伴随对端关流）。", ct);
        }
        catch (EndOfStreamException)
        {
            throw new TimeoutException($"请求未应答（对端关流）：{target}。");
        }
    }

    /// <summary>单次请求尝试（头 + 可选 CorrId + 载荷 → half-close → 读 [CorrId 回显 8B][4B len][应答]）。</summary>
    private async Task<byte[]> SendRequestOnceAsync(Connection state, byte[] header, ulong? correlationId,
        ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var stream = await state.Inner.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            await stream.WriteAsync(header, ct).ConfigureAwait(false);
            if (correlationId is { } cid)
            {
                var corrBytes = new byte[8];
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(corrBytes, cid);
                await stream.WriteAsync(corrBytes, ct).ConfigureAwait(false);
            }
            await stream.WriteAsync(payload, ct).ConfigureAwait(false);
            stream.CompleteWrites();   // half-close——载荷边界

            if (correlationId is not null)
                await ReadExactlyAsync(stream, 8, ct).ConfigureAwait(false);   // CorrId 回显（配对校验位）
            var lenBytes = await ReadExactlyAsync(stream, 4, ct).ConfigureAwait(false);
            var len = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(lenBytes);
            if (len == 0) return [];
            return await ReadExactlyAsync(stream, len, ct).ConfigureAwait(false);
        }
    }

    // ═══ 协议域面（inbound 连接的 stream 分发循环）═══

    private async ValueTask DispatchInboundAsync(Connection state, QuicStream stream, CancellationToken ct)
    {
        try
        {
            var streamHeader = new byte[3];
            if (!await TryReadExactlyAsync(stream, streamHeader, ct).ConfigureAwait(false)) return;
            var kind = streamHeader[0];
            var protocolId = streamHeader[1];
            var flags = streamHeader[2];

            switch (kind)
            {
                case KindDatagram:
                    {
                        using var buffer = new System.IO.MemoryStream();
                        var chunk = new byte[8192];
                        int read;
                        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
                        {
                            if (buffer.Length + read > FrameCodec.MaxPayloadLength)
                                return;   // 超帧上限——畸形丢弃（同构防御面）
                            await buffer.WriteAsync(chunk.AsMemory(0, read), ct).ConfigureAwait(false);
                        }
                        if (_datagramHandlers.TryGetValue(protocolId, out var handler))
                            handler.OnDatagram(state.Remote, buffer.ToArray());
                        else
                            Logger?.LogDebug("QUIC 数据报未注册协议域：{ProtocolId}", protocolId);
                        break;
                    }
                case KindRequest:
                    {
                        // 请求载荷至 EOF（发送方 half-close）→ 分发 → 应答 [CorrId 8B][4B len][bytes]（重发面）
                        ulong? correlationId = null;
                        if ((flags & FlagCorrelation) != 0)
                        {
                            var corrBytes = await ReadExactlyAsync(stream, 8, ct).ConfigureAwait(false);
                            correlationId = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(corrBytes);
                        }
                        using var buffer = new System.IO.MemoryStream();
                        var chunk = new byte[8192];
                        int read;
                        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
                            await buffer.WriteAsync(chunk.AsMemory(0, read), ct).ConfigureAwait(false);

                        // at-least-once 去重：重发同 CorrId——已应答 = 重放缓存；进行中 = 关流（原流应答）
                        if (correlationId is { } cid && !_dedup.TryBegin(cid))
                        {
                            var cached = _dedup.GetReply(cid);
                            if (cached is not null)
                                await QuicReplyContext.WriteCachedReplyAsync(stream, cid, cached).ConfigureAwait(false);
                            await stream.DisposeAsync().ConfigureAwait(false);
                            return;
                        }

                        if (_requestHandlers.TryGetValue(protocolId, out var requestHandler))
                        {
                            var reply = new QuicReplyContext(state.Remote, protocolId, stream, correlationId);
                            if (correlationId is { } cid2)
                                reply.ReplyCached += cached => _dedup.CacheReply(cid2, cached);
                            requestHandler.OnRequest(state.Remote, buffer.ToArray(), reply);
                            if (reply.ReplyTask is null)
                                state.Hold(stream);   // deferred——防 GC 终结 abort（应答/连接关时释放）
                            // ★ 应答写回受控（fire-and-forget 纪律——handler 同步回调返回后写任务
                            //   归任务组观测；stream 生命周期随应答完成释放）
                            if (reply.ReplyTask is { } replyTask)
                            {
                                var replyStream = stream;
                                _loops.Submit((Func<CancellationToken, Task>)(async _ =>
                                {
                                    try { await replyTask.ConfigureAwait(false); }
                                    catch { /* 回程失败——发送方超时语义 */ }
                                    await replyStream.DisposeAsync().ConfigureAwait(false);
                                }));
                            }
                            // ★ 未应答 ≠ 不会应答——deferred handler 稍后调用 ReplyAsync（流保持存活）；
                            //   不主动 abort（abort 与对端取消并发时会冒竞态异常——连接关闭时随连接统一释放，
                            //   流上限 InboundStreamCapacity 兜底异常形态）
                        }
                        else
                        {
                            await stream.DisposeAsync().ConfigureAwait(false);   // 未注册 = 不应答（对端超时——同构语义）
                        }
                        break;
                    }
                case KindStream:
                    {
                        if (_streamAcceptors.TryGetValue(protocolId, out var acceptor))
                        {
                            // ★ 二期-C1：openPayload 首段读取 [4B len][payload]（发起端头部后追加）
                            var lenBuf = await ReadExactlyAsync(stream, 4, ct).ConfigureAwait(false);
                            var len = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
                            ArgumentOutOfRangeException.ThrowIfNegative(len);
                            ArgumentOutOfRangeException.ThrowIfGreaterThan(len, (int)FrameCodec.MaxPayloadLength);   // openPayload 上限 = 帧载荷上限（16MB）
                            var payload = len == 0 ? ReadOnlyMemory<byte>.Empty : await ReadExactlyAsync(stream, len, ct).ConfigureAwait(false);
                            await stream.WriteAsync((byte[])[AckAccept], ct).ConfigureAwait(false);   // 开流确认（传输层——帧数据之外，发起端 OpenStream 消费）
                            var wire = new QuicWireStream(stream, protocolId, state.Remote, payload);
                            acceptor.OnStream(state.Remote, wire, payload);
                        }
                        else
                        {
                            stream.Abort(QuicAbortDirection.Both, AbortCodeRefused);   // 拒绝开流（未注册协议域）
                            await stream.DisposeAsync().ConfigureAwait(false);
                        }
                        break;
                    }
                default:
                    Logger?.LogWarning("QUIC 入站 stream 非法 kind：{Kind}", kind);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Logger?.LogDebug("QUIC 入站 stream 处理结束（异常吸收）：{Ex}", ex.Message);
        }
    }

    /// <summary>入站应答上下文（[CorrId 8B（重发面）][4B len][bytes] 回写）。
    /// <para>★ 应答写回任务入传输任务组受控（fire-and-forget 纪律——<see cref="ReplyTask"/> 供
    /// dispatch 侧接管 stream 生命周期）。</para></summary>
    private sealed class QuicReplyContext(NodeId peer, byte protocolId, QuicStream stream, ulong? correlationId) : IReplyContext
    {
        private int _replied;

        /// <summary>应答缓存钩子（at-least-once 重发面——应答落 dedup 窗口）。</summary>
        public event Action<byte[]>? ReplyCached;

        public NodeId Peer => peer;
        public byte ProtocolId => protocolId;
        public ulong CorrelationId => correlationId ?? (ulong)stream.Id;

        /// <summary>应答写回任务（handler 未调用应答 = null——发送方超时语义）。</summary>
        public Task? ReplyTask { get; private set; }

        private readonly TaskCompletionSource _called = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>等待 handler 调用应答（deferred 形态——stream 生命周期挂此）。</summary>
        /// <returns>handler 调用 <see cref="ReplyAsync"/> 时完成的任务。</returns>
        public Task WaitForReplyCallAsync() => _called.Task;

        /// <summary>回发请求应答（恰好一次——CorrId[若相关]/长度前缀/载荷写回请求 stream）。</summary>
        /// <param name="payload">应答载荷（空应答合法）。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>完成时应答已写入 stream；重复应答返回 faulted 的 ValueTask（InvalidOperationException）。</returns>
        public ValueTask ReplyAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref _replied, 1) != 0)
                return ValueTask.FromException(new InvalidOperationException("请求已应答（ReplyAsync 只可一次）。"));
            _called.TrySetResult();
            var data = payload.ToArray();
            ReplyCached?.Invoke(data);
            ReplyTask = WriteReplyAsync(data, ct);
            return new ValueTask(ReplyTask);
        }

        /// <summary>缓存应答重放（重发同 CorrId 到达且已应答——dedup 窗口直接回放）。</summary>
        /// <param name="stream">重发请求对应的 QUIC stream。</param>
        /// <param name="correlationId">请求关联 ID（8B 小端写回）。</param>
        /// <param name="payload">缓存的应答载荷。</param>
        /// <returns>完成时重放已尽力写出（对端已放弃等写失败被吞——尽力语义）。</returns>
        public static async Task WriteCachedReplyAsync(QuicStream stream, ulong correlationId, byte[] payload)
        {
            try
            {
                var corrBytes = new byte[8];
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(corrBytes, correlationId);
                await stream.WriteAsync(corrBytes).ConfigureAwait(false);
                var lenBytes = new byte[4];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(lenBytes, payload.Length);
                await stream.WriteAsync(lenBytes).ConfigureAwait(false);
                if (payload.Length > 0)
                    await stream.WriteAsync(payload).ConfigureAwait(false);
                stream.CompleteWrites();
            }
            catch { /* 尽力重放——对端已放弃则忽略 */ }
        }

        private async Task WriteReplyAsync(byte[] payload, CancellationToken ct)
        {
            if (correlationId is { } cid)
            {
                var corrBytes = new byte[8];
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(corrBytes, cid);
                await stream.WriteAsync(corrBytes, ct).ConfigureAwait(false);
            }
            var lenBytes = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(lenBytes, payload.Length);
            await stream.WriteAsync(lenBytes, ct).ConfigureAwait(false);
            if (payload.Length > 0)
                await stream.WriteAsync(payload, ct).ConfigureAwait(false);
            stream.CompleteWrites();
        }
    }

    /// <summary>at-least-once 去重窗口（CorrId → 应答缓存；null = 首达进行中；容量上限 FIFO 淘汰）。</summary>
    internal sealed class DedupWindow
    {
        private readonly object _lock = new();
        private readonly Dictionary<ulong, byte[]?> _entries = [];
        private readonly Queue<ulong> _order = [];
        private readonly int _capacity;

        public DedupWindow(int capacity) => _capacity = capacity;

        /// <summary>登记首达（true = 新请求；false = 重发——查 <see cref="GetReply"/>）。</summary>
        /// <param name="correlationId">请求关联 ID。</param>
        /// <returns>true = 首达（已占用窗口槽位）；false = 重发（窗口内已有记录）。</returns>
        public bool TryBegin(ulong correlationId)
        {
            lock (_lock)
            {
                if (_entries.ContainsKey(correlationId)) return false;
                _entries[correlationId] = null;
                _order.Enqueue(correlationId);
                while (_order.Count > _capacity)
                {
                    var oldest = _order.Dequeue();
                    _entries.Remove(oldest);
                }
                return true;
            }
        }

        /// <summary>已应答的缓存（null = 首达进行中/未登记）。</summary>
        public byte[]? GetReply(ulong correlationId)
        {
            lock (_lock) return _entries.TryGetValue(correlationId, out var cached) ? cached : null;
        }

        /// <summary>应答落缓存（首达应答完成时调用）。</summary>
        /// <param name="correlationId">请求关联 ID（须已由 <see cref="TryBegin"/> 登记，否则无操作）。</param>
        /// <param name="payload">应答载荷（驻留去重窗口供重发重放）。</param>
        public void CacheReply(ulong correlationId, byte[] payload)
        {
            lock (_lock)
            {
                if (_entries.ContainsKey(correlationId))
                    _entries[correlationId] = payload;
            }
        }
    }

    /// <summary>精确读 n 字节（EOF 提前 = 抛）。</summary>
    private static async Task<byte[]> ReadExactlyAsync(QuicStream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        if (!await TryReadExactlyAsync(stream, buffer, ct).ConfigureAwait(false))
            throw new EndOfStreamException("QUIC stream EOF——对端提前关闭。");
        return buffer;
    }

    private static async ValueTask<bool> TryReadExactlyAsync(QuicStream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }

    // ═══ 连接状态（入站 stream 分发循环宿主）═══

    internal sealed class Connection : IAsyncDisposable
    {
        private readonly QuicTransport _owner;
        private readonly CancellationTokenSource _cts = new();
        private int _receiveLoopStarted;
        private int _disposed;

        public Connection(QuicTransport owner, QuicConnection connection, NodeId remote)
        {
            _owner = owner;
            Inner = connection;
            Remote = remote;
        }

        public QuicConnection Inner { get; }
        public NodeId Remote { get; }
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        /// <summary>移除断链（二期-C2 RemovePeer 面）：同步段 = 取消接收循环 + 释放持有流；
        /// 连接级资源（CloseAsync/Dispose）经宿主 TaskSink 受控异步收尾（fire-and-forget 纪律）。</summary>
        public void Close()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _cts.Cancel();
            foreach (var s in _heldStreams.Keys)
            {
                try { s.Abort(System.Net.Quic.QuicAbortDirection.Both, 0x0B); } catch { /* 持有流尽力断 */ }
            }
            _heldStreams.Clear();
        }

        // deferred 未应答流持有表（防 GC 终结 abort——连接关闭统一释放）
        private readonly ConcurrentDictionary<QuicStream, byte> _heldStreams = new();

        /// <summary>持有未决流（防 GC 终结关闭句柄——deferred 应答形态）。</summary>
        public void Hold(QuicStream stream) => _heldStreams.TryAdd(stream, 0);

        /// <summary>启动连接级入站 stream 接收循环（专用线程+泵域——每连接恰一次，重复调用为 no-op；
        /// 循环退出即触发连接关闭清理）。</summary>
        public void StartReceiveLoop()
        {
            if (Interlocked.Exchange(ref _receiveLoopStarted, 1) != 0) return;
            var ct = _cts.Token;
            // ★ 连接级接收循环 = 长稳定循环——专用线程+泵域（池续体丢失 = 连接聋死，
            //   入站流无人 accept）；每 stream 分发 DispatchInboundAsync = 有界短任务（EOF/超时自愈）留池
            _owner.StartLoopThread($"quic-receive-{Remote}", async token =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        var stream = await Inner.AcceptInboundStreamAsync(token);
                        _owner._loops.Submit(ct2 => _owner.DispatchInboundAsync(this, stream, ct2).AsTask());
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _owner.Logger?.LogDebug("QUIC 接收循环退出（连接级异常）：peer={Peer} {Ex}", Remote, ex.Message);
                    // 连接级异常 = 链路死——清理路径
                }
                finally
                {
                    _owner.OnConnectionClosed(Remote);
                }
            });
        }

        /// <summary>释放连接（取消接收循环、统一释放未决流与底层 QUIC 连接；幂等）。</summary>
        /// <returns>完成时连接及其全部未决流均已释放。</returns>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _cts.Cancel();
            foreach (var held in _heldStreams.Keys)
                await held.DisposeAsync().ConfigureAwait(false);   // 连接关闭——未决流统一释放
            await Inner.DisposeAsync().ConfigureAwait(false);
            _cts.Dispose();
        }
    }

    // ═══ 故障注入（对齐 TCP FaultsImpl 语义；乱序对 QUIC 不适用）═══

    internal sealed class FaultsImpl(QuicTransport owner) : ITransportFaultInjector
    {
        private readonly ConcurrentDictionary<(NodeId From, NodeId To), TimeSpan> _latency = new();
        private readonly ConcurrentDictionary<(NodeId From, NodeId To), double> _dropRate = new();
        private readonly ConcurrentDictionary<(NodeId From, NodeId To), byte> _partitioned = new();

        /// <summary>对向是否分区（拨号阻断判定）。</summary>
        /// <param name="self">本端节点。</param>
        /// <param name="peer">对端节点。</param>
        /// <returns>true = 双向任一方向已标记分区（拨号将被阻断）；false = 未分区。</returns>
        public bool IsPartitioned(NodeId self, NodeId peer)
            => _partitioned.ContainsKey((self, peer)) || _partitioned.ContainsKey((peer, self));

        /// <summary>发送路径注入：分区/丢包 = false（静默丢弃）；延迟 = 阻塞时延。</summary>
        /// <param name="from">发送方节点。</param>
        /// <param name="to">接收方节点。</param>
        /// <param name="ct">取消令牌（延迟等待可取消）。</param>
        /// <returns>true = 放行投递；false = 被注入丢弃（分区/丢包——调用方静默丢弃或按超时语义处理）。</returns>
        public async ValueTask<bool> ApplyDeliveryAsync(NodeId from, NodeId to, CancellationToken ct)
        {
            if (_partitioned.ContainsKey((from, to))) return false;
            if (_dropRate.TryGetValue((from, to), out var rate) && rate > 0 && Random.Shared.NextDouble() < rate) return false;
            if (_latency.TryGetValue((from, to), out var latency) && latency > TimeSpan.Zero)
                await Task.Delay(latency, ct).ConfigureAwait(false);
            return true;
        }

        /// <inheritdoc/>
        /// <param name="a">端点 A（方向 a→b 生效）。</param>
        /// <param name="b">端点 B。</param>
        /// <param name="latency">单向延迟（null/非正 = 清除该定向延迟）。</param>
        public void SetLatency(NodeId a, NodeId b, TimeSpan? latency)
        {
            var key = (a, b);
            if (latency is null || latency.Value <= TimeSpan.Zero) _latency.TryRemove(key, out _);
            else _latency[key] = latency.Value;
        }

        /// <inheritdoc/>
        /// <param name="groupA">分区组 A。</param>
        /// <param name="groupB">分区组 B（与组 A 之间双向断并关闭既有连接）。</param>
        public void Partition(IEnumerable<NodeId> groupA, IEnumerable<NodeId> groupB)
        {
            ArgumentNullException.ThrowIfNull(groupA);
            ArgumentNullException.ThrowIfNull(groupB);
            var listA = groupA.ToArray();
            var listB = groupB.ToArray();
            foreach (var a in listA)
                foreach (var b in listB)
                {
                    _partitioned[(a, b)] = 0;
                    _partitioned[(b, a)] = 0;
                }
            foreach (var conn in owner._connections.Values)
            {
                if (listA.Contains(conn.Remote) || listB.Contains(conn.Remote))
                {
                    // 断连（PeerGone 事件链 + 拨号阻断）——经任务组受控提交（TCSG138 存量清扫：
                    // 裸 `_ =` 丢弃改 sink 观测面），SubmitFast 首段内联保持切断即时生效
                    try
                    {
                        owner._loops.SubmitFast(_ => conn.DisposeAsync());
                    }
                    catch (ObjectDisposedException)
                    {
                        // 传输已收尾——分区注入晚到为 no-op（连接已随 Dispose 全量断开）
                    }
                }
            }
        }

        /// <inheritdoc/>
        /// <param name="from">发送方节点。</param>
        /// <param name="to">接收方节点。</param>
        /// <param name="rate">丢包率（0..1；0 = 清除该定向丢包）。</param>
        public void Drop(NodeId from, NodeId to, double rate)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(rate);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(rate, 1.0);
            var key = (from, to);
            if (rate <= 0) _dropRate.TryRemove(key, out _);
            else _dropRate[key] = rate;
        }

        /// <inheritdoc/>
        /// <param name="from">发送方节点。</param>
        /// <param name="to">接收方节点。</param>
        /// <param name="enable">true/false 均为 no-op（QUIC 数据报承载于有序 stream——乱序注入结构性不适用）。</param>
        public void Reorder(NodeId from, NodeId to, bool enable)
        {
            // QUIC 介质数据报承载于有序 stream——乱序注入结构性不适用（有意 no-op）
        }

        /// <inheritdoc/>
        public void Reset()
        {
            _latency.Clear();
            _dropRate.Clear();
            _partitioned.Clear();
        }
    }
}
