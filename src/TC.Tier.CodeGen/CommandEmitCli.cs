using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using TC.Tier.CodeGen.Templating;

namespace TC.Tier.CodeGen;

/// <summary>
/// CLI 解析器发射（<see cref="CommandGenerator"/> 产物一）：
/// Run/RunAsync 逐级组路径匹配 → 绑定 → 直调；WriteHelp/CompleteNext/CommandPaths 数据面。
/// </summary>
public sealed partial class CommandGenerator
{
    /// <summary>命令树节点——组节点（Group 非空）与命令叶子（Command 非空）。</summary>
    private sealed class TreeNode
    {
        public CommandModel.GroupModel? Group;
        public CommandModel.CmdModel? Command;
        public string Path = string.Empty;   // 空格分隔相对根路径（根 = ""）
        public readonly List<TreeNode> Children = new();
    }

    private static readonly char[] KebabSeparators = { '-' };

    private static string Pascal(string name)
    {
        var parts = name.Split(KebabSeparators, StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        foreach (var part in parts) sb.Append(char.ToUpperInvariant(part[0])).Append(part.Substring(1));
        return sb.ToString();
    }

    private static string HelpConstName(TreeNode node)
        => node.Path.Length == 0 ? "HelpRoot" : "Help_" + Pascal(node.Path.Replace(" ", "_"));

    private static string FuncName(CommandModel.CmdModel command, string path)
        => "Cmd_" + (path.Length == 0 ? Pascal(command.Name) : Pascal(path.Replace(" ", "_")));

    private static void EmitRoot(SourceProductionContext spc, CommandModel.GroupModel root,
        Dictionary<string, CommandModel.GroupModel> byFqn)
    {
        var rootNode = BuildNode(root, string.Empty, byFqn);
        if (rootNode is null) return;

        var rootClass = root.Fqn.Substring(root.Fqn.LastIndexOf('.') + 1);
        var ns = root.Fqn.Contains('.')
            ? root.Fqn.Substring(0, root.Fqn.LastIndexOf('.'))
            : string.Empty;
        var cliClass = rootClass + "Cli";
        var title = $"{cliClass}——[CommandGroup] {root.Fqn} 生成（#435）";

        spc.AddSource($"{ns.Replace('.', '_')}_{cliClass}.g.cs",
            RenderCli(root, rootNode, ns, cliClass, title));
    }

    private static TreeNode? BuildNode(CommandModel.GroupModel group, string path,
        Dictionary<string, CommandModel.GroupModel> byFqn)
    {
        var node = new TreeNode { Group = group, Path = path };
        foreach (var childGroup in byFqn.Values.Where(g => !g.IsRoot && g.ParentFqn == group.Fqn))
        {
            var childPath = path.Length == 0 ? childGroup.Name : path + " " + childGroup.Name;
            var child = BuildNode(childGroup, childPath, byFqn);
            if (child is not null) node.Children.Add(child);
        }

        foreach (var command in group.Commands)
        {
            node.Children.Add(new TreeNode
            {
                Command = command,
                Path = path.Length == 0 ? command.Name : path + " " + command.Name,
            });
        }

        return node;
    }

    private static string RenderCli(CommandModel.GroupModel root, TreeNode rootNode, string ns,
        string cliClass, string title)
    {
        var allNodes = new List<TreeNode>();
        Collect(rootNode, allNodes);

        var helpConsts = new StringBuilder();
        foreach (var node in allNodes)
        {
            helpConsts.AppendLine($"    private const string {HelpConstName(node)} =");
            helpConsts.AppendLine("        " + Literal(BuildHelpText(node, root)) + ";");
            helpConsts.AppendLine();
        }

        var helpMap = new StringBuilder();
        helpMap.AppendLine("            [string.Empty] = HelpRoot,");
        foreach (var node in allNodes.Where(n => n.Path.Length > 0))
        {
            helpMap.AppendLine($"            [\"{node.Path}\"] = {HelpConstName(node)},");
        }

        var nodePathList = allNodes.Where(n => n.Path.Length > 0)
            .Select(n => $"\"{n.Path}\"").ToList();
        var nodePaths = nodePathList.Count > 0
            ? "new[]\n        {\n            " + string.Join(",\n            ", nodePathList) + "\n        }"
            : "global::System.Array.Empty<string>()";

        var commandPathList = allNodes.Where(n => n.Command is not null)
            .Select(n => "new[] { \"" + n.Path.Replace(" ", "\", \"") + "\" }").ToList();
        var commandPaths = commandPathList.Count > 0
            ? "new[]\n        {\n            " + string.Join(",\n            ", commandPathList) + "\n        }"
            : "global::System.Array.Empty<string[]>()";

        var runBody = new StringBuilder();
        EmitRunLevel(runBody, "        ", rootNode);

        var commandFuncs = new StringBuilder();
        foreach (var node in allNodes.Where(n => n.Command is not null))
        {
            commandFuncs.AppendLine(RenderCliCommandFunc(node, root.Fqn));
        }

        return TemplateEngine.Render("Commands/CliShell", TemplateEngine.Tokens(
            ("TITLE", title),
            ("NAMESPACE", ns),
            ("CLI_CLASS", cliClass),
            ("ROOT_CLASS", "global::" + root.Fqn),
            ("JSON_CONTEXT", root.Fqn.Substring(root.Fqn.LastIndexOf('.') + 1) + "JsonContext"),
            ("JSON_RESULTS", root.Fqn.Substring(root.Fqn.LastIndexOf('.') + 1) + "JsonResults"),
            ("HELP_CONSTS", helpConsts.ToString().TrimEnd()),
            ("HELP_MAP", helpMap.ToString().TrimEnd()),
            ("NODE_PATHS", nodePaths),
            ("COMMAND_PATHS", commandPaths),
            ("RUN_BODY", runBody.ToString().TrimEnd()),
            ("COMMAND_FUNCS", commandFuncs.ToString().TrimEnd())));
    }

    private static void Collect(TreeNode node, List<TreeNode> into)
    {
        into.Add(node);
        foreach (var child in node.Children) Collect(child, into);
    }

    // ══════════ 帮助文本 ══════════

    private static string BuildHelpText(TreeNode node, CommandModel.GroupModel root)
        => node.Command is not null ? BuildCommandHelp(node, root) : BuildGroupHelp(node, root);

    private static string BuildGroupHelp(TreeNode node, CommandModel.GroupModel root)
    {
        var sb = new StringBuilder();
        var display = node.Path.Length == 0 ? root.Name : $"{root.Name} {node.Path}";
        var group = node.Group!;
        var desc = string.IsNullOrEmpty(group.Description) ? string.Empty : $" — {group.Description}";
        sb.AppendLine($"{display}{desc}");
        sb.AppendLine();
        sb.AppendLine($"用法：{display} <组|命令> [参数] [选项]");
        var groups = node.Children.Where(c => c.Group is not null).ToList();
        var commands = node.Children.Where(c => c.Command is not null).ToList();
        if (groups.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("组：");
            foreach (var child in groups)
            {
                var childDesc = string.IsNullOrEmpty(child.Group!.Description)
                    ? string.Empty
                    : "  " + child.Group.Description;
                sb.AppendLine($"  {child.Group.Name}{childDesc}");
            }
        }

        if (commands.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("命令：");
            foreach (var child in commands)
            {
                sb.AppendLine($"  {child.Command!.Name}{UsageArgs(child.Command)}" +
                              (string.IsNullOrEmpty(child.Command.Description)
                                  ? string.Empty
                                  : $"  {child.Command.Description}"));
            }
        }

        sb.AppendLine();
        sb.Append("（-h/--help 查看任意层级帮助）");
        return Literal(sb.ToString());
    }

    private static string BuildCommandHelp(TreeNode node, CommandModel.GroupModel root)
    {
        var command = node.Command!;
        var sb = new StringBuilder();
        var display = $"{root.Name} {node.Path}";
        var desc = string.IsNullOrEmpty(command.Description) ? string.Empty : $" — {command.Description}";
        sb.AppendLine($"{display}{desc}");
        sb.AppendLine();
        sb.AppendLine($"用法：{display}{UsageArgs(command)}");
        var positional = command.Params
            .Where(p => p.Role == CommandModel.ParamRole.Arg && p.Annotated && p.Unbindable.Length == 0
                        && p.Kind != CommandModel.BindKind.CancellationToken)
            .OrderBy(p => p.Position).ToList();
        if (positional.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("位置参数：");
            foreach (var parameter in positional)
            {
                var req = parameter.HasDefaultValue ? "可选" : "必选";
                sb.AppendLine($"  <{parameter.Name}>  ({parameter.TypeDisplay}，{req})");
            }
        }

        var options = command.Params.Where(p => p.Role == CommandModel.ParamRole.Option).ToList();
        if (options.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("选项：");
            foreach (var parameter in options)
            {
                var shortPart = parameter.ShortName != default ? $", -{parameter.ShortName}" : string.Empty;
                var req = parameter.Kind == CommandModel.BindKind.Bool || parameter.HasDefaultValue
                    ? "可选"
                    : "必选";
                sb.AppendLine($"  --{parameter.LongName}{shortPart}  ({parameter.TypeDisplay}，{req})");
            }
        }

        var body = command.Params.FirstOrDefault(p => p.Role == CommandModel.ParamRole.Body);
        if (body is not null)
        {
            sb.AppendLine();
            sb.Append($"body：经 stdin 读 JSON（{body.TypeDisplay}）—— echo '<json>' | {display}");
        }

        return Literal(sb.ToString());
    }

    /// <summary>用法参数签名（位置参数按 Position 序 + 选项长名）。</summary>
    private static string UsageArgs(CommandModel.CmdModel command)
    {
        var sb = new StringBuilder();
        foreach (var parameter in command.Params
                     .Where(p => p.Role == CommandModel.ParamRole.Arg && p.Annotated && p.Unbindable.Length == 0
                                 && p.Kind != CommandModel.BindKind.CancellationToken)
                     .OrderBy(p => p.Position))
        {
            sb.Append(parameter.HasDefaultValue ? $" [{parameter.Name}]" : $" <{parameter.Name}>");
        }

        foreach (var parameter in command.Params.Where(p => p.Role == CommandModel.ParamRole.Option))
        {
            sb.Append(parameter.Kind == CommandModel.BindKind.Bool
                ? $" [--{parameter.LongName}]"
                : $" [--{parameter.LongName} <{parameter.Name}>]");
        }

        if (command.Params.Any(p => p.Role == CommandModel.ParamRole.Body)) sb.Append(" [body]");
        return sb.ToString();
    }

    /// <summary>字符串字面量发射（\n 转义、LF 归一）。</summary>
    private static string Literal(string text)
        => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"")
            .Replace("\r", string.Empty).Replace("\n", "\\n") + "\"";

