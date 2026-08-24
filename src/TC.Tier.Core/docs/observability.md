# 可观测性使用指南（ObservabilityHub / Metrics / Tracing）

> 定位：TC.Tier 的**唯一可观测接入点**是 `ObservabilityHub`——聚合**指标（Metrics）+ 追踪（Tracing）**两个信号，
> 构造期把「总开关 × sink/tracer.IsEnabled × 维度开关」三重 AND 折叠成单 bool，热路径只读一个 bool。
> **⚠️ 不含 Logging**——日志是第三信号，独立注入（见 [`logging.md`](logging.md)）。
> 单测：`tests/TC.Tier.Core.Tests/Observability/ObservabilityHubTests.cs`（89 测试）+ `Metrics/`。

---

## 0. 三信号模型与一句话决策

| 信号 | 接口 | 接入方式 | 默认 |
|------|------|---------|------|
| **日志** | `ILogger` | 上层独立注入 `ILoggerFactory`，**不经 Hub** | `NullLogger`（零开销） |
| **指标** | `IMetricsSink` | **经 Hub 维度视图**，禁止直调 sink | `NullMetricsSink`（IsEnabled=false） |
| **追踪** | `ITracer` | **经 Hub**（`BeginSpan`） | `NullTracer`（零开销） |

```
我要打日志？ → ILogger（独立注入）。我要发指标/开 span？ → 经 ObservabilityHub，先判 IsEnabled/TracingEnabled。
```

---

## 1. Hub 的获取与装配

```csharp
// ① 宿主装配期（唯一真实配置点）：注入真实 sink/tracer + 配置
var hub = ObservabilityHub.Create(myMetricsSink, myTracer, new ObservabilityOptions
{
    Metrics = new MetricsConfig { Enabled = true, EnableStorageMetrics = true, SampleRate = 20 },
    Tracing = new TracingConfig { Enabled = true },
});
// ② 组件默认 / 无观测需求：零开销单例
var hub0 = ObservabilityHub.Disabled;        // 所有 IsEnabled=false，热路径短路
// ③ 高级工厂：显式采样率（0~1 double），且 sink/tracer 非 Null 时自动开对应 Enabled
var hub2 = ObservabilityHub.Create(sink, null, options, sampleRate: 0.2);
```

- **组件接入范式**：底层组件构造只收一个 `ObservabilityHub?`（`null → Disabled`），
  存 `_hub` 字段，用 `hub.Metrics`/`hub.Storage` 等子视图——**不直接持有 sink/tracer**。
  （Core 内范例：`IsolatedTaskScheduler`、`CpuSampler`——见各使用文档 §可观测。）
- `ObservabilityOptions`：`Metrics`（`MetricsConfig`）+ `Tracing`（`TracingConfig`），`Default` 全默认。
- **两级短路折叠**：`Options.Enabled && sink/tracer.IsEnabled && 维度开关` 在 ctor 折叠成每视图一个
  `_enabled` bool——热路径读 bool 短路，**关了就是零开销**（JIT 消除后续分支）。

## 2. 视图全景（5 个）

| 视图（Hub 属性） | 开关（MetricsConfig） | 覆盖 | 指标名前缀 |
|---|---|---|---|
| `Metrics` | `Enabled` | **三原语透传**：`Counter/Histogram/Gauge`（自定义指标走这） | 自定 |
| `Storage` | `EnableStorageMetrics`（默认 true） | 引擎 IO：Read/Write/Flush/Compact/Reclaim/Throttle/QueueDepth | `storage.*` |
| `Log` | `EnableLogMetrics`（默认 true） | Log 结构：Append/Commit/Truncate/BufferFull/Recover | `log.*` |
| `Index` | `EnableIndexMetrics`（默认 true） | Index 结构：Find/Insert/Upsert/Delete/Scan | `index.*` |
| `SegmentAllocator` | `EnableSegmentAllocatorMetrics`（默认 **false**，高频按需开） | **段表专用**：Segment 分配/释放/FreeList 深度 | `segment_allocator.*` |

