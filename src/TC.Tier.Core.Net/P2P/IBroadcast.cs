namespace TC.Tier.Core.Net.P2P;

/// <summary>
/// 最终一致性广播端口（spec-06 §6 定案 × spec-12 §6 机制面——HyParView 的 dissemination 件；
/// 实现 = <see cref="SwarmBroadcast"/>）。
/// <para>★ 语义要点：gossip 沿活跃视图扇出（fanout ≤ k）、TTL/去重（消息 ID 缓存）、
///   最终一致性（不保证顺序/不保证送达——P2P 模式定位）。</para>
/// </summary>
public interface IBroadcast
{
    /// <summary>广播数据（gossip 语义——最终送达活跃视图可达的全体成员）。</summary>
    /// <param name="data">广播载荷。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask BroadcastAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);

    /// <summary>收到广播消息（去重后投递——同消息 ID 至多一次；节点 = 帧内最初广播者）。</summary>
    event Action<NodeId, ReadOnlyMemory<byte>>? MessageReceived;
}