    // ══════════ RunAsync 组路径派发 ══════════

    private static void EmitRunLevel(StringBuilder sb, string indent, TreeNode node)
    {
        var help = HelpConstName(node);
        sb.AppendLine($"{indent}if (pos >= args.Length || args[pos] is \"-h\" or \"--help\") {{ stdout.Write({help}); return 0; }}");

        var first = true;
        foreach (var child in node.Children.Where(c => c.Group is not null))
        {
            sb.AppendLine($"{indent}{(first ? string.Empty : "else ")}if (args[pos] == \"{child.Group!.Name}\")");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    pos++;");
            EmitRunLevel(sb, indent + "    ", child);
            sb.AppendLine($"{indent}}}");
            first = false;
        }

        foreach (var child in node.Children.Where(c => c.Command is not null))
        {
            var command = child.Command!;
            var container = command.IsStatic
                ? "global::" + command.ContainerFqn
                : command.ContainerAcquireExpr;
            sb.AppendLine($"{indent}{(first ? string.Empty : "else ")}if (args[pos] == \"{command.Name}\")");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    pos++;");
            sb.AppendLine($"{indent}    for (int i = pos; i < args.Length; i++)");
            sb.AppendLine($"{indent}        if (args[i] is \"-h\" or \"--help\") {{ stdout.Write({HelpConstName(child)}); return 0; }}");
            var serviceArg = command.IsStatic ? "null!" : "service";
            sb.AppendLine($"{indent}    return await {FuncName(command, child.Path)}(args, pos, {serviceArg}, stdout, stderr, json, ct, results);");
            sb.AppendLine($"{indent}}}");
            first = false;
        }

        sb.AppendLine($"{indent}else");
        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{indent}    stderr.WriteLine($\"未知命令 '{{args[pos]}}'\");");
        sb.AppendLine($"{indent}    stderr.Write({help});");
        sb.AppendLine($"{indent}    return 2;");
        sb.AppendLine($"{indent}}}");
    }

