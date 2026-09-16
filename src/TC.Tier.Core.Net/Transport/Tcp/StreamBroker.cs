using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using TC.Tier.Core.Net.Channels;
using TC.Tier.Core.Net.Wire;

namespace TC.Tier.Core.Net.Transport.Tcp;

/// <summary>
/// 流式会话核心（spec-12 §5.3——TCP 介质实现；<see cref="IWireStream"/> 契约在 Channels/）——
/// 会话号双向映射制 + <b>会话域按链路分立</b>：
/// <para>★ 每端每链路独立号空间（1..254 循环分配）：Open 携带发起端本地号（帧 ChannelId）；
///   Accept 携带接受端本地号（帧 ChannelId）+ 回显发起端号（载荷 1B）——双方各建
///   本地号 ↔ 远端号映射；Data/End/Reset 按发送方本地号发送、接收方按映射反查。
///   多对端链路（raft 真实形态）号空间互不相干，链路断开只 Reset 本链路会话。</para>
/// <para>★ 会话缓冲有界（帧数容量——满 = 读循环 await = 背压传导，§7）；链路断开 =
///   该链路全部会话 Reset（不跨重连恢复——上层重开）。</para>
/// </summary>
internal sealed class StreamBroker
{
    private readonly ConcurrentDictionary<byte, IStreamAcceptor> _acceptors = new();
    private readonly ConcurrentDictionary<PeerLink, LinkSessions> _links = new();
    private readonly int _windowFrames;
    private readonly long _slowAcceptTicks;                       // 二期-I3：慢 accept 判定阈值
    private readonly Action<byte, double>? _onSlowAccept;         // 二期-I3：慢 accept 回调（域, 耗时 ms）

    /// <summary>构造。</summary>
    /// <param name="windowFrames">每会话入站缓冲帧数（背压窗口）。</param>
    /// <param name="slowAcceptThreshold">acceptor 慢回调阈值（null/Zero = 关闭——二期-I3）。</param>
    /// <param name="onSlowAccept">慢 accept 回调（参数 = 协议域 + 耗时 ms）。</param>
    public StreamBroker(int windowFrames, TimeSpan? slowAcceptThreshold = null, Action<byte, double>? onSlowAccept = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(windowFrames, 1);
        _windowFrames = windowFrames;
        _slowAcceptTicks = slowAcceptThreshold is { } t && t > TimeSpan.Zero
            ? (long)(t.TotalMilliseconds * System.Diagnostics.Stopwatch.Frequency / 1000) : 0;
        _onSlowAccept = onSlowAccept;
    }

    /// <summary>在途打开中的会话数（诊断/测试——全链路合计）。</summary>
    public int OpeningCount
    {
        get { var sum = 0; foreach (var sessions in _links.Values) sum += sessions.Opening.Count; return sum; }
    }

    /// <summary>活跃会话数（诊断/测试——全链路合计）。</summary>
    public int ActiveCount
    {
        get { var sum = 0; foreach (var sessions in _links.Values) sum += sessions.ByLocal.Count; return sum; }
    }

    // ══ 注册面（§3.5 分流——同数据报/请求回调规则；传输级共享）══

    /// <summary>流接受面注册·公开口（注册区 0x60-0xAF）。</summary>
    /// <param name="protocolId">协议域 ID（注册区 0x60-0xAF）。</param>
    /// <param name="acceptor">入站会话接受面。</param>
    public void RegisterUserAcceptor(byte protocolId, IStreamAcceptor acceptor)
    {
        ProtocolRegistration.ValidateUserPort(protocolId);
        ProtocolRegistration.Add(_acceptors, protocolId, acceptor);
    }

    /// <summary>流接受面注册·内部口（核心区——机制面专用）。</summary>
    /// <param name="protocolId">协议域 ID（内部核心区）。</param>
    /// <param name="acceptor">入站会话接受面。</param>
    public void RegisterCoreAcceptor(byte protocolId, IStreamAcceptor acceptor)
    {
        ProtocolRegistration.ValidateCorePort(protocolId);
        ProtocolRegistration.Add(_acceptors, protocolId, acceptor);
    }

