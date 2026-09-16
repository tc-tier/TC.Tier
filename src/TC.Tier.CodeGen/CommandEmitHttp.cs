using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using TC.Tier.CodeGen.Templating;

namespace TC.Tier.CodeGen;

/// <summary>
/// HttpApi 执行代理发射（<see cref="CommandGenerator"/> 产物二）：
/// TryHandleAsync 单一入口 + 编译期路由表（switch 分段字面量匹配，零运行期路由构建）。
/// </summary>
public sealed partial class CommandGenerator
{
    private static void EmitHttpRoot(SourceProductionContext spc, CommandModel.GroupModel root,
        Dictionary<string, CommandModel.GroupModel> byFqn)
    {
        var rootNode = BuildNode(root, string.Empty, byFqn);
        if (rootNode is null) return;

        var rootClass = root.Fqn.Substring(root.Fqn.LastIndexOf('.') + 1);
        var ns = root.Fqn.Contains('.')
            ? root.Fqn.Substring(0, root.Fqn.LastIndexOf('.'))
            : string.Empty;
        var httpClass = rootClass + "Http";
        var title = $"{httpClass}——[CommandGroup] {root.Fqn} 生成（#435）";

        spc.AddSource($"{ns.Replace('.', '_')}_{httpClass}.g.cs",
            RenderHttp(root, rootNode, ns, httpClass, title));
    }

    private static string RenderHttp(CommandModel.GroupModel root, TreeNode rootNode, string ns,
        string httpClass, string title)
    {
        var allNodes = new List<TreeNode>();
        Collect(rootNode, allNodes);

        var routeBody = new StringBuilder();
        EmitHttpLevel(routeBody, "        ", rootNode);

        // 自定义路由（Route 覆写）——树外字面段精确匹配，位置参数只取 query
        var customRoutes = allNodes.Where(n => n.Command is not null && n.Command!.Route.Length > 0)
            .OrderByDescending(n => n.Command!.Route.Split('/').Length).ToList();
        var customBody = new StringBuilder();
        foreach (var node in customRoutes)
        {
            var command = node.Command!;
            var segments = command.Route.Trim('/').Split('/');
            var conditions = new List<string>();
            for (int i = 0; i < segments.Length; i++)
            {
                conditions.Add($"si + {i} < segs.Length && global::System.Uri.UnescapeDataString(segs[si + {i}]) == \"{segments[i]}\"");
            }

            customBody.AppendLine($"        if ({string.Join(" && ", conditions)})");
            customBody.AppendLine($"            return await {HttpFuncName(command, node.Path)}(request, segs, si + {segments.Length}, ParseQuery(request.Query), {ServiceArg(command)}, results, json, ct);");
        }

        var commandFuncs = new StringBuilder();
        foreach (var node in allNodes.Where(n => n.Command is not null))
        {
            commandFuncs.AppendLine(RenderHttpCommandFunc(node, root.Fqn));
        }

        return TemplateEngine.Render("Commands/HttpShell", TemplateEngine.Tokens(
            ("TITLE", title),
            ("NAMESPACE", ns),
            ("HTTP_CLASS", httpClass),
            ("ROOT_CLASS", "global::" + root.Fqn),
            ("ROOT_TOKEN", root.Name),
            ("ROUTE_BODY", routeBody.ToString().TrimEnd()),
            ("CUSTOM_ROUTES", customBody.ToString().TrimEnd()),
            ("COMMAND_FUNCS", commandFuncs.ToString().TrimEnd())));
    }

    /// <summary>HTTP 解析失败块（400 + WriteError——传输中立渲染）。</summary>
    private static string HttpErrBlock(string message, string valExpr)
        => $"{{ return __tcsg_results.WriteError(400, \"{message}\" + {valExpr}); }}";

    /// <summary>service 形参实参——根实例（嵌套组经函数体内 AcquireExpr 导航；静态命令不消费实例传 null!）。</summary>
    private static string ServiceArg(CommandModel.CmdModel command)
        => command.IsStatic ? "null!" : "service";

    private static string AcquireFor(CommandModel.CmdModel command)
        => command.IsStatic ? "global::" + command.ContainerFqn : command.ContainerAcquireExpr;

    private static string HttpFuncName(CommandModel.CmdModel command, string path)
        => "Hnd_" + (path.Length == 0 ? Pascal(command.Name) : Pascal(path.Replace(" ", "_")));

