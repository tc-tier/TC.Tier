namespace TC.Tier.Core.Tracing;

/// <summary>
/// 链路追踪配置（独立于指标/日志，对齐 OTel TracerProvider）。
/// <para>★ 三信号各自独立配置 —— 追踪、指标、日志是完全不同的可观测信号，
///   不应共用一个开关。每个信号有自己的 Enabled + 策略。</para>
/// <para>★ 默认 Enabled=false（零开销）：热路径只读 1 个 volatile bool（IsEnabled），false 时
///   不调 BeginSpan、不分配 span、不调 Dispose。</para>
/// <para>★ 采样策略：即便启用追踪，高吞吐热路径（每秒数十万次 Append）全采 span 会拖垮性能。
///   SampleRate 控制采样频率（100=全采，10=采10%，0=不采）。</para>
/// </summary>
public sealed class TracingConfig
{
    /// <summary>是否启用链路追踪（默认 false = 零开销）。</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// 采样率百分比（默认 100 = 全采）。
    /// <para>100 = 每次操作都 BeginSpan；10 = 每 10 次采 1 次；0 = 不采（等同 Enabled=false）。</para>
    /// <para>采样命中才 BeginSpan，未命中跳过（OTel IsRecording 语义）。</para>
    /// </summary>
    public int SampleRate { get; init; } = 100;
}
