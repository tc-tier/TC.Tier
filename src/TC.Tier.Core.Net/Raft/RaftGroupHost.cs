using System.Collections.Concurrent;
using TC.Tier.Core.Execution;
using TC.Tier.Core.Net.Channels;
using TC.Tier.Core.Net.Wire;

namespace TC.Tier.Core.Net.Raft;

/// <summary>
/// Multi-Raft 组宿主（二期-C1 §4.1——一个传输上 N 组的独占注册者）：在物理传输上独占注册
/// Raft 家族三域（0x01 请求 / 0x03 请求 / 0x04 流接受）各一次，按载荷首部 <c>[GroupId 8B]</c>
/// 前缀路由到各组的绑定（§2.1 统一组路由——无特例、无双轨）。
/// <para>★ 关键不变量（§2.3）：①三域注册者唯一 = 本宿主；②组前缀在组通道边界加/剥，引擎
/// 零感知；③组 ID 宿主内唯一，未注册组入站丢弃 + 计数（不致命、不断链）；④宿主不拥有传输
/// （装配层拥有——Dispose 不释放传输）；⑤动态成员只增删物理可达性，组内成员集合以各组
/// ClusterConfig 为准。</para>
/// <para>★ 分发快进快出：入站仅"解 8B + 查表 + 委派"（引擎 OnRequest 皆同步快退）——无跨组
/// 失败传染面。统计（in/out/丢弃）组维度可观测（二期-I6 组标签的挂点）。</para>
/// </summary>
public sealed class RaftGroupHost : IAsyncDisposable
{
    private readonly IProtocolTransport _transport;
    private readonly ConcurrentDictionary<RaftGroupId, GroupBinding> _groups = new();
    // ★ 拒绝会话释放经 TaskSink 受控提交（TCSG138 存量清扫）——异常观测面 + Dispose 有界 drain。
    private readonly TaskSink _closeSink = new("raft-group-host");
    private int _started;
    private int _disposed;

    /// <summary>构造（传输由装配层拥有并先行 <see cref="ITransport.Start"/>；宿主不释放传输）。</summary>
    /// <param name="transport">物理传输（须实现内部口 <see cref="ICoreProtocolPort"/>——机制宿主归 Core.Net）。</param>
    public RaftGroupHost(IProtocolTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (transport is not ICoreProtocolPort)
            throw new InvalidOperationException(
                $"组宿主挂载须内部注册口（ICoreProtocolPort）——介质 {transport.GetType().Name} 未实现；机制宿主归 Core.Net（spec-12 §3.5）。");
        _transport = transport;
    }

    /// <summary>宿主挂载的物理传输（装配层可继续在其上注册产品协议域——与组路由正交）。</summary>
    public IProtocolTransport Transport => _transport;

    /// <summary>已注册组集合（快照）。</summary>
    public IReadOnlyCollection<RaftGroupId> Groups => [.. _groups.Keys];

    /// <summary>宿主统计（组维度——未知组/畸形前缀丢弃计数，二期-I6 组标签扩展点）。</summary>
    public RaftGroupHostStats Stats { get; } = new();