> ⚠️ `SegmentAllocator` 是**段表专用**视图（不是通用 Allocator 原语）——命名即作用域，别的分配器
> 别往里塞；需要新维度就加独立视图（partial 文件），不泛化旧视图。

### 2.1 指标命名约定

点号分层 + 单位后缀：`storage.read.latency_us`、`log.append.latency_us`、`segment_allocator.free_list_depth`。
tag 用 `ObservabilityHub.Kv(k, v)` 零分配构造；sink 端收到 `ReadOnlySpan<KeyValuePair<string,string>>`。

### 2.2 三原语怎么选

| 原语 | 语义 | 用于 |
|------|------|------|
| `Counter` | 单调累计 | 次数/事件（`OnThrottle`、`segment_allocator.alloc`） |
| `Histogram` | 分布 | **延迟（`_us` 后缀）/大小**——别用 Gauge 存延迟 |
| `Gauge` | 瞬时值 | 队列深度/水位（`OnQueueDepth`、`free_list_depth`） |

## 3. 标准接入代码（自包含骨架）

```csharp
sealed class MyComponent : IDisposable
{
    private readonly ObservabilityHub _hub;
    private readonly KeyValuePair<string, string>[] _nameTag;   // ★ 预构造 tag，热路径零分配

    public MyComponent(ObservabilityHub? hub = null, ILogger? logger = null)
    {
        _hub = hub ?? ObservabilityHub.Disabled;                // 默认零开销
        _nameTag = [ObservabilityHub.Kv("name", "my-component")];
    }

    public void DoWork()
    {
        // 指标关→整段被 JIT 消除；开→计时 + 计数 + 直方图
        var metrics = _hub.Metrics;
        var timing = metrics.IsEnabled ? MicroTimer.Start() : default;
        try { /* 干活 */ }
        finally
        {
            if (timing.IsActive)
            {
                metrics.Counter("mycomponent.work.count", _nameTag);
                metrics.Histogram("mycomponent.work.us", timing.ElapsedMicros(), _nameTag);
            }
        }
    }
}
```

要点（对齐 `MicroTimer` 的 active=false JIT 消除语义——见 [`cache-and-compute.md`](cache-and-compute.md)）：
- **先 `IsEnabled` 一次读**，再决定计时/上报——这是 Hub 全部视图的统一模式。
- tag 数组**预构造**存字段，不要热路径现场拼。
- 有既有维度视图就用视图方法（`hub.Storage.OnRead(...)`），**不要**绕过视图直调 sink
  （丢两级短路 + 采样）；自定义指标才走 `hub.Metrics` 三原语。

## 4. 采样（确定性百分比，非随机）

- `ShouldSample(ref counter, rate)`：`counter % 100 < rate`——每 100 个事件**精确**采 rate 个、均匀分布、
  `Interlocked` 线程安全。★ 2026-08-14 修复：旧实现 `counter % (100/rate) == 0` 整数除法截断，
  rate=34 实际 50%、rate=51 实际 100%——概念值大幅偏差；新式零除法对 1..99 精确。
- 视图内置按操作独立采样：`ShouldSampleRead()/Write()/Flush()/Compact()`（Storage）、
  `ShouldSampleAppend()/Commit()`（Log）、`ShouldSampleFind()/Insert()/Upsert()/Delete()/Scan()`（Index）、
  `ShouldSampleAlloc()`（SegmentAllocator）——配合 `MicroTimer`：`var t = hub.Storage.BeginReadSample();`
  命中采样才计时。
- **背压/错误信号全采、不走采样**：`OnThrottle`/`OnBufferFull`/`ReportError`（这类信号稀有且必须可见）。
- `SampleRate` 配置为 0~100 整数（`MetricsConfig.SampleRate` 默认 100=全采）；`Create(..., sampleRate)` 的
  double 0~1 经 `ToSamplePercent` 四舍五入折叠（0.006→0 全灭的截断 bug 同批修复）。

## 5. 追踪（Tracing）

