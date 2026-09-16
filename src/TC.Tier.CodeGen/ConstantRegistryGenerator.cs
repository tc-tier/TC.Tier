using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TC.Tier.CodeGen.Templating;

namespace TC.Tier.CodeGen;

/// <summary>
/// [ConstantRegistry] 源生成器（spec-12 §10 E3）——常量注册表编译期防冲突 + 区间助手。
/// <para>★ 重复值检测（恒生效）：类内整型 const 字段数值两两相异，重复报 TCSG040
///   （FrameKind/ProtocolId/Channel/特性位常量表手写漂移的编译期锁死）。</para>
/// <para>★ 区间助手（Zones 声明）：谓词注入本类 partial（与 TierFs 生成 partial TierFs 同构）；
///   声明格式/区间值域/类声明形态/值域可判性违规分别报 TCSG041-044——拼错即炸，fail-fast。</para>
/// </summary>
[Generator]
public sealed class ConstantRegistryGenerator : IIncrementalGenerator
{
    /// <summary>★ 重复值 = 注册表防冲突的核心不变量——两常量同值即歧义路由/歧义诊断，编译期拒绝。</summary>
    private static readonly DiagnosticDescriptor DuplicateValueRule = new(
        id: "TCSG040",
        title: "ConstantRegistry duplicate value",
        messageFormat: "常量 '{0}' 与 '{1}' 数值重复（{2}）——注册表值空间内必须两两相异（spec-12 §10 E3）。",
        category: "CodeGeneration",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>★ Zones 声明格式（谓词名非法/区间串不可解析）——拼错即炸。</summary>
    private static readonly DiagnosticDescriptor BadZoneDeclarationRule = new(
        id: "TCSG041",
        title: "ConstantRegistry invalid zone declaration",
        messageFormat: "[ConstantRegistry] Zones 条目 '{0}' 非法——格式 = \"谓词名:0xLL-0xHH[,0xLL-0xHH]*\"（谓词名为合法 C# 标识符；十六进制 0x 前缀可省）。",
        category: "CodeGeneration",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>★ 区间端点倒挂/超出值域——生成谓词会静默恒 false 或截断，编译期拒绝。</summary>
    private static readonly DiagnosticDescriptor ZoneRangeInvalidRule = new(
        id: "TCSG042",
        title: "ConstantRegistry invalid zone range",
        messageFormat: "[ConstantRegistry] 条目 '{0}' 的区间 '{1}' 非法（low > high 或超出值域上限 0x{2:X}）。",
        category: "CodeGeneration",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>★ 区间助手注入点 = 本类 partial——非 partial 无法注入，编译期拒绝。</summary>
    private static readonly DiagnosticDescriptor NotPartialRule = new(
        id: "TCSG043",
        title: "ConstantRegistry class not partial",
        messageFormat: "[ConstantRegistry] 类 '{0}' 声明了 Zones 但不是 partial——区间助手生成需要 partial 注入点。",
        category: "CodeGeneration",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>★ 谓词参数类型 = 类内 const 统一整型——无常量/混合类型时值域不可判，编译期拒绝。</summary>
    private static readonly DiagnosticDescriptor ZoneDomainAmbiguousRule = new(
        id: "TCSG044",
        title: "ConstantRegistry zone value domain ambiguous",
        messageFormat: "[ConstantRegistry] 类 '{0}' 声明了 Zones 但整型 const 值域不可判（无常量或混合整型类型）——谓词参数类型取类内 const 的统一类型。",
        category: "CodeGeneration",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>谓词名合法性（标识符形态；关键字冲突由编译器对生成代码兜底）。</summary>
    private static readonly System.Text.RegularExpressions.Regex PredicateNamePattern =
        new("^[A-Za-z_][A-Za-z0-9_]*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>整型 SpecialType → 十六进制值域上限（区间为非负 hex——ID 空间无负号形态）。</summary>
    private static readonly ImmutableDictionary<SpecialType, ulong> TypeMaxValue =
        new Dictionary<SpecialType, ulong>
        {
            [SpecialType.System_SByte] = 0x7F,
            [SpecialType.System_Byte] = 0xFF,
            [SpecialType.System_Int16] = 0x7FFF,
            [SpecialType.System_UInt16] = 0xFFFF,
            [SpecialType.System_Int32] = 0x7FFF_FFFF,
            [SpecialType.System_UInt32] = 0xFFFF_FFFF,
            [SpecialType.System_Int64] = 0x7FFF_FFFF_FFFF_FFFF,
            [SpecialType.System_UInt64] = ulong.MaxValue,
        }.ToImmutableDictionary();

    /// <summary>
    /// 注册生成管道：[ConstantRegistry] 标注 → 重复值检测（恒生效）+ Zones 谓词注入（声明时）。
    /// </summary>
    /// <param name="context">增量生成器初始化上下文。</param>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var registries = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                "TC.Tier.CodeGen.ConstantRegistryAttribute",
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) =>
                {
                    var decl = (ClassDeclarationSyntax)ctx.TargetNode;
                    var symbol = (INamedTypeSymbol)ctx.TargetSymbol;
                    var consts = ImmutableArray.CreateBuilder<(string Name, decimal Value, string TypeName, SpecialType Special, Location Location)>();
                    foreach (var member in symbol.GetMembers())
                    {
                        if (member is not IFieldSymbol { IsConst: true, ConstantValue: not null } field) continue;
                        if (!TypeMaxValue.ContainsKey(field.Type.SpecialType)) continue;   // 非整型 const（string/enum 等）不参与
                        consts.Add((field.Name, Convert.ToDecimal(field.ConstantValue), field.Type.ToDisplayString(), field.Type.SpecialType,
                            field.Locations.Length > 0 ? field.Locations[0] : decl.GetLocation()));
                    }
                    return (Decl: decl,
                        Namespace: symbol.ContainingNamespace.IsGlobalNamespace
                            ? string.Empty
                            : symbol.ContainingNamespace.ToDisplayString(),
                        ClassName: symbol.Name,
                        Modifiers: decl.Modifiers.ToString(),
                        IsPartial: decl.Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PartialKeyword)),
                        IsPublic: symbol.DeclaredAccessibility == Accessibility.Public,
                        Consts: consts.ToImmutable(),
                        Zones: TryGetStringArrayProp(ctx.Attributes, "Zones"));
                })
            .Where(t => t.Decl is not null);

        context.RegisterSourceOutput(registries, static (spc, registry) =>
        {
            // ══ 重复值检测（恒生效——Zones 未声明同样校验）══
            var firstNameByValue = new Dictionary<decimal, string>();
            foreach (var (name, value, _, _, location) in registry.Consts)
            {
                if (firstNameByValue.TryGetValue(value, out var firstName))
                {
                    var display = value < 0 ? value.ToString(System.Globalization.CultureInfo.InvariantCulture) : $"0x{(ulong)value:X}";
                    spc.ReportDiagnostic(Diagnostic.Create(DuplicateValueRule, location, name, firstName, display));
                }
                else
                {
                    firstNameByValue[value] = name;
                }
            }

            // ══ 区间助手（Zones 声明时）══
            if (registry.Zones is not { Length: > 0 } zones) return;

            if (!registry.IsPartial)
            {
                spc.ReportDiagnostic(Diagnostic.Create(NotPartialRule, registry.Decl.GetLocation(), registry.ClassName));
                return;
            }

            // 值域类型 = 类内整型 const 的统一类型（无常量/混合类型 = 不可判）
            if (registry.Consts.IsEmpty || registry.Consts.Any(c => c.Special != registry.Consts[0].Special))
            {
                spc.ReportDiagnostic(Diagnostic.Create(ZoneDomainAmbiguousRule, registry.Decl.GetLocation(), registry.ClassName));
                return;
            }
            var domainType = registry.Consts[0];
            ulong domainMax = TypeMaxValue[domainType.Special];

            var parsed = new List<(string Name, string Doc, string Body)>();
            var seenNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in zones)
            {
                if (!TryParseZoneSyntax(entry, out var name, out var ranges))
                {
                    spc.ReportDiagnostic(Diagnostic.Create(BadZoneDeclarationRule, registry.Decl.GetLocation(), entry));
                    continue;
                }
                if (!seenNames.Add(name))
                {
                    spc.ReportDiagnostic(Diagnostic.Create(BadZoneDeclarationRule, registry.Decl.GetLocation(), entry));
                    continue;
                }
                var invalidIndex = ranges.FindIndex(r => r.Low > r.High || r.High > domainMax);
                if (invalidIndex >= 0)
                {
                    var invalid = ranges[invalidIndex];
                    var rangeText = invalid.Low > invalid.High ? $"0x{invalid.Low:X}-0x{invalid.High:X}" : $"0x{invalid.High:X}";
                    spc.ReportDiagnostic(Diagnostic.Create(ZoneRangeInvalidRule, registry.Decl.GetLocation(), entry, rangeText, domainMax));
                    continue;
                }

                var checks = new List<string>();
                var descriptions = new List<string>();
                foreach (var (low, high) in ranges)
                {
                    if (low == high)
                    {
                        checks.Add($"value == 0x{low:X}");
                        descriptions.Add($"0x{low:X}");
                    }
                    else
                    {
                        checks.Add($"value >= 0x{low:X} && value <= 0x{high:X}");
                        descriptions.Add($"0x{low:X}–0x{high:X}");
                    }
                }
                parsed.Add((name, $"{name}：{string.Join(" 或 ", descriptions)}（区间含端点）", string.Join(" || ", checks)));
            }
            if (parsed.Count == 0) return;

            var accessibility = registry.IsPublic ? "public" : "internal";
            var predicates = string.Concat(parsed.Select(z => TemplateEngine.Render(TplZonePredicate, TemplateEngine.Tokens(
                ("DOC", z.Doc),
                ("ACCESS", accessibility),
                ("NAME", z.Name),
                ("TYPE", domainType.TypeName),
                ("BODY", z.Body)))));
            spc.AddSource($"{(string.IsNullOrEmpty(registry.Namespace) ? string.Empty : registry.Namespace + ".")}{registry.ClassName}.Zones.g.cs",
                TemplateEngine.Render(TplZones, TemplateEngine.Tokens(
                    ("NAMESPACE", string.IsNullOrEmpty(registry.Namespace)
                        ? ""
                        : "namespace " + registry.Namespace + ";\n"),
                    ("CLASS_NAME", registry.ClassName),
                    ("MODIFIERS", registry.Modifiers),
                    ("PREDICATES", predicates))));
        });
    }

