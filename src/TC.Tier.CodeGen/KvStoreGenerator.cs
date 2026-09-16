using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TC.Tier.CodeGen.Templating;

namespace TC.Tier.CodeGen;

/// <summary>
/// [KvStore] 生成器（tierkv-design.md §0.1/§4 W1——开放泛型产品类的封闭消费面，[RingKey] 同款先例）。
/// <para>★ <c>[assembly: KvStore(typeof(TKey), typeof(TValue))]</c> → 一行声明产出封闭形态：
///   TierKvOfKeyLeafValueLeaf : TierKv&lt;TKey,TValue&gt;（ctor 转发 + CreateAsync 工厂一步生命周期 +
///   内嵌派生 Ring/索引实现——派生肢获得 protected internal 构造权，开放泛型零构造）。
///   formatter 按 TValue 自动发射（byte/byte[]/ROM&lt;byte&gt;/unmanaged 零代码，D6/D7 裁定）；
///   IndexKind 标注缺省发射 DefaultOptions（运行 Options.IndexKind 覆盖——显式胜出）。</para>
/// <para>★ 编译期校验：Key 不满足 unmanaged → TCSG021；TValue 无可用 formatter（非内建覆盖面且未声明
///   Formatter）→ TCSG022；Formatter 未实现 IValueFormatter&lt;TValue&gt; → TCSG023
///   （消费方未引用 Products 时接口符号不可解析——跳过该校验由生成物编译错误兜底，IEquatable 缺失同款 CS0314）。</para>
/// </summary>
[Generator]
public sealed class KvStoreGenerator : IIncrementalGenerator
{
    private const string KvNamespace = "TC.Tier.Products.Kv";