    // ══════════ 命令函数（Commands/CliParseCommand.sbn）══════════

    private static string RenderCliCommandFunc(TreeNode node, string rootFqn)
    {
        var command = node.Command!;
        var helpConst = HelpConstName(node);
        return TemplateEngine.Render("Commands/CliParseCommand", TemplateEngine.Tokens(
            ("FUNC_NAME", FuncName(command, node.Path)),
            ("ROOT_CLASS", "global::" + rootFqn),
            ("CMD_TITLE", $"{node.Path}: {command.Description}"),
            ("BODY", BuildCliCommandBody(command, helpConst))));
    }

    /// <summary>是否需要 bound 校验标志（非默认值、非 body、非 bool 裸形态的可绑定参数）。</summary>
    private static bool NeedsBoundFlag(CommandModel.ParamModel p)
        => p.Role != CommandModel.ParamRole.Body && !p.HasDefaultValue
            && p.Kind != CommandModel.BindKind.Bool && IsBindable(p);

    /// <summary>参数遍历序（绑定局部声明——保方法签名序，跳过 CT）。</summary>
    private static bool IsBindable(CommandModel.ParamModel p)
        => p.Kind != CommandModel.BindKind.CancellationToken && p.Unbindable.Length == 0;

    // 发射体保留命名空间标识符（__tcsg_ 前缀——用户参数名占用即 TCSG060，生成代码永不撞名）
    private static string BoundFlag(CommandModel.ParamModel p) => $"__tcsg_bound_{p.Name}";

