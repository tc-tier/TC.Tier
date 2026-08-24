namespace TC.Tier.Core.Logging;

public sealed class NullLoggerFactory : ILoggerFactory
{
    public static readonly NullLoggerFactory Instance = new();
    public ILogger CreateLogger(string categoryName) => NullLogger.Instance;
}

public sealed class NullLogger : ILogger
{
    public static readonly NullLogger Instance = new();
    public void Log(LogLevel logLevel, string message, Exception? exception = null) { }
    public bool IsEnabled(LogLevel logLevel) => false;
}
