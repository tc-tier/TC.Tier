namespace TC.Tier.Core.Net.Transport;

/// <summary>
/// 传输故障注入器（spec-12 §9.2——传输常设面，对抗场景族在任一介质等价复跑的机制基础，
/// 非测试代码泄漏）。
/// <para>注入语义均为"定向"（节点对级）——延迟/分区/丢包各管各的注入维度，可叠加。</para>
/// </summary>
public interface ITransportFaultInjector
{
    /// <summary>定向延迟（a → b 方向单向）；null = 清除该方向延迟。时间戳级精度按调用方配置。</summary>
    void SetLatency(NodeId a, NodeId b, TimeSpan? latency);

    /// <summary>分区：groupA ↔ groupB 双向断连（有向对全断）；愈合 = <see cref="Reset"/>。</summary>
    void Partition(IEnumerable<NodeId> groupA, IEnumerable<NodeId> groupB);

    /// <summary>丢包率（from → to 方向单向；rate ∈ [0,1]，0 = 清除）。</summary>
    void Drop(NodeId from, NodeId to, double rate);

    /// <summary>乱序重排（from → to 方向单向；true = 投递前随机抖动破坏到达顺序；false = 清除）。</summary>
    void Reorder(NodeId from, NodeId to, bool enable);

    /// <summary>清除全部注入（分区愈合/延迟与丢包清零/乱序关闭）。</summary>
    void Reset();
}
