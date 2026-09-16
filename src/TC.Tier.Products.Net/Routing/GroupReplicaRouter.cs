using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using TC.Tier.Contracts.Storage;
using TC.Tier.Core.Execution;
using TC.Tier.Core.Logging;
using TC.Tier.Core.Net.Raft;

namespace TC.Tier.Products.Net.Routing;

/// <summary>
/// GroupReplicaRouter——复制档通用组路由面（一个物理传输 × 一个协议域一实例）：
/// 传输绑定 + 组分发 + 提案转发 + 状态封套四件事的公共单点，产品零重复。
/// <para>★ 一个物理传输一个路由器（同进程多组共享——<see cref="GetOrCreate"/> 惰性挂载），
/// 入站请求按组 ID 分发到对应 <see cref="IGroupProposalHandler"/>；出站 = <see cref="ForwardAsync"/>
/// （把命令送达目标节点的提案面——目标非 leader 时回 NotLeaderHint，调用方重路由）。</para>
/// <para>★ 线格式（请求回调形态——CorrId 由传输承载）：<c>请求 [GroupId 8B][命令字节]</c> →
/// <c>应答 [Status 1B][体]</c>：Ok=[HasAddr 1B][Addr 16B][Index 8B]；StaleEpoch/Rejected=[Len 4B][UTF8]；
/// NotLeaderHint=[Leader 16B]。状态字节值即 <see cref="GroupApplyStatus"/>——域内线格式稳定锚。</para>
/// <para>★ 组表 = 本地装配事实（<see cref="Register"/> 由 Builder 装配完成时挂载）；入站未装配组 =
/// 确定性 <see cref="GroupApplyStatus.Rejected"/> 应答（组发现不归路由器——另一半在 gossip/静态配置）。</para>
/// </summary>
public sealed class GroupReplicaRouter : IDisposable
{
    private static readonly ConcurrentDictionary<(IProtocolTransport Transport, byte ProtocolId), GroupReplicaRouter>
        _routers = new();

    private readonly IProtocolTransport _transport;
    private readonly byte _protocolId;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<RaftGroupId, IGroupProposalHandler> _handlers = new();
    private readonly TaskSink _replies;   // 转发应答任务组（fire-and-forget 纪律——受控丢弃，Dispose drain）
    private int _registeredHandler;

    private GroupReplicaRouter(IProtocolTransport transport, byte protocolId, ILogger? logger)
    {
        _transport = transport;
        _protocolId = protocolId;
        _logger = logger;
        _replies = new TaskSink("group-replica-forward", logger: logger);
    }

    /// <summary>取/建传输绑定的路由器（幂等——同传输同协议域返回同一实例；handler 首次挂载）。</summary>
    /// <param name="transport">物理传输（产品协议域注册面）。</param>
    /// <param name="protocolId">产品协议域号（域号分配表见 Products.Net/COORDINATION.md）。</param>
    /// <param name="logger">日志。</param>
    /// <returns>路由器实例。</returns>
    public static GroupReplicaRouter GetOrCreate(IProtocolTransport transport, byte protocolId,
        ILogger? logger = null)
    {
        var router = _routers.GetOrAdd((transport, protocolId),
            key => new GroupReplicaRouter(key.Transport, key.ProtocolId, logger));
        if (Interlocked.Exchange(ref router._registeredHandler, 1) == 0)
            router._transport.RegisterRequestHandler(protocolId, new Handler(router));
        return router;
    }

    /// <summary>注册组提案受理面（Builder 装配完成时——入站按组分发）。</summary>
    /// <param name="groupId">组 ID。</param>
    /// <param name="handler">提案受理面。</param>
    public void Register(RaftGroupId groupId, IGroupProposalHandler handler) => _handlers[groupId] = handler;

    /// <summary>注销组（副本释放时）。</summary>
    /// <param name="groupId">组 ID。</param>
    public void Unregister(RaftGroupId groupId) => _handlers.TryRemove(groupId, out _);

    /// <summary>转发命令到目标节点（提案面——目标本地提案后回终局结果）。</summary>
    /// <param name="target">目标节点。</param>
    /// <param name="groupId">组 ID。</param>
    /// <param name="command">命令字节。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>apply 结果（NotLeaderHint 不经本面返回——转抛 NotLeaderException，调用方重路由）。</returns>
    public async ValueTask<GroupApplyResult> ForwardAsync(NodeId target, RaftGroupId groupId,
        ReadOnlyMemory<byte> command, CancellationToken ct)
    {
        var payload = new byte[8 + command.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, groupId.Value);
        command.CopyTo(payload.AsMemory(8));
        var resp = await _transport.SendRequestAsync(target, _protocolId, payload, options: null, ct)
            .ConfigureAwait(false);
        if (!GroupApplyReplyCodec.TryDecode(resp, out var reply))
            throw new InvalidDataException("转发应答畸形（未知 tag/截断/超上限）。");
        var result = reply.ToResult();
        if (result.Status == GroupApplyStatus.NotLeaderHint)
        {
            var hint = result.Error is { } hex && NodeId.TryParse(hex, out var leader) ? leader : NodeId.Empty;
            throw new NotLeaderException(hint);   // 目标已非 leader——调用方重路由（NotLeader 惯例契约）
        }
        return result;
    }

    private async ValueTask<byte[]> HandleRequestAsync(NodeId from, byte[] payload, CancellationToken ct)
    {
        if (payload.Length < 8)
            throw new InvalidDataException("转发请求头截断。");
        var groupId = new RaftGroupId(BinaryPrimitives.ReadUInt64LittleEndian(payload));
        if (!_handlers.TryGetValue(groupId, out var handler))
            return GroupApplyReplyCodec.Encode(new GroupApplyRejectedReply
            {
                Error = Encoding.UTF8.GetBytes($"转发目标组未装配：{groupId}"),
            });
        var result = await handler.ProposeAsync(payload.AsMemory(8), ct).ConfigureAwait(false);
        return GroupApplyReplyCodec.Encode(result.ToReply());
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var groupId in _handlers.Keys)
            _handlers.TryRemove(groupId, out _);
        _routers.TryRemove((_transport, _protocolId), out _);
    }

    /// <summary>应答任务组排空（路由器退役时调用——在途应答 drain）。</summary>
    /// <returns>任务组排空后完成。</returns>
    internal async ValueTask DrainAsync() => await _replies.DisposeAsync().ConfigureAwait(false);

    /// <summary>入站请求回调适配（快进快出契约——重活经路由器 TaskSink 受控提交，异常不外泄不裸丢弃）。</summary>
    private sealed class Handler(GroupReplicaRouter owner) : IRequestHandler
    {
        /// <inheritdoc/>
        public void OnRequest(NodeId from, ReadOnlyMemory<byte> payload, IReplyContext reply)
        {
            owner._replies.SubmitFast((Func<CancellationToken, ValueTask>)(async ct =>
            {
                try
                {
                    var resp = await owner.HandleRequestAsync(from, payload.ToArray(), ct).ConfigureAwait(false);
                    await reply.ReplyAsync(resp, ct).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 不回复 = 对端超时（IRequestHandler 契约——错误经超时语义暴露）
                }
            }));
        }
    }
}
