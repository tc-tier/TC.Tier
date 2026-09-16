using Microsoft.CodeAnalysis;
using TC.Tier.CodeGen;
using Xunit;

namespace TC.Tier.CodeGen.Tests;

/// <summary>
/// TierFsGenerator golden 快照——四输出面：TypedOverloads（[MediumOptions]）、Specs DSL（[SpecParam]）、
/// 本地协议注册（[NetworkProtocol]）、外部协议注册桥（引用程序集 [assembly: TierProtocolExported] 扫描）。
/// </summary>
public sealed class TierFsGoldenTests : GoldenTestBase
{
    private const string Fixtures = """
        using TC.Tier.CodeGen;

        namespace Golden.Fixtures
        {
            [MediumOptions("local", Verbs = "New,Open")]
            public sealed class FakeOptions
            {
            }

            public sealed class FakeSpec
            {
                [SpecParam]
                public string Label { get; set; }

                [SpecParam]
                public long QuotaBytes { get; set; }

                [SpecParam(Media = "local")]
                public string Access { get; set; }
            }

            [NetworkProtocol("fakeproto")]
            public sealed class FakeProtocolBuilder
            {
            }
        }
        """;

    /// <summary>外部协议程序集（内存编译）——[assembly: TierProtocolExported] + [NetworkProtocol] 类型。</summary>
    private const string ExternalSource = """
        using TC.Tier.CodeGen;

        [assembly: TC.Tier.CodeGen.TierProtocolExported]

        namespace Golden.External
        {
            [NetworkProtocol("extproto")]
            public sealed class ExtProtocolBuilder
            {
            }
        }
        """;

    [Fact]
    public void GeneratedOutput_MatchesGoldenFiles()
    {
        var externalCompilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
            "GoldenExternal",
            new[] { Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(ExternalSource, path: "external.cs") },
            References(),
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));

        var driver = RunGenerator(new TierFsGenerator(), Fixtures, "GoldenTierFs",
            References(externalCompilation.ToMetadataReference()));
        AssertMatchesGolden(driver, "TierFs");
    }
}
