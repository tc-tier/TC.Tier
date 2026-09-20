using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace TC.Tier.Analyzers.Tests;

/// <summary>
/// 禁用模式规则族测试（#439 件二）——TCSG134-138 正反例 × 配置变体
/// （pack 开关/豁免前缀/热路径作用域/未配置零报告/非法配置 TCSG139 fail-fast）。
/// </summary>
public class ForbiddenPatternsTests
{
    private static Task Delay() => Task.Delay(1);

    // === 零默认诊断 ===

    [Fact]
    public async Task Unconfigured_ZeroDiagnostics_EvenWithViolations()
    {
        var diags = await AnalyzerTestDriver.AnalyzeWithAsync("""
            using System.Threading.Tasks;
            namespace App;
            public class C
            {
                public void M()
                {
                    _ = Delay();
                    var t = typeof(C).GetMethod("M");
                    var x = Task.Delay(1).GetAwaiter().GetResult();
                }
                static Task Delay() => Task.Delay(1);
            }
            """, "App", new Dictionary<string, string>(),
            new TierGovernanceAnalyzer());

        diags.Where(d => d.Id is "TCSG134" or "TCSG135" or "TCSG136" or "TCSG137" or "TCSG138")
            .Should().BeEmpty("零默认诊断：pack 未声明零报告");
    }

    // === 136：反射（判定自 TCSG136 原样迁移）===

    [Fact]
    public async Task Reflection_TypeGetMethod_ReportsTCSG136()
    {
        var diags = await AnalyzePatternsAsync("var m = typeof(C).GetMethod(\"M\");");
        diags.Where(d => d.Id == "TCSG136").Should().HaveCount(1);
    }

    [Fact]
    public async Task Reflection_SameNameOnBusinessType_NotReported()
    {
        var diags = await AnalyzePatternsAsync("""
            var repo = new Repo();
            var x = repo.GetValue();
            class Repo { public string GetValue() => ""; }
            """);
        diags.Where(d => d.Id == "TCSG136").Should().BeEmpty("业务同名方法不误伤（语义确认接收者）");
    }

    // === 137：sync-over-async（判定自 TCSG137 原样迁移）===

    [Fact]
    public async Task SyncOverAsync_GetResult_ReportsTCSG137()
    {
        var diags = await AnalyzePatternsAsync("var x = Delay().GetAwaiter().GetResult();");
        diags.Where(d => d.Id == "TCSG137").Should().HaveCount(1);
    }

    [Fact]
    public async Task SyncOverAsync_TaskWait_ReportsTCSG137()
    {
        var diags = await AnalyzePatternsAsync("""
            var t = Delay();
            t.Wait();
            """);
        diags.Where(d => d.Id == "TCSG137").Should().HaveCount(1);
    }

    [Fact]
    public async Task SyncOverAsync_BusinessWaitNamedMethod_NotReported()
    {
        var diags = await AnalyzePatternsAsync("""
            var g = new Gate();
            g.WaitForReady();
            class Gate { public void WaitForReady() { } }
            """);
        diags.Where(d => d.Id == "TCSG137").Should().BeEmpty("WaitForX 业务名不误伤");
    }

    // === 138：fire-and-forget（判定自 TCSG138 原样迁移）===

    [Fact]
    public async Task FireAndForget_TaskDiscard_ReportsTCSG138()
    {
        var diags = await AnalyzePatternsAsync("_ = Delay();");
        diags.Where(d => d.Id == "TCSG138").Should().HaveCount(1);
    }

    [Fact]
    public async Task FireAndForget_NonTaskDiscard_NotReported()
    {
        var diags = await AnalyzePatternsAsync("_ = 42;");
        diags.Where(d => d.Id == "TCSG138").Should().BeEmpty("非 Task 的 _ = 习惯用法不误伤");
    }

    [Fact]
    public async Task FireAndForget_DehconstructionPattern_NotReported()
    {
        var diags = await AnalyzePatternsAsync("""
            var (_, second) = (1, 2);
            _ = second;
            """);
        diags.Where(d => d.Id == "TCSG138").Should().BeEmpty("解构形态非本规则面");
    }