```csharp
if (hub.TracingEnabled)                       // ★ 先判，关了 BeginSpan 返回 null
{
    using var span = hub.BeginSpan("wal.append", SpanKind.Producer);
    span?.SetTag("entry", entryId.ToString()); // hub.BeginSpan 返回 ISpan?（关闭时 null）
    // ... 业务
}
```

- `SpanKind`：Internal/Server/Client/Producer/Consumer；`SpanStatus`：Ok/Error（无 Unset）。
- `ITracer.Current` 走 `AsyncLocal` 父子链——子方法无需传参自动挂在当前 span 下。
- **AOT 友好**：纯接口 + 枚举 + AsyncLocal，无反射/Emit。
- ⚠️ 两个 BeginSpan 语义差异：裸 `ITracer.BeginSpan` 恒非 null（`NullTracer` 返回 `NullSpan.Instance`，
  `using` 安全）；**`hub.BeginSpan` 关闭时返回 null**——`using var` 配 `?.` 或先判 `TracingEnabled`。
- 追踪默认**关**（高频路径 span 开销大）——按采样率/开关显式启用。

## 6. 错误上报

`hub.ReportError(component, errorCode, detail)`——统一 `error` Counter（tag: component/code）。
全采不走采样；不抛异常（观测永不影响业务路径）。

## 7. 宿主接入市面后端（OTel / Prometheus / Datadog / 控制台）

桥接核心思路：**适配到 `System.Diagnostics.Metrics`（Meter）与 `System.Diagnostics.ActivitySource`**——
这两者是 .NET 8 BCL 内建、OTel SDK 的标准绑定面。一套适配器即可解锁全部市面后端，**Core 零新增依赖**：

| 想接的后端 | 走法 |
|---|---|
| OTLP → Datadog / Jaeger / Tempo / Collector | OTel SDK `AddOtlpExporter()` |
| Prometheus（Grafana 拉取） | OTel SDK `AddPrometheusExporter()`（点号指标名→下划线由 exporter 处理） |
| Azure Monitor / AWS X-Ray 等 | 各自 OTel 分发包，绑同一批 Meter/Source |
| 控制台调试 | §7.1 十行 sink |

### 7.1 控制台调试 sink（最短路径）

```csharp
sealed class ConsoleMetricsSink : IMetricsSink
{
    public bool IsEnabled => true;
    public void Counter(string name, ReadOnlySpan<KeyValuePair<string, string>> tags)
        => Console.WriteLine($"[C] {name}{FormatTags(tags)}");
    public void Histogram(string name, double value, ReadOnlySpan<KeyValuePair<string, string>> tags)
        => Console.WriteLine($"[H] {name} = {value}{FormatTags(tags)}");
    public void Gauge(string name, double value, ReadOnlySpan<KeyValuePair<string, string>> tags)
        => Console.WriteLine($"[G] {name} = {value}{FormatTags(tags)}");

    private static string FormatTags(ReadOnlySpan<KeyValuePair<string, string>> tags)
        => tags.Length == 0 ? "" : " {" + string.Join(",", tags.ToArray().Select(t => $"{t.Key}={t.Value}")) + "}";
}
```

### 7.2 指标 → `System.Diagnostics.Metrics`（OTel 标准绑定面）

