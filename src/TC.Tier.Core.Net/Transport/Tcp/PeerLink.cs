using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using TC.Tier.Core.Logging;
using TC.Tier.Core.Net.Channels;
using TC.Tier.Core.Net.Security;
using TC.Tier.Core.Net.Wire;

namespace TC.Tier.Core.Net.Transport.Tcp;
/// <summary>
/// 节点对连接（spec-12 §4——节点对单 TCP 长连接：三步握手 + 帧收发循环 + 通道复用读循环）。
/// <para>★ 拨号归属（§4.3 分裂）：成员制 = NodeId 较小方拨号（入站来自较大成员 = 握手期违规
///   Error + 断连——防双向对拨）；地址制 = 有地址者拨号（NodeId 大小无关——入站身份未知，
///   从握手的 Init/Ack 得知）。监听侧握手前不知道对端身份（从 Init 读得），拨号侧成员制
///   开始即知、地址制从 Ack 得知。</para>
/// <para>★ 连接准入（§4.3）：任何完成合法握手（帧/版本/ClusterTag/安全形态/拨号归属）的连接
///   可建立——成员与直连共用同一监听口；是否参与集群路由寻址 = 传输层链路表职责。</para>
/// <para>★ 背压形态（§5.1 数据报通道）：发送 = 本端 socket 写 await（有界发送队列语义由
///   socket 缓冲承担）；接收 = 读循环 → <see cref="IDatagramHandler.OnDatagram"/> 同步回调
///   （快进快出是契约——慢回调计数器兜底）。</para>
/// <para>★ 故障注入面 = 协议域数据通道（对齐 InProcess DeliverAsync 语义——握手/管理帧不注入）。</para>
/// </summary>
internal sealed class PeerLink : IAsyncDisposable
{
    private const int StateHandshaking = 0;
    private const int StateEstablished = 1;
    private const int StateClosed = 2;

    private readonly ClusterTransport _owner;
    private readonly bool _isDialer;
    private readonly NodeId? _expectRemote;   // 成员制拨号期望身份；null = 地址制（身份从 Ack 得知）
    private readonly TcpClient _socket;
    private Stream _stream;   // 明文 = NetworkStream；mTLS = SslStream（握手前升级）
    // ★ 写队列（双优先——帧序 = writer 单循环；写锁覆盖 await socket 写是缺陷：流写背压挂起时
    //   socket 写 await 永不完成、写锁永不放，数据报/心跳/请求回调全链路饿死（dumpasync 实锤）。
    //   队列化+优先级分流：流帧（低优先）背压只停 writer 不锁写者；控制帧/数据报/请求/心跳
    //   （高优先）插队有界到达——raft 心跳不因流写背压饿死（§2/§6 心跳约束）。
    private readonly Channel<OutgoingFrame> _controlQueue;   // 高优先（管理/握手/数据报/请求/应答/保活——容量 32）
    private readonly Channel<OutgoingFrame> _streamQueue;    // 低优先（流式五帧——容量 = Options.WriteQueueFrames）
    private NodeId _remote;
    private int _state = StateHandshaking;
    private byte _negotiatedFeatures;
    private KeepaliveTracker? _keepalive;   // 握手协商出 bit0 才建——保活计票
    // ★ 常驻读缓冲（批拉帧——游标 [_rPos, _rEnd) 为未消费区；构造期分配随链路生命周期，废弃不复位）
    private readonly byte[] _readBuf = new byte[64 * 1024];
    private int _rPos;
    private int _rEnd;
    // ★ 安全会话（KeyPair 档握手产物——非 null 且已激活才走记录层；激活点=Final 明文帧写完
    //   （队列序=写序——Final 是最后一条明文；Plaintext 档恒 null 零开销）
    private SecureRecordCodec? _records;
    private NodeId _tlsDeclaredRemote;   // mTLS：对端证书 SAN 声明身份（地址制应用层核对）
    private volatile bool _sealActive;

    /// <summary>控制帧队列容量（数据报/请求/心跳低频——32 富余；背压 = 满时写者 await）。</summary>
    private const int ControlQueueFrames = 32;

    /// <summary>出站帧（ArrayPool 租借随帧走——writer 写完归还；Delivery = 拒绝路径"已上线"信号，
    /// 仅握手 Error 帧携带——writer 写完置位，等待者据此才允许关闭连接）。</summary>
    private readonly record struct OutgoingFrame(byte[] Buffer, int Length, byte ProtocolId, TaskCompletionSource? Delivery = null,
        Action? OnWritten = null);   // 写完成回调（记录层激活点——Final 帧专用）

    /// <summary>拨号侧构造·成员制（对端已知——NodeId 较小方拨号，握手核对 Ack 身份一致）。</summary>
    /// <param name="owner">所属传输（注入面/选项/事件源）。</param>
    /// <param name="remote">期望的对端节点 ID（握手核对 Ack 一致）。</param>
    /// <param name="socket">已连接的 TCP socket（所有权随链路）。</param>
    /// <returns>拨号侧链路（随后执行 <see cref="HandshakeAsync"/>）。</returns>
    public static PeerLink DialKnown(ClusterTransport owner, NodeId remote, TcpClient socket) => new(owner, socket, isDialer: true, expectRemote: remote);

    /// <summary>拨号侧构造·地址制（对端身份未知——从握手的 Ack 得知，§4.1；有地址者拨号）。</summary>
    /// <param name="owner">所属传输（注入面/选项/事件源）。</param>
    /// <param name="socket">已连接的 TCP socket（所有权随链路）。</param>
    /// <returns>拨号侧链路（随后执行 <see cref="HandshakeAsync"/>）。</returns>
    public static PeerLink DialAddressed(ClusterTransport owner, TcpClient socket) => new(owner, socket, isDialer: true, expectRemote: null);

    /// <summary>监听侧构造（对端身份从握手的 Init 帧读得——连接准入见握手）。</summary>
    /// <param name="owner">所属传输（注入面/选项/事件源）。</param>
    /// <param name="socket">已接受的 TCP socket（所有权随链路）。</param>
    /// <returns>监听侧链路（随后执行 <see cref="HandshakeAsync"/>）。</returns>
    public static PeerLink Accept(ClusterTransport owner, TcpClient socket) => new(owner, socket, isDialer: false, expectRemote: null);

    private PeerLink(ClusterTransport owner, TcpClient socket, bool isDialer, NodeId? expectRemote)
    {
        _owner = owner;
        _isDialer = isDialer;
        _expectRemote = expectRemote;
        _socket = socket;
        _stream = socket.GetStream();
        _remote = expectRemote ?? NodeId.Empty;
        _controlQueue = Channel.CreateBounded<OutgoingFrame>(new BoundedChannelOptions(ControlQueueFrames)
        {
            FullMode = BoundedChannelFullMode.Wait,   // 满 = 写者 await（控制帧背压——低频面）
            SingleReader = true,
            SingleWriter = false,
        });
        _streamQueue = Channel.CreateBounded<OutgoingFrame>(new BoundedChannelOptions(owner.Options.WriteQueueFrames)
        {
            FullMode = BoundedChannelFullMode.Wait,   // 满 = 流写者 await（背压传导——对端消费慢则流挂）
            SingleReader = true,
            SingleWriter = false,
        });
    }

    internal NodeId Remote => _remote;
    internal bool IsEstablished => Volatile.Read(ref _state) == StateEstablished;

    /// <summary>协商后生效的特性位（双方都开——交集规则；握手完成后有效）。</summary>
    internal byte NegotiatedFeatures => Volatile.Read(ref _negotiatedFeatures);

    /// <summary>握手失败原因（对端拒绝/超时——ConnectAsync 外泄给调用方；成功时为 null）。</summary>
    internal string? HandshakeFailureReason { get; private set; }

    // ══ 握手（spec-12 §3.3 三步——Init/Ack/Final；超时 = options.HandshakeTimeout）══

