namespace TC.Tier.Core.Tracing;

/// <summary>
/// 链路 span 契约 —— 一次操作的执行上下文（参照 OpenTelemetry ISpan / Datadog IScope）。
/// <para>由 <see cref="ITracer.BeginSpan"/> 创建，<see cref="System.IDisposable.Dispose"/> 结束 span（= EndSpan）。</para>
/// <para>典型用法：<c>using var span = _tracer.BeginSpan("wal.append", SpanKind.Producer);</c></para>
/// <para>★ AOT 友好：纯接口 + 枚举，无反射/Emit。NullSpan 是零开销空实现。</para>
/// </summary>
public interface ISpan : IDisposable
{
    /// <summary>设置字符串标签（如 collection 名、shard id）。</summary>
    void SetTag(string key, string? value);

    /// <summary>设置数值标签（如 entry 大小、页号）。</summary>
    void SetTag(string key, long value);

    /// <summary>记录异常（自动设 Error 状态）。</summary>
    void RecordException(Exception ex);

    /// <summary>设置 span 状态（Ok/Error）+ 可选描述。</summary>
    void SetStatus(SpanStatus status, string? description = null);

    /// <summary>记录事件（如 "page.flushed"、"checkpoint.started"，时间戳自动）。</summary>
    void AddEvent(string name);
}