    // ── 模板名常量（Templates/ConstantRegistry/*.sbn 内嵌资源）──

    private const string TplZones = "ConstantRegistry/Zones";
    private const string TplZonePredicate = "ConstantRegistry/ZonePredicate";

    /// <summary>语法层解析 Zones 条目（谓词名 + 区间列表）；值域校验归调用方（TCSG042 口径分离）。</summary>
    /// <param name="entry">Zones 条目原始串（格式 = "谓词名:0xLL-0xHH[,0xLL-0xHH]*"）。</param>
    /// <param name="name">解析出的谓词名（合法 C# 标识符；失败为空串）。</param>
    /// <param name="ranges">解析出的区间列表（low/high；失败为空列表）。</param>
    /// <returns>true 表示语法层解析成功；false 表示格式非法（TCSG041 由调用方上报）。</returns>
    private static bool TryParseZoneSyntax(string entry, out string name, out List<(ulong Low, ulong High)> ranges)
    {
        name = string.Empty;
        ranges = new List<(ulong, ulong)>();
        var colon = entry.IndexOf(':');
        if (colon <= 0 || colon == entry.Length - 1) return false;
        name = entry.Substring(0, colon).Trim();
        if (!PredicateNamePattern.IsMatch(name)) return false;

        foreach (var rawRange in SplitTrim(entry.Substring(colon + 1), ',', removeEmpty: true))
        {
            var parts = SplitTrim(rawRange, '-', removeEmpty: false);
            if (parts.Length is < 1 or > 2) return false;
            if (!TryParseHex(parts[0], out var low)) return false;
            if (parts.Length == 1)
            {
                ranges.Add((low, low));
                continue;
            }
            if (!TryParseHex(parts[1], out var high)) return false;
            ranges.Add((low, high));
        }
        return ranges.Count > 0;
    }