    /// <summary>执行三步握手。false = 失败（已尽力 Error 告知/记录——连接随之关闭）。</summary>
    /// <param name="ct">取消令牌（叠加握手超时——<see cref="TransportOptions.HandshakeTimeout"/>）。</param>
    /// <returns>true = 握手建立（含 mTLS 升级/UDP 端点通告）；false = 握手失败（原因见 <see cref="HandshakeFailureReason"/>）。</returns>
    public async Task<bool> HandshakeAsync(CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_owner.Tunables.HandshakeTimeout);   // 二期-D6：运行时旋钮
        try
        {
            if (_owner.Security is { Mode: SecurityMode.MutualTls } tls)
                await UpgradeTlsAsync(tls, timeoutCts.Token).ConfigureAwait(false);   // TLS 先于应用层三步握手
            if (_isDialer) await HandshakeAsDialerAsync(timeoutCts.Token).ConfigureAwait(false);
            else await HandshakeAsListenerAsync(timeoutCts.Token).ConfigureAwait(false);
            Volatile.Write(ref _state, StateEstablished);
            if ((Volatile.Read(ref _negotiatedFeatures) & HandshakeFeatures.Keepalive) != 0)
                _keepalive = new KeepaliveTracker(_owner.Tunables.KeepaliveMaxUnanswered);   // 二期-D6：挂载时值（既有链路不回溯）
            await SendUdpEndpointAnnounceAsync(timeoutCts.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or FormatException or ArgumentException
            or OperationCanceledException or CryptographicException or System.Security.Authentication.AuthenticationException)
        {
            // ★ KeyPair 验签失败（CryptographicException）与 TLS 失败（AuthenticationException——错 CA/
            //   错 SAN/明文撞 TLS）统一收口（二期-A）：置 HandshakeFailureReason + NetView 上报 + 返回
            //   false——ConnectAsync 据此抛 NetIOException，原始异常不外逸（契约异常面）。
            HandshakeFailureReason = ex.Message;
            _owner.NetView?.OnHandshakeFailure(ex.Message);
            _owner.Logger?.LogDebug("握手失败（{Role} → {Remote}）：{Message}", _isDialer ? "拨号" : "监听", _remote, ex.Message);
            return false;
        }
    }

