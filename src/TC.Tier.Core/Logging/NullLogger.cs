namespace TC.Tier.Core.Logging;

/// <summary>
/// 零开销空 Logger 工厂 —— 默认实现。<see cref="CreateLogger"/> 恒返回 <see cref="NullLogger.Instance"/>。
/// </summary>
public sealed class NullLoggerFactory : ILoggerFactory
{
    /// <summary>全局共享单例（无状态，安全并发使用）。</summary>
    public static readonly NullLoggerFactory Instance = new();
    /// <summary>创建 Logger —— 忽略分类名，恒返回 <see cref="NullLogger.Instance"/>。</summary>
    /// <param name="categoryName">Logger 分类名（忽略）。</param>
    /// <returns>空 Logger 单例。</returns>
    public ILogger CreateLogger(string categoryName) => NullLogger.Instance;
}

/// <summary>
/// 零开销空 Logger —— 默认实现。<see cref="Log"/> 空操作，<see cref="IsEnabled"/> 恒 false。
/// </summary>
public sealed class NullLogger : ILogger
{
    /// <summary>全局共享单例（无状态，安全并发使用）。</summary>
    public static readonly NullLogger Instance = new();
    /// <summary>写日志 —— 空操作（零开销丢弃）。</summary>
    /// <param name="logLevel">日志级别（忽略）。</param>
    /// <param name="message">日志消息（忽略）。</param>
    /// <param name="exception">关联异常（忽略）。</param>
    public void Log(LogLevel logLevel, string message, Exception? exception = null) { }
    /// <summary>级别是否启用 —— 恒 false：热路径 <c>if(_logger.IsEnabled(level))</c> 完全短路。</summary>
    /// <param name="logLevel">日志级别（忽略）。</param>
    /// <returns>恒返回 false。</returns>
    public bool IsEnabled(LogLevel logLevel) => false;
}
