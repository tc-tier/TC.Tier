namespace TC.Tier.Core.Net.Channels;

/// <summary>
/// 请求回调接收面（spec-12 §5.2——形态面对高层的 API 契约）：单请求 → 关联应答 → 超时。
/// <para>★ 回调契约同数据报 <see cref="IDatagramHandler"/>：快进快出（重活自排队）、
///   异常不外泄不致命；不回复 = 对端超时（ReplyAsync 零次调用合法）。</para>
/// <para>★ 幂等约定：at-least-once（RetryPolicy）下同 CorrId 请求可能重发——传输端
///   去重窗口缓存重放应答不重复执行；跨重启窗口清空（§8.4）——handler 幂等是使用方约定。</para>
/// </summary>
public interface IRequestHandler
{
    /// <summary>入站请求回调（发送方/介质上下文同步触发——快进快出）。</summary>
    /// <param name="from">请求来源节点。</param>
    /// <param name="payload">请求载荷。</param>
    /// <param name="reply">应答上下文（<see cref="IReplyContext.ReplyAsync"/> 应答；不调用 = 对端超时）。</param>
    void OnRequest(NodeId from, ReadOnlyMemory<byte> payload, IReplyContext reply);
}

/// <summary>
/// 应答上下文（spec-12 §5.2）——回程路由知识在介质（TCP 链路回写 / InProcess 定向投递），
/// handler 只见 <see cref="ReplyAsync"/>；<see cref="CorrelationId"/> 由传输内核生成（机制消息族不携带）。
/// </summary>
public interface IReplyContext
{
    /// <summary>请求来源节点（应答目标）。</summary>
    NodeId Peer { get; }

    /// <summary>协议域 ID。</summary>
    byte ProtocolId { get; }

    /// <summary>关联 ID（传输生成——回带定位发起端 pending 表）。</summary>
    ulong CorrelationId { get; }

    /// <summary>应答（零次调用 = 对端超时；失败传播给调用方——介质尽力的最后一步）。</summary>
    /// <param name="payload">应答载荷。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask ReplyAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default);
}