    private static string BuildCliCommandBody(CommandModel.CmdModel command, string helpConst)
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
        var container = command.IsStatic
            ? "global::" + command.ContainerFqn
            : command.ContainerAcquireExpr;

        // 绑定局部声明（保签名序）——非必选项配 bound 标志（必选校验用；bool 裸形态隐含 false）
        foreach (var parameter in bindable)
        {
            var init = string.Empty;
            if (parameter.Kind == CommandModel.BindKind.Body) init = " = default!";
            else if (parameter.HasDefaultValue) init = " = " + parameter.DefaultValueExpr;
            else if (parameter.Kind == CommandModel.BindKind.Bool) init = " = false";
            else if (parameter.Kind == CommandModel.BindKind.String
                     || (parameter.Kind == CommandModel.BindKind.Nullable
                         && parameter.InnerKind == CommandModel.BindKind.String)) init = " = default!";
            else init = " = default";   // 值类型/枚举/可空值类型——零值初始化消 CS0165
            sb.AppendLine($"{indent}{parameter.TypeDisplay} {parameter.Name}{init};");
            // ★ 旗标声明面 = 全部位置参数 ∪ NeedsBoundFlag（#454 残项：带缺省值的位置参数也是槽位
            //   跟踪单位——槽消费循环按旗标判"已填/未填"，不声明即 CS0103；必选校验面仍只认
            //   NeedsBoundFlag——带缺省值参数缺席走默认值，不算缺参）
            if (parameter.Role == CommandModel.ParamRole.Arg || NeedsBoundFlag(parameter))
                sb.AppendLine($"{indent}bool {BoundFlag(parameter)} = false;");
        }

