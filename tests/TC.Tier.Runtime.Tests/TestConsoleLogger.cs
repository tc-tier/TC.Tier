namespace TC.Tier.Runtime.Tests;

/// <summary>最简 logger——把警告输出到 Console（诊断 PunchHole 平台行为用）。</summary>
internal sealed class TestConsoleLogger : ILogger
{
    public static readonly TestConsoleLogger Instance = new();
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
    public void Log(LogLevel logLevel, string message, Exception? exception = null)
        => Console.WriteLine($"[TestLogger] {logLevel}: {message}" + (exception != null ? $" | {exception.Message}" : ""));
}
