using System.Runtime.CompilerServices;

namespace TC.Tier.Core.Observability;

/// <summary>可观测 hub——本部分定义 <see cref="NetView"/>（Net 传输维度视图，spec-12 §9.1）。</summary>
public sealed partial class ObservabilityHub
{
    /// <summary>
    /// Net 传输维度视图（spec-12 §9.1 第五轮裁定：在 Hub 加视图，不是自己造——第六维度，
    /// 对齐 Storage/Log/Index/SegmentAllocator 形态）。peer 一律以 hex32 文本标签过界
    /// （Core 不依赖 Core.Net——兄弟层不互依）。
    /// <para>★ 语义：帧量 <b>采样</b>（确定性百分比——热路径零开销）；
    ///   错误/丢弃<b>全采</b>（reconnect/CRC/握手失败/UDP 发送失败/保活断连/丢弃计数）；
    ///   per-protocol = tag（维度即 tag 零字段爆炸）。Core.Net 只调本视图，绝不直调 sink。</para>
    /// </summary>
    public sealed partial class NetView
    {
        private readonly IMetricsSink _sink;
        private readonly int _rate;
        private readonly bool _enabled;
        private int _frameCtr;

        internal NetView(IMetricsSink sink, int rate, bool enabled)
        {
            _sink = sink;
            _rate = rate;
            _enabled = enabled;
        }

        /// <summary>Net 维度指标是否启用（总开关 &amp;&amp; EnableNetMetrics 短路后的终值）。</summary>
        public bool IsEnabled => _enabled;

        /// <summary>帧事件本次是否应采样（确定性百分比采样；维度关闭恒 false）。</summary>
        /// <returns>true 表示本次应采样；false 表示不采样（维度关闭时恒 false）。</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ShouldSampleFrame() => _enabled && ShouldSample(ref _frameCtr, _rate);

        /// <summary>上报已发送帧（<c>net.frames_sent</c>，采样命中；调用方先过 <see cref="ShouldSampleFrame"/>）。</summary>
        /// <param name="protocolId">协议域 ID（tag）。</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnFrameSent(byte protocolId)
        {
            if (!_enabled) return;
            _sink.Counter("net.frames_sent", [Kv("protocol", protocolId.ToString("X2"))]);
        }

        /// <summary>上报已接收帧（<c>net.frames_received</c>，采样命中）。</summary>
        /// <param name="protocolId">协议域 ID（tag）。</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnFrameReceived(byte protocolId)
        {
            if (!_enabled) return;
            _sink.Counter("net.frames_received", [Kv("protocol", protocolId.ToString("X2"))]);
        }

        /// <summary>上报帧大小直方图（<c>net.frame_bytes</c>，可选观测——大块诊断）。</summary>
        /// <param name="protocolId">协议域 ID（tag）。</param>
        /// <param name="bytes">帧字节数。</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnFrameSize(byte protocolId, long bytes)
        {
            if (!_enabled) return;
            _sink.Histogram("net.frame_bytes", bytes, [Kv("protocol", protocolId.ToString("X2"))]);
        }

        /// <summary>上报数据报丢弃（<c>net.datagram_dropped</c>，错误全采——尽力送达语义的可观测面）。</summary>
        /// <param name="protocolId">协议域 ID（tag）。</param>
        /// <param name="reason">丢弃原因（no_link/unknown_protocol/unknown_kind/udp_unknown_source/udp_malformed）。</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnDatagramDropped(byte protocolId, string reason)
        {
            if (!_enabled) return;
            _sink.Counter("net.datagram_dropped",
                [Kv("protocol", protocolId.ToString("X2")), Kv("reason", reason)]);
        }

        /// <summary>上报帧 CRC 校验失败（<c>net.crc_failures</c>，错误全采——半帧/错位检测）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnCrcFailure()
        {
            if (!_enabled) return;
            _sink.Counter("net.crc_failures", []);
        }

