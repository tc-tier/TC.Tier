namespace TC.Tier.Core.Tracing;

/// <summary>
/// Span 状态 —— 参照 OpenTelemetry。
/// </summary>
public enum SpanStatus
{
    /// <summary>操作成功完成。</summary>
    Ok,
    /// <summary>操作出错（配合 RecordException + 描述）。</summary>
    Error
}