using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TC.Tier.CodeGen.Templating;

namespace TC.Tier.CodeGen;

/// <summary>
/// [WireMessage] 源生成器（spec-12 §10 E2）——消息族线编码：
/// <c>[Tag 1B][根字段声明序][子类字段声明序]</c>。
/// <para>★ 族根（[WireMessage] 抽象类）声明公共前缀字段；直接派生子类各标 [WireMessageTag]。
///   生成 <c>XxxCodec</c>：Encode（tag 判别 + 全字段编码）/ TryDecode（未知 tag/截断/超上限 = false）/
///   tag 常量（<c>Tag子类名</c> 同源）。</para>
/// <para>★ 成员类型支持集：bool/基元整型（小端）、标注 [BinaryLayout] struct（嵌套件——
///   项大小经其生成 StructSize）、byte[]/ReadOnlyMemory&lt;byte&gt; blob（[Len 4B][bytes]）、
///   数组/列表 of 上述与 ReadOnlyMemory&lt;byte&gt;（[Count 4B][item×N]，blob 项 [Len 4B][bytes]）。
///   变长成员必须 [WireMember(MaxCount)] 显式声明防御上限。</para>
/// <para>★ 诊断 TCSG050-053（tag 重复/成员类型不支持/变长成员缺上限/孤立 tag）——拼错即炸。</para>
/// </summary>
[Generator]
public sealed class WireMessageGenerator : IIncrementalGenerator
{
    /// <summary>★ 族内 tag 必须两两相异（歧义判别）。</summary>
    private static readonly DiagnosticDescriptor DuplicateTagRule = new(
        id: "TCSG050",
        title: "WireMessage duplicate tag",
        messageFormat: "[WireMessage] 族 '{0}' 的子类 '{1}' tag 0x{2:X2} 与 '{3}' 重复——族内 tag 两两相异。",
        category: "CodeGeneration",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>★ 成员类型必须在支持集。</summary>
    private static readonly DiagnosticDescriptor UnsupportedMemberRule = new(
        id: "TCSG051",
        title: "WireMessage unsupported member",
        messageFormat: "[WireMessage] 类型 '{0}' 的成员 '{1}' 类型 '{2}' 不在支持集（bool/基元整型/标注 [BinaryLayout] struct/blob byte[]·ReadOnlyMemory<byte>/其数组或 IReadOnlyList）。",
        category: "CodeGeneration",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>★ 变长成员必须显式声明防御上限。</summary>
    private static readonly DiagnosticDescriptor MissingMaxCountRule = new(
        id: "TCSG052",
        title: "WireMessage variable member without MaxCount",
        messageFormat: "[WireMessage] 类型 '{0}' 的变长成员 '{1}' 必须标注 [WireMember(MaxCount = n)] 显式声明防御上限（spec-12 §10 截断防御）。",
        category: "CodeGeneration",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>★ [WireMessageTag] 必须标在 [WireMessage] 族的直接子类上。</summary>
    private static readonly DiagnosticDescriptor OrphanTagRule = new(
        id: "TCSG053",
        title: "WireMessageTag outside a message family",
        messageFormat: "[WireMessageTag] 类型 '{0}' 不是任何 [WireMessage] 族的直接子类——孤立 tag。",
        category: "CodeGeneration",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly ImmutableHashSet<SpecialType> SupportedPrimitives =
        new[] { SpecialType.System_Byte, SpecialType.System_UInt16, SpecialType.System_Int16,
                SpecialType.System_UInt32, SpecialType.System_Int32, SpecialType.System_UInt64, SpecialType.System_Int64 }
            .ToImmutableHashSet();

    /// <summary>成员分类。</summary>
    private enum MemberKind { FixedBool, FixedPrimitive, FixedNested, Blob, ListOfBool, ListOfPrimitive, ListOfNested, ListOfBlob }

    /// <summary>成员语义模型（全值形态）。</summary>
    /// <param name="Name">属性名。</param>
    /// <param name="Kind">成员分类。</param>
    /// <param name="TypeDisplay">属性类型显示名。</param>
    /// <param name="ItemTypeName">集合元素类型显示名（定长成员 = 自身类型）。</param>
    /// <param name="Special">基元 SpecialType。</param>
    /// <param name="CountExpr">计数表达式（数组 Length / 列表 Count）。</param>
    /// <param name="MaxCount">防御上限（变长成员）。</param>
    private sealed record Member(string Name, MemberKind Kind, string TypeDisplay, string ItemTypeName,
        SpecialType Special, string CountExpr, int MaxCount);

    private sealed record RootModel(TypeDeclarationSyntax Decl, string Namespace, string RootName, ImmutableArray<Member> Members);

    private sealed record TagModel(TypeDeclarationSyntax Decl, string Name, string BaseType, byte Tag,
        ImmutableArray<Member> Members, string? DiagnosticId, string DiagnosticMember, string DiagnosticType);

    /// <summary>
    /// 注册生成管道：[WireMessage] 根 + [WireMessageTag] 子类 → 合并生成族 Codec。
    /// </summary>
    /// <param name="context">增量生成器初始化上下文。</param>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var roots = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                "TC.Tier.CodeGen.WireMessageAttribute",
                static (node, _) => node is TypeDeclarationSyntax,
                static (ctx, ct) => new RootModel(
                    (TypeDeclarationSyntax)ctx.TargetNode,
                    NamespaceOf(ctx.TargetSymbol),
                    ctx.TargetSymbol.Name,
                    CollectMembers((INamedTypeSymbol)ctx.TargetSymbol, out _, out _, out _)))
            .Where(t => t.Decl is not null)
            .Collect();

        var tags = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                "TC.Tier.CodeGen.WireMessageTagAttribute",
                static (node, _) => node is TypeDeclarationSyntax,
                static (ctx, _) =>
                {
                    var symbol = (INamedTypeSymbol)ctx.TargetSymbol;
                    byte tag = 0xFF;
                    foreach (var attr in ctx.Attributes)
                        if (attr.AttributeClass?.Name == "WireMessageTagAttribute"
                            && attr.ConstructorArguments.Length == 1
                            && attr.ConstructorArguments[0].Value is byte b)
                            tag = b;
                    var members = CollectMembers(symbol, out var badMember, out var badMemberType, out var missingMax);
                    string? diagnosticId = badMember is not null ? "TCSG051" : missingMax is not null ? "TCSG052" : null;
                    return new TagModel((TypeDeclarationSyntax)ctx.TargetNode, symbol.Name,
                        symbol.BaseType?.ToDisplayString() ?? string.Empty, tag, members,
                        diagnosticId, badMember ?? missingMax ?? string.Empty, badMemberType ?? string.Empty);
                })
            .Where(t => t.Decl is not null)
            .Collect();

        context.RegisterSourceOutput(roots.Combine(tags), static (spc, pair) =>
        {
            var (rootList, tagList) = pair;
            foreach (var root in rootList)
            {
                var members = tagList.Where(t => t.BaseType == FullRootName(root)).ToList();
                if (members.Count == 0) continue;

                foreach (var m in members.Where(m => m.DiagnosticId is not null))
                {
                    if (m.DiagnosticId == "TCSG051")
                        spc.ReportDiagnostic(Diagnostic.Create(UnsupportedMemberRule, m.Decl.GetLocation(), m.Name, m.DiagnosticMember, m.DiagnosticType));
                    else
                        spc.ReportDiagnostic(Diagnostic.Create(MissingMaxCountRule, m.Decl.GetLocation(), m.Name, m.DiagnosticMember));
                }
                if (members.Any(m => m.DiagnosticId is not null)) continue;

                var byTag = new Dictionary<byte, string>();
                foreach (var m in members)
                {
                    if (byTag.TryGetValue(m.Tag, out var other))
                    {
                        spc.ReportDiagnostic(Diagnostic.Create(DuplicateTagRule, m.Decl.GetLocation(), root.RootName, m.Name, m.Tag.ToString("X2"), other));
                        continue;
                    }
                    byTag[m.Tag] = m.Name;
                }
                if (byTag.Count != members.Count) continue;

                spc.AddSource($"{(string.IsNullOrEmpty(root.Namespace) ? string.Empty : root.Namespace + ".")}{root.RootName}Codec.g.cs",
                    EmitFamily(root, members));
            }
            foreach (var orphan in tagList.Where(t => rootList.All(r => FullRootName(r) != t.BaseType)))
                spc.ReportDiagnostic(Diagnostic.Create(OrphanTagRule, orphan.Decl.GetLocation(), orphan.Name));
        });
    }

    // ══ 成员收集（语义模型）══

    private static ImmutableArray<Member> CollectMembers(INamedTypeSymbol symbol, out string? badMember, out string? badMemberType, out string? missingMax)
    {
        badMember = null;
        badMemberType = null;
        missingMax = null;
        var result = ImmutableArray.CreateBuilder<Member>();
        foreach (var member in symbol.GetMembers())   // 声明序 = 线序
        {
            if (member is not IPropertySymbol { DeclaredAccessibility: Accessibility.Public, IsStatic: false } prop) continue;

            var maxCount = 0;
            var hasWireMember = false;
            foreach (var attr in prop.GetAttributes())
            {
                if (attr.AttributeClass?.Name != "WireMemberAttribute") continue;
                // 单参 ctor 的位置/命名两种实参形态都接受（[WireMember(8)] / [WireMember(MaxCount = 8)]）
                if (attr.ConstructorArguments.Length == 1 && attr.ConstructorArguments[0].Value is int mcPos)
                {
                    maxCount = mcPos;
                    hasWireMember = true;
                    continue;
                }
                foreach (var named in attr.NamedArguments)
                    if (named.Key == "MaxCount" && named.Value.Value is int mcNamed)
                    {
                        maxCount = mcNamed;
                        hasWireMember = true;
                    }
            }

            if (!TryClassify(prop.Type, out var kind, out var itemName, out var special, out var countExpr))
            {
                badMember ??= prop.Name;
                badMemberType ??= prop.Type.ToDisplayString();
                continue;
            }
            bool isVariable = kind is MemberKind.Blob or MemberKind.ListOfBool or MemberKind.ListOfPrimitive
                or MemberKind.ListOfNested or MemberKind.ListOfBlob;
            if (isVariable && !hasWireMember)
            {
                missingMax ??= prop.Name;
                continue;
            }

            result.Add(new Member(prop.Name, kind, prop.Type.ToDisplayString(), itemName, special, countExpr, maxCount));
        }
        return result.ToImmutable();
    }

    /// <summary>成员类型分类。</summary>
    /// <param name="type">成员类型符号（bool/基元/嵌套 struct/blob/集合）。</param>
    /// <param name="kind">输出：成员分类（FixedBool/FixedPrimitive/FixedNested/Blob/ListOf*）。</param>
    /// <param name="itemTypeName">输出：元素类型显示名（定长成员 = 自身类型；集合 = 元素类型）。</param>
    /// <param name="special">输出：基元 SpecialType（集合时为元素 SpecialType）。</param>
    /// <param name="countExpr">输出：计数表达式（"Length"=数组/"Count"=IReadOnlyList；定长成员为空串）。</param>
    /// <returns>true 表示在支持集；false 表示不支持（TCSG051 由调用方上报）。</returns>
    private static bool TryClassify(ITypeSymbol type, out MemberKind kind, out string itemTypeName, out SpecialType special, out string countExpr)
    {
        kind = default;
        itemTypeName = type.ToDisplayString();
        special = type.SpecialType;
        countExpr = string.Empty;

        if (type.SpecialType == SpecialType.System_Boolean) { kind = MemberKind.FixedBool; return true; }
        if (SupportedPrimitives.Contains(type.SpecialType)) { kind = MemberKind.FixedPrimitive; return true; }
        if (HasBinaryLayout(type)) { kind = MemberKind.FixedNested; return true; }

        // blob：byte[] / ReadOnlyMemory<byte>
        if (type is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte })
        {
            kind = MemberKind.Blob; countExpr = "Length"; return true;
        }
        if (type is INamedTypeSymbol rom && rom.Name == "ReadOnlyMemory" && rom.Arity == 1
            && rom.TypeArguments[0].SpecialType == SpecialType.System_Byte)
        {
            kind = MemberKind.Blob; countExpr = "Length"; return true;
        }

        // 集合：T[] / IReadOnlyList<T>
        ITypeSymbol? element = null;
        if (type is IArrayTypeSymbol a) { element = a.ElementType; countExpr = "Length"; }
        else if (type is INamedTypeSymbol { Name: "IReadOnlyList", Arity: 1 } rl) { element = rl.TypeArguments[0]; countExpr = "Count"; }
        if (element is null) return false;

        itemTypeName = element.ToDisplayString();
        special = element.SpecialType;
        if (element.SpecialType == SpecialType.System_Boolean) { kind = MemberKind.ListOfBool; return true; }
        if (SupportedPrimitives.Contains(element.SpecialType)) { kind = MemberKind.ListOfPrimitive; return true; }
        if (HasBinaryLayout(element)) { kind = MemberKind.ListOfNested; return true; }
        if (element is INamedTypeSymbol romItem && romItem.Name == "ReadOnlyMemory" && romItem.Arity == 1
            && romItem.TypeArguments[0].SpecialType == SpecialType.System_Byte)
        {
            kind = MemberKind.ListOfBlob; return true;
        }
        return false;
    }

