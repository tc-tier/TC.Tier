namespace TC.Tier.Core.Logging;

/// <summary>
/// Logger 工厂接口 — 创建指定分类名的 Logger。
/// </summary>
public interface ILoggerFactory
{
    ILogger CreateLogger(string categoryName);
}