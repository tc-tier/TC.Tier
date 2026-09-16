namespace TC.Tier.CodeGen;

/// <summary>
/// 结果渲染口——CLI/HTTP 两端对偶（宿主实现；推荐 System.Text.Json 泛型 source-gen 零反射）。
/// <para>★ HTTP 成功载荷走 <see cref="WriteAsync{T}"/>；用法/绑定错误走
///   <see cref="WriteError(int, string)"/>；已声明业务异常走 <see cref="WriteError(int, Exception)"/>
///   （消费方模式匹配自家异常类型提取错误码——零反射）。</para>
/// </summary>
public interface ICommandResults
{
    /// <summary>HTTP 成功载荷渲染。</summary>
    /// <typeparam name="T">命令返回类型。</typeparam>
    /// <param name="result">命令执行结果。</param>
    /// <returns>HTTP 应答。</returns>
    /// <remarks>Task 形态（ns2.0 契约面无 ValueTask——零新包依赖优先；宿主可内部换 ValueTask）。</remarks>
    global::System.Threading.Tasks.Task<CommandHttpResponse> WriteAsync<T>(T result);

    /// <summary>用法/绑定错误渲染（消息形态）。</summary>
    /// <param name="status">HTTP 状态码（400 用法/绑定失败）。</param>
    /// <param name="message">错误消息。</param>
    /// <returns>HTTP 应答。</returns>
    CommandHttpResponse WriteError(int status, string message);

    /// <summary>已声明业务异常渲染（消费方模式匹配自家异常取错误码）。</summary>
    /// <param name="status">HTTP 状态码（声明 Status；未声明异常 500 兜底）。</param>
    /// <param name="error">命令抛出的异常。</param>
    /// <returns>HTTP 应答。</returns>
    CommandHttpResponse WriteError(int status, global::System.Exception error);

    /// <summary>CLI 成功载荷渲染（表格/JSON 格式化归宿主）。</summary>
    /// <typeparam name="T">命令返回类型。</typeparam>
    /// <param name="result">命令执行结果。</param>
    /// <returns>渲染文本（直写 stdout）。</returns>
    string Render<T>(T result);
}