    private static bool HasBinaryLayout(ITypeSymbol t)
        => t is INamedTypeSymbol named && t.TypeKind == TypeKind.Struct
           && named.GetAttributes().Any(a => a.AttributeClass?.Name == "BinaryLayoutAttribute");

    private static string FullRootName(RootModel root)
        => string.IsNullOrEmpty(root.Namespace) ? root.RootName : $"{root.Namespace}.{root.RootName}";

    private static string NamespaceOf(ISymbol symbol)
        => symbol.ContainingNamespace.IsGlobalNamespace ? string.Empty : symbol.ContainingNamespace.ToDisplayString();

    // ══ 生成（模板驱动——骨架走 Templates/WireMessage/*.sbn，逐成员语句行由映射表供参）══

    // ── 模板名常量（Templates/WireMessage/*.sbn 内嵌资源）──
    private const string TplCodecClass = "WireMessage/CodecClass";
    private const string TplTagConst = "WireMessage/TagConst";
    private const string TplEncodeMethod = "WireMessage/EncodeMethod";
    private const string TplEncodePooledMethod = "WireMessage/EncodePooledMethod";
    private const string TplEncodeWriterMethod = "WireMessage/EncodeWriterMethod";
    private const string TplComputeLengthMethod = "WireMessage/ComputeLengthMethod";
    private const string TplLengthCase = "WireMessage/LengthCase";
    private const string TplEncodeIntoMethod = "WireMessage/EncodeIntoMethod";
    private const string TplWriteCase = "WireMessage/WriteCase";
    private const string TplTryDecodeMethod = "WireMessage/TryDecodeMethod";
    private const string TplDecodeCase = "WireMessage/DecodeCase";

