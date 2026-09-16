using System.Collections.Concurrent;
using TC.Tier.Core.Metrics;
using TC.Tier.Core.Observability;

namespace TC.Tier.Core.Net.Tests.Fixtures;

/// <summary>
/// 测试捕获 sink（spec-12 §9.1：测试 = 捕获 sink 断言——视图唯一接入点的验收面）。
/// 记录全部 Counter/Histogram 事件，按量纲名 + 可选 tag 过滤计数。
/// </summary>
public sealed class RecordingMetricsSink : IMetricsSink
{
    private readonly ConcurrentQueue<(string Name, KeyValuePair<string, string>[] Tags)> _counters = new();
    private readonly ConcurrentQueue<(string Name, double Value)> _histograms = new();
    private readonly ConcurrentQueue<(string Name, double Value)> _gauges = new();

    /// <inheritdoc/>
    public bool IsEnabled => true;

    /// <inheritdoc/>
    public void Counter(string name, ReadOnlySpan<KeyValuePair<string, string>> tags)
        => _counters.Enqueue((name, tags.ToArray()));

    /// <inheritdoc/>
    public void Histogram(string name, double value, ReadOnlySpan<KeyValuePair<string, string>> tags)
        => _histograms.Enqueue((name, value));

    /// <inheritdoc/>
    public void Gauge(string name, double value, ReadOnlySpan<KeyValuePair<string, string>> tags)
        => _gauges.Enqueue((name, value));

    /// <summary>最近一次 gauge 值（name 过滤；无记录 = null）。</summary>
    public double? LastGaugeOf(string name)
    {
        double? last = null;
        foreach (var (gaugeName, value) in _gauges)
            if (gaugeName == name) last = value;
        return last;
    }

    /// <summary>量纲计数（可选 tag 过滤——键值都匹配）。</summary>
    public long CountOf(string name, string? tagKey = null, string? tagValue = null)
    {
        long count = 0;
        foreach (var (counterName, tags) in _counters)
        {
            if (counterName != name) continue;
            if (tagKey is not null && !tags.Contains(new KeyValuePair<string, string>(tagKey, tagValue!))) continue;   // CS8604：tagValue 测试上下文恒非空
            count++;
        }
        return count;
    }

    /// <summary>直方图样本求和（诊断）。</summary>
    public IReadOnlyList<(string Name, double Value)> Histograms => _histograms.ToList();

    /// <summary>清空（测试内复用）。</summary>
    public void Reset()
    {
        _counters.Clear();
        _histograms.Clear();
    }

    /// <summary>装配 Hub（Metrics 全开——帧量采样 100%）。</summary>
    public ObservabilityHub ToHub()
        => ObservabilityHub.Create(this, null, new ObservabilityOptions
        {
            Metrics = new MetricsConfig { Enabled = true },
        });
}
