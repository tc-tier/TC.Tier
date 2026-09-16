using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TC.Tier.CodeGen.Templating;

namespace TC.Tier.CodeGen;

/// <summary>
/// TierFs 构造面生成器（spec-typed-frontend-and-generator-design）：
/// <para>★ P-a 重载族：<c>[MediumOptions]</c> 标注 → <c>TierFsTypedOverloads.g.cs</c>——
///   每（options 类型 × 动词）发射类型化重载（人不再手写重载族——漂移源消灭）。</para>
/// <para>★ P-c 协议注册：<c>[NetworkProtocol]</c> 标注 → ModuleInitializer 注册代码
///   （替代手写 TierFsS3ModuleInitializer——加协议 = 实现接口 + 标注，注册自动）。</para>
/// <para>★ 标注一律 string 形态（Core→Abstractions 既有方向，不可反引 Core 类型）；
///   未知本性/动词编译期报错（TCSG0xx——拼错即炸）。</para>
/// <para>★ ★ 外部协议导出配方（设计决策定稿——Tier 通用导出协议机制）：
///   第三方协议程序集（任意命名，如 Contoso.Storage.S3）三步行接入，全部在项目配置内：
///   <list type="number">
///   <item><description>csproj 声明导出意图（与 AssemblyInfo.cs 等价——编译时合成程序集特性）：
///     <c>&lt;AssemblyAttribute Include="TC.Tier.CodeGen.TierProtocolExportedAttribute" /&gt;</c></description></item>
///   <item><description>实现类标注协议身份：<c>[NetworkProtocol("myproto")] class MyBuilder : ITierProtocolBuilder</c>
///     （引用 CodeGen.Abstractions 特性定义 + TC.Tier.Core 接口）</description></item>
///   <item><description>直接编译——本生成器在<b>消费方编译</b>里扫描带
///     <c>TierProtocolExported</c> 标记的引用程序集（<c>asm.GetAttributes()</c>——零误扫非协议程序集），
///     生成 <c>TierFsExternalProtocolRegistration</c>（ModuleInitializer——消费方加载即注册）。
///     消费方 <c>TierFs.Open("network:///myproto/…")</c> 直接可用，零反射、NativeAOT 安全。</description></item>
///   </list>
///   关注点分离：类型级 <c>[NetworkProtocol]</c> 标注协议<b>身份</b>（本地 ModuleInitializer 注册）；
///   程序集级 <c>TierProtocolExported</c> 声明导出<b>意图</b>（外部桥精确扫描）。</para>
/// </summary>
[Generator]
public sealed class TierFsGenerator : IIncrementalGenerator
{
    private static readonly ImmutableHashSet<string> KnownNatures =
        new[] { "local", "memory", "virtual", "network" }.ToImmutableHashSet(StringComparer.Ordinal);
    private static readonly ImmutableHashSet<string> KnownVerbs =
        new[] { "New", "Open", "OpenOrCreate" }.ToImmutableHashSet(StringComparer.Ordinal);

