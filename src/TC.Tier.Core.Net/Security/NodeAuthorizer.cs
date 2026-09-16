using TC.Tier.Core.Net.Channels;

namespace TC.Tier.Core.Net.Security;

/// <summary>
/// 节点授权钩子（二期-H4——框架钩子，策略模型归产品）：节点在位（握手完成、身份已知）后
/// 的链路准入判定 + 域级请求准入判定。null 钩子 = 全放行（既有语义）。
/// <para>★ 边界：本接口只做"允许/拒绝"判定面——ACL/租户策略模型归产品层
/// （Redis ACL、Kafka authorizer 对标的产品语义，spec-12 §3.4）。</para>
/// </summary>
public interface INodeAuthorizer
{
    /// <summary>链路准入（握手完成、身份已知后调用——false = 断连并审计 authz_link_denied）。</summary>
    bool AuthorizeLink(NodeId remote);

    /// <summary>域级请求准入（入站请求分发前调用——false = 丢弃并审计 authz_request_denied）。</summary>
    bool AuthorizeRequest(NodeId from, byte protocolId);
}

/// <summary>
/// 审计事件接收器（二期-H3 NETGAP-009——安全事件/管理动作的审计面）：
/// 连接拒绝、握手失败、授权拒绝、连接治理拒绝等安全事件全量上报（实现归外部——结构化落盘/导出）。
/// </summary>
public interface IAuditSink
{
    /// <summary>审计事件（实现侧自行结构化——事件名/主体/细节）。</summary>
    /// <param name="event">事件名（如 link_denied / handshake_failed / authz_link_denied / cooldown_rejected）。</param>
    /// <param name="actor">主体（对端节点或端点文本）。</param>
    /// <param name="detail">细节（单行——实现侧自行结构化）。</param>
    void OnAudit(string @event, string actor, string detail);
}
