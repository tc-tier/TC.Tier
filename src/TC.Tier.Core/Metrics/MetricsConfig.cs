namespace TC.Tier.Core.Metrics;

/// <summary>
/// 指标配置（独立于链路/日志，对齐 OTel MeterProvider）。
/// <para>★ 三信号各自独立配置。默认 Enabled=false（零开销）。</para>
/// <para>★ 维度级开关：Enabled=true 后，可精细控制哪些维度采集。</para>
/// </summary>
public sealed class MetricsConfig
{
    /// <summary>总开关（默认 false = 零开销）。</summary>
    public bool Enabled { get; init; }

    /// <summary>采样率百分比（100=全采，10=采10%，0=不采）。默认 100。</summary>
    public int SampleRate { get; init; } = 100;

    /// <summary>Storage Engine IO 维度（Read/Write/Flush/Compact/Throttle）。默认 true。</summary>
    public bool EnableStorageMetrics { get; init; } = true;

    /// <summary>Log 结构维度（Append/Commit/Truncate/Recover）。默认 true。</summary>
    public bool EnableLogMetrics { get; init; } = true;

    /// <summary>Index 结构维度（Find/Insert/Upsert/Delete/Scan）。默认 true。</summary>
    public bool EnableIndexMetrics { get; init; } = true;

    /// <summary>Segment 分配器维度（段表专用）。默认 false（高频，建议按需开）。</summary>
    public bool EnableSegmentAllocatorMetrics { get; init; }
}
