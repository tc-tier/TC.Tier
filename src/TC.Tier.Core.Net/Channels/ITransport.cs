using TC.Tier.Core.Net.Transport;

namespace TC.Tier.Core.Net.Channels;

/// <summary>
/// 传输连接面（spec-12 §4/§7——介质无关的连接契约）：节点身份/生命周期/对端可达性观测/故障注入。
/// <para>★ 与 <see cref="IProtocol"/>（协议域面——注册分流+三形态收发）正交；介质实现两者
///   （经 <see cref="IProtocolTransport"/> 合一），消费方按需持半边或整面。</para>
/// <para>★ 介质语义对齐：对端可达 = TCP 握手完成 ∥ InProcess 节点注册；离线 = 链路断开 ∥ 注销。
///   注入面 <see cref="Faults"/> 常设（§9.2——延迟/分区/丢包/乱序，任一介质等价复跑）。</para>
/// </summary>
public interface ITransport : IAsyncDisposable
{
    /// <summary>本端节点 ID。</summary>
    NodeId Self { get; }

    /// <summary>启动（幂等——监听/拨号循环/注册即活，介质实现差异在此吸收）。</summary>
    void Start();

    /// <summary>对端可达（TCP = 握手完成；InProcess = 节点注册）。</summary>
    event Action<NodeId>? PeerConnected;

    /// <summary>对端离线（TCP = Established 链路断开；InProcess = 节点注销）。</summary>
    event Action<NodeId>? PeerGone;

    /// <summary>故障注入（常设面——延迟/分区/丢包/乱序，任一介质等价复跑）。</summary>
    ITransportFaultInjector Faults { get; }
}
