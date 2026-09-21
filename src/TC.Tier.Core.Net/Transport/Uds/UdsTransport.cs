using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using TC.Tier.CodeGen;
using TC.Tier.Core.Execution;
using TC.Tier.Core.IO;
using TC.Tier.Core.Logging;
using TC.Tier.Core.Net.Channels;
using TC.Tier.Core.Net.Wire;

namespace TC.Tier.Core.Net.Transport.Uds;

/// <summary>UDS 请求帧头（29B：[CorrId 8B][From 16B][Domain 1B][Len 4B]——[BinaryLayout] 声明式生成）。</summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 29)]
internal struct UdsRequestHead
{
    /// <summary>关联 ID。</summary>
    [FieldOffset(0)] public ulong CorrId;

    /// <summary>发起端身份。</summary>
    [FieldOffset(8)] public NodeId From;

    /// <summary>产品协议域。</summary>
    [FieldOffset(24)] public byte Domain;

    /// <summary>payload 长度。</summary>
    [FieldOffset(25)] public int Length;
}

/// <summary>UDS 应答帧头（12B：[CorrId 8B][Len 4B]——应答载荷尾随）。</summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 12)]
internal struct UdsReplyHead
{
    /// <summary>关联 ID。</summary>
    [FieldOffset(0)] public ulong CorrId;

    /// <summary>payload 长度。</summary>
    [FieldOffset(8)] public int Length;
}


/// <summary>
/// UDS 同机控制面介质（二期-E7，DDR-E7——docs/design/e7-uds-medium-ddr.md）：
/// Unix domain socket 点对点请求回调传输——定位 = 同机管理/控制面
/// （管理查询/G2 段分配/锁服务/健康探针等请求回调域）。
/// <para>★ 线信封（自描述，无跨介质互通承诺）：</para>
/// <code>
/// 请求帧  [CorrId 8B][From 16B][Domain 1B][len 4B][payload]
/// 应答帧  [CorrId 8B][len 4B][payload]
/// </code>
/// <para>★ 承载面：请求回调 only——数据报/流式/核心数据报注册 → NotSupported
///   （raft 复制走 TCP/QUIC——介质定位文档化，DDR-E7 §0）。</para>
/// <para>★ 寻址与连接形态：ctor 供给对端路径表（NodeId → socket 路径）；**每请求短连接**
///   （new → connect → 写请求 → 读一帧应答 → dispose——并发在途靠每请求独立连接成立，
///   不靠连接复用；#416 方案 B 裁定：承认现状，长连接复用 + CorrId 常驻读循环分发为
///   独立立项）。<see cref="PeerConnected"/>/<see cref="PeerGone"/> 为 ITransport 契约成员，
///   当前形态不触发（无对端生命周期跟踪）。</para>
/// </summary>
public sealed class UdsTransport : IProtocolTransport, ICoreProtocolPort
{
    private const int RequestHeaderBytes = 29;   // [CorrId 8B][From 16B][Domain 1B][len 4B]
    private const int ResponseHeaderBytes = 12;  // [CorrId 8B][len 4B]

    private readonly NodeId _self;
    private readonly string? _ownPath;
    private readonly IReadOnlyDictionary<NodeId, string> _peerPaths;
    private readonly UnixFileMode? _socketFileMode;
    private readonly ILogger? _logger;

    private readonly ConcurrentDictionary<byte, IRequestHandler> _handlers = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    // ★ #416 方案 B：_pending（CorrId 等待表）与 _dialSockets（长连接池）死代码退役——
    //   应答配对靠每请求独立 socket 直读，两者注册/声明后零消费；长连接复用立项时重建。
    private readonly CancellationTokenSource _cts = new();
    // ★ 入站连接环经 TaskSink 受控提交（TCSG138 存量清扫）——每请求短连接形态下属事件驱动
    //   型派发（可丢失自愈：对端超时重试），异常观测面 + Dispose 有界 drain 一体。
    private readonly TaskSink _inboundSink;
    private Task? _acceptThread;   // accept 循环专用线程任务（Start 起、DisposeAsync 有界等退——观察面）
    private Socket? _listener;
    private ulong _corrIdCursor;
    private int _started;
    private int _disposed;

