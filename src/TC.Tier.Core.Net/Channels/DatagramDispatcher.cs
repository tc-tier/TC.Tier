using System.Collections.Concurrent;
using System.Diagnostics;

namespace TC.Tier.Core.Net.Channels;

/// <summary>
/// 协议域注册表与数据报分发器（spec-12 §5.1/§3.5）——全介质单源组件：注册分流
/// （公开口 = 注册区 0x60-0xAF / 内部口 = 核心区 0x00-0x4F）、bearer 声明表、
/// 入站分发（handler 异常隔离——回调异常不外泄不致命，尽力送达语义）。
/// <para>★ 可观测钩子（构造注入，介质适配）：未知协议丢弃 / 慢回调计数——两介质均接
///   ObservabilityHub.Net 视图（spec-12 §9.1——手写指标类不存在）。
///   慢回调阈值未配置 = 零计时开销（直排热路径无 timestamp 成本）。</para>
/// </summary>
internal sealed class DatagramDispatcher
{
    private readonly ConcurrentDictionary<byte, IDatagramHandler> _handlers = new();
    private readonly ConcurrentDictionary<byte, DatagramBearer> _bearers = new();
    private readonly long _slowThresholdTicks;
    private readonly Action<byte>? _onUnknownProtocol;
    private readonly Action<byte>? _onSlowDispatch;

    /// <summary>构造。</summary>
    /// <param name="slowDispatchThreshold">慢回调阈值（null = 不检测——分发零计时开销）。</param>
    /// <param name="onUnknownProtocol">未知协议丢弃钩子（参数 = 协议域 ID——可观测 tag）。</param>
    /// <param name="onSlowDispatch">慢回调钩子（参数 = 协议域 ID；超阈值时每分发至多一次）。</param>
    public DatagramDispatcher(TimeSpan? slowDispatchThreshold = null, Action<byte>? onUnknownProtocol = null, Action<byte>? onSlowDispatch = null)
    {
        if (slowDispatchThreshold is { } threshold)
            _slowThresholdTicks = threshold.Ticks * Stopwatch.Frequency / TimeSpan.TicksPerSecond;
        _onUnknownProtocol = onUnknownProtocol;
        _onSlowDispatch = onSlowDispatch;
    }

    /// <summary>
    /// 注册·公开口（§3.5 分流——只放行注册区 0x60-0xAF：使用方在此自管唯一性，
    /// 结构上无法占用内部号）；同 ID 重复注册抛。
    /// </summary>
    /// <param name="protocolId">协议域 ID（注册区）。</param>
    /// <param name="handler">入站数据报处理器。</param>
    /// <param name="bearer">发送承载声明。</param>
    public void RegisterUser(byte protocolId, IDatagramHandler handler, DatagramBearer bearer = DatagramBearer.Tcp)
    {
        ProtocolRegistration.ValidateUserPort(protocolId);
        ProtocolRegistration.Add(_handlers, protocolId, handler);
        _bearers[protocolId] = bearer;
    }

    /// <summary>
    /// 注册·内部口（机制面专用——只放行内部核心区 0x00-0x4F）。
    /// </summary>
    /// <param name="protocolId">协议域 ID（内部核心区）。</param>
    /// <param name="handler">入站数据报处理器。</param>
    /// <param name="bearer">发送承载声明。</param>
    public void RegisterCore(byte protocolId, IDatagramHandler handler, DatagramBearer bearer = DatagramBearer.Tcp)
    {
        ProtocolRegistration.ValidateCorePort(protocolId);
        ProtocolRegistration.Add(_handlers, protocolId, handler);
        _bearers[protocolId] = bearer;
    }

    /// <summary>发送承载声明查询（介质承载路由——如 TCP 的 UDP 回落判定）。</summary>
    /// <param name="protocolId">协议域 ID。</param>
    /// <param name="bearer">查得的承载声明（未找到时为默认值）。</param>
    /// <returns>true = 已注册承载声明；false = 未注册。</returns>
    public bool TryGetBearer(byte protocolId, out DatagramBearer bearer) => _bearers.TryGetValue(protocolId, out bearer);

    /// <summary>
    /// 入站分发：未注册协议 = 静默丢弃 + 钩子；handler（使用方代码）异常隔离——不外泄不致命。
    /// </summary>
    /// <param name="from">来源节点。</param>
    /// <param name="protocolId">协议域 ID。</param>
    /// <param name="payload">载荷。</param>
    public void Dispatch(NodeId from, byte protocolId, ReadOnlyMemory<byte> payload)
    {
        if (!_handlers.TryGetValue(protocolId, out var handler))
        {
            _onUnknownProtocol?.Invoke(protocolId);
            return;
        }

        long start = _slowThresholdTicks > 0 ? Stopwatch.GetTimestamp() : 0;
        try
        {
            handler.OnDatagram(from, payload);
        }
        catch (Exception)
        {
            // handler（使用方代码）异常不外泄、不致命——尽力送达语义（全介质同构）
        }
        if (_slowThresholdTicks > 0 && _onSlowDispatch is not null
            && Stopwatch.GetTimestamp() - start > _slowThresholdTicks)
        {
            _onSlowDispatch(protocolId);   // 快进快出是契约不是机制——慢回调计数兜底
        }
    }
}
