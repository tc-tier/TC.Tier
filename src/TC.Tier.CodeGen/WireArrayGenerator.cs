using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TC.Tier.CodeGen.Templating;

namespace TC.Tier.CodeGen;

/// <summary>
/// [WireArray] 源生成器（spec-12 §10 E1）——变长集合线编码：<c>[定长前缀字段…][Count 4B][item×N]</c>。
/// <para>★ 布局 = 声明序：非数组字段按声明顺序构成定长前缀（cursor 累积，零偏移知识——
///   字段顺序即线顺序），数组字段承载变长区（byte[] = 字节块；T[] = 项数组，T 为基元整型或
///   标注 [BinaryLayout] 的 struct——项大小经其生成的 <c>StructSize</c> 表达式获得，
///   本生成器不重算别家布局）。</para>
/// <para>★ 生成 <c>XxxCodec.Encode(Span&lt;byte&gt;, in T) → int</c> / <c>TryDecode(ReadOnlySpan&lt;byte&gt;, out T) → bool</c>——
///   计数、位置、字节序全部生成物，消费层零手写。</para>
/// <para>★ 防御上限（MaxCount）显式声明：Count 超限 = TryDecode false——畸形报文拦截。</para>
/// <para>★ 诊断 TCSG045-048（数组字段数/元素类型/上限/构造函数形态）——拼错即炸。</para>
/// </summary>
[Generator]
public sealed class WireArrayGenerator : IIncrementalGenerator
{
    /// <summary>★ 数组字段必须恰一个（承载变长区，永远最后）。</summary>
    private static readonly DiagnosticDescriptor ArrayFieldCountRule = new(
        id: "TCSG045",
        title: "WireArray array field count",
        messageFormat: "[WireArray] struct '{0}' 必须恰含一个数组字段（byte[] 字节块或 T[] 项数组）——当前 {1} 个。",
        category: "CodeGeneration",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>★ 数组元素类型必须在支持集（基元整型 + 标注 [BinaryLayout] struct）。</summary>
    private static readonly DiagnosticDescriptor UnsupportedItemTypeRule = new(
        id: "TCSG046",
        title: "WireArray unsupported item type",
        messageFormat: "[WireArray] struct '{0}' 的数组字段 '{1}' 元素类型 '{2}' 不在支持集（基元整型 byte/ushort/short/uint/int/ulong/long 或标注 [BinaryLayout] 的 struct；string 走薄层转 byte[])。",
        category: "CodeGeneration",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>★ 防御上限必须显式且为正（有界集合契约）。</summary>
    private static readonly DiagnosticDescriptor BadMaxCountRule = new(
        id: "TCSG047",
        title: "WireArray invalid MaxCount",
        messageFormat: "[WireArray] struct '{0}' 的 MaxCount={1} 非法——必须为正整数（防御上限显式声明，spec-12 §10 E1）。",
        category: "CodeGeneration",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>★ TryDecode 走构造调用——构造函数参数序必须等于（前缀字段声明序 + 数组字段）。</summary>
    private static readonly DiagnosticDescriptor CtorMismatchRule = new(
        id: "TCSG048",
        title: "WireArray constructor mismatch",
        messageFormat: "[WireArray] struct '{0}' 的构造函数参数序列必须为（前缀字段按声明序 + 数组字段）——当前形态不匹配，生成 TryDecode 无法构造。",
        category: "CodeGeneration",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>前缀/元素基元支持集（小端原语）。</summary>
    private static readonly ImmutableHashSet<SpecialType> SupportedPrimitives =
        new[] { SpecialType.System_Byte, SpecialType.System_UInt16, SpecialType.System_Int16,
                SpecialType.System_UInt32, SpecialType.System_Int32, SpecialType.System_UInt64, SpecialType.System_Int64 }
            .ToImmutableHashSet();

    /// <summary>收集结果（全值形态——管道可缓存）。</summary>
    /// <param name="Decl">标注 struct 声明（诊断定位）。</param>
    /// <param name="Namespace">命名空间。</param>
    /// <param name="Name">struct 名。</param>
    /// <param name="MaxCount">防御上限。</param>
    /// <param name="Prefixes">前缀字段（声明序）。</param>
    /// <param name="ArrayField">数组字段名。</param>
    /// <param name="ArrayTypeName">数组字段类型显示名（ctor 校验）。</param>
    /// <param name="ElementTypeName">元素类型显示名。</param>
    /// <param name="ElementSpecial">元素 SpecialType（基元时）。</param>
    /// <param name="IsByteBlock">byte[] 字节块形态。</param>
    /// <param name="IsElementNested">元素为标注 [BinaryLayout] struct。</param>
    /// <param name="Diagnostic">诊断（null = 合法）。</param>
    private sealed record Model(
        StructDeclarationSyntax Decl,
        string Namespace,
        string Name,
        int MaxCount,
        ImmutableArray<(string Name, string TypeName, SpecialType Special, bool IsNested)> Prefixes,
        string ArrayField,
        string ArrayTypeName,
        string ElementTypeName,
        SpecialType ElementSpecial,
        bool IsByteBlock,
        bool IsElementNested,
        (DiagnosticDescriptor Rule, string[] Args)? Diagnostic);

    /// <summary>
    /// 注册生成管道：[WireArray] 标注 → XxxCodec（Encode/TryDecode）。
    /// </summary>
    /// <param name="context">增量生成器初始化上下文。</param>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var arrays = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                "TC.Tier.CodeGen.WireArrayAttribute",
                static (node, _) => node is StructDeclarationSyntax,
                static (ctx, _) => Transform(ctx))
            .Where(m => m.Decl is not null);

        context.RegisterSourceOutput(arrays, static (spc, model) =>
        {
            if (model.Diagnostic is { } d)
            {
                spc.ReportDiagnostic(Diagnostic.Create(d.Rule, model.Decl.GetLocation(), d.Args));
                return;
            }
            spc.AddSource($"{(string.IsNullOrEmpty(model.Namespace) ? string.Empty : model.Namespace + ".")}{model.Name}Codec.g.cs",
                EmitCodec(model));
        });
    }

    // ══ 收集 ══

    private static Model Transform(GeneratorAttributeSyntaxContext ctx)
    {
        var decl = (StructDeclarationSyntax)ctx.TargetNode;
        var symbol = (INamedTypeSymbol)ctx.TargetSymbol;
        var maxCount = 0;
        foreach (var attr in ctx.Attributes)
            foreach (var named in attr.NamedArguments)
                if (named.Key == "MaxCount" && named.Value.Value is int mc)
                    maxCount = mc;

        var prefixes = ImmutableArray.CreateBuilder<(string, string, SpecialType, bool)>();
        var arrayFields = new List<(string Name, string TypeName, ITypeSymbol Element)>();
        (DiagnosticDescriptor Rule, string[] Args)? diagnostic = null;

        foreach (var member in symbol.GetMembers())   // 声明序——字段顺序即线顺序
        {
            if (member is not IFieldSymbol field || field.IsStatic) continue;

            if (field.Type is IArrayTypeSymbol array)
            {
                arrayFields.Add((field.Name, field.Type.ToDisplayString(), array.ElementType));
                continue;
            }

            var isNested = HasBinaryLayout(field.Type);
            if (SupportedPrimitives.Contains(field.Type.SpecialType) || isNested)
                prefixes.Add((field.Name, field.Type.ToDisplayString(), field.Type.SpecialType, isNested));
            else
                diagnostic ??= (UnsupportedItemTypeRule, [symbol.Name, field.Name, field.Type.ToDisplayString()]);
        }

        if (arrayFields.Count != 1)
        {
            diagnostic ??= (ArrayFieldCountRule, [symbol.Name, arrayFields.Count.ToString()]);
        }
        else
        {
            var elementType = arrayFields[0].Element;
            if (elementType.SpecialType != SpecialType.System_Byte
                && !SupportedPrimitives.Contains(elementType.SpecialType)
                && !HasBinaryLayout(elementType))
            {
                diagnostic ??= (UnsupportedItemTypeRule, [symbol.Name, arrayFields[0].Name, elementType.ToDisplayString()]);
            }
        }
        if (maxCount <= 0)
            diagnostic ??= (BadMaxCountRule, [symbol.Name, maxCount.ToString()]);

        if (diagnostic is null)
        {
            var expected = prefixes.Select(p => p.Item2).Append(arrayFields[0].TypeName).ToList();
            var maxCtor = symbol.InstanceConstructors
                .Where(c => c.Parameters.Length > 0)
                .OrderByDescending(c => c.Parameters.Length)
                .FirstOrDefault();
            if (maxCtor is null
                || maxCtor.Parameters.Length != expected.Count
                || !maxCtor.Parameters.Select(p => p.Type.ToDisplayString()).SequenceEqual(expected))
            {
                diagnostic = (CtorMismatchRule, [symbol.Name]);
            }
        }

        var arrayField = arrayFields.Count == 1 ? arrayFields[0] : default;
        var elem = arrayField.Element;
        return new Model(decl,
            symbol.ContainingNamespace.IsGlobalNamespace ? string.Empty : symbol.ContainingNamespace.ToDisplayString(),
            symbol.Name, maxCount, prefixes.ToImmutable(),
            arrayField.Name, arrayField.TypeName,
            elem?.ToDisplayString() ?? string.Empty, elem?.SpecialType ?? SpecialType.None,
            IsByteBlock: elem?.SpecialType == SpecialType.System_Byte,
            IsElementNested: elem is not null && HasBinaryLayout(elem),
            diagnostic);
    }

    private static bool HasBinaryLayout(ITypeSymbol t)
        => t is INamedTypeSymbol named && t.TypeKind == TypeKind.Struct
           && named.GetAttributes().Any(a => a.AttributeClass?.Name == "BinaryLayoutAttribute");

    // ══ 生成 ══

    /// <summary>项的线大小表达式（字节块 = 1；基元 = 字面常量；嵌套 = 其生成 StructSize——本生成器不重算别家布局）。</summary>
    /// <param name="m">收集阶段 Model（含元素类型形态判定）。</param>
    /// <returns>项字节数表达式字符串（"1"/"2"/"4"/"8" 或 "<c>{ElementTypeName}Codec.StructSize</c>"）。</returns>
    private static string ItemSizeExpr(Model m) => m.IsByteBlock ? "1"
        : m.IsElementNested ? $"{m.ElementTypeName}Codec.StructSize"
        : m.ElementSpecial switch
        {
            SpecialType.System_Byte => "1",
            SpecialType.System_UInt16 or SpecialType.System_Int16 => "2",
            SpecialType.System_UInt32 or SpecialType.System_Int32 => "4",
            _ => "8",
        };

    /// <summary>前缀字段的线大小表达式（基元常量 / 嵌套 StructSize）。</summary>
    /// <param name="f">前缀字段元组（Name/TypeName/Special/IsNested）。</param>
    /// <returns>字段字节数表达式（基元常量 "1"/"2"/"4"/"8"；嵌套为 "<c>{TypeName}Codec.StructSize</c>"）。</returns>
    private static string FieldSizeExpr((string Name, string TypeName, SpecialType Special, bool IsNested) f)
        => f.IsNested ? $"{f.TypeName}Codec.StructSize"
        : f.Special switch
        {
            SpecialType.System_Byte => "1",
            SpecialType.System_UInt16 or SpecialType.System_Int16 => "2",
            SpecialType.System_UInt32 or SpecialType.System_Int32 => "4",
            _ => "8",
        };

    // ══ 生成（模板驱动——骨架走 Templates/WireArray/*.sbn，逐形态语句块由映射表供参）══

    // ── 模板名常量（Templates/WireArray/*.sbn 内嵌资源）──
    private const string TplCodecClass = "WireArray/CodecClass";
    private const string TplMaxCountConst = "WireArray/MaxCountConst";
    private const string TplEncodeArrayMethod = "WireArray/EncodeArrayMethod";
    private const string TplEncodeSpanMethod = "WireArray/EncodeSpanMethod";
    private const string TplEncodePooledMethod = "WireArray/EncodePooledMethod";
    private const string TplEncodeWriterMethod = "WireArray/EncodeWriterMethod";
    private const string TplComputeLengthMethod = "WireArray/ComputeLengthMethod";
    private const string TplTryDecodeMethod = "WireArray/TryDecodeMethod";

    private static string EmitCodec(Model m)
    {
        // ── Encode(Span) 体：前缀写 + Count + 形态块 ──
        var encodeBody = new List<string> { "        int cursor = 0;" };
        foreach (var f in m.Prefixes)
        {
            encodeBody.Add("        " + WriteStmt(f, $"value.{f.Name}"));
            encodeBody.Add($"        cursor += {FieldSizeExpr(f)};");
        }
        encodeBody.Add($"        int count = value.{m.ArrayField}.Length;");
        encodeBody.Add($"        int total = cursor + 4 + count * {ItemSizeExpr(m)};");
        encodeBody.Add("        if (dest.Length < total)");
        encodeBody.Add("            throw new System.ArgumentException($\"Buffer too small: {dest.Length} < {total}\", nameof(dest));");
        encodeBody.Add("        BinaryPrimitives.WriteInt32LittleEndian(dest[cursor..], count);");
        encodeBody.Add("        cursor += 4;");
        if (m.IsByteBlock)
            encodeBody.Add($"        value.{m.ArrayField}.CopyTo(dest[cursor..]);");
        else
        {
            encodeBody.Add($"        foreach (var item in value.{m.ArrayField})");
            encodeBody.Add("        {");
            if (m.IsElementNested)
            {
                encodeBody.Add($"            {m.ElementTypeName}Codec.Write(dest[cursor..], item);");
                encodeBody.Add($"            cursor += {ItemSizeExpr(m)};");
            }
            else
            {
                encodeBody.Add("            " + WriteStmt((m.ArrayField, m.ElementTypeName, m.ElementSpecial, false), "item", "dest[cursor..]"));
                encodeBody.Add($"            cursor += {ItemSizeExpr(m)};");
            }
            encodeBody.Add("        }");
        }

        // ── TryDecode 体：前缀读 + Count 防御 + 形态块 ──
        var decodeBody = new List<string> { "        int cursor = 0;" };
        foreach (var f in m.Prefixes)
        {
            decodeBody.Add($"        if (source.Length < cursor + {FieldSizeExpr(f)}) return false;");
            decodeBody.Add("        var " + LocalName(f.Name) + " = " + ReadExpr(f, "source[cursor..]") + ";");
            decodeBody.Add($"        cursor += {FieldSizeExpr(f)};");
        }
        decodeBody.Add("        if (source.Length < cursor + 4) return false;");
        decodeBody.Add("        int count = BinaryPrimitives.ReadInt32LittleEndian(source[cursor..]);");
        decodeBody.Add($"        if ((uint)count > {m.MaxCount}) return false;   // 负数经无符号比较一并拦截");
        decodeBody.Add($"        if (source.Length < cursor + 4 + count * {ItemSizeExpr(m)}) return false;");
        decodeBody.Add("        cursor += 4;");
        if (m.IsByteBlock)
            decodeBody.Add("        var items = source.Slice(cursor, count).ToArray();");
        else
        {
            decodeBody.Add($"        var items = new {m.ElementTypeName}[count];");
            decodeBody.Add("        for (int i = 0; i < count; i++)");
            decodeBody.Add("        {");
            if (m.IsElementNested)
            {
                decodeBody.Add($"            items[i] = {m.ElementTypeName}Codec.Read(source.Slice(cursor, {ItemSizeExpr(m)}));");
                decodeBody.Add($"            cursor += {ItemSizeExpr(m)};");
            }
            else
            {
                decodeBody.Add("            items[i] = " + ReadExpr((m.ArrayField, m.ElementTypeName, m.ElementSpecial, false), $"source.Slice(cursor, {ItemSizeExpr(m)})") + ";");
                decodeBody.Add($"            cursor += {ItemSizeExpr(m)};");
            }
            decodeBody.Add("        }");
        }

        var prefixTotalExpr = string.Join(" + ", m.Prefixes.Select(FieldSizeExpr).DefaultIfEmpty("0"));
        var ctorArgs = string.Join(", ", m.Prefixes.Select(f => LocalName(f.Name)).Append("items"));

        return TemplateEngine.Render(TplCodecClass, TemplateEngine.Tokens(
            ("NAMESPACE_BLOCK", string.IsNullOrEmpty(m.Namespace) ? "" : $"namespace {m.Namespace};\n\n"),
            ("TYPE_NAME", m.Name),
            ("MAX_COUNT", m.MaxCount.ToString()),
            ("MAX_COUNT_CONST", TemplateEngine.Render(TplMaxCountConst, TemplateEngine.Tokens(("MAX_COUNT", m.MaxCount.ToString())))),
            ("ENCODE_ARRAY_METHOD", TemplateEngine.Render(TplEncodeArrayMethod, TemplateEngine.Tokens(
                ("TYPE_NAME", m.Name),
                ("TOTAL_EXPR", prefixTotalExpr),
                ("ARRAY_FIELD", m.ArrayField),
                ("ITEM_SIZE", ItemSizeExpr(m))))),
            ("ENCODE_SPAN_METHOD", TemplateEngine.Render(TplEncodeSpanMethod, TemplateEngine.Tokens(
                ("TYPE_NAME", m.Name),
                ("BODY", string.Join("\n", encodeBody))))),
            ("ENCODE_POOLED_METHOD", TemplateEngine.Render(TplEncodePooledMethod, TemplateEngine.Tokens(
                ("TYPE_NAME", m.Name),
                ("TOTAL_EXPR", prefixTotalExpr),
                ("ARRAY_FIELD", m.ArrayField),
                ("ITEM_SIZE", ItemSizeExpr(m))))),
            ("ENCODE_WRITER_METHOD", TemplateEngine.Render(TplEncodeWriterMethod, TemplateEngine.Tokens(
                ("TYPE_NAME", m.Name),
                ("TOTAL_EXPR", prefixTotalExpr),
                ("ARRAY_FIELD", m.ArrayField),
                ("ITEM_SIZE", ItemSizeExpr(m))))),
            ("COMPUTE_LENGTH_METHOD", TemplateEngine.Render(TplComputeLengthMethod, TemplateEngine.Tokens(
                ("TYPE_NAME", m.Name),
                ("TOTAL_EXPR", prefixTotalExpr),
                ("ARRAY_FIELD", m.ArrayField),
                ("ITEM_SIZE", ItemSizeExpr(m))))),
            ("TRY_DECODE_METHOD", TemplateEngine.Render(TplTryDecodeMethod, TemplateEngine.Tokens(
                ("TYPE_NAME", m.Name),
                ("MAX_COUNT", m.MaxCount.ToString()),
                ("BODY", string.Join("\n", decodeBody)),
                ("CTOR_ARGS", ctorArgs))))));
    }

    private static string LocalName(string name) => name.Length > 1
        ? char.ToLowerInvariant(name[0]) + name.Substring(1)
        : name.ToLowerInvariant();

    /// <summary>写语句（基元小端 / 嵌套委托其 Codec）。</summary>
    /// <param name="f">字段元组（Name/TypeName/Special/IsNested）。</param>
    /// <param name="valueExpr">写入值表达式（如 "value.Field" 或 "item"）。</param>
    /// <param name="destExpr">目标 span 表达式（缺省 = "dest[cursor..]"）。</param>
    /// <returns>生成的写语句字符串（BinaryPrimitives 调用或嵌套 Codec.Write）。</returns>
    private static string WriteStmt((string Name, string TypeName, SpecialType Special, bool IsNested) f, string valueExpr, string? destExpr = null)
    {
        var target = destExpr ?? "dest[cursor..]";
        if (f.IsNested)
            return $"{f.TypeName}Codec.Write({target}, {valueExpr});";
        return f.Special switch
        {
            SpecialType.System_Byte => $"{{ var d = {target}; d[0] = {valueExpr}; }}",
            SpecialType.System_UInt16 => $"BinaryPrimitives.WriteUInt16LittleEndian({target}, {valueExpr});",
            SpecialType.System_Int16 => $"BinaryPrimitives.WriteInt16LittleEndian({target}, {valueExpr});",
            SpecialType.System_UInt32 => $"BinaryPrimitives.WriteUInt32LittleEndian({target}, {valueExpr});",
            SpecialType.System_Int32 => $"BinaryPrimitives.WriteInt32LittleEndian({target}, {valueExpr});",
            SpecialType.System_UInt64 => $"BinaryPrimitives.WriteUInt64LittleEndian({target}, {valueExpr});",
            SpecialType.System_Int64 => $"BinaryPrimitives.WriteInt64LittleEndian({target}, {valueExpr});",
            _ => $"throw new System.NotSupportedException(\"{f.TypeName}\");",
        };
    }

    /// <summary>读表达式（基元小端 / 嵌套委托其 Codec）。</summary>
    /// <param name="f">字段元组（Name/TypeName/Special/IsNested）。</param>
    /// <param name="sourceExpr">源 span 表达式（如 "source[cursor..]"）。</param>
    /// <returns>生成的读表达式字符串（BinaryPrimitives 调用或嵌套 Codec.Read）。</returns>
    private static string ReadExpr((string Name, string TypeName, SpecialType Special, bool IsNested) f, string sourceExpr)
    {
        if (f.IsNested)
            return $"{f.TypeName}Codec.Read({sourceExpr}.Slice(0, {f.TypeName}Codec.StructSize))";
        return f.Special switch
        {
            SpecialType.System_Byte => $"{sourceExpr}[0]",
            SpecialType.System_UInt16 => $"BinaryPrimitives.ReadUInt16LittleEndian({sourceExpr}.Slice(0, 2))",
            SpecialType.System_Int16 => $"BinaryPrimitives.ReadInt16LittleEndian({sourceExpr}.Slice(0, 2))",
            SpecialType.System_UInt32 => $"BinaryPrimitives.ReadUInt32LittleEndian({sourceExpr}.Slice(0, 4))",
            SpecialType.System_Int32 => $"BinaryPrimitives.ReadInt32LittleEndian({sourceExpr}.Slice(0, 4))",
            SpecialType.System_UInt64 => $"BinaryPrimitives.ReadUInt64LittleEndian({sourceExpr}.Slice(0, 8))",
            SpecialType.System_Int64 => $"BinaryPrimitives.ReadInt64LittleEndian({sourceExpr}.Slice(0, 8))",
            _ => "default",
        };
    }
}