    /// <summary>构造（ownPath 非空 = 监听形态——接受对端入站请求；null = 纯客户端形态）。</summary>
    /// <param name="self">本端节点 ID。</param>
    /// <param name="ownPath">本端 socket 文件路径（Dispose unlink）。</param>
    /// <param name="peerPaths">对端路径表（NodeId → socket 路径——拨号目标）。</param>
    /// <param name="logger">日志（可选）。</param>
    /// <param name="socketFileMode">socket inode 权限位（#508——bind 后收紧，IPC 身份授权铁证面；
    /// 典型 0600=仅属主可连，替代「本机即信任域」免鉴权）。null = 不置（umask 决定，零行为变化）。
    /// 仅 Unix（Linux/macOS）生效——Windows 无 POSIX inode 语义，非 null 值在 Start 抛
    /// <see cref="NotSupportedException"/>（安全权限请求绝不静默忽略）。</param>
    public UdsTransport(NodeId self, string? ownPath, IReadOnlyDictionary<NodeId, string>? peerPaths = null,
        ILogger? logger = null, UnixFileMode? socketFileMode = null)
    {
        if (self == NodeId.Empty) throw new ArgumentException("本端节点 ID 不能为 Empty 哨兵。", nameof(self));
        _self = self;
        _ownPath = ownPath;
        _peerPaths = peerPaths ?? new Dictionary<NodeId, string>();
        _logger = logger;
        _socketFileMode = socketFileMode;
        _inboundSink = new TaskSink("uds-inbound", logger: logger);
    }

    /// <summary>对端路径表（诊断/测试）。</summary>
    public IReadOnlyDictionary<NodeId, string> PeerPaths => _peerPaths;

    /// <inheritdoc/>
    public NodeId Self => _self;

    /// <inheritdoc/>
    /// <remarks>★ #416 方案 B：当前每请求短连接形态不触发（无对端生命周期跟踪）——
    /// ITransport 契约成员保留，长连接复用立项时接线。</remarks>
#pragma warning disable CS0067 // 契约保留成员（见 remarks）——接线前不触发
    public event Action<NodeId>? PeerConnected;

    /// <inheritdoc/>
    /// <remarks>★ 同 <see cref="PeerConnected"/>——当前形态不触发。</remarks>
#pragma warning disable CS0067 // 契约保留成员（见 remarks）——接线前不触发
    public event Action<NodeId>? PeerGone;

    /// <inheritdoc/>
    public ITransportFaultInjector Faults { get; } = new NoopFaults();

    /// <summary>启动：ownPath 非空 = 绑定/监听/accept 循环（socket 文件残留先清理）。</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_ownPath is null) return;   // 纯客户端形态——无监听