        sb.AppendLine($"{indent}int __tcsg_pos = __tcsg_start;");
        sb.AppendLine($"{indent}bool __tcsg_afterDoubleDash = false;");
        sb.AppendLine($"{indent}while (__tcsg_pos < __tcsg_args.Length)");
        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{indent}    var __tcsg_arg = __tcsg_args[__tcsg_pos];");
        sb.AppendLine($"{indent}    if (!__tcsg_afterDoubleDash && __tcsg_arg == \"--\") {{ __tcsg_afterDoubleDash = true; __tcsg_pos++; continue; }}");
        sb.AppendLine($"{indent}    if (!__tcsg_afterDoubleDash && __tcsg_arg.StartsWith(\"--\", global::System.StringComparison.Ordinal))");
        sb.AppendLine($"{indent}    {{");
        sb.AppendLine($"{indent}        var __tcsg_eq = __tcsg_arg.IndexOf('=');");
        sb.AppendLine($"{indent}        var __tcsg_key = (__tcsg_eq < 0 ? __tcsg_arg.Substring(2) : __tcsg_arg.Substring(2, __tcsg_eq - 2)).Trim();");
        sb.AppendLine($"{indent}        var __tcsg_val = __tcsg_eq < 0 ? null : __tcsg_arg.Substring(__tcsg_eq + 1);");

        var firstOpt = true;
        foreach (var parameter in options)
        {
            sb.AppendLine($"{indent}        {(firstOpt ? "if" : "else if")} (__tcsg_key == \"{parameter.LongName}\")");
            sb.AppendLine($"{indent}        {{");
            EmitOptionAssign(sb, indent + "            ", parameter, "__tcsg_key", "__tcsg_val", helpConst);
            sb.AppendLine($"{indent}            {(NeedsBoundFlag(parameter) ? $"{BoundFlag(parameter)} = true;" : string.Empty)}");
            sb.AppendLine($"{indent}            __tcsg_pos++;");
            sb.AppendLine($"{indent}            continue;");
            sb.AppendLine($"{indent}        }}");
            firstOpt = false;
        }

        sb.AppendLine($"{indent}        {{ __tcsg_stderr.WriteLine($\"未知选项 '{{__tcsg_arg}}'\"); __tcsg_stderr.Write({helpConst}); return 2; }}");
        sb.AppendLine($"{indent}    }}");
        sb.AppendLine($"{indent}    if (!__tcsg_afterDoubleDash && __tcsg_arg.Length >= 2 && __tcsg_arg[0] == '-' && __tcsg_arg[1] != '-')");
        sb.AppendLine($"{indent}    {{");
        var firstShort = true;
        foreach (var parameter in options.Where(p => p.ShortName != default))
        {
            sb.AppendLine($"{indent}        {(firstShort ? "if" : "else if")} (__tcsg_arg.Length == 2 && __tcsg_arg[1] == '{parameter.ShortName}')");
            sb.AppendLine($"{indent}        {{");
            if (parameter.Kind == CommandModel.BindKind.Bool)
            {
                sb.AppendLine($"{indent}            {parameter.Name} = true;");
            }
            else
            {
                sb.AppendLine($"{indent}            if (__tcsg_pos + 1 >= __tcsg_args.Length) {{ __tcsg_stderr.WriteLine($\"选项 -{parameter.ShortName} 缺值\"); __tcsg_stderr.Write({helpConst}); return 2; }}");
                EmitParse(sb, indent + "            ", parameter.Kind, parameter.InnerKind, parameter.EnumFqn,
                    parameter.TypeDisplay, "__tcsg_args[__tcsg_pos + 1]", parameter.Name,
                CliErrBlock($"选项 -{parameter.ShortName} 值非法: ", "__tcsg_args[__tcsg_pos + 1]", helpConst));
            }

            sb.AppendLine($"{indent}            {(NeedsBoundFlag(parameter) ? $"{BoundFlag(parameter)} = true;" : string.Empty)}");
            sb.AppendLine($"{indent}            __tcsg_pos += {(parameter.Kind == CommandModel.BindKind.Bool ? "1" : "2")};");
            sb.AppendLine($"{indent}            continue;");
            sb.AppendLine($"{indent}        }}");
            firstShort = false;
        }

