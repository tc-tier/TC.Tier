using System.Runtime.CompilerServices;

namespace TC.Tier.Core.Observability;

/// <summary>
/// 可观测性聚合入口 —— TC.Tier 唯一可观测性接入点。
/// <para>★ 外部配置一个 Hub，分发到各引擎/结构。底层组件只持 Hub 引用（或子视图），调用极简。</para>
/// <para>★ 默认零开销：不配置时用 <see cref="Disabled"/> 单例，所有 IsEnabled 返回 false。</para>
/// <para>★ 内部封装两级短路（Options.Enabled &amp;&amp; sink/tracer.IsEnabled）+ 维度开关 + 采样率。</para>
/// <para>★ partial：<see cref="MetricsView"/> + 分类视图（<see cref="StorageView"/>/<see cref="LogView"/>/
///   <see cref="IndexView"/>/<see cref="SegmentAllocatorView"/>）定义在单独 partial 文件中。</para>
/// </summary>
public sealed partial class ObservabilityHub
{
    private readonly IMetricsSink _metrics;
    private readonly ITracer _tracer;
    private readonly ObservabilityOptions _options;

    // === 视图 ===

    /// <summary>三原语公共视图（Counter/Histogram/Gauge + IsEnabled 短路）。</summary>
    /// <remarks>★ MetricsView 仅封装三原语，所有维度视图都依赖它（避免重复短路）。</remarks>
    public MetricsView Metrics { get; }

    /// <summary>Storage Engine IO 维度视图（Read/Write/Flush/Compact/Reclaim/Throttle）。</summary>
    /// <remarks>★ StorageView 仅封装 Storage Engine IO 相关指标，避免与 Log/Index/SegmentAllocator 指标混杂。</remarks>
    public StorageView Storage { get; }

    /// <summary>Log 结构维度视图（Append/Commit/Truncate/Recover）。</summary>
    /// <remarks>★ LogView 仅封装 Log 相关指标，避免与 Storage/Index/SegmentAllocator 指标混杂。</remarks>
    public LogView Log { get; }

    /// <summary>Index 结构维度视图（Find/Insert/Upsert/Delete/Scan）。</summary>
    /// <remarks>★ IndexView 仅封装 Index 相关指标，避免与 Storage/Log/SegmentAllocator 指标混杂。</remarks>
    public IndexView Index { get; }

    /// <summary>Segment 分配器维度视图（段表的 Segment 分配/释放/FreeList——段表专用，非通用 Allocator）。</summary>
    /// <remarks>★ SegmentAllocatorView 仅封装段表的 Segment 分配器相关指标，避免与 Storage/Log/Index 指标混杂。</remarks>
    public SegmentAllocatorView SegmentAllocator { get; }

    /// <summary>指标总开关（Options.Metrics.Enabled &amp;&amp; sink.IsEnabled）。</summary>
    /// <remarks>★ MetricsEnabled 仅封装总开关，避免各维度视图重复短路。</remarks>
    public bool MetricsEnabled { get; }

    /// <summary>链路追踪总开关（Options.Tracing.Enabled &amp;&amp; tracer.IsEnabled）。</summary>
    /// <remarks>★ TracingEnabled 仅封装总开关，避免各 Span 重复短路。</remarks>
    public bool TracingEnabled => _options.Tracing.Enabled && _tracer.IsEnabled;

    private ObservabilityHub(IMetricsSink metrics, ITracer tracer, ObservabilityOptions options)
    {
        _metrics = metrics;
        _tracer = tracer;
        _options = options;
        var metricsEnabled = options.Metrics.Enabled && metrics.IsEnabled;
        MetricsEnabled = metricsEnabled;
        Metrics = new MetricsView(metrics, metricsEnabled);
        Storage = new StorageView(metrics, options.Metrics.SampleRate,
            metricsEnabled && options.Metrics.EnableStorageMetrics);
        Log = new LogView(metrics, options.Metrics.SampleRate, metricsEnabled && options.Metrics.EnableLogMetrics);
        Index = new IndexView(metrics, options.Metrics.SampleRate,
            metricsEnabled && options.Metrics.EnableIndexMetrics);
        SegmentAllocator = new SegmentAllocatorView(metrics, options.Metrics.SampleRate,
            metricsEnabled && options.Metrics.EnableSegmentAllocatorMetrics);
    }

    // === 工厂 ===

    /// <summary>
    /// 创建 Hub（零开销默认单例）。metricsSink/tracer 可为 null，options 可为 null。
    /// </summary>
    /// <param name="metricsSink">指标接收器，可为 null。</param>
    /// <param name="tracer">链路追踪器，可为 null。</param>
    /// <param name="options">观测配置，可为 null。</param>
    /// <returns>返回一个 <see cref="ObservabilityHub"/> 实例。</returns>
    public static ObservabilityHub Create(
        IMetricsSink? metricsSink,
        ITracer? tracer,
        ObservabilityOptions? options)
        => new(metricsSink ?? NullMetricsSink.Instance, tracer ?? NullTracer.Instance,
            options ?? ObservabilityOptions.Default);

