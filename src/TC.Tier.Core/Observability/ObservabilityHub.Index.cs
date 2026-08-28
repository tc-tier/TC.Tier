using System.Runtime.CompilerServices;

namespace TC.Tier.Core.Observability;

public sealed partial class ObservabilityHub
{
    /// <summary>Index 结构维度视图 —— Find/Insert/Upsert/Delete/Scan。</summary>
    public sealed partial class IndexView
    {
        private readonly IMetricsSink _sink;
        private readonly int _rate;
        private readonly bool _enabled;
        private int _findCtr, _insertCtr, _upsertCtr, _deleteCtr, _scanCtr;

        internal IndexView(IMetricsSink sink, int rate, bool enabled)
        { _sink = sink; _rate = rate; _enabled = enabled; }

        /// <summary>Index 维度指标是否启用（Options.Metrics.Enabled &amp;&amp; EnableIndexMetrics 短路后的终值）。</summary>
        public bool IsEnabled => _enabled;

        /// <summary>Find 本次是否应采样（确定性百分比采样；维度关闭恒 false）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ShouldSampleFind() => _enabled && ShouldSample(ref _findCtr, _rate);
        /// <summary>Insert 本次是否应采样（确定性百分比采样；维度关闭恒 false）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ShouldSampleInsert() => _enabled && ShouldSample(ref _insertCtr, _rate);
        /// <summary>Upsert 本次是否应采样（确定性百分比采样；维度关闭恒 false）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ShouldSampleUpsert() => _enabled && ShouldSample(ref _upsertCtr, _rate);
        /// <summary>Delete 本次是否应采样（确定性百分比采样；维度关闭恒 false）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ShouldSampleDelete() => _enabled && ShouldSample(ref _deleteCtr, _rate);
        /// <summary>Scan 本次是否应采样（确定性百分比采样；维度关闭恒 false）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ShouldSampleScan() => _enabled && ShouldSample(ref _scanCtr, _rate);

        /// <summary>开始 Find 计时——采样命中返回激活计时器；未命中返回空计时器（零开销）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public MicroTimer BeginFindSample() => MicroTimer.Start(ShouldSampleFind());
        /// <summary>开始 Insert 计时——采样命中返回激活计时器；未命中返回空计时器（零开销）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public MicroTimer BeginInsertSample() => MicroTimer.Start(ShouldSampleInsert());
        /// <summary>开始 Upsert 计时——采样命中返回激活计时器；未命中返回空计时器（零开销）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public MicroTimer BeginUpsertSample() => MicroTimer.Start(ShouldSampleUpsert());
        /// <summary>开始 Delete 计时——采样命中返回激活计时器；未命中返回空计时器（零开销）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public MicroTimer BeginDeleteSample() => MicroTimer.Start(ShouldSampleDelete());
        /// <summary>开始 Scan 计时——采样命中返回激活计时器；未命中返回空计时器（零开销）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public MicroTimer BeginScanSample() => MicroTimer.Start(ShouldSampleScan());

        /// <summary>上报 Find 延迟直方图（<c>index.find.latency_us</c>）。</summary>
        /// <param name="latencyMicros">本次 Find 延迟（微秒）。</param>
        /// <param name="hit">是否命中。</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnFind(long latencyMicros, bool hit)
        {
            if (!_enabled) return;
            _sink.Histogram("index.find.latency_us", latencyMicros, [Kv("hit", hit.ToString())]);
        }

        /// <summary>上报 Insert 延迟直方图（<c>index.insert.latency_us</c>，含键值尺寸标签）。</summary>
        /// <param name="latencyMicros">本次 Insert 延迟（微秒）。</param>
        /// <param name="keySize">键尺寸（字节）。</param>
        /// <param name="valueSize">值尺寸（字节）。</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnInsert(long latencyMicros, int keySize, int valueSize)
        {
            if (!_enabled) return;
            _sink.Histogram("index.insert.latency_us", latencyMicros,
                [Kv("key_size", keySize.ToString()), Kv("value_size", valueSize.ToString())]);
        }

        /// <summary>上报 Upsert 延迟直方图（<c>index.upsert.latency_us</c>，含键值尺寸标签）。</summary>
        /// <param name="latencyMicros">本次 Upsert 延迟（微秒）。</param>
        /// <param name="keySize">键尺寸（字节）。</param>
        /// <param name="valueSize">值尺寸（字节）。</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnUpsert(long latencyMicros, int keySize, int valueSize)
        {
            if (!_enabled) return;
            _sink.Histogram("index.upsert.latency_us", latencyMicros,
                [Kv("key_size", keySize.ToString()), Kv("value_size", valueSize.ToString())]);
        }

        /// <summary>上报 Delete 延迟直方图（<c>index.delete.latency_us</c>）。</summary>
        /// <param name="latencyMicros">本次 Delete 延迟（微秒）。</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnDelete(long latencyMicros)
        {
            if (!_enabled) return;
            _sink.Histogram("index.delete.latency_us", latencyMicros, []);
        }

        /// <summary>上报 Scan 延迟直方图（<c>index.scan.latency_us</c>，含返回条数标签）。</summary>
        /// <param name="latencyMicros">本次 Scan 延迟（微秒）。</param>
        /// <param name="itemsReturned">本次 Scan 返回的条目数。</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnScan(long latencyMicros, int itemsReturned)
        {
            if (!_enabled) return;
            _sink.Histogram("index.scan.latency_us", latencyMicros,
                [Kv("items", itemsReturned.ToString())]);
        }
    }
}
