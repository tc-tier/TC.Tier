namespace TC.Tier.Core.IO.Shared;

/// <summary>
/// 简单通配匹配器（filesystem-root-space-design §3.5）——对齐 BCL <c>MatchType.Simple</c> 语义：
/// <c>*</c> 任意字符序列 / <c>?</c> 单字符；Ordinal 比较；无转义、无字符类。
/// <para>★ 匹配目标 = 条目<b>最终组件名</b>（非整路径）；Mem/Remote（客户端过滤）与 Disk（BCL 原生
///   EnumerationOptions.MatchType.Simple）三介质同一语义——契约测试同集断言。</para>
/// </summary>
internal static class PathPattern
{
    /// <summary>校验 pattern（枚举族入口共用）——null/空拒绝（缺省 "*" 由调用方/参数默认值表达）。</summary>
    /// <param name="pattern">通配模式。</param>
    /// <exception cref="ArgumentException">pattern 为 null/空。</exception>
    public static void Validate(string? pattern)
    {
        if (string.IsNullOrEmpty(pattern))
            throw new ArgumentException("匹配模式不能为空（缺省 \"*\"）。", nameof(pattern));
    }

    /// <summary>通配匹配（双指针回溯——'*' 记位回退；O(n·p) 最坏，名字级输入无压力）。</summary>
    /// <param name="name">条目名（通常为最终组件名）。</param>
    /// <param name="pattern">通配模式（'*' 任意序列 / '?' 单字符，Ordinal）。</param>
    /// <returns>true = name 匹配 pattern；false = 不匹配。</returns>
    public static bool IsMatch(ReadOnlySpan<char> name, ReadOnlySpan<char> pattern)
    {
        var n = 0;          // name 游标
        var p = 0;          // pattern 游标
        var starName = -1;  // 最近 '*' 时的 name 位置
        var starPat = -1;   // 最近 '*' 后的 pattern 位置
        while (n < name.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == name[n]))
            {
                n++;
                p++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starPat = p++;
                starName = n;
            }
            else if (starPat >= 0)
            {
                p = starPat + 1;      // 回退到 '*' 后重试（吃进一个字符）
                n = ++starName;
            }
            else
            {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == '*') p++;   // 尾部 '*' 全消耗
        return p == pattern.Length;
    }

    /// <summary>sidecar 伴生名（§3.6 元数据回退通道）：同目录点前缀——"a/b/data.0" → "a/b/.data.0"。</summary>
    /// <param name="path">原文件相对路径。</param>
    /// <returns>伴生文件相对路径（末组件前加 '.'）。</returns>
    public static string SidecarOf(string path)
    {
        var last = path.LastIndexOf('/');
        return last < 0
            ? "." + path
            : string.Concat(path.AsSpan(0, last + 1), ".", path.AsSpan(last + 1));
    }

    /// <summary>
    /// 隐藏类判定（§3.5 评审修订）：相对路径<b>任一组件</b>以 <c>.</c> 开头 → 枚举不可见
    /// （含隐藏子树：<c>a/.b/c</c> 整支）。豁免由调用方判定（pattern 首字符 <c>.</c>）。
    /// </summary>
    /// <param name="relativeName">相对路径（'/' 分隔，逐组件检查）。</param>
    /// <returns>true = 任一组件以 '.' 开头（枚举默认不可见）；false = 无隐藏组件。</returns>
    public static bool IsHiddenRelative(ReadOnlySpan<char> relativeName)
    {
        var start = 0;
        for (var i = 0; i <= relativeName.Length; i++)
        {
            if (i != relativeName.Length && relativeName[i] != '/') continue;
            if (i > start && relativeName[start] == '.') return true;
            start = i + 1;
        }
        return false;
    }

    /// <summary>枚举调用的隐藏豁免判定（A 方案）：pattern 首字符 <c>.</c> = 显式查看隐藏类。</summary>
    /// <param name="pattern">通配模式。</param>
    /// <returns>true = 隐藏类豁免（枚举可见隐藏条目）；false = 默认隐藏。</returns>
    public static bool HiddenExempt(ReadOnlySpan<char> pattern)
        => pattern.Length > 0 && pattern[0] == '.';
}