        UnlinkOwnSocket();   // 残留 socket 清理（进程崩溃遗留）
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(_ownPath));
        // ★ #508：bind 后收紧 socket inode 权限位（bind 权限由 umask 决定——典型 0755 世界可连；
        //   path-based chmod 对 socket inode 适用，安全权限请求绝不静默忽略）
        if (_socketFileMode is { } mode)
        {
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                File.SetUnixFileMode(_ownPath, mode);
                _logger?.LogInformation("UDS socket 权限位已收紧：{Path} → {Mode}", _ownPath, mode);
            }
            else
            {
                throw new NotSupportedException(
                    $"socketFileMode 需 POSIX inode 权限语义（Linux/macOS）——当前平台不支持（值 {mode} 不允许静默忽略）。");
            }
        }
        _listener.Listen(16);
        // ★ 专用线程 + AsyncPump 泵域（TCSG138 存量清扫——长稳定生命周期循环不入池，
        //   对齐共识循环先例）：accept 环 = 传输活性源，域内续体回流泵线程，池抖动不断链。
        //   返回任务持有于 _acceptThread（DisposeAsync 等退观察）——非丢弃。
        var pump = new AsyncPump("uds-accept", _logger);
        var loopCt = _cts.Token;
        _acceptThread = Task.Factory.StartNew(() =>
        {
            try { pump.Run(() => AcceptLoopAsync(loopCt), loopCt); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logger?.LogError(ex, "UDS accept 循环异常终止"); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
    }

    /// <summary>DisposeAsync（幂等）：unlink socket 文件 + 关闭监听/连接 + 取消循环 + 有界 drain
    /// （accept 专用线程等退、入站环排空——重复调用幂等直返）。</summary>
    /// <returns>完成时 socket 文件已 unlink、监听已关闭、循环已收口。</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        _listener?.Dispose();
        UnlinkOwnSocket();
        if (_acceptThread is { } accept)
        {
            try { await accept.ConfigureAwait(false); }
            catch { /* 启动 lambda 内已收口——不外泄 */ }
        }
        await _inboundSink.DisposeAsync().ConfigureAwait(false);   // 组取消 + 有界 drain 在途入站环
        _cts.Dispose();
    }

    /// <summary>unlink 本端 socket 文件（★ 零 BCL IO 纪律，设计 §11——目录项变更经 IFileSystem；
    /// socket 文件 = 普通目录项，Disk 介质的 Delete 即 unlink）。临时挂载 socket 所在目录，用毕即释。</summary>
    private void UnlinkOwnSocket()
    {
        if (_ownPath is null) return;
        var dir = Path.GetDirectoryName(_ownPath)?.Replace('\\', '/') ?? "";
        using var fs = TierFs.OpenOrCreate($"local:///{dir}");
        var rel = Path.GetFileName(_ownPath);
        if (fs.Exists(rel)) fs.Delete(rel);
    }

    /// <summary>应答上下文（UDS——同 socket 回写 [CorrId 8B][len 4B][payload] 应答帧）。</summary>
    private sealed class UdsReplyContext(Socket socket, ulong corrId,
        TaskCompletionSource<byte[]> done) : IReplyContext
    {
        public NodeId Peer { get; private set; }
        public byte ProtocolId => 0;
        public ulong CorrelationId => corrId;

        /// <summary>回发请求应答（同 socket 写应答帧并通知入站循环应答已就绪）。</summary>
        /// <param name="payload">应答载荷。</param>
        /// <param name="ct">取消令牌（传播到 socket 写）。</param>
        /// <returns>完成时应答帧已写入 socket 且等待方已放行。</returns>
        public async ValueTask ReplyAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default)
        {
            var frame = new byte[12 + payload.Length];
            UdsReplyHeadCodec.Write(frame, new UdsReplyHead { CorrId = corrId, Length = payload.Length });
            payload.Span.CopyTo(frame.AsSpan(12));
            await socket.SendAsync(frame, SocketFlags.None, ct).ConfigureAwait(false);
            done.TrySetResult(payload.ToArray());
        }
    }

    private sealed class NoopFaults : ITransportFaultInjector
    {
        /// <summary>空实现（UDS 介质无故障注入面——同机 socket 无注入点）。</summary>
        /// <param name="a">源节点 ID（忽略）。</param>
        /// <param name="b">目标节点 ID（忽略）。</param>
        /// <param name="latency">延迟值（忽略）。</param>
        public void SetLatency(NodeId a, NodeId b, TimeSpan? latency) { }
        /// <summary>空实现（同 <see cref="SetLatency"/>——UDS 介质无注入面）。</summary>
        /// <param name="groupA">分组 A 成员（忽略）。</param>
        /// <param name="groupB">分组 B 成员（忽略）。</param>
        public void Partition(IEnumerable<NodeId> groupA, IEnumerable<NodeId> groupB) { }
        /// <summary>空实现（同 <see cref="SetLatency"/>——UDS 介质无注入面）。</summary>
        /// <param name="from">源节点 ID（忽略）。</param>
        /// <param name="to">目标节点 ID（忽略）。</param>
        /// <param name="rate">丢包率 ∈ [0,1]（忽略）。</param>
        public void Drop(NodeId from, NodeId to, double rate) { }
        /// <summary>空实现（同 <see cref="SetLatency"/>——UDS 介质无注入面）。</summary>
        /// <param name="from">源节点 ID（忽略）。</param>
        /// <param name="to">目标节点 ID（忽略）。</param>
        /// <param name="enable">是否启用乱序（忽略）。</param>
        public void Reorder(NodeId from, NodeId to, bool enable) { }
        /// <summary>空实现（无注入即无清除——恒为无操作）。</summary>
        public void Reset() { }
    }

    /// <summary>accept 循环（泵域专用线程——域内续体回流泵线程，不写 ConfigureAwait）。</summary>
    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var socket = await _listener!.AcceptAsync(ct);
                _inboundSink.Submit(ct2 => InboundLoopAsync(socket, ct2));
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { _logger?.LogWarning(ex, "UDS 监听异常终止（Dispose 收尾）"); }
    }

    /// <summary>入站连接循环：逐帧 [CorrId 8B][From 16B][Domain 1B][len 4B][payload]——
    /// 查 handler（未注册域丢弃）→ 同 socket 回应答帧。</summary>
    private async Task InboundLoopAsync(Socket socket, CancellationToken ct)
    {
        var header = new byte[29];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!await ReadExactAsync(socket, header, ct).ConfigureAwait(false)) break;
                var head = UdsRequestHeadCodec.Read(header);
                var corrId = head.CorrId;
                var from = head.From;
                var domain = head.Domain;
                var len = head.Length;
                if (len < 0) break;
                var payload = new byte[len];
                if (len > 0 && !await ReadExactAsync(socket, payload, ct).ConfigureAwait(false)) break;

                if (!_handlers.TryGetValue(domain, out var handler)) continue;   // 未注册域——尽力丢弃
                var response = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                handler.OnRequest(from, payload, new UdsReplyContext(socket, corrId, response));

                // ★ #415：应答帧由 <see cref="UdsReplyContext.ReplyAsync"/> **唯一**写（与 TCP 侧
                //   LinkReplyContext 同构回程契约）——此处仅等完成（同 socket 写串行化：应答写完
                //   才读下一请求帧）。旧实现此处再写一次同一帧 = 双重回写，长连接复用形态下
                //   重复帧使对端 [CorrId][len] 逐帧解析失配（当前每请求短连接形态恰好掩盖）。
                await response.Task.WaitAsync(ct).ConfigureAwait(false);
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // 链路异常——连接终止（尽力语义）
        }
    }

    private async Task<bool> ReadExactAsync(Socket socket, Memory<byte> buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await socket.ReceiveAsync(buffer[offset..], SocketFlags.None, ct).ConfigureAwait(false);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }

    // ═══ IProtocol（控制面：请求回调 only）══

    /// <inheritdoc/>
    public void RegisterProtocol(byte protocolId, IDatagramHandler handler, DatagramBearer bearer = DatagramBearer.Tcp)
        => throw new NotSupportedException("UDS 定位 = 同机控制面（请求回调）——数据报面不支持（DDR-E7）。");

    /// <inheritdoc/>
    public ValueTask SendDatagramAsync(NodeId target, byte protocolId, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
        => throw new NotSupportedException("UDS 定位 = 同机控制面（请求回调）——数据报面不支持。");

    /// <inheritdoc/>
    public void RegisterStreamAcceptor(byte protocolId, IStreamAcceptor acceptor)
        => throw new NotSupportedException("UDS 定位 = 同机控制面（请求回调）——流式面不支持。");

    /// <inheritdoc/>
    public void RegisterRequestHandler(byte protocolId, IRequestHandler handler)
    {
        ProtocolRegistration.ValidateUserPort(protocolId);
        ProtocolRegistration.Add(_handlers, protocolId, handler);
    }

    /// <inheritdoc/>
    public void RegisterCoreRequestHandler(byte protocolId, IRequestHandler handler)
    {
        ProtocolRegistration.ValidateCorePort(protocolId);
        ProtocolRegistration.Add(_handlers, protocolId, handler);
    }

    /// <inheritdoc/>
    public void RegisterCoreStreamAcceptor(byte protocolId, IStreamAcceptor acceptor)
        => throw new NotSupportedException("UDS 定位 = 同机控制面（请求回调）——流式面不支持。");

    /// <inheritdoc/>
    public void RegisterCoreProtocol(byte protocolId, IDatagramHandler handler, DatagramBearer bearer = DatagramBearer.Tcp)
        => throw new NotSupportedException("UDS 定位 = 同机控制面（请求回调）——数据报面不支持。");

    /// <inheritdoc/>
    public ValueTask<IWireStream> OpenStreamAsync(NodeId target, byte protocolId, ReadOnlyMemory<byte> openPayload = default, CancellationToken ct = default)
        => throw new NotSupportedException("UDS 定位 = 同机控制面（请求回调）——流式面不支持。");

    /// <summary>
    /// 请求回调发送（点对点——target 须在 PeerPaths 中声明路径；否则 NetIOException）。
    /// 线帧：[CorrId 8B][From 16B][Domain 1B][len 4B][payload]；应答 [CorrId 8B][len 4B][payload]。
    /// </summary>
    /// <param name="target">目标节点（须在 <see cref="PeerPaths"/> 中声明路径——未声明抛 <see cref="NetIOException"/>）。</param>
    /// <param name="protocolId">协议域 ID（请求帧携带；对端未注册域 = 静默丢弃——本端超时）。</param>
    /// <param name="payload">请求载荷。</param>
    /// <param name="options">per-call 选项（本实现未读——超时/重试不生效，仅签名同构；默认 null）。</param>
    /// <param name="ct">取消令牌（覆盖连接/写/读全程）。</param>
    /// <returns>完成时返回应答载荷（应答帧剥离 [CorrId 8B][len 4B] 头）；对端关闭 = 抛 <see cref="NetIOException"/>；取消传播。</returns>
    public async ValueTask<byte[]> SendRequestAsync(NodeId target, byte protocolId, ReadOnlyMemory<byte> payload,
        RequestOptions? options = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var path = _peerPaths.TryGetValue(target, out var p)
            ? p : throw new NetIOException($"UDS 对端 {target} 无路径（PeerPaths 未声明）。");

        var corrId = (ulong)Interlocked.Increment(ref _corrIdCursor);
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), ct).ConfigureAwait(false);
        try
        {
            var frame = new byte[29 + payload.Length];
            UdsRequestHeadCodec.Write(frame, new UdsRequestHead
            {
                CorrId = corrId,
                From = Self,
                Domain = protocolId,
                Length = payload.Length,
            });
            payload.Span.CopyTo(frame.AsSpan(29));
            await socket.SendAsync(frame, SocketFlags.None, ct).ConfigureAwait(false);

            var respBytes = new byte[12 + payload.Length];
            if (!await ReadExactAsync(socket, respBytes, ct).ConfigureAwait(false))
                throw new NetIOException("UDS 对端关闭（无应答）。");
            var respLen = UdsReplyHeadCodec.Read_Length(respBytes);
            return respBytes[12..(12 + respLen)];
        }
        finally { socket.Dispose(); }
    }

}