    /// <summary>启动（注册三域各一次；重复 Start 抛——与引擎 StartAsync 只可一次纪律一致）。</summary>
    /// <param name="ct">取消令牌（注册为同步内存操作——未使用）。</param>
    /// <returns>三域注册完成即结束（同步完成）。</returns>
    /// <exception cref="InvalidOperationException">宿主已启动或已释放。</exception>
    public Task StartAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("RaftGroupHost 已启动（StartAsync 只可一次）。");
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var core = (ICoreProtocolPort)_transport;
        core.RegisterCoreRequestHandler(ProtocolIds.Raft, new CoreRequestHandler(this, ProtocolIds.Raft));
        core.RegisterCoreRequestHandler(ProtocolIds.SwarmSync, new CoreRequestHandler(this, ProtocolIds.SwarmSync));
        core.RegisterCoreStreamAcceptor(ProtocolIds.SnapshotStream, new CoreStreamAcceptor(this));
        return Task.CompletedTask;
    }

    /// <summary>注册组并返回组作用域通道（引擎经通道挂载三域——引擎零感知）。可在
    /// StartAsync 之前调用（仅登记路由表）；组 ID 已存在抛；宿主释放后调用抛。</summary>
    /// <param name="id">组 ID（宿主内唯一——重复注册抛）。</param>
    /// <returns>组作用域通道（出站加 <c>[GroupId 8B]</c> 前缀、入站剥前缀——引擎零感知）。</returns>
    /// <exception cref="InvalidOperationException">组 ID 已注册，或宿主已释放。</exception>
    public RaftGroupChannel CreateGroup(RaftGroupId id)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var binding = new GroupBinding(id);
        if (!_groups.TryAdd(id, binding))
            throw new InvalidOperationException($"Raft 组 {id} 已注册（组 ID 宿主内唯一）。");
        return new RaftGroupChannel(this, binding);
    }

    /// <summary>注销组路由（在途调用按各自契约收尾，不等待；移除后入站按未注册处理——丢弃 + 计数）。</summary>
    /// <param name="id">要注销的组 ID（未注册 = no-op）。</param>
    public void RemoveGroup(RaftGroupId id)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _groups.TryRemove(id, out _);
    }

    // ══ 组通道绑定面（内部——通道核心注册口写入；装配期单线程，重复注册 = 程序错误即抛）══

    /// <summary>绑定组请求 handler（重复注册抛——沿用单组纪律）。</summary>
    internal void BindHandler(RaftGroupHost.GroupBinding binding, byte protocolId, IRequestHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (protocolId == ProtocolIds.Raft)
        {
            if (binding.RaftHandler is not null)
                throw new InvalidOperationException($"Raft 组 {binding.Id} 0x01 域重复注册（每域至多一个——沿用单组纪律）。");
            binding.RaftHandler = handler;
            return;
        }
        if (binding.SwarmHandler is not null)
            throw new InvalidOperationException($"Raft 组 {binding.Id} 0x03 域重复注册（每域至多一个——沿用单组纪律）。");
        binding.SwarmHandler = handler;
    }

    /// <summary>绑定组快照 acceptor（重复注册抛——沿用单组纪律）。</summary>
    internal void BindAcceptor(RaftGroupHost.GroupBinding binding, IStreamAcceptor acceptor)
    {
        ArgumentNullException.ThrowIfNull(acceptor);
        if (binding.SnapshotAcceptor is not null)
            throw new InvalidOperationException($"Raft 组 {binding.Id} 0x04 域重复注册（每域至多一个——沿用单组纪律）。");
        binding.SnapshotAcceptor = acceptor;
    }

    // ══ 入站分发（介质回调——快进快出）══

    private void DispatchRequest(byte protocolId, NodeId from, ReadOnlyMemory<byte> payload, IReplyContext reply)
    {
        if (payload.Length < GroupPrefixBytes)
        {
            Stats.MalformedDropsInc();
            return;   // 畸形——丢弃（尽力语义：对端超时自愈）
        }
        var id = ParseGroupId(payload);
        if (!_groups.TryGetValue(id, out var binding))
        {
            Stats.UnknownGroupDropsInc();
            return;   // 未注册组——丢弃 + 计数（不致命、不断链）
        }
        var handler = protocolId == ProtocolIds.Raft ? binding.RaftHandler : binding.SwarmHandler;
        if (handler is null)
        {
            Stats.UnknownGroupDropsInc();   // 组已注册但该域 handler 未注册（如 SwarmSync ServeBlocks=false）——同未注册
            return;
        }
        handler.OnRequest(from, payload[GroupPrefixBytes..], new GroupReplyContext(reply, id));
    }

    private void DispatchStream(NodeId from, IWireStream stream, ReadOnlyMemory<byte> openPayload)
    {
        if (openPayload.Length < GroupPrefixBytes)
        {
            Stats.MalformedDropsInc();
            CloseRejectedStream(stream);   // 畸形（< 8B）——丢弃并释放会话
            return;
        }
        var id = ParseGroupId(openPayload);
        if (!_groups.TryGetValue(id, out var binding) || binding.SnapshotAcceptor is null)
        {
            Stats.UnknownGroupDropsInc();
            CloseRejectedStream(stream);   // 未注册组 / 组未挂快照接受面——丢弃 + 计数
            return;
        }
        binding.SnapshotAcceptor.OnStream(from, stream, openPayload[GroupPrefixBytes..]);   // 内层透传——引擎零感知
    }

    /// <summary>拒绝会话受控释放：宿主已释放时不提交（传输归装配层且晚于宿主释放——§4.5，
    /// 残留会话随传输收尾；Dispose 竞窗 ODE 吞——提交进已释放 sink 无意义）。</summary>
    private void CloseRejectedStream(IWireStream stream)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try { _closeSink.SubmitFast(_ => stream.DisposeAsync()); }
        catch (ObjectDisposedException) { }
    }

    private static RaftGroupId ParseGroupId(ReadOnlyMemory<byte> payload)
        => new(System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(payload.Span[..GroupPrefixBytes]));

    /// <summary>组前缀宽度（8B 小端——§3.2）。</summary>
    internal const int GroupPrefixBytes = 8;

    /// <summary>组前缀写入（通道出站加前缀）。</summary>
    internal static void WritePrefix(Span<byte> destination, RaftGroupId id)
        => System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(destination, id.Value);

    /// <summary>组绑定（§4.2——三域各自至多一个，重复注册抛——沿用单组纪律；组移除竞态 =
    /// 入站按未注册处理）。</summary>
    internal sealed class GroupBinding(RaftGroupId id)
    {
        public RaftGroupId Id { get; } = id;
        public IRequestHandler? RaftHandler { get; set; }
        public IRequestHandler? SwarmHandler { get; set; }
        public IStreamAcceptor? SnapshotAcceptor { get; set; }
    }

    /// <summary>核心域请求 handler 桥（解前缀 → 查表 → 委派组绑定）。</summary>
    private sealed class CoreRequestHandler(RaftGroupHost owner, byte protocolId) : IRequestHandler
    {
        public void OnRequest(NodeId from, ReadOnlyMemory<byte> payload, IReplyContext reply)
        {
            if (payload.Length < GroupPrefixBytes)
            {
                owner.Stats.MalformedDropsInc();
                return;   // 畸形——丢弃（尽力语义：对端超时自愈）
            }
            owner.DispatchRequest(protocolId, from, payload, reply);
        }
    }

    /// <summary>核心域流 acceptor 桥（解前缀 → 查表 → 委派组绑定）。</summary>
    private sealed class CoreStreamAcceptor(RaftGroupHost owner) : IStreamAcceptor
    {
        public void OnStream(NodeId from, IWireStream stream, ReadOnlyMemory<byte> openPayload)
            => owner.DispatchStream(from, stream, openPayload);
    }

    /// <summary>宿主统计（Interlocked 计数——任意线程）。</summary>
    public sealed class RaftGroupHostStats
    {
        private long _unknownGroupDrops;
        private long _malformedDrops;

        /// <summary>未注册组（含组已注册但该域 handler 未注册）入站丢弃计数。</summary>
        public long UnknownGroupDrops => Interlocked.Read(ref _unknownGroupDrops);

        /// <summary>畸形载荷（&lt; 8B 前缀）丢弃计数。</summary>
        public long MalformedDrops => Interlocked.Read(ref _malformedDrops);

        internal void UnknownGroupDropsInc() => Interlocked.Increment(ref _unknownGroupDrops);
        internal void MalformedDropsInc() => Interlocked.Increment(ref _malformedDrops);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _groups.Clear();   // 注销路由；引擎生命周期归装配层（§4.5：传输释放晚于宿主）
        await _closeSink.DisposeAsync().ConfigureAwait(false);   // 有界 drain 在途会话释放
    }
}
