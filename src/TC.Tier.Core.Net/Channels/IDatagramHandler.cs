namespace TC.Tier.Core.Net.Channels;

/// <summary>
/// 协议域数据报处理器（spec-12 §5.1——按协议 ID 分发后的入站回调；形态面 API）。
/// </summary>
public interface IDatagramHandler
{
    /// <summary>
    /// 入站数据报（已按协议 ID 分发）。
    /// <para>★ 契约：处理快进快出（读循环内同步调用——慢回调卡整条连接的所有协议域），
    ///   重活自排队（入自有有界队列/actor）。载荷为每帧独立缓冲，可安全持有/异步消化。</para>
    /// </summary>
    /// <param name="from">发送方节点 ID（流介质上来源即连接；UDP 上 = 握手通告端点映射——
    ///   明文模式不提供源认证保证，安全档报文认证随 W-Security 收口）。</param>
    /// <param name="payload">载荷（≤ 16MB）。</param>
    void OnDatagram(NodeId from, ReadOnlyMemory<byte> payload);
}
