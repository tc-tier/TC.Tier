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
    public void SetTag(string key, string? value) { }
    /// <inheritdoc/>
    public void SetTag(string key, long value) { }
    /// <inheritdoc/>
    public void RecordException(Exception ex) { }
    /// <inheritdoc/>
    public void SetStatus(SpanStatus status, string? description = null) { }
    /// <inheritdoc/>
    public void AddEvent(string name) { }
    /// <inheritdoc/>
    public void Dispose() { }
}
