using System.Buffers.Binary;
using TC.Tier.Core.Execution;
using TC.Tier.Core.Net.Channels;
using TC.Tier.Core.Net.Wire;

namespace TC.Tier.Core.Net.P2P;

/// <summary>
/// Gossip 广播（spec-06 §6 dissemination 件——最终一致性广播；<see cref="IBroadcast"/> 实现）。
/// <para>★ 语义（spec-06 §6 语义要点）：沿活跃视图扇出（fanout ≤ k）、MsgId 去重（同消息全网
///   至多投递一次）、TTL 逐跳减一（减尽即停——环路面保险）；不保证顺序/不保证送达（P2P 模式定位，
///   数据报尽力语义）。</para>
/// <para>★ 组合（机制挂载面）：构造收活跃视图提供者委托——会员关系零类型依赖（PeerMechanism
///   装配时接 <see cref="PeerController.ActiveView"/>）；广播是独立组件，会员管理器不含广播
///   （spec-06 语义纪律）。</para>
/// <para>★ 通道：协议域 <see cref="ProtocolIds.Gossip"/>（0x05）数据报，Udp bearer 声明
///   （gossip/成员消息走数据报形态——spec-12 §6 机制面）；出站尽力发送经 <see cref="TaskSink"/>
///   （同步完成零分配快路径，异常组内观测）。</para>
/// <para>★ 契约：handler 快进快出（介质读循环内同步回调——spec-12 §7）；
///   <see cref="MessageReceived"/> 订阅方同契约（重活自排队）。</para>
/// </summary>
public sealed class SwarmBroadcast : IBroadcast, IDatagramHandler, IAsyncDisposable
{
    private readonly IProtocolTransport _transport;
    private readonly Func<IReadOnlyList<NodeId>> _activeView;
    private readonly BroadcastOptions _options;
    private readonly Random _random;
    private readonly TaskSink _sends;   // 广播出站（尽力送达——fire-and-forget 的受控形态）
    private readonly SeenCache _seen;   // MsgId 去重（有界 LRU——同消息至多投递一次）
    private volatile bool _disposed;

    /// <summary>
    /// 构造即注册协议域（数据报入站即刻生效——<see cref="MessageReceived"/> 未订阅窗口内
    /// 到达的消息按尽力语义静默）。
    /// </summary>
    /// <param name="transport">节点端点完整面（须实现内部挂载口——机制宿主归 Core.Net，spec-12 §3.5）。</param>
    /// <param name="activeView">活跃视图提供者（扇出目标域——装配接 <see cref="PeerController.ActiveView"/>）。</param>
    /// <param name="options">广播参数（null = 缺省表）。</param>
    public SwarmBroadcast(
        IProtocolTransport transport,
        Func<IReadOnlyList<NodeId>> activeView,
        BroadcastOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(activeView);
        _options = options ?? BroadcastOptions.Default;
        Validate(_options);
        if (transport is not ICoreProtocolPort)
            throw new ArgumentException(
                $"广播挂载须内部注册口（ICoreProtocolPort）——介质 {transport.GetType().Name} 未实现；机制宿主归 Core.Net（spec-12 §3.5）。");
        _transport = transport;
        _activeView = activeView;
        _random = _options.Random ?? Random.Shared;
        _seen = new SeenCache(_options.DedupCapacity);
        _sends = new TaskSink("swarm-broadcast");
        ((ICoreProtocolPort)transport).RegisterCoreProtocol(ProtocolIds.Gossip, this, DatagramBearer.Udp);
    }

    /// <inheritdoc/>
    public event Action<NodeId, ReadOnlyMemory<byte>>? MessageReceived;

