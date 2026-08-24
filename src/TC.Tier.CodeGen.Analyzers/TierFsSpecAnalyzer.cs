using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace TC.Tier.CodeGen.Analyzers;

/// <summary>
/// TierFs const spec 诊断器（spec-typed-frontend-and-generator-design §4 P-d）——L1 运行时校验的
/// 编译期前置层：识别 <c>TierFs.New/Open</c> 常量字符串实参 → 内嵌语法真值表诊断
/// （scheme 非法 / 参数名未知 / 参数 × 介质违规）。非常量字符串零诊断（运行时校验保留——纵深防御）。
/// <para>★ 真值表同源保证：本表与 TierSpec 参数面的一致性由 Core 契约测试钉死
///   （TierFsSpecAnalyzerTableTests——TierSpec 属性集漂移即红）。</para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TierFsSpecAnalyzer : DiagnosticAnalyzer
{
    private const string Category = "TierFs";

    private static readonly DiagnosticDescriptor BadSchemeRule = new(
        id: "TCSG120",   // ★ 缺陷 2：与生成器 RingKeyGenerator 的 TCSG020 冲突——分析器段 1xx 隔离
        title: "TierFs spec unknown scheme",
        messageFormat: "spec scheme '{0}' 未知（合法：local:// / local: / memory: / virtual:// / network:///）。",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor BadParamRule = new(
        id: "TCSG121",
        title: "TierFs spec unknown parameter",
        messageFormat: "spec 参数 '{0}' 未知（合法集：label/quota/access/exclusive/spill/cred/region/vhost/tls/member）。",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ParamMediaRule = new(
        id: "TCSG122",
        title: "TierFs spec parameter not valid for medium",
        messageFormat: "spec 参数 '{0}' 不适用于 {1}（{0} 的介质归属：{2}）。",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>参数 → 适用介质（回退表——TierSpec 符号不可得时用；正常路径从编译内 [SpecParam] 符号派生——单一事实源）。</summary>
    public static readonly ImmutableDictionary<string, string> ParamMedia =
        new[]
        {
            ("label", "all"), ("quota", "all"), ("access", "all"), ("exclusive", "all"),
            ("spill", "network"), ("cred", "network"), ("region", "network"), ("vhost", "network"), ("tls", "network"),
            ("member", "virtual"),
        }.ToImmutableDictionary(p => p.Item1, p => p.Item2, StringComparer.Ordinal);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        => ImmutableArray.Create(BadSchemeRule, BadParamRule, ParamMediaRule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    /// <summary>TierSpec 属性名 → query 参数名（符号派生路径用——与 TierSpec.ToString 序列化同源）。</summary>
    private static readonly ImmutableDictionary<string, string> PropertyToQuery =
        new[]
        {
            ("Label", "label"), ("QuotaBytes", "quota"), ("Access", "access"), ("Exclusive", "exclusive"),
            ("Spill", "spill"), ("CredentialRef", "cred"), ("Region", "region"),
            ("VirtualHostAddressing", "vhost"), ("Tls", "tls"), ("Members", "member"),
        }.ToImmutableDictionary(p => p.Item1, p => p.Item2, StringComparer.Ordinal);

    // RS1008 抑制理由：ConditionalWeakTable 即 Roslyn 官方推荐的按编译弱键缓存模式（键不延长编译寿命）
#pragma warning disable RS1008
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Compilation, ImmutableDictionary<string, string>> TableCache = new();
#pragma warning restore RS1008

    /// <summary>真值表单源化：被分析编译内可解析 TierSpec 时从其 [SpecParam] 符号派生（单一事实源）；
    /// 不可解析（如无 Core 引用的冒烟编译）→ 静态回退表。</summary>
    private static ImmutableDictionary<string, string> ResolveTable(SyntaxNodeAnalysisContext ctx)
    {
        var cached = TableCache.GetValue(ctx.Compilation, comp =>
        {
            var tierSpec = comp.GetTypeByMetadataName("TC.Tier.Core.IO.TierSpec");
            if (tierSpec is null)
                return ParamMedia;
            var props = tierSpec.GetMembers().OfType<IPropertySymbol>()
                .Select(p2 => (p2.Name, Attr: p2.GetAttributes()
                    .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "TC.Tier.CodeGen.SpecParamAttribute")))
                .Where(t => t.Attr is not null)
                .Select(t => (t.Name, Attr: t.Attr!))
                .ToList();
            if (props.Count == 0)
                return ParamMedia;
            var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            foreach (var (propName, attr) in props)
            {
                if (!PropertyToQuery.TryGetValue(propName, out var query)) continue;
                var media = "all";
                foreach (var named in attr.NamedArguments)
                    if (named.Key == "Media" && named.Value.Value is string m)
                        media = m;
                builder[query] = media;
            }
            return builder.Count > 0 ? builder.ToImmutable() : ParamMedia;
        });
        return cached;
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext ctx)
    {
        var invocation = (InvocationExpressionSyntax)ctx.Node;
        if (invocation.ArgumentList.Arguments.Count == 0) return;
        var name = (invocation.Expression as MemberAccessExpressionSyntax)?.Name.Identifier.ValueText as string;
        if (name != "New" && name != "Open" && name != "OpenOrCreate") return;

        // 解析 TierFs.New/Open 符号（防同名误报）
        var symbol = ctx.SemanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
        if (symbol is null || symbol.ContainingType?.Name != "TierFs") return;

        var first = invocation.ArgumentList.Arguments[0];
        if (first.Expression is not LiteralExpressionSyntax literal || !literal.IsKind(SyntaxKind.StringLiteralExpression))
            return;   // 非常量——运行时校验管辖（L1 纵深防御）
        var spec = literal.Token.ValueText;

        // 快捷形态（无冒号裸 local / memory?…）合法——先切 query 再取冒号前缀
        var queryStart = spec.IndexOf('?');
        var head = queryStart < 0 ? spec : spec.Substring(0, queryStart);
        var schemeEnd = head.IndexOf(":", StringComparison.Ordinal);
        var scheme = schemeEnd <= 0 ? head : head.Substring(0, schemeEnd);
        var table = ResolveTable(ctx);
        switch (scheme)
        {
            case "local":
            case "memory":
            case "virtual":
            case "network":
                break;
            default:
                ctx.ReportDiagnostic(Diagnostic.Create(BadSchemeRule, first.GetLocation(), scheme));
                return;
        }

        if (queryStart < 0) return;
        foreach (var piece in spec.Substring(queryStart + 1).Split('&'))
        {
            var key = piece.Split('=')[0];
            if (!table.TryGetValue(key, out var media))
            {
                ctx.ReportDiagnostic(Diagnostic.Create(BadParamRule, first.GetLocation(), key));
                continue;
            }
            if (media != "all" && media != scheme)
                ctx.ReportDiagnostic(Diagnostic.Create(ParamMediaRule, first.GetLocation(), key, scheme, media));
        }
    }
}
