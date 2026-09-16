namespace TC.Tier.Core.Net.Channels;

/// <summary>
/// 请求回调发送选项（spec-12 §5.2——per-call 覆盖传输缺省）。
/// </summary>
/// <param name="Timeout">单发等待应答超时（null = <c>TransportOptions.RequestTimeout</c> 缺省）。</param>
/// <param name="Retry">at-least-once 重发策略（null = at-most-once 缺省——可靠是显式选择）。</param>
public sealed record RequestOptions(TimeSpan? Timeout = null, RetryPolicy? Retry = null)
{
    /// <summary>缺省（全参数走传输配置）。</summary>
    public static RequestOptions Default { get; } = new();

    /// <summary>跨节点 trace 上下文（二期-I4——26B 线格式；null 且 tracer 可用 = 自动捕获 Current）。
    /// 仅对协商出 <see cref="TC.Tier.Core.Net.Wire.HandshakeFeatures.Trace"/> 的对端实际上线。</summary>
    public byte[]? TraceContext { get; init; }
}
