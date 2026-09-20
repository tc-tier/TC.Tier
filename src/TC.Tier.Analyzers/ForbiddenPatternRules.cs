using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace TC.Tier.Analyzers;

/// <summary>
/// 禁用模式规则族分析器——<c>tier_forbidden.*</c> 键配置驱动（件二，#439）：
/// <para>★ TCSG136 禁止运行时反射（pack=reflection）——反射破坏 AOT/裁剪/性能，白盒访问走 InternalsVisibleTo。</para>
/// <para>★ TCSG137 禁 sync-over-async（pack=sync_over_async）——同步阻塞后台 Task 会死锁（同步上下文）+ 线程池耗尽。</para>
/// <para>★ TCSG138 禁 fire-and-forget 丢弃（pack=fire_and_forget）——异常未观测/无背压/生命周期失控，走 TaskSink。</para>
/// <para>★ TCSG134 裸线程原语（pack=bare_threads）：new Thread / Thread.Sleep / Task.Run /
///   new PeriodicTimer / SemaphoreSlim / Mutex / ManualResetEvent(Slim)——语义确认构造/接收者
///   类型防同名误伤；豁免 = tier_forbidden.exempt.bare_threads FQN 前缀（实现组件本体豁免，
///   对齐 #pragma 受控豁免语义）；lock 语句不报（合法惯用）。</para>
/// <para>★ TCSG135 热路径分配纪律（pack=hotpath_discipline）：作用域 = members_with([FQN]) 标注
///   方法；违禁三类 = LINQ（System.Linq 语义确认）/ 装箱（Operation 面 Conversion.IsBoxing）/
///   string 分配（new string / 内插 / Format/Concat/Join / 值类型 ToString——启发式契约，
///   漏报可能、不承诺完备）。</para>
/// <para>★ TCSG139 配置非法：未知键、坏 pack 令牌、作用域/规则依赖缺失——fail-fast。</para>
/// <para>零默认诊断：pack 未声明 = 零报告。</para>
/// </summary>
/// <remarks>注册宿主 = <see cref="TierGovernanceAnalyzer"/>（包内单分析器类，诊断 ID 不跨类重复）。</remarks>
internal static class ForbiddenPatternRules
{
    private const string Category = "TierCode";
    private const string KeyNamespace = "tier_forbidden.";

    internal const string KeyPack = "tier_forbidden.pack";
    internal const string KeyScopeHotPath = "tier_forbidden.scope.hotpath_discipline";
    internal const string KeyExemptBareThreads = "tier_forbidden.exempt.bare_threads";
    internal const string KeyAdditionalBareThreads = "tier_forbidden.bare_threads.additional";

    private static readonly HashSet<string> KnownKeys = new(StringComparer.Ordinal)
    {
        KeyPack, KeyScopeHotPath, KeyExemptBareThreads, KeyAdditionalBareThreads,
    };

    private static readonly HashSet<string> KnownPacks = new(StringComparer.Ordinal)
    {
        "reflection", "sync_over_async", "fire_and_forget", "bare_threads", "hotpath_discipline",
    };

