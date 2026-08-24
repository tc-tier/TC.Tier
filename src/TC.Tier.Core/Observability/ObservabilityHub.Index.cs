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

        public bool IsEnabled => _enabled;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ShouldSampleFind() => _enabled && ShouldSample(ref _findCtr, _rate);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ShouldSampleInsert() => _enabled && ShouldSample(ref _insertCtr, _rate);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ShouldSampleUpsert() => _enabled && ShouldSample(ref _upsertCtr, _rate);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ShouldSampleDelete() => _enabled && ShouldSample(ref _deleteCtr, _rate);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ShouldSampleScan() => _enabled && ShouldSample(ref _scanCtr, _rate);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public MicroTimer BeginFindSample() => MicroTimer.Start(ShouldSampleFind());
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public MicroTimer BeginInsertSample() => MicroTimer.Start(ShouldSampleInsert());
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public MicroTimer BeginUpsertSample() => MicroTimer.Start(ShouldSampleUpsert());
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public MicroTimer BeginDeleteSample() => MicroTimer.Start(ShouldSampleDelete());
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public MicroTimer BeginScanSample() => MicroTimer.Start(ShouldSampleScan());

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnFind(long latencyMicros, bool hit)
        {
            if (!_enabled) return;
            _sink.Histogram("index.find.latency_us", latencyMicros, [Kv("hit", hit.ToString())]);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnInsert(long latencyMicros, int keySize, int valueSize)
        {
            if (!_enabled) return;
            _sink.Histogram("index.insert.latency_us", latencyMicros,
                [Kv("key_size", keySize.ToString()), Kv("value_size", valueSize.ToString())]);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnUpsert(long latencyMicros, int keySize, int valueSize)
        {
            if (!_enabled) return;
            _sink.Histogram("index.upsert.latency_us", latencyMicros,
                [Kv("key_size", keySize.ToString()), Kv("value_size", valueSize.ToString())]);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnDelete(long latencyMicros)
        {
            if (!_enabled) return;
            _sink.Histogram("index.delete.latency_us", latencyMicros, []);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnScan(long latencyMicros, int itemsReturned)
        {
            if (!_enabled) return;
            _sink.Histogram("index.scan.latency_us", latencyMicros,
                [Kv("items", itemsReturned.ToString())]);
        }
    }
}
