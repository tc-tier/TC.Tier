using TC.Tier.Core.Execution;
using TC.Tier.Core.Logging;
using TC.Tier.Core.Net.Channels;
using TC.Tier.Core.Net.Raft;

namespace TC.Tier.Products.Net.Admin;

/// <summary>
/// Tier 管理面（二期-D1 NETGAP-005——healthz/readyz 语义 + 最小 admin 协议·产品面）：
/// 挂载请求回调域（0x70——注册区产品自管号），远程可查询节点状态导出与健康探针。
/// <para>★ 客户端形态：静态 <see cref="QueryStateAsync"/> / <see cref="QueryHealthAsync"/>
/// （编码请求 → 请求回调 → 解码应答）；服务端形态：构造时传状态供给回调即挂载。</para>
/// </summary>
public sealed class TierRaftAdmin : IRequestHandler, IAsyncDisposable
{
    /// <summary>管理协议域（注册区 0x60-0xAF 产品自管号）。</summary>
    public const byte ProtocolId = 0x70;

    private readonly IProtocolTransport _transport;
    private readonly Func<RaftStateSnapshot?> _stateProvider;
    // ★ 管理面回执经 TaskSink 受控提交（TCSG138 存量清扫）——异常观测面（计数/日志）。
    //   不挂 Dispose 链（handler 生命周期随传输——对齐 request-replay 先例）。
    private readonly TaskSink _replySink = new("tier-raft-admin");
    private readonly ILogger? _logger;

    /// <summary>构造并挂载（transport.RegisterRequestHandler(0x70, this)——对端查询即服务）。</summary>
    /// <param name="transport">承载传输（物理传输或 NodeEndpoint）。</param>
    /// <param name="stateProvider">状态供给回调（通常 = node.GetStateSnapshot；null = 未就绪——应答 unhealthy）。</param>
    /// <param name="logger">日志（可选）。</param>
    public TierRaftAdmin(IProtocolTransport transport, Func<RaftStateSnapshot?> stateProvider, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(stateProvider);
        _transport = transport;
        _stateProvider = stateProvider;
        _logger = logger;
        transport.RegisterRequestHandler(ProtocolId, this);
    }

    /// <summary>远程状态导出查询（TierAdminStateReq → TierAdminStateResp）。</summary>
    /// <param name="transport">发起端传输。</param>
    /// <param name="target">目标节点。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>目标节点状态导出（role/term/leader/水位/成员复制进度——服务端状态供给回调产出）；应答畸形抛 <see cref="TC.Tier.Core.Net.NetIOException"/>。</returns>
    public static async Task<TierAdminStateResp> QueryStateAsync(IProtocolTransport transport, NodeId target,
        CancellationToken ct = default)
    {
        var bytes = TierAdminMessageCodec.Encode(new TierAdminStateReq());
        var respBytes = await transport.SendRequestAsync(target, ProtocolId, bytes, ct: ct).ConfigureAwait(false);
        if (!TierAdminMessageCodec.TryDecode(respBytes, out var msg) || msg is not TierAdminStateResp resp)
            throw new TC.Tier.Core.Net.NetIOException($"管理面状态查询应答畸形：{target}。");
        return resp;
    }

    /// <summary>远程健康探针（TierAdminHealthReq → TierAdminHealthResp）。</summary>
    /// <param name="transport">发起端传输。</param>
    /// <param name="target">目标节点。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>目标节点健康态（Healthy/Ready/CommitIndex——状态未就绪 = unhealthy）；应答畸形抛 <see cref="TC.Tier.Core.Net.NetIOException"/>。</returns>
    public static async Task<TierAdminHealthResp> QueryHealthAsync(IProtocolTransport transport, NodeId target,
        CancellationToken ct = default)
    {
        var bytes = TierAdminMessageCodec.Encode(new TierAdminHealthReq());
        var respBytes = await transport.SendRequestAsync(target, ProtocolId, bytes, ct: ct).ConfigureAwait(false);
        if (!TierAdminMessageCodec.TryDecode(respBytes, out var msg) || msg is not TierAdminHealthResp resp)
            throw new TC.Tier.Core.Net.NetIOException($"管理面健康查询应答畸形：{target}。");
        return resp;
    }

    /// <summary>入站管理查询分发（快进快出——状态导出为内存读）。</summary>
    /// <param name="from">请求来源节点。</param>
    /// <param name="payload">管理请求帧（TierAdminMessageCodec 编码——State/Health 查询）。</param>
    /// <param name="reply">应答上下文（异步回帧；畸形/未知请求静默丢弃不回复——对端超时自愈）。</param>
    public void OnRequest(NodeId from, ReadOnlyMemory<byte> payload, IReplyContext reply)
    {
        if (!TierAdminMessageCodec.TryDecode(payload.Span, out var req))
        {
            _logger?.LogDebug("管理面查询解码失败（丢弃）：from={From} len={Len}", from, payload.Length);
            return;   // 畸形——对端超时自愈（尽力语义）
        }

        switch (req)
        {
            case TierAdminStateReq:
            {
                var snapshot = _stateProvider();
                if (snapshot is null)
                {
                    _replySink.SubmitFast(_ => reply.ReplyAsync(TierAdminMessageCodec.Encode(new TierAdminHealthResp
                    {
                        Node = _transport.Self, Healthy = false, Ready = false, CommitIndex = 0,
                    }), CancellationToken.None));
                    return;
                }

                var hasLeader = snapshot.LeaderId is { } leaderId;
                _replySink.SubmitFast(_ => reply.ReplyAsync(TierAdminMessageCodec.Encode(new TierAdminStateResp
                {
                    Node = snapshot.Self,
                    Role = (byte)snapshot.Role,
                    Term = snapshot.Term,
                    HasLeader = hasLeader,
                    LeaderId = snapshot.LeaderId ?? default,
                    CommitIndex = snapshot.CommitIndex,
                    AppliedIndex = snapshot.AppliedIndex,
                    LastLogIndex = snapshot.LastLogIndex,
                    IsVoter = snapshot.IsVoter,
                    Healthy = snapshot.Healthy,
                    Ready = snapshot.Ready,
                    MemberIds = [.. snapshot.Members.Select(m => m.Id)],
                    MatchIndex = [.. snapshot.Members.Select(m => m.MatchIndex)],
                    GroupIdValue = snapshot.GroupId.Value,
                }), CancellationToken.None));
                return;
            }
            case TierAdminHealthReq:
            {
                var snapshot = _stateProvider();
                _replySink.SubmitFast(_ => reply.ReplyAsync(TierAdminMessageCodec.Encode(new TierAdminHealthResp
                {
                    Node = _transport.Self,
                    Healthy = snapshot?.Healthy ?? false,
                    Ready = snapshot?.Ready ?? false,
                    CommitIndex = snapshot?.CommitIndex ?? 0,
                }), CancellationToken.None));
                return;
            }
            default:
                _logger?.LogDebug("管理面查询类型未知（丢弃）：type={Type}", req.GetType().Name);
                return;
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        // 无持有资源——handler 生命周期随传输（挂载面被动）；类型兼容 IAsyncDisposable 供 await using
        return ValueTask.CompletedTask;
    }
}
