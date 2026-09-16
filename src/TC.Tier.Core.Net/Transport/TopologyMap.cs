using System.Net;

namespace TC.Tier.Core.Net.Transport;

/// <summary>
/// 拓扑感知（二期-F1 NETGAP-017——成员位置属性 + 放置约束/反亲和的判定面）：
/// 成员 → 位置标签（rack/zone/region 任一粒度，语义归使用方）的映射与故障域判定。
/// <para>★ 约束判定：反亲和（任意两成员不同故障域）、故障域计数（跨 AZ 分布度）——
/// 供装配/再平衡/放置策略消费。位置属性装配期供给（运行期静态——服务发现动态化归 F8/F9）。</para>
/// </summary>
public sealed class TopologyMap
{
    private readonly IReadOnlyDictionary<NodeId, string> _zones;

    /// <summary>构造。</summary>
    /// <param name="zones">成员 → 位置标签（如 "az-1"/"rack-7"/"cn-east-1"）。</param>
    public TopologyMap(IReadOnlyDictionary<NodeId, string> zones)
    {
        ArgumentNullException.ThrowIfNull(zones);
        _zones = zones;
    }

    /// <summary>成员位置标签（未标注 = null——未标注成员不参与反亲和判定）。</summary>
    public string? ZoneOf(NodeId id) => _zones.TryGetValue(id, out var z) ? z : null;

    /// <summary>两成员是否同故障域（任一未标注 = false——不可判定不视为同域）。</summary>
    /// <param name="a">成员节点 ID。</param>
    /// <param name="b">另一成员节点 ID。</param>
    /// <returns>true = 两者位置标签均已知且相同；false = 标签不同或任一未标注。</returns>
    public bool SameFaultDomain(NodeId a, NodeId b)
    {
        var za = ZoneOf(a);
        var zb = ZoneOf(b);
        return za is not null && zb is not null && za == zb;
    }

    /// <summary>成员集合覆盖的故障域数（未标注成员按独立域各计一）。</summary>
    /// <param name="members">成员集合（空集 = 0 域）。</param>
    /// <returns>覆盖的故障域数（不同标签数 + 未标注成员数；可重复枚举同一成员——集合语义由调用方保证）。</returns>
    public int DistinctFaultDomains(IEnumerable<NodeId> members)
    {
        var labels = new HashSet<string>();
        var unlabeled = 0;
        foreach (var m in members)
        {
            var z = ZoneOf(m);
            if (z is null) unlabeled++;
            else labels.Add(z);
        }
        return labels.Count + unlabeled;
    }

    /// <summary>反亲和约束：成员集合内任意两成员不同故障域（未标注成员视为独立域不违约）。</summary>
    /// <param name="members">成员集合（重复的已标注成员按同域违约处理——集合语义由调用方保证）。</param>
    /// <returns>true = 全部已标注成员两两不同故障域；false = 任两已标注成员同域。</returns>
    public bool SatisfiesAntiAffinity(IEnumerable<NodeId> members)
    {
        var seen = new List<(NodeId Id, string? Zone)>();
        foreach (var m in members)
        {
            var z = ZoneOf(m);
            if (z is null) continue;   // 未标注不违约（也不约束后续）
            foreach (var (_, otherZone) in seen)
                if (otherZone is not null && otherZone == z)
                    return false;
            seen.Add((m, z));
        }
        return true;
    }
}
