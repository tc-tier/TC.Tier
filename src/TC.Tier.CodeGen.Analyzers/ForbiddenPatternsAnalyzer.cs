using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace TC.Tier.CodeGen.Analyzers;

/// <summary>
/// TC.Tier 代码规范分析器——禁止两条反模式：
/// <para>★ TCSG030 禁止运行时反射（System.Reflection 调用面）——反射破坏 AOT/裁剪/性能，且本代码库
///   IVT + 契约面设计下反射零必要（白盒访问走 InternalsVisibleTo，不用反射绕过）。</para>
/// <para>★ TCSG031 禁止同步强制等待异步（sync-over-async：<c>.GetAwaiter().GetResult()</c> / <c>.Wait()</c>）——
///   同步阻塞后台 Task 在同步上下文下经典死锁 + 线程池耗尽风险（同步 Compact 废除决策：
///   一律后台句柄 await WaitAsync）。</para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ForbiddenPatternsAnalyzer : DiagnosticAnalyzer
{
    private const string Category = "TierCode";

    private static readonly DiagnosticDescriptor NoReflectionRule = new(
        id: "TCSG030",
        title: "禁止运行时反射",
        messageFormat: "禁止反射调用 '{0}'——本代码库反射零必要（白盒走 InternalsVisibleTo；反射破坏 AOT/裁剪/性能）。",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor NoSyncOverAsyncRule = new(
        id: "TCSG031",
        title: "禁止同步强制等待异步（sync-over-async）",
        messageFormat: "禁止 '{0}'——同步阻塞后台 Task 会死锁（同步上下文）+ 线程池耗尽风险；一律 await 后台句柄（WaitAsync）。",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>反射调用面（方法名）——接收者经语义确认是反射类型才报（防业务同名方法误伤）。</summary>
    private static readonly HashSet<string> ReflectionMembers = new(StringComparer.Ordinal)
    {
        "GetMethod", "GetField", "GetProperty", "GetConstructor", "GetEvent", "GetNestedType",
        "GetTypeInfo", "GetMethods", "GetFields", "GetProperties", "GetConstructors", "GetInterfaces",
        "CreateInstance", "Load", "LoadFrom", "LoadFile", "Invoke", "GetValue", "SetValue", "GetGenericArguments",
    };

    /// <summary>本分析器支持的全部诊断规则（TCSG030 禁运行时反射 / TCSG031 禁 sync-over-async）。</summary>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(NoReflectionRule, NoSyncOverAsyncRule);

    /// <summary>注册 InvocationExpression 语法节点回调：AnalyzeReflection（TCSG030）、
    /// AnalyzeSyncOverAsync / AnalyzeSyncOverAsyncWait（TCSG031）——生成代码不分析、启用并发执行。</summary>
    /// <param name="context">分析上下文。</param>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeReflection, SyntaxKind.InvocationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeSyncOverAsync, SyntaxKind.InvocationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeSyncOverAsyncWait, SyntaxKind.InvocationExpression);
    }

    // === TCSG030：反射调用面（语义确认接收者类型——只拦真反射，不误伤业务同名方法）===

    private static void AnalyzeReflection(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax member) return;
        var name = member.Name.Identifier.Text;
        if (!ReflectionMembers.Contains(name)) return;

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
        if (!isReflectionReceiver) return;

        Report(context, invocation, NoReflectionRule, $"{name}()");
    }

    // === TCSG031：sync-over-async（GetAwaiter().GetResult()）===

    private static void AnalyzeSyncOverAsync(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax member) return;
        if (member.Name.Identifier.Text != "GetResult") return;

        // 形态：X.GetAwaiter().GetResult()
        if (member.Expression is InvocationExpressionSyntax inner
            && inner.Expression is MemberAccessExpressionSyntax awaiterMember
            && awaiterMember.Name.Identifier.Text == "GetAwaiter")
        {
            Report(context, invocation, NoSyncOverAsyncRule, ".GetAwaiter().GetResult()");
        }
    }

    // === TCSG031：sync-over-async（.Wait()）===

    private static void AnalyzeSyncOverAsyncWait(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax member) return;
        if (member.Name.Identifier.Text != "Wait") return;
        if (member.Expression is null) return;

        // 排除 WaitForReady/WaitForExit 等业务名（它们不叫 Wait）；.Wait( 是 Task/ValueTask 同步等待形态
        // （语义确认：接收者解析为 Task/ValueTask 才报——避免自定义 Wait 方法误报）。
        var type = context.SemanticModel.GetTypeInfo(member.Expression, context.CancellationToken).Type;
        if (type is null) return;
        var display = type.ToDisplayString();
        if (display is "System.Threading.Tasks.Task" or "System.Threading.Tasks.ValueTask"
            or "System.Threading.Tasks.Task<int>" or "System.Threading.Tasks.Task<long>"
            or "System.Threading.Tasks.ValueTask<int>" or "System.Threading.Tasks.ValueTask<long>"
            || display.StartsWith("System.Threading.Tasks.Task<", StringComparison.Ordinal)
            || display.StartsWith("System.Threading.Tasks.ValueTask<", StringComparison.Ordinal))
        {
            Report(context, invocation, NoSyncOverAsyncRule, ".Wait()");
        }
    }

    private static void Report(SyntaxNodeAnalysisContext context, SyntaxNode node, DiagnosticDescriptor rule, string what)
    {
        context.ReportDiagnostic(Diagnostic.Create(rule, node.GetLocation(), what));
    }
}
