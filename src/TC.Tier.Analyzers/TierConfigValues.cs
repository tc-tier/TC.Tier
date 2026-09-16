using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Diagnostics;

namespace TC.Tier.Analyzers;

/// <summary>
/// tier_layer.* / tier_forbidden.* 配置键的共享解析工具——值编码契约：
/// 单键值内 <c>;</c> 分隔多条规则、<c>|</c> 列举多目标、空白容忍、箭头对 <c>左 =&gt; 右1 | 右2</c>。
/// 解析不抛异常——坏语法逐条产出错误串，由调用方以 TCSG139 fail-fast 上报（配置错误绝静默）。
/// </summary>
internal static class TierConfigValues
{
    /// <summary>读取一个全局配置键；键存在时返回 true。</summary>
    public static bool TryGet(AnalyzerConfigOptions options, string key, out string value)
        => options.TryGetValue(key, out value!);

    /// <summary>按 <c>|</c> / <c>;</c> 分隔符拆分列表值（去空项、容忍空白——ns2.0 无 TrimEntries，手工去空白）。</summary>
    public static ImmutableArray<string> SplitList(string value)
        => SplitTrimmed(value, ['|', ';']);

    /// <summary>按 <c>;</c> 拆分多条箭头规则（去空项、容忍空白）。</summary>
    public static ImmutableArray<string> SplitRules(string value)
        => SplitTrimmed(value, [';']);

    private static ImmutableArray<string> SplitTrimmed(string value, char[] separators)
    {
        var parts = value.Split(separators, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return ImmutableArray<string>.Empty;

        var builder = ImmutableArray.CreateBuilder<string>(parts.Length);
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 0) builder.Add(trimmed);
        }

        return builder.ToImmutable();
    }

    /// <summary>解析单条箭头规则 <c>左 =&gt; 右1 | 右2</c>——缺箭头 = 坏语法（error 产出）。</summary>
    public static bool TryParseArrow(string rule, out string left, out ImmutableArray<string> rights, out string? error)
    {
        left = string.Empty;
        rights = default;
        error = null;

        var arrowIndex = rule.IndexOf("=>", StringComparison.Ordinal);
        if (arrowIndex <= 0 || arrowIndex + 2 >= rule.Length)
        {
            error = $"规则缺少 '=>' 箭头对：'{rule}'";
            return false;
        }

        left = rule.Substring(0, arrowIndex).Trim();
        rights = SplitList(rule.Substring(arrowIndex + 2));
        if (left.Length == 0 || rights.IsEmpty)
        {
            error = $"规则箭头对左值或右值为空：'{rule}'";
            return false;
        }

        return true;
    }

    /// <summary>左值匹配语义：以 <c>*</c> 结尾 = 前缀通配；否则精确匹配。</summary>
    public static bool LeftMatches(string left, string candidate)
    {
        if (left.Length > 0 && left[left.Length - 1] == '*')
        {
            var prefix = left.Substring(0, left.Length - 1);
            return candidate.StartsWith(prefix, StringComparison.Ordinal);
        }

        return string.Equals(left, candidate, StringComparison.Ordinal);
    }

    /// <summary>命名空间作用域匹配：相等或以其为前缀（<c>Ns.Sub</c> 形态）。</summary>
    public static bool NamespaceMatches(string prefix, string target)
        => target == prefix || target.StartsWith(prefix + ".", StringComparison.Ordinal);
}
