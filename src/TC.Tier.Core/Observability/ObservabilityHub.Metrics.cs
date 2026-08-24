using System.Runtime.CompilerServices;

namespace TC.Tier.Core.Observability;

public sealed partial class ObservabilityHub
{
    /// <summary>三原语公共视图 —— 镜像 <see cref="IMetricsSink"/>，带 IsEnabled 短路。</summary>
    public sealed partial class MetricsView
    {
        private readonly IMetricsSink _sink;
        private readonly bool _enabled;

        internal MetricsView(IMetricsSink sink, bool enabled)
        { _sink = sink; _enabled = enabled; }

        public bool IsEnabled => _enabled;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Counter(string name, ReadOnlySpan<KeyValuePair<string, string>> tags)
        { if (_enabled) _sink.Counter(name, tags); }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Histogram(string name, double value, ReadOnlySpan<KeyValuePair<string, string>> tags)
        { if (_enabled) _sink.Histogram(name, value, tags); }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Gauge(string name, double value, ReadOnlySpan<KeyValuePair<string, string>> tags)
        { if (_enabled) _sink.Gauge(name, value, tags); }
    }
}