    /// <summary>HTTP 路由派发（段字面量匹配；不属本树返回 null 交宿主——多根安全）。</summary>
    private static void EmitHttpLevel(StringBuilder sb, string indent, TreeNode node, int depth = 0)
    {
        var segVar = $"seg{depth}";
        sb.AppendLine($"{indent}if (si >= segs.Length) return null;");
        sb.AppendLine($"{indent}var {segVar} = global::System.Uri.UnescapeDataString(segs[si]);");

        var first = true;
        foreach (var child in node.Children.Where(c => c.Group is not null))
        {
            sb.AppendLine($"{indent}{(first ? "if" : "else if")} ({segVar} == \"{child.Group!.Name}\")");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    si++;");
            EmitHttpLevel(sb, indent + "    ", child, depth + 1);
            sb.AppendLine($"{indent}    return null;");
            sb.AppendLine($"{indent}}}");
            first = false;
        }

        foreach (var child in node.Children.Where(c => c.Command is not null))
        {
            var command = child.Command!;
            if (command.Route.Length > 0) continue;   // 自定义路由走 CUSTOM_ROUTES 字面匹配
            sb.AppendLine($"{indent}{(first ? "if" : "else if")} ({segVar} == \"{command.Name}\")");
            sb.AppendLine($"{indent}    return await {HttpFuncName(command, child.Path)}(request, segs, si + 1, ParseQuery(request.Query), {ServiceArg(command)}, results, json, ct);");
            first = false;
        }

        if (!first) return;
        sb.AppendLine($"{indent}return null;");
    }

    private static string RenderHttpCommandFunc(TreeNode node, string rootFqn)
    {
        var command = node.Command!;
        return TemplateEngine.Render("Commands/HttpBindCommand", TemplateEngine.Tokens(
            ("FUNC_NAME", HttpFuncName(command, node.Path)),
            ("ROOT_CLASS", "global::" + rootFqn),
            ("CMD_TITLE", $"{node.Path}: {command.Description}"),
            ("BODY", BuildHttpCommandBody(command))));
    }

