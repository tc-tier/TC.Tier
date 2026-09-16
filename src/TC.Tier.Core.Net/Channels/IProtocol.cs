namespace TC.Tier.Core.Net.Channels;

/// <summary>
/// 协议域面（spec-12 §3.5/§5——端点的多路复用子系统：协议域注册分流 + 数据报/请求回调/流式
/// 三形态收发；全部成员以 protocolId 为参数）。
/// <para>★ 数据报语义（尽力送达，§5.1）：对端未连/未注册协议/注入丢弃 = 静默丢弃——协议域自愈；
///   handler 快进快出（回调异常不外泄、不杀链路）。</para>
/// <para>★ 注册面（§3.5 分流）：公开口只放行注册区 0x60-0xAF——使用方自管号码；
///   内部核心区经程序集内 internal 挂载口（<see cref="ICoreProtocolPort"/>——机制面专用）。
///   bearer 声明承载偏好（介质不支持时回落/直派——尽力语义不变）。</para>
/// <para>★ 请求回调（§5.2）：单请求 → 关联应答 → 超时；CorrId 传输生成；目标未连 =
///   <see cref="NetIOException"/>（调用方有应答期待，不静默浪费超时窗）；投递语义缺省
///   at-most-once，重传/确认 = 显式选择（RetryPolicy——at-least-once）。</para>
/// <para>★ 流式会话（§5.3）：可靠有序 + 背压；GB 级 O(单帧) 不驻留；未注册 acceptor 的
///   协议域 = 拒绝开流。</para>
/// </summary>
public interface IProtocol
{
    /// <summary>
    /// 协议域注册·公开口（spec-12 §3.5 注册面分流）——只放行注册区 0x60-0xAF
    /// （<see cref="Wire.ProtocolIds.IsUserRegistrable"/>）；同 ID 重复注册抛。
    /// </summary>
    /// <param name="protocolId">协议域 ID（注册区 0x60-0xAF）。</param>
    /// <param name="handler">入站数据报处理器。</param>
    /// <param name="bearer">发送承载声明（默认 TCP 帧流；内存介质恒承载——尽力语义不变）。</param>
    void RegisterProtocol(byte protocolId, IDatagramHandler handler, DatagramBearer bearer = DatagramBearer.Tcp);

    /// <summary>
    /// 数据报发送（尽力送达——spec-12 §5.1）：对端未连/注入丢弃 = 静默丢弃；
    /// 仅参数错误（超帧上限/取消）抛。
    /// </summary>
    /// <param name="target">目标节点。</param>
    /// <param name="protocolId">协议域 ID。</param>
    /// <param name="payload">载荷（≤帧协议上限——大块走流式通道）。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask SendDatagramAsync(NodeId target, byte protocolId, ReadOnlyMemory<byte> payload, CancellationToken ct = default);

    /// <summary>
    /// 请求回调 handler 注册·公开口（spec-12 §5.2——只放行注册区 0x60-0xAF，同 §3.5 分流）；
    /// 同 ID 重复注册抛。
    /// </summary>
    /// <param name="protocolId">协议域 ID（注册区 0x60-0xAF）。</param>
    /// <param name="handler">入站请求处理器。</param>
    void RegisterRequestHandler(byte protocolId, IRequestHandler handler);

    /// <summary>
    /// 请求回调发送（spec-12 §5.2——单请求 → 关联应答 → 超时；CorrId 传输生成）。
    /// <para>★ 目标未连 = 抛 <see cref="NetIOException"/>（调用方有应答期待，不静默浪费超时窗）；
    ///   注入丢弃 = 超时；取消传播；应答到达返回载荷（空应答合法）。</para>
    /// <para>★ 投递语义缺省 at-most-once（单发 + 超时）；重传/确认 = 显式选择
    ///   （RetryPolicy——at-least-once）。</para>
    /// <para>★ ValueTask 单次消费契约（await 恰一次）；参数校验（未连接/载荷超限/已释放）
    ///   在调用点同步抛（非 async 直通形态——调用方 try/catch 即达）。</para>
    /// </summary>
    /// <param name="target">目标节点。</param>
    /// <param name="protocolId">协议域 ID。</param>
    /// <param name="payload">请求载荷。</param>
    /// <param name="options">per-call 选项（null = 传输缺省）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>应答载荷。</returns>
    ValueTask<byte[]> SendRequestAsync(NodeId target, byte protocolId, ReadOnlyMemory<byte> payload, RequestOptions? options = null, CancellationToken ct = default);

    /// <summary>
    /// 流接受面注册·公开口（spec-12 §5.3——注册区 0x60-0xAF，同 §3.5 分流）。
    /// 未注册 acceptor 的协议域 = 拒绝开流（发起端 <see cref="OpenStreamAsync"/> 抛 <see cref="NetIOException"/>）。
    /// </summary>
    /// <param name="protocolId">协议域 ID（注册区）。</param>
    /// <param name="acceptor">入站会话接受面。</param>
    void RegisterStreamAcceptor(byte protocolId, IStreamAcceptor acceptor);

    /// <summary>
    /// 打开流式会话（spec-12 §5.3——可靠有序 + 背压；GB 级 O(单帧) 不驻留）。
    /// <para>★ 目标未连/对端不接受 = 抛 <see cref="NetIOException"/>；等待超时 = <see cref="TimeoutException"/>；
    ///   会话不跨重连恢复（链路断 = Reset，上层重开）。</para>
    /// <para>★ openPayload（二期-C1）：随 StreamOpen 帧承载的发起方载荷（不透明字节，协议域自解释——
    ///   如组路由的 [GroupId 8B] 前缀）；接受侧经 <see cref="IWireStream.OpenPayload"/> 与 acceptor
    ///   回调取得。空 = 无载荷（既有形态）。</para>
    /// </summary>
    /// <param name="target">目标节点。</param>
    /// <param name="protocolId">协议域 ID。</param>
    /// <param name="openPayload">开流载荷（不透明字节——协议域自解释）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>会话。</returns>
    ValueTask<IWireStream> OpenStreamAsync(NodeId target, byte protocolId, ReadOnlyMemory<byte> openPayload = default, CancellationToken ct = default);
}