    private static readonly DiagnosticDescriptor BareThreadsRule = new(
        id: "TCSG134",
        title: "裸线程原语（tier_forbidden.pack=bare_threads）",
        messageFormat: "禁止裸线程原语 '{0}'——后台循环一律 BackgroundWorkerLoop、受控后台任务一律 TaskSink；实现组件本体走 exempt 前缀声明豁免。",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor HotPathDisciplineRule = new(
        id: "TCSG135",
        title: "热路径分配纪律（tier_forbidden.pack=hotpath_discipline）",
        messageFormat: "热路径方法禁止 {0}（启发式契约：漏报可能，不承诺完备）。",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor NoReflectionRule = new(
        id: "TCSG136",
        title: "禁止运行时反射（tier_forbidden.pack=reflection）",
        messageFormat: "禁止反射调用 '{0}'——反射破坏 AOT/裁剪/性能（白盒走 InternalsVisibleTo）。",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor NoSyncOverAsyncRule = new(
        id: "TCSG137",
        title: "禁止同步强制等待异步（tier_forbidden.pack=sync_over_async）",
        messageFormat: "禁止 '{0}'——同步阻塞后台 Task 会死锁（同步上下文）+ 线程池耗尽风险；一律 await 后台句柄（WaitAsync）。",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor NoFireAndForgetData = new(
        id: "TCSG138",
        title: "禁止丢弃 Task/ValueTask（tier_forbidden.pack=fire_and_forget）",
        messageFormat: "禁止丢弃 '{0}'（fire-and-forget）——异常未观测（静默吞）、无背压（队列堆积=延迟泄漏）、生命周期失控；ValueTask 更危险（池化 token 不归还）。走 TaskSink.Submit/SubmitFast 或显式观测；治理内核受控丢弃用 #pragma 豁免并注明理由。",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <summary>反射调用面（方法名）——接收者经语义确认是反射类型才报（防业务同名方法误伤）。</summary>
    private static readonly HashSet<string> ReflectionMembers = new(StringComparer.Ordinal)
    {
        "GetMethod", "GetField", "GetProperty", "GetConstructor", "GetEvent", "GetNestedType",
        "GetTypeInfo", "GetMethods", "GetFields", "GetProperties", "GetConstructors", "GetInterfaces",
        "CreateInstance", "Load", "LoadFrom", "LoadFile", "Invoke", "GetValue", "SetValue", "GetGenericArguments",
    };

    /// <summary>裸线程构造类型清单（System.Threading 命名空间）——语义确认构造类型才报。
    /// 消费方追加类型走 <see cref="KeyAdditionalBareThreads"/> 配置（精确全名、add-only），不改包。</summary>
    private static readonly HashSet<string> BareThreadConstructorNames = new(StringComparer.Ordinal)
    {
        "Thread", "PeriodicTimer", "Timer", "SemaphoreSlim", "Mutex", "ManualResetEvent", "ManualResetEventSlim",
    };

    private sealed class ForbiddenConfig
    {
        public bool Reflection;
        public bool SyncOverAsync;
        public bool FireAndForget;
        public bool BareThreads;
        public bool HotPathDiscipline;
        public string? HotPathAttributeFqn;
        public readonly List<string> BareThreadExemptPrefixes = [];
        public readonly List<string> BareThreadAdditionalNames = [];

        public bool HasAny =>
            Reflection || SyncOverAsync || FireAndForget || BareThreads || HotPathDiscipline;
    }

    /// <summary>本规则族全部诊断（配置非法 TCSG139 由 TierConfigDiagnostics 统一持有）。</summary>
    internal static readonly ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =
        ImmutableArray.Create(BareThreadsRule, HotPathDisciplineRule, NoReflectionRule,
            NoSyncOverAsyncRule, NoFireAndForgetData);

    /// <summary>编译起点读全局配置并按 pack 注册检查（生成代码不分析、并发执行）。</summary>
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
                    Location.None, $"tier_forbidden.* 配置非法：{error}");
                start.RegisterCompilationEndAction(ctx => ctx.ReportDiagnostic(diagnostic));
            }

            return;   // 配置非法不降级继续——fail-fast 语义
        }

        if (!config.HasAny) return;

        // TCSG136/137：反射与 sync-over-async 共用 Invocation 回调面
        if (config.Reflection || config.SyncOverAsync)
        {
            start.RegisterSyntaxNodeAction(
                ctx => AnalyzeInvocation(ctx, config.Reflection, config.SyncOverAsync),
                SyntaxKind.InvocationExpression);
        }

        // TCSG138：_ = 丢弃赋值
        if (config.FireAndForget)
            start.RegisterSyntaxNodeAction(AnalyzeTaskDiscard, SyntaxKind.SimpleAssignmentExpression);

        // TCSG134：裸线程——构造面（Operation）+ 调用面（语法）
        if (config.BareThreads)
        {
            var exemptPrefixes = config.BareThreadExemptPrefixes.ToImmutableArray();
            var additionalNames = config.BareThreadAdditionalNames.ToImmutableArray();
            start.RegisterOperationAction(ctx => AnalyzeBareThreadCreation(ctx, exemptPrefixes, additionalNames),
                OperationKind.ObjectCreation);
            start.RegisterSyntaxNodeAction(ctx => AnalyzeBareThreadInvocation(ctx, exemptPrefixes),
                SyntaxKind.InvocationExpression);
        }

        // TCSG135：热路径纪律——Operation 面注册（装箱检测必须操作面）
        if (config.HotPathDiscipline)
        {
            var fqn = config.HotPathAttributeFqn!;
            start.RegisterOperationBlockAction(ctx => AnalyzeHotPathBlock(ctx, fqn));
        }
    }

    // === 配置解析 ===

    private static ForbiddenConfig ParseConfig(AnalyzerConfigOptions global, List<string> errors)
    {
        var config = new ForbiddenConfig();
        var scopeDeclared = false;

        foreach (var key in global.Keys)
        {
            if (!key.StartsWith(KeyNamespace, StringComparison.Ordinal)) continue;
            if (!KnownKeys.Contains(key))
            {
                errors.Add($"未知键 '{key}'");
                continue;
            }

            TierConfigValues.TryGet(global, key, out var value);

            switch (key)
            {
                case KeyPack:
                    foreach (var pack in TierConfigValues.SplitList(value))
                    {
                        if (!KnownPacks.Contains(pack))
                        {
                            errors.Add($"未知 pack 令牌 '{pack}'（已知：{string.Join(" | ", KnownPacks)}）");
                            continue;
                        }

                        switch (pack)
                        {
                            case "reflection": config.Reflection = true; break;
                            case "sync_over_async": config.SyncOverAsync = true; break;
                            case "fire_and_forget": config.FireAndForget = true; break;
                            case "bare_threads": config.BareThreads = true; break;
                            case "hotpath_discipline": config.HotPathDiscipline = true; break;
                        }
                    }

                    break;

                case KeyScopeHotPath:
                    scopeDeclared = true;
                    if (!TryParseMembersWith(value, out var fqn, out var scopeError))
                        errors.Add(scopeError ?? $"作用域解析失败：'{value}'");
                    else
                        config.HotPathAttributeFqn = fqn;
                    break;

                case KeyExemptBareThreads:
                    foreach (var prefix in TierConfigValues.SplitList(value))
                        config.BareThreadExemptPrefixes.Add(prefix);
                    break;

                case KeyAdditionalBareThreads:
                    foreach (var typeName in TierConfigValues.SplitList(value))
                    {
                        if (typeName.IndexOfAny(['*', '?']) >= 0)
                            errors.Add($"{KeyAdditionalBareThreads} 仅支持精确类型全名（无通配）：'{typeName}'");
                        else
                            config.BareThreadAdditionalNames.Add(typeName);
                    }
                    break;
            }
        }

        // 规则与作用域依赖双向收口——声明了不生效的配置 = 配置错误（绝静默）
        if (config.HotPathDiscipline && !scopeDeclared)
            errors.Add($"{KeyPack} 含 hotpath_discipline 但 {KeyScopeHotPath} 缺失——作用域未定义");
        if (scopeDeclared && !config.HotPathDiscipline)
            errors.Add($"{KeyScopeHotPath} 已声明但 {KeyPack} 未含 hotpath_discipline——作用域无生效规则");

        return config;
    }

    /// <summary>解析 members_with([FQN]) 作用域形态（v1 唯一形态——其他语法 = 坏语法）。</summary>
    private static bool TryParseMembersWith(string value, out string fqn, out string? error)
    {
        fqn = string.Empty;
        error = null;

        var trimmed = value.Trim();
        const string prefix = "members_with([";
        const string suffix = "])";

        if (!trimmed.StartsWith(prefix, StringComparison.Ordinal) || !trimmed.EndsWith(suffix, StringComparison.Ordinal))
        {
            error = $"{KeyScopeHotPath} 仅支持 members_with([FQN]) 形态，实际：'{value}'";
            return false;
        }

        fqn = trimmed.Substring(prefix.Length, trimmed.Length - prefix.Length - suffix.Length).Trim();
        if (fqn.Length == 0)
        {
            error = $"{KeyScopeHotPath} 特性 FQN 为空：'{value}'";
            return false;
        }

        return true;
    }

    // === TCSG136/137：反射与 sync-over-async ===

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context, bool checkReflection,
        bool checkSyncOverAsync)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax member) return;

        if (checkReflection)
        {
            var name = member.Name.Identifier.Text;
            if (ReflectionMembers.Contains(name))
            {
                var receiverType = context.SemanticModel.GetTypeInfo(member.Expression, context.CancellationToken).Type;
                var receiver = receiverType?.ToDisplayString() ?? string.Empty;

                // 接收者必须是反射面类型才报：
                //  Type 上的 GetMethod/GetField/...（System.Type）
                //  Activator.CreateInstance（System.Activator）
                //  Assembly.Load*（System.Reflection.Assembly）
                //  *Info.Invoke/GetValue/SetValue（System.Reflection.MethodInfo 等——名字含 Info 且 System.Reflection）
                var isReflectionReceiver = receiver == "System.Type"
                    || receiver == "System.Activator"
                    || receiver == "System.Reflection.Assembly"
                    || (receiver.StartsWith("System.Reflection.", StringComparison.Ordinal) && receiver.Contains("Info"));
                if (isReflectionReceiver)
                {
                    context.ReportDiagnostic(Diagnostic.Create(NoReflectionRule,
                        invocation.GetLocation(), $"{name}()"));
                    return;
                }
            }
        }

        if (!checkSyncOverAsync) return;

        // 形态一：X.GetAwaiter().GetResult()
        if (member.Name.Identifier.Text == "GetResult"
            && member.Expression is InvocationExpressionSyntax inner
            && inner.Expression is MemberAccessExpressionSyntax awaiterMember
            && awaiterMember.Name.Identifier.Text == "GetAwaiter")
        {
            context.ReportDiagnostic(Diagnostic.Create(NoSyncOverAsyncRule,
                invocation.GetLocation(), ".GetAwaiter().GetResult()"));
            return;
        }

        // 形态二：.Wait()——接收者语义确认是 Task/ValueTask 才报（排除业务名 WaitForX）
        if (member.Name.Identifier.Text == "Wait" && member.Expression is not null)
        {
            var type = context.SemanticModel.GetTypeInfo(member.Expression, context.CancellationToken).Type;
            if (type is null) return;
            var display = type.ToDisplayString();
            if (display is "System.Threading.Tasks.Task" or "System.Threading.Tasks.ValueTask"
                || display.StartsWith("System.Threading.Tasks.Task<", StringComparison.Ordinal)
                || display.StartsWith("System.Threading.Tasks.ValueTask<", StringComparison.Ordinal))
            {
                context.ReportDiagnostic(Diagnostic.Create(NoSyncOverAsyncRule,
                    invocation.GetLocation(), ".Wait()"));
            }
        }
    }

    // === TCSG138：fire-and-forget ===

    private static void AnalyzeTaskDiscard(SyntaxNodeAnalysisContext context)
    {
        var assignment = (AssignmentExpressionSyntax)context.Node;
        if (assignment.Left is not IdentifierNameSyntax discard || discard.Identifier.Text != "_") return;
        if (assignment.Right is DeclarationExpressionSyntax) return;   // var (_, x) = 解构形态——非本规则面

        var type = context.SemanticModel.GetTypeInfo(assignment.Right, context.CancellationToken).Type;
        if (type is null || !IsTaskLike(type)) return;
        context.ReportDiagnostic(Diagnostic.Create(NoFireAndForgetData,
            assignment.GetLocation(), type.ToDisplayString()));
    }

    /// <summary>Task/ValueTask 判定：沿基类链查 System.Threading.Tasks.Task（覆盖 Task&lt;T&gt; 全族）；
    /// ValueTask/ValueTask&lt;T&gt; 是 struct 无基类链——按命名空间+类型名判定（泛型/非泛型 Name 同为 "ValueTask"）。</summary>
    private static bool IsTaskLike(ITypeSymbol type)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            if (t is not INamedTypeSymbol named) continue;
            var def = named.OriginalDefinition;
            if (def.ContainingNamespace?.ToDisplayString() != "System.Threading.Tasks") continue;
            if (def.Name is "Task" or "ValueTask")
                return true;
        }

        return false;
    }

    // === TCSG134：裸线程 ===

    private static void AnalyzeBareThreadCreation(OperationAnalysisContext context,
        ImmutableArray<string> exemptPrefixes, ImmutableArray<string> additionalNames)
    {
        if (context.Operation is not IObjectCreationOperation creation) return;
        var constructed = creation.Type;
        if (constructed is not INamedTypeSymbol named) return;
        var def = named.OriginalDefinition;
        var ns = def.ContainingNamespace?.ToDisplayString();
        if (ns != "System.Threading")
        {
            // 追加清单 = 精确类型全名匹配（消费方配置追加，add-only——内建底线不被覆盖）
            var fqn = ns is null ? def.Name : ns + "." + def.Name;
            if (!additionalNames.Contains(fqn)) return;
        }
        else if (!BareThreadConstructorNames.Contains(def.Name))
        {
            return;
        }

        if (IsExempt(context.ContainingSymbol, exemptPrefixes)) return;
        context.ReportDiagnostic(Diagnostic.Create(BareThreadsRule,
            creation.Syntax.GetLocation(), $"new {def.Name}(...)"));
    }

    private static void AnalyzeBareThreadInvocation(SyntaxNodeAnalysisContext context,
        ImmutableArray<string> exemptPrefixes)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax member) return;

        var name = member.Name.Identifier.Text;
        if (name is not ("Sleep" or "Run")) return;

        var receiverType = context.SemanticModel.GetTypeInfo(member.Expression, context.CancellationToken).Type;
        if (receiverType is null) return;

        var what = name switch
        {
            "Sleep" when IsThreadType(receiverType) => "Thread.Sleep",
            "Run" when IsTaskType(receiverType) => "Task.Run",
            _ => null,
        };
        if (what is null) return;

        if (IsExempt(context.ContainingSymbol, exemptPrefixes)) return;
        context.ReportDiagnostic(Diagnostic.Create(BareThreadsRule, invocation.GetLocation(), what));
    }

    /// <summary>Thread 类型判定（sealed 单形——精确匹配）。</summary>
    private static bool IsThreadType(ITypeSymbol type)
        => type is INamedTypeSymbol named
            && named.OriginalDefinition.ContainingNamespace?.ToDisplayString() == "System.Threading"
            && named.OriginalDefinition.Name == "Thread";

    /// <summary>Task 类型判定：沿基类链查 System.Threading.Tasks.Task（覆盖 Task&lt;T&gt; 接收者）。</summary>
    private static bool IsTaskType(ITypeSymbol type)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            if (t is not INamedTypeSymbol named) continue;
            var def = named.OriginalDefinition;
            if (def.ContainingNamespace?.ToDisplayString() == "System.Threading.Tasks" && def.Name == "Task")
                return true;
        }

        return false;
    }

    /// <summary>裸线程豁免：包含符号的完整名（含命名空间与嵌套链）命中任一豁免前缀。</summary>
    private static bool IsExempt(ISymbol? containingSymbol, ImmutableArray<string> exemptPrefixes)
    {
        if (containingSymbol is null || exemptPrefixes.IsEmpty) return false;

        var type = containingSymbol switch
        {
            IMethodSymbol method => method.ContainingType,
            INamedTypeSymbol named => named,
            IFieldSymbol field => field.ContainingType,
            IPropertySymbol property => property.ContainingType,
            IEventSymbol evt => evt.ContainingType,
            _ => null,
        };
        if (type is null) return false;

        var display = type.ToDisplayString();
        foreach (var prefix in exemptPrefixes)
        {
            if (TierConfigValues.NamespaceMatches(prefix, display)) return true;
        }

        return false;
    }

    // === TCSG135：热路径纪律（Operation 面——装箱必须操作面）===

    private static void AnalyzeHotPathBlock(OperationBlockAnalysisContext context, string attributeFqn)
    {
        // v1 作用域 = 标注方法体本身（方法带特性即入检查集；内联 callee 近似展开后置）
        if (context.OwningSymbol is not IMethodSymbol method) return;
        if (!HasAttribute(method, attributeFqn)) return;

        foreach (var block in context.OperationBlocks)
            WalkOperation(block, context.ReportDiagnostic);
    }

    private static bool HasAttribute(IMethodSymbol method, string attributeFqn)
    {
        foreach (var attribute in method.GetAttributes())
        {
            if (attribute.AttributeClass is null) continue;
            if (string.Equals(attribute.AttributeClass.ToDisplayString(), attributeFqn, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static void WalkOperation(IOperation operation, Action<Diagnostic> report)
    {
        switch (operation)
        {
            case IConversionOperation conversion:
                // 装箱判定（类型语义——CommonConversion 无 IsBoxing）：值类型 → object/ValueType/Enum/接口
                if (IsBoxingConversion(conversion))
                    report(Diagnostic.Create(HotPathDisciplineRule, conversion.Syntax.GetLocation(), "装箱转换"));
                break;

            case IInvocationOperation invocation:
                AnalyzeHotPathInvocation(invocation, report);
                break;

            case IObjectCreationOperation creation:
                if (creation.Type?.SpecialType == SpecialType.System_String)
                    report(Diagnostic.Create(HotPathDisciplineRule, creation.Syntax.GetLocation(), "new string(...) 分配"));
                break;

            case IInterpolatedStringOperation:
            case IInterpolatedStringHandlerCreationOperation:
                report(Diagnostic.Create(HotPathDisciplineRule, operation.Syntax.GetLocation(), "内插字符串分配"));
                break;
        }

        foreach (var child in operation.ChildOperations)
            WalkOperation(child, report);
    }

    /// <summary>装箱转换判定：操作数是值类型而目标是引用形态（object/ValueType/Enum/任一接口）。</summary>
    private static bool IsBoxingConversion(IConversionOperation conversion)
    {
        var source = conversion.Operand.Type;
        var target = conversion.Type;
        if (source is null || target is null) return false;
        if (!source.IsValueType) return false;

        return target.SpecialType is SpecialType.System_Object or SpecialType.System_ValueType or SpecialType.System_Enum
            || target.TypeKind == TypeKind.Interface;
    }

    private static void AnalyzeHotPathInvocation(IInvocationOperation invocation, Action<Diagnostic> report)
    {
        var target = invocation.TargetMethod;
        var location = invocation.Syntax.GetLocation();

        var ns = target.ContainingNamespace?.ToDisplayString();
        if (ns is not null && ns == "System.Linq")
        {
            report(Diagnostic.Create(HotPathDisciplineRule, location, $"LINQ 调用 '{target.ToDisplayString()}'"));
            return;
        }

        if (target.ContainingType?.SpecialType == SpecialType.System_String
            && target.Name is "Format" or "Concat" or "Join")
        {
            report(Diagnostic.Create(HotPathDisciplineRule, location, $"string.{target.Name} 分配"));
            return;
        }

        // 值类型 .ToString() 启发式（装箱 string 分配的高发形态；枚举 ToString 免报——常量形态编译器内联）
        if (target.Name == "ToString"
            && invocation.Instance?.Type is { IsValueType: true, TypeKind: not TypeKind.Enum })
        {
            report(Diagnostic.Create(HotPathDisciplineRule, location, "值类型 ToString 分配"));
        }
    }
}