    private static string EmitFamily(RootModel root, List<TagModel> members)
    {
        var tagConsts = string.Join("\n", members.OrderBy(m => m.Tag).Select(m =>
            TemplateEngine.Render(TplTagConst, TemplateEngine.Tokens(
                ("MEMBER_NAME", m.Name),
                ("TAG_HEX", m.Tag.ToString("X2"))))));

        var lengthCases = string.Join("\n", members.Select(m =>
        {
            var stmts = Indent(JoinLines(LengthLines(root.Members).Concat(LengthLines(m.Members))), 16);
            return TemplateEngine.Render(TplLengthCase, TemplateEngine.Tokens(
                ("MEMBER_NAME", m.Name),
                ("LENGTH_STMTS", stmts)));
        }));

        var writeCases = string.Join("\n", members.Select(m =>
        {
            var stmts = Indent(JoinLines(WriteLines(root.Members).Concat(WriteLines(m.Members))), 16);
            return TemplateEngine.Render(TplWriteCase, TemplateEngine.Tokens(
                ("MEMBER_NAME", m.Name),
                ("WRITE_STMTS", stmts)));
        }));

        var decodeCases = string.Join("\n", members.Select(m =>
        {
            var inits = new List<string>();
            var stmts = JoinLines(DecodeLines(root.Members, inits).Concat(DecodeLines(m.Members, inits)));
            return TemplateEngine.Render(TplDecodeCase, TemplateEngine.Tokens(
                ("MEMBER_NAME", m.Name),
                ("DECODE_STMTS", Indent(stmts, 16)),
                ("INIT_STMTS", Indent(JoinLines(inits), 20))));
        }));

        var encodeMethod = TemplateEngine.Render(TplEncodeMethod, TemplateEngine.Tokens(("ROOT_NAME", root.RootName)));
        var encodePooledMethod = TemplateEngine.Render(TplEncodePooledMethod, TemplateEngine.Tokens(("ROOT_NAME", root.RootName)));
        var encodeWriterMethod = TemplateEngine.Render(TplEncodeWriterMethod, TemplateEngine.Tokens(("ROOT_NAME", root.RootName)));
        var computeLengthMethod = TemplateEngine.Render(TplComputeLengthMethod, TemplateEngine.Tokens(
            ("ROOT_NAME", root.RootName),
            ("LENGTH_CASES", lengthCases)));
        var encodeIntoMethod = TemplateEngine.Render(TplEncodeIntoMethod, TemplateEngine.Tokens(
            ("ROOT_NAME", root.RootName),
            ("WRITE_CASES", writeCases)));
        var tryDecodeMethod = TemplateEngine.Render(TplTryDecodeMethod, TemplateEngine.Tokens(
            ("ROOT_NAME", root.RootName),
            ("DECODE_CASES", decodeCases)));

        return TemplateEngine.Render(TplCodecClass, TemplateEngine.Tokens(
            ("NAMESPACE_BLOCK", string.IsNullOrEmpty(root.Namespace) ? "" : $"namespace {root.Namespace};\n\n"),
            ("ROOT_NAME", root.RootName),
            ("TAG_CONSTS", tagConsts),
            ("ENCODE_METHOD", encodeMethod),
            ("ENCODE_POOLED_METHOD", encodePooledMethod),
            ("ENCODE_WRITER_METHOD", encodeWriterMethod),
            ("COMPUTE_LENGTH_METHOD", computeLengthMethod),
            ("ENCODE_INTO_METHOD", encodeIntoMethod),
            ("TRY_DECODE_METHOD", tryDecodeMethod)));
    }

