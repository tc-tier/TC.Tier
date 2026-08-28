namespace TC.Tier.Core.Tracing;

/// <summary>
/// 零开销空 Tracer —— 默认实现。
/// <para>★ <see cref="BeginSpan"/> 返回 <see cref="NullSpan.Instance"/>（非 null，避免 <c>using</c> 块 NRE —— 修复原 NullTracer 返回 null 的缺陷）。</para>
/// <para>★ <see cref="Current"/> 恒返回 null（不维护 AsyncLocal 栈，零开销）。生产路径默认用此实现。</para>
/// </summary>
public sealed class NullTracer : ITracer
{
    /// <summary>全局共享单例（无状态，安全并发使用）。</summary>
    public static readonly NullTracer Instance = new();

    /// <summary>恒 false —— 零开销：热路径 if(_tracer.IsEnabled) 完全短路。</summary>
    public bool IsEnabled => false;   // ★ 零开销：热路径 if(_tracer.IsEnabled) 完全短路
    /// <summary>返回空 Span 单例（非 null——可安全用于 <c>using</c> 块）。</summary>
    /// <param name="name">Span 名称（忽略）。</param>
    /// <param name="kind">Span 类型（忽略）。</param>
    /// <returns>恒返回 <see cref="NullSpan.Instance"/>。</returns>
    public ISpan BeginSpan(string name, SpanKind kind = SpanKind.Internal) => NullSpan.Instance;
    /// <summary>当前活动 Span —— 恒返回 null（不维护 AsyncLocal 栈）。</summary>
    public ISpan? Current => null;
}
