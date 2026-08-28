namespace TC.Tier.Core.Logging;

/// <summary>
/// Logger 工厂接口 — 创建指定分类名的 Logger。
/// </summary>
public interface ILoggerFactory
{
    /// <summary>创建指定分类名的 Logger。</summary>
    /// <param name="categoryName">Logger 分类名（通常为组件/类型全名）。</param>
    /// <returns>该分类的 Logger 实例。</returns>
    ILogger CreateLogger(string categoryName);
}