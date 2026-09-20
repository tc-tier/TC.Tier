using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Xunit;
using static TC.Tier.Analyzers.Tests.AnalyzerTestDriver;

namespace TC.Tier.Analyzers.Tests;

/// <summary>
/// 分层依赖规则族测试（#438 件一）——TCSG130/131/132/133 正反例 × 配置变体
/// （作用域匹配/通配/命名空间左值/框架豁免/未配置零报告/非法配置 TCSG139 fail-fast）。
/// </summary>
public class LayerDependencyTests
{
    private static readonly Dictionary<string, string> EmptyConfig = new();

    // === 130：禁用引用（using 命名空间面）===

    [Fact]
    public async Task ForbiddenReference_UsingInAssembly_ReportsTCSG130()
    {
        var diags = await AnalyzeAsync("""
            using TC.Traffic.Proxy;
            namespace App;
            public class C { }
            """, "TC.Traffic.Control", new Dictionary<string, string>
        {
            ["tier_layer.forbidden_reference"] = "TC.Traffic.Control => TC.Traffic.Proxy",
        });

        diags.Where(d => d.Id == "TCSG130").Should().HaveCount(1);
    }

    [Fact]
    public async Task ForbiddenReference_UsingSubNamespace_ReportsTCSG130()
    {
        var diags = await AnalyzeAsync("""
            using TC.Traffic.Proxy.Internal;
            namespace App;
            public class C { }
            """, "TC.Traffic.Control", new Dictionary<string, string>
        {
            ["tier_layer.forbidden_reference"] = "TC.Traffic.Control => TC.Traffic.Proxy",
        });

        diags.Where(d => d.Id == "TCSG130").Should().HaveCount(1);
    }

    [Fact]
    public async Task ForbiddenReference_OtherAssembly_UsingTarget_NotReported()
    {
        var diags = await AnalyzeAsync("""
            using TC.Traffic.Proxy;
            namespace App;
            public class C { }
            """, "TC.Traffic.App", new Dictionary<string, string>
        {
            ["tier_layer.forbidden_reference"] = "TC.Traffic.Control => TC.Traffic.Proxy",
        });

        diags.Where(d => d.Id.StartsWith("TCSG", System.StringComparison.Ordinal))
            .Should().BeEmpty("规则左值不匹配当前程序集 = 零报告（fixture 本身的 CS 错误不在断言面）");
    }

    [Fact]
    public async Task ForbiddenReference_WildcardLeft_MatchesPrefix()
    {
        var diags = await AnalyzeAsync("""
            using TC.Traffic.Proxy;
            namespace App;
            public class C { }
            """, "TC.Traffic.Control.Main", new Dictionary<string, string>
        {
            ["tier_layer.forbidden_reference"] = "TC.Traffic.Control* => TC.Traffic.Proxy",
        });

        diags.Where(d => d.Id == "TCSG130").Should().HaveCount(1);
    }

    // === 130：命名空间左值（TCSG061 基础四面禁机制面的迁移通道）===

    [Fact]
    public async Task ForbiddenReference_NamespaceLeft_RestrictsToFilesInNamespace()
    {
        var config = new Dictionary<string, string>
        {
            ["tier_layer.forbidden_reference"] = "Acme.Wire => Acme.Raft | Acme.P2P | Acme.Swarm ; Acme.Transport => Acme.Raft",
        };

        // Wire 文件 using Raft → 报
        var wireUsingRaft = await AnalyzeAsync("""
            using Acme.Raft;
            namespace Acme.Wire;
            public class C { }
            """, "Acme.Net", config);
        wireUsingRaft.Where(d => d.Id == "TCSG130").Should().HaveCount(1);

        // Wire 文件 using P2P（同一规则多右值）→ 报
        var wireUsingP2P = await AnalyzeAsync("""
            using Acme.P2P;
            namespace Acme.Wire.Sub;
            public class C { }
            """, "Acme.Net", config);
        wireUsingP2P.Where(d => d.Id == "TCSG130").Should().HaveCount(1);

        // Transport 文件 using Raft → 报（第二条规则）
        var transportUsingRaft = await AnalyzeAsync("""
            using Acme.Raft;
            namespace Acme.Transport;
            public class C { }
            """, "Acme.Net", config);
        transportUsingRaft.Where(d => d.Id == "TCSG130").Should().HaveCount(1);

        // 机制文件自身 using 基础面 → 不报（方向是设计）
        var raftUsingWire = await AnalyzeAsync("""
            using Acme.Wire;
            namespace Acme.Raft;
            public class C { }
            """, "Acme.Net", config);
        raftUsingWire.Where(d => d.Id == "TCSG130").Should().BeEmpty("机制消费基础面 = 依赖正方向");
    }

