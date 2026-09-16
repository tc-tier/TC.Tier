namespace TC.Tier.Core.Net.Channels;

/// <summary>
/// 背压策略（二期-D5 NETGAP-014/016——请求 pending 满载时的域级行为；产品按域声明，
/// 缺省 FailFast 保持既有语义）。
/// </summary>
public enum BackpressurePolicy : byte
{
    /// <summary>缺省——pending 满 = 立即抛 <see cref="InvalidOperationException"/>（fail-fast，
    /// 调用方并发失控或应答缺失的快速失败面）。</summary>
    FailFast = 0,

    /// <summary>有界排队——pending 满时等待槽位释放（≤ 队列等待上限 <c>RequestQueueWait</c>；
    /// 超时回落 fail-fast 抛）。适合可容忍短时延迟的域。</summary>
    Queue = 1,

    /// <summary>满载降级——pending 满立即抛 <see cref="RequestDegradeException"/>（携带降级语义
    /// 的失败面——产品可据此fallback/限流告警，区别于失控 fail-fast）。</summary>
    Degrade = 2,

    /// <summary>高优先级——共享池满仍可入（预留预算 <c>RequestPriorityReserve</c> 槽位内准入；
    /// 超预留回落 fail-fast）。适合管理面/心跳类关键域。</summary>
    Priority = 3,
}

/// <summary>
/// 满载降级异常（二期-D5——<see cref="BackpressurePolicy.Degrade"/> 专用失败面）：
/// 语义 = "系统满载已降级拒绝"，区别于 fail-fast 的"调用方失控"——产品据此 fallback/限流告警。
/// </summary>
public sealed class RequestDegradeException : InvalidOperationException
{
    /// <summary>构造。</summary>
    /// <param name="message">降级说明（进告警/日志）。</param>
    public RequestDegradeException(string message) : base(message) { }
}