    private static readonly DiagnosticDescriptor UnknownNatureRule = new(
        id: "TCSG010",
        title: "MediumOptions unknown nature",
        messageFormat: "[MediumOptions] 的 nature '{0}' 不在已知集（local/memory/virtual/network）——拼错即炸（fail-fast，§3）。",
        category: "CodeGeneration",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnknownVerbRule = new(
        id: "TCSG011",
        title: "MediumOptions unknown verb",
        messageFormat: "[MediumOptions] 的 Verbs '{0}' 含未知动词（已知：New/Open）。",
        category: "CodeGeneration",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor BadProtocolKeyRule = new(
        id: "TCSG012",
        title: "NetworkProtocol invalid key",
        messageFormat: "[NetworkProtocol] 的 Protocol '{0}' 非法（非空、小写字母数字——与 spec path 首段一致）。",
        category: "CodeGeneration",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnmappedSpecParamRule = new(
        id: "TCSG013",
        title: "SpecParam without DSL shape mapping",
        messageFormat: "TierSpec 属性 '{0}' 标注了 [SpecParam] 但生成器无 DSL 方法形映射——单一事实源纪律：补映射或撤标注。",
        category: "CodeGeneration",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnknownSpecMediaRule = new(
        id: "TCSG014",
        title: "SpecParam unknown media",
        messageFormat: "[SpecParam] 的 Media '{0}' 不在已知集（all/local/memory/virtual/network）——拼错即静默丢参，fail-fast。",
        category: "CodeGeneration",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>TierSpec 属性名 → DSL wither 方法形（§4 P-b；单一事实源——TierSpec 属性集 = DSL 方法集）。</summary>
    private static readonly System.Collections.Immutable.ImmutableDictionary<string, (string Signature, string Body)> DslShapes =
        new System.Collections.Generic.Dictionary<string, (string, string)>
        {
            ["Label"] = ("Label(string value)", "_spec = _spec with { Label = value };"),
            ["QuotaBytes"] = ("Quota(long bytes)", "_spec = _spec with { QuotaBytes = bytes };"),
            ["Access"] = ("Access(AccessMode mode)", "_spec = _spec with { Access = mode };"),
            ["Exclusive"] = ("Exclusive()", "_spec = _spec with { Exclusive = true };"),
            ["Spill"] = ("Spill(string nestedSpec)", "_spec = _spec with { Spill = TierSpec.Parse(nestedSpec) };"),
            ["CredentialRef"] = ("Cred(string reference)", "_spec = _spec with { CredentialRef = reference };"),
            ["Region"] = ("Region(string value)", "_spec = _spec with { Region = value };"),
            ["VirtualHostAddressing"] = ("Vhost()", "_spec = _spec with { VirtualHostAddressing = true };"),
            ["Tls"] = ("Tls(bool enabled)", "_spec = _spec with { Tls = enabled };"),
            ["Members"] = ("Member(string path)", "_spec = _spec with { Members = _spec.Members.Append(path).ToArray() };"),
        }.ToImmutableDictionary(System.StringComparer.Ordinal);

    /// <summary>
    /// 注册生成管道：<c>[MediumOptions]</c> 标注 → 类型化重载族（TierFsTypedOverloads.g.cs）；
    /// 带 <c>[assembly: TierProtocolExported]</c> 的引用程序集 → 外部协议注册桥
    /// （消费方程序集加载即注册，零反射）；本性/动词未知报 TCSG010/TCSG011。
    /// </summary>
    /// <param name="context">增量生成器初始化上下文。</param>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // ★ 外部协议注册桥（设计决策——源生成器解决，零反射）：
        //   [NetworkProtocol] 的本地 ModuleInitializer 只在协议程序集自身被加载时执行——纯 spec
        //   消费者（只写 TierFs.Open("network:///s3/…")，不触碰任何 S3 类型）时 JIT 不加载引用
        //   程序集 → 注册缺失（实测复现）。正解：生成器在"引用协议程序集的消费方编译"里扫描
        //   引用程序集符号（编译期符号 API——非运行时反射，NativeAOT 安全），生成注册桥
        //   （消费方程序集加载即注册）。过滤 TC.Tier 前缀程序集（协议程序集皆属本族——系统库免扫）。
        context.RegisterSourceOutput(context.CompilationProvider, static (spc, compilation) =>
        {
            var external = new List<(string Protocol, string FullName)>();
            foreach (var asm in compilation.SourceModule.ReferencedAssemblySymbols)
            {
                // ★ 程序集级导出标记扫描（设计决策——Tier 通用协议导出机制）：
                //   只深入带 [assembly: TierProtocolExported] 的引用程序集找 [NetworkProtocol] 类型——
                //   协议身份声明在类型（NetworkProtocolAttribute）、导出意图声明在程序集（TierProtocolExported），
                //   关注点分离；零误扫非协议程序集（比"引 Core"判据更精确——外部协议程序集任意命名均覆盖）
                if (!asm.GetAttributes().Any(a => a.AttributeClass?.Name == "TierProtocolExportedAttribute")) continue;
                foreach (var t in EnumerateAllTypes(asm.GlobalNamespace))
                {
                    if (t.TypeKind != TypeKind.Class || t.IsAbstract) continue;
                    var attr = t.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "NetworkProtocolAttribute");
                    if (attr is null) continue;
                    var proto = attr.ConstructorArguments.FirstOrDefault().Value as string;
                    if (proto is null || string.IsNullOrWhiteSpace(proto) || !proto.All(char.IsLetterOrDigit) || proto.Any(char.IsUpper))
                        continue;   // 协议键非法——协议程序集自身编译时已报 TCSG012，这里跳过（显式判 null：ns2.0 facade 缺 NotNotNullWhen 注解，IsNullOrWhiteSpace 不收窄）
                    external.Add((proto, t.ToDisplayString()));
                }
            }
            if (external.Count == 0) return;
            spc.AddSource("TierFsExternalProtocolRegistration.g.cs", TemplateEngine.Render(TplRegistration, TemplateEngine.Tokens(
                ("TITLE", "外部 [NetworkProtocol] 注册桥——消费方程序集加载即注册（源生成器，零反射，NativeAOT 安全）"),
                ("CLASS_NAME", "TierFsExternalProtocolRegistration"),
                ("METHOD_NAME", "Register"),
                ("REGISTRATIONS", string.Join("\n", external.Select(e => TemplateEngine.Render(TplProtocolRegister, TemplateEngine.Tokens(
                    ("PROTOCOL", e.Protocol),
                    ("FQCN", e.FullName)))))))));
        });

        var mediumOptions = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                "TC.Tier.CodeGen.MediumOptionsAttribute",
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => (ClassDecl: (ClassDeclarationSyntax)ctx.TargetNode,
                    Namespace: ctx.TargetSymbol.ContainingNamespace.IsGlobalNamespace
                        ? string.Empty
                        : ctx.TargetSymbol.ContainingNamespace.ToDisplayString(),
                    Nature: TryGetStringArg(ctx.Attributes, 0),
                    Verbs: TryGetStringProp(ctx.Attributes, "Verbs"),
                    OptionsTypeName: TryGetStringProp(ctx.Attributes, "OptionsTypeName")))
            .Where(t => t.ClassDecl is not null);

        context.RegisterSourceOutput(mediumOptions.Collect(), static (spc, items) =>
        {
            var methods = new System.Text.StringBuilder();
            var emitted = false;
            foreach (var item in items)
            {
                var nature = item.Nature;
                var className = item.ClassDecl!.Identifier.ValueText;
                if (nature is null || !KnownNatures.Contains(nature))
                {
                    spc.ReportDiagnostic(Diagnostic.Create(UnknownNatureRule,
                        item.ClassDecl.GetLocation(), nature ?? "<缺失>"));
                    continue;
                }
                var verbText = item.Verbs ?? "New,Open";
                var verbs = SplitTrim(verbText, ',', removeEmpty: true);
                var display = item.OptionsTypeName ?? className;
                foreach (var verb in verbs)
                {
                    if (!KnownVerbs.Contains(verb))
                    {
                        spc.ReportDiagnostic(Diagnostic.Create(UnknownVerbRule, item.ClassDecl.GetLocation(), item.Verbs));
                        continue;
                    }
                    var natureEnum = nature switch
                    {
                        "local" => "StorageNature.Local",
                        "memory" => "StorageNature.Memory",
                        "virtual" => "StorageNature.Virtual",
                        "network" => "StorageNature.Network",
                        _ => "StorageNature.Local",
                    };
                    methods.Append(TemplateEngine.Render(TplOverloadMethod, TemplateEngine.Tokens(
                        ("VERB", verb),
                        ("NATURE", nature),
                        ("OPTIONS_TYPE", $"{item.Namespace}.{className}"),
                        ("NATURE_ENUM", natureEnum),
                        ("DISPLAY", display))));
                    emitted = true;
                }
            }
            if (emitted)
                spc.AddSource("TierFsTypedOverloads.g.cs", TemplateEngine.Render(TplOverloads, TemplateEngine.Tokens(
                    ("METHODS", methods.ToString()))));
        });

        var specParams = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                "TC.Tier.CodeGen.SpecParamAttribute",
                static (node, _) => node is PropertyDeclarationSyntax,
                static (ctx, _) => (Prop: (PropertyDeclarationSyntax)ctx.TargetNode,
                    Name: ctx.TargetSymbol.Name,
                    Media: TryGetStringProp(ctx.Attributes, "Media") ?? "all"))
            .Where(t => t.Prop is not null);

        context.RegisterSourceOutput(specParams.Collect(), static (spc, props) =>
        {
            if (props.IsEmpty) return;
            // 形映射完备性（单一事实源纪律）：标注了 [SpecParam] 但无映射 → 编译期报错
            foreach (var p in props)
                if (!DslShapes.ContainsKey(p.Name))
                    spc.ReportDiagnostic(Diagnostic.Create(UnmappedSpecParamRule, p.Prop!.GetLocation(), p.Name));

            // 媒体集完备（防静默丢参）：已知集外的 Media 编译期报错（TCSG014）——评审修复：local/memory 专属参数此前会无声消失
            foreach (var p in props)
                if (p.Media is not ("all" or "local" or "memory" or "virtual" or "network"))
                    spc.ReportDiagnostic(Diagnostic.Create(UnknownSpecMediaRule, p.Prop!.GetLocation(), p.Media));
            List<(string, string)> OfMedia(string media) => props
                .Where(p => DslShapes.ContainsKey(p.Name) && p.Media == media)
                .Select(p => DslShapes[p.Name]).ToList();
            var all = OfMedia("all");
            var network = OfMedia("network");
            var virtualOnly = OfMedia("virtual");
            var localOnly = OfMedia("local");
            var memoryOnly = OfMedia("memory");

            var builders = new System.Text.StringBuilder();

            void EmitBuilder(string className, string remark, System.Collections.Generic.IReadOnlyList<(string Signature, string Body)> methods)
            {
                var methodText = string.Concat(methods.Select(m => TemplateEngine.Render(TplBuilderMethod, TemplateEngine.Tokens(
                    ("METHOD_NAME", m.Signature.Split('(')[0].Trim()),
                    ("CLASS_NAME", className),
                    ("SIGNATURE", m.Signature),
                    ("BODY", m.Body)))));
                builders.Append(TemplateEngine.Render(TplBuilder, TemplateEngine.Tokens(
                    ("REMARK", remark),
                    ("CLASS_NAME", className),
                    ("METHODS", methodText))));
            }

            EmitBuilder("LocalSpec", "local:// spec 构造器", [.. all, .. localOnly]);
            EmitBuilder("MemorySpec", "memory: spec 构造器", [.. all, .. memoryOnly]);
            EmitBuilder("VirtualSpec", "virtual:// spec 构造器", [.. all, .. virtualOnly]);
            EmitBuilder("NetworkSpec", "network:///s3 spec 构造器", [.. all, .. network]);
            spc.AddSource("Specs.g.cs", TemplateEngine.Render(TplSpecs, TemplateEngine.Tokens(
                ("BUILDERS", builders.ToString()))));
        });

        var protocols = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                "TC.Tier.CodeGen.NetworkProtocolAttribute",
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => (ClassDecl: (ClassDeclarationSyntax)ctx.TargetNode,
                    Namespace: ctx.TargetSymbol.ContainingNamespace.IsGlobalNamespace
                        ? string.Empty
                        : ctx.TargetSymbol.ContainingNamespace.ToDisplayString(),
                    Protocol: TryGetStringArg(ctx.Attributes, 0)))
            .Where(t => t.ClassDecl is not null);

        context.RegisterSourceOutput(protocols.Collect(), static (spc, items) =>
        {
            if (items.IsEmpty) return;
            var registrations = new List<string>();
            foreach (var item in items)
            {
                var proto = item.Protocol;
                if (string.IsNullOrWhiteSpace(proto) || !proto.All(char.IsLetterOrDigit) || proto.Any(char.IsUpper))
                {
                    spc.ReportDiagnostic(Diagnostic.Create(BadProtocolKeyRule,
                        item.ClassDecl!.GetLocation(), proto ?? "<缺失>"));
                    continue;
                }
                var fqcn = string.IsNullOrEmpty(item.Namespace)
                    ? item.ClassDecl!.Identifier.ValueText
                    : $"{item.Namespace}.{item.ClassDecl!.Identifier.ValueText}";
                registrations.Add(TemplateEngine.Render(TplProtocolRegister, TemplateEngine.Tokens(
                    ("PROTOCOL", proto),
                    ("FQCN", fqcn))));
            }
            if (registrations.Count == 0) return;
            spc.AddSource("TierFsProtocolRegistration.g.cs", TemplateEngine.Render(TplRegistration, TemplateEngine.Tokens(
                ("TITLE", "network 协议注册（[NetworkProtocol] 派生——spec-typed-frontend §4 P-c）"),
                ("CLASS_NAME", "TierFsProtocolModule"),
                ("METHOD_NAME", "RegisterProtocols"),
                ("REGISTRATIONS", string.Join("\n", registrations)))));
        });
    }

