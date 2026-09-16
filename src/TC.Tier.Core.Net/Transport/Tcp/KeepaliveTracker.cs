namespace TC.Tier.Core.Net.Transport.Tcp;

/// <summary>
/// 保活计票（spec-12 §4.5——周期发送 Keepalive，连续无入站保活达上限即断连）。
/// <para>★ 并发契约：发送循环（OnSent/Exceeded）与接收循环（OnReceived）两线程并发——
///   Interlocked 计数，"连续"语义 = 收到即归零（TCP 帧流不丢帧，丢帧形态 = 半开死链）。</para>
/// </summary>
internal sealed class KeepaliveTracker(int maxUnanswered)
{
    private int _unanswered;

    /// <summary>发送一次保活。true = 连续未答已超上限（调用方断连）。</summary>
    /// <returns>true = 连续未应答次数已超过上限（本端判定链路死——调用方断连）；false = 继续等待。</returns>
    public bool OnSent()
    {
        int unanswered = Interlocked.Increment(ref _unanswered);
        return unanswered > maxUnanswered;   // 此前 maxUnanswered 次全部未答——每个已发保活恰有一个周期可被应答
    }

    /// <summary>收到入站保活（对端活性证明——归零）。</summary>
    public void OnReceived() => Interlocked.Exchange(ref _unanswered, 0);
}
