namespace TC.Tier.Core.Metrics;

/// <summary>
/// 指标接收器底层原语契约 —— 三原语模型（对齐 OpenTelemetry Meter）。
/// <para>外部监控适配器（Prometheus / Datadog / OTel）只需实现这三个方法 + <see cref="IsEnabled"/>，
/// 不必认识任何上层语义方法（OnRead/OnCheckpoint 等）。</para>
/// <para>★ 三原语语义：</para>
/// <list type="bullet">
/// <item><term>Counter</term><description>单调递增计数（发生次数：throttle、bufferFull、error）。</description></item>
/// <item><term>Histogram</term><description>值分布（延迟、大小：read latency、payload size、duration）。</description></item>
/// <item><term>Gauge</term><description>瞬时值，可增可减（当前队列深度、内存使用率、freeList 深度）。</description></item>
/// </list>
/// <para>★ 默认实现 <see cref="NullMetricsSink"/>：全空方法 + <c>IsEnabled=false</c>，热路径零开销。</para>
/// <para>★ public —— 供上层注入实现，对接外部监控系统。</para>
/// </summary>
public interface IMetricsSink
{
    /// <summary>
    /// 是否启用指标采集（热路径零开销的关键开关）。
    /// <para>NullMetricsSink 返回 false → 热路径 <c>if (_hub.Metrics.IsEnabled)</c> 完全短路，
    /// 不调任何原语方法。</para>
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// 计数器 —— 单调递增的累计计数。
    /// <para>典型场景：throttle 次数、buffer full 次数、page allocate 次数、error 次数。</para>
    /// </summary>
    /// <param name="name">指标名（点号分层 + 单位后缀，如 <c>"device.throttle"</c>）。</param>
    /// <param name="tags">标签（维度切分，如 <c>[("kind", "wal")]</c>）。</param>
    void Counter(string name, ReadOnlySpan<KeyValuePair<string, string>> tags);

    /// <summary>
    /// 直方图 —— 值分布记录（延迟、大小）。
    /// <para>典型场景：read latency（μs）、payload size（bytes）、checkpoint duration（ms）。</para>
    /// </summary>
    /// <param name="name">指标名（带单位后缀，如 <c>"device.read.latency_us"</c>）。</param>
    /// <param name="value">本次记录的值。</param>
    /// <param name="tags">标签。</param>
    void Histogram(string name, double value, ReadOnlySpan<KeyValuePair<string, string>> tags);

    /// <summary>
    /// 仪表 —— 瞬时值（可增可减）。
    /// <para>典型场景：inflight IO 队列深度、freeList 深度、heap usage ratio。</para>
    /// </summary>
    /// <param name="name">指标名（如 <c>"device.queue_depth"</c>）。</param>
    /// <param name="value">当前值。</param>
    /// <param name="tags">标签。</param>
    void Gauge(string name, double value, ReadOnlySpan<KeyValuePair<string, string>> tags);
}