    /// <summary>语句行拼接（模板供参——行内缩进由语句自带，块缩进统一补齐）。</summary>
    /// <param name="lines">语句行序列。</param>
    /// <returns>用 "\n" 拼接的语句块（无尾换行）。</returns>
    private static string JoinLines(IEnumerable<string> lines) => string.Join("\n", lines);

    /// <summary>多行语句块按列缩进（首行由模板行位对齐——逐行补前缀）。</summary>
    /// <param name="block">原始语句块（"\n" 分隔，可空——空块直接返回）。</param>
    /// <param name="spaces">每行需补的前缀空格数。</param>
    /// <returns>逐行补空格前缀后的块（空行保持空，不补前缀）。</returns>
    private static string Indent(string block, int spaces)
    {
        if (block.Length == 0) return block;
        var pad = new string(' ', spaces);
        return string.Join("\n", block.Split('\n').Select(l => l.Length == 0 ? l : pad + l));
    }

    // ══ 成员行生成（长度/写入/解码三体）══

    private static IEnumerable<string> LengthLines(ImmutableArray<Member> members)
    {
        foreach (var f in members)
        {
            switch (f.Kind)
            {
                case MemberKind.FixedBool:
                    yield return "cursor += 1;";
                    break;
                case MemberKind.FixedPrimitive:
                    yield return $"cursor += {PrimSize(f.Special)};";
                    break;
                case MemberKind.FixedNested:
                    yield return $"cursor += {f.ItemTypeName}Codec.StructSize;";
                    break;
                case MemberKind.Blob:
                    yield return $"cursor += 4 + m.{f.Name}.Length;";
                    break;
                case MemberKind.ListOfBool or MemberKind.ListOfPrimitive or MemberKind.ListOfNested or MemberKind.ListOfBlob:
                    var itemExpr = f.Kind switch
                    {
                        MemberKind.ListOfBool => "1",
                        MemberKind.ListOfPrimitive => PrimSize(f.Special),
                        MemberKind.ListOfNested => $"{f.ItemTypeName}Codec.StructSize",
                        _ => "0",   // blob 项单独累加
                    };
                    yield return $"cursor += 4;";
                    if (f.Kind == MemberKind.ListOfBlob)
                    {
                        yield return $"foreach (var it in m.{f.Name}) cursor += 4 + it.Length;";
                    }
                    else
                    {
                        yield return $"cursor += m.{f.Name}.{f.CountExpr} * {itemExpr};";
                    }
                    break;
            }
        }
    }

