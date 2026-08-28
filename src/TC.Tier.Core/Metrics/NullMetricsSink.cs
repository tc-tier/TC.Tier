namespace TC.Tier.Core.Metrics;

/// <summary>
/// 零开销空指标接收器 —— 默认实现，三原语全空方法体。
/// <para>生产路径默认用此（无监控注入时）。所有方法内联消除（JIT/AOT 友好）。</para>
/// </summary>
public sealed class NullMetricsSink : IMetricsSink
{
    /// <summary>全局共享单例（无状态，安全并发使用）。</summary>
    public static readonly NullMetricsSink Instance = new();
    private NullMetricsSink() { }

    /// <summary>零开销：热路径 if(IsEnabled) 完全短路。</summary>
    public bool IsEnabled => false;

    /// <summary>计数器 —— 空操作（零开销丢弃）。</summary>
    /// <param name="name">指标名（忽略）。</param>
    /// <param name="tags">标签（忽略）。</param>
    public void Counter(string name, ReadOnlySpan<KeyValuePair<string, string>> tags) { }
    /// <summary>直方图 —— 空操作（零开销丢弃）。</summary>
    /// <param name="name">指标名（忽略）。</param>
    /// <param name="value">记录值（忽略）。</param>
    /// <param name="tags">标签（忽略）。</param>
    public void Histogram(string name, double value, ReadOnlySpan<KeyValuePair<string, string>> tags) { }
    /// <summary>仪表 —— 空操作（零开销丢弃）。</summary>
    /// <param name="name">指标名（忽略）。</param>
    /// <param name="value">当前值（忽略）。</param>
    /// <param name="tags">标签（忽略）。</param>
    public void Gauge(string name, double value, ReadOnlySpan<KeyValuePair<string, string>> tags) { }
}