    /// <summary>十六进制解析（0x/0X 前缀可省，1-16 位数字）。</summary>
    /// <param name="text">十六进制字面（"0x" 前缀可省）。</param>
    /// <param name="value">解析结果（失败为 0）。</param>
    /// <returns>true 表示成功解析为 ulong；false 表示非合法十六进制（位数超 16 或含非 hex 字符）。</returns>
    private static bool TryParseHex(string text, out ulong value)
    {
        value = 0;
        var s = text.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
        if (s.Length is < 1 or > 16 || !s.All(Uri.IsHexDigit)) return false;
        value = Convert.ToUInt64(s, 16);
        return true;
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

    /// <summary>提取标注的 string[] 命名实参（Zones）。</summary>
    /// <param name="attributes">标注数据数组（在多个标注里找命名实参）。</param>
    /// <param name="propName">命名实参名（如 "Zones"）。</param>
    /// <returns>string[] 实参（存在且全为 string 元素时返回；否则 null）。</returns>
    private static string[]? TryGetStringArrayProp(ImmutableArray<AttributeData> attributes, string propName)
    {
        foreach (var attr in attributes)
        {
            foreach (var named in attr.NamedArguments)
            {
                if (named.Key != propName || named.Value.Kind != TypedConstantKind.Array) continue;
                var values = named.Value.Values;
                if (values.IsDefaultOrEmpty) return null;
                var result = new string[values.Length];
                for (int i = 0; i < values.Length; i++)
                {
                    if (values[i].Value is not string s) return null;
                    result[i] = s;
                }
                return result;
            }
        }
        return null;
    }
}