    // === 130：元数据引用面 ===

    [Fact]
    public async Task ForbiddenReference_MetadataReference_ReportsTCSG130()
    {
        var proxyRef = BuildLibraryReference("TC.Traffic.Proxy",
            "namespace TC.Traffic.Proxy { public class P { } }");

        var diags = await AnalyzeAsync("""
            namespace App;
            public class C { }
            """, "TC.Traffic.Control", new Dictionary<string, string>
        {
            ["tier_layer.forbidden_reference"] = "TC.Traffic.Control => TC.Traffic.Proxy",
        }, proxyRef);

        diags.Where(d => d.Id == "TCSG130").Should().HaveCount(1, "ProjectReference 的分析器可见形态即被引程序集");
    }

    [Fact]
    public async Task ForbiddenReference_MetadataReference_OtherAssembly_NotReported()
    {
        var proxyRef = BuildLibraryReference("TC.Traffic.Proxy",
            "namespace TC.Traffic.Proxy { public class P { } }");

        var diags = await AnalyzeAsync("""
            namespace App;
            public class C { }
            """, "TC.Traffic.Control", new Dictionary<string, string>
        {
            ["tier_layer.forbidden_reference"] = "TC.Traffic.Other => TC.Traffic.Proxy",
        }, proxyRef);

        diags.Where(d => d.Id == "TCSG130").Should().BeEmpty("左值不匹配");
    }

    // === 131：零内部依赖 ===

    [Fact]
    public async Task ZeroInternalRefs_ReferencingFamilyAssembly_ReportsTCSG131()
    {
        var siblingRef = BuildLibraryReference("TC.Traffic.Engine",
            "namespace TC.Traffic.Engine { public class E { } }");

        var diags = await AnalyzeAsync("""
            namespace TC.Traffic.Contracts;
            public class C { }
            """, "TC.Traffic.Contracts", new Dictionary<string, string>
        {
            ["tier_layer.zero_internal_refs"] = "TC.Traffic.Contracts",
            ["tier_layer.internal_assembly_prefix"] = "TC.Traffic.",
        }, siblingRef);

        diags.Where(d => d.Id == "TCSG131").Should().HaveCount(1);
    }

    [Fact]
    public async Task ZeroInternalRefs_ReferencingExternalAssembly_NotReported()
    {
        var externalRef = BuildLibraryReference("ThirdParty.Lib",
            "namespace ThirdParty { public class L { } }");

        var diags = await AnalyzeAsync("""
            namespace TC.Traffic.Contracts;
            public class C { }
            """, "TC.Traffic.Contracts", new Dictionary<string, string>
        {
            ["tier_layer.zero_internal_refs"] = "TC.Traffic.Contracts",
            ["tier_layer.internal_assembly_prefix"] = "TC.Traffic.",
        }, externalRef);

        diags.Where(d => d.Id == "TCSG131").Should().BeEmpty("外部程序集不在内部家族前缀内");
    }

    // === 132：引用白名单 ===

    [Fact]
    public async Task AllowedReference_OutsideWhitelist_ReportsTCSG132()
    {
        var allowedRef = BuildLibraryReference("App.Allowed", "namespace App.Allowed { public class A { } }");
        var strangerRef = BuildLibraryReference("App.Stranger", "namespace App.Stranger { public class S { } }");

        var diags = await AnalyzeAsync("""
            namespace App.Main;
            public class C { }
            """, "App.Main", new Dictionary<string, string>
        {
            ["tier_layer.allowed_reference"] = "App.Main => App.Allowed",
        }, allowedRef, strangerRef);

        var violations = diags.Where(d => d.Id == "TCSG132").ToList();
        violations.Should().HaveCount(1);
        violations[0].GetMessage().Should().Contain("App.Stranger");
    }