    private static readonly DiagnosticDescriptor KeyNotUnmanagedRule = new(
        id: "TCSG021",
        title: "KvStore 标注的 Key 类型不满足 unmanaged 约束",
        messageFormat: "{0} 不满足 unmanaged 约束——TierKv<TKey,TValue> 要求定长 blittable Key（tierkv-design §0 泛型约束）",
        category: "CodeGeneration",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ValueNotFormattableRule = new(
        id: "TCSG022",
        title: "KvStore 标注的 Value 类型无可用的值格式化器",
        messageFormat: "{0} 不在内建 formatter 覆盖面（byte/byte[]/ReadOnlyMemory<byte>/unmanaged——D6 裁定）且未声明 Formatter——" +
            "实现 IValueFormatter<TValue> 后经 [KvStore(Formatter=typeof(...))] 显式声明",
        category: "CodeGeneration",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor FormatterNotImplementingRule = new(
        id: "TCSG023",
        title: "KvStore 标注的 Formatter 未实现 IValueFormatter<TValue>",
        messageFormat: "{0} 未实现 IValueFormatter<{1}>——Formatter 须为值格式化契约实现（编译期校验）",
        category: "CodeGeneration",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>单条 [KvStore] 标注（Key/Value/Formatter 类型符号 + IndexKind 缺省 + 诊断定位）。
    /// <para>★ Value 收 <see cref="ITypeSymbol"/>——byte[]（IArrayTypeSymbol）是 D6 内建覆盖面，
    ///   不得按 INamedTypeSymbol 过滤（否则 byte[] 标注静默丢失）。</para></summary>
    /// <param name="Key">Key 类型符号（须满足 unmanaged 约束，否则报 TCSG021）。</param>
    /// <param name="Value">Value 类型符号（收 ITypeSymbol——byte[] 等数组类型是 D6 内建覆盖面）。</param>
    /// <param name="Formatter">显式声明的 Formatter 类型符号（null = 缺省，按 TValue 自动）。</param>
    /// <param name="IndexKind">IndexKind 标注缺省（0=Hash 即 Default；非零需发射覆盖表达式）。</param>
    /// <param name="Location">标注应用位置（用于诊断定位）。</param>
    private readonly record struct KvSpec(
        INamedTypeSymbol Key, ITypeSymbol Value, INamedTypeSymbol? Formatter, int IndexKind, Location Location);

    /// <summary>封闭类名叶子——基元类型用 C# 关键字拼型（long→TierKvOfLongLong，[RingKey] ClosedLeafName 同款），其余取类型名。</summary>
    /// <param name="type">基元类型用 C# 关键字拼型，其余取 <c>type.Name</c>。</param>
    /// <returns>封闭类名叶子（首字母大写，如 "Long"/"Ulong"/"Float"；非基元取类型名）。</returns>
    private static string ClosedLeafName(INamedTypeSymbol type) => type.SpecialType switch
    {
        SpecialType.System_SByte => "Sbyte",
        SpecialType.System_Byte => "Byte",
        SpecialType.System_Int16 => "Short",
        SpecialType.System_UInt16 => "Ushort",
        SpecialType.System_Int32 => "Int",
        SpecialType.System_UInt32 => "Uint",
        SpecialType.System_Int64 => "Long",
        SpecialType.System_UInt64 => "Ulong",
        SpecialType.System_Single => "Float",
        SpecialType.System_Double => "Double",
        SpecialType.System_Decimal => "Decimal",
        SpecialType.System_Char => "Char",
        SpecialType.System_Boolean => "Bool",
        SpecialType.System_IntPtr => "Nint",
        SpecialType.System_UIntPtr => "Nuint",
        _ => type.Name,
    };

    /// <summary>
    /// 注册生成管道：扫描程序集级 <c>[KvStore]</c> 标注（一次编译单元可挂多条）→ Collect →
    /// 为每对 (TKey,TValue) 生成封闭形态 + 校验诊断上报。
    /// </summary>
    /// <param name="context">增量生成器初始化上下文。</param>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // 程序集级标注：目标语法 = CompilationUnit；AllowMultiple
        var specs = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                "TC.Tier.CodeGen.KvStoreAttribute",
                static (node, _) => node is CompilationUnitSyntax,
                static (ctx, _) => ExtractSpecs(ctx))
            .SelectMany(static (items, _) => items)
            .Collect();

        context.RegisterSourceOutput(specs, static (spc, items) => Emit(spc, items));
    }

    /// <summary>标注提取（纯语法/符号变换——不触 Compilation，保增量缓存）。</summary>
    /// <param name="ctx">Roslyn 标注语法上下文（含 TargetNode/TargetSymbol/Attributes）。</param>
    /// <returns>解析出的 KvSpec 数组（每条 [KvStore] 标注一个；构造参数不合规的标注被跳过）。</returns>
    private static ImmutableArray<KvSpec> ExtractSpecs(GeneratorAttributeSyntaxContext ctx)
    {
        var builder = ImmutableArray.CreateBuilder<KvSpec>();
        foreach (var attribute in ctx.Attributes)
        {
            if (attribute.ConstructorArguments.Length != 2
                || attribute.ConstructorArguments[0].Value is not INamedTypeSymbol key
                || attribute.ConstructorArguments[1].Value is not ITypeSymbol value)
                continue;

            var indexKind = 0;
            INamedTypeSymbol? formatter = null;
            foreach (var namedArg in attribute.NamedArguments)
            {
                var name = namedArg.Key;
                var typed = namedArg.Value;
                if (name == "IndexKind" && typed.Value is { } raw)
                    indexKind = System.Convert.ToInt32(raw);
                else if (name == "Formatter" && typed.Value is INamedTypeSymbol f)
                    formatter = f;
            }

            builder.Add(new KvSpec(key, value, formatter, indexKind,
                attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation()
                    ?? ((CompilationUnitSyntax)ctx.TargetNode).GetLocation()));
        }
        return builder.ToImmutable();
    }

    /// <summary>逐条校验 + 渲染封闭形态（[RingKey] RegisterSourceOutput 同构）。</summary>
    /// <param name="spc">源产物上下文（上报诊断 + 添加源）。</param>
    /// <param name="items">所有 [KvStore] 标注的 KvSpec 集合。</param>
    private static void Emit(SourceProductionContext spc, ImmutableArray<KvSpec> items)
    {
        if (items.IsEmpty) return;

        var emitted = new HashSet<string>(StringComparer.Ordinal);
        var classes = new StringBuilder();
        var any = false;

        foreach (var item in items)
        {
            if (!item.Key.IsUnmanagedType)
            {
                spc.ReportDiagnostic(Diagnostic.Create(KeyNotUnmanagedRule, item.Location,
                    item.Key.ToDisplayString()));
                continue;
            }

            var formatterExpr = ResolveFormatterExpression(spc, item);
            if (formatterExpr is null) continue;   // 诊断已上报（TCSG022/TCSG023）

            var keyFqn = item.Key.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var valueFqn = item.Value.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (!emitted.Add(keyFqn + "|" + valueFqn)) continue;   // 同 (Key,Value) 重复标注去重

            classes.Append(TemplateEngine.Render(TplKvClosed, TemplateEngine.Tokens(
                ("KEY_NAME", item.Key.Name),
                ("VALUE_NAME", ValueDisplayName(item.Value)),
                ("CLASS_NAME", "TierKvOf" + ClosedLeafName(item.Key) + ValueLeafName(item.Value)),
                ("KEY_FQN", keyFqn),
                ("VALUE_FQN", valueFqn),
                ("FORMATTER_EXPR", formatterExpr),
                ("DEFAULT_OPTIONS_EXPR", DefaultOptionsExpression(item.IndexKind)))));
            any = true;
        }

        if (any) spc.AddSource("KvStoreClosed.g.cs", TemplateEngine.Render(TplShell, TemplateEngine.Tokens(
            ("TITLE", "[KvStore] 封闭形态——TierKv 产品类（formatter 零代码 + 默认装配）"),
            ("NAMESPACE", KvNamespace),
            ("CLASSES", classes.ToString()))));
    }

    /// <summary>
    /// formatter 发射表达式解析：显式 Formatter 胜出（D7——接口实现校验：实现 TC.Tier.Products.Kv 的
    /// IValueFormatter`1 且类型实参恰为 TValue）；缺省按 TValue 自动（byte/byte[]/ROM&lt;byte&gt;/unmanaged
    /// 内建，D6 覆盖面）；无可用的 = null（已报诊断）。
    /// <para>★ 接口校验走符号元数据名+类型实参直判，零 Compilation 依赖——纯符号比较增量缓存友好。</para>
    /// </summary>
    /// <param name="spc">源产物上下文（用于上报 TCSG022/TCSG023 诊断）。</param>
    /// <param name="item">单条 [KvStore] 标注的语义模型。</param>
    /// <returns>formatter 表达式（如 "new XxxFormatter()" 或内建覆盖面的常量引用）；无可用的 = null（已报诊断）。</returns>
    private static string? ResolveFormatterExpression(SourceProductionContext spc, KvSpec item)
    {
        if (item.Formatter is { } formatter)
        {
            if (!formatter.AllInterfaces.Any(i =>
                    i.OriginalDefinition.MetadataName == "IValueFormatter`1"
                    && i.OriginalDefinition.ContainingNamespace is { IsGlobalNamespace: false } ifaceNs
                    && ifaceNs.ToDisplayString() == "TC.Tier.Products.Kv"
                    && i.TypeArguments.Length == 1
                    && SymbolEqualityComparer.Default.Equals(i.TypeArguments[0], item.Value)))
            {
                spc.ReportDiagnostic(Diagnostic.Create(FormatterNotImplementingRule, item.Location,
                    formatter.ToDisplayString(), item.Value.ToDisplayString()));
                return null;
            }
            return "new " + formatter.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + "()";
        }

        var value = item.Value;
        if (value.SpecialType == SpecialType.System_Byte)
            return "global::TC.Tier.Products.Kv.ValueFormatters.Byte";
        if (value is IArrayTypeSymbol { Rank: 1, ElementType.SpecialType: SpecialType.System_Byte })
            return "global::TC.Tier.Products.Kv.ValueFormatters.ByteArray";
        if (IsReadOnlyMemoryOfByte(value))
            return "global::TC.Tier.Products.Kv.ValueFormatters.ByteMemory";
        if (value.IsUnmanagedType)
            return "global::TC.Tier.Products.Kv.ValueFormatters.Blittable<"
                + value.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + ">()";

        spc.ReportDiagnostic(Diagnostic.Create(ValueNotFormattableRule, item.Location,
            value.ToDisplayString()));
        return null;
    }

    /// <summary>System.ReadOnlyMemory&lt;byte&gt; 识别（SpecialType 无此类型——按元数据名判）。</summary>
    /// <param name="type">待判类型符号。</param>
    /// <returns>true 表示该类型为 <c>System.ReadOnlyMemory&lt;byte&gt;</c>（D6 内建覆盖面）。</returns>
    private static bool IsReadOnlyMemoryOfByte(ITypeSymbol type)
        => type is INamedTypeSymbol named
           && named.TypeArguments.Length == 1
           && named.TypeArguments[0].SpecialType == SpecialType.System_Byte
           && named.OriginalDefinition.MetadataName == "ReadOnlyMemory`1"
           && named.OriginalDefinition.ContainingNamespace is { IsGlobalNamespace: false } ns
           && ns.ToDisplayString() == "System";

    /// <summary>封闭类名 Value 叶子（byte[] → ByteArray——数组类型无 Name/SpecialType 叶，单列）。</summary>
    /// <param name="value">Value 类型符号（byte[]/INamedTypeSymbol/其他）。</param>
    /// <returns>Value 叶子名（"ByteArray" 或 ClosedLeafName 结果或 "Unknown"——后者不可达）。</returns>
    private static string ValueLeafName(ITypeSymbol value) => value switch
    {
        IArrayTypeSymbol { Rank: 1, ElementType.SpecialType: SpecialType.System_Byte } => "ByteArray",
        INamedTypeSymbol named => ClosedLeafName(named),
        _ => "Unknown",   // 不可达——ResolveFormatterExpression 已按 TCSG022 拦截非内建类型
    };

    /// <summary>标注文档显示名（INamedTypeSymbol → Name；byte[] → byte[]）。</summary>
    /// <param name="value">Value 类型符号。</param>
    /// <returns>显示名（命名类型取 <c>Name</c>；数组等其他类型取 <c>ToDisplayString()</c>）。</returns>
    private static string ValueDisplayName(ITypeSymbol value) => value switch
    {
        INamedTypeSymbol named => named.Name,
        _ => value.ToDisplayString(),
    };

    /// <summary>缺省 Options 表达式（IndexKind 标注缺省；0=Hash 即 Default——零发射税）。</summary>
    /// <param name="indexKind">IndexKind 标注值（0=Hash 即 Default；非零走 WithIndexKind 覆盖）。</param>
    /// <returns>生成的 Options 表达式字符串（0 = "TierKvOptions.Default"；非零 = ".WithIndexKind(...)"）。</returns>
    private static string DefaultOptionsExpression(int indexKind)
        => indexKind == 0
            ? "global::TC.Tier.Products.Kv.TierKvOptions.Default"
            : "global::TC.Tier.Products.Kv.TierKvOptions.Default.WithIndexKind((global::TC.Tier.Products.Kv.KvIndexKind)"
              + indexKind + ")";

    // ── 模板名常量（Templates/KvStore/*.sbn 内嵌资源）──

    private const string TplShell = "KvStore/Shell";
    private const string TplKvClosed = "KvStore/KvClosed";
}