    // === 134：裸线程 ===

    [Theory]
    [InlineData("new System.Threading.Thread(() => { });")]
    [InlineData("System.Threading.Thread.Sleep(10);")]
    [InlineData("System.Threading.Tasks.Task.Run(() => { });")]
    [InlineData("new System.Threading.PeriodicTimer(System.TimeSpan.FromSeconds(1));")]
    [InlineData("new System.Threading.SemaphoreSlim(1);")]
    [InlineData("new System.Threading.Mutex();")]
    [InlineData("new System.Threading.ManualResetEvent(false);")]
    [InlineData("new System.Threading.ManualResetEventSlim(false);")]
    public async Task BareThreads_Primitives_ReportsTCSG134(string violation)
    {
        var diags = await AnalyzePatternsAsync(violation, pack: "bare_threads");
        diags.Where(d => d.Id == "TCSG134").Should().HaveCount(1, violation);
    }

    [Fact]
    public async Task BareThreads_NewTimer_ReportsTCSG134()
    {
        // #500 验收：System.Threading.Timer 构造入清单——BannedSymbols.txt 最后 1 行对账收口
        var diags = await AnalyzePatternsAsync("new System.Threading.Timer(_ => { }, null, 1, 1);", pack: "bare_threads");
        diags.Where(d => d.Id == "TCSG134").Should().HaveCount(1, "new Timer(...) 裸定时漏检收口");
    }

    [Fact]
    public async Task BareThreads_Timer_ExemptPrefixStillWorks()
    {
        var config = new Dictionary<string, string>
        {
            ["tier_forbidden.pack"] = "bare_threads",
            ["tier_forbidden.exempt.bare_threads"] = "App.Execution",
        };

        var exempt = await AnalyzerTestDriver.AnalyzeWithAsync("""
            namespace App.Execution
            {
                public class Scheduler
                {
                    public void M() { new System.Threading.Timer(_ => { }, null, 1, 1); }
                }
            }
            """, "App", config, new TierGovernanceAnalyzer());
        exempt.Where(d => d.Id == "TCSG134").Should().BeEmpty("Timer 构造豁免照常生效");

        var notExempt = await AnalyzerTestDriver.AnalyzeWithAsync("""
            namespace App.Web
            {
                public class Scheduler
                {
                    public void M() { new System.Threading.Timer(_ => { }, null, 1, 1); }
                }
            }
            """, "App", config, new TierGovernanceAnalyzer());
        notExempt.Where(d => d.Id == "TCSG134").Should().HaveCount(1);
    }

    [Fact]
    public async Task BareThreads_AdditionalConfig_OrgTypeTriggers()
    {
        // 配置化扩展（#500 评审裁定）：追加类型零代码——.editorconfig/.globalconfig 一行即生效
        var config = new Dictionary<string, string>
        {
            ["tier_forbidden.pack"] = "bare_threads",
            ["tier_forbidden.bare_threads.additional"] = "Traffic.Infra.BareSpinner",
        };

        var diags = await AnalyzerTestDriver.AnalyzeMultiTreeAsync(
        [
            """
            namespace Traffic.Infra;
            public class BareSpinner { }
            """,
            "new Traffic.Infra.BareSpinner();",
        ], "App", config, new Dictionary<string, string>());
        diags.Where(d => d.Id == "TCSG134").Should().HaveCount(1, "追加清单精确全名命中");

        var unconfigured = await AnalyzerTestDriver.AnalyzeMultiTreeAsync(
        [
            """
            namespace Traffic.Infra;
            public class BareSpinner { }
            """,
            "new Traffic.Infra.BareSpinner();",
        ], "App",
            new Dictionary<string, string> { ["tier_forbidden.pack"] = "bare_threads" },
            new Dictionary<string, string>());
        unconfigured.Where(d => d.Id == "TCSG134").Should().BeEmpty("未配置追加 = 内建底线不变（add-only 语义）");
    }