    /// <inheritdoc/>
    /// <param name="data">广播载荷（≤ <see cref="BroadcastOptions.MaxPayloadBytes"/>——超限抛）。</param>
    /// <param name="ct">取消令牌（本实现同步出站——形态面签名同构）。</param>
    /// <returns>完成时消息已封装（生成 MsgId、源端自登记去重）并沿活跃视图扇出尽力发送。</returns>
    public ValueTask BroadcastAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(data.Length, _options.MaxPayloadBytes);
        var frame = GC.AllocateUninitializedArray<byte>(GossipFrameHeaderCodec.StructSize + data.Length);
        Span<byte> idBytes = stackalloc byte[Opaque16.Size];
        _random.NextBytes(idBytes);
        var msgId = new Opaque16(idBytes);
        GossipFrameHeaderCodec.Write(frame, new GossipFrameHeader(msgId, _transport.Self, (byte)_options.Ttl, data.Length));
        data.Span.CopyTo(frame.AsSpan(GossipFrameHeaderCodec.StructSize));
        _seen.TryRegister(msgId);   // 源头自登记——环回副本/自播路径归一
        Send(frame, SampleTargets(exclude: null));
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    /// <param name="from">消息来源节点（直接发送者——中继扇出时排除）。</param>
    /// <param name="payload">Gossip 帧（[MsgId 16B][Origin 16B][Ttl 1B][Len 4B][data]；畸形/重复 = 静默丢弃）。</param>
    public void OnDatagram(NodeId from, ReadOnlyMemory<byte> payload)
    {
        if (_disposed || payload.Length < GossipFrameHeaderCodec.StructSize) return;
        var header = GossipFrameHeaderCodec.Read(payload.Span);
        if (header.PayloadLength < 0
            || payload.Length < GossipFrameHeaderCodec.StructSize + header.PayloadLength) return;   // 畸形——尽力丢弃
        if (!_seen.TryRegister(header.MsgId)) return;   // 重复——同 MsgId 至多投递一次
        var data = payload.Slice(GossipFrameHeaderCodec.StructSize, header.PayloadLength);
        MessageReceived?.Invoke(header.Origin, data);
        if (header.Ttl <= 1) return;
        Relay(from, header, data);
    }

    /// <summary>中继（Ttl-1 重帧——来源者排除后沿活跃视图扇出；尽力发送）。</summary>
    private void Relay(NodeId from, in GossipFrameHeader header, ReadOnlyMemory<byte> data)
    {
        var frame = GC.AllocateUninitializedArray<byte>(GossipFrameHeaderCodec.StructSize + data.Length);
        GossipFrameHeaderCodec.Write(frame, new GossipFrameHeader(header.MsgId, header.Origin, (byte)(header.Ttl - 1), data.Length));
        data.Span.CopyTo(frame.AsSpan(GossipFrameHeaderCodec.StructSize));
        Send(frame, SampleTargets(exclude: from));
    }

    /// <summary>沿活跃视图扇出（去自身/去来源者——尽力发送，静默丢弃不外泄）。</summary>
    private void Send(byte[] frame, List<NodeId> targets)
    {
        foreach (var target in targets)
            _sends.SubmitFast(ct => _transport.SendDatagramAsync(target, ProtocolIds.Gossip, frame, ct));
    }

    /// <summary>扇出目标抽样（全视图 ≤ Fanout 直取；超出按随机抽样——spec-06 §6 fanout ≤ k）。</summary>
    private List<NodeId> SampleTargets(NodeId? exclude)
    {
        var view = _activeView();
        var self = _transport.Self;
        List<NodeId>? targets = null;
        foreach (var id in view)
        {
            if (id == self || id == exclude) continue;
            targets ??= [];
            targets.Add(id);
        }
        if (targets is null || targets.Count <= _options.Fanout) return targets ?? [];
        for (var i = targets.Count - 1; i > 0; i--)   // partial Fisher-Yates——前 Fanout 位即样本
        {
            var j = _random.Next(i + 1);
            (targets[i], targets[j]) = (targets[j], targets[i]);
        }
        targets.RemoveRange(_options.Fanout, targets.Count - _options.Fanout);
        return targets;
    }

    /// <inheritdoc/>
    /// <returns>完成时出站 sink 已有界排空（尽力发送收尾；实例不再可用）。</returns>
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await _sends.DisposeAsync().ConfigureAwait(false);   // 有界排空（尽力发送的收尾）
    }

    private static void Validate(BroadcastOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Ttl, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.Ttl, byte.MaxValue);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Fanout, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.DedupCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxPayloadBytes, 1);
    }

    /// <summary>MsgId 去重缓存（有界 LRU——同消息至多投递一次；容量满逐最旧，重复窗口 = 容量 × 传播时延）。</summary>
    private sealed class SeenCache(int capacity)
    {
        private readonly object _lock = new();
        private readonly Dictionary<Opaque16, LinkedListNode<Opaque16>> _map = [];
        private readonly LinkedList<Opaque16> _lru = [];

        /// <summary>登记 MsgId（首次 = true 入缓存；重复 = false，并刷新新近位）。</summary>
        /// <param name="id">消息 ID（16B 不透明字节串）。</param>
        /// <returns>true = 首次见到（已入缓存）；false = 重复（刷新 LRU 新近位，调用方丢弃）。</returns>
        public bool TryRegister(Opaque16 id)
        {
            lock (_lock)
            {
                if (_map.TryGetValue(id, out var node))
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                    return false;
                }
                node = _lru.AddFirst(id);
                _map.Add(id, node);
                if (_map.Count > capacity)
                {
                    var eldest = _lru.Last!;
                    _lru.RemoveLast();
                    _map.Remove(eldest.Value);
                }
                return true;
            }
        }
    }
}
