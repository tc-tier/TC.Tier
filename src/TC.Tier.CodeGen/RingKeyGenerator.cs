using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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
    private readonly record struct KeySpec(INamedTypeSymbol Type, Location Location);

    /// <summary>封闭类名叶子——基元类型用 C# 关键字拼型（long→RingOfLong，设计稿 §2 命名），其余取类型名。</summary>
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
            var ringSb = RingSb();
            var probingSb = ProbingSb();
            var sortedSb = SortedSb();
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
                EmitRingClosed(ringSb, type, leaf, keyFqn); ringAny = true;
                EmitHashClosed(probingSb, type, leaf, keyFqn); probingAny = true;
                EmitSortedClosed(sortedSb, "BTree", leaf, keyFqn); sortedAny = true;
                EmitSortedClosed(sortedSb, "SkipList", leaf, keyFqn);
            }

            if (ringAny) spc.AddSource("RingKeyClosed.g.cs", ringSb.ToString());
            if (probingAny) spc.AddSource("RingKeyProbingClosed.g.cs", probingSb.ToString());
            if (sortedAny) spc.AddSource("RingKeySortedClosed.g.cs", sortedSb.ToString());
        });
    }

    private static StringBuilder RingSb()
    {
        var sb = Header("[RingKey] 封闭薄类——Ring");
        sb.AppendLine($"namespace {RingNamespace};");
        sb.AppendLine();
        return sb;
    }

    private static StringBuilder ProbingSb()
    {
        var sb = Header("[RingKey] 封闭薄类——探测族索引（Hash）");
        sb.AppendLine($"namespace {ProbingNamespace};");
        sb.AppendLine();
        return sb;
    }

    private static StringBuilder SortedSb()
    {
        var sb = Header("[RingKey] 封闭薄类——比较族索引（BTree/SkipList）");
        sb.AppendLine($"namespace {SortedNamespace};");
        sb.AppendLine();
        return sb;
    }

    private static StringBuilder Header(string title)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"// <auto-generated>{title}（ring-generic-key 设计稿 §2——一行 [RingKey] 声明产出全套封闭形态）</auto-generated>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        return sb;
    }

    private static void EmitRingClosed(StringBuilder sb, INamedTypeSymbol type, string leaf, string keyFqn)
    {
        var className = $"RingOf{leaf}";
        sb.AppendLine($"/// <summary><c>[RingKey(typeof({type.Name}))]</c> 封闭薄类——开放泛型内核的编译期封闭，消费面只见本类型。</summary>");
        sb.AppendLine($"public sealed class {className} : global::{RingNamespace}.BlittableRing<{keyFqn}>");
        sb.AppendLine("{");
        sb.AppendLine($"    public {className}(global::{RingNamespace}.BlittableRingSettings settings,");
        sb.AppendLine("        global::TC.Tier.Core.IO.IFileSystem fs,");
        sb.AppendLine("        global::TC.Tier.Core.Epochs.LightEpoch? epoch = null,");
        sb.AppendLine("        global::TC.Tier.Core.Logging.ILogger? logger = null)");
        sb.AppendLine("        : base(settings, fs, epoch: epoch, logger: logger)");
        sb.AppendLine("    {");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine($"    /// <summary>★ 工厂：构造 + Initialize + WaitForReady 一步到位（对齐 BlittableRing.Create 形态）。</summary>");
        sb.AppendLine($"    public static {className} Create(global::{RingNamespace}.BlittableRingSettings settings,");
        sb.AppendLine("        global::TC.Tier.Core.IO.IFileSystem fs,");
        sb.AppendLine("        global::TC.Tier.Core.Epochs.LightEpoch? epoch = null)");
        sb.AppendLine("    {");
        sb.AppendLine($"        var ring = new {className}(settings, fs, epoch);");
        sb.AppendLine("        ring.Initialize();");
        sb.AppendLine("        ring.WaitForReady();");
        sb.AppendLine("        return ring;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();
    }

    private static void EmitHashClosed(StringBuilder sb, INamedTypeSymbol type, string leaf, string keyFqn)
    {
        var className = $"HashOf{leaf}";
        sb.AppendLine($"/// <summary><c>[RingKey(typeof({type.Name}))]</c> 探测族封闭薄类——判等闭环硬依赖 IKeyResolver（构造期必注入）。</summary>");
        sb.AppendLine($"public sealed class {className} : global::{ProbingNamespace}.HashIndex<{keyFqn}>");
        sb.AppendLine("{");
        sb.AppendLine($"    public {className}(global::TC.Tier.Core.IO.IFileSystem fs,");
        sb.AppendLine($"        global::{ProbingNamespace}.HashIndexSettings settings,");
        sb.AppendLine($"        global::TC.Tier.Contracts.Structures.IKeyResolver<{keyFqn}> keyResolver,");
        sb.AppendLine("        global::TC.Tier.Core.Epochs.LightEpoch? epoch = null)");
        sb.AppendLine("        : base(fs, settings, epoch, keyResolver)");
        sb.AppendLine("    {");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine($"    /// <summary>★ 工厂：构造 + Initialize + WaitForReady 一步到位（空结构首开；恢复窗口经 Initialize(hints) 注入）。</summary>");
        sb.AppendLine($"    public static {className} Create(global::TC.Tier.Core.IO.IFileSystem fs,");
        sb.AppendLine($"        global::{ProbingNamespace}.HashIndexSettings settings,");
        sb.AppendLine($"        global::TC.Tier.Contracts.Structures.IKeyResolver<{keyFqn}> keyResolver,");
        sb.AppendLine("        global::TC.Tier.Core.Epochs.LightEpoch? epoch = null)");
        sb.AppendLine("    {");
        sb.AppendLine($"        var index = new {className}(fs, settings, keyResolver, epoch);");
        sb.AppendLine("        index.Initialize();");
        sb.AppendLine("        index.WaitForReady();");
        sb.AppendLine("        return index;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();
    }

    private static void EmitSortedClosed(StringBuilder sb, string family, string leaf, string keyFqn)
    {
        var className = $"{family}Of{leaf}";
        sb.AppendLine($"/// <summary><c>[RingKey(typeof(...))]</c> 比较族封闭薄类——keyResolver 可选（判等不需要，恢复重放需要）。</summary>");
        sb.AppendLine($"public sealed class {className} : global::{SortedNamespace}.{family}Index<{keyFqn}>");
        sb.AppendLine("{");
        sb.AppendLine($"    public {className}(global::TC.Tier.Core.IO.IFileSystem fs,");
        sb.AppendLine($"        global::{SortedNamespace}.{family}IndexSettings settings,");
        sb.AppendLine("        global::TC.Tier.Core.Epochs.LightEpoch? epoch = null,");
        sb.AppendLine($"        global::TC.Tier.Contracts.Structures.IKeyResolver<{keyFqn}>? keyResolver = null)");
        sb.AppendLine("        : base(fs, settings, epoch, keyResolver)");
        sb.AppendLine("    {");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine($"    /// <summary>★ 工厂：构造 + Initialize + WaitForReady 一步到位（空结构首开；恢复窗口经 Initialize(hints) 注入）。</summary>");
        sb.AppendLine($"    public static {className} Create(global::TC.Tier.Core.IO.IFileSystem fs,");
        sb.AppendLine($"        global::{SortedNamespace}.{family}IndexSettings settings,");
        sb.AppendLine("        global::TC.Tier.Core.Epochs.LightEpoch? epoch = null)");
        sb.AppendLine("    {");
        sb.AppendLine($"        var index = new {className}(fs, settings, epoch);");
        sb.AppendLine("        index.Initialize();");
        sb.AppendLine("        index.WaitForReady();");
        sb.AppendLine("        return index;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();
    }
}