    [Fact]
    public async Task BareThreads_AdditionalConfig_Wildcard_TCSG139()
    {
        var diags = await AnalyzePatternsAsync("var x = 1;", pack: "bare_threads",
            extra: new Dictionary<string, string>
            {
                ["tier_forbidden.bare_threads.additional"] = "App.* | App.B?",
            });
        diags.Where(d => d.Id == "TCSG139").Should().NotBeEmpty("通配非精确全名——逐项 fail-fast 绝静默");
    }

    [Fact]
    public async Task BareThreads_LockStatement_NotReported()
    {
        var diags = await AnalyzePatternsAsync("""
            var gate = new object();
            lock (gate) { }
            """, pack: "bare_threads");
        diags.Where(d => d.Id == "TCSG134").Should().BeEmpty("lock 是合法惯用，不报");
    }

    [Fact]
    public async Task BareThreads_SameNameCustomType_NotReported()
    {
        var diags = await AnalyzePatternsAsync("""
            new MyThread();
            class MyThread { }
            """, pack: "bare_threads");
        diags.Where(d => d.Id == "TCSG134").Should().BeEmpty("同名自定义类型不误伤（语义确认命名空间）");
    }

    [Fact]
    public async Task BareThreads_ExemptPrefix_Suppresses()
    {
        var config = new Dictionary<string, string>
        {
            ["tier_forbidden.pack"] = "bare_threads",
            ["tier_forbidden.exempt.bare_threads"] = "App.Execution | App.Infra",
        };

        var exempt = await AnalyzerTestDriver.AnalyzeWithAsync("""
            namespace App.Execution
            {
                public class Pumper
                {
                    public void M() { System.Threading.Thread.Sleep(10); }
                }
            }
            """, "App", config, new TierGovernanceAnalyzer());
        exempt.Where(d => d.Id == "TCSG134").Should().BeEmpty("豁免前缀命中 = 组件本体受控豁免");

        var notExempt = await AnalyzerTestDriver.AnalyzeWithAsync("""
            namespace App.Web
            {
                public class Handler
                {
                    public void M() { System.Threading.Thread.Sleep(10); }
                }
            }
            """, "App", config, new TierGovernanceAnalyzer());
        notExempt.Where(d => d.Id == "TCSG134").Should().HaveCount(1);
    }

    // === 135：热路径纪律 ===

    private const string HotPathAttributeSource = """
        namespace App
        {
            public sealed class HotPathAttribute : System.Attribute { }
        }
        """;

    [Theory]
    [InlineData("var xs = new[] { 1, 2 }.Where(i => i > 0);", "LINQ")]
    [InlineData("object o = 42;", "装箱")]
    [InlineData("var s = $\"v={1}\";", "内插")]
    [InlineData("var s = string.Format(\"{0}\", 1);", "string.Format")]
    [InlineData("var s = string.Concat(\"a\", \"b\");", "string.Concat")]
    [InlineData("var s = 42.ToString();", "值类型 ToString")]
    public async Task HotPath_ViolationClass_ReportsTCSG135(string violation, string reason)
    {
        var config = new Dictionary<string, string>
        {
            ["tier_forbidden.pack"] = "hotpath_discipline",
            ["tier_forbidden.scope.hotpath_discipline"] = "members_with([App.HotPathAttribute])",
        };

        var source = $$"""
            using System.Linq;
            {{HotPathAttributeSource}}
            namespace App
            {
                public class C
                {
                    [HotPath]
                    public void M()
                    {
                        {{violation}}
                    }
                }
            }
            """;

        var diags = await AnalyzerTestDriver.AnalyzeWithAsync(source, "App", config,
            new TierGovernanceAnalyzer());
        var hits = diags.Where(d => d.Id == "TCSG135").ToList();
        hits.Should().NotBeEmpty(reason);
        hits.Any(d => d.GetMessage().Contains(reason)).Should().BeTrue(
            $"应命中 {reason} 类违禁——同一标注方法可能多类并发（如 string.Format 的实参装箱）");
    }

