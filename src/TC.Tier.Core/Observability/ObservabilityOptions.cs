namespace TC.Tier.Core.Observability;

/// <summary>
/// 可观测性配置聚合 —— 追踪 + 指标两信号各自独立配置（对齐 OTel TracerProvider + MeterProvider）。
/// <para>★ 注入引擎/结构，由工厂分发到各子视图。</para>
/// <para>★ 日志不由 Options 控制——日志的级别/过滤由注入的 <see cref="ILoggerFactory"/> 实现自行决定。</para>
/// <para>★ 默认零开销：new ObservabilityOptions() → 追踪关、指标关。</para>
/// </summary>
public sealed class ObservabilityOptions
{
    /// <summary>链路追踪配置（默认关）。</summary>
    public TracingConfig Tracing { get; init; } = new();

    /// <summary>指标配置（默认关）。</summary>
    public MetricsConfig Metrics { get; init; } = new();

    /// <summary>全默认（零开销）单例 —— 追踪关、指标关。</summary>
    public static readonly ObservabilityOptions Default = new();
}