    private static string BuildHttpCommandBody(CommandModel.CmdModel command)
    {
        var sb = new StringBuilder();
        var indent = "        ";
        var positional = command.Params
            .Where(p => p.Role == CommandModel.ParamRole.Arg && IsBindable(p))
            .OrderBy(p => p.Position).ToList();
        var options = command.Params
            .Where(p => p.Role == CommandModel.ParamRole.Option && IsBindable(p)).ToList();
        var bodyParam = command.Params.FirstOrDefault(p => p.Role == CommandModel.ParamRole.Body);
        var bindable = command.Params.Where(IsBindable).ToList();

        // HTTP 方法校验（GET 命令禁 body 已在编译期 TCSG058 拦截）
        sb.AppendLine($"{indent}if (__tcsg_request.Method != \"{command.Method}\")");
        sb.AppendLine($"{indent}    return __tcsg_results.WriteError(405, $\"方法 {{__tcsg_request.Method}} 不被支持——本命令为 {command.Method}\");");

        // 绑定局部声明（自有标识符 __tcsg_ 保留前缀——与用户参数名永不撞，#454）
        foreach (var parameter in bindable)
        {
            var init = string.Empty;
            if (parameter.Kind == CommandModel.BindKind.Body) init = " = default!";
            else if (parameter.HasDefaultValue) init = " = " + parameter.DefaultValueExpr;
            else if (parameter.Kind == CommandModel.BindKind.Bool) init = " = false";
            else if (parameter.Kind == CommandModel.BindKind.String) init = " = default!";
            else if (parameter.Kind == CommandModel.BindKind.Nullable) init = " = default";
            sb.AppendLine($"{indent}{parameter.TypeDisplay} {parameter.Name}{init};");
            if (NeedsBoundFlag(parameter))
                sb.AppendLine($"{indent}bool {BoundFlag(parameter)} = false;");
        }

        // 位置参数：路由段依序捕获，段尽则回落 query（设计 §5 双来源）
        sb.AppendLine($"{indent}int __tcsg_spi = __tcsg_segStart;");
        foreach (var parameter in positional)
        {
            sb.AppendLine($"{indent}if (__tcsg_spi < __tcsg_segs.Length)");
            sb.AppendLine($"{indent}{{");
            EmitParse(sb, indent + "    ", parameter.Kind, parameter.InnerKind, parameter.EnumFqn,
                parameter.TypeDisplay, "global::System.Uri.UnescapeDataString(__tcsg_segs[__tcsg_spi])", parameter.Name,
                HttpErrBlock($"路由参数 {parameter.Name} 值非法: ", "global::System.Uri.UnescapeDataString(__tcsg_segs[__tcsg_spi])"));
            sb.AppendLine($"{indent}    __tcsg_spi++;");
            if (NeedsBoundFlag(parameter)) sb.AppendLine($"{indent}    {BoundFlag(parameter)} = true;");
            sb.AppendLine($"{indent}}}");
            sb.AppendLine($"{indent}else if (__tcsg_query is not null && __tcsg_query.TryGetValue(\"{parameter.Name}\", out var __tcsg_q_{parameter.Name}))");
            sb.AppendLine($"{indent}{{");
            EmitParse(sb, indent + "    ", parameter.Kind, parameter.InnerKind, parameter.EnumFqn,
                parameter.TypeDisplay, $"__tcsg_q_{parameter.Name}", parameter.Name,
                HttpErrBlock($"query 参数 {parameter.Name} 值非法: ", $"__tcsg_q_{parameter.Name}"));
            if (NeedsBoundFlag(parameter)) sb.AppendLine($"{indent}    {BoundFlag(parameter)} = true;");
            sb.AppendLine($"{indent}}}");
            if (!parameter.HasDefaultValue)
                sb.AppendLine($"{indent}else return __tcsg_results.WriteError(400, \"缺少路由参数 {parameter.Name}\");");
        }

        if (positional.Count > 0)
        {
            sb.AppendLine($"{indent}if (__tcsg_spi < __tcsg_segs.Length) return __tcsg_results.WriteError(400, \"路径参数段多于命令声明\");");
        }

        // 选项：query 来源
        foreach (var parameter in options)
        {
            sb.AppendLine($"{indent}if (__tcsg_query is not null && __tcsg_query.TryGetValue(\"{parameter.LongName}\", out var __tcsg_q_{parameter.Name}))");
            sb.AppendLine($"{indent}{{");
            if (parameter.Kind == CommandModel.BindKind.Bool)
            {
                sb.AppendLine($"{indent}    if (__tcsg_q_{parameter.Name} is null or \"\" or \"true\") {parameter.Name} = true;");
                sb.AppendLine($"{indent}    else if (__tcsg_q_{parameter.Name} == \"false\") {parameter.Name} = false;");
                sb.AppendLine($"{indent}    else return __tcsg_results.WriteError(400, \"query 选项 {parameter.LongName} 值非法\");");
            }
            else
            {
                EmitParse(sb, indent + "    ", parameter.Kind, parameter.InnerKind, parameter.EnumFqn,
                    parameter.TypeDisplay, $"__tcsg_q_{parameter.Name}", parameter.Name,
                    HttpErrBlock($"query 选项 {parameter.LongName} 值非法: ", $"__tcsg_q_{parameter.Name}"));
            }

            if (NeedsBoundFlag(parameter)) sb.AppendLine($"{indent}    {BoundFlag(parameter)} = true;");
            sb.AppendLine($"{indent}}}");
            if (!parameter.HasDefaultValue && parameter.Kind != CommandModel.BindKind.Bool)
                sb.AppendLine($"{indent}if (!{BoundFlag(parameter)}) return __tcsg_results.WriteError(400, \"缺少 query 选项 {parameter.LongName}\");");
        }

        // body：JSON 反序列化（GetTypeInfo 查表非反射——AOT 契约）
        if (bodyParam is not null)
        {
            sb.AppendLine($"{indent}if (__tcsg_json is null) return __tcsg_results.WriteError(400, \"命令含 body 参数——需 JsonSerializerContext\");");
            sb.AppendLine($"{indent}try");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    var __tcsg_ti = __tcsg_json.GetTypeInfo(typeof({bodyParam.TypeDisplay}));");
            sb.AppendLine($"{indent}    {bodyParam.Name} = ({bodyParam.TypeDisplay})global::System.Text.Json.JsonSerializer.Deserialize(__tcsg_request.Body, __tcsg_ti)!;");
            sb.AppendLine($"{indent}}}");
            sb.AppendLine($"{indent}catch (global::System.Text.Json.JsonException __tcsg_ex) {{ return __tcsg_results.WriteError(400, \"body JSON 解析失败: \" + __tcsg_ex.Message); }}");
        }

        // 调用（返回形态分流）+ 异常状态码映射（最派生优先 catch 链——提取期已排序）
        var invokeArgs = string.Join(", ", command.Params.Select(p =>
            p.Kind == CommandModel.BindKind.CancellationToken ? "__tcsg_ct" : p.Name));
        var container = command.IsStatic
            ? "global::" + command.ContainerFqn
            : command.ContainerAcquireExpr;
        sb.AppendLine($"{indent}try");
        sb.AppendLine($"{indent}{{");
        switch (command.Return)
        {
            case CommandModel.ReturnKind.Void:
            case CommandModel.ReturnKind.Task:
            case CommandModel.ReturnKind.ValueTask:
                var awaitVoid = command.Return == CommandModel.ReturnKind.Void ? string.Empty : "await ";
                sb.AppendLine($"{indent}    {awaitVoid}{container}.{command.MethodName}({invokeArgs});");
                sb.AppendLine($"{indent}    return new global::TC.Tier.CodeGen.CommandHttpResponse {{ Status = 200 }};");
                break;
            default:
                var awaitPrefix = command.Return == CommandModel.ReturnKind.Sync ? string.Empty : "await ";
                sb.AppendLine($"{indent}    var __tcsg_r = {awaitPrefix}{container}.{command.MethodName}({invokeArgs});");
                sb.AppendLine($"{indent}    return await __tcsg_results.WriteAsync(__tcsg_r);");
                break;
        }

        sb.AppendLine($"{indent}}}");

        var catchDepth = 0;
        foreach (var error in command.Errors)
        {
            var variable = $"__tcsg_ex{catchDepth++}";
            sb.AppendLine($"{indent}catch ({error.ExceptionFqn} {variable}) {{ return __tcsg_results.WriteError({error.Status}, {variable}); }}");
        }

        sb.AppendLine($"{indent}catch (global::System.Exception __tcsg_ex) {{ return __tcsg_results.WriteError(500, __tcsg_ex); }}");
        return sb.ToString();
    }
}