        sb.AppendLine($"{indent}        {{ __tcsg_stderr.WriteLine($\"未知选项 '{{__tcsg_arg}}'\"); __tcsg_stderr.Write({helpConst}); return 2; }}");
        sb.AppendLine($"{indent}    }}");
        // 位置参数槽（按 Position 序逐槽消费——槽已绑定则令牌属多余参数）
        sb.AppendLine($"{indent}    {{");
        var firstSlot = true;
        foreach (var parameter in positional)
        {
            sb.AppendLine($"{indent}        {(firstSlot ? "if" : "else if")} (!{BoundFlag(parameter)})");
            sb.AppendLine($"{indent}        {{");
            EmitParse(sb, indent + "            ", parameter.Kind, parameter.InnerKind, parameter.EnumFqn,
                parameter.TypeDisplay, "__tcsg_args[__tcsg_pos]", parameter.Name,
                CliErrBlock($"位置参数 {parameter.Name} 值非法: ", "__tcsg_args[__tcsg_pos]", helpConst));
            // ★ 槽已消耗——旗标无条件置位（全部位置参数都有旗标声明，#454 残项）：可选槽置位后
            //   后续多余令牌正确落"多余的参数"，不再被未置位的可选槽重复吸收
            sb.AppendLine($"{indent}            {BoundFlag(parameter)} = true;");
            sb.AppendLine($"{indent}            __tcsg_pos++;");
            sb.AppendLine($"{indent}            continue;");
            sb.AppendLine($"{indent}        }}");
            firstSlot = false;
        }

        sb.AppendLine($"{indent}        {{ __tcsg_stderr.WriteLine($\"多余的参数 '{{__tcsg_arg}}'\"); __tcsg_stderr.Write({helpConst}); return 2; }}");
        sb.AppendLine($"{indent}    }}");
        sb.AppendLine($"{indent}}}");

        // 必选校验（bound 标志——位置槽/选项统一）
        foreach (var parameter in command.Params.Where(p => NeedsBoundFlag(p)))
        {
            var label = parameter.Role == CommandModel.ParamRole.Option
                ? $"选项 --{parameter.LongName}"
                : $"位置参数 {parameter.Name}";
            sb.AppendLine($"{indent}if (!{BoundFlag(parameter)}) {{ __tcsg_stderr.WriteLine(\"缺少{label}\"); __tcsg_stderr.Write({helpConst}); return 2; }}");
        }

        // body 读取（stdin 全量字节——Unix 惯例，零新选项）。
        // json 缺省 = 生成 JsonContext（#484——body 类型编译期自动登记）；消费方自建 context 未登记
        // 该类型 → 语法错误退出（stdin 读取前拦截 fail-fast 不吞输入流）；JSON null 绑非可空声明同口径
        if (bodyParam is not null)
        {
            var bodyType = bodyParam.TypeDisplay.Replace("global::", string.Empty);
            sb.AppendLine($"{indent}var __tcsg_ti = __tcsg_json.GetTypeInfo(typeof({bodyParam.TypeOfDisplay}));");
            sb.AppendLine($"{indent}if (__tcsg_ti is null) {{ __tcsg_stderr.WriteLine(\"body 类型 {bodyType} 未注册于 JsonSerializerContext（需 [JsonSerializable(typeof({bodyType}))]）\"); return 2; }}");
            sb.AppendLine($"{indent}byte[] __tcsg_bodyBytes;");
            sb.AppendLine($"{indent}using (var __tcsg_stdin = global::System.Console.OpenStandardInput())");
            sb.AppendLine($"{indent}using (var __tcsg_ms = new global::System.IO.MemoryStream())");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    __tcsg_stdin.CopyTo(__tcsg_ms);");
            sb.AppendLine($"{indent}    __tcsg_bodyBytes = __tcsg_ms.ToArray();");
            sb.AppendLine($"{indent}}}");
            sb.AppendLine($"{indent}try");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    var __tcsg_deserialized = global::System.Text.Json.JsonSerializer.Deserialize(__tcsg_bodyBytes, __tcsg_ti);");
            if (!bodyParam.AllowsNull)
            {
                sb.AppendLine($"{indent}    if (__tcsg_deserialized is null) {{ __tcsg_stderr.WriteLine(\"body 不能为 null\"); return 2; }}");
            }
            sb.AppendLine($"{indent}    {bodyParam.Name} = ({bodyParam.TypeDisplay})__tcsg_deserialized!;");
            sb.AppendLine($"{indent}}}");
            sb.AppendLine($"{indent}catch (global::System.Text.Json.JsonException __tcsg_ex) {{ __tcsg_stderr.WriteLine($\"body JSON 解析失败: {{__tcsg_ex.Message}}\"); return 2; }}");
        }

