namespace TC.Tier.CodeGen;

/// <summary>
/// 叶子命令标注——命令组内的一条子命令（#435 命令源生成）。
/// <para>方法签名契约：返回 ValueTask&lt;T&gt;/Task&lt;T&gt;/ValueTask/Task/T/void 全支持；
///   <see cref="System.Threading.CancellationToken"/> 参数框架注入不参与绑定；其余业务参数必须标注
///   <see cref="CommandArgAttribute"/> / <see cref="CommandOptionAttribute"/> / <see cref="CommandBodyAttribute"/> 之一。</para>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class CommandAttribute : Attribute
{
    /// <summary>构造（命令名必填——组内唯一，重名 = 编译期诊断 TCSG054）。</summary>
    /// <param name="name">子命令名（组内唯一；空名/含路径分隔符 = TCSG055）。</param>
    public CommandAttribute(string name) => Name = name;

    /// <summary>子命令名（CLI 输入 token / HTTP 路径段）。</summary>
    public string Name { get; }

    /// <summary>命令描述——帮助文本。</summary>
    public string? Description { get; init; }

    /// <summary>HTTP 路由覆写（须以 / 起头）；缺省 = /{各级组名}/{命令名}。</summary>
    public string? Route { get; init; }

    /// <summary>HTTP 方法："GET" | "POST"（缺省 POST——执行动作语义；GET 禁 body 参数 = TCSG058）。</summary>
    public string Method { get; init; } = "POST";
}
