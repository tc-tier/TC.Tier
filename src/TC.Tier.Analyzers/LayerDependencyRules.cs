using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace TC.Tier.Analyzers;

/// <summary>
/// 分层依赖规则分析器——<c>tier_layer.*</c> 键配置驱动（件一，#438）：
/// <para>★ TCSG130 禁用引用（forbidden_reference）：<c>A =&gt; B1 | B2</c>——作用域内禁引用 B，
///   元数据引用面与 using 命名空间面双形态独立检测（B 同时按程序集名与命名空间前缀解释——
///   ProjectReference 的分析器可见形态即被引程序集，Traffic 实践证明双检必要）。</para>
/// <para>★ TCSG131 零内部依赖违约（zero_internal_refs）：作用域程序集引用了命中
///   internal_assembly_prefix 前缀的"兄弟内部项目"。</para>
/// <para>★ TCSG132 引用白名单越界（allowed_reference）：作用域程序集的引用不在白名单 ∪
///   框架豁免集（System/Microsoft/netstandard/mscorlib 前缀缺省 + allowed_framework_prefix 追加）。</para>
/// <para>★ TCSG133 命名空间归属越界（namespace_prefix）：作用域程序集的公开类型（可空性上
///   全链 public）落名命名空间不在声明前缀——"命名空间即归属"契约面收口。</para>
/// <para>★ TCSG139 配置非法：未知键、坏语法、规则依赖键缺失——fail-fast，配置错误绝静默。</para>
/// <para>零默认诊断：配置未声明任何规则 = 零注册零报告。左值程序集名匹配（精确 / 前缀 <c>*</c>
///   通配）；不匹配任何程序集名的左值按命名空间前缀解释（using 形态生效——命名空间粒度作用域，如基础四面禁机制面）。</para>
/// </summary>
/// <remarks>注册宿主 = <see cref="TierGovernanceAnalyzer"/>（包内单分析器类，诊断 ID 不跨类重复）。</remarks>
internal static class LayerDependencyRules
{
    private const string Category = "TierCode";
    private const string KeyNamespace = "tier_layer.";

    internal const string KeyForbiddenReference = "tier_layer.forbidden_reference";
    internal const string KeyZeroInternalRefs = "tier_layer.zero_internal_refs";
    internal const string KeyInternalAssemblyPrefix = "tier_layer.internal_assembly_prefix";
    internal const string KeyAllowedReference = "tier_layer.allowed_reference";
    internal const string KeyNamespacePrefix = "tier_layer.namespace_prefix";
    internal const string KeyAllowedFrameworkPrefix = "tier_layer.allowed_framework_prefix";

    /// <summary>已知键全集——键命名空间内出现未知键 = TCSG139（打错键名绝静默）。</summary>
    private static readonly HashSet<string> KnownKeys = new(StringComparer.Ordinal)
    {
        KeyForbiddenReference, KeyZeroInternalRefs, KeyInternalAssemblyPrefix,
        KeyAllowedReference, KeyNamespacePrefix, KeyAllowedFrameworkPrefix,
    };

    /// <summary>框架豁免缺省集（132 白名单防被 BCL 引用淹死）——netstandard/mscorlib 是
    /// 跨目标 facade 程序集，与 System/Microsoft 前缀一并缺省豁免。</summary>
    private static readonly string[] DefaultFrameworkPrefixes = ["System", "Microsoft", "netstandard", "mscorlib"];

