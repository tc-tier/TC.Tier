namespace TC.Tier.Core.Tracing;

/// <summary>
/// 链路追踪器契约 —— 完整 span 体系（从零设计）。
/// <para>★ 完整 span 体系（参照 OpenTelemetry/Datadog），从零设计，非修补：</para>
/// <para>- <see cref="ITracer.BeginSpan(string, SpanKind)"/> 创建 span，返回 <see cref="ISpan"/>（Dispose=EndSpan）；</para>
/// <para>- <see cref="Current"/> 暴露 AsyncLocal 当前 span（Datadog 模式，避免层层传参，子 span 自动关联父 span）；</para>
/// <para>- public（与 <see cref="IMetricsSink"/> / <see cref="ILogger"/> 一致，供上层注入）。</para>
/// <para>★ 默认实现 <see cref="NullTracer"/>：<see cref="ITracer.BeginSpan(string, SpanKind)"/> 返回 <see cref="NullSpan.Instance"/>（非 null，避免 using NRE），零开销。</para>
/// <para>★ AOT 友好：纯接口 + 枚举 + AsyncLocal，无反射/Emit。</para>
/// </summary>
public interface ITracer
{
    /// <summary>
    /// 是否启用追踪（热路径零开销的关键开关，参照 IKernelLogger.IsEnabled）。
    /// <para>NullTracer 返回 false → 热路径 <c>if (_tracer.IsEnabled)</c> 完全短路，不调 BeginSpan、不分配 span。</para>
    /// <para>注入真实 tracer 返回 true → 配合 <see cref="TracingConfig.SampleRate"/> 采样命中才 BeginSpan。</para>
    /// </summary>
    /// <remarks>★ 热路径必须在 <c>if (_tracer.IsEnabled)</c> 内调用 BeginSpan，避免 NullTracer 也分配 span。</remarks>
    bool IsEnabled { get; }

    /// <summary>
    /// 开始一个 span。返回的 <see cref="ISpan"/> 的 Dispose 结束 span。
    /// <para>新 span 自动关联到 <see cref="Current"/>（AsyncLocal 父子链）。</para>
    /// <para>★ 热路径必须在 <c>if (_tracer.IsEnabled)</c> 内调用，避免 NullTracer 也分配 span。</para>
    /// </summary>
    /// <param name="name">span 名（如 "wal.append"、"kv.read"、"checkpoint"）。</param>
    /// <param name="kind">span 角色（默认 Internal）。</param>
    /// <returns>新 span（Dispose=EndSpan）。</returns>
    ISpan BeginSpan(string name, SpanKind kind = SpanKind.Internal);

    /// <summary>
    /// 当前线程异步上下文的活跃 span（Datadog AsyncLocal 模式）。
    /// <para>BeginSpan 自动设为新 span，Dispose 恢复父 span。子方法无需传参即可拿到当前 span 添加标签/事件。</para>
    /// <para>NullTracer 返回 null（无活跃 span）。</para>
    /// </summary>
    /// <remarks>★ AsyncLocal 模式：BeginSpan/Dispose 自动维护父子链，子方法无需传参即可拿到当前 span 添加标签/事件。</remarks>
    ISpan? Current { get; }

    /// <summary>
    /// 开始一个 Span，以跨进程父上下文为父（二期-I4——default 方法，不破坏既有实现）。
    /// <para>默认实现忽略线上下文（按无父处理）；外部 tracer 覆写本方法 +
    /// <see cref="CaptureContext"/> 即得跨节点串联能力。线字节格式归 Net 层
    /// （SpanContextCodec——26B 定长），tracer 只解语义。</para>
    /// </summary>
    /// <param name="name">span 名。</param>
    /// <param name="kind">span 角色。</param>
    /// <param name="parentWireContext">线上传播的父上下文（长度/畸形由 tracer 自行判定）。</param>
    ISpan? BeginSpan(string name, SpanKind kind, ReadOnlySpan<byte> parentWireContext) => BeginSpan(name, kind);

    /// <summary>
    /// 当前上下文线序列化（二期-I4——default 方法，不破坏既有实现）。
    /// <para>默认返回 null = 无传播能力；实现方返回的字节需可被自己的
    /// <c>BeginSpan(name, kind, parent)</c> 解释（对称编解码）。</para>
    /// </summary>
    byte[]? CaptureContext() => null;
}