    [Fact]
    public async Task HotPath_UnmarkedMethod_NotReported()
    {
        var config = new Dictionary<string, string>
        {
            ["tier_forbidden.pack"] = "hotpath_discipline",
            ["tier_forbidden.scope.hotpath_discipline"] = "members_with([App.HotPathAttribute])",
        };

        var diags = await AnalyzerTestDriver.AnalyzeWithAsync($$"""
            {{HotPathAttributeSource}}
            namespace App
            {
                public class C
                {
                    public void M()
                    {
                        object o = 42;
                    }
                }
            }
            """, "App", config, new TierGovernanceAnalyzer());
        diags.Where(d => d.Id == "TCSG135").Should().BeEmpty("未标注方法不在检查集（v1 作用域 = 标注方法体）");
    }

    // === 139：配置非法 fail-fast ===

    [Fact]
    public async Task UnknownPackToken_ReportsTCSG139()
    {
        var diags = await AnalyzePatternsAsync("var x = 1;", pack: "reflekshun");
        diags.Where(d => d.Id == "TCSG139").Should().HaveCount(1);
    }

    [Fact]
    public async Task UnknownKey_ReportsTCSG139()
    {
        var diags = await AnalyzeWithConfigAsync(new Dictionary<string, string>
        {
            ["tier_forbidden.exempt.bar_threads"] = "App",   // 拼写错误
        });
        diags.Where(d => d.Id == "TCSG139").Should().HaveCount(1);
    }

    [Fact]
    public async Task HotPath_PackWithoutScope_ReportsTCSG139()
    {
        var diags = await AnalyzeWithConfigAsync(new Dictionary<string, string>
        {
            ["tier_forbidden.pack"] = "hotpath_discipline",
        });
        diags.Where(d => d.Id == "TCSG139").Should().HaveCount(1, "作用域缺失 = 配置不完整");
    }

    [Fact]
    public async Task HotPath_ScopeWithoutPack_ReportsTCSG139()
    {
        var diags = await AnalyzeWithConfigAsync(new Dictionary<string, string>
        {
            ["tier_forbidden.scope.hotpath_discipline"] = "members_with([App.HotPathAttribute])",
        });
        diags.Where(d => d.Id == "TCSG139").Should().HaveCount(1, "作用域无生效规则 = 配置错误");
    }

    [Fact]
    public async Task BadScopeSyntax_ReportsTCSG139()
    {
        var diags = await AnalyzeWithConfigAsync(new Dictionary<string, string>
        {
            ["tier_forbidden.pack"] = "hotpath_discipline",
            ["tier_forbidden.scope.hotpath_discipline"] = "everywhere",
        });
        diags.Where(d => d.Id == "TCSG139").Should().HaveCount(1, "v1 仅支持 members_with([FQN]) 形态");
    }

    // === 驱动便捷封装 ===

    private static Task<System.Collections.Immutable.ImmutableArray<Microsoft.CodeAnalysis.Diagnostic>> AnalyzePatternsAsync(
        string bodyStatement, string pack = "reflection | sync_over_async | fire_and_forget",
        IReadOnlyDictionary<string, string>? extra = null)
    {
        var config = new Dictionary<string, string> { ["tier_forbidden.pack"] = pack };
        if (extra is not null)
            foreach (var (key, value) in extra) config[key] = value;
        return AnalyzeWithConfigAsync(config, bodyStatement);
    }

    private static Task<System.Collections.Immutable.ImmutableArray<Microsoft.CodeAnalysis.Diagnostic>> AnalyzeWithConfigAsync(
        Dictionary<string, string> config, string? bodyStatement = null)
    {
        var body = bodyStatement ?? "";
        var source = $$"""
            using System.Threading.Tasks;
            namespace App
            {
                public class C
                {
                    public void M()
                    {
                        {{body}}
                    }
                    static Task Delay() => Task.Delay(1);
                }
            }
            """;
        return AnalyzerTestDriver.AnalyzeWithAsync(source, "App", config, new TierGovernanceAnalyzer());
    }
}
