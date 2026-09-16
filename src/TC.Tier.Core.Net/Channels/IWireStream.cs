namespace TC.Tier.Core.Net.Channels;

/// <summary>
/// 流式会话（spec-12 §5.3——可靠有序 + 背压；GB 级 O(单帧) 不驻留）：StreamOpen/Accept/
/// Data/End/Reset 帧族承载，会话号 1..254，不跨重连恢复（上层重开）。
/// <para>★ 背压 = 写 await 传导：接收侧会话缓冲有界，满 → 介质读循环 await →
///   对端 socket 写 await → 本端 <see cref="WriteAsync"/> await——整链传导。</para>
/// <para>★ 生命周期：写端正常收尾 <see cref="CompleteAsync"/>（End 帧——对端
///   <see cref="ReadAllAsync"/> 自然结束）；异常中止 = <see cref="IAsyncDisposable.DisposeAsync"/>
///   （Reset 帧——双方读写立即终止）。</para>
/// </summary>
public interface IWireStream : IAsyncDisposable
{
    /// <summary>协议域 ID（会话归属）。</summary>
    byte ProtocolId { get; }

    /// <summary>对端节点。</summary>
    NodeId Peer { get; }

    /// <summary>开流载荷（二期-C1——发起端 <see cref="IProtocol.OpenStreamAsync"/> 随 StreamOpen
    /// 承载的不透明字节；接受/发起两侧同义 = 线上全量。协议域自解释——如组路由前缀）。</summary>
    ReadOnlyMemory<byte> OpenPayload { get; }

    /// <summary>写一帧（可靠有序；≤帧协议上限——GB 级流由使用方分块逐帧推进，O(单帧) 不驻留）。
    /// await = 背压传导（对端消费慢 → 本端等待）。</summary>
    /// <param name="buffer">帧数据。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default);

    /// <summary>本端写完（End 帧——正常收尾；对端 <see cref="ReadAllAsync"/> 枚举自然结束）。</summary>
    /// <param name="ct">取消令牌。</param>
    ValueTask CompleteAsync(CancellationToken ct = default);

    /// <summary>读全部帧（可靠有序；对端 Complete 后枚举自然结束；Dispose/Reset 后终止）。
    /// 逐帧产出——GB 级流读一帧处理一帧，O(单帧) 不驻留。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>帧序列。</returns>
    IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync(CancellationToken ct = default);
}

/// <summary>
/// 流式会话接受面（spec-12 §5.3——<c>RegisterStreamAcceptor(protocol, acceptor)</c> 的回调契约）。
/// 未注册 acceptor 的协议域 = 拒绝开流（发起端 <c>OpenStreamAsync</c> 抛 <see cref="NetIOException"/>）。
/// </summary>
public interface IStreamAcceptor
{
    /// <summary>入站会话回调（快进快出——重活自起消费循环：<c>_ = ConsumeAsync(stream.ReadAllAsync())</c> 形态由使用方管理）。</summary>
    /// <param name="from">发起节点。</param>
    /// <param name="stream">会话（回调返回后即可读写）。</param>
    /// <param name="openPayload">开流载荷（二期-C1——线上全量；组路由由 host 剥前缀后传内层）。</param>
    void OnStream(NodeId from, IWireStream stream, ReadOnlyMemory<byte> openPayload);
}
