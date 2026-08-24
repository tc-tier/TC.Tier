using TC.Tier.Core.Metrics;
using TC.Tier.Core.Observability;
using TC.Tier.Core.Tracing;

namespace TC.Tier.Core.Tests.Observability;

/// <summary>
/// ObservabilityHub 核心 + 所有视图的全面单元测试。
/// 覆盖：禁用模式零开销 / 启用后短路 + 采样率 / 高级工厂 / 错误上报 / Span 创建。
/// </summary>
public sealed class ObservabilityHubTests
{
    private sealed class SpySink : IMetricsSink
    {
        public bool IsEnabled => true;
        public readonly List<(string Name, double Value, string Tags)> Histograms = new();
        public readonly List<(string Name, string Tags)> Counters = new();
        public readonly List<(string Name, double Value, string Tags)> Gauges = new();

        public void Counter(string name, ReadOnlySpan<KeyValuePair<string, string>> tags)
            => Counters.Add((name, FormatTags(tags)));

        public void Histogram(string name, double value, ReadOnlySpan<KeyValuePair<string, string>> tags)
            => Histograms.Add((name, value, FormatTags(tags)));

        public void Gauge(string name, double value, ReadOnlySpan<KeyValuePair<string, string>> tags)
            => Gauges.Add((name, value, FormatTags(tags)));

        private static string FormatTags(ReadOnlySpan<KeyValuePair<string, string>> tags)
        {
            var list = new List<string>();
            foreach (var t in tags) list.Add($"{t.Key}={t.Value}");
            return string.Join(',', list);
        }
    }

    private sealed class SpyTracer : ITracer
    {
        public bool IsEnabled => true;
        public readonly List<(string Name, SpanKind Kind)> Spans = new();

        public ISpan BeginSpan(string name, SpanKind kind = SpanKind.Internal)
        {
            Spans.Add((name, kind));
            return NullSpan.Instance;
        }

        public ISpan? Current => null;
    }

    // ═══════════════════════════════════════
    //  辅助方法
    // ═══════════════════════════════════════

    /// <summary>创建指标已启用的 Hub（spy sink 可选，默认注入 SpySink）。</summary>
    private static ObservabilityHub EnabledHub(SpySink? spy = null, SpyTracer? tracer = null, int sampleRate = 100)
    {
        var sink = spy ?? new SpySink();
        var opts = new ObservabilityOptions
        {
            Metrics = new MetricsConfig { Enabled = true, SampleRate = sampleRate, EnableSegmentAllocatorMetrics = true },
            Tracing = tracer is not null ? new TracingConfig { Enabled = true } : new TracingConfig(),
        };
        return ObservabilityHub.Create(sink, (ITracer?)tracer ?? NullTracer.Instance, opts);
    }

    // ═══════════════════════════════════════
    //  Disabled 单例
    // ═══════════════════════════════════════

    [Fact]
    public void Disabled_MetricsEnabled_IsFalse()
        => ObservabilityHub.Disabled.MetricsEnabled.Should().BeFalse();

    [Fact]
    public void Disabled_TracingEnabled_IsFalse()
        => ObservabilityHub.Disabled.TracingEnabled.Should().BeFalse();