    /// <summary>
    /// 创建 Hub（零开销默认单例）。metricsSink/tracer 可为 null，options 可为 null。
    /// </summary>
    /// <param name="metricsSink">指标接收器，可为 null。</param>
    /// <param name="tracer">链路追踪器，可为 null。</param>
    /// <param name="options">观测配置，可为 null。</param>
    /// <param name="sampleRate">采样率（0-1）。</param>
    /// <returns>返回一个 <see cref="ObservabilityHub"/> 实例。</returns>
    public static ObservabilityHub Create(IMetricsSink? metricsSink, ITracer? tracer, ObservabilityOptions? options,
        double sampleRate)
    {
        var sink = metricsSink ?? NullMetricsSink.Instance;
        var t = tracer ?? NullTracer.Instance;
        var opts = options ?? ObservabilityOptions.Default;
        var hasSink = sink is not NullMetricsSink;
        var hasTracer = t is not NullTracer;
        var samplePercent = ToSamplePercent(sampleRate);
        if ((hasSink && !opts.Metrics.Enabled) || (hasTracer && !opts.Tracing.Enabled))
        {
            opts = new ObservabilityOptions
            {
                Metrics = hasSink ? new MetricsConfig { Enabled = true, SampleRate = samplePercent } : opts.Metrics,
                Tracing = hasTracer ? new TracingConfig { Enabled = true, SampleRate = samplePercent } : opts.Tracing,
            };
        }

        return new ObservabilityHub(sink, t, opts);
    }

   /// <summary>
   /// 创建一个禁用的 Hub（零开销默认单例）。metricsSink/tracer 可为 null，options 可为 null。
   /// </summary>
    public static readonly ObservabilityHub Disabled = new(NullMetricsSink.Instance, NullTracer.Instance,
        ObservabilityOptions.Default);

    // === 采样 & Span ===

    /// <summary>
    /// 确定性百分比采样：<c>counter % 100 &lt; rate</c>——每 100 个事件<b>精确</b>采 rate 个，均匀分布。
    /// <para>★ 修复（）：旧实现 <c>counter % (100/rate) == 0</c> 整数除法截断——
    ///   rate=3→实际 3.03%、rate=7→7.14%、rate=34→<b>50%</b>、rate=51→<b>100%</b>，
    ///   绝大多数非整除值大幅偏差（概念计算非精确值）。新式对任意 1..99 精确且零除法。</para>
    /// <para>★ internal——可测性缝，数学契约由单测锁定。</para>
    /// </summary>
    /// <param name="counter">计数器，需外部维护。</param>
    /// <param name="rate">采样率（0-100）。</param>
    /// <returns>返回 true 表示采样，false 表示不采样。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool ShouldSample(ref int counter, int rate) => rate switch
    {
        <= 0 => false,
        >= 100 => true,
        _ => Interlocked.Increment(ref counter) % 100 < rate
    };

    /// <summary>
    /// 采样率(0~1) → 整数百分比：四舍五入（AwayFromZero）后 Clamp [0,100]。
    /// <para>★ 修复（）：旧实现 <c>(int)</c> 截断系统性偏低——0.006→0%（配 ShouldSample 的
    ///   rate≤0→false 即采样全灭）、0.996→99%。internal 可测性缝，边界表由单测锁定。</para>
    /// </summary>
    internal static int ToSamplePercent(double sampleRate)
        => Math.Clamp((int)Math.Round(sampleRate * 100, MidpointRounding.AwayFromZero), 0, 100);

    /// <summary>
    /// 开始一个 Span（链路追踪）。如果 TracingEnabled=false 则返回 null，表示不采样。
    /// </summary>
    /// <param name="name">Span 的名称。</param>
    /// <param name="kind">Span 的类型，默认为 Internal。</param>
    /// <returns>返回一个 ISpan 实例，如果不采样则返回 null。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ISpan? BeginSpan(string name, SpanKind kind = SpanKind.Internal)
        => TracingEnabled ? _tracer.BeginSpan(name, kind) : null;

    /// <summary>
    /// 上报错误指标（Counter）。如果 MetricsEnabled=false 则不上报。
    /// </summary>
    /// <param name="component">组件名称。</param>
    /// <param name="errorCode">错误码。</param>
    /// <param name="detail">错误详情，可为 null。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReportError(string component, int errorCode, string? detail)
    {
        if (!MetricsEnabled) return;
        _metrics.Counter("error", [Kv("component", component), Kv("code", errorCode.ToString())]);
    }

    /// <summary>零分配 tag 构造辅助。</summary>
    // ReSharper disable once MemberCanBePrivate.Global
    public static KeyValuePair<string, string> Kv(string k, string v) => new(k, v);
}