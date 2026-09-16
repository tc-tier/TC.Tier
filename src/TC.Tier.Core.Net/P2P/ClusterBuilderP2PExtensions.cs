using TC.Tier.Core.Net.Hosting;
using TC.Tier.Core.Net.Ports;

namespace TC.Tier.Core.Net.P2P;

/// <summary>
/// ClusterBuilder 的机制 opt-in 扩展（spec-12 §11——<c>.WithP2P(seed)</c> 流式形态）：
/// 机制挂载以扩展方法供给（P2P 命名空间——机制 → 装配单向依赖），装配面本体零机制知识
/// （<see cref="ClusterBuilder.WithMechanism"/> 通用挂载口 + TCSG061 四面锁不动）。
/// </summary>
public static class ClusterBuilderP2PExtensions
{
    /// <summary>挂载 HyParView（机制显式 opt-in——永不默认挂载）：JOIN 从 seed 扩散。</summary>
    /// <param name="builder">成员制节点装配器。</param>
    /// <param name="seed">JOIN 种子节点（null = 部署种子自身——只启动不 JOIN）。</param>
    /// <param name="options">HyParView 参数（null = 缺省表）。</param>
    /// <returns>同一装配器实例（供链式配置）。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> 为 null。</exception>
    public static ClusterBuilder WithP2P(this ClusterBuilder builder, NodeId? seed = null, PeerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithMechanism(new PeerMechanism(seed, options));
    }
}
