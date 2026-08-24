using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using TC.Tier.CodeGen;
using TC.Tier.CodeGen.Analyzers;
using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Disk;

namespace TC.Tier.Core.Tests.IO;

/// <summary>
/// spec-typed-frontend 契约测试（spec-typed-frontend-and-generator-design §6 验证项）：
/// P-a 重载 × 本性错配 fail-fast / P-b DSL ↔ spec 往返 / P-c 注册行为不变（S3 套件覆盖）/
/// P-d 真值表同步（TierSpec 属性面 × analyzer 表）+ 诊断冒烟（const 非法 spec 报 / 非常量零诊断）。
/// </summary>
public sealed class TypedFrontendContractTests : IDisposable
{
    private readonly string _dir = TestTempDir.Create("typed-frontend");

    public void Dispose() => TestTempDir.TryCleanup(_dir);

    // ═══════════════ P-a：类型化重载族 ═══════════════

    [Fact]
    public void TypedOverload_LocalNew_BindsOptions()
    {
        var root = Path.Combine(_dir, "pa");
        var spec = root.Replace(System.IO.Path.DirectorySeparatorChar, '/');
        using var fs = (DiskFileSystem)TierFs.New("local:///" + spec,
            new DiskFileSystemOptions { QuotaBytes = 1L << 20, Label = "typed" });
        fs.Volume.Label.Should().Be("typed", "options 同名值生效（合流优先级——spec 未显式时 options 填充）");
    }

    [Fact]
    public void TypedOverload_NatureMismatch_FailsFastWithExpectation()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => TierFs.New("memory:", new DiskFileSystemOptions()));
        ex.Message.Should().Contain("memory").And.Contain("local");   // 『该重载期望 X，字符串是 Y』（P-a 共核错误设计）
    }

    // ═══════════════ P-b：DSL ↔ spec 往返 ═══════════════

    [Theory]
    [InlineData("local:///data", "label=prod&quota=1073741824&access=ro")]
    [InlineData("memory:", "label=t&quota=1073741824&exclusive=1")]
    public void Dsl_RoundTrip_LocalMemory(string head, string query)
    {
        var core = head == "local:///data"
            ? Specs.Local("/data").Label("prod").Quota(1L.Giga()).ReadOnly()
            : (object)Specs.Memory().Label("t").Quota(1L.Giga()).Exclusive();
        core.ToString().Should().Be($"{head}?{query}");
        TierSpec.Parse(core.ToString()!).Should().Be(((dynamic)core).ToSpec(), "DSL ToString → Parse 往返等价");
    }

    [Fact]
    public void Dsl_Virtual_DeviceAndMembers()
    {
        var spec = Specs.Virtual("/data/v.raw").Label("vol").Member("/v2.raw").Member("/v3.raw").ToSpec();
        spec.Members.Should().Equal("/v2.raw", "/v3.raw");   // 可重复参数（Repeatable）
        spec.SubKind.Should().BeNull("文件载体缺省二级");
        var dev = Specs.Virtual("/dev/nvme0n1").ToSpec();
        dev.SubKind.Should().Be("dev", "首段制设备形态（path 首段定语义）");
    }

    [Fact]
    public void Dsl_Network_FullParamFamily()
    {
        var s = Specs.Network("cos.example.com", "bucket", "pfx")
            .Vhost().Cred("env:K").Spill("local:///var/tmp").Tls(false).ToSpec();
        s.VirtualHostAddressing.Should().BeTrue();
        s.CredentialRef.Should().Be("env:K");
        s.Spill!.Nature.Should().Be(StorageNature.Local, "嵌套位置递归解析");
        s.Tls.Should().BeFalse();
        // network 参数族不在其他 builder 上（编译期写不出——非法参数×介质不可写出的 DSL 形态）
    }

    // ═══════════════ P-d：analyzer 真值表 + 诊断冒烟 ═══════════════

    [Fact]
    public void AnalyzerTable_MatchesSpecParamSurface()
    {
        // 契约：analyzer 表的键集 == TierSpec [SpecParam] 属性经 query 名映射后的全集（漂移即红）
        var propertyToQuery = new Dictionary<string, string>
        {
            ["Label"] = "label", ["QuotaBytes"] = "quota", ["Access"] = "access", ["Exclusive"] = "exclusive",
            ["Spill"] = "spill", ["CredentialRef"] = "cred", ["Region"] = "region",
            ["VirtualHostAddressing"] = "vhost", ["Tls"] = "tls", ["Members"] = "member",
        };
        var annotated = typeof(TierSpec).GetProperties()
            .Select(p => (p.Name, Attr: p.GetCustomAttribute<SpecParamAttribute>()))
            .Where(t => t.Attr is not null)
            .ToDictionary(t => propertyToQuery[t.Name], t => t.Attr!.Media);
        annotated.Count.Should().Be(TierFsSpecAnalyzer.ParamMedia.Count,
            "TierSpec 参数面与 analyzer 真值表同源（单一事实源由本测试钉死）");
        foreach (var (key, media) in annotated)
            TierFsSpecAnalyzer.ParamMedia[key].Should().Be(media, $"参数 {key} 介质归属同步");
    }

    private static readonly string[] s_expectedAnalyzerIds = ["TCSG120", "TCSG121", "TCSG122"];
    private static readonly string[] s_expectedAnalyzerIds4 = ["TCSG120", "TCSG121", "TCSG122", "TCSG122"];

    private static List<Diagnostic> Analyze(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var comp = CSharpCompilation.Create("analyzer-smoke",
            new[] { tree },
            references: new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(TierSpec).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(SpecParamAttribute).Assembly.Location),   // [SpecParam] 符号派生真值表
                MetadataReference.CreateFromFile(typeof(TC.Tier.Core.Logging.ILogger).Assembly.Location),
            },
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var analyzer = new TierFsSpecAnalyzer();
        var compWithAnalyzers = comp.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(analyzer));
        var diags = compWithAnalyzers.GetAllDiagnosticsAsync().GetAwaiter().GetResult();
        return diags.Where(d => d.Id.StartsWith("TCSG", StringComparison.Ordinal)).ToList();
    }

    [Fact]
    public void Analyzer_ConstBadSpec_Diagnosed()
    {
        var diags = Analyze("""
            class C {
                void M() {
                    TC.Tier.Core.IO.TierFs.New("locul:///data");
                    TC.Tier.Core.IO.TierFs.Open("memory:?nope=1");
                    TC.Tier.Core.IO.TierFs.Open("local:///d?cred=env:K");
                }
                void N() {
                    TC.Tier.Core.IO.TierFs.OpenOrCreate("memory:?member=/x");   // OpenOrCreate 同拦截 + member×介质违规
                }
            }
            """);
        diags.Select(d => d.Id).Should().BeEquivalentTo(s_expectedAnalyzerIds4,
            "scheme 未知 / 参数未知 / 参数×介质违规——const 非法 spec 编译期报诊断（P-d）");
    }

    [Fact]
    public void Analyzer_NonConstantSpec_ZeroDiagnostics()
    {
        var diags = Analyze("""
            class C {
                void M(string s) {
                    TC.Tier.Core.IO.TierFs.New(s + "?acess=ro");
                }
            }
            """);
        diags.Should().BeEmpty("非常量字符串零诊断——运行时校验保留（L1 纵深防御）");
    }
}
