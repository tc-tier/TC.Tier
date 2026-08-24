namespace TC.Tier.Core.Tracing;

/// <summary>
/// 零开销空 Span —— NullTracer.BeginSpan 返回此单例。
/// <para>所有方法空实现，Dispose 空操作。可安全用于 <c>using var span = tracer.BeginSpan(...)</c>。</para>
/// </summary>
public sealed class NullSpan : ISpan
{
    public static readonly NullSpan Instance = new();
    private NullSpan() { }
    public void SetTag(string key, string? value) { }
    public void SetTag(string key, long value) { }
    public void RecordException(Exception ex) { }
    public void SetStatus(SpanStatus status, string? description = null) { }
    public void AddEvent(string name) { }
    public void Dispose() { }
}