    /// <summary>TLS1.3 相互认证（mTLS 档——流升级：此后三步握手与全部帧走 TLS 通道；
    /// 应用层不再套记录层）。★ NodeId 绑定 = SAN <c>nid:</c> 条目（成员制预期身份比对；
    /// 地址制记录声明身份供应用层 Ack 比对）；证书链校验（自签 CA 显式传入，否则系统信任库）。</summary>
    private async Task UpgradeTlsAsync(SecurityOptions tls, CancellationToken ct)
    {
        var ssl = new SslStream(_stream, leaveInnerStreamOpen: false,
            (sender, certificate, chain, errors) => ValidateRemoteCertificate(tls, certificate, chain, errors));
        var optionsca = tls.CaCertificates;
        try
        {
            if (_isDialer)
            {
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = "tc-tier-node",   // 身份由 SAN nid 承担（非主机名校验）
                    ClientCertificates = new X509CertificateCollection { tls.Certificate! },
                    EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls13,
                }, ct).ConfigureAwait(false);
            }
            else
            {
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = tls.Certificate!,
                    ClientCertificateRequired = true,   // 相互认证——缺客户端证书拒绝
                    EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls13,
                }, ct).ConfigureAwait(false);
            }
            _stream = ssl;
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private bool ValidateRemoteCertificate(SecurityOptions tls, X509Certificate? certificate,
        X509Chain? chain, System.Net.Security.SslPolicyErrors errors)
    {
        if (certificate is null || chain is null) return false;
        // SAN nid 绑定：成员制预期身份精确比对；地址制记录声明身份（应用层 Ack 比对）
        if (!CertificateNodeId.IsBoundTo(certificate, _expectRemote, out var declared))
            return false;
        _tlsDeclaredRemote = declared;
        if (_expectRemote is null)
            _remote = declared;   // 地址制：TLS 层即得对端身份（Ack 侧核对同值）
        // 链校验：自签 CA 显式信任集；null = 系统信任库（SslPolicyErrors.None 直过）
        if (tls.CaCertificates is { } cas)
        {
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.AddRange(cas);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;   // 短期证书+轮换形态——吊销被"等它短命"取代
            return chain.Build(new X509Certificate2(certificate));
        }
        return errors == System.Net.Security.SslPolicyErrors.None;
    }

    private async Task HandshakeAsDialerAsync(CancellationToken ct)
    {
        var security = _owner.Security;
        ulong nonce = BinaryPrimitives.ReadUInt64LittleEndian(RandomNumberGenerator.GetBytes(8));
        var keyPairDial = security is { Mode: SecurityMode.KeyPair };
        using var ephemeral = keyPairDial ? NodeKeyPair.Generate() : null;

        // Init 载荷：定长 32B +（KeyPair 档）变长扩展段[临时公钥+签名]
        var initPayload = new byte[HandshakeCodec.InitPayloadSize
            + (keyPairDial ? SecureSession.InitExtensionSize : 0)];
        HandshakeCodec.WriteInit(initPayload, _owner.Self, FrameCodec.CurrentHeaderVersion, FrameCodec.CurrentHeaderVersion,
            _owner.AdvertisedFeatures, _owner.Options.ClusterTag, nonce, _owner.SecurityByte);
        if (keyPairDial && ephemeral is not null)
            SecureSession.EncodeInitExtension(security.OwnKey!, ephemeral, nonce,
                initPayload.AsSpan(HandshakeCodec.InitPayloadSize));
        await WriteFrameAsync(FrameKind.HandshakeInit, ChannelIds.Management, ProtocolIds.Management, initPayload, ct).ConfigureAwait(false);

        var ackFrame = await ReadFrameAsync(ct).ConfigureAwait(false) ?? throw new NetIOException("连接在握手期间关闭（等 Ack）。");
        var (header, payload) = ackFrame;
        if (header.Kind == FrameKind.HandshakeAck)
        {
            var ack = HandshakeCodec.ReadAck(payload.AsSpan(0, HandshakeCodec.AckPayloadSize));
            if (_expectRemote is { } expected && ack.NodeId != expected)
                throw new NetIOException($"Ack 身份不符：宣称 {ack.NodeId}，期望 {expected}（成员制身份静态已知）。");
            // ★ 拨号侧 mTLS 身份对称核对（二期-A 缺口回归——监听侧 Init 核对同款）：地址制 _expectRemote
            //   为空，服务端证书 SAN 声明身份必须 == Ack 宣称身份——防"合法证书冒名他节点"（MitM 中转）。
            if (_owner.Security?.Mode == SecurityMode.MutualTls && _tlsDeclaredRemote != NodeId.Empty && ack.NodeId != _tlsDeclaredRemote)
                throw new NetIOException($"Ack 身份 {ack.NodeId} ≠ 证书 SAN 声明 {_tlsDeclaredRemote}（mTLS 绑定违规——拨号侧对称核对）。");
            _remote = ack.NodeId;   // 地址制身份握手得知（§4.1——此前未知）；成员制同值核对后赋入
            if (ack.Nonce != nonce) throw new NetIOException("Ack nonce 回带不符（错配/重放）。");
            if (!HandshakeCodec.ClusterTagMatches(_owner.Options.ClusterTag, ack.ClusterTag))
                throw new NetIOException($"Ack 集群标签回显不符：收到 {ack.ClusterTag}，期望 {_owner.Options.ClusterTag}（错集群——fail-fast，spec-12 §3.3）。");
            if (ack.Version != FrameCodec.CurrentHeaderVersion) throw new NetIOException($"选定版本 {ack.Version} 非 {FrameCodec.CurrentHeaderVersion}。");
            CheckSecurityTier(ack.Security, "Ack");
            Volatile.Write(ref _negotiatedFeatures, (byte)(ack.Features & _owner.AdvertisedFeatures));   // 对端交集再与本端相与——双保险同值

            if (keyPairDial && ephemeral is not null && security is not null)
            {
                // Ack 扩展段（自报静态公钥）→ 验签派生会话 → 信任锚第二道校验（钉扎比对/TOFU 学习）
                var (session, responderStatic) = SecureSession.FinishAsInitiator(
                    security.OwnKey!, ephemeral, nonce, payload.AsSpan(HandshakeCodec.AckPayloadSize),
                    security.Protection);
                CheckTrustAnchor(security, ack.NodeId, responderStatic);
                var confirmTag = session.ComputeConfirmTag([]);   // Final = 密钥确认 MAC（明文握手帧承载）
                _owner.RegisterUdpAuthKey(ack.NodeId, session.DeriveUdpKey());   // UDP 报文认证键（会话派生）
                _records = new SecureRecordCodec(session);        // 建而未激活——Final 写完才 Seal 生效
                session.Dispose();   // 记录层独立持有密钥副本
                var finalReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await EnqueuePlaintextFrameAsync(FrameKind.HandshakeFinal, ChannelIds.Management,
                    ProtocolIds.Management, confirmTag, onWritten: () => { _sealActive = true; finalReady.TrySetResult(); }, ct).ConfigureAwait(false);
                await finalReady.Task.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);   // 激活点确认（防后续帧抢跑）
                return;   // Final 已发——跳过末尾的明文 Final 兜底
            }
        }
        else if (header.Kind == FrameKind.Error)
        {
            var reason = TransportError.TryDecode(payload, out _, out var detail) ? detail : "对端拒绝";
            throw new NetIOException($"对端握手拒绝：{reason}");
        }
        else throw new NetIOException($"握手期望 Ack，收到 Kind=0x{header.Kind:X2}。");

        // Plaintext 档兜底 Final（KeyPair 档已在 Ack 分支内发密钥确认 Final 后 return）
        await WriteFrameAsync(FrameKind.HandshakeFinal, ChannelIds.Management, ProtocolIds.Management,
            ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
    }

    /// <summary>安全档防降级核对（本端配置档 vs 对端到达帧声明档——不匹配即断连，永不机会主义升降级）。</summary>
    private void CheckSecurityTier(byte declared, string frameName)
    {
        var expected = _owner.SecurityByte;
        if (declared != expected)
            throw new NetIOException($"{frameName} 安全档不符：收到 {declared}，期望 {expected}（本端配置档 {(SecurityMode)expected}——fail-closed 防降级，spec-12 §3.4）。");
    }

    /// <summary>信任锚第二道校验（验签已证自报公钥持有者身份）：已绑定=比对（不符即冒充拒绝）；
    /// 未绑定=TOFU 学习（钉扎表不可写→拒绝——信任变更须改配置重新装配）。</summary>
    private static void CheckTrustAnchor(SecurityOptions security, NodeId peer, byte[] reportedStatic)
    {
        if (security.Trust is not { } trust)
            throw new NetIOException("KeyPair 档缺少信任锚配置（KeyPairPinned/KeyPairTofu）。");
        if (trust is ISupportsKeyRotation rotatable)
        {
            // ★ 二期-H1：多锚并存判定（current 或过渡窗内 previous）——轮换过渡期不误断
            if (!rotatable.IsTrusted(peer, reportedStatic))
                throw new NetIOException($"节点 {peer} 静态公钥与信任锚不符（冒充/换钥——拒绝断连，人工介入）。");
            return;
        }
        if (trust.TryGet(peer, out var anchored))
        {
            if (!anchored.Span.SequenceEqual(reportedStatic))
                throw new NetIOException($"节点 {peer} 静态公钥与信任锚不符（冒充/换钥——拒绝断连，人工介入）。");
            return;
        }
        try
        {
            trust.Save(peer, reportedStatic);   // TOFU 首连学习（签名已成立）
        }
        catch (NotSupportedException ex)
        {
            throw new NetIOException($"节点 {peer} 不在钉扎表（信任变更 = 改配置重新装配——spec-12 §3.4）。", ex);
        }
    }

    private async Task HandshakeAsListenerAsync(CancellationToken ct)
    {
        var initFrame = await ReadFrameAsync(ct).ConfigureAwait(false) ?? throw new NetIOException("连接在握手期间关闭（等 Init）。");
        var (header, payload) = initFrame;
        if (header.Kind != FrameKind.HandshakeInit)
        {
            await TrySendErrorAsync(TransportError.HandshakeViolation, $"握手完成前收到 Kind=0x{header.Kind:X2}", ct).ConfigureAwait(false);
            throw new NetIOException($"握手期望 Init，收到 Kind=0x{header.Kind:X2}（握手前数据帧 = Error + 断连）。");
        }

        // KeyPair 档载荷 = 定长 32B + 变长扩展段——定长前缀切片读（codec 按定长校验）
        var init = HandshakeCodec.ReadInit(payload.AsSpan(0, HandshakeCodec.InitPayloadSize));
        _remote = init.NodeId;
        if (_owner.Security?.Mode == SecurityMode.MutualTls && init.NodeId != _tlsDeclaredRemote)
            throw new NetIOException($"Init 身份 {init.NodeId} ≠ 证书 SAN 声明 {_tlsDeclaredRemote}（mTLS 绑定违规）。");
        if (!HandshakeCodec.ClusterTagMatches(_owner.Options.ClusterTag, init.ClusterTag))
        {
            await TrySendErrorAsync(TransportError.HandshakeViolation,
                $"集群标签不符：收到 {init.ClusterTag}，期望 {_owner.Options.ClusterTag}（错集群——fail-fast，spec-12 §3.3）", ct).ConfigureAwait(false);
            throw new NetIOException($"错集群连接：标签 {init.ClusterTag} ≠ {_owner.Options.ClusterTag}（握手期拒绝——§3.3）。");
        }
        if (init.NodeId == _owner.Self)
        {
            await TrySendErrorAsync(TransportError.HandshakeViolation, "自连（Init 身份 = 本端）", ct).ConfigureAwait(false);
            throw new NetIOException("入站连接 Init 身份 = 本端（自连违规）。");
        }
        if (init.NodeId.CompareTo(_owner.Self) > 0 && _owner.IsKnownPeer(init.NodeId))
        {
            // 拨号归属·成员制（§4.3）：较小方才拨号——成员表中较大方入站 = 双向对拨，握手期拒绝
            //（地址制入站不受 NodeId 大小约束——有地址者拨号，身份未知在握手后得知）。
            await TrySendErrorAsync(TransportError.HandshakeViolation,
                $"成员制拨号归属违规：{init.NodeId} 为较大方不应拨号（双向对播防御，spec-12 §4.3）", ct);
            throw new NetIOException($"成员制较大方入站违规：{init.NodeId}（较小方拨号——§4.3）。");
        }
        // ★ 入站黑名单（二期-C2 §5.3——动态成员治理）：被 RemovePeer 移除的成员重拨入 = 拒绝断连。
        if (_owner.IsInboundDenied(init.NodeId))
        {
            await TrySendErrorAsync(TransportError.HandshakeViolation,
                $"对端已被移除（入站黑名单——动态成员治理）", ct);
            throw new NetIOException($"入站连接被拒：{init.NodeId}（成员移除黑名单——§5.3）。");
        }
        if (!HandshakeCodec.TryNegotiateVersion(FrameCodec.CurrentHeaderVersion, FrameCodec.CurrentHeaderVersion,
                init.MinVersion, init.MaxVersion, out _))
        {
            await TrySendErrorAsync(TransportError.VersionRejected,
                $"本端 v{FrameCodec.CurrentHeaderVersion} 与对端 {init.MinVersion}..{init.MaxVersion} 无交集", ct).ConfigureAwait(false);
            throw new NetIOException($"版本协商无交集：本端 v{FrameCodec.CurrentHeaderVersion}，对端 {init.MinVersion}..{init.MaxVersion}。");
        }
        if (init.Security != _owner.SecurityByte)
        {
            await TrySendErrorAsync(TransportError.SecurityRejected,
                $"安全档不符：收到 {init.Security}，本端配置 {_owner.SecurityByte}（fail-closed 防降级——spec-12 §3.4）", ct).ConfigureAwait(false);
            throw new NetIOException("握手安全档与本端配置不符（防降级 fail-closed——永不机会主义升降级）。");
        }

        // KeyPair 档：Init 扩展段验签（自报静态公钥）→ 信任锚第二道 → Ack 扩展段携带密钥材料
        var security = _owner.Security;
        var keyPairListen = security is { Mode: SecurityMode.KeyPair } && init.Security == HandshakeSecurity.KeyPair;
        using var respEphemeral = keyPairListen ? NodeKeyPair.Generate() : null;
        (byte[] InitiatorStatic, Func<NodeKeyPair, ulong, byte[]> AckExt, Func<NodeKeyPair, ulong, SecureSession> Finish)?
            responder = null;
        if (keyPairListen && security is not null && respEphemeral is not null)
        {
            try
            {
                responder = SecureSession.StartAsResponder(security.OwnKey!,
                    payload.AsSpan(HandshakeCodec.InitPayloadSize), init.Nonce, security.Protection);
                CheckTrustAnchor(security, init.NodeId, responder.Value.InitiatorStatic);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                await TrySendErrorAsync(TransportError.SecurityRejected, $"KeyPair 握手材料验证失败：{ex.Message}", ct).ConfigureAwait(false);
                throw new NetIOException($"Init 密钥材料验证失败（{ex.Message}）——Error + 断连。", ex);
            }
        }

        var negotiatedFeatures = HandshakeCodec.IntersectFeatures(_owner.AdvertisedFeatures, init.Features);
        Volatile.Write(ref _negotiatedFeatures, negotiatedFeatures);
        var respNonce = RandomNumberGenerator.GetBytes(8);
        var respNonceValue = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(respNonce);
        var ackPayload = new byte[HandshakeCodec.AckPayloadSize
            + (responder is not null ? SecureSession.AckExtensionSize : 0)];
        HandshakeCodec.WriteAck(ackPayload, _owner.Self, FrameCodec.CurrentHeaderVersion,
            negotiatedFeatures, init.ClusterTag, init.Nonce, _owner.SecurityByte);
        if (responder is not null && respEphemeral is not null)
            responder.Value.AckExt(respEphemeral, respNonceValue)
                .CopyTo(ackPayload.AsSpan(HandshakeCodec.AckPayloadSize));
        await WriteFrameAsync(FrameKind.HandshakeAck, ChannelIds.Management, ProtocolIds.Management, ackPayload, ct).ConfigureAwait(false);

        var finalFrame = await ReadFrameAsync(ct).ConfigureAwait(false) ?? throw new NetIOException("连接在握手期间关闭（等 Final）。");
        if (finalFrame.Header.Kind != FrameKind.HandshakeFinal)
            throw new NetIOException($"握手期望 Final，收到 Kind=0x{finalFrame.Header.Kind:X2}。");
        if (responder is not null && respEphemeral is not null && security is not null)
        {
            // Final = 密钥确认 MAC（32B）——验证失败 = 密钥不一致/篡改，断连
            var session = responder.Value.Finish(respEphemeral, respNonceValue);
            if (finalFrame.Payload.Length != 32 || !session.VerifyConfirmTag([], finalFrame.Payload))
            {
                session.Dispose();
                throw new NetIOException("Final 密钥确认失败（会话密钥不一致——Error + 断连）。");
            }
            _owner.RegisterUdpAuthKey(init.NodeId, session.DeriveUdpKey());   // UDP 报文认证键（会话派生）
            _records = new SecureRecordCodec(session);
            _sealActive = true;   // 读侧单线程——Final 已验，下一条起即记录
            session.Dispose();   // 记录层独立持有密钥副本
        }
    }

    /// <summary>握手期尽力发送 Error 帧（发送失败不再连锁——连接本就要关）。
    /// <para>★ 等待"已上线"再返回（有界 1s）：写者"入队即返回"形态下，握手失败路径立刻
    /// Close 会把未写的 Error 帧随写循环 finally 归还缓冲——拨号方只见"连接在握手期间关闭"，
    /// 归属违规/错集群/版本/安全的判别文本全在 Error 帧里（同族 flaky 三次复发的根因）。</para></summary>
    private async Task TrySendErrorAsync(byte code, string? detail, CancellationToken ct)
    {
        try
        {
            var delivery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await WriteFrameAsync(FrameKind.Error, ChannelIds.Management, ProtocolIds.Management,
                TransportError.Encode(code, detail), ct, delivery).ConfigureAwait(false);
            await delivery.Task.WaitAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException or TimeoutException)
        {
            _owner.Logger?.LogDebug("Error 帧发送失败（连接已在关闭）：{Message}", ex.Message);
        }
    }

    /// <summary>
    /// 握手完成后的 UDP 数据报端点通告（特性位 bit1——Negotiate 帧承载，管理通道）。
    /// 尽力而为：通告失败/未协商 = 对端回落 TCP 承载，尽力送达语义不变。
    /// </summary>
    private async Task SendUdpEndpointAnnounceAsync(CancellationToken ct)
    {
        if ((Volatile.Read(ref _negotiatedFeatures) & HandshakeFeatures.UdpEndpoint) == 0) return;
        var tcpLocal = (IPEndPoint?)_socket.Client.LocalEndPoint;
        if (tcpLocal is null || _owner.BuildUdpAnnounce(tcpLocal) is not { } udpLocal) return;

        var payload = NegotiateCodec.EncodeUdpEndpoint(udpLocal);
        try
        {
            await WriteFrameAsync(FrameKind.Negotiate, ChannelIds.Management, ProtocolIds.Management, payload, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            _owner.Logger?.LogDebug("UDP 端点通告发送失败（对端回落 TCP 承载）：{Remote} {Message}", _remote, ex.Message);
        }
    }

    /// <summary>
    /// 流式会话帧发送（spec-12 §5.3——ChannelId = 发送方本地会话号 1..254；
    /// 不走注入面——流式对抗由 Reset/断连驱动，帧级注入是数据报/请求回调面）。
    /// </summary>
    /// <param name="kind">帧种类（<see cref="FrameKind"/> Stream* 常量）。</param>
    /// <param name="sessionId">流式会话号（ChannelId = 发送方本地会话号 1..254）。</param>
    /// <param name="protocolId">协议域 ID。</param>
    /// <param name="payload">帧载荷。</param>
    /// <param name="ct">取消令牌（入队等待可取消）。</param>
    /// <returns>完成时帧已入写队列（写循环依序上线；流帧走低优先队列——背压只停 writer 不锁写者）。</returns>
    public ValueTask SendStreamFrameAsync(byte kind, byte sessionId, byte protocolId, ReadOnlyMemory<byte> payload, CancellationToken ct)
        => new(WriteFrameAsync(kind, sessionId, protocolId, payload, ct));

    /// <summary>入站请求帧处理（拆 CorrId 前缀 → broker 分发；违规载荷丢弃计数）。
    /// ★ 二期-I4：已协商 Trace 位则识别并剥离 trace 前缀（不匹配原样透传——向前兼容）。</summary>
    private void OnRequestFrame(byte protocolId, ReadOnlyMemory<byte> payload)
    {
        if (!RequestCodec.TryRead(payload.Span, out var corrId, out _))
        {
            _owner.NetView?.OnDatagramDropped(protocolId, "unknown_kind");
            return;
        }
        var body = payload[RequestCodec.PrefixSize..];
        byte[]? traceWire = null;
        if ((Volatile.Read(ref _negotiatedFeatures) & HandshakeFeatures.Trace) != 0
            && SpanContextCodec.TryStripPrefix(body.Span, out var spanContext, out var consumed))
        {
            traceWire = new byte[SpanContextCodec.ContextSize];
            body.Span[1..SpanContextCodec.PrefixSize].CopyTo(traceWire);
            _ = spanContext;   // 语义由 tracer 解释——本层只搬运字节
            body = body[consumed..];
        }
        _owner.DispatchRequest(_remote, protocolId, corrId, body,
            new LinkReplyContext(this, protocolId, corrId), traceWire);
    }

    /// <summary>Negotiate 帧处理（管理通道——UDP 端点登记；未知 Tag 向前兼容丢弃）。</summary>
    private void OnNegotiate(ReadOnlySpan<byte> payload)
    {
        if (!NegotiateCodec.TryReadTag(payload, out var tag) || tag != NegotiateCodec.TagUdpEndpoint) return;
        if (!NegotiateCodec.TryReadUdpEndpoint(payload, out var endpoint))
        {
            _owner.Logger?.LogDebug("Negotiate UDP 端点载荷违规（丢弃）：{Remote}", _remote);
            return;
        }
        _owner.StoreUdpEndpoint(_remote, endpoint);
    }

    // ══ 数据通道 ══

    /// <summary>数据报发送（尽力送达——介质故障吞掉并关链路；注入拦截在此面，握手/管理帧不注入）。</summary>
    /// <param name="protocolId">协议域 ID。</param>
    /// <param name="payload">载荷。</param>
    /// <param name="ct">取消令牌（注入延迟/入队等待可取消）。</param>
    /// <returns>完成时数据报已注入面放行并入写队列（注入丢弃 = 静默丢弃；介质写失败 = 关链路，不外泄）。</returns>
    public async ValueTask SendDatagramAsync(byte protocolId, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (!await _owner.Injector.ApplyDeliveryDelay(_owner.Self, _remote, ct).ConfigureAwait(false)) return;   // 丢包/延迟/乱序（false = 注入丢弃）
        try
        {
            await WriteFrameAsync(FrameKind.Datagram, ChannelIds.Datagram, protocolId, payload, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            _owner.Logger?.LogDebug("数据报发送失败（链路断开）：{Remote} {Message}", _remote, ex.Message);
            Close();
        }
    }

    /// <summary>
    /// 保活循环（§4.5——协商出 bit0 才启动：周期 Keepalive 帧（管理通道，不走注入面）；
    /// 连续无入站保活达上限 = 断连（半开死链唯一检测手段），拨号方退避重连）。
    /// <para>★ 专用线程 + AsyncPump 泵域载体（tcp-keepalive-{remote}）——域内 await 不写
    /// ConfigureAwait(false)，续体回流泵线程（池续体丢失 = 保活停 = 半开死链不检测）。</para>
    /// </summary>
    /// <param name="ct">取消令牌（传输停止时终止循环——常态退出不视为故障）。</param>
    /// <returns>循环退出（断连/取消/介质故障）时完成的任务。</returns>
    public async Task RunKeepaliveAsync(CancellationToken ct)
    {
        var tracker = _keepalive!;
        var interval = _owner.Tunables.KeepaliveInterval;   // 二期-D6：每拍重读——运行时更新下一拍生效
        try
        {
            while (!ct.IsCancellationRequested && IsEstablished)
            {
                await Task.Delay(interval, ct);
                if (!IsEstablished) break;
                if (tracker.OnSent())
                {
                    _owner.NetView?.OnKeepaliveDrop(_remote.ToString());
                    _owner.Logger?.LogDebug("保活超时断连：{Remote}（连续 {Max} 周期无入站保活）",
                        _remote, _owner.Tunables.KeepaliveMaxUnanswered);
                    Close();
                    break;
                }
                await WriteFrameAsync(FrameKind.Keepalive, ChannelIds.Management, ProtocolIds.Management,
                    ReadOnlyMemory<byte>.Empty, ct);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // 连接已断/传输停止——常态退出
        }
    }

    /// <summary>
    /// 请求回调帧发送（spec-12 §5.2——Request 帧 [CorrId 8B][payload]，数据报通道承载）：
    /// 注入丢弃 = 静默（对端超时——真实网络丢包语义，发送方无从得知，与 InProcess 同构）；
    /// 介质写失败关链路并外泄（调用方有应答期待）。
    /// </summary>
    /// <param name="protocolId">协议域 ID。</param>
    /// <param name="corrId">请求关联 ID（8B——应答据此关联回发）。</param>
    /// <param name="payload">请求载荷。</param>
    /// <param name="ct">取消令牌（注入延迟/入队等待可取消）。</param>
    /// <param name="traceContext">trace 上下文（二期-I4 跨节点 trace 串联；仅对协商出 Trace 位的对端随帧上线，null = 无）。</param>
    /// <returns>完成时请求帧已入写队列（注入丢弃 = 静默——对端超时语义）。</returns>
    public async ValueTask SendRequestFrameAsync(byte protocolId, ulong corrId, ReadOnlyMemory<byte> payload,
        CancellationToken ct, byte[]? traceContext = null)
    {
        // ★ 二期-I4：trace 前缀仅对协商出 Trace 位的对端上线（混版 N-1 载荷零变化）
        if (traceContext is not null
            && (Volatile.Read(ref _negotiatedFeatures) & HandshakeFeatures.Trace) != 0)
            payload = SpanContextCodec.AttachPrefix(payload, traceContext);
        if (!await _owner.Injector.ApplyDeliveryDelay(_owner.Self, _remote, ct).ConfigureAwait(false)) return;
        await EnqueueFrameAsync(EncodeCorrIdFrame(FrameKind.Request, protocolId, corrId, payload), streamFrame: false, ct)
            .ConfigureAwait(false);
    }

    /// <summary>应答帧发送（ReplyContext 回程——[CorrId 8B][payload]；注入面与请求方向对称，
    /// 应答回程丢/延迟 = 发起端超时或重发）。</summary>
    private async ValueTask SendResponseFrameAsync(byte protocolId, ulong corrId, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (!await _owner.Injector.ApplyDeliveryDelay(_remote, _owner.Self, ct).ConfigureAwait(false)) return;
        await EnqueueFrameAsync(EncodeCorrIdFrame(FrameKind.Response, protocolId, corrId, payload), streamFrame: false, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// CorrId 帧单缓冲编码（审查 P2——省前缀中转数组与一次全量拷贝）：CorrId+payload 直写
    /// 租借帧缓冲的载荷区，<c>FrameCodec.Encode</c> 以"源 = 目标后缀"收口
    /// （Span.CopyTo = memmove 语义，自重叠安全；载荷 CRC 在自拷贝前计算——值正确）。
    /// </summary>
    private OutgoingFrame EncodeCorrIdFrame(byte kind, byte protocolId, ulong corrId, ReadOnlyMemory<byte> payload)
    {
        var frameLength = FrameCodec.HeaderSize + RequestCodec.PrefixSize + payload.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(frameLength);
        var frame = buffer.AsSpan(0, frameLength);
        var payloadSpan = frame[FrameCodec.HeaderSize..];
        RequestCodec.EncodeInto(corrId, payload.Span, payloadSpan);
        var written = FrameCodec.Encode(kind, ChannelIds.Datagram, protocolId, payloadSpan, frame);
        return new OutgoingFrame(buffer, written, protocolId);
    }

    /// <summary>
    /// 请求回调应答上下文（回程 = 本链路 Response 帧——对端身份与协议域随连接/请求携带）。
    /// </summary>
    private sealed class LinkReplyContext(PeerLink link, byte protocolId, ulong corrId) : IReplyContext
    {
        public NodeId Peer => link._remote;
        public byte ProtocolId => protocolId;
        public ulong CorrelationId => corrId;

        /// <summary>回发请求应答（本链路 Response 帧——[CorrId 8B][payload]）。</summary>
        /// <param name="payload">应答载荷（空应答合法）。</param>
        /// <param name="ct">取消令牌（入队等待可取消）。</param>
        /// <returns>完成时应答帧已入写队列（回程注入丢弃/链路断 = 发起端超时或重发语义）。</returns>
        public ValueTask ReplyAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default)
            => link.SendResponseFrameAsync(protocolId, corrId, payload, ct);
    }

    /// <summary>接收循环（Established 后——数据报分发/请求回调/管理帧处理；退出即断连）。
    /// <para>★ 专用线程 + AsyncPump 泵域载体（拨号/入站握手链/直连各自线程）——域内 await 不写
    /// ConfigureAwait(false)，续体回流泵线程（池续体丢失 = 接收停 = 链路僵死，2026-09-03 活性判例）。</para></summary>
    /// <param name="ct">取消令牌（传输停止时终止循环）。</param>
    /// <returns>循环退出（对端断开/帧损坏/取消）时完成的任务——退出即断连。</returns>
    public async Task RunReceiveAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                (FrameHeader Header, byte[] Payload)? frame;
                if (_records is not null)
                {
                    frame = await ReadSecureFrameAsync(ct);   // 安全会话：记录层整帧（冷路径——同旧形态）
                    if (frame is null) break;
                }
                else
                {
                    // ★ 明文热路径——同步解析（零箱），唯一常态挂起点 = 缓冲不足时的 FillAsync（~每批一次）
                    var take = TryTakeFrame(out var nextHeader, out var nextPayload);
                    if (take == FrameTake.NeedMore)
                    {
                        if (!await FillAsync(ct)) break;      // 流关闭——断连
                        continue;
                    }
                    if (take == FrameTake.Corrupt) break;     // 头/载 CRC 失败（已计数）——断连
                    if (take == FrameTake.Large)
                    {
                        frame = await ReadLargeFrameAsync(nextHeader, ct);   // 大帧绕缓冲（低频）
                        if (frame is null) break;
                    }
                    else
                    {
                        frame = (nextHeader, nextPayload!);
                    }
                }
                var (header, payload) = frame.Value;
                switch (header.Kind)
                {
                    case FrameKind.Error:
                        if (TransportError.TryDecode(payload, out var errorCode, out var errorDetail))
                            _owner.Logger?.LogDebug("对端 Error：{Remote} {Code} {Detail}", _remote, TransportError.Describe(errorCode), errorDetail);
                        break;                                 // 对端拒绝告知 → 断连（拨号方退避重连）
                    case FrameKind.Keepalive:
                        _keepalive?.OnReceived();              // 入站保活 = 对端活性证明——归零
                        break;
                    case FrameKind.Negotiate when header.ChannelId == ChannelIds.Management:
                        OnNegotiate(payload);
                        break;
                    case FrameKind.Request when header.ChannelId == ChannelIds.Datagram:
                        OnRequestFrame(header.ProtocolId, payload);
                        break;
                    case FrameKind.Response when header.ChannelId == ChannelIds.Datagram:
                        if (RequestCodec.TryRead(payload.AsSpan(), out var corrId, out _))
                            _owner.OnResponseArrived(corrId, payload.AsMemory(RequestCodec.PrefixSize));
                        else
                            _owner.NetView?.OnDatagramDropped(header.ProtocolId, "unknown_kind");   // 载荷短于 CorrId 前缀 = 违规帧
                        break;
                    case FrameKind.Datagram when header.ChannelId == ChannelIds.Datagram:
                        _owner.DispatchDatagram(_remote, header.ProtocolId, payload);
                        break;
                    case FrameKind.StreamOpen when ChannelIds.IsStream(header.ChannelId):
                        await _owner.OnStreamOpen(this, header.ChannelId, header.ProtocolId, payload, ct);   // 二期-C1：openPayload 透传
                        break;
                    case FrameKind.StreamAccept when ChannelIds.IsStream(header.ChannelId):
                        if (payload.Length >= 1)
                            _owner.OnStreamAccept(this, header.ChannelId, header.ProtocolId, payload[0]);   // 载荷 = 发起端号回显
                        break;
                    case FrameKind.StreamData when ChannelIds.IsStream(header.ChannelId):
                        await _owner.OnStreamData(this, header.ChannelId, payload);   // 缓冲满 await = 背压传导
                        break;
                    case FrameKind.StreamEnd when ChannelIds.IsStream(header.ChannelId):
                        _owner.OnStreamEnd(this, header.ChannelId);
                        break;
                    case FrameKind.StreamReset when ChannelIds.IsStream(header.ChannelId):
                        _owner.OnStreamReset(this, header.ChannelId);
                        break;
                    case FrameKind.StreamAck when ChannelIds.IsStream(header.ChannelId):
                        _owner.OnStreamAck(this, header.ChannelId);   // 窗口确认——发起端在途-1
                        break;
                    default:
                        _owner.NetView?.OnDatagramDropped(header.ProtocolId, "unknown_kind");   // 未知种类=丢弃+计数，不断连
                        break;
                }

                if (header.Kind == FrameKind.Error) break;
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // 连接关闭/对端断开——常态退出
        }
        finally
        {
            Close();
        }
    }

    // ══ 帧收发原语 ══

    /// <summary>流式五帧判别（低优先队列——流写背压不挤压控制面）。</summary>
    private static bool IsStreamKind(byte kind) => kind is FrameKind.StreamOpen or FrameKind.StreamAccept
        or FrameKind.StreamData or FrameKind.StreamEnd or FrameKind.StreamReset;

    /// <summary>
    /// 帧写入（编码 → 按种类分流双优先队列入队——await = 队列背压，非 socket 写完成）。
    /// <para>★ 队列化根因修复：旧形态写锁覆盖 await socket 写——流写背压挂起时写锁永不放，
    ///   数据报/心跳/请求回调全链路协议域饿死（dumpasync 实锤：数据报写者排队等锁永等）。
    ///   双优先：控制帧/数据报/请求/心跳走高优先队列（插队有界到达——raft 心跳不饿死）；
    ///   流帧走低优先（背压只停流写者）。帧序 = writer 单循环（高优先 drain 后再取低优先）。</para>
    /// </summary>
    private Task WriteFrameAsync(byte kind, byte channelId, byte protocolId, ReadOnlyMemory<byte> payload, CancellationToken ct,
        TaskCompletionSource? delivery = null)
    {
        int frameLength = FrameCodec.HeaderSize + payload.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(frameLength);
        int written = FrameCodec.Encode(kind, channelId, protocolId, payload.Span, buffer.AsSpan(0, frameLength));
        return EnqueueFrameAsync(new OutgoingFrame(buffer, written, protocolId, delivery), IsStreamKind(kind), ct);
    }

    /// <summary>入队（携带写完成回调——Final 帧激活记录层；写循环按序调用）。</summary>
    private Task EnqueuePlaintextFrameAsync(byte kind, byte channelId, byte protocolId, ReadOnlyMemory<byte> payload,
        Action onWritten, CancellationToken ct)
    {
        int frameLength = FrameCodec.HeaderSize + payload.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(frameLength);
        int written = FrameCodec.Encode(kind, channelId, protocolId, payload.Span, buffer.AsSpan(0, frameLength));
        return EnqueueFrameAsync(new OutgoingFrame(buffer, written, protocolId, OnWritten: onWritten), IsStreamKind(kind), ct);
    }

    /// <summary>入队（缓冲所有权随帧走——入队失败归还；await = 队列背压非 socket 写）。</summary>
    private async Task EnqueueFrameAsync(OutgoingFrame frame, bool streamFrame, CancellationToken ct)
    {
        var queue = streamFrame ? _streamQueue : _controlQueue;
        try
        {
            await queue.Writer.WriteAsync(frame, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ChannelClosedException or ObjectDisposedException)
        {
            ArrayPool<byte>.Shared.Return(frame.Buffer);
            throw new NetIOException("链路已关闭（写队列终止）。", ex);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(frame.Buffer);
            throw;
        }
    }

    /// <summary>
    /// 写循环（单 writer——高优先队列先 drain、低优先流帧次之；socket 写失败 = 断连）。
    /// 专用线程 + AsyncPump 泵域（ClusterTransport——tcp-write-{endpoint}）经创建点
    /// StartWriteLoop 起线（握手前必须已起——握手帧走队列）。域内 await 不写
    /// ConfigureAwait(false)——续体回流泵线程（池续体丢失 = 写循环停 = 出站全断）。
    /// <para>★ 批聚合（drain-nowait）：非阻塞取尽两队列已就绪帧，拷贝聚合后一次 socket 写
    ///   ——syscall 1/帧 → 1/批（请求回调洪水下出站积压自然成批）。不引入等待延迟
    ///   （有帧即写；两队列空 = WhenAny 挂起，聚合只吃队列里已有的）。源缓冲拷入聚合缓冲后
    ///   立即归还 ArrayPool（池周转更快，不等 socket 写完成）；超大帧（&gt; 聚合上限）单帧
    ///   直写零拷贝；封批时放不下的帧局部暂存下轮优先（不回队——bounded 队列满则丢帧
    ///   且控制帧降级序）。</para>
    /// </summary>
    /// <param name="ct">取消令牌（传输停止时终止循环）。</param>
    /// <returns>循环退出（socket 写失败/取消/队列终止——断连）时完成的任务。</returns>
    public async Task RunWriteLoopAsync(CancellationToken ct)
    {
        const int MaxCoalesceFrames = 64;
        const int MaxCoalesceBytes = 256 * 1024;
        byte[]? coalesce = null;
        var wires = new List<WireBytes>(MaxCoalesceFrames);   // 本批上线字节（Buf 归还义务随批）
        WireBytes carry = default;   // 封批时未入批的（下轮 drain 优先——序保持；非 bounded 回队）
        try
        {
            while (!ct.IsCancellationRequested)
            {
                wires.Clear();
                var length = 0;
                // 非阻塞 drain（carry 优先 → control → stream——帧序 = 优先级序保持）；
                // ★ 安全会话：帧经记录层封装（Seal 按取出顺序保计数器序）后为上线字节
                while (wires.Count < MaxCoalesceFrames)
                {
                    WireBytes wire;
                    if (carry != default) { wire = carry; carry = default; }
                    else if (TakeWire(_controlQueue, _records, _sealActive) is { Length: > 0 } controlWire) wire = controlWire;
                    else if (TakeWire(_streamQueue, _records, _sealActive) is { Length: > 0 } streamWire) wire = streamWire;
                    else break;
                    if (wire.Length > MaxCoalesceBytes)
                    {
                        // 超大帧（流式 bulk）不聚合：已有批先封批下轮直写；无批单帧直写（零拷贝）
                        if (length > 0) { carry = wire; break; }
                        wires.Add(wire);
                        await FlushAsync(wire.Buf.AsMemory(wire.Offset, wire.Length), wires, ct);
                        goto Drained;
                    }
                    coalesce ??= new byte[64 * 1024];
                    if (length + wire.Length > coalesce.Length)
                    {
                        if (wires.Count == 0)
                            coalesce = new byte[BitOperations.RoundUpToPowerOf2((uint)wire.Length)];   // 首帧即大——扩容（≤ 上限已判）
                        else { carry = wire; break; }   // 放不下——封批
                    }
                    wire.Buf.AsSpan(wire.Offset, wire.Length).CopyTo(coalesce.AsSpan(length));
                    length += wire.Length;
                    wires.Add(wire);   // 归还义务：写完成 finally（Seal 产物/明文帧缓冲统一）
                }
                if (length > 0)
                    await FlushAsync(coalesce.AsMemory(0, length), wires, ct);
                else if (carry == default)
                {
                    // 两队列空——挂起等任一可读（无忙转；有积压时不布等待任务）
                    var controlPending = _controlQueue.Reader.WaitToReadAsync(ct).AsTask();
                    var streamPending = _streamQueue.Reader.WaitToReadAsync(ct).AsTask();
                    await Task.WhenAny(controlPending, streamPending);
                }
            Drained: ;
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException or ChannelClosedException)
        {
            // socket 写失败/取消/队列终止——断连（写路径失败 = 链路失效）
        }
        finally
        {
            Close();
            // 关闭后残留归还（channel complete 后未写帧的缓冲不泄漏）
            if (carry != default) ArrayPool<byte>.Shared.Return(carry.Buf);
            while (_controlQueue.Reader.TryRead(out var leftover))
                ArrayPool<byte>.Shared.Return(leftover.Buffer);
            while (_streamQueue.Reader.TryRead(out var leftover))
                ArrayPool<byte>.Shared.Return(leftover.Buffer);
        }

        // ── 局部：取一帧转上线字节（安全会话 = Seal 封装；返回 default = 队列空）──
        static WireBytes TakeWire(Channel<OutgoingFrame> queue, SecureRecordCodec? records, bool sealActive)
        {
            if (!queue.Reader.TryRead(out var frame)) return default;
            if (records is null || !sealActive)
                return new WireBytes(frame.Buffer, 0, frame.Length, frame.ProtocolId, frame.Delivery, OnWritten: frame.OnWritten);
            var sealedBytes = records.Seal(frame.Buffer.AsSpan(0, frame.Length));
            ArrayPool<byte>.Shared.Return(frame.Buffer);   // 帧已封装——立即归还（池周转不等写完成）
            return new WireBytes(sealedBytes, 0, sealedBytes.Length, frame.ProtocolId, frame.Delivery,
                Rented: false);   // Seal 产物非池租借——写完成不归还（GC 管；池化归后续优化）
        }

        // ── 局部：写一批上线字节（聚合缓冲段 ∥ 单段直写）+ 事后采样/Delivery/归还 ──
        async Task FlushAsync(Memory<byte> bytes, List<WireBytes> batch, CancellationToken token)
        {
            try
            {
                await _stream.WriteAsync(bytes, token).ConfigureAwait(false);
                foreach (var w in batch)
                {
                    w.OnWritten?.Invoke();   // 记录层激活点（Final 明文帧写完——此后 Seal 生效）   // 记录层激活点（Final 明文帧写完——此后 Seal 生效）
                    w.Delivery?.TrySetResult();   // Error 帧等待者放行——此批已真正上线
                    if (_owner.NetView is { } net && net.ShouldSampleFrame()) net.OnFrameSent(w.ProtocolId);
                }
            }
            finally
            {
                foreach (var w in batch)
                {
                    if (!w.Rented) continue;   // Seal 产物已在 TakeWire 归还源——聚合字节随批归还
                    ArrayPool<byte>.Shared.Return(w.Buf);
                }
            }
        }
    }

    /// <summary>上线字节段（明文帧缓冲 ∥ 安全记录 Seal 产物——写完成归还义务统一）。</summary>
    private readonly record struct WireBytes(byte[] Buf, int Offset, int Length, byte ProtocolId,
        TaskCompletionSource? Delivery = null, bool Rented = true, Action? OnWritten = null);

    /// <summary>读整帧。null = 头损坏（CRC/版本/长度上限）/载损坏/流关闭——调用方断连。
    /// <para>★ 同步解析 + 显式挂起点（W3 乙案去箱——旧形态整体 async 使每帧硬付一个状态机箱，
    ///   而批填充后缓冲内多帧解析纯同步完成，唯一常态挂起点是 <see cref="FillAsync"/> ~每批一次）：
    ///   接收热循环走 <see cref="TryTakeFrame"/> 同步解析（零箱），握手冷路径沿用本包装。</para></summary>
    private async Task<(FrameHeader Header, byte[] Payload)?> ReadFrameAsync(CancellationToken ct)
    {
        if (_records is not null)
            return await ReadSecureFrameAsync(ct).ConfigureAwait(false);   // 安全会话：切记录层
        while (true)
        {
            var take = TryTakeFrame(out var header, out var payload);
            if (take == FrameTake.Got) return (header, payload!);
            if (take == FrameTake.Corrupt) return null;
            if (take == FrameTake.Large)
            {
                var large = await ReadLargeFrameAsync(header, ct).ConfigureAwait(false);   // 大帧绕缓冲
                return large;   // null = CRC 失败（已计数）/流关闭——调用方断连
            }
            if (!await FillAsync(ct).ConfigureAwait(false)) return null;   // 流关闭
        }
    }

    /// <summary>帧解析状态（同步解析段产物——<see cref="TryTakeFrame"/>）。</summary>
    private enum FrameTake : byte
    {
        /// <summary>完整帧已取出（缓冲推进）。</summary>
        Got,
        /// <summary>数据不足——填充后重试。</summary>
        NeedMore,
        /// <summary>头/载损坏（CRC/版本/长度上限——已计数）。</summary>
        Corrupt,
        /// <summary>大帧（载荷超读缓冲——头已验，经 <see cref="ReadLargeFrameAsync"/> 直读）。</summary>
        Large,
    }

    /// <summary>同步取帧（缓冲内解析——零箱零分配；载荷拷入独立数组，消费方跨 await 持有的既有契约不变）。</summary>
    private FrameTake TryTakeFrame(out FrameHeader header, out byte[]? payload)
    {
        header = default;
        payload = null;
        if (_rEnd - _rPos < FrameCodec.HeaderSize) return FrameTake.NeedMore;
        if (!FrameCodec.TryReadHeader(_readBuf.AsSpan(_rPos), out header))
        {
            _owner.NetView?.OnCrcFailure();
            return FrameTake.Corrupt;
        }
        var total = FrameCodec.HeaderSize + (int)header.PayloadLength;
        if (total > _readBuf.Length) return FrameTake.Large;
        if (_rEnd - _rPos < total) return FrameTake.NeedMore;   // 帧未到齐（头完整、载荷残余在路上）
        var buf = new byte[header.PayloadLength];
        _readBuf.AsSpan(_rPos + FrameCodec.HeaderSize, (int)header.PayloadLength).CopyTo(buf);
        _rPos += total;
        if (!FrameCodec.VerifyPayload(header, buf))
        {
            _owner.NetView?.OnCrcFailure();
            return FrameTake.Corrupt;
        }
        if (_owner.NetView is { } net && net.ShouldSampleFrame()) net.OnFrameReceived(header.ProtocolId);
        payload = buf;
        return FrameTake.Got;
    }

    /// <summary>安全会话读整帧：读缓冲切记录（[长度 4B][计数器 8B][载荷]）→ <see cref="SecureRecordCodec.Open"/>
    /// 解包 → 明文整帧一步解码。null = 解密/认证/帧验证失败或流关闭——调用方断连。</summary>
    private async Task<(FrameHeader Header, byte[] Payload)?> ReadSecureFrameAsync(CancellationToken ct)
    {
        var records = _records ?? throw new InvalidOperationException("安全会话未初始化（记录层缺失）。");
        while (true)
        {
            if (_rEnd - _rPos >= SecureRecordCodec.RecordHeaderSize)
            {
                var bodyLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(_readBuf.AsSpan(_rPos, 4));
                if (bodyLength <= 0 || bodyLength > SecureRecordCodec.MaxRecordBytes)
                    return null;   // 畸形记录长度——断连
                var total = SecureRecordCodec.RecordHeaderSize + bodyLength;
                byte[] recordBody;
                if (total > _readBuf.Length)
                {
                    // 大记录绕缓冲直读（残余 + 补齐——独立数组交付 Open）
                    recordBody = new byte[bodyLength];
                    var available = _rEnd - _rPos - SecureRecordCodec.RecordHeaderSize;
                    var inBuffer = Math.Max(0, Math.Min(bodyLength, available));
                    if (inBuffer > 0)
                        _readBuf.AsSpan(_rPos + SecureRecordCodec.RecordHeaderSize, inBuffer).CopyTo(recordBody);
                    _rPos += SecureRecordCodec.RecordHeaderSize + inBuffer;
                    if (inBuffer < bodyLength
                        && !await ReadExactAsync(recordBody.AsMemory(inBuffer), ct).ConfigureAwait(false))
                        return null;
                }
                else if (_rEnd - _rPos >= total)
                {
                    recordBody = _readBuf[_rPos..(total + _rPos - SecureRecordCodec.RecordHeaderSize)];
                    recordBody = new byte[bodyLength];
                    _readBuf.AsSpan(_rPos + SecureRecordCodec.RecordHeaderSize, bodyLength).CopyTo(recordBody);
                    _rPos += total;
                }
                else
                {
                    recordBody = null!;   // 记录未到齐——走填充
                }
                if (recordBody is not null)
                {
                    var body = recordBody!;   // 显式空检后（async 域空态流分析跨 await 保守）
                    try
                    {
                        var frame = records.Open(
                            _readBuf.AsSpan(_rPos - body.Length - SecureRecordCodec.RecordHeaderSize, SecureRecordCodec.RecordHeaderSize),
                            body);
                        // 两段式解码（TryDecode 的 ReadOnlySpan out 是 ref struct——async 域禁用）
                        if (!FrameCodec.TryReadHeader(frame, out var header))
                        {
                            _owner.NetView?.OnCrcFailure();
                            return null;
                        }
                        var payload = new byte[header.PayloadLength];
                        frame.AsSpan(FrameCodec.HeaderSize, (int)header.PayloadLength).CopyTo(payload);
                        if (!FrameCodec.VerifyPayload(header, payload))
                        {
                            _owner.NetView?.OnCrcFailure();
                            return null;
                        }
                        if (_owner.NetView is { } net && net.ShouldSampleFrame()) net.OnFrameReceived(header.ProtocolId);
                        return (header, payload);
                    }
                    catch (System.Security.Cryptography.CryptographicException ex)
                    {
                        _owner.Logger?.LogDebug("安全记录解包失败（断连）：{Remote} {Message}", _remote, ex.Message);
                        _owner.NetView?.OnCrcFailure();   // 认证/解密失败——断连
                        return null;
                    }
                }
            }
            if (!await FillAsync(ct).ConfigureAwait(false)) return null;   // 流关闭
        }
    }

    /// <summary>大帧直读（载荷 &gt; 读缓冲——头已验，载荷 = 缓冲残余 + 直读余量，独立数组交付）。</summary>
    private async Task<(FrameHeader, byte[] Payload)?> ReadLargeFrameAsync(FrameHeader header, CancellationToken ct)
    {
        var len = (int)header.PayloadLength;
        var payload = new byte[len];
        var available = _rEnd - _rPos - FrameCodec.HeaderSize;
        var inBuffer = Math.Max(0, Math.Min(len, available));
        if (inBuffer > 0)
            _readBuf.AsSpan(_rPos + FrameCodec.HeaderSize, inBuffer).CopyTo(payload);
        _rPos += FrameCodec.HeaderSize + inBuffer;
        if (inBuffer < len && !await ReadExactAsync(payload.AsMemory(inBuffer), ct).ConfigureAwait(false))
            return null;
        if (!FrameCodec.VerifyPayload(header, payload))
        {
            _owner.NetView?.OnCrcFailure();
            return null;
        }
        if (_owner.NetView is { } net && net.ShouldSampleFrame()) net.OnFrameReceived(header.ProtocolId);
        return (header, payload);
    }

    /// <summary>填充读缓冲（残余前移到头 + 一次 socket 读）。false = 流关闭。
    /// 缓冲满不可达：载荷超缓冲的大帧在帧到齐判定前已分流直读。</summary>
    private async Task<bool> FillAsync(CancellationToken ct)
    {
        if (_rPos > 0)
        {
            var remain = _rEnd - _rPos;
            if (remain > 0) Array.Copy(_readBuf, _rPos, _readBuf, 0, remain);
            _rPos = 0;
            _rEnd = remain;
        }
        var read = await _stream.ReadAsync(_readBuf.AsMemory(_rEnd), ct).ConfigureAwait(false);
        if (read <= 0) return false;
        _rEnd += read;
        return true;
    }

    private async Task<bool> ReadExactAsync(Memory<byte> target, CancellationToken ct)
    {
        while (target.Length > 0)
        {
            int read = await _stream.ReadAsync(target, ct).ConfigureAwait(false);
            if (read <= 0) return false;
            target = target[read..];
        }
        return true;
    }

    // ══ 关闭 ══

    /// <summary>关闭连接。返回关闭前是否已建立（Established→down 才触发 PeerGone——握手失败不算）。</summary>
    /// <returns>true = 关闭前链路已建立（Established → down，触发 PeerGone）；false = 握手未完成或重复关闭（幂等）。</returns>
    public bool Close()
    {
        int prior = Interlocked.Exchange(ref _state, StateClosed);
        if (prior == StateClosed) return false;
        _controlQueue.Writer.TryComplete();   // 写循环退出（在途帧由循环 finally 归还；新入队即抛链路关闭）
        _streamQueue.Writer.TryComplete();
        try { _socket.Close(); } catch { /* 关闭幂等——socket 状态由 OS 兜底 */ }
        _owner.UnregisterUdpAuthKey(_remote);   // UDP 认证键随链路失效（防旧键验新连接）
        _owner.OnLinkClosed(_remote, this, established: prior == StateEstablished);
        return prior == StateEstablished;
    }

    /// <summary>关闭连接（幂等——与 <see cref="Close"/> 同路径）。</summary>
    /// <returns>完成时连接已关闭。</returns>
    public ValueTask DisposeAsync()
    {
        Close();
        return ValueTask.CompletedTask;
    }
}
