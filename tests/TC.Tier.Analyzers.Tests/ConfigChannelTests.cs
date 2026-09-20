using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;
using static TC.Tier.Analyzers.Tests.AnalyzerTestDriver;

namespace TC.Tier.Analyzers.Tests;

/// <summary>
/// 治理配置读数通道测试（#459）——.globalconfig（GlobalOptions）与 .editorconfig（per-tree）
/// 双通道：树配置执法、双通道异键并集、同键同值不重复、跨源同键异值 TCSG139 fail-fast、
/// 禁用模式族树配置执法。
/// </summary>
public class ConfigChannelTests
{
    // === .editorconfig 通道（per-tree options）——#459 主回归 ===

    [Fact]
    public async Task TreeConfig_ForbiddenReference_UsingFace_ReportsTCSG130()
    {
        var diags = await AnalyzeMultiTreeAsync(
            ["""
            using TC.Traffic.Proxy;
            namespace App;
            public class C { }
            """], "TC.Traffic.Control",
            new Dictionary<string, string>(),
            new Dictionary<string, string>
            {
                ["tier_layer.forbidden_reference"] = "TC.Traffic.Control => TC.Traffic.Proxy",
            });

        diags.Where(d => d.Id == "TCSG130").Should().HaveCount(1);
    }

    [Fact]
    public async Task TreeConfig_ForbiddenReference_MetadataFace_ReportsTCSG130()
    {
        var lib = BuildLibraryReference("TC.Traffic.Proxy", "namespace TC.Traffic.Proxy { public class P { } }");
        var diags = await AnalyzeMultiTreeAsync(
            ["namespace App; public class C { }"], "TC.Traffic.Control",
            new Dictionary<string, string>(),
            new Dictionary<string, string>
            {
                ["tier_layer.forbidden_reference"] = "TC.Traffic.Control => TC.Traffic.Proxy",
            }, lib);

        diags.Where(d => d.Id == "TCSG130").Should().HaveCount(1);
    }

    [Fact]
    public async Task TreeConfig_ForbiddenPack_FireAndForget_ReportsTCSG138()
    {
        var diags = await AnalyzeMultiTreeAsync(
            ["_ = Delay(); static System.Threading.Tasks.Task Delay() => System.Threading.Tasks.Task.Delay(1);"],
            "App",
            new Dictionary<string, string>(),
            new Dictionary<string, string>
            {
                ["tier_forbidden.pack"] = "fire_and_forget",
            });

        diags.Where(d => d.Id == "TCSG138").Should().HaveCount(1);
    }

    // === 双通道合并语义 ===

    [Fact]
    public async Task DualChannel_UnionAcrossKeys_BothEnforced_NoConflict()
    {
        var lib = BuildLibraryReference("TC.Traffic.Proxy", "namespace TC.Traffic.Proxy { public class P { } }");
        var diags = await AnalyzeMultiTreeAsync(
            ["""
            using TC.Traffic.Proxy;
            namespace App;
            public class C { }
            """], "TC.Traffic.Control",
            new Dictionary<string, string>
            {
                // .globalconfig 通道：白名单键
                ["tier_layer.allowed_reference"] = "TC.Traffic.Control => TC.Traffic.Proxy",
            },
            new Dictionary<string, string>
            {
                // .editorconfig 通道：禁引用键（异键并集无损）
                ["tier_layer.forbidden_reference"] = "TC.Traffic.Control => TC.Traffic.Proxy",
            }, lib);

        diags.Where(d => d.Id == "TCSG130").Should().HaveCount(2, "using 命名空间面＋元数据引用面双形态独立检测（既有语义）");
        diags.Where(d => d.Id == "TCSG139").Should().BeEmpty("异键并集不产生冲突");
    }

    [Fact]
    public async Task CrossSource_SameKeySameValue_SingleEnforcement()
    {
        var diags = await AnalyzeMultiTreeAsync(
            ["""
            using TC.Traffic.Proxy;
            namespace App;
            public class C { }
            """], "TC.Traffic.Control",
            new Dictionary<string, string>
            {
                ["tier_layer.forbidden_reference"] = "TC.Traffic.Control => TC.Traffic.Proxy",
            },
            new Dictionary<string, string>
            {
                ["tier_layer.forbidden_reference"] = "TC.Traffic.Control => TC.Traffic.Proxy",
            });

        diags.Where(d => d.Id == "TCSG130").Should().HaveCount(1, "同值不冲突且合并键不重复枚举");
        diags.Where(d => d.Id == "TCSG139").Should().BeEmpty();
    }

    [Fact]
    public async Task CrossSource_SameKeyDifferentValue_FailFastTCSG139()
    {
        var diags = await AnalyzeMultiTreeAsync(
            ["""
            using TC.Traffic.Proxy;
            namespace App;
            public class C { }
            """], "TC.Traffic.Control",
            new Dictionary<string, string>
            {
                ["tier_layer.forbidden_reference"] = "TC.Traffic.Control => TC.Traffic.Proxy",
            },
            new Dictionary<string, string>
            {
                ["tier_layer.forbidden_reference"] = "TC.Traffic.Control => TC.Traffic.Other",
            });

        diags.Where(d => d.Id == "TCSG139").Should().HaveCount(1, "同键异值 = 配置冲突，fail-fast 绝静默取一");
        diags.Where(d => d.Id == "TCSG130").Should().BeEmpty("配置非法不降级继续执法");
    }

    [Fact]
    public async Task CrossSource_KeyCaseDifferent_SameValue_SingleEnforcement_NoConflict()
    {
        var diags = await AnalyzeMultiTreeAsync(
            ["""
            using TC.Traffic.Proxy;
            namespace App;
            public class C { }
            """], "TC.Traffic.Control",
            new Dictionary<string, string>
            {
                ["tier_layer.forbidden_reference"] = "TC.Traffic.Control => TC.Traffic.Proxy",
            },
            new Dictionary<string, string>
            {
                // 编辑器配置键规范即大小写不敏感——跨源异大小写同键按同键合并（不误报冲突、不双记）
                ["TIER_LAYER.FORBIDDEN_REFERENCE"] = "TC.Traffic.Control => TC.Traffic.Proxy",
            });

        diags.Where(d => d.Id == "TCSG130").Should().HaveCount(1, "同键（异大小写）同值 = 单次执法");
        diags.Where(d => d.Id == "TCSG139").Should().BeEmpty("不误报配置冲突");
    }
}
