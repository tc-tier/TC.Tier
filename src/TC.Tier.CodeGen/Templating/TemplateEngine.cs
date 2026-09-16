using System.Collections.Generic;
using System.Text;

namespace TC.Tier.CodeGen.Templating;

/// <summary>
/// 极简模板引擎——{{TOKEN}} 占位符替换，零控制流：条件/循环留在 C# 编排层，
/// 空白与缩进由模板字面呈现（WYSIWYG），不存在模板引擎的空白控制坑。
/// <para>★ 严格模式：模板里出现的每个 token 必须在值表里有值（缺失 = 生成器内部缺陷，抛异常
///   fail-fast）；值表里多出的键无害；空值渲染为空串。</para>
/// <para>★ 全仓生成器共用底座：BinaryLayout/RingKey/TierFs/ConstantRegistry 四生成器统一走此组件，
///   新生成器不再拼接字符串。</para>
/// </summary>
internal static class TemplateEngine
{
    /// <summary>按模板名渲染（TemplateSet 取文 + 严格替换——生成器主入口）。</summary>
    /// <param name="templateName">模板键（Templates 资源路径去前缀去后缀、'.'→'/'，如 "BinaryLayout/CodecClass"）。</param>
    /// <param name="values">token 值表（Ordinal 精确匹配；模板中出现的每个 token 必须有值，缺失即抛）。</param>
    /// <returns>替换后的完整文本。</returns>
    public static string Render(string templateName, IReadOnlyDictionary<string, string?> values)
        => Render(TemplateSet.Get(templateName), values, templateName);

    /// <summary>按模板名逐行渲染同一模板并拼接（重复片段——缩进由模板自带）。</summary>
    /// <param name="templateName">模板键。</param>
    /// <param name="rows">逐行 token 值表序列（每行独立替换后拼接）。</param>
    /// <returns>所有行渲染结果的拼接文本。</returns>
    public static string RenderEach(string templateName, IEnumerable<IReadOnlyDictionary<string, string?>> rows)
        => RenderEach(TemplateSet.Get(templateName), rows, templateName);

    /// <summary>token 值表构造（Ordinal 精确匹配——与 Render 的查表口径一致）。</summary>
    /// <param name="pairs">(键, 值) 元组数组；同键后者覆盖前者。</param>
    /// <returns>Ordinal 比较的 token 字典（容量预分配 = pairs.Length）。</returns>
    public static Dictionary<string, string?> Tokens(params (string Key, string? Value)[] pairs)
    {
        var tokens = new Dictionary<string, string?>(pairs.Length, StringComparer.Ordinal);
        foreach (var (key, value) in pairs)
            tokens[key] = value;
        return tokens;
    }

    /// <summary>单次渲染：把模板中的 {{TOKEN}} 逐一替换为值表内容。</summary>
    /// <param name="template">模板文本（含 {{TOKEN}} 占位符）。</param>
    /// <param name="values">token 值表（模板中出现的每个 token 必须有值，缺失即抛 InvalidOperationException）。</param>
    /// <param name="templateName">模板名（仅用于异常信息——定位未闭合/未提供值的 token）。</param>
    /// <returns>替换后的完整文本。</returns>
    public static string Render(
        string template, IReadOnlyDictionary<string, string?> values, string templateName)
    {
        var sb = new StringBuilder(template.Length);
        int position = 0;
        while (position < template.Length)
        {
            int open = template.IndexOf("{{", position, StringComparison.Ordinal);
            if (open < 0)
            {
                sb.Append(template, position, template.Length - position);
                break;
            }
            int close = template.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0)
                throw new System.InvalidOperationException(
                    $"模板 '{templateName}' 存在未闭合的 '{{{{'（位置 {open}）。");
            sb.Append(template, position, open - position);
            var token = template.Substring(open + 2, close - open - 2);
            if (!values.TryGetValue(token, out var value))
                throw new System.InvalidOperationException(
                    $"模板 '{templateName}' 的 token '{token}' 未提供值——生成器内部缺陷。");
            sb.Append(value);
            position = close + 2;
        }
        return sb.ToString();
    }

    /// <summary>同一模板逐行渲染并拼接（重复片段——缩进由模板自带）。</summary>
    /// <param name="template">模板文本。</param>
    /// <param name="rows">逐行 token 值表序列（每行独立替换后拼接）。</param>
    /// <param name="templateName">模板名（仅用于异常信息）。</param>
    /// <returns>所有行渲染结果的拼接文本。</returns>
    public static string RenderEach(
        string template, IEnumerable<IReadOnlyDictionary<string, string?>> rows, string templateName)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
            sb.Append(Render(template, row, templateName));
        return sb.ToString();
    }
}