```csharp
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

/// IMetricsSink → Meter。Counter/Histogram 直映射；Gauge 是推模型、Meter 是拉模型
/// （ObservableGauge），用每序列最新值持有器桥接。
sealed class DiagnosticsMetricsSink : IMetricsSink
{
    private readonly Meter _meter;
    private readonly ConcurrentDictionary<string, Counter<double>> _counters = new();
    private readonly ConcurrentDictionary<string, Histogram<double>> _histograms = new();
    private readonly ConcurrentDictionary<string, GaugeState> _gauges = new(StringComparer.Ordinal);

    public bool IsEnabled => true;   // ★ false = 整条指标链路在 Hub 构造期折叠为关

    public DiagnosticsMetricsSink(string meterName = "TC.Tier") => _meter = new Meter(meterName);

    public void Counter(string name, ReadOnlySpan<KeyValuePair<string, string>> tags)
        => _counters.GetOrAdd(name, n => _meter.CreateCounter<double>(n))
                    .Add(1, ToTags(tags));

    public void Histogram(string name, double value, ReadOnlySpan<KeyValuePair<string, string>> tags)
        => _histograms.GetOrAdd(name, n => _meter.CreateHistogram<double>(n))
                      .Record(value, ToTags(tags));

    public void Gauge(string name, double value, ReadOnlySpan<KeyValuePair<string, string>> tags)
    {
        var tagArr = tags.ToArray();   // ★ ref-like（ReadOnlySpan）不能被 lambda 捕获（CS9108）——先物化
        _gauges.GetOrAdd(SeriesKey(name, tags), _ => new GaugeState(_meter, name, tagArr)).Value = value;
    }

    // Gauge 拉模型桥：持最新值，OTel 采集时读
    private sealed class GaugeState
    {
        public double Value;   // ObservableGauge 回调线程读，写入方为业务线程——volatile 语义足够（double 原子性 x64）
        public GaugeState(Meter meter, string name, KeyValuePair<string, string>[] tags)
            => meter.CreateObservableGauge(name,
                () => new[] { new Measurement<double>(Volatile.Read(ref Value), ToTags(tags)) });
    }

    private static KeyValuePair<string, object?>[] ToTags(ReadOnlySpan<KeyValuePair<string, string>> tags)
    {
        var arr = new KeyValuePair<string, object?>[tags.Length];
        for (var i = 0; i < tags.Length; i++) arr[i] = new(tags[i].Key, tags[i].Value);
        return arr;
    }

    private static string SeriesKey(string name, ReadOnlySpan<KeyValuePair<string, string>> tags)
    {
        if (tags.Length == 0) return name;
        var sb = new System.Text.StringBuilder(name);
        foreach (var t in tags) sb.Append(',').Append(t.Key).Append('=').Append(t.Value);
        return sb.ToString();
    }
}
```

### 7.3 追踪 → `ActivitySource`（OTel 标准绑定面）

```csharp
using System.Diagnostics;

sealed class ActivityTracer : ITracer
{
    private readonly ActivitySource _source;
    public ActivityTracer(string name = "TC.Tier") => _source = new ActivitySource(name);

    public bool IsEnabled => _source.HasListeners();

    // SpanKind 与 ActivityKind 枚举值同构（Internal..Consumer = 0..4）——直接强转
    public ISpan BeginSpan(string name, SpanKind kind = SpanKind.Internal)
        => _source.CreateActivity(name, (ActivityKind)kind) is { } a
            ? new ActivitySpan(a.Start())
            : Tracing.NullSpan.Instance;   // ★ 无监听者→必须返回非 null（协议：BeginSpan 恒非 null）

    // 桥接场景父子链经 Activity.Current（原生 AsyncLocal）自然传递，本接口 Current 无需实现
    public ISpan? Current => null;

    private sealed class ActivitySpan : ISpan
    {
        private readonly Activity _a;
        public ActivitySpan(Activity a) => _a = a;
        public void SetTag(string key, string? value) => _a.SetTag(key, value);
        public void SetTag(string key, long value) => _a.SetTag(key, value);
        public void SetStatus(SpanStatus status, string? description = null)
            => _a.SetStatus(status == SpanStatus.Ok ? ActivityStatusCode.Ok : ActivityStatusCode.Error, description);
        public void AddEvent(string name) => _a.AddEvent(new ActivityEvent(name));

        public void RecordException(Exception ex)   // net8 无 Activity.AddException（.NET 9+），按 OTel 惯例打事件
            => _a.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection {
                { "exception.type", ex.GetType().FullName },
                { "exception.message", ex.Message },
                { "exception.stacktrace", ex.StackTrace } }));

        public void Dispose() => _a.Dispose();   // Activity.Dispose = Stop + 恢复父 Activity.Current
    }
}
```

### 7.4 宿主装配