    // ══ 发起端 ══

    /// <summary>在途打开项（二期-C1：载荷随回执入会话——发起侧 <see cref="WireStreamSession.OpenPayload"/> 同值）。</summary>
    private readonly record struct PendingOpen(TaskCompletionSource<WireStreamSession> Completion, ReadOnlyMemory<byte> OpenPayload);

    /// <summary>
    /// 打开会话（Open 帧 → 等 Accept；超时抛 <see cref="TimeoutException"/>）。
    /// 链路断/Reset → <see cref="NetIOException"/>（不跨重连恢复）。
    /// </summary>
    /// <param name="link">目标对端链路。</param>
    /// <param name="protocolId">协议域 ID。</param>
    /// <param name="openPayload">开流载荷（随 Open 帧透传给对端 acceptor；组路由场景 = host 剥前缀后的内层载荷——二期-C1）。</param>
    /// <param name="timeout">等待 Accept 的超时（超时抛 <see cref="TimeoutException"/>）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>完成后的会话（发起端视图——本地号/对端号映射已建立）。</returns>
    public async Task<WireStreamSession> OpenAsync(PeerLink link, byte protocolId, ReadOnlyMemory<byte> openPayload,
        TimeSpan timeout, CancellationToken ct)
    {
        var sessions = SessionsOf(link);
        var local = sessions.AllocateSessionId();
        var completion = new TaskCompletionSource<WireStreamSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        sessions.Opening[local] = new PendingOpen(completion, openPayload);
        try
        {
            await link.SendStreamFrameAsync(FrameKind.StreamOpen, local, protocolId, openPayload, ct).ConfigureAwait(false);
            return await completion.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        finally
        {
            sessions.Opening.TryRemove(local, out _);
        }
    }

    /// <summary>Accept 到达（发起端）：payload = 本端 Open 时的会话号回显，ChannelId = 接受端本地号。</summary>
    /// <param name="link">对端链路。</param>
    /// <param name="peerLocalSession">接受端本地会话号（帧 ChannelId——本端接收方向反查键）。</param>
    /// <param name="protocolId">协议域 ID。</param>
    /// <param name="echoedLocal">本端 Open 会话号回显（匹配在途打开；未知/已超时 = 迟到 Accept 忽略）。</param>
    public void OnAccept(PeerLink link, byte peerLocalSession, byte protocolId, byte echoedLocal)
    {
        var sessions = SessionsOf(link);
        if (!sessions.Opening.TryRemove(echoedLocal, out var pending)) return;   // 未知/已超时——迟到 Accept 忽略

        var session = new WireStreamSession(link, echoedLocal, peerLocalSession, protocolId, _windowFrames, pending.OpenPayload);
        Register(sessions, session);
        pending.Completion.TrySetResult(session);
    }

    // ══ 接受端 ══

    /// <summary>Open 到达（接受端）：未注册 acceptor 的协议域 = Reset 拒绝（发起端超时）。
    /// openPayload 随会话暴露（<see cref="WireStreamSession.OpenPayload"/>）并透传 acceptor。</summary>
    /// <param name="link">发起方链路。</param>
    /// <param name="remoteSession">发起端本地会话号（帧 ChannelId）。</param>
    /// <param name="protocolId">协议域 ID。</param>
    /// <param name="openPayload">开流载荷（随 Open 帧到达——透传 acceptor 并随会话暴露）。</param>
    /// <param name="ct">取消令牌（Accept 帧发送可取消）。</param>
    /// <returns>完成时会话已建立并回调 acceptor（未注册协议域 = 已回 Reset 拒绝）。</returns>
    public async ValueTask OnOpenAsync(PeerLink link, byte remoteSession, byte protocolId, ReadOnlyMemory<byte> openPayload, CancellationToken ct)
    {
        if (!_acceptors.TryGetValue(protocolId, out var acceptor))
        {
            await link.SendStreamFrameAsync(FrameKind.StreamReset, remoteSession, protocolId, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
            return;
        }

        var sessions = SessionsOf(link);
        var local = sessions.AllocateSessionId();
        var session = new WireStreamSession(link, local, remoteSession, protocolId, _windowFrames, openPayload);
        Register(sessions, session);
        // ★ 二期-I3：acceptor 慢回调计时（快进快出是契约——耗时观测兜底）
        var start = _slowAcceptTicks > 0 ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        try
        {
            acceptor.OnStream(link.Remote, session, openPayload);
        }
        catch (Exception)
        {
            // acceptor（使用方代码）异常不外泄、不致命——会话清理由使用方 Dispose 规则兜底
        }
        if (_slowAcceptTicks > 0 && _onSlowAccept is not null)
        {
            var elapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (elapsedMs > _slowAcceptTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency)
                _onSlowAccept(protocolId, elapsedMs);
        }
        await link.SendStreamFrameAsync(FrameKind.StreamAccept, local, protocolId, new byte[] { remoteSession }, ct).ConfigureAwait(false);
    }

    // ══ 数据面（读循环调用——链路作用域）══

    /// <summary>Data 到达——入站缓冲（满 = await = 读循环暂停 = 背压传导，§7）。</summary>
    /// <param name="link">对端链路。</param>
    /// <param name="remoteSession">发送方本地会话号（反查本端会话）。</param>
    /// <param name="payload">帧载荷。</param>
    /// <returns>完成时数据已入会话入站缓冲（未知/已终止会话 = 立即完成丢弃）。</returns>
    public ValueTask OnDataAsync(PeerLink link, byte remoteSession, ReadOnlyMemory<byte> payload)
        => _links.TryGetValue(link, out var sessions) && sessions.ByRemote.TryGetValue(remoteSession, out var session)
            ? session.OnDataAsync(payload)
            : ValueTask.CompletedTask;

    /// <summary>Ack 到达（对端消费一帧——发起端窗口位释放；未知会话 = 迟到 ack 忽略）。</summary>
    /// <param name="link">对端链路。</param>
    /// <param name="remoteSession">发送方本地会话号（反查本端会话）。</param>
    public void OnAck(PeerLink link, byte remoteSession)
    {
        if (_links.TryGetValue(link, out var sessions) && sessions.ByRemote.TryGetValue(remoteSession, out var session))
            session.OnAck();
    }

    /// <summary>End 到达（对端写完——本端读枚举自然结束）。</summary>
    /// <param name="link">对端链路。</param>
    /// <param name="remoteSession">发送方本地会话号（反查本端会话）。</param>
    public void OnEnd(PeerLink link, byte remoteSession)
    {
        if (_links.TryGetValue(link, out var sessions) && sessions.ByRemote.TryGetValue(remoteSession, out var session))
            session.OnPeerCompleted();
    }

    /// <summary>Reset 到达（对端中止/拒绝开流）——已建立会话立即终止；在途 Open 失败
    /// （拒绝 Reset 回显发起端本地号——映射未建立，走 opening 表）。</summary>
    /// <param name="link">对端链路。</param>
    /// <param name="session">会话号（已建立 = 发送方本地号；在途 Open = 发起端本地号）。</param>
    public void OnReset(PeerLink link, byte session)
    {
        if (!_links.TryGetValue(link, out var sessions)) return;
        if (sessions.ByRemote.TryGetValue(session, out var established))
        {
            established.OnPeerReset();   // 已建立——ChannelId = 对端本地号
            return;
        }
        if (sessions.Opening.TryGetValue(session, out var opening))
        {
            sessions.Opening.TryRemove(session, out _);
            opening.Completion.TrySetException(new NetIOException("对端拒绝开流（协议域未注册 acceptor）。"));
        }
    }

    /// <summary>链路关闭——<b>该链路</b>全部会话 Reset（不跨重连恢复；在途 Open 失败）。多对端互不相干。</summary>
    /// <param name="link">已关闭的链路。</param>
    public void OnLinkClosed(PeerLink link)
    {
        if (_links.TryRemove(link, out var sessions))
        {
            foreach (var session in sessions.ByLocal.Values) session.OnPeerReset();
            foreach (var opening in sessions.Opening.Values)
                opening.Completion.TrySetException(new NetIOException("链路断开——流打开未完成（会话不跨重连恢复）。"));
        }
    }

    private LinkSessions SessionsOf(PeerLink link) => _links.GetOrAdd(link, static _ => new LinkSessions());

    private void Register(LinkSessions sessions, WireStreamSession session)
    {
        sessions.ByLocal[session.LocalSession] = session;
        sessions.ByRemote[session.RemoteSession] = session;
        session.Closed += () => Remove(sessions, session);
    }

    private void Remove(LinkSessions sessions, WireStreamSession session)
    {
        sessions.ByLocal.TryRemove(new KeyValuePair<byte, WireStreamSession>(session.LocalSession, session));
        sessions.ByRemote.TryRemove(new KeyValuePair<byte, WireStreamSession>(session.RemoteSession, session));
    }

    /// <summary>单链路会话域（号空间/映射表/在途打开——链路关闭整体废弃；背压窗口在会话级，
    /// 经 <see cref="WireStreamSession"/> 构造注入）。</summary>
    private sealed class LinkSessions()
    {
        public ConcurrentDictionary<byte, WireStreamSession> ByLocal { get; } = new();

        public ConcurrentDictionary<byte, WireStreamSession> ByRemote { get; } = new();

        public ConcurrentDictionary<byte, PendingOpen> Opening { get; } = new();

        private int _cursor;

        /// <summary>会话号循环分配（1..254——游标推进避开刚关闭的号防迟到帧误绑；
        /// 同时避开 ByRemote 键——使 opening/ByRemote 两表永不撞键，拒绝 Reset 查表无歧义）。</summary>
        /// <returns>新分配的会话号（1..254，未占用）。</returns>
        public byte AllocateSessionId()
        {
            while (true)
            {
                int next = Interlocked.Increment(ref _cursor);
                byte id = (byte)((next % ChannelIds.MaxStream) + ChannelIds.MinStream);
                if (!ByLocal.ContainsKey(id) && !Opening.ContainsKey(id) && !ByRemote.ContainsKey(id)) return id;
            }
        }
    }
}

/// <summary>
/// TCP 会话（spec-12 §5.3 <see cref="IWireStream"/> 实现）：写 = 链路 StreamData 帧
/// （socket 写 await——对端不读时 TCP 窗口收缩传导背压）；读 = 有界入站缓冲
/// （满 = 读循环 await 传导对端写）。
/// <para>★ 发起端在途窗口（读侧 HoL 根因修复）：对端最多 <c>windowFrames</c> 帧在途
/// （WriteAsync 前等窗口位）——入站缓冲（容量 = windowFrames）不溢出 → 读循环永不因
/// 流缓冲满停读 socket → 数据报/请求回调不被流的背压饿死（消费一帧回 <see cref="FrameKind.StreamAck"/>）。</para>
/// </summary>
internal sealed class WireStreamSession : IWireStream
{
    private readonly PeerLink _link;
    private readonly Channel<ReadOnlyMemory<byte>> _inbound;
    private readonly SemaphoreSlim _sendWindow;   // 发起端在途窗口（容量 = windowFrames——对端 ack 释放）
    private readonly int _windowFrames;
    private int _completed;    // 本端写完（CompleteAsync 一次）
    private int _reset;

    /// <summary>会话终止（正常/异常）——broker 移除映射索引。</summary>
    internal event Action? Closed;

    internal WireStreamSession(PeerLink link, byte localSession, byte remoteSession, byte protocolId, int windowFrames,
        ReadOnlyMemory<byte> openPayload = default)
    {
        _link = link;
        LocalSession = localSession;
        RemoteSession = remoteSession;
        ProtocolId = protocolId;
        _windowFrames = windowFrames;
        OpenPayload = openPayload;   // 二期-C1：发起/接受两侧同义 = 线上全量
        _sendWindow = new SemaphoreSlim(windowFrames, windowFrames);
        _inbound = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(windowFrames)
        {
            SingleWriter = true,
            SingleReader = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    /// <summary>本端本地会话号（发送方向）。</summary>
    internal byte LocalSession { get; }

    /// <summary>对端本地会话号（接收方向反查键）。</summary>
    internal byte RemoteSession { get; }

    /// <inheritdoc/>
    public byte ProtocolId { get; }

    /// <inheritdoc/>
    public ReadOnlyMemory<byte> OpenPayload { get; }   // 二期-C1：开流载荷（线上全量）

    /// <inheritdoc/>
    public NodeId Peer => _link.Remote;

    /// <inheritdoc/>
    /// <param name="buffer">待写数据块（字节）。</param>
    /// <param name="ct">取消令牌（等待在途窗口位时可取消）。</param>
    /// <returns>完成时 StreamData 帧已入写队列（会话已 Reset 抛 <see cref="NetIOException"/>；窗口满 = await 背压传导）。</returns>
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        if (Volatile.Read(ref _reset) != 0) throw new NetIOException("会话已中止（Reset/链路断）。");
        await _sendWindow.WaitAsync(ct).ConfigureAwait(false);   // ★ 在途窗口（对端 ack 释放——消费慢 = 写者挂 = 背压传导）
        if (Volatile.Read(ref _reset) != 0)
        {
            _sendWindow.Release();
            throw new NetIOException("会话已中止（Reset/链路断）。");
        }
        await _link.SendStreamFrameAsync(FrameKind.StreamData, LocalSession, ProtocolId, buffer, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <param name="ct">取消令牌（End 帧发送可取消）。</param>
    /// <returns>完成时 StreamEnd 帧已发送（对端读枚举自然结束；幂等——重复调用立即完成）。</returns>
    public async ValueTask CompleteAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0) return;
        await _link.SendStreamFrameAsync(FrameKind.StreamEnd, LocalSession, ProtocolId, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <param name="ct">取消令牌（传播到枚举与消费确认帧发送）。</param>
    /// <returns>入站数据块异步序列（按写入序，逐帧回 StreamAck；会话 Reset = 序列抛 <see cref="NetIOException"/> 终止）。</returns>
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var frame in _inbound.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            // ★ 消费即确认（ack 走高优先队列——流控帧不随流帧排队；对端窗口位释放）
            if (Volatile.Read(ref _reset) == 0)
            {
                try
                {
                    await _link.SendStreamFrameAsync(FrameKind.StreamAck, LocalSession, ProtocolId,
                        ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
                {
                    // ack 尽力（链路断——对端会话随链路 Reset）
                }
            }
            yield return frame;
        }
    }

    /// <summary>入站数据（读循环——缓冲满 await = 背压传导；在途窗口保证不溢出）。</summary>
    internal ValueTask OnDataAsync(ReadOnlyMemory<byte> payload) => _inbound.Writer.WriteAsync(payload);

    /// <summary>对端 ack——发起端窗口位释放（在途-1）。</summary>
    internal void OnAck()
    {
        if (_sendWindow.CurrentCount < _windowFrames) _sendWindow.Release();
    }

    /// <summary>对端写完——读枚举自然结束。</summary>
    internal void OnPeerCompleted() => _inbound.Writer.TryComplete();

    /// <summary>对端中止/链路断——读写立即终止（挂起窗口写者全部唤醒——_reset 检查后抛）。</summary>
    internal void OnPeerReset()
    {
        if (Interlocked.Exchange(ref _reset, 1) != 0) return;
        _inbound.Writer.TryComplete(new NetIOException("会话被对端 Reset。"));
        var pending = _windowFrames - _sendWindow.CurrentCount;   // 唤醒挂起写者（窗口位全放——写者经 _reset 检查抛）
        if (pending > 0) _sendWindow.Release(pending);
        Closed?.Invoke();
    }

    /// <inheritdoc/>
    /// <returns>完成时本端已发 StreamReset 中止会话（幂等；链路已断则尽力清理后完成）。</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _reset, 1) == 0)
        {
            _inbound.Writer.TryComplete();
            Closed?.Invoke();
            var pending = _windowFrames - _sendWindow.CurrentCount;
            if (pending > 0) _sendWindow.Release(pending);
            try
            {
                await _link.SendStreamFrameAsync(FrameKind.StreamReset, LocalSession, ProtocolId, ReadOnlyMemory<byte>.Empty, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                // 链路已断——Reset 尽力（会话清理已完成）
            }
        }
    }
}
