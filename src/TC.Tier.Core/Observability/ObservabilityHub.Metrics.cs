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

        /// <summary>指标总开关（Options.Metrics.Enabled &amp;&amp; sink.IsEnabled 短路后的终值）。</summary>
        public bool IsEnabled => _enabled;

        /// <summary>计数器 —— 单调递增的累计计数（disabled 时零开销短路）。</summary>
        /// <param name="name">指标名（点号分层 + 单位后缀）。</param>
        /// <param name="tags">标签（维度切分）。</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Counter(string name, ReadOnlySpan<KeyValuePair<string, string>> tags)
        { if (_enabled) _sink.Counter(name, tags); }

        /// <summary>直方图 —— 值分布记录（延迟、大小；disabled 时零开销短路）。</summary>
        /// <param name="name">指标名（带单位后缀）。</param>
        /// <param name="value">本次记录的值。</param>
        /// <param name="tags">标签（维度切分）。</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Histogram(string name, double value, ReadOnlySpan<KeyValuePair<string, string>> tags)
        { if (_enabled) _sink.Histogram(name, value, tags); }

        /// <summary>仪表 —— 瞬时值（可增可减；disabled 时零开销短路）。</summary>
        /// <param name="name">指标名。</param>
        /// <param name="value">当前值。</param>
        /// <param name="tags">标签（维度切分）。</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Gauge(string name, double value, ReadOnlySpan<KeyValuePair<string, string>> tags)
        { if (_enabled) _sink.Gauge(name, value, tags); }
    }
}