        /// <summary>上报重连发生（<c>net.reconnects</c>，错误全采——首连不计）。</summary>
        /// <param name="peer">对端节点 tag（hex32——Core 不依赖 Core.Net，身份以文本标签过界）。</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnReconnect(string peer)
        {
            if (!_enabled) return;
            _sink.Counter("net.reconnects", [Kv("peer", peer)]);
        }

        /// <summary>上报分发慢回调（<c>net.slow_dispatch</c>，错误全采——快进快出契约的可观测兜底）。</summary>
        /// <param name="protocolId">协议域 ID（tag）。</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnSlowDispatch(byte protocolId)
        {
            if (!_enabled) return;
            _sink.Counter("net.slow_dispatch", [Kv("protocol", protocolId.ToString("X2"))]);
        }

        /// <summary>上报请求回调慢处理（<c>net.slow_request</c>，二期-I3——分发至应答耗时超阈值；错误全采）。</summary>
        /// <param name="protocolId">协议域 ID（tag）。</param>
        /// <param name="elapsedMs">分发至应答耗时（毫秒——同时入 <c>net.request_latency_ms</c> 直方图）。</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnSlowRequest(byte protocolId, double elapsedMs)
        {
            if (!_enabled) return;
            _sink.Counter("net.slow_request", [Kv("protocol", protocolId.ToString("X2"))]);
            _sink.Histogram("net.request_latency_ms", elapsedMs, [Kv("protocol", protocolId.ToString("X2"))]);
        }

        /// <summary>上报流 acceptor 慢回调（<c>net.slow_stream_accept</c>，二期-I3）。</summary>
        /// <param name="protocolId">协议域 ID（tag）。</param>
        /// <param name="elapsedMs">accept 回调耗时（毫秒）。</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnSlowStreamAccept(byte protocolId, double elapsedMs)
        {
            if (!_enabled) return;
            _sink.Counter("net.slow_stream_accept", [Kv("protocol", protocolId.ToString("X2"))]);
        }

        /// <summary>上报握手失败（<c>net.handshake_failures</c>，错误全采——超时/拒绝/违规）。</summary>
        /// <param name="reason">失败原因。</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnHandshakeFailure(string reason)
        {
            if (!_enabled) return;
            _sink.Counter("net.handshake_failures", [Kv("reason", reason)]);
        }

        /// <summary>上报 UDP 数据报发送失败（<c>net.udp_send_failures</c>，错误全采——尽力静默的可观测面）。</summary>
        /// <param name="peer">目标节点 tag（hex32）。</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnUdpSendFailure(string peer)
        {
            if (!_enabled) return;
            _sink.Counter("net.udp_send_failures", [Kv("peer", peer)]);
        }

        /// <summary>上报保活超时断连（<c>net.keepalive_drops</c>，错误全采——半开死链检测）。</summary>
        /// <param name="peer">对端节点 tag（hex32）。</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnKeepaliveDrop(string peer)
        {
            if (!_enabled) return;
            _sink.Counter("net.keepalive_drops", [Kv("peer", peer)]);
        }

        /// <summary>上报 UDP 数据报发送量（<c>net.udp.datagrams_sent</c>——介质级量纲，承载路由判定的观测面）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnUdpDatagramSent()
        {
            if (!_enabled) return;
            _sink.Counter("net.udp.datagrams_sent", []);
        }

        /// <summary>上报 UDP 数据报接收量（<c>net.udp.datagrams_received</c>——介质级量纲）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnUdpDatagramReceived()
        {
            if (!_enabled) return;
            _sink.Counter("net.udp.datagrams_received", []);
        }

        /// <summary>上报 RTT 样本（<c>net.rtt_us</c> 直方图——per-peer 往返观测）。</summary>
        /// <param name="peer">对端节点 tag（hex32）。</param>
        /// <param name="micros">往返延迟（微秒）。</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnRttSample(string peer, long micros)
        {
            if (!_enabled) return;
            _sink.Histogram("net.rtt_us", micros, [Kv("peer", peer)]);
        }
    }
}
