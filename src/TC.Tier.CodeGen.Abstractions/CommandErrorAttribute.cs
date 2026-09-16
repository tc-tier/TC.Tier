namespace TC.Tier.CodeGen;

/// <summary>
/// 业务异常 → HTTP 状态码映射（#435 命令源生成）。
/// <para>组级声明全组适用、方法级就近覆写。生成器按继承深度排序生成 catch 链（最派生优先）；
///   未声明的异常 → 500 兜底。消费方经 <see cref="ICommandResults.WriteError(int, Exception)"/>
///   模式匹配自家异常提取错误码——零反射。</para>
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true)]
public sealed class CommandErrorAttribute : Attribute
{
    /// <summary>构造。</summary>
    /// <param name="exceptionType">业务异常类型（须 Exception 派生——违者编译期诊断 TCSG059）。</param>
    /// <param name="status">HTTP 状态码。</param>
    public CommandErrorAttribute(Type exceptionType, int status)
    {
        ExceptionType = exceptionType;
        Status = status;
    }

    /// <summary>业务异常类型。</summary>
    public Type ExceptionType { get; }

    /// <summary>HTTP 状态码。</summary>
    public int Status { get; }
}