    private static IEnumerable<string> WriteLines(ImmutableArray<Member> members)
    {
        foreach (var f in members)
        {
            switch (f.Kind)
            {
                case MemberKind.FixedBool:
                    yield return $"dest[cursor] = m.{f.Name} ? (byte)1 : (byte)0; cursor += 1;";
                    break;
                case MemberKind.FixedPrimitive:
                    yield return $"{PrimWrite(f.Special, "dest[cursor..]", $"m.{f.Name}")} cursor += {PrimSize(f.Special)};";
                    break;
                case MemberKind.FixedNested:
                    yield return $"{f.ItemTypeName}Codec.Write(dest[cursor..], m.{f.Name}); cursor += {f.ItemTypeName}Codec.StructSize;";
                    break;
                case MemberKind.Blob:
                    var spanExpr = f.TypeDisplay.Contains("ReadOnlyMemory") ? $"m.{f.Name}.Span" : $"m.{f.Name}";
                    yield return $"BinaryPrimitives.WriteInt32LittleEndian(dest[cursor..], m.{f.Name}.Length); cursor += 4;";
                    yield return $"{spanExpr}.CopyTo(dest[cursor..]); cursor += m.{f.Name}.Length;";
                    break;
                case MemberKind.ListOfBool or MemberKind.ListOfPrimitive or MemberKind.ListOfNested:
                    yield return $"BinaryPrimitives.WriteInt32LittleEndian(dest[cursor..], m.{f.Name}.{f.CountExpr}); cursor += 4;";
                    yield return $"foreach (var it in m.{f.Name})";
                    yield return "{";
                    if (f.Kind == MemberKind.ListOfBool)
                        yield return "    dest[cursor] = it ? (byte)1 : (byte)0; cursor += 1;";
                    else if (f.Kind == MemberKind.ListOfPrimitive)
                        yield return $"    {PrimWrite(f.Special, "dest[cursor..]", "it")} cursor += {PrimSize(f.Special)};";
                    else
                        yield return $"    {f.ItemTypeName}Codec.Write(dest[cursor..], it); cursor += {f.ItemTypeName}Codec.StructSize;";
                    yield return "}";
                    break;
                case MemberKind.ListOfBlob:
                    yield return $"BinaryPrimitives.WriteInt32LittleEndian(dest[cursor..], m.{f.Name}.{f.CountExpr}); cursor += 4;";
                    yield return $"foreach (var it in m.{f.Name})";
                    yield return "{";
                    yield return "    BinaryPrimitives.WriteInt32LittleEndian(dest[cursor..], it.Length); cursor += 4;";
                    yield return "    it.Span.CopyTo(dest[cursor..]); cursor += it.Length;";
                    yield return "}";
                    break;
            }
        }
    }