        // 调用（按返回形态分流 await/直调）+ 渲染 + 异常隔离
        var invokeArgs = string.Join(", ", command.Params.Select(p =>
            p.Kind == CommandModel.BindKind.CancellationToken ? "__tcsg_ct" : p.Name));
        sb.AppendLine($"{indent}try");
        sb.AppendLine($"{indent}{{");
        switch (command.Return)
        {
            case CommandModel.ReturnKind.Void:
            case CommandModel.ReturnKind.Task:
            case CommandModel.ReturnKind.ValueTask:
                var awaitVoid = command.Return == CommandModel.ReturnKind.Void ? string.Empty : "await ";
                sb.AppendLine($"{indent}    {awaitVoid}{container}.{command.MethodName}({invokeArgs});");
                sb.AppendLine($"{indent}    return 0;");
                break;
            default:
                var awaitPrefix = command.Return == CommandModel.ReturnKind.Sync ? string.Empty : "await ";
                sb.AppendLine($"{indent}    var __tcsg_r = {awaitPrefix}{container}.{command.MethodName}({invokeArgs});");
                if (command.ResultSimple)
                {
                    if (command.ResultTypeDisplay == "string")
                        sb.AppendLine($"{indent}    __tcsg_stdout.WriteLine(__tcsg_r);");
                    else if (command.ResultTypeDisplay.EndsWith("?", System.StringComparison.Ordinal))
                        sb.AppendLine($"{indent}    __tcsg_stdout.WriteLine(__tcsg_r?.ToString() ?? string.Empty);");
                    else
                        sb.AppendLine($"{indent}    __tcsg_stdout.WriteLine(__tcsg_r.ToString());");
                }
                else
                {
                    sb.AppendLine($"{indent}    __tcsg_stdout.WriteLine(__tcsg_results.Render(__tcsg_r));");
                }

                sb.AppendLine($"{indent}    return 0;");
                break;
        }

