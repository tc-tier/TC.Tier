namespace TC.Tier.Core.Net.Channels;

/// <summary>
/// 机制面挂载口（spec-12 §3.5 内部口——Core.Net 内建机制专用）：内建机制
/// （Raft/HyParView/SwarmSync）经内部注册口挂内部核心区 0x00-0x4F 号段——
/// <see cref="IProtocol"/> 公开面只放行注册区（使用方结构上无法占内部号），
/// 内部口不出程序集（机制零特权 = 与使用方同一分发路径，号码来源分流）。
/// <para>★ 窄面：只含三个 core 注册方法、不继承任何公共面——挂载能力是最小面
///   （机制组件以 <see cref="IProtocolTransport"/> 收端点，挂载时按本口模式匹配）。</para>
/// <para>★ 介质实现（<see cref="Transport.Tcp.ClusterTransport"/>/<see cref="Transport.InProcess.InProcessNode"/>
///   等）实现本接口（internal 方法显式实现——隐式实现要求 public 而内部口不出程序集）；
///   外部自定义介质不实现即不可挂内建机制（机制宿主归 Core.Net）。</para>
/// </summary>
internal interface ICoreProtocolPort
{
    /// <summary>
    /// 协议域注册·内部口（数据报 handler——只放行内部核心区 0x00-0x4F）。
    /// </summary>
    /// <param name="protocolId">协议域 ID（内部核心区）。</param>
    /// <param name="handler">入站数据报处理器。</param>
    /// <param name="bearer">发送承载声明（内存介质恒承载——声明语义在进程内退化为直派）。</param>
    void RegisterCoreProtocol(byte protocolId, IDatagramHandler handler, DatagramBearer bearer = DatagramBearer.Tcp);

    /// <summary>请求回调 handler 注册·内部口（只放行内部核心区）。</summary>
    /// <param name="protocolId">协议域 ID（内部核心区）。</param>
    /// <param name="handler">入站请求处理器。</param>
    void RegisterCoreRequestHandler(byte protocolId, IRequestHandler handler);

    /// <summary>流接受面注册·内部口（只放行内部核心区）。</summary>
    /// <param name="protocolId">协议域 ID（内部核心区）。</param>
    /// <param name="acceptor">入站会话接受面。</param>
    void RegisterCoreStreamAcceptor(byte protocolId, IStreamAcceptor acceptor);
}
