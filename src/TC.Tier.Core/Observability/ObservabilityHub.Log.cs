using System.Runtime.CompilerServices;

namespace TC.Tier.Core.Observability;

public sealed partial class ObservabilityHub
{
    /// <summary>Log 结构维度视图 —— Append/Commit/Truncate/Recover。</summary>
    public sealed partial class LogView
    {
        private readonly IMetricsSink _sink;
        private readonly int _rate;
        private readonly bool _enabled;
        private int _appendCtr, _commitCtr;

        internal LogView(IMetricsSink sink, int rate, bool enabled)
        { _sink = sink; _rate = rate; _enabled = enabled; }

        public bool IsEnabled => _enabled;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ShouldSampleAppend() => _enabled && ShouldSample(ref _appendCtr, _rate);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ShouldSampleCommit() => _enabled && ShouldSample(ref _commitCtr, _rate);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public MicroTimer BeginAppendSample() => MicroTimer.Start(ShouldSampleAppend());
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public MicroTimer BeginCommitSample() => MicroTimer.Start(ShouldSampleCommit());

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnAppend(long latencyMicros, int entrySize)
        {
            if (!_enabled) return;
            _sink.Histogram("log.append.latency_us", latencyMicros,
                [Kv("entry_size", entrySize.ToString())]);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnCommit(long latencyMicros, long committedOffset)
        {
            if (!_enabled) return;
            _sink.Histogram("log.commit.latency_us", latencyMicros,
                [Kv("committed_offset", committedOffset.ToString())]);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnTruncate(bool isPrefix, long bytesTruncated)
        {
            if (!_enabled) return;
            _sink.Counter("log.truncate",
                [Kv("is_prefix", isPrefix.ToString()), Kv("bytes_truncated", bytesTruncated.ToString())]);
        }

        /// <summary>BufferFull 是背压关键信号，全采。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnBufferFull()
        {
            if (!_enabled) return;
            _sink.Counter("log.buffer_full", []);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnRecover(long latencyMicros, long bytesScanned)
        {
            if (!_enabled) return;
            _sink.Histogram("log.recover.latency_us", latencyMicros,
                [Kv("bytes_scanned", bytesScanned.ToString())]);
        }
    }
}
