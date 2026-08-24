using System.Buffers;

namespace TC.Tier.Core.IO.Shared;

/// <summary>
/// 路径验证共享规则——Disk/Mem/Remote 三介质同一实现（R5 七条，测试 ㉛ 断言集）。
/// <para>唯一入口：<see cref="ValidateRelative"/>（根空间层级相对路径——filesystem-root-space-design §4；
/// 单组件路径 = 规则子集，旧扁平文件名天然兼容）。</para>
/// <para>共享拒绝规则：null/空/纯空白；<c>..</c>/./越根；盘符/绝对路径；非法字符（\0 与 Windows 保留集
/// <c>&lt;&gt;:"|?*</c>——三介质同拒，即使 Linux 合法）；单组件 &gt;255、组合路径 &gt;4096；调用方比较一律
/// Ordinal（mem 区分大小写对齐 Linux）。</para>
/// <para>拒绝统一抛 <see cref="ArgumentException"/>（路径本身非法 = 参数错误）。</para>
/// </summary>
internal static class PathValidator
{
    /// <summary>单组件（文件名）最大长度。</summary>
    public const int MaxComponentLength = 255;

    /// <summary>组合路径（根 + 文件名）最大长度——跨平台保守上界。</summary>
    public const int MaxCombinedLength = 4096;

    private static readonly SearchValues<char> InvalidChars = SearchValues.Create("\0<>:\"|?*");

    /// <summary>校验 fs 根目录路径（fs 构造用）——须为非空。★ 根是绝对路径，盘符/UNC 合法（非法字符集只约束单组件文件名）。</summary>
    public static void ValidateRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("文件系统根目录不能为空。", nameof(root));
    }

    /// <summary>
    /// 校验<b>层级相对路径</b>（根空间模型——filesystem-root-space-design §4）——非法抛 <see cref="ArgumentException"/>。
    /// <para>规则：① null/空/纯空白拒绝；② <c>\</c> 拒绝（<c>/</c> 唯一合法分隔符）；③ 空组件拒绝
    /// （首/尾分隔符、连续分隔符）；④ <c>.</c>/<c>..</c> 组件拒绝（任何位置越根）；⑤ 盘符拒绝；
    /// ⑥ 非法字符（\0 与 Windows 保留集）；⑦ 单组件 &gt;255 / root+path &gt;4096；⑧ 比较一律 Ordinal。</para>
    /// <para>单组件路径天然合法（旧扁平文件名的规则子集——引擎既有段文件名零影响）；
    /// 点前缀组件（<c>.tier-volume-lock</c> / sidecar <c>.data.0</c>）合法。</para>
    /// </summary>
    public static void ValidateRelative(string path, string root)
    {
        // ① 空 / null / 纯空白
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("相对路径不能为空或纯空白。", nameof(path));

        // ② 反斜杠（'/' 唯一合法分隔符——S3 键/跨平台统一）
        if (path.Contains('\\'))
            throw new ArgumentException($"相对路径不允许反斜杠（'/' 唯一合法分隔符）: {path}", nameof(path));

        // ③ 空组件（首/尾分隔符、连续分隔符）
        if (path[0] == '/' || path[^1] == '/' || path.Contains("//"))
            throw new ArgumentException($"相对路径含空组件（首/尾/连续分隔符）: {path}", nameof(path));

        // ⑤ 盘符（显式声明意图——冒号同时被 ⑥ 保留集拦截）
        if (path.Length >= 2 && path[1] == ':' && char.IsLetter(path[0]))
            throw new ArgumentException($"相对路径不允许盘符: {path}", nameof(path));

        // ⑥ 非法字符（\0 + Windows 保留集——三介质同拒）
        if (path.AsSpan().IndexOfAny(InvalidChars) >= 0)
            throw new ArgumentException($"相对路径含非法字符（保留集 <>:\"|?* 或 \\0）: {path}", nameof(path));

        // ④ 逐组件：'.'/'..' 越根 + ⑦ 组件长度
        var start = 0;
        for (var i = 0; i <= path.Length; i++)
        {
            if (i != path.Length && path[i] != '/') continue;
            var len = i - start;
            if ((len == 1 && path[start] == '.') || (len == 2 && path[start] == '.' && path[start + 1] == '.'))
                throw new ArgumentException($"相对路径不允许 '.'/'..' 组件（越根）: {path}", nameof(path));
            if (len > MaxComponentLength)
                throw new ArgumentException($"路径组件超长（{len} > {MaxComponentLength}）: {path}", nameof(path));
            start = i + 1;
        }

        // ⑦ 组合长度
        if (root.Length + 1 + path.Length > MaxCombinedLength)
            throw new ArgumentException($"组合路径超长（root+path > {MaxCombinedLength}）。", nameof(path));
    }
}
