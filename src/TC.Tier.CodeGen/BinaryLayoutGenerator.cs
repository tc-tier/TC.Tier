using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TC.Tier.CodeGen;

[Generator]
public sealed class BinaryLayoutGenerator : IIncrementalGenerator
{
    /// <summary>
    /// 源生成器的命名空间必须与 [BinaryLayout] 特性所在程序集一致，
    /// </summary>
    private const string CodeGenNamespace = "TC.Tier.CodeGen";
    /// <summary>
    /// [StructLayout] 特性所在命名空间（System.Runtime.InteropServices），用于语法匹配。
    /// </summary>
    private const string InteropServicesNamespace = "System.Runtime.InteropServices";
   /// <summary>
   /// [BinaryLayout] 特性全名（不含命名空间），用于语法匹配。
   /// </summary>
    private const string BinaryLayoutName = "BinaryLayoutAttribute";
    /// <summary>
    /// [StructLayout] 特性全名（不含命名空间），用于语法匹配。
    /// </summary>
    private const string StructLayoutName = "StructLayoutAttribute";
    /// <summary>
    /// [BinaryLayout] 支持的字段校验特性全名（不含命名空间），用于语法匹配。
    /// </summary>
    private static readonly Dictionary<string,string> LayoutFieldAttributeNames = new()
    {
        {"FieldOffset","FieldOffsetAttribute"},
        { "ValidEquals", "ValidEqualsAttribute" },
        { "ValidRange", "ValidRangeAttribute" },
        { "ValidHasFlags", "ValidHasFlagsAttribute" },
        { "ValidNonDefault", "ValidNonDefaultAttribute" }
    };

    /// <summary>
    /// [BinaryLayout] 特性支持的字段名（OrFlags/IsEmpty/Features），用于语法匹配。
    /// </summary>
    private static readonly Dictionary<string, string> BinaryLayoutFieldNames = new()
    {
        {"OrFlags","OrFlags"},
        {"IsEmpty","IsEmpty"},
        {"Features","Features"},
    };

    // BinaryLayoutFeatures flag values (must match BinaryLayoutFeatures enum)
    private const int FeatureStructSize     = 1 << 0;
    private const int FeatureFieldConstants = 1 << 1;
    private const int FeatureFieldReaders   = 1 << 2;
    private const int FeatureFieldWriters   = 1 << 3;

