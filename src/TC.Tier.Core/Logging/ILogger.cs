namespace TC.Tier.Core.Logging;

/// <summary>
/// Kernel 日志接口 — 替代 Microsoft.Extensions.Logging.ILogger。
/// <para>内核代码通过此接口记录日志，默认 <see cref="NullLogger"/> 零开销。</para>
/// <para>★ 简化签名：去掉 M.E.Logging 的泛型 <c>TState</c> + <c>formatter</c>——内核只做文本日志，
///   结构化参数由 <see cref="LoggerExtensions"/> 的扩展方法用 <c>string.Format</c> 先格式化再传入。</para>
/// </summary>
public interface ILogger
{
    /// <summary>记录一条日志。</summary>
    /// <param name="logLevel">日志级别。</param>
    /// <param name="message">已格式化的消息文本。</param>
    /// <param name="exception">关联异常（可空）。</param>
    void Log(LogLevel logLevel, string message, Exception? exception = null);

    /// <summary>指定级别是否启用（热路径短路用）。</summary>
    /// <param name="logLevel">日志级别。</param>
    /// <returns>是否启用指定级别的日志。</returns>
    bool IsEnabled(LogLevel logLevel);
}