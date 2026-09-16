using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace TC.Tier.Analyzers;

/// <summary>
/// TC.Tier 配置化治理分析器（单一 DiagnosticAnalyzer 对外——分层依赖族 + 禁用模式族）：
/// <para>★ 件一（#438）分层依赖——<c>tier_layer.*</c> 键：TCSG130 禁用引用（元数据引用/using
///   双形态）/ TCSG131 零内部依赖违约 / TCSG132 引用白名单越界 / TCSG133 命名空间归属越界。</para>
/// <para>★ 件二（#439）禁用模式——<c>tier_forbidden.pack</c> 键：TCSG134 裸线程原语 /
///   TCSG135 热路径分配纪律 / TCSG136 禁反射 / TCSG137 禁 sync-over-async / TCSG138 禁丢弃。</para>
/// <para>★ TCSG139 配置非法 fail-fast（未知键/坏语法/依赖缺失——绝静默）。</para>
/// <para>零默认诊断：配置未声明 = 零注册零报告（装包未配置零打扰）。</para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TierGovernanceAnalyzer : DiagnosticAnalyzer
{
    /// <summary>全部支持诊断（两规则族 + 配置非法）。</summary>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
    {
        get
        {
            var builder = ImmutableArray.CreateBuilder<DiagnosticDescriptor>();
            builder.AddRange(LayerDependencyRules.SupportedDiagnostics);
            builder.AddRange(ForbiddenPatternRules.SupportedDiagnostics);
            builder.Add(TierConfigDiagnostics.ConfigInvalidRule);
            return builder.ToImmutable();
        }
    }

    /// <summary>注册两规则族的编译起点回调（生成代码不分析、并发执行）。</summary>
    /// <param name="context">分析上下文。</param>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        LayerDependencyRules.Register(context);
        ForbiddenPatternRules.Register(context);
    }
}
