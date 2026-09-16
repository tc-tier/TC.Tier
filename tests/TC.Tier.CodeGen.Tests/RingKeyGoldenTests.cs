using TC.Tier.CodeGen;
using Xunit;

namespace TC.Tier.CodeGen.Tests;

/// <summary>RingKeyGenerator golden 快照——程序集级 [RingKey] → 封闭三件套（Ring/Hash/BTree/SkipList）。</summary>
public sealed class RingKeyGoldenTests : GoldenTestBase
{
    private const string Fixtures = """
        using TC.Tier.CodeGen;

        [assembly: TC.Tier.CodeGen.RingKey(typeof(Golden.Fixtures.TestKey))]

        namespace Golden.Fixtures
        {
            public struct TestKey
            {
                public long Id;
            }
        }
        """;

    [Fact]
    public void GeneratedOutput_MatchesGoldenFiles()
    {
        var driver = RunGenerator(new RingKeyGenerator(), Fixtures, "GoldenRingKey", References());
        AssertMatchesGolden(driver, "RingKey");
    }
}
