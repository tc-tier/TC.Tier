namespace TC.Tier.Products.Net.Routing;

/// <summary>
/// 组提案受理面（<see cref="GroupReplicaRouter"/> 入站分发的目标）：副本类显式实现本接口
/// （或 3 行适配器）挂载到路由器——路由器零感知产品类型，组分发/传输绑定/应答封套全在公共面。
/// </summary>
public interface IGroupProposalHandler
{
    /// <summary>受理提案（命令字节语义产品自释——apply 确定性归产品状态机，全组同判）。</summary>
    /// <param name="command">命令字节（转发入站的 <c>[GroupId 8B]</c> 头之后的部分）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>apply 终局结果（NotLeaderHint 仅作转发回执——路由器出站侧转 NotLeaderException）。</returns>
    ValueTask<GroupApplyResult> ProposeAsync(ReadOnlyMemory<byte> command, CancellationToken ct);
}
