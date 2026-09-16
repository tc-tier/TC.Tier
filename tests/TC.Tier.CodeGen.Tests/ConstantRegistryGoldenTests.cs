using TC.Tier.CodeGen;
using Xunit;

namespace TC.Tier.CodeGen.Tests;

/// <summary>ConstantRegistryGenerator golden 快照——[ConstantRegistry] Zones → 区间断言 partial 注入。</summary>
public sealed class ConstantRegistryGoldenTests : GoldenTestBase
{
    private const string Fixtures = """
        using TC.Tier.CodeGen;

        namespace Golden.Fixtures
        {
            [ConstantRegistry(Zones = new[] { "IsCore:0x00-0x0F", "IsEdge:0x10,0x20-0x2F" })]
            public static partial class FrameZonesFixture
            {
                public const uint Core = 0x01;
                public const uint Edge = 0x11;
            }
        }
        """;

    [Fact]
    public void GeneratedOutput_MatchesGoldenFiles()
    {
        var driver = RunGenerator(new ConstantRegistryGenerator(), Fixtures, "GoldenConstantRegistry", References());
        AssertMatchesGolden(driver, "ConstantRegistry");
    }
}
