using System.Collections.Concurrent;
using TC.Tier.Core.Logging;

namespace TC.Tier.Core.Net.AdversarialTests;

/// <summary>
/// 取证内存日志（每节点环形缓冲——失败时转储最近 N 条轨迹；对抗挂死定位的常设仪器）。
/// </summary>
internal sealed class MemoryLogger(string name) : ILogger
{
    private readonly ConcurrentQueue<string> _lines = new();
    private int _count;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log(LogLevel logLevel, string message, Exception? exception = null)
    {
        if (!IsEnabled(logLevel)) return;
        var line = $"[{DateTime.UtcNow:HH:mm:ss.fff}] {message}{(exception is null ? "" : $" EX={exception.GetType().Name}:{exception.Message}")}";
        _lines.Enqueue(line);
        if (Interlocked.Increment(ref _count) > 400)
        {
            _lines.TryDequeue(out _);
            Interlocked.Decrement(ref _count);
        }
    }

    /// <summary>转储（取证——失败信息拼接）。</summary>
    public string Dump()
    {
        var lines = _lines.ToArray();
        return $"── {name} trace ({lines.Length}) ──\n" + string.Join("\n", lines);
    }
}