    /// <summary>
    /// ★ Spec 27：[StructLayout].Size 与字段实际偏移和不一致时报错。
    /// 源生成器算大小靠 [StructLayout].Size，若有人写错（如字段偏移和=48 但 Size=44），
    /// 会静默生成漏字段的错误代码。此诊断强制纠错。
    /// </summary>
    private static readonly DiagnosticDescriptor SizeMismatchRule = new(
        id: "TCSG001",
        title: "BinaryLayout size mismatch",
        messageFormat: "[BinaryLayout] struct '{0}' 的 [StructLayout].Size={1}，但字段实际偏移和={2}（max(offset+size)）。两者必须一致，否则源生成器会静默生成漏字段的错误 Codec。请核对 [StructLayout(Size=...)] 与 [FieldOffset] 声明。",
        category: "CodeGeneration",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// ★ Spec 27：嵌套字段类型未标记 [BinaryLayout] 报错。
    /// 只有源生成器收集表里（已标 [BinaryLayout]）的 struct 才支持作嵌套字段。
    /// 未标记的 struct 作嵌套字段时，源生成器无法知道其大小，会静默生成 Size=0 的错误代码。
    /// </summary>
    /// <summary>TCSG003（★ 缺陷 3 修复）：字段类型不在 Emit 支持集——Int16/SByte 等会静默生成
    /// 注释/"default"（写缺字节、读恒 0），且 GetPrimitiveOrEnumSize 返回 1/2 使 TCSG001 交叉校验
    /// 不触发——潜伏的持久化正确性隐患（当前仓库无此类字段，新字段即静默损坏）——报编译期错误。</summary>
    /// <summary>TCSG004（缺陷 10）：[BinaryLayout] 标在 record struct 上——生成器只支持 struct（record 语义不符）。</summary>
    private static readonly DiagnosticDescriptor UnsupportedRecordStructRule = new(
        id: "TCSG004",
        title: "BinaryLayout on record struct unsupported",
        messageFormat: "struct '{0}' 是 record struct——[BinaryLayout] 只支持普通 struct（record 语义与显式布局不符），请改用 struct。",
        category: "CodeGeneration",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>TCSG005（缺陷 12）：[BinaryLayout] struct 缺 [StructLayout(Size=…)]——生成代码无长度校验，运行时 Slice 抛。</summary>
    private static readonly DiagnosticDescriptor MissingStructLayoutRule = new(
        id: "TCSG005",
        title: "BinaryLayout struct missing StructLayout Size",
        messageFormat: "struct '{0}' 缺 [StructLayout(LayoutKind.Explicit, Size = XxxSize)]——生成代码无长度校验（运行时 Slice 抛），请补齐。",
        category: "CodeGeneration",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnsupportedFieldTypeRule = new(
        id: "TCSG003",
        title: "Unsupported BinaryLayout field type",
        messageFormat: "字段类型 '{2}'（struct '{0}' 字段 '{1}'）不在 Emit 支持集（uint/ushort/ulong/long/int/byte/enum[底层同集]）——生成器将静默生成错误代码，请改用支持类型。",
        category: "CodeGeneration",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnsupportedNestedTypeRule = new(
        id: "TCSG002",
        title: "Unsupported nested field type",
        messageFormat: "[BinaryLayout] struct '{0}' 的字段 '{1}' 类型 '{2}' 是未标记 [BinaryLayout] 的 struct。只有标了 [BinaryLayout]+[StructLayout] 的 struct 才支持作嵌套字段（源生成器靠收集表查其大小）。请给 '{2}' 加 [BinaryLayout]+[StructLayout] 标记，或改用基元/enum 类型。",
        category: "CodeGeneration",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(static ctx =>
        {
            ctx.AddSource("BinaryLayoutGenerator.Sentinel.g.cs",
                "// <auto-generated>BinaryLayoutGenerator active.</auto-generated>\n" +
                "namespace TC.Tier.CodeGen { internal static class Sentinel { public const int Active = 1; } }\n");
        });

        // ★ 两阶段管道：收集阶段只产出各 struct 自身元数据，Collect() 后全部 struct 在表里，
        //   生成阶段才查表解析嵌套引用。前向引用由 Collect() 解决（A 引用 B 不怕 B 还没处理）。
        var layouts = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                $"{CodeGenNamespace}.{BinaryLayoutName}",
                predicate: static (node, _) => node is StructDeclarationSyntax or RecordDeclarationSyntax
                    && node is BaseTypeDeclarationSyntax { AttributeLists.Count: > 0 },
                transform: static (ctx, ct) =>
                {
                    var structSymbol = (INamedTypeSymbol)ctx.TargetSymbol;
                    ct.ThrowIfCancellationRequested();
                    // ★ 缺陷 10：record struct 带 [BinaryLayout] 静默忽略（生成器只支持 struct——
                    //   AttributeUsage 已挡 class，record struct 是 struct 语法但语义不符）——报诊断 fail-fast
                    if (ctx.TargetNode is RecordDeclarationSyntax)
                        return new LayoutInfo(
                            structSymbol.ContainingNamespace.IsGlobalNamespace ? "" : structSymbol.ContainingNamespace.ToDisplayString(),
                            structSymbol.Name, [], 0, null, null, 0,
                            Diagnostic.Create(UnsupportedRecordStructRule, ctx.TargetNode.GetLocation(), structSymbol.Name),
                            false, false, "", false, ctx.TargetNode.GetLocation());
                    return ParseLayout(structSymbol, ctx.TargetNode.GetLocation(), ct);
                })
            .Where(static layout => layout is not null)!;

        // ★ Collect() 把多值 provider 聚合成单值（全部 struct 在一个 ImmutableArray）。
        //   增量代价：任一 struct 改动 → 全表重建 → 全部 Codec 重生成（可接受，源生成规模小）。
        var allLayouts = layouts.Collect();

        // ★ 合并 compilation provider 用于跨程序集 struct size 解析
        var combined = allLayouts.Combine(context.CompilationProvider);

        context.RegisterSourceOutput(combined, (spc, tuple) =>
        {
            var (list, compilation) = tuple;
            // 先报所有诊断（不生成都报，错误信息集中）
            foreach (var layout in list)
            {
                if (layout?.Diagnostic is not null)
                    spc.ReportDiagnostic(layout.Diagnostic);
            }
            // 建嵌套大小表：全名 → StructLayout.Size（收集阶段已解析，本编译内可见）
            //   ★ 只含无诊断的 struct（有诊断的不入表，避免用错误大小）
            var nestedSizeTable = new Dictionary<string, int>(list.Length);
            foreach (var layout in list.Where(layout => layout?.Diagnostic is null))
            {
                if (layout?.SizeConstValue > 0)
                    nestedSizeTable[layout.FullName] = layout.SizeConstValue;
            }
            // ★ 补全：从引用程序集中收集 [StructLayout] unmanaged struct 的大小
            CollectExternalStructSizes(compilation, nestedSizeTable);
            // 生成阶段：逐个 emit，查嵌套表解析嵌套字段大小
            foreach (var layout in Enumerable.OfType<LayoutInfo>(list).Where(layout => layout.Diagnostic is null))
            {
                var (hintName, source, diag) = EmitCodec(layout, nestedSizeTable);
                if (diag is not null) { spc.ReportDiagnostic(diag); continue; }
                spc.AddSource(hintName, source);
            }
        });
    }

    private static LayoutInfo? ParseLayout(INamedTypeSymbol structSymbol, Location location, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var fields = new List<FieldInfo>();
        Diagnostic? unsupportedFieldType = null;   // ★ 缺陷 3：非支持字段类型 fail-fast（TCSG003）

        foreach (var member in structSymbol.GetMembers())
        {
            ct.ThrowIfCancellationRequested();
            if (member is not IFieldSymbol field) continue;
            if (field.IsStatic) continue;

            int offset = -1;
            object? eqExpected = null;
            object? rangeMin = null, rangeMax = null;
            object? hasFlagsMask = null;
            bool nonDefault = false;

            foreach (var attr in field.GetAttributes())
            {
                if (attr.AttributeClass is null) continue;
                var name = attr.AttributeClass.Name;
                // ★ 特性遍历禁 break：字段可同时带 FieldOffset + 校验特性（[FieldOffset(0), ValidEquals(X)]）——
                //   旧 break 令 FieldOffset 命中即退出，校验特性从未解析（Validate 从未生成、validate:true 静默空转）。
                if (LayoutFieldAttributeNames["FieldOffset"] == name)
                {
                    if (attr.ConstructorArguments is [{ Value: int o } _])
                        offset = o;
                    continue;
                }
                if (LayoutFieldAttributeNames["ValidEquals"] == name)
                {
                    if (attr.ConstructorArguments.Length == 1)
                        eqExpected = attr.ConstructorArguments[0].Value;
                    continue;
                }
                if (LayoutFieldAttributeNames["ValidRange"] == name)
                {
                    if (attr.ConstructorArguments.Length == 2)
                    {
                        rangeMin = attr.ConstructorArguments[0].Value;
                        rangeMax = attr.ConstructorArguments[1].Value;
                    }
                    continue;
                }
                if (LayoutFieldAttributeNames["ValidHasFlags"] == name)
                {
                    if (attr.ConstructorArguments.Length == 1)
                        hasFlagsMask = attr.ConstructorArguments[0].Value;
                    continue;
                }
                if (LayoutFieldAttributeNames["ValidNonDefault"] == name)
                {
                    nonDefault = true;
                    continue;
                }
            }
            if (offset < 0) continue;

            // ★ 缺陷 3（fail-fast）：基元/enum 类型必须在 Emit 支持集（EmitWriteStmt/EmitReadExpr
            //   只覆盖 UInt32/UInt16/UInt64/Int64/Int32/Byte + 底层同集枚举）——Int16/SByte 静默
            //   生成注释/"default"（写缺字节/读恒 0）且 TCSG001 交叉校验不触发；嵌套 struct 由
            //   IsNestedStruct 路由（Emit 阶段处理）不在此检查。
            if (!IsNestedStruct(field.Type) && !IsEmitSupportedPrimitiveOrEnum(field.Type))
            {
                unsupportedFieldType ??= Diagnostic.Create(UnsupportedFieldTypeRule, location,
                    structSymbol.Name, field.Name, field.Type.ToDisplayString());
                continue;
            }

            Constraint? constraint = null;
            if (eqExpected is not null)
                constraint = new Constraint(ConstraintKind.Equals, eqExpected, null, null, null, false);
            else if (hasFlagsMask is not null)
                constraint = new Constraint(ConstraintKind.HasFlags, null, null, null, hasFlagsMask, false);
            else if (rangeMin is not null && rangeMax is not null)
                constraint = new Constraint(ConstraintKind.Range, null, rangeMin, rangeMax, null, false);
            else if (nonDefault)
                constraint = new Constraint(ConstraintKind.NonDefault, null, null, null, null, true);

            fields.Add(new FieldInfo(field.Name, field.Type, offset, constraint)
            {
                // ★ 收集阶段：基元/enum 算好 size；嵌套 struct 留 null（生成阶段查收集表）
                ResolvedSize = IsNestedStruct(field.Type)
                    ? null
                    : int.Parse(GetPrimitiveOrEnumSize(field.Type), System.Globalization.CultureInfo.InvariantCulture)
            });
        }

        if (fields.Count == 0) return null;

        int sizeConst = 0;
        Diagnostic? sizeMismatch = null;   // TCSG001（Size 交叉校验）/ TCSG005（缺 StructLayout）
        var structLayoutAttr = structSymbol.GetAttributes().FirstOrDefault(
            a => a.AttributeClass?.Name == StructLayoutName);
        // ★ 缺陷 12（fail-fast）：缺 [StructLayout] 或 Size → TCSG005（生成代码无长度校验——运行时 Slice 抛）
        if (structLayoutAttr is null)
        {
            sizeMismatch ??= Diagnostic.Create(MissingStructLayoutRule, location, structSymbol.Name);
        }
        if (structLayoutAttr is not null)
        {
            foreach (var na in structLayoutAttr.NamedArguments)
            {
                if (na is { Key: "Size", Value.Value: int sz and > 0 })
                {
                    sizeConst = sz;
                    break;
                }
            }
        }

        // ★ Spec 27 问题 4 修复：交叉验证 StructLayout.Size vs 字段实际偏移和。
        //   源生成器算大小靠 [StructLayout].Size，若写错（如字段和=48 但 Size=44）会静默生成漏字段代码。
        //   检测：max(字段 offset + size) 必须 == StructLayout.Size，否则报 TCSG001。
        //   ★ 收集阶段只能校验基元/enum 字段（已解析 size）；嵌套字段留待 EmitCodec 查表后补校验。
        int computedFieldExtent = 0;
        bool hasUnresolvedNested = false;
        foreach (var f in fields)
        {
            if (!f.ResolvedSize.HasValue) { hasUnresolvedNested = true; continue; }
            int extent = f.Offset + f.ResolvedSize.Value;
            if (extent > computedFieldExtent) computedFieldExtent = extent;
        }
        // 仅当无嵌套字段（全部已解析）时才在收集阶段校验；有嵌套的推迟到 EmitCodec
        if (!hasUnresolvedNested && sizeConst > 0 && computedFieldExtent != sizeConst)
        {
            sizeMismatch = Diagnostic.Create(SizeMismatchRule, location,
                structSymbol.Name, sizeConst, computedFieldExtent);
        }

        string? orFlagsField = null;
        string? isEmptyField = null;
        int featureFlags = 0;

        var layoutAttr = structSymbol.GetAttributes().FirstOrDefault(
            a => a.AttributeClass?.Name == BinaryLayoutName);
        if (layoutAttr is not null)
        {
            foreach (var na in layoutAttr.NamedArguments)
            {
                if (na.Key == BinaryLayoutFieldNames["OrFlags"] && na.Value.Value is string of && !string.IsNullOrEmpty(of))
                    orFlagsField = of;
                if (na.Key == BinaryLayoutFieldNames["IsEmpty"] && na.Value.Value is string ie && !string.IsNullOrEmpty(ie))
                    isEmptyField = ie;
                if (na.Key == BinaryLayoutFieldNames["Features"] && na.Value.Value is int ff)
                    featureFlags = ff;
            }
        }

        // ★ readonly struct 构造函数匹配检测：readonly 字段只能在构造函数赋值，
        //   故 Read 必须用构造调用。找最大参数构造函数，参数类型序列 == 字段（按 offset 排序）
        //   类型序列才 CanConstruct=true（能生成结构化 Read）；否则不生成整体 Read。
        //   非 readonly struct 恒 CanConstruct=true（用 object initializer，不依赖构造函数）。
        bool isReadOnly = structSymbol.IsReadOnly;
        bool canConstruct;
        if (!isReadOnly)
        {
            canConstruct = true;
        }
        else
        {
            // readonly：按 offset 排序的字段类型序列
            var fieldTypeSeq = fields.OrderBy(f => f.Offset).Select(f => f.Type).ToList();
            // 找最大参数的实例构造函数（排除无参默认）
            var maxCtor = structSymbol.InstanceConstructors
                .Where(c => c.Parameters.Length > 0)
                .OrderByDescending(c => c.Parameters.Length)
                .FirstOrDefault();
            canConstruct = maxCtor is not null
                && maxCtor.Parameters.Length == fieldTypeSeq.Count
                && maxCtor.Parameters.Select(p => p.Type)
                    .SequenceEqual(fieldTypeSeq, SymbolEqualityComparer.Default);
        }

        return new LayoutInfo(
            structSymbol.ContainingNamespace.IsGlobalNamespace ? "" : structSymbol.ContainingNamespace.ToDisplayString(),
            structSymbol.Name,
            fields,
            sizeConst,
            orFlagsField,
            isEmptyField,
            featureFlags,
            unsupportedFieldType ?? sizeMismatch,
            isReadOnly,
            canConstruct,
            BuildContainingTypePrefix(structSymbol),
            structSymbol.DeclaredAccessibility == Accessibility.Public,
            location);
    }

    /// <summary>
    /// ★ 构造嵌套类型的包含类型前缀（如 struct 嵌于 <c>DeviceBase</c> → <c>"DeviceBase."</c>；
    ///   非嵌套 → <c>""</c>）。源生成器的 codec 放在顶层命名空间，引用 struct 时必须带此前缀，
    ///   否则裸名（如 <c>DeviceMetaHeader</c>）在顶层命名空间下找不到嵌套类型。
    /// <para>★ 多层嵌套（A.B.C）按外→内顺序拼接：ContainingType 自顶向下遍历累加。</para>
    /// </summary>
    private static string BuildContainingTypePrefix(INamedTypeSymbol structSymbol)
    {
        if (structSymbol.ContainingType is null) return "";
        // ContainingType 是直接外层；多层嵌套需向上收集后反转（外→内顺序）
        var stack = new System.Collections.Generic.Stack<string>();
        for (var t = structSymbol.ContainingType; t is not null; t = t.ContainingType)
            stack.Push(t.Name);
        var sb = new System.Text.StringBuilder();
        while (stack.Count > 0)
            sb.Append(stack.Pop()).Append('.');
        return sb.ToString();
    }


    /// <summary>
    /// 生成单个 struct 的 Codec。生成阶段调用——此时嵌套大小表已建好（本编译内所有 [BinaryLayout] struct）。
    /// <param name="nestedSizeTable">本编译内所有 [BinaryLayout] struct 的全名→大小表（收集表）。</param>
    /// <returns>(hintName, source, diagnostic)。diagnostic 非 null 表示嵌套字段类型不支持（TCSG002），不生成。</returns>
    /// /// </summary>
    private static (string hintName, string source, Diagnostic? diagnostic) EmitCodec(
        LayoutInfo layout, Dictionary<string, int> nestedSizeTable)
    {
        // ★ 生成阶段：解析嵌套字段大小（收集阶段留 null 的）。查收集表，查不到报 TCSG002。
        foreach (var f in layout.Fields.Where(f => !f.ResolvedSize.HasValue))
        {
            if (!IsNestedStruct(f.Type))
            {
                f.ResolvedSize = 0; // 不该发生（非嵌套非基元），安全兜底
                continue;
            }
            // 查收集表（标记驱动：只有 [BinaryLayout] struct 才在表里）
            var typeFullName = f.Type.ToDisplayString();
            if (nestedSizeTable.TryGetValue(typeFullName, out var sz) && sz > 0)
            {
                f.ResolvedSize = sz;
            }
            else
            {
                // 收集表没找到，尝试从引用程序集的 [StructLayout] 获取 Size
                var externalSize = ResolveExternalStructSize(f.Type);
                if (externalSize > 0)
                {
                    f.ResolvedSize = externalSize;
                    nestedSizeTable[typeFullName] = externalSize;
                }
                else
                {
                    // 未标记 [BinaryLayout] 的 struct 不能作嵌套字段——报错，不生成
                    return (HintName(layout), "",
                        Diagnostic.Create(UnsupportedNestedTypeRule, layout.Location,
                            layout.StructName, f.Name, typeFullName));
                }
            }
        }

        // ★ TCSG001 补校验（含嵌套字段）：收集阶段因嵌套 size 未解析跳过了校验，
        //   此处全部字段已解析，重算 extent 与 StructLayout.Size 比对。
        if (layout.SizeConstValue > 0)
        {
            var extent = layout.Fields.Select(f => f.Offset + (f.ResolvedSize ?? 0)).Prepend(0).Max();
            if (extent != layout.SizeConstValue)
            {
                return (HintName(layout), "",
                    Diagnostic.Create(SizeMismatchRule, layout.Location,
                        layout.StructName, layout.SizeConstValue, extent));
            }
        }

        var ns = layout.Namespace;
        var structName = layout.StructName;
        // ★ 缺陷 11（hintName 唯一化）：含命名空间与包含类型前缀（. → _）——跨命名空间同名
        //   struct 不再触发 AddSource 同名注册（CS8785——生成器整体失效殃及全部 Codec）
        var hintName = HintName(layout);
        // ★ structRefName：codec 内部引用 struct 类型时用的名字（带包含类型前缀）。
        //   codec 类自身放在顶层命名空间，裸 structName 在顶层命名空间找不到嵌套类型，必须带前缀
        //   （如 DeviceBase.DeviceMetaHeader）。codec 类名沿用裸 structName（DeviceMetaHeaderCodec）。
        var structRefName = layout.ContainingType + structName;
        var codecName = structName + "Codec";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        if (!string.IsNullOrEmpty(ns))
        {
            sb.Append("namespace ").Append(ns).AppendLine(";");
            sb.AppendLine();
        }

        sb.Append(layout.IsPublic ? "public" : "internal").Append(" static class ").AppendLine(codecName);
        sb.AppendLine("{");

        // ── StructSize ──
        if ((layout.FeatureFlags & FeatureStructSize) != 0 && layout.SizeConstValue > 0)
        {
            sb.Append("    public const int StructSize = ").Append(layout.SizeConstValue).AppendLine(";");
            sb.AppendLine();
        }

        // ── FieldConstants: Offset_*/Size_* ──
        if ((layout.FeatureFlags & FeatureFieldConstants) != 0)
        {
            foreach (var f in layout.Fields)
            {
                sb.Append("    public const int Offset_").Append(f.Name).Append(" = ").Append(f.Offset).AppendLine(";");
                sb.Append("    public const int Size_").Append(f.Name).Append(" = ").Append(FieldSizeStr(f)).AppendLine(";");
            }
            sb.AppendLine();
        }

        // ── FieldReaders: Read_* ──
        if ((layout.FeatureFlags & FeatureFieldReaders) != 0)
        {
            EmitFieldReaders(sb, layout.Fields);
        }

        // ── FieldWriters: Write_*（对称 Read_*，收口字节序，避免业务层手写 BinaryPrimitives）──
        if ((layout.FeatureFlags & FeatureFieldWriters) != 0)
        {
            EmitFieldWriters(sb, layout.Fields);
        }

        // ── Create（默认值——ValidEquals 约束字段自动填常量；调用方只填变化字段）──
        // ★ 语义：ValidEquals 声明"字段恒等于该常量"——默认值即该常量，无需独立 DefaultValue 特性
        //   （双声明重复）。仅非 readonly struct（object initializer 可写字段）；readonly 不生成
        //   （构造参数序列=全字段，无参 Create 无法赋 readonly 字段）。
        bool hasEqualsDefaults = layout.Fields.Exists(f => f.Constraint is { Kind: ConstraintKind.Equals });
        if (hasEqualsDefaults && !layout.IsReadOnly)
        {
            sb.Append("    /// <summary>合法默认实例——[ValidEquals] 字段自动填常量（写侧只填变化字段）。</summary>").AppendLine();
            sb.Append("    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]").AppendLine();
            sb.Append("    public static ").Append(structRefName).AppendLine(" Create()");
            sb.AppendLine("    {");
            sb.Append("        return new ").Append(structRefName).AppendLine();
            sb.AppendLine("        {");
            for (int i = 0; i < layout.Fields.Count; i++)
            {
                var f = layout.Fields[i];
                string init = f.Constraint is { Kind: ConstraintKind.Equals } c
                    ? FormatConst(c.EqExpected, f.Type)
                    : "default";
                sb.Append("            ").Append(f.Name).Append(" = ").Append(init)
                  .Append(i == layout.Fields.Count - 1 ? "" : ",").AppendLine();
            }
            sb.AppendLine("        };");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        // ── Validate ──
        bool hasConstraints = layout.Fields.Exists(f => f.Constraint is not null);
        if (hasConstraints)
        {
            sb.Append("    public static bool Validate(in ").Append(structRefName).AppendLine(" value)");
            sb.AppendLine("    {");
            foreach (var f in layout.Fields)
            {
                if (f.Constraint is not null)
                    EmitValidateLine(sb, structName, f);
            }
            sb.AppendLine("        return true;");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        // ── Write ──
        // ★ validate 语义 = 防御性补全（非抛异常）：ValidEquals 字段不信任入参、强制写常量——
        //   调用方可传 default（如 MetaPolicy.WriteHeader(default)），布局层保证规范字段合法。
        sb.Append("    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]").AppendLine();
        sb.Append("    public static void Write(System.Span<byte> dest, in ").Append(structRefName).AppendLine(" value, bool validate = false)");
        sb.AppendLine("    {");
        if (layout.SizeConstValue > 0)
        {
            sb.Append("        if (dest.Length < ").Append(layout.SizeConstValue).AppendLine(")");
            sb.AppendLine("            throw new System.ArgumentException(\"Buffer too small\");");
        }
        foreach (var f in layout.Fields)
        {
            if (f.Constraint is { Kind: ConstraintKind.Equals } c)
                // ★ 防御性补全：validate=true 时 ValidEquals 字段不信任入参、强制写常量
                //   （调用方可传 default——如 MetaPolicy.WriteHeader(default)，布局层保证规范字段合法）
                EmitWriteLine(sb, f, "validate ? " + FormatConst(c.EqExpected, f.Type) + " : value." + f.Name);
            else
                EmitWriteLine(sb, f, null);
        }
        sb.AppendLine("    }");
        sb.AppendLine();

        // ── Read ──
        // ★ 字节序铁律（unified-binary-layout.md §1.2）：全部 BinaryPrimitives 小端，禁 MemoryMarshal。
        //   三分支：
        //   - readonly + CanConstruct（最大构造函数参数类型序列 == 字段类型序列）：
        //     构造调用 new StructName(field1LE, field2LE, ...)（readonly 字段只能构造函数赋值）。
        //   - readonly + !CanConstruct：不生成整体 Read（无法构造覆盖全部 readonly 字段）。
        //   - 非 readonly：object initializer（字段可写）。
        if (!layout.IsReadOnly || layout.CanConstruct)
        {
            sb.Append("    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]").AppendLine();
            sb.Append("    public static ").Append(structRefName).AppendLine(" Read(System.ReadOnlySpan<byte> source)");
            sb.AppendLine("    {");
            if (layout.SizeConstValue > 0)
            {
                sb.Append("        if (source.Length < ").Append(layout.SizeConstValue).AppendLine(")");
                sb.AppendLine("            throw new System.ArgumentException(\"Buffer too small\");");
            }
            if (layout.IsReadOnly)
            {
                // readonly + CanConstruct：构造调用（字段按 offset 排序对应构造函数参数序）
                var orderedFields = layout.Fields.OrderBy(f => f.Offset).ToList();
                sb.Append("        return new ").Append(structRefName).Append('(');
                for (var i = 0; i < orderedFields.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(EmitReadExpr(orderedFields[i], "source"));
                }
                sb.AppendLine(");");
            }
            else
            {
                sb.Append("        return new ").Append(structRefName).AppendLine();
                sb.AppendLine("        {");
                for (int i = 0; i < layout.Fields.Count; i++)
                    EmitReadLine(sb, layout.Fields[i], i == layout.Fields.Count - 1);
                sb.AppendLine("        };");
            }
            sb.AppendLine("    }");
            sb.AppendLine();
        }
        else
        {
            // readonly + !CanConstruct：不生成整体 Read
            sb.Append("    // readonly struct ").Append(structName).AppendLine(" 的最大构造函数不覆盖全部字段，不生成整体 Read。");
            sb.Append("    // 可用 FieldConstants(Offset_/Size_) + 单字段 Read_* 方法自行组合。");
            sb.AppendLine();
        }

        // ── OrFlags / IsEmpty ──
        if (!string.IsNullOrEmpty(layout.OrFlagsField))
        {
            var f = layout.Fields.Find(x => x.Name == layout.OrFlagsField);
            if (f is not null) EmitOrFlagsMethod(sb, structName, f);
        }
        if (!string.IsNullOrEmpty(layout.IsEmptyField))
        {
            var f = layout.Fields.Find(x => x.Name == layout.IsEmptyField);
            if (f is not null) EmitIsEmptyMethod(sb, structName, f);
        }

        sb.AppendLine("}");

        return (hintName, sb.ToString(), null);
    }

    // ── FieldReaders 生成 ──

    private static void EmitFieldReaders(System.Text.StringBuilder sb,
        System.Collections.Generic.List<FieldInfo> fields)
    {
        foreach (var f in fields)
        {
            var retType = f.Type.ToDisplayString();
            var readExpr = EmitReadExpr(f, "source");
            sb.Append("    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]").AppendLine();
            sb.Append("    public static ").Append(retType).Append(" Read_").Append(f.Name)
              .AppendLine("(System.ReadOnlySpan<byte> source)");
            sb.Append("        => ").Append(readExpr).AppendLine(";");
            sb.AppendLine();
        }
    }

    // ── FieldWriters 生成（对称 FieldReaders）──
    // 生成 Write_{FieldName}(Span<byte> dest, T value)：单字段写入（带字节序收口），
    // 避免业务层手写 BinaryPrimitives.WriteXxxLittleEndian（字节序铁律强制统一）。

    private static void EmitFieldWriters(System.Text.StringBuilder sb,
        System.Collections.Generic.List<FieldInfo> fields)
    {
        foreach (var f in fields)
        {
            var paramType = f.Type.ToDisplayString();
            var writeStmt = EmitWriteStmt(f, "dest", "value");
            sb.Append("    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]").AppendLine();
            sb.Append("    public static void Write_").Append(f.Name)
              .Append("(System.Span<byte> dest, ").Append(paramType).AppendLine(" value)");
            sb.AppendLine("    {");
            sb.Append("        ").Append(writeStmt);
            sb.AppendLine("    }");
            sb.AppendLine();
        }
    }

    /// <summary>
    /// 构造单字段写入语句（不带缩进前缀，调用方加）。
    /// <param name="destVar">目标 buffer 变量名（Span&lt;byte&gt;）。</param>
    /// <param name="valueVar">值变量名（单字段写入时，值直接是该字段类型，非 struct 字段访问）。</param>
    /// </summary>
    private static string EmitWriteStmt(FieldInfo f, string destVar, string valueVar)
    {
        var s = f.Type;
        var slice = destVar + ".Slice(" + f.Offset + ", " + FieldSizeStr(f) + ")";
        switch (s.SpecialType)
        {
            case SpecialType.System_UInt32:
                return "System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(" + slice + ", " + valueVar + ");";
            case SpecialType.System_UInt16:
                return "System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(" + slice + ", " + valueVar + ");";
            case SpecialType.System_UInt64:
                return "System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(" + slice + ", " + valueVar + ");";
            case SpecialType.System_Int64:
                return "System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(" + slice + ", " + valueVar + ");";
            case SpecialType.System_Int32:
                return "System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(" + slice + ", " + valueVar + ");";
            case SpecialType.System_Byte:
                return destVar + "[" + f.Offset + "] = " + valueVar + ";";
            default:
                // 嵌套 [BinaryLayout] struct：委托被嵌套 Codec.Write（小端逐字段）
                if (IsNestedStruct(s))
                    return s.ToDisplayString() + "Codec.Write(" + slice + ", in " + valueVar + ");";
                // enum：按底层基元 cast 后小端写
                if (s.TypeKind == TypeKind.Enum && s is INamedTypeSymbol e && e.EnumUnderlyingType is { } under)
                {
                    var ut = under.SpecialType;
                    var v = "(" + under.ToDisplayString() + ")" + valueVar;
                    switch (ut)
                    {
                        case SpecialType.System_UInt32:
                            return "System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(" + slice + ", " + v + ");";
                        case SpecialType.System_UInt16:
                            return "System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(" + slice + ", " + v + ");";
                        case SpecialType.System_UInt64:
                            return "System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(" + slice + ", " + v + ");";
                        case SpecialType.System_Int64:
                            return "System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(" + slice + ", " + v + ");";
                        case SpecialType.System_Byte:
                            return destVar + "[" + f.Offset + "] = " + v + ";";
                        default:
                            return "System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(" + slice + ", " + v + ");";
                    }
                }
                // 其它不支持（与 Read/Write 整体一致：源生成器只处理基元/enum/嵌套 struct）
                return "// unsupported field type: " + s.ToDisplayString();
        }
    }

    private static string EmitReadExpr(FieldInfo f, string sourceVar)
    {
        var s = f.Type;
        var sl = sourceVar + ".Slice(" + f.Offset + ", " + FieldSizeStr(f) + ")";
        switch (s.SpecialType)
        {
            case SpecialType.System_UInt32:
                return "System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(" + sl + ")";
            case SpecialType.System_UInt16:
                return "System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(" + sl + ")";
            case SpecialType.System_UInt64:
                return "System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(" + sl + ")";
            case SpecialType.System_Int64:
                return "System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(" + sl + ")";
            case SpecialType.System_Int32:
                return "System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(" + sl + ")";
            case SpecialType.System_Byte:
                return sourceVar + "[" + f.Offset + "]";
            default:
                // ★ Spec 27: 嵌套 [BinaryLayout] struct（如 SegmentAddress）——委托给被嵌套 struct 的 Codec.Read
                //   （小端逐字段，符合字节序铁律；禁 MemoryMarshal——平台字节序违反 §1.2）。
                //   被嵌套 struct 必标 [BinaryLayout]（TCSG002 保证在收集表 → 有 Codec）。
                if (IsNestedStruct(s))
                    return s.ToDisplayString() + "Codec.Read(" + sl + ")";
                if (s.TypeKind == TypeKind.Enum && s is INamedTypeSymbol e && e.EnumUnderlyingType is { } under)
                {
                    var cast = "(" + e.ToDisplayString() + ")";
                    switch (under.SpecialType)
                    {
                        case SpecialType.System_UInt32:
                            return cast + "System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(" + sl + ")";
                        case SpecialType.System_UInt16:
                            return cast + "System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(" + sl + ")";
                        case SpecialType.System_UInt64:
                            return cast + "System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(" + sl + ")";
                        case SpecialType.System_Int64:
                            return cast + "System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(" + sl + ")";
                        case SpecialType.System_Byte:
                            return cast + sourceVar + "[" + f.Offset + "]";
                        default:
                            return cast + "System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(" + sl + ")";
                    }
                }
                return "default";
        }
    }

    /// <summary>
    /// 字段大小（字符串形式，用于代码生成）。基元/enum 走 <see cref="GetPrimitiveOrEnumSize"/>，
    /// 嵌套 struct 走 <see cref="FieldInfo.ResolvedSize"/>（生成阶段查收集表填好）。
    /// </summary>
    private static string FieldSizeStr(FieldInfo f)
    {
        // 基元/enum：收集阶段已算好存入 ResolvedSize
        // 嵌套 struct：生成阶段查表填入 ResolvedSize
        return (f.ResolvedSize ?? 0).ToString();
    }

    /// <summary>★ 缺陷 3：字段类型是否在 Emit 支持集（EmitWriteStmt/EmitReadExpr 覆盖）——
    /// uint/ushort/ulong/long/int/byte + 底层同集的 enum（嵌套 struct 走 IsNestedStruct 路由）。</summary>
    private static bool IsEmitSupportedPrimitiveOrEnum(ITypeSymbol t)
    {
        var st = t.SpecialType;
        if (st is SpecialType.System_UInt32 or SpecialType.System_UInt16 or SpecialType.System_UInt64
            or SpecialType.System_Int64 or SpecialType.System_Int32 or SpecialType.System_Byte)
            return true;
        return t is { TypeKind: TypeKind.Enum } e && e is INamedTypeSymbol named
            && named.EnumUnderlyingType is { } under && IsEmitSupportedPrimitiveOrEnum(under);
    }

    /// <summary>基元类型 + enum 的字节大小（收集阶段可解析，不依赖跨 struct 表）。</summary>
    private static string GetPrimitiveOrEnumSize(ITypeSymbol s) => s.SpecialType switch
    {
        SpecialType.System_UInt32 or SpecialType.System_Int32 => "4",
        SpecialType.System_UInt16 or SpecialType.System_Int16 => "2",
        SpecialType.System_UInt64 or SpecialType.System_Int64 => "8",
        SpecialType.System_Byte or SpecialType.System_SByte => "1",
        _ => s is { TypeKind: TypeKind.Enum } e2 ? GetEnumSize(e2) : "0",
    };

    /// <summary>
    /// ★ Spec 27: 判断字段类型是否嵌套 unmanaged struct（读写委托给被嵌套 struct 的 Codec）。
    /// <para>★ 跨程序集可靠：用类型系统判断（IsValueType + IsUnmanagedType），不依赖
    ///   <c>[StructLayout]</c> 伪属性（跨程序集 GetAttributes 拿不到）。</para>
    /// <para>★ 标记驱动：只有 <c>[BinaryLayout]</c> 收集表里的 struct 才支持嵌套（见 EmitCodec 的 TCSG002）。</para>
    /// </summary>
    private static bool IsNestedStruct(ITypeSymbol s)
        => s is { TypeKind: TypeKind.Struct, IsUnmanagedType: true }
           && !s.SpecialType.ToString().StartsWith("System_", StringComparison.Ordinal);

    /// <summary>
    /// 尝试从引用程序集的 [StructLayout] 获取 Size。
    /// 当嵌套字段类型不在本编译的收集表中时（跨程序集引用），
    /// 回退读取 [StructLayout(Size=N)] 的 Size 参数。
    /// </summary>
    private static int ResolveExternalStructSize(ITypeSymbol type)
    {
        // ★ 跨程序集 struct size fallback：对于常见的一等公民类型，直接返回已知大小。
        //   跨程序集时 GetAttributes() 不返回 StructLayout/FieldOffset 等伪属性，
        //   无法静态计算 extent。此处手动维护已知嵌套类型大小表。
        var fullName = type.ToDisplayString();
        if (fullName.EndsWith(".LogicalAddress", StringComparison.Ordinal))
            return 16;
        if (type is not INamedTypeSymbol namedType) return 0;
        if (namedType.TypeKind != TypeKind.Struct) return 0;
        // 通用兜底：遍历字段计算 extent（仅当能拿到 [FieldOffset] 才有效）
        int maxExtent = 0;
        foreach (var member in namedType.GetMembers())
        {
            if (member is not IFieldSymbol field || field.IsStatic) continue;
            int offset = 0;
            int fieldSize = 0;
            foreach (var attr in field.GetAttributes())
            {
                if (attr.AttributeClass?.Name == "FieldOffsetAttribute" && attr.ConstructorArguments.Length == 1)
                {
                    if (attr.ConstructorArguments[0].Value is int o)
                        offset = o;
                }
            }
            var fieldType = field.Type;
            if (fieldType.SpecialType != SpecialType.None)
            {
                fieldSize = fieldType.SpecialType switch
                {
                    SpecialType.System_Boolean => 1,
                    SpecialType.System_Byte or SpecialType.System_SByte => 1,
                    SpecialType.System_Int16 or SpecialType.System_UInt16 => 2,
                    SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Single => 4,
                    SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_Double => 8,
                    _ => 0
                };
            }
            else if (fieldType is { TypeKind: TypeKind.Enum } && fieldType is INamedTypeSymbol e)
            {
                fieldSize = e.EnumUnderlyingType?.SpecialType switch
                {
                    SpecialType.System_Byte or SpecialType.System_SByte => 1,
                    SpecialType.System_Int16 or SpecialType.System_UInt16 => 2,
                    SpecialType.System_Int32 or SpecialType.System_UInt32 => 4,
                    SpecialType.System_Int64 or SpecialType.System_UInt64 => 8,
                    _ => 0
                };
            }
            int extent = offset + fieldSize;
            if (extent > maxExtent) maxExtent = extent;
        }
        return maxExtent;
    }

    /// <summary>
    /// 从引用程序集中收集所有带 [StructLayout] 的 unmanaged struct 的大小，
    /// 补充到嵌套大小表中，解决跨程序集的嵌套 struct 大小解析。
    /// </summary>
    private static void CollectExternalStructSizes(
        Compilation compilation, Dictionary<string, int> table)
    {
        // 遍历所有引用程序集中的类型
        foreach (var referencedAssembly in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            CollectStructSizesInNamespace(referencedAssembly.GlobalNamespace, table);
        }
    }

    private static void CollectStructSizesInNamespace(
        INamespaceSymbol ns, Dictionary<string, int> table)
    {
        foreach (var member in ns.GetTypeMembers())
        {
            if (member.TypeKind != TypeKind.Struct) continue;
            if (!member.IsUnmanagedType) continue;
            var fullName = member.ToDisplayString();
            if (table.ContainsKey(fullName)) continue;

            // 尝试从 [StructLayout] 提取 Size
            foreach (var attr in member.GetAttributes())
            {
                if (attr.AttributeClass?.Name != StructLayoutName
                    && attr.AttributeClass?.ToDisplayString() != InteropServicesNamespace + "." + StructLayoutName)
                    continue;
                foreach (var na in attr.NamedArguments)
                {
                    if (na.Key == "Size" && na.Value.Value is int size && size > 0)
                    {
                        table[fullName] = size;
                        break;
                    }
                }
            }
        }
        foreach (var child in ns.GetNamespaceMembers())
            CollectStructSizesInNamespace(child, table);
    }

    private static string GetEnumSize(ISymbol e)
    {
        if (e is INamedTypeSymbol ne && ne.EnumUnderlyingType is { } u)
            return GetPrimitiveOrEnumSize(u);
        return "0";
    }

    // ── Validate ──

    private static void EmitValidateLine(System.Text.StringBuilder sb, string structName, FieldInfo f)
    {
        var c = f.Constraint!;
        var fieldName = "value." + f.Name;
        switch (c.Kind)
        {
            case ConstraintKind.Equals:
                sb.Append("        if (").Append(fieldName).Append(" != ").Append(FormatConst(c.EqExpected, f.Type)).AppendLine(") return false;");
                break;
            case ConstraintKind.HasFlags:
                sb.Append("        if ((").Append(fieldName).Append(" & ").Append(FormatConst(c.HasFlagsMask, f.Type))
                  .Append(") != ").Append(FormatConst(c.HasFlagsMask, f.Type)).AppendLine(") return false;");
                break;
            case ConstraintKind.Range:
                sb.Append("        if (").Append(fieldName).Append(" < ").Append(FormatConst(c.RangeMin, f.Type))
                  .Append(" || ").Append(fieldName).Append(" > ").Append(FormatConst(c.RangeMax, f.Type)).AppendLine(") return false;");
                break;
            case ConstraintKind.NonDefault:
                sb.Append("        if (").Append(fieldName).Append(" == ").Append(DefaultLiteral(f.Type)).AppendLine(") return false;");
                break;
        }
    }

    private static string FormatConst(object? v, ITypeSymbol type)
    {
        if (v is null) return "default";
        string s = v switch
        {
            uint => v.ToString() + "u",
            ulong => v.ToString() + "ul",
            long => v.ToString() + "L",
            int => v.ToString() ?? "0",
            ushort => "(ushort)" + v.ToString(),
            short => "(short)" + v.ToString(),
            byte => "(byte)" + v.ToString(),
            sbyte => "(sbyte)" + v.ToString(),
            _ => v.ToString() ?? "default"
        };
        return s.Length == 0 ? "default" : s;
    }

    private static string DefaultLiteral(ITypeSymbol type) => type.SpecialType switch
    {
        SpecialType.System_UInt32 or SpecialType.System_UInt16 or SpecialType.System_UInt64
        or SpecialType.System_Int32 or SpecialType.System_Int16 or SpecialType.System_Int64
        or SpecialType.System_Byte or SpecialType.System_SByte => "0",
        _ => "default"
    };

    // ── Write ──

    /// <summary>
    /// 构造单字段写入行。值表达式缺省 = <c>value.{FieldName}</c>（写入参）；
    /// 显式传入（ValidEquals 常量）用于 validate 防御性补全（不信任入参）。
    /// </summary>
    private static void EmitWriteLine(System.Text.StringBuilder sb, FieldInfo f, string? valueExpr)
    {
        var v = valueExpr ?? "value." + f.Name;
        switch (f.Type.SpecialType)
        {
            case SpecialType.System_UInt32:
                sb.Append("        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(").Append(f.Offset).Append(", 4), ").Append(v).AppendLine(");");
                break;
            case SpecialType.System_UInt16:
                sb.Append("        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(").Append(f.Offset).Append(", 2), ").Append(v).AppendLine(");");
                break;
            case SpecialType.System_UInt64:
                sb.Append("        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(dest.Slice(").Append(f.Offset).Append(", 8), ").Append(v).AppendLine(");");
                break;
            case SpecialType.System_Int64:
                sb.Append("        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(dest.Slice(").Append(f.Offset).Append(", 8), ").Append(v).AppendLine(");");
                break;
            case SpecialType.System_Int32:
                sb.Append("        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dest.Slice(").Append(f.Offset).Append(", 4), ").Append(v).AppendLine(");");
                break;
            case SpecialType.System_Byte:
                sb.Append("        dest[").Append(f.Offset).Append("] = ").Append(v).AppendLine(";");
                break;
            default:
                // ★ Spec 27: 嵌套 [BinaryLayout] struct（如 SegmentAddress）——委托给被嵌套 struct 的 Codec.Write
                //   （小端逐字段，符合字节序铁律；禁 MemoryMarshal——平台字节序违反 §1.2）。
                if (IsNestedStruct(f.Type))
                {
                    sb.Append("        ").Append(f.Type.ToDisplayString()).Append("Codec.Write(dest.Slice(").Append(f.Offset).Append(", ").Append(FieldSizeStr(f)).Append("), in ").Append(v).AppendLine(");");
                    break;
                }
                EmitWriteNonPrimitive(sb, f, v);
                break;
        }
    }

    private static void EmitWriteNonPrimitive(System.Text.StringBuilder sb, FieldInfo f, string v)
    {
        var t = f.Type;
        if (t.TypeKind == TypeKind.Enum && t is INamedTypeSymbol e && e.EnumUnderlyingType is { } under)
        {
            switch (under.SpecialType)
            {
                case SpecialType.System_UInt32:
                    sb.Append("        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(").Append(f.Offset).Append(", 4), (uint)").Append(v).AppendLine(");");
                    return;
                case SpecialType.System_UInt16:
                    sb.Append("        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(").Append(f.Offset).Append(", 2), (ushort)").Append(v).AppendLine(");");
                    return;
                case SpecialType.System_UInt64:
                    sb.Append("        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(dest.Slice(").Append(f.Offset).Append(", 8), (ulong)").Append(v).AppendLine(");");
                    return;
                case SpecialType.System_Int64:
                    sb.Append("        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(dest.Slice(").Append(f.Offset).Append(", 8), (long)").Append(v).AppendLine(");");
                    return;
                case SpecialType.System_Byte:
                    sb.Append("        dest[").Append(f.Offset).Append("] = (byte)").Append(v).AppendLine(";");
                    return;
                default:
                    sb.Append("        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dest.Slice(").Append(f.Offset).Append(", 4), (int)").Append(v).AppendLine(");");
                    return;
            }
        }
        sb.Append("        // unsupported field type: ").Append(t.ToDisplayString()).Append(' ').Append(f.Name).AppendLine();
    }

    // ── Read ──

    private static void EmitReadLine(System.Text.StringBuilder sb, FieldInfo f, bool isLast)
    {
        var comma = isLast ? "" : ",";
        switch (f.Type.SpecialType)
        {
            case SpecialType.System_UInt32:
                sb.Append("            ").Append(f.Name).Append(" = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(").Append(f.Offset).Append(", 4))").AppendLine(comma);
                break;
            case SpecialType.System_UInt16:
                sb.Append("            ").Append(f.Name).Append(" = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(").Append(f.Offset).Append(", 2))").AppendLine(comma);
                break;
            case SpecialType.System_UInt64:
                sb.Append("            ").Append(f.Name).Append(" = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(").Append(f.Offset).Append(", 8))").AppendLine(comma);
                break;
            case SpecialType.System_Int64:
                sb.Append("            ").Append(f.Name).Append(" = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(source.Slice(").Append(f.Offset).Append(", 8))").AppendLine(comma);
                break;
            case SpecialType.System_Int32:
                sb.Append("            ").Append(f.Name).Append(" = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(source.Slice(").Append(f.Offset).Append(", 4))").AppendLine(comma);
                break;
            case SpecialType.System_Byte:
                sb.Append("            ").Append(f.Name).Append(" = source[").Append(f.Offset).Append(']').AppendLine(comma);
                break;
            default:
                // ★ Spec 27: 嵌套 [BinaryLayout] struct（如 SegmentAddress）——委托给被嵌套 struct 的 Codec.Read
                //   （小端逐字段，符合字节序铁律；禁 MemoryMarshal——平台字节序违反 §1.2）。
                if (IsNestedStruct(f.Type))
                {
                    sb.Append("            ").Append(f.Name).Append(" = ").Append(f.Type.ToDisplayString()).Append("Codec.Read(source.Slice(").Append(f.Offset).Append(", ").Append(FieldSizeStr(f)).AppendLine("))").Append(comma);
                    break;
                }
                EmitReadNonPrimitive(sb, f, comma);
                break;
        }
    }

    private static void EmitReadNonPrimitive(System.Text.StringBuilder sb, FieldInfo f, string comma)
    {
        var t = f.Type;
        if (t.TypeKind == TypeKind.Enum && t is INamedTypeSymbol e && e.EnumUnderlyingType is { } under)
        {
            var cast = "(" + t.ToDisplayString() + ")";
            switch (under.SpecialType)
            {
                case SpecialType.System_UInt32:
                    sb.Append("            ").Append(f.Name).Append(" = ").Append(cast).Append("System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(").Append(f.Offset).Append(", 4))").AppendLine(comma);
                    return;
                case SpecialType.System_UInt16:
                    sb.Append("            ").Append(f.Name).Append(" = ").Append(cast).Append("System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(").Append(f.Offset).Append(", 2))").AppendLine(comma);
                    return;
                case SpecialType.System_UInt64:
                    sb.Append("            ").Append(f.Name).Append(" = ").Append(cast).Append("System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(").Append(f.Offset).Append(", 8))").AppendLine(comma);
                    return;
                case SpecialType.System_Int64:
                    sb.Append("            ").Append(f.Name).Append(" = ").Append(cast).Append("System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(source.Slice(").Append(f.Offset).Append(", 8))").AppendLine(comma);
                    return;
                case SpecialType.System_Byte:
                    sb.Append("            ").Append(f.Name).Append(" = ").Append(cast).Append("source[").Append(f.Offset).Append(']').AppendLine(comma);
                    return;
                default:
                    sb.Append("            ").Append(f.Name).Append(" = ").Append(cast).Append("System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(source.Slice(").Append(f.Offset).Append(", 4))").AppendLine(comma);
                    return;
            }
        }
        sb.Append("            // unsupported field type: ").Append(t.ToDisplayString()).Append(' ').Append(f.Name).AppendLine();
    }

    // ── OrFlags / IsEmpty ──

    private static void EmitOrFlagsMethod(System.Text.StringBuilder sb, string structName, FieldInfo f)
    {
        sb.Append("    /// <summary>原地 OR 设置 ").Append(structName).Append('.').Append(f.Name)
          .Append("（偏移 ").Append(f.Offset).AppendLine("）。Seal 用。</summary>");
        sb.Append("    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]").AppendLine();
        sb.Append("    public static void Or").Append(f.Name).AppendLine("(System.Span<byte> dest, ushort flagsToSet)");
        sb.AppendLine("    {");
        sb.Append("        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(dest.Slice(")
          .Append(f.Offset).Append(", 2), (ushort)(System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(dest.Slice(")
          .Append(f.Offset).Append(", 2)) | flagsToSet));").AppendLine();
        sb.AppendLine("    }");
        sb.AppendLine();
    }

    private static void EmitIsEmptyMethod(System.Text.StringBuilder sb, string structName, FieldInfo f)
    {
        sb.Append("    /// <summary>判断 ").Append(structName).Append('.').Append(f.Name)
          .Append(" == 0（偏移 ").Append(f.Offset).AppendLine("）——空位 record。</summary>");
        sb.Append("    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]").AppendLine();
        sb.Append("    public static bool IsEmpty").Append(f.Name).AppendLine("(System.ReadOnlySpan<byte> source)");
        sb.AppendLine("    {");
        sb.Append("        return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(")
          .Append(f.Offset).Append(", 4)) == 0;").AppendLine();
        sb.AppendLine("    }");
        sb.AppendLine();
    }

    // ── 数据载体 ──

    private sealed record LayoutInfo(
        string Namespace,
        string StructName,
        System.Collections.Generic.List<FieldInfo> Fields,
        int SizeConstValue,
        string? OrFlagsField,
        string? IsEmptyField,
        int FeatureFlags,
        Diagnostic? Diagnostic,
        bool IsReadOnly,
        bool CanConstruct,
        string ContainingType,
        bool IsPublic,
        Microsoft.CodeAnalysis.Location? Location)
    {
        /// <summary>★ struct 全名（namespace.ContainingType.Name），嵌套大小表的 key。</summary>
        public string FullName => (string.IsNullOrEmpty(Namespace) ? "" : Namespace + ".") + ContainingType + StructName;
    }

    private sealed record FieldInfo(string Name, ITypeSymbol Type, int Offset, Constraint? Constraint)
    {
        /// <summary>
        /// ★ 字段大小（字节数）。收集阶段对基元/enum 算好；嵌套 struct 留 null，
        /// 生成阶段（有收集表）查表填上。null 表示尚未解析（查表阶段处理）。
        /// </summary>
        public int? ResolvedSize { get; set; }
    }

    private sealed record Constraint(ConstraintKind Kind, object? EqExpected, object? RangeMin, object? RangeMax, object? HasFlagsMask, bool NonDefault);

    private enum ConstraintKind { Equals, HasFlags, Range, NonDefault }

    /// <summary>★ 缺陷 11：Codec 生成 hintName 唯一化（命名空间 + 含类型前缀，. → _）——跨命名空间
    /// 同名 struct 不再触发 AddSource 同名注册（CS8785——生成器整体失效）。</summary>
    private static string HintName(LayoutInfo layout)
        => (string.IsNullOrEmpty(layout.Namespace) ? "" : layout.Namespace.Replace('.', '_') + "_")
           + layout.ContainingType.Replace('.', '_') + layout.StructName + "Codec.g.cs";
}
