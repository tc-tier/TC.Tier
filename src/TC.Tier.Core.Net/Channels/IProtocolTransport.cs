namespace TC.Tier.Core.Net.Channels;

/// <summary>
/// 节点端点完整面（spec-12 §5 形态面消费面——机制/装配/使用方实际持有的类型）：
/// 连接面（<see cref="ITransport"/>：身份/生命周期/对端观测/注入）×
/// 协议域面（<see cref="IProtocol"/>：注册分流 + 三形态收发）合一。
/// <para>★ 介质无关消费面（§12 同构门的抽象基础）：同一机制/产品代码不改一行换介质
///   （InProcess = 同构基准/语义参考实现）。</para>
/// </summary>
public interface IProtocolTransport : IProtocol, ITransport
{
}