    [Fact]
    public async Task AllowedReference_ExtraFrameworkPrefix_ExemptsConfiguredAssembly()
    {
        var frameworkRef = BuildLibraryReference("Acme.Framework.Core",
            "namespace Acme.Framework { public class F { } }");
        var strangerRef = BuildLibraryReference("App.Stranger", "namespace App.Stranger { public class S { } }");

        var diags = await AnalyzeAsync("""
            namespace App.Main;
            public class C { }
            """, "App.Main", new Dictionary<string, string>
        {
            ["tier_layer.allowed_reference"] = "App.Main => App.Allowed",
            ["tier_layer.allowed_framework_prefix"] = "Acme.Framework",
        }, frameworkRef, strangerRef);

        var violations = diags.Where(d => d.Id == "TCSG132").ToList();
        violations.Should().HaveCount(1, "框架豁免追加只豁免 Acme.Framework.Core");
        violations[0].GetMessage().Should().Contain("App.Stranger");
    }

    // === 133：命名空间归属 ===

    [Fact]
    public async Task NamespacePrefix_PublicTypeOutsidePrefix_ReportsTCSG133()
    {
        var diags = await AnalyzeAsync("""
            namespace TC.Traffic.Contracts
            {
                public class Good { }
                public class Bad { }
            }
            namespace Other
            {
                public class Leaked { }
            }
            """, "TC.Traffic.Contracts", new Dictionary<string, string>
        {
            ["tier_layer.namespace_prefix"] = "TC.Traffic.Contracts => TC.Traffic.Contracts",
        });

        diags.Where(d => d.Id == "TCSG133").Should().ContainSingle("Leaked 越界，Good 合规");
    }

    [Fact]
    public async Task NamespacePrefix_NestedPublicInsidePublic_InPrefix_NotReported()
    {
        var diags = await AnalyzeAsync("""
            namespace TC.Traffic.Contracts;
            public class Holder
            {
                public class Nested { }
            }
            """, "TC.Traffic.Contracts", new Dictionary<string, string>
        {
            ["tier_layer.namespace_prefix"] = "TC.Traffic.Contracts => TC.Traffic.Contracts",
        });

        diags.Where(d => d.Id == "TCSG133").Should().BeEmpty();
    }

    [Fact]
    public async Task NamespacePrefix_InternalTypeOutsidePrefix_NotReported()
    {
        var diags = await AnalyzeAsync("""
            namespace Elsewhere;
            internal class Internal1 { }
            """, "TC.Traffic.Contracts", new Dictionary<string, string>
        {
            ["tier_layer.namespace_prefix"] = "TC.Traffic.Contracts => TC.Traffic.Contracts",
        });

        diags.Where(d => d.Id == "TCSG133").Should().BeEmpty("非公开类型不在契约面");
    }

    // === 程序集名大小写不敏感（#491 回归——左值/右值与实际 AssemblyName 大小写不一致
    //     曾导致 131/132 规则整体静默失活，配置校验 139 却正常开火）===

    [Fact]
    public async Task ZeroInternalRefs_AssemblyNameCaseMismatch_StillReportsTCSG131()
    {
        var siblingRef = BuildLibraryReference("tc.traffic.engine",
            "namespace Traffic.Engine { public class E { } }");

        var diags = await AnalyzeAsync("""
            namespace Traffic.Contracts;
            public class C { }
            """, "tc.traffic.contracts", new Dictionary<string, string>
        {
            ["tier_layer.zero_internal_refs"] = "TC.Traffic.Contracts",
            ["tier_layer.internal_assembly_prefix"] = "TC.Traffic.",
        }, siblingRef);

        diags.Where(d => d.Id == "TCSG131").Should().HaveCount(1,
            "CLR 程序集名比较大小写不敏感——左值与 AssemblyName 大小写不一致不得静默失活");
    }

    [Fact]
    public async Task AllowedReference_AssemblyNameCaseMismatch_StillReportsTCSG132()
    {
        var allowedRef = BuildLibraryReference("app.allowed", "namespace App.Allowed { public class A { } }");
        var strangerRef = BuildLibraryReference("App.Stranger", "namespace App.Stranger { public class S { } }");

        var diags = await AnalyzeAsync("""
            namespace App.Main;
            public class C { }
            """, "app.main", new Dictionary<string, string>
        {
            ["tier_layer.allowed_reference"] = "App.Main => App.Allowed",
        }, allowedRef, strangerRef);

        var violations = diags.Where(d => d.Id == "TCSG132").ToList();
        violations.Should().HaveCount(1, "左值大小写不匹配仍激活规则；右值大小写不匹配仍入白名单");
        violations[0].GetMessage().Should().Contain("App.Stranger");
    }

