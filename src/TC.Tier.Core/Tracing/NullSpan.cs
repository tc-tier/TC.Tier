namespace TC.Tier.Core.Tracing;

/// <summary>
/// 零开销空 Span —— NullTracer.BeginSpan 返回此单例。
/// <para>所有方法空实现，Dispose 空操作。可安全用于 <c>using var span = tracer.BeginSpan(...)</c>。</para>
/// </summary>
public sealed class NullSpan : ISpan
{
    /// <summary>全局共享单例（无状态，安全并发使用）。</summary>
    public static readonly NullSpan Instance = new();
    private NullSpan() { }
    /// <inheritdoc/>
    /// <param name="key">标签名（如 "entry.size"、"page.number"）。本实现为 no-op。</param>
    /// <param name="value">标签值，可为 null。本实现为 no-op。</param>
    public void SetTag(string key, string? value) { }
    /// <inheritdoc/>
    /// <param name="key">标签名（如 "entry.size"、"page.number"）。本实现为 no-op。</param>
    /// <param name="value">标签数值。本实现为 no-op。</param>
    public void SetTag(string key, long value) { }
    /// <inheritdoc/>
    /// <param name="ex">要记录的异常对象。本实现为 no-op。</param>
    public void RecordException(Exception ex) { }
    /// <inheritdoc/>
    /// <param name="status">span 状态（Ok/Error）。本实现为 no-op。</param>
    /// <param name="description">可选状态描述，默认 null。本实现为 no-op。</param>
    public void SetStatus(SpanStatus status, string? description = null) { }
    /// <inheritdoc/>
    /// <param name="name">事件名称（如 "page.flushed"、"checkpoint.started"）。本实现为 no-op。</param>
    public void AddEvent(string name) { }
    /// <inheritdoc/>
    public void Dispose() { }
}
