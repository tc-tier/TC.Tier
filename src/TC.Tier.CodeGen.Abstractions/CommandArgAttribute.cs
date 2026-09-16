namespace TC.Tier.CodeGen;

/// <summary>
/// 位置参数标注——CLI 有序、HTTP 取自路由段（#435 命令源生成）。
/// <para>类型集 = string/bool/int/uint/long/ulong/double/decimal/Guid/DateTimeOffset/任意枚举
///   及其 Nullable&lt;T&gt; 形态（编译期 TryParse 直调零反射）；有 C# 默认值 = 可选，无 = 必选。</para>
/// </summary>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class CommandArgAttribute : Attribute
{
    /// <summary>构造（位置必填，从 0 起；重复 = 编译期诊断 TCSG054）。</summary>
    /// <param name="position">位置序号（从 0 起）。</param>
    public CommandArgAttribute(int position) => Position = position;

    /// <summary>位置序号（从 0 起；重复 = 编译期诊断）。</summary>
    public int Position { get; }
}