    [Fact]
    public async Task AllowedReference_WildcardLeftCaseMismatch_StillReportsTCSG132()
    {
        var strangerRef = BuildLibraryReference("App.Stranger", "namespace App.Stranger { public class S { } }");

        var diags = await AnalyzeAsync("""
            namespace App.Main;
            public class C { }
            """, "app.main", new Dictionary<string, string>
        {
            ["tier_layer.allowed_reference"] = "APP.MAIN* => App.Nothing",
        }, strangerRef);

        diags.Where(d => d.Id == "TCSG132").Should().HaveCount(1, "前缀通配左值同样大小写不敏感");
    }

    [Fact]
    public async Task NamespacePrefix_NamespaceCaseMismatch_IsOutsidePrefix_ReportsTCSG133()
    {
        var diags = await AnalyzeAsync("""
            namespace other;
            public class Leaked { }
            """, "App", new Dictionary<string, string>
        {
            ["tier_layer.namespace_prefix"] = "App => Other",
        });

        diags.Where(d => d.Id == "TCSG133").Should().HaveCount(1,
            "命名空间是区分大小写的 C# 标识符——大小写不匹配 = 越界（与程序集名语义有意相反）");
    }

    // === 零默认诊断 ===

    [Fact]
    public async Task Unconfigured_ZeroDiagnostics_EvenWithViolations()
    {
        var diags = await AnalyzeAsync("""
            using TC.Traffic.Proxy;
            namespace App;
            public class C
            {
                public object B() => (object)42;
            }
            """, "TC.Traffic.Control", EmptyConfig);

        diags.Where(d => d.Id.StartsWith("TCSG1", System.StringComparison.Ordinal))
            .Should().BeEmpty("零默认诊断：装包未配置零打扰");
    }

    // === 139：配置非法 fail-fast ===

    [Fact]
    public async Task UnknownKey_ReportsTCSG139()
    {
        var diags = await AnalyzeAsync("namespace App; public class C { }", "App", new Dictionary<string, string>
        {
            ["tier_layer.forbidden_refernce"] = "A => B",   // 拼写错误的键
        });

        diags.Where(d => d.Id == "TCSG139").Should().HaveCount(1);
        diags.First(d => d.Id == "TCSG139").Severity.Should().Be(Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task UnknownKey_DifferentCase_StillReportsTCSG139()
    {
        var diags = await AnalyzeAsync("namespace App; public class C { }", "App", new Dictionary<string, string>
        {
            ["TIER_LAYER.TOTALY_BOGUS_KEY"] = "A => B",   // 编辑器配置键大小写不敏感——仍属未知键
        });

        diags.Where(d => d.Id == "TCSG139").Should().HaveCount(1, "键比较大小写不敏感，改大小写不绕过校验");
    }

    [Fact]
    public async Task BadSyntax_MissingArrow_ReportsTCSG139()
    {
        var diags = await AnalyzeAsync("namespace App; public class C { }", "App", new Dictionary<string, string>
        {
            ["tier_layer.forbidden_reference"] = "App B 没有箭头",
        });

        diags.Where(d => d.Id == "TCSG139").Should().HaveCount(1);
    }

    [Fact]
    public async Task ZeroInternalRefs_MissingPrefixKey_ReportsTCSG139()
    {
        var diags = await AnalyzeAsync("namespace App; public class C { }", "App", new Dictionary<string, string>
        {
            ["tier_layer.zero_internal_refs"] = "App",
        });

        diags.Where(d => d.Id == "TCSG139").Should().HaveCount(1, "规则依赖键缺失 = 配置不完整 fail-fast");
    }

    [Fact]
    public async Task OtherNamespaceKeys_NotValidated()
    {
        var diags = await AnalyzeAsync("namespace App; public class C { }", "App", new Dictionary<string, string>
        {
            ["build_property.Foo"] = "bar",   // 其他通道键——不属于本分析器管辖
        });

        diags.Where(d => d.Id == "TCSG139").Should().BeEmpty("键命名空间外零管辖");
    }
}
