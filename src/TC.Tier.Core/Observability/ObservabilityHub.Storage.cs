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

        public bool IsEnabled => _enabled;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ShouldSampleRead() => _enabled && ShouldSample(ref _readCtr, _rate);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ShouldSampleWrite() => _enabled && ShouldSample(ref _writeCtr, _rate);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ShouldSampleFlush() => _enabled && ShouldSample(ref _flushCtr, _rate);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ShouldSampleCompact() => _enabled && ShouldSample(ref _compactCtr, _rate);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public MicroTimer BeginReadSample() => MicroTimer.Start(ShouldSampleRead());
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public MicroTimer BeginWriteSample() => MicroTimer.Start(ShouldSampleWrite());
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public MicroTimer BeginFlushSample() => MicroTimer.Start(ShouldSampleFlush());
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public MicroTimer BeginCompactSample() => MicroTimer.Start(ShouldSampleCompact());

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnRead(long bytes, long latencyMicros, int errorCode)
        {
            if (!_enabled) return;
            _sink.Histogram("storage.read.latency_us", latencyMicros,
                [Kv("bytes", bytes.ToString()), Kv("error_code", errorCode.ToString())]);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnWrite(long bytes, long latencyMicros, int errorCode)
        {
            if (!_enabled) return;
            _sink.Histogram("storage.write.latency_us", latencyMicros,
                [Kv("bytes", bytes.ToString()), Kv("error_code", errorCode.ToString())]);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnFlush(long latencyMicros)
        {
            if (!_enabled) return;
            _sink.Histogram("storage.flush.latency_us", latencyMicros, []);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnCompact(long latencyMicros, long bytesCompacted, int segmentsReplaced)
        {
            if (!_enabled) return;
            _sink.Histogram("storage.compact.latency_us", latencyMicros,
                [Kv("bytes", bytesCompacted.ToString()), Kv("segments", segmentsReplaced.ToString())]);
        }

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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnQueueDepth(int inflight)
        {
            if (!_enabled) return;
            _sink.Gauge("storage.queue_depth", inflight, []);
        }
    }
}