    private static IEnumerable<string> DecodeLines(ImmutableArray<Member> members, List<string> inits)
    {
        foreach (var f in members)
        {
            var local = LocalName(f.Name);
            switch (f.Kind)
            {
                case MemberKind.FixedBool:
                    yield return $"if (source.Length < cursor + 1) return false;";
                    yield return $"var {local} = source[cursor] != 0; cursor += 1;";
                    inits.Add($"{f.Name} = {local},");
                    break;
                case MemberKind.FixedPrimitive:
                    yield return $"if (source.Length < cursor + {PrimSize(f.Special)}) return false;";
                    yield return $"var {local} = {PrimRead(f.Special, "source[cursor..]")}; cursor += {PrimSize(f.Special)};";
                    inits.Add($"{f.Name} = {local},");
                    break;
                case MemberKind.FixedNested:
                    yield return $"if (source.Length < cursor + {f.ItemTypeName}Codec.StructSize) return false;";
                    yield return $"var {local} = {f.ItemTypeName}Codec.Read(source.Slice(cursor, {f.ItemTypeName}Codec.StructSize)); cursor += {f.ItemTypeName}Codec.StructSize;";
                    inits.Add($"{f.Name} = {local},");
                    break;
                case MemberKind.Blob:
                    yield return "if (source.Length < cursor + 4) return false;";
                    yield return $"int {local}Len = BinaryPrimitives.ReadInt32LittleEndian(source[cursor..]);";
                    yield return $"if ((uint){local}Len > {f.MaxCount}) return false;";
                    yield return $"if (source.Length < cursor + 4 + {local}Len) return false;";
                    yield return $"var {local} = source.Slice(cursor + 4, {local}Len).ToArray(); cursor += 4 + {local}Len;";
                    inits.Add($"{f.Name} = {local},");
                    break;
                case MemberKind.ListOfBool or MemberKind.ListOfPrimitive or MemberKind.ListOfNested:
                    yield return "if (source.Length < cursor + 4) return false;";
                    yield return $"int {local}Count = BinaryPrimitives.ReadInt32LittleEndian(source[cursor..]); cursor += 4;";
                    yield return $"if ((uint){local}Count > {f.MaxCount}) return false;";
                    if (f.Kind == MemberKind.ListOfNested)
                        yield return $"if (source.Length < cursor + {local}Count * {f.ItemTypeName}Codec.StructSize) return false;";
                    else if (f.Kind == MemberKind.ListOfPrimitive)
                        yield return $"if (source.Length < cursor + {local}Count * {PrimSize(f.Special)}) return false;";
                    else
                        yield return $"if (source.Length < cursor + {local}Count) return false;";
                    yield return $"var {local} = new {f.ItemTypeName}[{local}Count];";
                    yield return $"for (int i = 0; i < {local}Count; i++)";
                    yield return "{";
                    if (f.Kind == MemberKind.ListOfBool)
                        yield return $"    if (source.Length < cursor + 1) return false;";
                    else if (f.Kind == MemberKind.ListOfPrimitive)
                        yield return $"    if (source.Length < cursor + {PrimSize(f.Special)}) return false;";
                    else
                        yield return $"    if (source.Length < cursor + {f.ItemTypeName}Codec.StructSize) return false;";
                    if (f.Kind == MemberKind.ListOfBool)
                        yield return $"    {local}[i] = source[cursor] != 0; cursor += 1;";
                    else if (f.Kind == MemberKind.ListOfPrimitive)
                        yield return $"    {local}[i] = {PrimRead(f.Special, "source[cursor..]")}; cursor += {PrimSize(f.Special)};";
                    else
                        yield return $"    {local}[i] = {f.ItemTypeName}Codec.Read(source.Slice(cursor, {f.ItemTypeName}Codec.StructSize)); cursor += {f.ItemTypeName}Codec.StructSize;";
                    yield return "}";
                    inits.Add($"{f.Name} = {local},");
                    break;
                case MemberKind.ListOfBlob:
                    yield return "if (source.Length < cursor + 4) return false;";
                    yield return $"int {local}Count = BinaryPrimitives.ReadInt32LittleEndian(source[cursor..]); cursor += 4;";
                    yield return $"if ((uint){local}Count > {f.MaxCount}) return false;";
                    yield return $"var {local} = new {f.ItemTypeName}[{local}Count];";
                    yield return $"for (int i = 0; i < {local}Count; i++)";
                    yield return "{";
                    yield return "    if (source.Length < cursor + 4) return false;";
                    yield return $"    int len = BinaryPrimitives.ReadInt32LittleEndian(source[cursor..]);";
                    yield return "    if (len < 0 || source.Length < cursor + 4 + len) return false;";
                    yield return $"    {local}[i] = source.Slice(cursor + 4, len).ToArray(); cursor += 4 + len;";
                    yield return "}";
                    inits.Add($"{f.Name} = {local},");
                    break;
            }
        }
    }

