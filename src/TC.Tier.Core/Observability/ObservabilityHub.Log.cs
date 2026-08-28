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

        /// <summary>Log 维度指标是否启用（Options.Metrics.Enabled &amp;&amp; EnableLogMetrics 短路后的终值）。</summary>
        public bool IsEnabled => _enabled;

        /// <summary>Append 本次是否应采样（确定性百分比采样；维度关闭恒 false）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ShouldSampleAppend() => _enabled && ShouldSample(ref _appendCtr, _rate);
        /// <summary>Commit 本次是否应采样（确定性百分比采样；维度关闭恒 false）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ShouldSampleCommit() => _enabled && ShouldSample(ref _commitCtr, _rate);

        /// <summary>开始 Append 计时——采样命中返回激活计时器；未命中返回空计时器（零开销）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public MicroTimer BeginAppendSample() => MicroTimer.Start(ShouldSampleAppend());
        /// <summary>开始 Commit 计时——采样命中返回激活计时器；未命中返回空计时器（零开销）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public MicroTimer BeginCommitSample() => MicroTimer.Start(ShouldSampleCommit());

        /// <summary>上报 Append 延迟直方图（<c>log.append.latency_us</c>，含条目尺寸标签）。</summary>
        /// <param name="latencyMicros">本次 Append 延迟（微秒）。</param>
        /// <param name="entrySize">本次追加的条目尺寸（字节）。</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnAppend(long latencyMicros, int entrySize)
        {
            if (!_enabled) return;
            _sink.Histogram("log.append.latency_us", latencyMicros,
                [Kv("entry_size", entrySize.ToString())]);
        }

        /// <summary>上报 Commit 延迟直方图（<c>log.commit.latency_us</c>，含提交偏移标签）。</summary>
        /// <param name="latencyMicros">本次 Commit 延迟（微秒）。</param>
        /// <param name="committedOffset">本次提交推进到的日志偏移。</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnCommit(long latencyMicros, long committedOffset)
        {
            if (!_enabled) return;
            _sink.Histogram("log.commit.latency_us", latencyMicros,
                [Kv("committed_offset", committedOffset.ToString())]);
        }

        /// <summary>上报日志截断计数（<c>log.truncate</c>，含方向与截断字节标签）。</summary>
        /// <param name="isPrefix">true = 截前缀；false = 截后缀。</param>
        /// <param name="bytesTruncated">本次截断的字节数。</param>
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

        /// <summary>上报恢复扫描延迟直方图（<c>log.recover.latency_us</c>，含扫描字节标签）。</summary>
        /// <param name="latencyMicros">本次恢复延迟（微秒）。</param>
        /// <param name="bytesScanned">本次恢复扫描的字节数。</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnRecover(long latencyMicros, long bytesScanned)
        {
            if (!_enabled) return;
            _sink.Histogram("log.recover.latency_us", latencyMicros,
                [Kv("bytes_scanned", bytesScanned.ToString())]);
        }
    }
}