    // ── 模板名常量（Templates/TierFs/*.sbn 内嵌资源）──

    private const string TplOverloads = "TierFs/Overloads";
    private const string TplOverloadMethod = "TierFs/OverloadMethod";
    private const string TplSpecs = "TierFs/Specs";
    private const string TplBuilder = "TierFs/Builder";
    private const string TplBuilderMethod = "TierFs/BuilderMethod";
    private const string TplRegistration = "TierFs/Registration";
    private const string TplProtocolRegister = "TierFs/ProtocolRegister";

    private static string? TryGetStringArg(ImmutableArray<AttributeData> attributes, int position)
    {
        foreach (var attr in attributes)
        {
            if (attr.ConstructorArguments.Length <= position) continue;
            if (attr.ConstructorArguments[position].Value is string s) return s;
        }
        return null;
    }

    private static string? TryGetStringProp(ImmutableArray<AttributeData> attributes, string propName)
    {
        foreach (var attr in attributes)
        {
            foreach (var named in attr.NamedArguments)
                if (named.Key == propName && named.Value.Value is string s)
                    return s;
        }
        return null;
    }

    /// <summary>递归遍历命名空间树全部类型（外部协议扫描用——编译期符号 API，零反射）。</summary>
    /// <param name="ns">起始命名空间符号（通常为引用程序集的 GlobalNamespace）。</param>
    /// <returns>该命名空间及其全部子命名空间下的所有命名类型（深度优先递归）。</returns>
    internal static IEnumerable<INamedTypeSymbol> EnumerateAllTypes(INamespaceSymbol ns)
    {
        foreach (var t in ns.GetTypeMembers())
            yield return t;
        foreach (var child in ns.GetNamespaceMembers())
            foreach (var t in EnumerateAllTypes(child))
                yield return t;
    }

    /// <summary>切分并逐项 Trim（netstandard2.0 无 StringSplitOptions.TrimEntries——语义等价：先切分再 Trim，空项按需剔除）。</summary>
    private static string[] SplitTrim(string text, char separator, bool removeEmpty)
    {
        var raw = text.Split(separator);
        var result = new List<string>(raw.Length);
        foreach (var part in raw)
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 0 || !removeEmpty) result.Add(trimmed);
        }
        return result.ToArray();
    }
}
