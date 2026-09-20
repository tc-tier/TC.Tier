using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TC.Tier.CodeGen.Templating;

namespace TC.Tier.CodeGen;

/// <summary>
/// 命令定义源生成器（#435——CLI 解析器 ＋ HttpApi 执行代理，命令模型标注一次两端生成）。
/// <para>★ <c>[CommandGroup]</c> 类（可嵌套任意深度）内 <c>[Command]</c> 方法 = 叶子命令；
///   每根组生成 <c>&lt;RootClass&gt;Cli.g.cs</c>（Run/RunAsync/WriteHelp/CompleteNext/CommandPaths）
///   与 <c>&lt;RootClass&gt;Http.g.cs</c>（TryHandleAsync 单一入口＋编译期路由表）文件对——
///   多根组互不知晓，聚合归宿主（首 token switch / 首段路由分发）。</para>
/// <para>★ 诊断（TCSG054-061）：路径冲突/非法命令名/参数未标注/不可绑定形态/GET 带 body/标注宿主非法/参数名占用保留前缀/嵌套组未链接线且不可实例化。</para>
/// </summary>
[Generator]
public sealed partial class CommandGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor PathConflictRule = new(
        id: "TCSG054",
        title: "命令路径冲突",
        messageFormat: "{0}",
        category: "CodeGeneration",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor IllegalNameRule = new(
        id: "TCSG055",
        title: "非法命令名",
        messageFormat: "{0}",
        category: "CodeGeneration",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnannotatedParamRule = new(
        id: "TCSG056",
        title: "命令参数未标注",
        messageFormat: "命令 '{0}' 的参数 '{1}' 未标注 [CommandArg]/[CommandOption]/[CommandBody] 之一（CancellationToken 框架注入除外）",
        category: "CodeGeneration",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnbindableParamRule = new(
        id: "TCSG057",
        title: "不可绑定的参数形态",
        messageFormat: "命令 '{0}' 的参数 '{1}' 为不可绑定形态（ref/out/in、指针、ref struct）",
        category: "CodeGeneration",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor GetWithBodyRule = new(
        id: "TCSG058",
        title: "GET 命令带 body",
        messageFormat: "命令 '{0}' 为 GET 却带 [CommandBody] 参数 '{1}'——GET 参数只取路由段＋query",
        category: "CodeGeneration",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor IllegalAttributeHostRule = new(
        id: "TCSG059",
        title: "命令标注宿主非法",
        messageFormat: "{0}",
        category: "CodeGeneration",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ReservedParamNameRule = new(
        id: "TCSG060",
        title: "参数名占用生成器保留前缀",
        messageFormat: "命令 '{0}' 的参数 '{1}' 以 '__tcsg_' 开头——生成代码自有标识符（形参＋局部变量）的保留命名空间，撞名即生成物编译失败，请改名",
        category: "CodeGeneration",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor GroupUnlinkedRule = new(
        id: "TCSG061",
        title: "嵌套组未链接线且不可实例化",
        messageFormat: "{0}",
        category: "CodeGeneration",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>生成器初始化：组标注 → 组模型；游离 [Command]（非组类型宿主）→ TCSG059。</summary>
    /// <param name="context">增量生成器初始化上下文。</param>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var groups = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                "TC.Tier.CodeGen.CommandGroupAttribute",
                static (node, _) => node is TypeDeclarationSyntax,
                static (ctx, _) => ExtractGroup(ctx))
            .SelectMany(static (g, _) => g)
            .Collect();

        var strayCommands = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                "TC.Tier.CodeGen.CommandAttribute",
                static (node, _) => node is MethodDeclarationSyntax,
                static (ctx, _) => ExtractStrayCommands(ctx))
            .SelectMany(static (g, _) => g)
            .Collect();

        context.RegisterSourceOutput(groups.Combine(strayCommands), static (spc, pair) =>
            Emit(spc, pair.Left, pair.Right));
    }

    /// <summary>游离 [Command] 提取——宿主类型非 [CommandGroup] 时产出诊断载荷（正常命令不产出）。</summary>
    private static ImmutableArray<(string Method, string Host, Location Loc)> ExtractStrayCommands(
        GeneratorAttributeSyntaxContext ctx)
    {
        var builder = ImmutableArray.CreateBuilder<(string, string, Location)>();
        foreach (var attribute in ctx.Attributes)
        {
            if (ctx.TargetSymbol is not IMethodSymbol method) continue;
            if (method.ContainingType is not { } host || CommandModel.HasGroupAttribute(host)) continue;
            builder.Add((method.Name, host.ToDisplayString(),
                attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? method.Locations[0]));
        }

        return builder.ToImmutable();
    }

    /// <summary>组提取（纯符号变换——保增量缓存）：组名/描述/根判定/命令集/服务获取表达式。</summary>
    private static ImmutableArray<CommandModel.GroupModel> ExtractGroup(GeneratorAttributeSyntaxContext ctx)
    {
        var builder = ImmutableArray.CreateBuilder<CommandModel.GroupModel>();
        foreach (var attribute in ctx.Attributes)
        {
            if (ctx.TargetSymbol is not INamedTypeSymbol type) continue;
            if (type.TypeKind != TypeKind.Class && type.TypeKind != TypeKind.Struct) continue;
            if (type.IsGenericType || type.Arity > 0) continue;   // 泛型组宿主不生成（开放泛型无消费面）

            var isRoot = true;
            for (var container = type.ContainingType; container is not null; container = container.ContainingType)
            {
                if (CommandModel.HasGroupAttribute(container)) isRoot = false;
            }

            var root = type;
            for (var container = type.ContainingType; container is not null; container = container.ContainingType)
            {
                if (CommandModel.HasGroupAttribute(container)) root = container;
            }

            var commands = ImmutableArray.CreateBuilder<CommandModel.CmdModel>();
            foreach (var member in type.GetMembers())
            {
                if (member is not IMethodSymbol method || method.MethodKind != MethodKind.Ordinary) continue;
                var cmdAttr = method.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "CommandAttribute");
                if (cmdAttr is null) continue;
                if (cmdAttr.ConstructorArguments.Length != 1 || cmdAttr.ConstructorArguments[0].Value is not string cmdName)
                    continue;

                commands.Add(ExtractCommand(method, cmdAttr, cmdName, root));
            }

            var (acquireExpr, memberLinked) = CommandModel.Acquire(type, root);
            builder.Add(new CommandModel.GroupModel(
                type.ToDisplayString(),
                CommandModel.GroupName(type, attribute),
                CommandModel.GroupDescription(type, attribute),
                isRoot,
                type.ContainingType?.ToDisplayString() ?? string.Empty,
                acquireExpr,
                memberLinked,
                memberLinked || CommandModel.HasAccessibleParameterlessCtor(type),
                commands.ToImmutable(),
                attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation()
                    ?? type.Locations[0]));
        }

        return builder.ToImmutable();
    }

    /// <summary>叶子命令提取——签名契约分类（返回形态/参数绑定/异常映射）。</summary>
    private static CommandModel.CmdModel ExtractCommand(
        IMethodSymbol method, AttributeData attr, string name, INamedTypeSymbol root)
    {
        var description = CommandModel.NamedString(attr, "Description") ?? string.Empty;
        var route = CommandModel.NamedString(attr, "Route") ?? string.Empty;
        var httpMethod = (CommandModel.NamedString(attr, "Method") ?? "POST").ToUpperInvariant();

        var parameters = ImmutableArray.CreateBuilder<CommandModel.ParamModel>();
        foreach (var parameter in method.Parameters)
        {
            var argAttr = parameter.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "CommandArgAttribute");
            var optAttr = parameter.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "CommandOptionAttribute");
            var bodyAttr = parameter.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "CommandBodyAttribute");

            // CancellationToken——框架注入特判（任意参数位识别，保留调用序、不参与绑定/校验）
            if (argAttr is null && optAttr is null && bodyAttr is null
                && CommandModel.IsCancellationToken(parameter.Type))
            {
                var ctDisplay = parameter.Type.ToDisplayString();
                parameters.Add(new CommandModel.ParamModel(parameter.Name, CommandModel.ParamRole.Arg,
                    CommandModel.BindKind.CancellationToken, CommandModel.BindKind.CancellationToken,
                    ctDisplay, ctDisplay, string.Empty, -1, string.Empty, default,
                    false, string.Empty, true, string.Empty, parameter.Locations[0]));
                continue;
            }

            // 057：不可绑定形态（ref/out/in、指针、ref struct）——占位保序，Emit 期报
            var refKind = parameter.RefKind;
            var type = parameter.Type;
            var unbindable = string.Empty;
            if (refKind != RefKind.None) unbindable = refKind.ToString();
            else if (type is Microsoft.CodeAnalysis.IPointerTypeSymbol or Microsoft.CodeAnalysis.IFunctionPointerTypeSymbol) unbindable = "pointer";
            else if (type.IsRefLikeType) unbindable = "ref struct";

            var role = argAttr is not null ? CommandModel.ParamRole.Arg
                : optAttr is not null ? CommandModel.ParamRole.Option
                : bodyAttr is not null ? CommandModel.ParamRole.Body
                : (CommandModel.ParamRole?)null;
            if (role is null)
            {
                // 未标注——TCSG056 在 Emit 期报（这里用未标注占位保序）
                var unannotatedDisplay = type.ToDisplayString();
                parameters.Add(new CommandModel.ParamModel(parameter.Name, CommandModel.ParamRole.Arg,
                    CommandModel.BindKind.String, CommandModel.BindKind.String,
                    unannotatedDisplay, unannotatedDisplay, string.Empty, -1, CommandModel.Kebab(parameter.Name),
                    default, parameter.HasExplicitDefaultValue, CommandModel.DefaultExpr(parameter),
                    false, unbindable, parameter.Locations[0]));
                continue;
            }

            var position = -1;
            if (role == CommandModel.ParamRole.Arg && argAttr!.ConstructorArguments.Length == 1
                && argAttr.ConstructorArguments[0].Value is int p) position = p;

            var longName = role == CommandModel.ParamRole.Option
                ? CommandModel.NamedString(optAttr!, "LongName") ?? CommandModel.Kebab(parameter.Name)
                : string.Empty;
            var shortName = default(char);
            if (role == CommandModel.ParamRole.Option)
            {
                foreach (var named in optAttr!.NamedArguments)
                {
                    if (named.Key == "ShortName" && named.Value.Value is char c) shortName = c;
                }
            }

            var (kind, inner, enumFqn) = role == CommandModel.ParamRole.Body
                ? (CommandModel.BindKind.Body, CommandModel.BindKind.Body, string.Empty)
                : CommandModel.ClassifyParam(type);

            // typeof 查表用显示名——可空引用类型剥尾缀 '?'（typeof(T?) 非法 CS8639），
            // Nullable<T> 值类型包裹保留（typeof(T?) 合法且查表类型不同）
            var display = type.ToDisplayString();
            var typeOfDisplay = display;
            if (type is INamedTypeSymbol namedType
                && namedType.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T
                && display.EndsWith("?", StringComparison.Ordinal))
            {
                typeOfDisplay = display.Substring(0, display.Length - 1);
            }

            parameters.Add(new CommandModel.ParamModel(parameter.Name, role.Value, kind, inner,
                display, typeOfDisplay, enumFqn, position, longName, shortName,
                parameter.HasExplicitDefaultValue, CommandModel.DefaultExpr(parameter),
                true, unbindable, parameter.Locations[0]));
        }

        // 异常映射：方法级就近覆写组级（同类型去重——方法级优先）
        var errors = ImmutableArray.CreateBuilder<CommandModel.ErrorMap>();
        var illegalErrors = ImmutableArray.CreateBuilder<string>();
        CollectErrors(method.ContainingType, errors, illegalErrors);
        CollectErrors(method, errors, illegalErrors);
        var deduped = errors
            .GroupBy(e => e.ExceptionFqn)
            .Select(g => g.Last())
            .OrderByDescending(e => e.InheritanceDepth)
            .ToImmutableArray();   // 最派生优先

        var ret = CommandModel.ClassifyReturn(method);
        var resultType = ret is CommandModel.ReturnKind.TaskOfT or CommandModel.ReturnKind.ValueTaskOfT
            ? ((INamedTypeSymbol)method.ReturnType).TypeArguments[0].ToDisplayString()
            : ret == CommandModel.ReturnKind.Sync ? method.ReturnType.ToDisplayString() : string.Empty;
        var resultSimple = ret != CommandModel.ReturnKind.Sync || IsSimpleResultType(method.ReturnType);

        return new CommandModel.CmdModel(
            method.Name, name, description, route, httpMethod,
            method.IsStatic,
            method.ContainingType.ToDisplayString(),
            CommandModel.AcquireExpr(method.ContainingType, root),
            ret, resultType, parameters.ToImmutable(), deduped, resultSimple,
            illegalErrors.ToImmutable(), method.Locations[0]);
    }

    private static void CollectErrors(ISymbol symbol, ImmutableArray<CommandModel.ErrorMap>.Builder into,
        ImmutableArray<string>.Builder illegal)
    {
        foreach (var attr in symbol.GetAttributes())
        {
            if (attr.AttributeClass?.Name != "CommandErrorAttribute") continue;
            if (attr.ConstructorArguments.Length != 2
                || attr.ConstructorArguments[0].Value is not INamedTypeSymbol ex
                || attr.ConstructorArguments[1].Value is not int status) continue;

            var isException = false;
            for (var t = (ITypeSymbol)ex; t is not null; t = t.BaseType)
            {
                if (t.ToDisplayString() == "System.Exception")
                {
                    isException = true;
                    break;
                }
            }

            if (!isException)
            {
                illegal.Add(ex.ToDisplayString());
                continue;
            }

            var depth = 0;
            for (var t = (ITypeSymbol)ex; t is not null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
                depth++;
            into.Add(new CommandModel.ErrorMap(ex.ToDisplayString(), status, depth));
        }
    }

    /// <summary>同步可直写结果类型（string/基元/枚举/Nullable 包裹——stdout.WriteLine 形态）。</summary>
    private static bool IsSimpleResultType(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol named
            && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
            && named.TypeArguments.Length == 1)
            return IsSimpleResultType(named.TypeArguments[0]);
        if (type.SpecialType != SpecialType.None) return true;
        return type.TypeKind == TypeKind.Enum;
    }

    // ══════════ 装配与发射 ══════════

    private static void Emit(
        SourceProductionContext spc,
        ImmutableArray<CommandModel.GroupModel> groups,
        ImmutableArray<(string Method, string Host, Location Loc)> strayCommands)
    {
        foreach (var (method, host, loc) in strayCommands)
        {
            spc.ReportDiagnostic(Diagnostic.Create(IllegalAttributeHostRule, loc,
                $"[Command] 方法 '{method}' 挂在非 [CommandGroup] 类型 '{host}'——命令方法必须声明在命令组内"));
        }

        if (groups.IsEmpty) return;

        // TCSG059：泛型/重复宿主等在提取期已过滤；此处做组表与逐组校验
        var byFqn = new Dictionary<string, CommandModel.GroupModel>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            if (byFqn.TryGetValue(group.Fqn, out var duplicate))
            {
                spc.ReportDiagnostic(Diagnostic.Create(IllegalAttributeHostRule, group.Loc,
                    $"命令组 '{group.Fqn}' 重复标注（partial 多片多处 [CommandGroup]）"));
            }
            else
            {
                byFqn[group.Fqn] = group;
            }
        }

        foreach (var group in groups) ValidateGroup(spc, group);
        ValidateGlobal(spc, groups);

        foreach (var root in groups.Where(g => g.IsRoot))
        {
            if (byFqn[root.Fqn] != root) continue;
            EmitRoot(spc, root, byFqn);
            EmitHttpRoot(spc, root, byFqn);
            EmitJsonRoot(spc, root);
        }
    }

    // 装配与发射（树装配在 CommandEmitCli.BuildNode——两产物共用）

    private static void ValidateGroup(SourceProductionContext spc, CommandModel.GroupModel group)
    {
        // 061：嵌套组全链无同型成员且无参构造不可达——获取表达式已落占位，组声明处给修法
        if (!group.IsRoot && !group.MemberLinked && !group.FallbackInstantiable)
        {
            spc.ReportDiagnostic(Diagnostic.Create(GroupUnlinkedRule, group.Loc,
                $"命令组 '{group.Fqn}' 未通过父属性链接线且无可访问无参构造——生成物无法获取组实例。" +
                "修法：在任一祖辈组（根组在根类）声明该组类型的 public 属性/字段并接线" +
                "（如 `public KeyGroup Key { get; } = new(services);`），或为组提供可访问无参构造"));
        }

        foreach (var command in group.Commands)
        {
            // 055：非法命令名
            if (command.Name.Length == 0 || command.Name.Contains('/'))
            {
                spc.ReportDiagnostic(Diagnostic.Create(IllegalNameRule, command.Loc,
                    $"命令名 '{command.Name}' 非法（空名或含路径分隔符 '/'）——方法 {group.Fqn}.{command.MethodName}"));
            }

            // 054：组内子命令重名 / 组名与子命令名冲突
            // 056/057/058/059：参数面
            var positions = new HashSet<int>();
            var bodyCount = 0;
            foreach (var parameter in command.Params)
            {
                if (parameter.Position >= 0 && !positions.Add(parameter.Position))
                {
                    spc.ReportDiagnostic(Diagnostic.Create(PathConflictRule, parameter.Loc,
                        $"命令 '{group.Name} {command.Name}' 位置参数 Position={parameter.Position} 重复"));
                }

                if (parameter.Role == CommandModel.ParamRole.Body)
                {
                    bodyCount++;
                    if (command.Method == "GET")
                    {
                        spc.ReportDiagnostic(Diagnostic.Create(GetWithBodyRule, parameter.Loc,
                            command.Name, parameter.Name));
                    }
                }
            }

            if (bodyCount > 1)
            {
                spc.ReportDiagnostic(Diagnostic.Create(IllegalAttributeHostRule, command.Loc,
                    $"命令 '{group.Name} {command.Name}' 有 {bodyCount} 个 [CommandBody] 参数——一个命令最多一个请求体"));
            }

            // 059：CommandError 非 Exception 派生
            foreach (var illegal in command.IllegalErrorFqns)
            {
                spc.ReportDiagnostic(Diagnostic.Create(IllegalAttributeHostRule, command.Loc,
                    $"命令 '{group.Name} {command.Name}' 的 [CommandError] 异常类型 '{illegal}' 未派生自 System.Exception"));
            }

            // 056/057：未标注 / 不可绑定（CT 参数 Annotated=true 跳过）；060：保留前缀占用
            foreach (var parameter in command.Params)
            {
                if (parameter.Kind == CommandModel.BindKind.CancellationToken) continue;
                if (!parameter.Annotated)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(UnannotatedParamRule, parameter.Loc,
                        command.Name, parameter.Name));
                }
                else if (parameter.Unbindable.Length > 0)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(UnbindableParamRule, parameter.Loc,
                        command.Name, parameter.Name));
                }

                if (parameter.Name.StartsWith(CommandModel.ReservedLocalPrefix, StringComparison.Ordinal))
                {
                    spc.ReportDiagnostic(Diagnostic.Create(ReservedParamNameRule, parameter.Loc,
                        command.Name, parameter.Name));
                }
            }
        }
    }

    private static void ValidateGlobal(SourceProductionContext spc, ImmutableArray<CommandModel.GroupModel> groups)
    {
        // 同父下组名/命令名冲突（含组名 vs 命令名跨类冲突——同一命名空间树）
        var byParent = groups.GroupBy(g => g.ParentFqn);
        foreach (var parentGroup in byParent)
        {
            var names = new Dictionary<string, CommandModel.GroupModel>(StringComparer.Ordinal);
            foreach (var group in parentGroup)
            {
                if (names.TryGetValue(group.Name, out var existingGroup))
                {
                    spc.ReportDiagnostic(Diagnostic.Create(PathConflictRule, group.Loc,
                        $"组名 '{group.Name}' 在 '{parentGroup.Key}' 下重复（{existingGroup.Fqn} vs {group.Fqn}）"));
                }
            }
        }

        foreach (var group in groups)
        {
            var cmdNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var command in group.Commands)
            {
                if (!cmdNames.Add(command.Name))
                {
                    spc.ReportDiagnostic(Diagnostic.Create(PathConflictRule, command.Loc,
                        $"组 '{group.Fqn}' 内子命令 '{command.Name}' 重名"));
                }
            }
        }

        // 路由覆写相撞（自定义 Route 精确相等）
        var routes = new Dictionary<string, CommandModel.CmdModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            foreach (var command in group.Commands)
            {
                if (command.Route.Length == 0) continue;
                if (routes.TryGetValue(command.Route, out var existing))
                {
                    spc.ReportDiagnostic(Diagnostic.Create(PathConflictRule, command.Loc,
                        $"路由 '{command.Route}' 覆写相撞（{existing.ContainerFqn}.{existing.MethodName} vs {command.ContainerFqn}.{command.MethodName}）"));
                }
                else
                {
                    routes[command.Route] = command;
                }
            }
        }
    }
}
