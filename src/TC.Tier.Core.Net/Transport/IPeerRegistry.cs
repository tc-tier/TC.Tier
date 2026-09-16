using System.Net;

namespace TC.Tier.Core.Net.Transport;

/// <summary>
/// 运行时动态成员表（二期-C2 §5.3）：物理可达性的增删面——组内成员集合始终以各组
/// <see cref="Raft.ClusterConfig"/> 为准（§2.3 不变量⑤），本表只管"谁能拨/拨到哪"。
/// <para>★ 语义（§5.3）：AddPeer 自端 = 抛；同 ID 换端点 = 更新（下一拨号轮生效）；
/// RemovePeer = 出表 + 停拨号循环 + 入站黑名单（运行时易失）+ 可选断链；未知 ID 幂等。</para>
/// </summary>
public interface IPeerRegistry
{
    /// <summary>加入对端（同 ID = 更新端点并解除入站黑名单；自端抛）。</summary>
    void AddPeer(NodeId id, IPEndPoint endpoint);

    /// <summary>移除对端（停拨号循环 + 入站黑名单；closeLink 默认关闭既有链路；未知 ID 幂等）。</summary>
    void RemovePeer(NodeId id, bool closeLink = true);

    /// <summary>对端表快照（NodeId → 端点）。</summary>
    IReadOnlyDictionary<NodeId, IPEndPoint> Peers { get; }
}
