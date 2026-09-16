namespace TC.Tier.CodeGen;

/// <summary>
/// 具名选项标注——CLI <c>--name value</c> / <c>-x value</c>、HTTP 取自 query（#435 命令源生成）。
/// <para>类型集同位置参数；解析即按长名归一，短名仅作输入别名。布尔选项支持 <c>--flag</c> 裸形态。</para>
/// </summary>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class CommandOptionAttribute : Attribute
{
    /// <summary>长名。缺省 = 参数名 kebab-case。</summary>
    public string? LongName { get; init; }

    /// <summary>短名（单字符）；0 = 无短名。</summary>
    public char ShortName { get; init; }
}
