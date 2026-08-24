namespace TC.Tier.Core.Metrics;

/// <summary>
/// 零开销空指标接收器 —— 默认实现，三原语全空方法体。
/// <para>生产路径默认用此（无监控注入时）。所有方法内联消除（JIT/AOT 友好）。</para>
/// </summary>
public sealed class NullMetricsSink : IMetricsSink
{
    public static readonly NullMetricsSink Instance = new();
    private NullMetricsSink() { }

    /// <summary>零开销：热路径 if(IsEnabled) 完全短路。</summary>
    public bool IsEnabled => false;

    public void Counter(string name, ReadOnlySpan<KeyValuePair<string, string>> tags) { }
    public void Histogram(string name, double value, ReadOnlySpan<KeyValuePair<string, string>> tags) { }
    public void Gauge(string name, double value, ReadOnlySpan<KeyValuePair<string, string>> tags) { }
}
