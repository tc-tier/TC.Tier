namespace TC.Tier.Core.Tracing;

/// <summary>
/// 零开销空 Tracer —— 默认实现。
/// <para>★ <see cref="BeginSpan"/> 返回 <see cref="NullSpan.Instance"/>（非 null，避免 <c>using</c> 块 NRE —— 修复原 NullTracer 返回 null 的缺陷）。</para>
/// <para>★ <see cref="Current"/> 恒返回 null（不维护 AsyncLocal 栈，零开销）。生产路径默认用此实现。</para>
/// </summary>
public sealed class NullTracer : ITracer
{
    public static readonly NullTracer Instance = new();

    public bool IsEnabled => false;   // ★ 零开销：热路径 if(_tracer.IsEnabled) 完全短路
    public ISpan BeginSpan(string name, SpanKind kind = SpanKind.Internal) => NullSpan.Instance;
    public ISpan? Current => null;
}