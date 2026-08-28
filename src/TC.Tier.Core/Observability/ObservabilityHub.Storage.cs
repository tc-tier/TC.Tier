using System.Runtime.CompilerServices;

namespace TC.Tier.Core.Observability;

public sealed partial class ObservabilityHub
{
    /// <summary>Storage Engine IO 维度视图 —— Read/Write/Flush/Compact/Reclaim/Throttle。</summary>
    public sealed partial class StorageView
    {
        private readonly IMetricsSink _sink;
        private readonly int _rate;
        private readonly bool _enabled;
        private int _readCtr, _writeCtr, _flushCtr, _compactCtr;

        internal StorageView(IMetricsSink sink, int rate, bool enabled)
        { _sink = sink; _rate = rate; _enabled = enabled; }

        /// <summary>Storage 维度指标是否启用（Options.Metrics.Enabled &amp;&amp; EnableStorageMetrics 短路后的终值）。</summary>
        public bool IsEnabled => _enabled;

        /// <summary>Read 本次是否应采样（确定性百分比采样；维度关闭恒 false）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ShouldSampleRead() => _enabled && ShouldSample(ref _readCtr, _rate);
        /// <summary>Write 本次是否应采样（确定性百分比采样；维度关闭恒 false）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ShouldSampleWrite() => _enabled && ShouldSample(ref _writeCtr, _rate);
        /// <summary>Flush 本次是否应采样（确定性百分比采样；维度关闭恒 false）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ShouldSampleFlush() => _enabled && ShouldSample(ref _flushCtr, _rate);
        /// <summary>Compact 本次是否应采样（确定性百分比采样；维度关闭恒 false）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ShouldSampleCompact() => _enabled && ShouldSample(ref _compactCtr, _rate);

        /// <summary>开始 Read 计时——采样命中返回激活计时器；未命中返回空计时器（零开销）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public MicroTimer BeginReadSample() => MicroTimer.Start(ShouldSampleRead());
        /// <summary>开始 Write 计时——采样命中返回激活计时器；未命中返回空计时器（零开销）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public MicroTimer BeginWriteSample() => MicroTimer.Start(ShouldSampleWrite());
        /// <summary>开始 Flush 计时——采样命中返回激活计时器；未命中返回空计时器（零开销）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public MicroTimer BeginFlushSample() => MicroTimer.Start(ShouldSampleFlush());
        /// <summary>开始 Compact 计时——采样命中返回激活计时器；未命中返回空计时器（零开销）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public MicroTimer BeginCompactSample() => MicroTimer.Start(ShouldSampleCompact());

        /// <summary>上报 Read 延迟直方图（<c>storage.read.latency_us</c>，含字节数与错误码标签）。</summary>
        /// <param name="bytes">本次 Read 字节数。</param>
        /// <param name="latencyMicros">本次 Read 延迟（微秒）。</param>
        /// <param name="errorCode">错误码（0 = 无错误）。</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnRead(long bytes, long latencyMicros, int errorCode)
        {
            if (!_enabled) return;
            _sink.Histogram("storage.read.latency_us", latencyMicros,
                [Kv("bytes", bytes.ToString()), Kv("error_code", errorCode.ToString())]);
        }

        /// <summary>上报 Write 延迟直方图（<c>storage.write.latency_us</c>，含字节数与错误码标签）。</summary>
        /// <param name="bytes">本次 Write 字节数。</param>
        /// <param name="latencyMicros">本次 Write 延迟（微秒）。</param>
        /// <param name="errorCode">错误码（0 = 无错误）。</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnWrite(long bytes, long latencyMicros, int errorCode)
        {
            if (!_enabled) return;
            _sink.Histogram("storage.write.latency_us", latencyMicros,
                [Kv("bytes", bytes.ToString()), Kv("error_code", errorCode.ToString())]);
        }

        /// <summary>上报 Flush 延迟直方图（<c>storage.flush.latency_us</c>）。</summary>
        /// <param name="latencyMicros">本次 Flush 延迟（微秒）。</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnFlush(long latencyMicros)
        {
            if (!_enabled) return;
            _sink.Histogram("storage.flush.latency_us", latencyMicros, []);
        }

        /// <summary>上报 Compact 延迟直方图（<c>storage.compact.latency_us</c>，含压缩字节与替换段数标签）。</summary>
        /// <param name="latencyMicros">本次 Compact 延迟（微秒）。</param>
        /// <param name="bytesCompacted">本次压缩回收的字节数。</param>
        /// <param name="segmentsReplaced">本次替换的段数。</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnCompact(long latencyMicros, long bytesCompacted, int segmentsReplaced)
        {
            if (!_enabled) return;
            _sink.Histogram("storage.compact.latency_us", latencyMicros,
                [Kv("bytes", bytesCompacted.ToString()), Kv("segments", segmentsReplaced.ToString())]);
        }

        /// <summary>上报空间回收计数（<c>storage.reclaim</c>，含回收字节标签）。</summary>
        /// <param name="bytesReclaimed">本次回收的字节数。</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnReclaim(long bytesReclaimed)
        {
            if (!_enabled) return;
            _sink.Counter("storage.reclaim", [Kv("bytes", bytesReclaimed.ToString())]);
        }

        /// <summary>节流是低频背压信号，全采。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnThrottle()
        {
            if (!_enabled) return;
            _sink.Counter("storage.throttle", []);
        }

        /// <summary>上报在途 IO 队列深度（<c>storage.queue_depth</c>，瞬时 Gauge）。</summary>
        /// <param name="inflight">当前在途 IO 数。</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnQueueDepth(int inflight)
        {
            if (!_enabled) return;
            _sink.Gauge("storage.queue_depth", inflight, []);
        }
    }
}
