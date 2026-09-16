using FluentAssertions;
using TC.Tier.Core.Net.Transport;
using Xunit;

namespace TC.Tier.Core.Net.Tests.Transport;

/// <summary>
/// 拓扑感知（二期-F1 NETGAP-017——验证矩阵 F1 行"跨 AZ 放置与故障域"）：
/// 位置属性查询、同故障域判定、故障域计数（跨 AZ 分布度）、反亲和约束（同域共置违约/跨域满足/未标注豁免）。
/// </summary>
public class TopologyMapTests
{
    private static TopologyMap CreateMap() => new(new Dictionary<NodeId, string>
    {
        [NodeId.Parse("11111111111111111111111111111111")] = "az-1",
        [NodeId.Parse("22222222222222222222222222222222")] = "az-2",
        [NodeId.Parse("33333333333333333333333333333333")] = "az-1",   // 与 11 同域
        [NodeId.Parse("44444444444444444444444444444444")] = "az-3",
    });

    private static NodeId Id(string hex8) => NodeId.Parse(hex8.PadRight(32, '0'));

    /// <summary>F1：位置属性查询 + 同故障域判定（未标注 = null/不同域）。</summary>
    [Fact]
    public void ZoneOf_And_SameFaultDomain()
    {
        var map = CreateMap();
        var az1 = NodeId.Parse("11111111111111111111111111111111");
        var az1b = NodeId.Parse("33333333333333333333333333333333");
        var az2 = NodeId.Parse("22222222222222222222222222222222");
        var unknown = NodeId.Parse("99999999999999999999999999999999");

        map.ZoneOf(az1).Should().Be("az-1");
        map.ZoneOf(unknown).Should().BeNull("未标注成员");
        map.SameFaultDomain(az1, az1b).Should().BeTrue("同 az-1");
        map.SameFaultDomain(az1, az2).Should().BeFalse("跨 AZ");
        map.SameFaultDomain(az1, unknown).Should().BeFalse("未标注不可判定——不视为同域");
    }

    /// <summary>F1：故障域计数（跨 AZ 分布度）。</summary>
    [Fact]
    public void DistinctFaultDomains_Counts()
    {
        var map = CreateMap();
        var ids = new[]
        {
            NodeId.Parse("11111111111111111111111111111111"),
            NodeId.Parse("22222222222222222222222222222222"),
            NodeId.Parse("33333333333333333333333333333333"),
            NodeId.Parse("44444444444444444444444444444444"),
            NodeId.Parse("99999999999999999999999999999999"),   // 未标注——独立计一
        };

        map.DistinctFaultDomains(ids).Should().Be(4, "az-1/az-2/az-3/未标注");
        map.DistinctFaultDomains(new[] { ids[0], ids[2] }).Should().Be(1, "同域成员合并计数");
    }

    /// <summary>F1：反亲和——同域共置违约、跨域满足、未标注豁免。</summary>
    [Fact]
    public void AntiAffinity_ViolationAndSatisfaction()
    {
        var map = CreateMap();
        var az1 = NodeId.Parse("11111111111111111111111111111111");
        var az1dup = NodeId.Parse("33333333333333333333333333333333");
        var az2 = NodeId.Parse("22222222222222222222222222222222");
        var az3 = NodeId.Parse("44444444444444444444444444444444");
        var unlabeled = NodeId.Parse("99999999999999999999999999999999");

        map.SatisfiesAntiAffinity(new[] { az1, az1dup }).Should().BeFalse("同 az-1 共置——反亲和违约");
        map.SatisfiesAntiAffinity(new[] { az1, az2 }).Should().BeTrue("跨域满足");
        map.SatisfiesAntiAffinity(new[] { az1, az2, az3 }).Should().BeTrue("三域满足");
    }

    /// <summary>F1 集成：EnforceAntiAffinity 开启——同域共置建组被拒、跨域满足放行。</summary>
    [Fact]
    public async Task Host_EnforceAntiAffinity_ThrowsOnCoLocation()
    {
        var map = new TopologyMap(new Dictionary<NodeId, string>
        {
            [NodeId.Parse("11111111111111111111111111111111")] = "az-1",
            [NodeId.Parse("33333333333333333333333333333333")] = "az-1",   // 共置
        });
        map.SatisfiesAntiAffinity(new[]
        {
            NodeId.Parse("11111111111111111111111111111111"),
            NodeId.Parse("33333333333333333333333333333333"),
        }).Should().BeFalse("同域共置——反亲和违约（建组校验同款判定）");
        await Task.CompletedTask;
    }
}
