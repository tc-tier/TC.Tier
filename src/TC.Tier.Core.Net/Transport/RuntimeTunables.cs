using TC.Tier.Core.Net.Transport;

namespace TC.Tier.Core.Net;

/// <summary>
/// 运行时可变旋钮（二期-D6 NETGAP-011——重连/保活/超时的运行时可变面）：
/// 装配期 <see cref="TransportOptions"/> 不可变面不受影响——本类型只承载"允许运行期调小/调大
/// 而不破坏装配"的旋钮子集，由 <see cref="TC.Tier.Core.Net.Transport.Tcp.ClusterTransport.UpdateRuntimeTunables"/> 校验后生效。
/// <para>★ 各旋钮独立生效（无跨旋钮一致性承诺）；读取方在用点取当前值（拨号每轮/请求每次/保活每拍）。</para>
/// </summary>
public sealed class RuntimeTunables
{
    /// <summary>重连退避初值（拨号循环每轮重读）。</summary>
    public TimeSpan ReconnectInitialDelay { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>重连退避封顶。</summary>
    public TimeSpan ReconnectMaxDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>重连退避倍率。</summary>
    public double ReconnectBackoffFactor { get; set; } = 2.0;

    /// <summary>握手超时（拨号/监听两侧握手窗；> 0）。</summary>
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>请求回调缺省等待（> 0；per-call options 仍可覆盖）。</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>保活周期（> 0；既有链路下一拍生效）。</summary>
    public TimeSpan KeepaliveInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>连续无应答保活断连阈值（>= 1；既有链路挂载时值不回溯）。</summary>
    public int KeepaliveMaxUnanswered { get; set; } = 3;

    internal RuntimeTunables Clone() => (RuntimeTunables)MemberwiseClone();

    internal void CopyTo(RuntimeTunables target)
    {
        target.ReconnectInitialDelay = ReconnectInitialDelay;
        target.ReconnectMaxDelay = ReconnectMaxDelay;
        target.ReconnectBackoffFactor = ReconnectBackoffFactor;
        target.HandshakeTimeout = HandshakeTimeout;
        target.RequestTimeout = RequestTimeout;
        target.KeepaliveInterval = KeepaliveInterval;
        target.KeepaliveMaxUnanswered = KeepaliveMaxUnanswered;
    }

    /// <summary>校验（Update 收口——非法旋钮整体拒绝，不部分生效）。</summary>
    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ReconnectInitialDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(ReconnectMaxDelay, ReconnectInitialDelay);
        ArgumentOutOfRangeException.ThrowIfLessThan(ReconnectBackoffFactor, 1.0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(HandshakeTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(RequestTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(KeepaliveInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(KeepaliveMaxUnanswered, 1);
    }
}
