namespace TC.Tier.Core.Tracing;

/// <summary>
/// Span 类型 —— 参照 OpenTelemetry/Datadog，标识 span 在分布式链路中的角色。
/// </summary>
public enum SpanKind
{
    /// <summary>内部操作（默认）。</summary>
    Internal,
    /// <summary>服务端处理（接收请求）。</summary>
    Server,
    /// <summary>客户端调用（发出请求）。</summary>
    Client,
    /// <summary>生产者（发消息到队列/日志）。</summary>
    Producer,
    /// <summary>消费者（从队列/日志消费）。</summary>
    Consumer
}