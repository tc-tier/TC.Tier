using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TC.Tier.CodeGen.Templating;

namespace TC.Tier.CodeGen;

/// <summary>
/// [RingKey] 生成器（ring-generic-key-and-index-split 设计稿 §2——开放泛型的封闭注册，TierFs P-c 同款先例）。
/// <para>★ <c>[assembly: RingKey(typeof(T))]</c> → 一行声明产出<b>全套</b>封闭形态：
///   RingOfT : BlittableRing&lt;T&gt; + HashOfT : HashIndex&lt;T&gt; + BTreeOfT/SkipListOfT（ctor 转发 + Create 工厂
///   一步生命周期）。每个具体 Key 只声明不开发；开放泛型不落消费面（三索引 ctor 已收 protected internal 同闸门）。</para>
/// <para>★ 编译期约束校验：Key 不满足 unmanaged → TCSG020 报错；IEquatable 缺失由生成类 CS0314 兜底。</para>
/// </summary>
[Generator]
public sealed class RingKeyGenerator : IIncrementalGenerator
{
    private const string RingNamespace = "TC.Tier.Runtime.Structures.Ring";
    private const string ProbingNamespace = "TC.Tier.Runtime.Structures.ProbingIndex";
    private const string SortedNamespace = "TC.Tier.Runtime.Structures.SortedIndex";

    private static readonly DiagnosticDescriptor KeyNotUnmanagedRule = new(
        id: "TCSG020",
        title: "RingKey 标注的 Key 类型不满足 unmanaged 约束",
        messageFormat: "{0} 不满足 unmanaged 约束——BlittableRing<TKey> 要求定长 blittable Key（ring-generic-key 设计稿 §1.1）",
        category: "CodeGeneration",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>单条 [RingKey] 标注（Key 类型符号 + 诊断定位）。</summary>
    /// <param name="Type">标注的 Key 类型符号（须满足 unmanaged 约束，否则报 TCSG020）。</param>
    /// <param name="Location">标注应用位置（用于诊断定位）。</param>
    private readonly record struct KeySpec(INamedTypeSymbol Type, Location Location);

    /// <summary>封闭类名叶子——基元类型用 C# 关键字拼型（long→RingOfLong，设计稿 §2 命名），其余取类型名。</summary>
    /// <param name="type">Key 类型符号（基元取关键字拼型，其余取 <c>type.Name</c>）。</param>
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
    /// 注册生成管道：扫描程序集级 <c>[RingKey]</c> 标注（一次编译单元可挂多条）→ Collect →
    /// 为每个 Key 生成封闭形态三件套（RingOfT / HashOfT / BTreeOfT / SkipListOfT），
    /// Key 不满足 unmanaged 时上报 TCSG020。
    /// </summary>
    /// <param name="context">增量生成器初始化上下文。</param>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // 程序集级标注：目标语法 = CompilationUnit；一次编译单元可能挂多条 [RingKey]（AllowMultiple）
        var keys = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                "TC.Tier.CodeGen.RingKeyAttribute",
                static (node, _) => node is CompilationUnitSyntax,
                static (ctx, _) => ctx.Attributes
                    .Where(a => a.ConstructorArguments.Length == 1
                                && a.ConstructorArguments[0].Value is INamedTypeSymbol)
                    .Select(a => new KeySpec(
                        (INamedTypeSymbol)a.ConstructorArguments[0].Value!,
                        a.ApplicationSyntaxReference?.GetSyntax().GetLocation()
                            ?? ((CompilationUnitSyntax)ctx.TargetNode).GetLocation()))
                    .ToImmutableArray())
            .SelectMany(static (specs, _) => specs)
            .Collect();

        context.RegisterSourceOutput(keys, static (spc, items) =>
        {
            if (items.IsEmpty) return;

            var emitted = new HashSet<string>(StringComparer.Ordinal);
            var ringClasses = new StringBuilder();
            var probingClasses = new StringBuilder();
            var sortedClasses = new StringBuilder();
            var ringAny = false; var probingAny = false; var sortedAny = false;

            foreach (var item in items)
            {
                var type = item.Type;
                if (!type.IsUnmanagedType)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(KeyNotUnmanagedRule, item.Location,
                        type.ToDisplayString()));
                    continue;
                }

                var keyFqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                if (!emitted.Add(keyFqn)) continue;   // 同 Key 重复标注去重

                var leaf = ClosedLeafName(type);
                ringClasses.Append(TemplateEngine.Render(TplRingClosed, TemplateEngine.Tokens(
                    ("TYPE_NAME", type.Name),
                    ("CLASS_NAME", "RingOf" + leaf),
                    ("KEY_FQN", keyFqn))));
                ringAny = true;
                probingClasses.Append(TemplateEngine.Render(TplHashClosed, TemplateEngine.Tokens(
                    ("TYPE_NAME", type.Name),
                    ("CLASS_NAME", "HashOf" + leaf),
                    ("KEY_FQN", keyFqn))));
                probingAny = true;
                sortedClasses.Append(TemplateEngine.Render(TplSortedClosed, TemplateEngine.Tokens(
                    ("CLASS_NAME", "BTreeOf" + leaf),
                    ("FAMILY", "BTree"),
                    ("KEY_FQN", keyFqn))));
                sortedClasses.Append(TemplateEngine.Render(TplSortedClosed, TemplateEngine.Tokens(
                    ("CLASS_NAME", "SkipListOf" + leaf),
                    ("FAMILY", "SkipList"),
                    ("KEY_FQN", keyFqn))));
                sortedAny = true;
            }

            if (ringAny) spc.AddSource("RingKeyClosed.g.cs", TemplateEngine.Render(TplShell, TemplateEngine.Tokens(
                ("TITLE", "[RingKey] 封闭薄类——Ring"),
                ("NAMESPACE", RingNamespace),
                ("CLASSES", ringClasses.ToString()))));
            if (probingAny) spc.AddSource("RingKeyProbingClosed.g.cs", TemplateEngine.Render(TplShell, TemplateEngine.Tokens(
                ("TITLE", "[RingKey] 封闭薄类——探测族索引（Hash）"),
                ("NAMESPACE", ProbingNamespace),
                ("CLASSES", probingClasses.ToString()))));
            if (sortedAny) spc.AddSource("RingKeySortedClosed.g.cs", TemplateEngine.Render(TplShell, TemplateEngine.Tokens(
                ("TITLE", "[RingKey] 封闭薄类——比较族索引（BTree/SkipList）"),
                ("NAMESPACE", SortedNamespace),
                ("CLASSES", sortedClasses.ToString()))));
        });
    }

    // ── 模板名常量（Templates/RingKey/*.sbn 内嵌资源）──

    private const string TplShell = "RingKey/Shell";
    private const string TplRingClosed = "RingKey/RingClosed";
    private const string TplHashClosed = "RingKey/HashClosed";
    private const string TplSortedClosed = "RingKey/SortedClosed";
}