        sb.AppendLine($"{indent}}}");
        sb.AppendLine($"{indent}catch (global::System.Exception __tcsg_ex) {{ __tcsg_stderr.WriteLine(__tcsg_ex.Message); return 1; }}");
        return sb.ToString();
    }

    /// <summary>位置槽序号（必选校验的 pos 阈值）。</summary>
    private static int SlotIndex(CommandModel.ParamModel parameter, List<CommandModel.ParamModel> positional)
        => positional.IndexOf(parameter);

    /// <summary>选项赋值（裸 bool / 缺值报错 / 类型化解析）。</summary>
    private static void EmitOptionAssign(StringBuilder sb, string indent, CommandModel.ParamModel parameter,
        string keyExpr, string valExpr, string helpConst)
    {
        if (parameter.Kind == CommandModel.BindKind.Bool)
        {
            sb.AppendLine($"{indent}if ({valExpr} is null) {parameter.Name} = true;");
            sb.AppendLine($"{indent}else if ({valExpr} == \"true\") {parameter.Name} = true;");
            sb.AppendLine($"{indent}else if ({valExpr} == \"false\") {parameter.Name} = false;");
            sb.AppendLine($"{indent}else {{ __tcsg_stderr.WriteLine($\"选项 {{{keyExpr}}} 值非法: {{{valExpr}}}\"); __tcsg_stderr.Write({helpConst}); return 2; }}");
            return;
        }

        sb.AppendLine($"{indent}string __tcsg_v_{parameter.Name};");
        sb.AppendLine($"{indent}if ({valExpr} is null)");
        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{indent}    if (__tcsg_pos + 1 >= __tcsg_args.Length) {{ __tcsg_stderr.WriteLine($\"选项 --{parameter.LongName} 缺值\"); __tcsg_stderr.Write({helpConst}); return 2; }}");
        sb.AppendLine($"{indent}    __tcsg_v_{parameter.Name} = __tcsg_args[++__tcsg_pos];");
        sb.AppendLine($"{indent}}}");
        sb.AppendLine($"{indent}else __tcsg_v_{parameter.Name} = {valExpr};");
        EmitParse(sb, indent, parameter.Kind, parameter.InnerKind, parameter.EnumFqn,
            parameter.TypeDisplay, $"__tcsg_v_{parameter.Name}", parameter.Name,
            CliErrBlock($"选项 --{parameter.LongName} 值非法: ", $"__tcsg_v_{parameter.Name}", helpConst));
    }

    /// <summary>CLI 解析失败块（stderr 提示 + 该层帮助 + 退出码 2）。</summary>
    private static string CliErrBlock(string message, string valExpr, string helpConst)
        => $"{{ __tcsg_stderr.WriteLine(\"{message}\" + {valExpr}); __tcsg_stderr.Write({helpConst}); return 2; }}";

    /// <summary>类型化解析发射（TryParse 直调零反射；失败块内 return 2——在命令函数体内生效）。</summary>
    private static void EmitParse(StringBuilder sb, string indent, CommandModel.BindKind kind,
        CommandModel.BindKind inner, string enumFqn, string typeDisplay, string valExpr, string target,
        string errBlock)
    {
        var uid = target.Replace(".", "_");
        var effective = kind == CommandModel.BindKind.Nullable ? inner : kind;
        var effectiveType = kind == CommandModel.BindKind.Nullable ? GetInnerTypeDisplay(typeDisplay) : typeDisplay;
        var tmp = $"__tcsg_t_{uid}";
        var err = errBlock;
        switch (effective)
        {
            case CommandModel.BindKind.String:
                sb.AppendLine($"{indent}{target} = {valExpr};");
                break;
            case CommandModel.BindKind.Bool:
                sb.AppendLine($"{indent}if (string.Equals({valExpr}, \"true\", global::System.StringComparison.OrdinalIgnoreCase)) {target} = true;");
                sb.AppendLine($"{indent}else if (string.Equals({valExpr}, \"false\", global::System.StringComparison.OrdinalIgnoreCase)) {target} = false;");
                sb.AppendLine($"{indent}else {err}");
                break;
            case CommandModel.BindKind.Integer:
            case CommandModel.BindKind.Floating:
                var styles = effective == CommandModel.BindKind.Integer ? "Integer" : "Float";
                sb.AppendLine($"{indent}if (!{effectiveType}.TryParse({valExpr}, global::System.Globalization.NumberStyles.{styles}, global::System.Globalization.CultureInfo.InvariantCulture, out var {tmp})) {err}");
                sb.AppendLine($"{indent}else {target} = {tmp};");
                break;
            case CommandModel.BindKind.Guid:
                sb.AppendLine($"{indent}if (!global::System.Guid.TryParse({valExpr}, out var {tmp})) {err}");
                sb.AppendLine($"{indent}else {target} = {tmp};");
                break;
            case CommandModel.BindKind.DateTimeOffset:
                sb.AppendLine($"{indent}if (!global::System.DateTimeOffset.TryParse({valExpr}, global::System.Globalization.CultureInfo.InvariantCulture, global::System.Globalization.DateTimeStyles.None, out var {tmp})) {err}");
                sb.AppendLine($"{indent}else {target} = {tmp};");
                break;
            case CommandModel.BindKind.Enum:
                sb.AppendLine($"{indent}if (!global::System.Enum.TryParse<{enumFqn}>({valExpr}, true, out {enumFqn} {tmp}) || !global::System.Enum.IsDefined(typeof({enumFqn}), {tmp})) {err}");
                sb.AppendLine($"{indent}else {target} = {tmp};");
                break;
        }
    }

    private static string GetInnerTypeDisplay(string typeDisplay)
        => typeDisplay.EndsWith("?", StringComparison.Ordinal)
            ? typeDisplay.Substring(0, typeDisplay.Length - 1)
            : typeDisplay.StartsWith("System.Nullable<", StringComparison.Ordinal) && typeDisplay.EndsWith(">", StringComparison.Ordinal)
                ? typeDisplay.Substring("System.Nullable<".Length, typeDisplay.Length - "System.Nullable<".Length - 1)
                : typeDisplay;
}