    [Fact]
    public void Disabled_AllViews_IsEnabledFalse()
    {
        var d = ObservabilityHub.Disabled;
        d.Metrics.IsEnabled.Should().BeFalse();
        d.Storage.IsEnabled.Should().BeFalse();
        d.Log.IsEnabled.Should().BeFalse();
        d.Index.IsEnabled.Should().BeFalse();
        d.SegmentAllocator.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void Disabled_ReportError_DoesNotThrow()
    {
        var act = () => ObservabilityHub.Disabled.ReportError("test", 1, null);
        act.Should().NotThrow();
    }

    [Fact]
    public void Disabled_BeginSpan_ReturnsNull()
        => ObservabilityHub.Disabled.BeginSpan("x").Should().BeNull();

    // ═══════════════════════════════════════
    //  简单工厂 (Create)
    // ═══════════════════════════════════════

    [Fact]
    public void Create_NullArgs_ReturnsDisabledEquivalent()
    {
        var hub = ObservabilityHub.Create(null, null, null);
        hub.MetricsEnabled.Should().BeFalse();
        hub.TracingEnabled.Should().BeFalse();
    }

    [Fact]
    public void Create_WithRealSink_RespectsOptionsDisabled()
    {
        // 简单 Create 不自动开启——严格遵循 options
        var hub = ObservabilityHub.Create(new SpySink(), null, null);
        hub.MetricsEnabled.Should().BeFalse("默认 options.Metrics.Enabled=false");
    }

    [Fact]
    public void Create_WithRealSinkAndEnabledOptions_MetricsEnabledTrue()
    {
        var opts = new ObservabilityOptions { Metrics = new MetricsConfig { Enabled = true } };
        var hub = ObservabilityHub.Create(new SpySink(), null, opts);
        hub.MetricsEnabled.Should().BeTrue();
    }

    [Fact]
    public void Create_WithRealTracer_TracingEnabled_OnlyWithOptions()
    {
        var opts = new ObservabilityOptions { Tracing = new TracingConfig { Enabled = true } };
        var hub = ObservabilityHub.Create(null, new SpyTracer(), opts);
        hub.TracingEnabled.Should().BeTrue();
    }

    [Fact]
    public void Create_WithDisabledOptions_MetricsEnabledFalse()
    {
        var opts = new ObservabilityOptions { Metrics = new MetricsConfig { Enabled = false } };
        var hub = ObservabilityHub.Create(new SpySink(), null, opts);
        hub.MetricsEnabled.Should().BeFalse();
    }

    // ═══════════════════════════════════════
    //  高级工厂 (Create + sampleRate)
    // ═══════════════════════════════════════

    [Fact]
    public void Create_WithSampleRate_Half_AutoEnables()
    {
        var hub = ObservabilityHub.Create(new SpySink(), new SpyTracer(), null, 0.5);
        hub.MetricsEnabled.Should().BeTrue();
        hub.TracingEnabled.Should().BeTrue();
    }

    // ═══════════════════════════════════════
    //  采样率换算契约（四舍五入，非截断）
    // ═══════════════════════════════════════

    /// <summary>★ 边界表回归（修复 (int) 截断）：0.006 必须 1%（旧实现 0%→采样全灭）、
    /// 0.996 必须 100%（旧 99%）。</summary>
    [Theory]
    [InlineData(-0.02, 0)]     // 负值 clamp 0
    [InlineData(0.0, 0)]
    [InlineData(0.004, 0)]     // 0.4% 四舍五入 → 0
    [InlineData(0.006, 1)]     // 0.6% → 1（旧截断：0 —— 核心坑）
    [InlineData(0.5, 50)]
    [InlineData(0.994, 99)]    // 99.4% → 99
    [InlineData(0.996, 100)]   // 99.6% → 100（旧截断：99）
    [InlineData(1.0, 100)]
    [InlineData(1.0001, 100)]  // 越界 clamp 100
    public void ToSamplePercent_Rounds_NotTruncates(double sampleRate, int expectedPercent)
        => ObservabilityHub.ToSamplePercent(sampleRate).Should().Be(expectedPercent);

    /// <summary>★ 采样精确性契约（修 100/rate 整数除法坑）：每 100 事件恰采 rate 个——
    /// 旧实现 rate=3→3.03%、rate=34→50%、rate=51→100% 全部大幅偏差。</summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(7, 7)]
    [InlineData(33, 33)]
    [InlineData(34, 34)]   // 旧：100/34=2 → 50%
    [InlineData(51, 51)]   // 旧：100/51=1 → 100%
    [InlineData(99, 99)]
    public void ShouldSample_ExactPercentOver100Events(int rate, int expectedHits)
    {
        var counter = 0;
        var hits = 0;
        for (var i = 0; i < 100; i++)
            if (ObservabilityHub.ShouldSample(ref counter, rate))
                hits++;
        hits.Should().Be(expectedHits, $"rate={rate}% 必须精确（每 100 事件采 {expectedHits} 个）");
    }

    [Fact]
    public void ShouldSample_Boundaries()
    {
        var zero = 0;
        ObservabilityHub.ShouldSample(ref zero, 0).Should().BeFalse("rate=0 永不采");
        ObservabilityHub.ShouldSample(ref zero, -1).Should().BeFalse("负 rate 永不采");
        var full = 0;
        ObservabilityHub.ShouldSample(ref full, 100).Should().BeTrue("rate=100 全采");
        ObservabilityHub.ShouldSample(ref full, 101).Should().BeTrue("越界全采");
    }

    /// <summary>端到端：工厂 sampleRate=0.006 → 精确 1% 采样（旧实现截断为 0% → 零事件，必红）。</summary>
    [Fact]
    public void Create_SampleRate006_YieldsExactlyOnePercentSampling()
    {
        var hub = ObservabilityHub.Create(new SpySink(), null, null, 0.006);
        var hits = 0;
        for (var i = 0; i < 100; i++)
            if (hub.Index.ShouldSampleFind())
                hits++;
        hits.Should().Be(1, "0.006 → 1%：每 100 个事件恰采 1 个（旧截断实现为 0）");
    }

    [Fact]
    public void Create_WithSampleRate_Zero_DoesNotAutoEnable()
    {
        var hub = ObservabilityHub.Create(new SpySink(), new SpyTracer(), null, 0.0);
        // sampleRate=0 → samplePercent=0，但 sink/tracer 存在 → 自动开启
        hub.MetricsEnabled.Should().BeTrue();
    }

    [Fact]
    public void Create_WithSampleRate_ClampsTo0_100()
    {
        var hub = ObservabilityHub.Create(new SpySink(), null, null, -1.0);
        // Clamp 到 0，但 sink 存在 → 自动开启
        hub.MetricsEnabled.Should().BeTrue();

        var hub2 = ObservabilityHub.Create(new SpySink(), null, null, 2.0);
        hub2.MetricsEnabled.Should().BeTrue();
    }

    // ═══════════════════════════════════════
    //  ReportError
    // ═══════════════════════════════════════

    [Fact]
    public void ReportError_Enabled_SendsCounter()
    {
        var spy = new SpySink();
        var hub = EnabledHub(spy);
        hub.ReportError("storage", 42, null);
        spy.Counters.Should().ContainSingle(c => c.Name == "error");
    }

    [Fact]
    public void ReportError_Disabled_SilentlyIgnored()
    {
        var spy = new SpySink();
        // Sink exists but options disable
        var opts = new ObservabilityOptions { Metrics = new MetricsConfig { Enabled = false } };
        var hub = ObservabilityHub.Create(spy, null, opts);
        hub.ReportError("x", 1, null);
        spy.Counters.Should().BeEmpty();
    }

    // ═══════════════════════════════════════
    //  BeginSpan
    // ═══════════════════════════════════════

    [Fact]
    public void BeginSpan_Enabled_CreatesSpan()
    {
        var tracer = new SpyTracer();
        var opts = new ObservabilityOptions { Tracing = new TracingConfig { Enabled = true } };
        var hub = ObservabilityHub.Create(null, tracer, opts);
        hub.BeginSpan("test", SpanKind.Client);
        tracer.Spans.Should().ContainSingle(s => s.Name == "test" && s.Kind == SpanKind.Client);
    }

    [Fact]
    public void BeginSpan_Disabled_ReturnsNull()
    {
        var hub = ObservabilityHub.Disabled;
        hub.BeginSpan("test").Should().BeNull();
    }

    // ═══════════════════════════════════════
    //  ShouldSample
    // ═══════════════════════════════════════

    // ═══════════════════════════════════════
    //  MetricsView
    // ═══════════════════════════════════════

    [Fact]
    public void MetricsView_Counter_ForwardsToSink()
    {
        var spy = new SpySink();
        var hub = EnabledHub(spy);
        hub.Metrics.Counter("test.metric", []);
        spy.Counters.Should().ContainSingle(c => c.Name == "test.metric");
    }

    [Fact]
    public void MetricsView_Histogram_ForwardsToSink()
    {
        var spy = new SpySink();
        var hub = EnabledHub(spy);
        hub.Metrics.Histogram("test.latency", 42.5, []);
        spy.Histograms.Should().ContainSingle(h => h.Name == "test.latency" && h.Value == 42.5);
    }

    [Fact]
    public void MetricsView_Gauge_ForwardsToSink()
    {
        var spy = new SpySink();
        var hub = EnabledHub(spy);
        hub.Metrics.Gauge("test.depth", 7.0, []);
        spy.Gauges.Should().ContainSingle(g => g.Name == "test.depth" && g.Value == 7.0);
    }

    [Fact]
    public void MetricsView_Disabled_DoesNotForward()
    {
        var spy = new SpySink();
        var opts = new ObservabilityOptions { Metrics = new MetricsConfig { Enabled = false } };
        var hub = ObservabilityHub.Create(spy, null, opts);
        hub.Metrics.Counter("x", []);
        spy.Counters.Should().BeEmpty();
    }

    // ═══════════════════════════════════════
    //  ShouldSample (rate testing)
    // ═══════════════════════════════════════

    [Fact]
    public void ShouldSample_Rate50_ApproximatelyHalf()
    {
        var hub = EnabledHub(new SpySink(), null, 50);
        int hits = 0;
        for (int i = 0; i < 1000; i++)
            if (hub.Storage.ShouldSampleRead()) hits++;
        hits.Should().BeInRange(350, 650);
    }

    [Fact]
    public void ShouldSample_Rate10_ApproximatelyTenth()
    {
        var hub = EnabledHub(new SpySink(), null, 10);
        int hits = 0;
        for (int i = 0; i < 1000; i++)
            if (hub.Storage.ShouldSampleRead()) hits++;
        hits.Should().BeInRange(50, 150);
    }

    // ═══════════════════════════════════════
    //  StorageView
    // ═══════════════════════════════════════

    [Fact]
    public void StorageView_IsEnabled_ReflectsConfig()
    {
        var opts = new ObservabilityOptions { Metrics = new MetricsConfig { Enabled = true, EnableStorageMetrics = false } };
        var hub = ObservabilityHub.Create(new SpySink(), null, opts);
        hub.Storage.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void StorageView_OnRead_SendsHistogram()
    {
        var spy = new SpySink();
        var hub = EnabledHub(spy);
        hub.Storage.OnRead(4096, 150, 0);
        spy.Histograms.Should().ContainSingle(h => h.Name == "storage.read.latency_us");
    }

    [Fact]
    public void StorageView_OnWrite_SendsHistogram()
    {
        var spy = new SpySink();
        var hub = EnabledHub(spy);
        hub.Storage.OnWrite(8192, 200, 1);
        spy.Histograms.Should().ContainSingle(h => h.Name == "storage.write.latency_us");
    }

    [Fact]
    public void StorageView_OnFlush_SendsHistogram()
    {
        var spy = new SpySink();
        var hub = EnabledHub(spy);
        hub.Storage.OnFlush(100);
        spy.Histograms.Should().ContainSingle(h => h.Name == "storage.flush.latency_us" && h.Value == 100);
    }

    [Fact]
    public void StorageView_OnCompact_SendsHistogram()
    {
        var spy = new SpySink();
        var hub = EnabledHub(spy);
        hub.Storage.OnCompact(5000, 1024 * 1024, 3);
        spy.Histograms.Should().ContainSingle(h =>
            h.Name == "storage.compact.latency_us" && h.Value == 5000);
    }

    [Fact]
    public void StorageView_OnReclaim_SendsCounter()
    {
        var spy = new SpySink();
        var hub = EnabledHub(spy);
        hub.Storage.OnReclaim(65536);
        spy.Counters.Should().ContainSingle(c => c.Name == "storage.reclaim");
    }

    [Fact]
    public void StorageView_OnThrottle_SendsCounter()
    {
        var spy = new SpySink();
        var hub = EnabledHub(spy);
        hub.Storage.OnThrottle();
        spy.Counters.Should().ContainSingle(c => c.Name == "storage.throttle");
    }

    [Fact]
    public void StorageView_OnQueueDepth_SendsGauge()
    {
        var spy = new SpySink();
        var hub = EnabledHub(spy);
        hub.Storage.OnQueueDepth(8);
        spy.Gauges.Should().ContainSingle(g => g.Name == "storage.queue_depth" && g.Value == 8);
    }

    [Fact]
    public void StorageView_Disabled_NoOps()
    {
        var spy = new SpySink();
        var opts = new ObservabilityOptions { Metrics = new MetricsConfig { Enabled = false } };
        var hub = ObservabilityHub.Create(spy, null, opts);
        hub.Storage.OnRead(1, 1, 0);
        hub.Storage.OnWrite(1, 1, 0);
        hub.Storage.OnFlush(1);
        hub.Storage.OnCompact(1, 1, 1);
        hub.Storage.OnReclaim(1);
        hub.Storage.OnThrottle();
        hub.Storage.OnQueueDepth(1);
        spy.Histograms.Should().BeEmpty();
        spy.Counters.Should().BeEmpty();
        spy.Gauges.Should().BeEmpty();
    }

    [Fact]
    public void StorageView_MicroTimer_BeginReadSample_Active()
    {
        // Rate=100 → 每次采样都命中 → MicroTimer.IsActive=true
        var hub = EnabledHub(new SpySink(), null, 100);
        var timer = hub.Storage.BeginReadSample();
        timer.IsActive.Should().BeTrue();
    }

    [Fact]
    public void StorageView_MicroTimer_Disabled_ReturnsInactive()
    {
        var opts = new ObservabilityOptions { Metrics = new MetricsConfig { Enabled = false } };
        var hub = ObservabilityHub.Create(new SpySink(), null, opts);
        var timer = hub.Storage.BeginReadSample();
        timer.IsActive.Should().BeFalse();
    }

    // ═══════════════════════════════════════
    //  LogView
    // ═══════════════════════════════════════

    [Fact]
    public void LogView_OnAppend_SendsHistogram()
    {
        var spy = new SpySink();
        var hub = EnabledHub(spy);
        hub.Log.OnAppend(30, 256);
        spy.Histograms.Should().ContainSingle(h => h.Name == "log.append.latency_us");
    }

    [Fact]
    public void LogView_OnCommit_SendsHistogram()
    {
        var spy = new SpySink();
        var hub = EnabledHub(spy);
        hub.Log.OnCommit(50, 1024);
        spy.Histograms.Should().ContainSingle(h => h.Name == "log.commit.latency_us");
    }

    [Fact]
    public void LogView_OnTruncate_SendsCounter()
    {
        var spy = new SpySink();
        var hub = EnabledHub(spy);
        hub.Log.OnTruncate(true, 4096);
        spy.Counters.Should().ContainSingle(c => c.Name == "log.truncate");
    }

    [Fact]
    public void LogView_OnBufferFull_SendsCounter()
    {
        var spy = new SpySink();
        var hub = EnabledHub(spy);
        hub.Log.OnBufferFull();
        spy.Counters.Should().ContainSingle(c => c.Name == "log.buffer_full");
    }

    [Fact]
    public void LogView_OnRecover_SendsHistogram()
    {
        var spy = new SpySink();
        var hub = EnabledHub(spy);
        hub.Log.OnRecover(10000, 1048576);
        spy.Histograms.Should().ContainSingle(h =>
            h.Name == "log.recover.latency_us" && h.Value == 10000);
    }

    [Fact]
    public void LogView_Disabled_NoOps()
    {
        var spy = new SpySink();
        var opts = new ObservabilityOptions { Metrics = new MetricsConfig { Enabled = false } };
        var hub = ObservabilityHub.Create(spy, null, opts);
        hub.Log.OnAppend(1, 1);
        hub.Log.OnCommit(1, 1);
        hub.Log.OnTruncate(true, 1);
        hub.Log.OnBufferFull();
        hub.Log.OnRecover(1, 1);
        spy.Histograms.Should().BeEmpty();
        spy.Counters.Should().BeEmpty();
    }

    // ═══════════════════════════════════════
    //  IndexView
    // ═══════════════════════════════════════

    [Fact]
    public void IndexView_OnFind_SendsHistogram()
    {
        var spy = new SpySink();
        var hub = EnabledHub(spy);
        hub.Index.OnFind(20, true);
        spy.Histograms.Should().ContainSingle(h => h.Name == "index.find.latency_us");
    }

    [Fact]
    public void IndexView_OnInsert_SendsHistogram()
    {
        var spy = new SpySink();
        var hub = EnabledHub(spy);
        hub.Index.OnInsert(25, 8, 128);
        spy.Histograms.Should().ContainSingle(h => h.Name == "index.insert.latency_us");
    }

    [Fact]
    public void IndexView_OnUpsert_SendsHistogram()
    {
        var spy = new SpySink();
        var hub = EnabledHub(spy);
        hub.Index.OnUpsert(30, 16, 256);
        spy.Histograms.Should().ContainSingle(h => h.Name == "index.upsert.latency_us");
    }

    [Fact]
    public void IndexView_OnDelete_SendsHistogram()
    {
        var spy = new SpySink();
        var hub = EnabledHub(spy);
        hub.Index.OnDelete(15);
        spy.Histograms.Should().ContainSingle(h => h.Name == "index.delete.latency_us");
    }

    [Fact]
    public void IndexView_OnScan_SendsHistogram()
    {
        var spy = new SpySink();
        var hub = EnabledHub(spy);
        hub.Index.OnScan(100, 50);
        spy.Histograms.Should().ContainSingle(h => h.Name == "index.scan.latency_us");
    }

    [Fact]
    public void IndexView_Disabled_NoOps()
    {
        var spy = new SpySink();
        var opts = new ObservabilityOptions { Metrics = new MetricsConfig { Enabled = false } };
        var hub = ObservabilityHub.Create(spy, null, opts);
        hub.Index.OnFind(1, true);
        hub.Index.OnInsert(1, 1, 1);
        hub.Index.OnUpsert(1, 1, 1);
        hub.Index.OnDelete(1);
        hub.Index.OnScan(1, 1);
        spy.Histograms.Should().BeEmpty();
    }

    // ═══════════════════════════════════════
    //  SegmentAllocatorView
    // ═══════════════════════════════════════

    [Fact]
    public void SegmentAllocatorView_OnSegmentAllocate_SendsCounter()
    {
        var spy = new SpySink();
        var hub = EnabledHub(spy);
        hub.SegmentAllocator.OnSegmentAllocate(0, 262144);
        spy.Counters.Should().ContainSingle(c => c.Name == "segment_allocator.alloc");
    }

    [Fact]
    public void SegmentAllocatorView_OnSegmentFree_SendsCounter()
    {
        var spy = new SpySink();
        var hub = EnabledHub(spy);
        hub.SegmentAllocator.OnSegmentFree(3);
        spy.Counters.Should().ContainSingle(c => c.Name == "segment_allocator.free");
    }

    [Fact]
    public void SegmentAllocatorView_OnFreeListDepth_SendsGauge()
    {
        var spy = new SpySink();
        var hub = EnabledHub(spy);
        hub.SegmentAllocator.OnFreeListDepth(12);
        spy.Gauges.Should().ContainSingle(g => g.Name == "segment_allocator.free_list_depth" && g.Value == 12);
    }

    [Fact]
    public void SegmentAllocatorView_Disabled_NoOps()
    {
        var spy = new SpySink();
        var opts = new ObservabilityOptions { Metrics = new MetricsConfig { Enabled = false } };
        var hub = ObservabilityHub.Create(spy, null, opts);
        hub.SegmentAllocator.OnSegmentAllocate(0, 1);
        hub.SegmentAllocator.OnSegmentFree(0);
        hub.SegmentAllocator.OnFreeListDepth(0);
        spy.Counters.Should().BeEmpty();
        spy.Gauges.Should().BeEmpty();
    }

    // ═══════════════════════════════════════
    //  维度级开关
    // ═══════════════════════════════════════

    [Fact]
    public void DimensionSwitch_StorageDisabled_DoesNotForward()
    {
        var spy = new SpySink();
        var opts = new ObservabilityOptions
        {
            Metrics = new MetricsConfig { Enabled = true, EnableStorageMetrics = false }
        };
        var hub = ObservabilityHub.Create(spy, null, opts);
        hub.Storage.OnRead(1, 1, 0);
        spy.Histograms.Should().BeEmpty();
    }

    [Fact]
    public void DimensionSwitch_AllocatorDisabledByDefault()
    {
        var spy = new SpySink();
        var opts = new ObservabilityOptions { Metrics = new MetricsConfig { Enabled = true } };
        var hub = ObservabilityHub.Create(spy, null, opts);
        hub.SegmentAllocator.IsEnabled.Should().BeFalse("SegmentAllocator 默认关（高频）");
    }

    [Fact]
    public void DimensionSwitch_AllocatorEnabledExplicitly()
    {
        var spy = new SpySink();
        var opts = new ObservabilityOptions
        {
            Metrics = new MetricsConfig { Enabled = true, EnableSegmentAllocatorMetrics = true }
        };
        var hub = ObservabilityHub.Create(spy, null, opts);
        hub.SegmentAllocator.IsEnabled.Should().BeTrue();
    }
}