    private static readonly DiagnosticDescriptor ForbiddenReferenceRule = new(
        id: "TCSG130",
        title: "禁用引用（tier_layer.forbidden_reference）",
        messageFormat: "禁止引用 '{0}'（规则：{1}）——元数据引用与 using 命名空间双形态独立检测。",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    private static readonly DiagnosticDescriptor ZeroInternalRefsRule = new(
        id: "TCSG131",
        title: "零内部依赖违约（tier_layer.zero_internal_refs）",
        messageFormat: "'{0}' 声明零内部依赖，却引用了内部家族程序集 '{1}'（前缀 {2}）。",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    private static readonly DiagnosticDescriptor AllowedReferenceRule = new(
        id: "TCSG132",
        title: "引用白名单越界（tier_layer.allowed_reference）",
        messageFormat: "'{0}' 引用了白名单外程序集 '{1}'（规则：{2}）。",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    private static readonly DiagnosticDescriptor NamespacePrefixRule = new(
        id: "TCSG133",
        title: "命名空间归属越界（tier_layer.namespace_prefix）",
        messageFormat: "公开类型 '{0}' 落在命名空间 '{1}'，不在声明前缀 '{2}' 内（规则：{3}）——命名空间即归属。",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    /// <summary>forbidden_reference / allowed_reference / namespace_prefix 共用的规则形态：
    /// 左值作用域 + 右值目标集 + 原文（诊断消息携带规则原文）。</summary>
    private sealed class ArrowRule(string left, ImmutableArray<string> rights, string raw)
    {
        public readonly string Left = left;
        public readonly ImmutableArray<string> Rights = rights;
        public readonly string Raw = raw;

        /// <summary>左值程序集名匹配（精确 / 前缀 * 通配）。</summary>
        public bool MatchesAssembly(string assemblyName)
            => TierConfigValues.LeftMatches(Left, assemblyName);

        /// <summary>左值命名空间匹配（相等或前缀）——仅 using 形态作用域。</summary>
        public bool MatchesNamespace(string? enclosingNamespace)
            => enclosingNamespace is not null && TierConfigValues.NamespaceMatches(Left, enclosingNamespace);
    }

    private sealed class LayerConfig
    {
        public readonly List<ArrowRule> Forbidden = [];
        public readonly List<string> ZeroInternalRefs = [];
        public string? InternalAssemblyPrefix;
        public readonly List<ArrowRule> Allowed = [];
        public readonly List<ArrowRule> NamespacePrefixes = [];
        public readonly List<string> ExtraFrameworkPrefixes = [];

        /// <summary>是否声明了任何规则（零默认：无规则 = 零注册）。</summary>
        public bool HasAny =>
            Forbidden.Count > 0 || ZeroInternalRefs.Count > 0 || Allowed.Count > 0 ||
            NamespacePrefixes.Count > 0;
    }

    /// <summary>本规则族全部诊断（配置非法 TCSG139 由 TierConfigDiagnostics 统一持有）。</summary>
    internal static readonly ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =
        ImmutableArray.Create(ForbiddenReferenceRule, ZeroInternalRefsRule, AllowedReferenceRule,
            NamespacePrefixRule);

    /// <summary>编译起点读全局配置并按规则注册检查（生成代码不分析、并发执行）。</summary>
    internal static void Register(AnalysisContext context)
    {
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext start)
    {
        // 读数通道 = .globalconfig（GlobalOptions）∪ .editorconfig（per-tree 并集）——#459
        var conflicts = new List<string>();
        var merged = TierConfigChannel.Merge(start.Options.AnalyzerConfigOptionsProvider,
            start.Compilation.SyntaxTrees, KeyNamespace, conflicts);
        var errors = new List<string>(conflicts);
        var config = ParseConfig(merged, errors);

        if (errors.Count > 0)
        {
            foreach (var error in errors)
            {
                var diagnostic = Diagnostic.Create(TierConfigDiagnostics.ConfigInvalidRule,
                    Location.None, $"tier_layer.* 配置非法：{error}");
                start.RegisterCompilationEndAction(ctx => ctx.ReportDiagnostic(diagnostic));
            }

            return;   // 配置非法不降级继续——fail-fast 语义（报了非法还按残缺规则跑 = 静默歧义）
        }

        if (!config.HasAny) return;

        var assemblyName = start.Compilation.AssemblyName ?? string.Empty;
        var forbidden = config.Forbidden.ToImmutableArray();
        var zeroInternalRefs = config.ZeroInternalRefs.ToImmutableArray();
        var internalPrefix = config.InternalAssemblyPrefix;
        var allowed = config.Allowed.ToImmutableArray();
        var namespacePrefixes = config.NamespacePrefixes.ToImmutableArray();
        var frameworkPrefixes = DefaultFrameworkPrefixes.ConcatNoAlloc(config.ExtraFrameworkPrefixes);

        // using 命名空间面（130）——程序集作用域与命名空间作用域双轨
        if (forbidden.Length > 0)
            start.RegisterSyntaxNodeAction(
                ctx => AnalyzeUsing(ctx, forbidden, assemblyName),
                SyntaxKind.UsingDirective);

        // 元数据引用面（130 程序集作用域 / 131 / 132）——每编译一次
        var metaForbidden = FilterByAssembly(forbidden, assemblyName);
        var needMetaCheck = metaForbidden.Length > 0
            || (zeroInternalRefs.Length > 0 && MatchesAny(zeroInternalRefs, assemblyName))
            || (allowed.Length > 0 && MatchesAnyRule(allowed, assemblyName));
        if (needMetaCheck)
            start.RegisterCompilationEndAction(ctx =>
                AnalyzeMetadataReferences(ctx, metaForbidden, zeroInternalRefs, internalPrefix, allowed,
                    frameworkPrefixes, assemblyName));

        // 公开类型命名空间归属（133）——每编译一次
        var activeNamespaceRules = FilterByAssembly(namespacePrefixes, assemblyName);
        if (activeNamespaceRules.Length > 0)
            start.RegisterCompilationEndAction(ctx => AnalyzePublicTypes(ctx.Compilation, activeNamespaceRules,
                ctx.ReportDiagnostic));
    }

    // === 配置解析 ===

    private static LayerConfig ParseConfig(AnalyzerConfigOptions global, List<string> errors)
    {
        var config = new LayerConfig();

        foreach (var key in global.Keys)
        {
            if (!key.StartsWith(KeyNamespace, StringComparison.Ordinal)) continue;
            if (!KnownKeys.Contains(key))
            {
                errors.Add($"未知键 '{key}'（已知键：{string.Join(", ", KeyNamespace)} 内六键）");
                continue;
            }

            TierConfigValues.TryGet(global, key, out var value);

            switch (key)
            {
                case KeyForbiddenReference:
                    ParseArrowRules(value, config.Forbidden, errors);
                    break;
                case KeyAllowedReference:
                    ParseArrowRules(value, config.Allowed, errors);
                    break;
                case KeyNamespacePrefix:
                    ParseArrowRules(value, config.NamespacePrefixes, errors);
                    break;
                case KeyZeroInternalRefs:
                    foreach (var name in TierConfigValues.SplitList(value))
                        config.ZeroInternalRefs.Add(name);
                    break;
                case KeyInternalAssemblyPrefix:
                    config.InternalAssemblyPrefix = value.Trim();
                    break;
                case KeyAllowedFrameworkPrefix:
                    foreach (var prefix in TierConfigValues.SplitList(value))
                        config.ExtraFrameworkPrefixes.Add(prefix);
                    break;
            }
        }

        // 规则依赖键缺失 = 配置不完整（131 无前缀无法定义"内部家族"）
        if (config.ZeroInternalRefs.Count > 0 && string.IsNullOrEmpty(config.InternalAssemblyPrefix))
            errors.Add($"{KeyZeroInternalRefs} 已声明但 {KeyInternalAssemblyPrefix} 缺失——内部家族前缀未定义");

        return config;
    }

    private static void ParseArrowRules(string value, List<ArrowRule> target, List<string> errors)
    {
        foreach (var rule in TierConfigValues.SplitRules(value))
        {
            if (!TierConfigValues.TryParseArrow(rule, out var left, out var rights, out var error))
            {
                errors.Add(error ?? $"规则解析失败：'{rule}'");
                continue;
            }

            target.Add(new ArrowRule(left, rights, rule));
        }
    }

    // === 130：using 命名空间面 ===

    private static void AnalyzeUsing(SyntaxNodeAnalysisContext context, ImmutableArray<ArrowRule> forbidden,
        string assemblyName)
    {
        var directive = (UsingDirectiveSyntax)context.Node;
        var target = directive.Name?.ToString();
        if (string.IsNullOrEmpty(target)) return;
        var ns = target!;

        var enclosing = EnclosingNamespace(directive);

        foreach (var rule in forbidden)
        {
            // 作用域 = 程序集匹配 ∪ 命名空间匹配（命名空间左值仅 using 形态生效）
            var inScope = rule.MatchesAssembly(assemblyName) || rule.MatchesNamespace(enclosing);
            if (!inScope) continue;

            foreach (var right in rule.Rights)
            {
                // 右值按命名空间前缀解释（程序集名形态的 using 目标即命名空间——同一判定覆盖）
                if (TierConfigValues.NamespaceMatches(right, ns))
                {
                    context.ReportDiagnostic(Diagnostic.Create(ForbiddenReferenceRule,
                        directive.GetLocation(), ns, rule.Raw));
                    return;
                }
            }
        }
    }

    // === 130/131/132：元数据引用面 ===

    private static void AnalyzeMetadataReferences(CompilationAnalysisContext context,
        ImmutableArray<ArrowRule> metaForbidden, ImmutableArray<string> zeroInternalRefs, string? internalPrefix,
        ImmutableArray<ArrowRule> allowed, ImmutableArray<string> frameworkPrefixes, string assemblyName)
    {
        var compilation = context.Compilation;

        foreach (var reference in compilation.References)
        {
            if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol referenced) continue;
            var referencedName = referenced.Identity.Name;

            foreach (var rule in metaForbidden)
            {
                foreach (var right in rule.Rights)
                {
                    // 右值双解释：程序集名或命名空间前缀（与 using 面同一判定——族前缀同样命中）
                    if (TierConfigValues.NamespaceMatches(right, referencedName))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(ForbiddenReferenceRule,
                            Location.None, referencedName, rule.Raw));
                    }
                }
            }
        }

        // 131：零内部依赖——引用了命中内部家族前缀的兄弟程序集
        if (zeroInternalRefs.Length > 0 && MatchesAny(zeroInternalRefs, assemblyName) && internalPrefix is not null)
        {
            foreach (var reference in compilation.References)
            {
                if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol referenced) continue;
                var referencedName = referenced.Identity.Name;
                if (referencedName.StartsWith(internalPrefix, StringComparison.Ordinal))
                {
                    context.ReportDiagnostic(Diagnostic.Create(ZeroInternalRefsRule,
                        Location.None, assemblyName, referencedName, internalPrefix));
                }
            }
        }

        // 132：引用白名单——越界引用（不在白名单 ∪ 框架豁免集）
        if (allowed.Length > 0 && MatchesAnyRule(allowed, assemblyName))
        {
            var active = FilterByAssembly(allowed, assemblyName);
            foreach (var reference in compilation.References)
            {
                if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol referenced) continue;
                var referencedName = referenced.Identity.Name;

                var whitelisted = false;
                foreach (var rule in active)
                {
                    foreach (var right in rule.Rights)
                    {
                        // 白名单右值同前缀语义（与禁用面一致——族子命名空间程序集同在白名单）
                        if (TierConfigValues.NamespaceMatches(right, referencedName)) whitelisted = true;
                    }
                }

                if (whitelisted) continue;

                var isFramework = false;
                foreach (var prefix in frameworkPrefixes)
                {
                    if (referencedName.StartsWith(prefix, StringComparison.Ordinal)) isFramework = true;
                }

                if (!isFramework)
                {
                    // 多条规则命中同一程序集时只报一次（规则原文取首条）
                    var raw = active[0].Raw;
                    context.ReportDiagnostic(Diagnostic.Create(AllowedReferenceRule,
                        Location.None, assemblyName, referencedName, raw));
                }
            }
        }
    }

    // === 133：公开类型命名空间归属 ===

    private static void AnalyzePublicTypes(Compilation compilation, ImmutableArray<ArrowRule> rules,
        Action<Diagnostic> report)
    {
        WalkNamespace(compilation.SourceModule.GlobalNamespace, rules, report);
    }

    private static void WalkNamespace(INamespaceSymbol ns, ImmutableArray<ArrowRule> rules, Action<Diagnostic> report)
    {
        foreach (var type in ns.GetTypeMembers())
            WalkType(type, rules, report);
        foreach (var child in ns.GetNamespaceMembers())
            WalkNamespace(child, rules, report);
    }

    private static void WalkType(INamedTypeSymbol type, ImmutableArray<ArrowRule> rules, Action<Diagnostic> report)
    {
        foreach (var nested in type.GetTypeMembers())
            WalkType(nested, rules, report);

        if (!IsEffectivelyPublic(type)) return;

        var namespaceDisplay = type.ContainingNamespace.IsGlobalNamespace
            ? string.Empty
            : type.ContainingNamespace.ToDisplayString();

        foreach (var rule in rules)
        {
            var inPrefix = false;
            foreach (var right in rule.Rights)
            {
                // 命名空间为空（全局命名空间）= 必然越界
                if (namespaceDisplay.Length > 0 && TierConfigValues.NamespaceMatches(right, namespaceDisplay))
                    inPrefix = true;
            }

            if (!inPrefix)
            {
                report(Diagnostic.Create(NamespacePrefixRule,
                    type.Locations.Length > 0 ? type.Locations[0] : Location.None,
                    type.ToDisplayString(), namespaceDisplay.Length == 0 ? "<全局命名空间>" : namespaceDisplay,
                    string.Join(" | ", rule.Rights), rule.Raw));
                return;   // 每类型报一次（首条命中规则）
            }
        }
    }

    /// <summary>可空性上的公开：类型自身 public 且全部包含类型 public（嵌套在内部类型里的
    /// public 类型不是契约面）。</summary>
    private static bool IsEffectivelyPublic(INamedTypeSymbol type)
    {
        for (var t = (ITypeSymbol)type; t is not null; t = t.ContainingType)
        {
            if (t.DeclaredAccessibility != Accessibility.Public) return false;
        }

        return true;
    }

    // === 工具 ===

    private static ImmutableArray<ArrowRule> FilterByAssembly(ImmutableArray<ArrowRule> rules, string assemblyName)
    {
        var builder = ImmutableArray.CreateBuilder<ArrowRule>();
        foreach (var rule in rules)
        {
            if (rule.MatchesAssembly(assemblyName)) builder.Add(rule);
        }

        return builder.ToImmutable();
    }

    private static bool MatchesAny(ImmutableArray<string> lefts, string assemblyName)
    {
        foreach (var left in lefts)
        {
            if (TierConfigValues.LeftMatches(left, assemblyName)) return true;
        }

        return false;
    }

    private static bool MatchesAnyRule(ImmutableArray<ArrowRule> rules, string assemblyName)
    {
        foreach (var rule in rules)
        {
            if (rule.MatchesAssembly(assemblyName)) return true;
        }

        return false;
    }

    /// <summary>using 指令所在文件的声明命名空间——using 通常位于文件顶部（file-scoped 命名空间
    /// 是其兄弟而非祖先），先沿祖先找块作用域声明，再回退取文件首个命名空间声明。</summary>
    /// <param name="directive">using 指令语法节点。</param>
    /// <returns>所在命名空间全名；无任何命名空间声明时返回 null。</returns>
    private static string? EnclosingNamespace(UsingDirectiveSyntax directive)
    {
        for (var node = directive.Parent; node is not null; node = node.Parent)
        {
            if (node is BaseNamespaceDeclarationSyntax ns)
                return ns.Name.ToString();
        }

        if (directive.FirstAncestorOrSelf<CompilationUnitSyntax>() is { } unit)
        {
            foreach (var member in unit.Members)
            {
                if (member is BaseNamespaceDeclarationSyntax ns)
                    return ns.Name.ToString();
            }
        }

        return null;
    }
}

/// <summary>ImmutableArray 拼接小工具（避免引入 Linq 依赖到热注册路径——次数极少，正确性优先）。</summary>
internal static class LayerAnalyzerExtensions
{
    public static ImmutableArray<string> ConcatNoAlloc(this string[] first, List<string> second)
    {
        if (second.Count == 0) return ImmutableArray.Create(first);
        var builder = ImmutableArray.CreateBuilder<string>(first.Length + second.Count);
        foreach (var item in first) builder.Add(item);
        foreach (var item in second) builder.Add(item);
        return builder.ToImmutable();
    }
}