    private static string LocalName(string name) => name.Length > 1
        ? char.ToLowerInvariant(name[0]) + name.Substring(1)
        : name.ToLowerInvariant();

    private static string PrimSize(SpecialType st) => st switch
    {
        SpecialType.System_Byte => "1",
        SpecialType.System_UInt16 or SpecialType.System_Int16 => "2",
        SpecialType.System_UInt32 or SpecialType.System_Int32 => "4",
        _ => "8",
    };

    private static string PrimWrite(SpecialType st, string target, string value) => st switch
    {
        SpecialType.System_Byte => $"{{ var d = {target}; d[0] = {value}; }}",
        SpecialType.System_UInt16 => $"BinaryPrimitives.WriteUInt16LittleEndian({target}, {value});",
        SpecialType.System_Int16 => $"BinaryPrimitives.WriteInt16LittleEndian({target}, {value});",
        SpecialType.System_UInt32 => $"BinaryPrimitives.WriteUInt32LittleEndian({target}, {value});",
        SpecialType.System_Int32 => $"BinaryPrimitives.WriteInt32LittleEndian({target}, {value});",
        SpecialType.System_UInt64 => $"BinaryPrimitives.WriteUInt64LittleEndian({target}, {value});",
        _ => $"BinaryPrimitives.WriteInt64LittleEndian({target}, {value});",
    };

    private static string PrimRead(SpecialType st, string sourceExpr) => st switch
    {
        SpecialType.System_Byte => $"{sourceExpr}[0]",
        SpecialType.System_UInt16 => $"BinaryPrimitives.ReadUInt16LittleEndian({sourceExpr}.Slice(0, 2))",
        SpecialType.System_Int16 => $"BinaryPrimitives.ReadInt16LittleEndian({sourceExpr}.Slice(0, 2))",
        SpecialType.System_UInt32 => $"BinaryPrimitives.ReadUInt32LittleEndian({sourceExpr}.Slice(0, 4))",
        SpecialType.System_Int32 => $"BinaryPrimitives.ReadInt32LittleEndian({sourceExpr}.Slice(0, 4))",
        SpecialType.System_UInt64 => $"BinaryPrimitives.ReadUInt64LittleEndian({sourceExpr}.Slice(0, 8))",
        _ => $"BinaryPrimitives.ReadInt64LittleEndian({sourceExpr}.Slice(0, 8))",
    };
}
