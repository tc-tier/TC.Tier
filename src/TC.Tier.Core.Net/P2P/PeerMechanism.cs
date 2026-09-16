using TC.Tier.Core.Logging;
using TC.Tier.Core.Net.Channels;
using TC.Tier.Core.Net.Ports;

namespace TC.Tier.Core.Net.P2P;

/// <summary>
/// HyParView 挂载件（spec-12 §6 × §1 机制零特权——机制在自身命名空间实现挂载形态，
/// 经 <see cref="INodeMechanism"/> 端口走与第三方/私有域同一挂载路径；装配四面零机制知识
/// （TCSG061 机器锁）的兼容形态）。
/// <para>★ MountAsync：PeerController 构造 + StartAsync（内部口注册 0x02）→ seed 非空时
///   JoinAsync(seed)（有界等待——<see cref="PeerOptions.JoinWaitTimeout"/> 兜底，seed 不可达
///   不挂装配）；seed 为空 = 部署种子自身（只启动不 JOIN——发现从种子扩散）。</para>
/// <para>★ Gossip 广播 opt-in（<see cref="PeerOptions.WithBroadcast"/>）：挂载时一并构造
///   <see cref="SwarmBroadcast"/>（视图提供者接控制器——一行委托，机制间零握手），随会员机制
///   同生命周期；<see cref="Broadcast"/> 挂载后为使用句柄（未开启 = null）。</para>
/// </summary>
public sealed class PeerMechanism : INodeMechanism
{
    private readonly NodeId? _seed;
    private readonly PeerOptions _options;
    private readonly ILogger? _logger;
    private PeerController? _controller;
    private SwarmBroadcast? _broadcast;

    /// <summary>构造（挂载形态自证——seed 来源是部署层事务，spec-12 §14）。</summary>
    /// <param name="seed">JOIN 种子节点（null = 部署种子自身——只启动不 JOIN）。</param>
    /// <param name="options">HyParView 参数（缺省 = 运行参数零写死缺省表）。</param>
    /// <param name="logger">日志（可选）。</param>
    public PeerMechanism(NodeId? seed = null, PeerOptions? options = null, ILogger? logger = null)
    {
        _seed = seed;
        _options = options ?? PeerOptions.Default;
        _logger = logger;
    }

    /// <inheritdoc/>
    public string Name => $"HyParView(seed={_seed?.ToString() ?? "self"}{(OptionsWithBroadcast ? ",gossip" : "")})";

    private bool OptionsWithBroadcast => _options.Broadcast is not null;

    /// <summary>主动视图（挂载后观测——诊断/测试面）。</summary>
    public IReadOnlyList<NodeId> ActiveView => _controller?.ActiveView ?? [];

    /// <summary>主动视图大小（挂载后观测）。</summary>
    public int ActiveCount => _controller?.ActiveCount ?? 0;

    /// <summary>Gossip 广播句柄（opt-in 开启且挂载后非空；未开启 = null）。</summary>
    public IBroadcast? Broadcast => _broadcast;

    /// <inheritdoc/>
    /// <param name="transport">节点端点完整面（构造控制器并启动；seed 非空时继而 JOIN）。</param>
    /// <param name="ct">取消令牌（JOIN 序列可取消——有界等待由 JoinWaitTimeout 兜底）。</param>
    /// <returns>完成时会员控制器已启动（Gossip 广播 opt-in 一并构造；seed JOIN 已完成）。</returns>
    /// <exception cref="InvalidOperationException">重复挂载。</exception>
    public async ValueTask MountAsync(IProtocolTransport transport, CancellationToken ct = default)
    {
        if (_controller is not null)
            throw new InvalidOperationException("PeerMechanism 已挂载（MountAsync 只可一次）。");
        var controller = new PeerController(transport.Self, transport, _options, _logger);
        await controller.StartAsync().ConfigureAwait(false);
        _controller = controller;
        if (_options.Broadcast is { } broadcastOptions)
            _broadcast = new SwarmBroadcast(transport, () => controller.ActiveView, broadcastOptions);
        if (_seed is { } seed)
            await controller.JoinAsync(seed, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <returns>完成时 Gossip 广播（若开启）与会员控制器均已释放（幂等）。</returns>
    public async ValueTask DisposeAsync()
    {
        if (_broadcast is { } broadcast)
            await broadcast.DisposeAsync().ConfigureAwait(false);   // 广播先于会员收尾——扇出读视图
        if (_controller is { } controller)
            await controller.DisposeAsync().ConfigureAwait(false);
    }
}
