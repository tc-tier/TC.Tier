using System.Net;

namespace TC.Tier.Core.Net.Channels;

/// <summary>
/// Raft 拨号抽象（二期-C1 §4.4——端点拨号能力面）：RaftGroupChannel 经此委托底层传输的
/// 地址制拨号（端点形态 JoinAsync）。ClusterTransport 实现之；组通道检测底层实现即透传。
/// </summary>
internal interface IRaftDialer
{
    /// <summary>地址制直连（拨号 → 握手 → 链路登记；返回对端身份）。</summary>
    ValueTask<NodeId> DialAsync(IPEndPoint endpoint, CancellationToken ct = default);
}