```csharp
// ① Hub 挂上两个适配器
var hub = ObservabilityHub.Create(
    new DiagnosticsMetricsSink("TC.Tier"), new ActivityTracer("TC.Tier"),
    new ObservabilityOptions
    {
        Metrics = new MetricsConfig { Enabled = true },
        Tracing = new TracingConfig { Enabled = true },
    });

// ② OTel SDK 绑同一批 Meter/ActivitySource（NuGet：OpenTelemetry.Extensions.Hosting + 选 exporter 包）
//    services.AddOpenTelemetry()
//        .WithMetrics(m => m.AddMeter("TC.Tier").AddOtlpExporter())          // → Collector/Datadog/…
//        .WithTracing(t => t.AddSource("TC.Tier").AddOtlpExporter())
//    换 .AddPrometheusExporter() 即 Prometheus/Grafana 拉取模式。
```

**成本与边界（诚实说明）**：
- tag 转换（`ReadOnlySpan<KVP<string,string>>` → `KVP<string,object?>[]`）在适配器里分配——但只发生在
  **已过 Hub 短路 + 采样**之后的调用上（关/未采样根本不进 sink），属宿主显式选择的成本。
- `IsEnabled => true` 意味着 Hub 侧开关全权由 `MetricsConfig.Enabled` + 维度开关控制；桥接器自己不折叠。

## 8. 测试场景（组件测试怎么验证可观测）

| 场景 | 模式（Core 单测现行做法） |
|------|--------------------------|
| 指标确实发了 | 挂**捕获 sink**（`IMetricsSink` 计数版，用并发安全集合——任务可能在私有线程上发）+ `MetricsConfig.Enabled=true`，断言计数 ≥N / 含某指标名；异步路径用 `SpinWait.SpinUntil` 等执行侧指标（入队在运行前发、执行在后发的时序差） |
| 关了就零发射 | 同一 sink 挂上但 `Metrics.Enabled=false` → 断言 sink 三集合全空（验证短路折叠） |
| `Disabled` 默认零开销 | `ObservabilityHub.Disabled` 所有视图 `IsEnabled=false` + `MetricsEnabled/TracingEnabled=false` |
| 维度开关独立 | `EnableSegmentAllocatorMetrics=false`（默认）→ 该视图 `IsEnabled=false`，其余不受影响 |
| 采样确定性 | `ShouldSample` 每 100 精确 rate 个（数学契约由单测锁边界表：0/1/50/99/100）；各视图 `ShouldSampleXxx` 独立计数互不干扰 |
| 背压/错误全采 | `OnThrottle`/`OnBufferFull`/`ReportError` 不受 `SampleRate` 影响 |

## 9. 铁律

- ❌ **绝不**直调 `IMetricsSink`——一律经 Hub 视图（两级短路 + 采样 + 维度开关全在视图里）。
- ❌ **绝不**热路径不判 `IsEnabled`/`TracingEnabled` 就格式化/计时/开 span。
- ❌ **绝不**热路径现场构造 tag 数组——预构造字段（`ObservabilityHub.Kv`）。
- ❌ **绝不**把 `ILogger` 塞进 Hub / 在 `ObservabilityOptions` 里找日志级别——三信号各自独立（[`logging.md`](logging.md)）。
- ✅ 延迟用 `Histogram`（`_us` 后缀），瞬时值用 `Gauge`，次数用 `Counter`。
- ✅ 新组件只收 `ObservabilityHub?`（null→`Disabled`），不收 sink/tracer 具体类型。

---

## 关联文档

- 日志（第三信号，独立注入）：[`logging.md`](logging.md) ｜ 积木全景：[`../COORDINATION.md`](../COORDINATION.md) §3
- 计时原语 `MicroTimer`：[`cache-and-compute.md`](cache-and-compute.md) ｜ 组件接入范例：`IsolatedTaskScheduler`（[dedicated-task-scheduler.md](dedicated-task-scheduler.md) §6）、`CpuSampler`（[worker-loop.md](worker-loop.md) §7）
