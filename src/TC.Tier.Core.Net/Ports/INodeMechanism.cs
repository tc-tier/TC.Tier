using TC.Tier.Core.Net.Channels;

namespace TC.Tier.Core.Net.Ports;

/// <summary>
/// 节点机制挂载端口（spec-12 §1/§6 机制零特权 × E4 四面锁——装配四面零机制知识的实现面）：
/// 内建机制（raft/p2p/swarm）与第三方/私有域**同一挂载路径**——机制自证挂载形态
/// （实现于各自命名空间），装配层只见本端口。
/// <para>★ Hosting 经 <see cref="INodeMechanism"/> 挂载——不引任何机制命名空间（TCSG061
/// 机器锁）；机制实现在自身命名空间引用本端口（机制 → 装配 单向依赖）。</para>
/// <para>★ 生命周期：装配方持机制并随节点端点释放（DisposeAsync 有界排空——机制自身
/// 保证幂等可重入）。</para>
/// </summary>
public interface INodeMechanism : IAsyncDisposable
{
    /// <summary>机制身份（诊断/日志）。</summary>
    string Name { get; }

    /// <summary>挂载到节点端点（机制经内部口注册协议域/自起循环——介质须实现内部挂载口，
    /// 否则抛——spec-12 §3.5 机制宿主归 Core.Net）。只可一次。</summary>
    /// <param name="transport">节点端点完整面。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask MountAsync(IProtocolTransport transport, CancellationToken ct = default);
}
