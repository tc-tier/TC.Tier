namespace TC.Tier.CodeGen;

/// <summary>
/// 命令组标注——组嵌套子命令的组节点（#435 命令源生成）。
/// <para>★ 根不根由结构位置判定：声明在别的 <c>[CommandGroup]</c> 类内 = 嵌套组（内节点），
///   最外层 = 根组。根组 <see cref="Name"/> = 宿主 CLI 分发 token 与 HTTP 路由首段，
///   <b>不作为 CLI 参数匹配</b>（git/docker 形态——用户输入从根子级开始）；嵌套组 Name = CLI/HTTP 路径段。</para>
/// <para>任意深度嵌套；命令方法 = <c>[Command]</c> 标注的方法。</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false)]
public sealed class CommandGroupAttribute : Attribute
{
    /// <summary>构造（位置形态——<c>[CommandGroup("traffic")]</c>）。</summary>
    /// <param name="name">组名（缺省形态走无参构造 + Name 命名实参 = 类型名 kebab-case）。</param>
    public CommandGroupAttribute(string name) => Name = name;

    /// <summary>无参构造（命名形态——<c>[CommandGroup(Name = "...")]</c> 或全缺省）。</summary>
    public CommandGroupAttribute()
    {
    }
    /// <summary>组名。缺省 = 类型名 kebab-case；根组 = 宿主分发 token + HTTP 首段（不进 CLI 参数），嵌套组 = CLI/HTTP 路径段。</summary>
    public string? Name { get; init; }

    /// <summary>组描述——帮助文本（CLI help + HTTP 描述预留）。</summary>
    public string? Description { get; init; }
}
