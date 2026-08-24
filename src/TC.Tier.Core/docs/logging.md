# 日志使用指南（ILogger / LoggerExtensions）

> 定位：TC.Tier 的**极简日志抽象**——两方法接口（`Log` / `IsEnabled`），刻意去掉
> `Microsoft.Extensions.Logging` 的 `TState`/`formatter` 复杂度；零开销短路内建。
> **日志不经 `ObservabilityHub`**（Hub 只聚合 Metrics+Tracing）——三信号各自独立，见 [`observability.md`](observability.md)。
> 单测：`tests/TC.Tier.Core.Tests/Logging/`。

---

## 0. 一句话决策

```
我要打日志？ → 注入 ILogger?（null 容忍——全部重载 null 安全），用 LogXxx 扩展。
  ★ 调用方【无需】手写 IsEnabled——短路已内建在每个重载里（关闭时不格式化、0~3 参零装箱零分配）。
  仅两个例外：
  · >3 参走 params 重载：数组+装箱在调用点发生（进扩展之前）——【热路径】才值得先手动 IsEnabled；
  · 直接调 Log(level, msg)：接口方法无任何折叠，自行格式化/自行判断。
```

## 1. 接口与级别

```csharp
public interface ILogger
{
    void Log(LogLevel logLevel, string message, Exception? exception = null);
    bool IsEnabled(LogLevel logLevel);
}
```

`LogLevel`：`Trace(0) / Debug(1) / Information(2) / Warning(3) / Error(4) / Critical(5) / None(6)`。
**级别过滤由实现自决**（Core 只定义枚举）——`IsEnabled` 是唯一判断入口，勿自行缓存结果。

## 2. 扩展方法（LoggerExtensions，36 个重载）

| 形态 | 重载 | 关闭时的开销 |
|------|------|-----------|
| 0~3 参 | `LogXxx(message)` / `(message, arg1)` / `(…, arg1, arg2)` / `(…, arg1, arg2, arg3)`——每级 Trace~Critical 全套 | 一次 `IsEnabled` 调用即返回，**不装箱、不分配、不格式化**——调用方零负担 |
| 带异常 | `LogWarning(exception, message[, args…])` / `LogError(...)` / `LogCritical(...)` | 同上 |
| 兜底 | `LogXxx(message, params object?[] args)`（>3 参走这） | 内部仍判 `IsEnabled`（不格式化），**但 params 数组 + 装箱在调用点已发生**——正确性无恙，热路径才值得手动保护（或拆成 ≤3 参） |

```csharp
// 命名占位符（推荐——可读 + 结构化友好）：{name} 自动按顺序映射 {0},{1},...
logger.LogInformation("append ok entry={entry} size={size}", entryId, size);
logger.LogWarning(ex, "compact failed seg={seg} attempts={n}", segId, n);

// ★ 仅热路径的 >3 参才手动保护（防 params 调用点分配；日常路径直接调即可）
if (logger.IsEnabled(LogLevel.Debug))
    logger.LogDebug("...{a} {b} {c} {d}", a, b, c, d);
```

- **命名占位符**：`{name}` 经 `NormalizeNamedPlaceholders` 归一为 `{0},{1},...` 再 `string.Format`
  （**仅 IsEnabled=true 时格式化**）——支持任意合法格式说明（`{size:N0}`）。
- **全部 36 个重载对 `null` logger 安全**（receiver `ILogger?` + `is not null` 短路）——
  组件可持有 `ILogger?` 字段直接 `_logger.LogXxx(...)`，无注入即静默。

## 3. 工厂与默认

| 类型 | 角色 | 默认行为 |
|------|------|---------|
| `ILoggerFactory` | 宿主侧创建 logger（按 categoryName） | — |
| `NullLoggerFactory.Instance` | 无注入时的默认工厂 | 产出 `NullLogger` |
| `NullLogger.Instance` | 空实现 | `IsEnabled` 恒 false——全链路零开销 |

**装配范式**：组件构造收 `ILogger?`（null 即静默——扩展方法 null 安全）；宿主从自己的 factory
创建并注入——Core 不绑定任何日志后端（桥接见 §4）。

## 4. 宿主桥接（控制台 / M.E.Logging / 可观测后端）——都很简单

接口只有两方法 + `LogLevel` 与 `Microsoft.Extensions.Logging` 的枚举值 **1:1 对齐**（Trace..None 同为 0..6，
直接强转），任何后端十几行桥完。

### 4.1 零依赖控制台（最短路径）

```csharp
sealed class ConsoleLogger : TC.Tier.Core.Logging.ILogger
{
    public void Log(LogLevel level, string message, Exception? exception = null)
        => Console.WriteLine($"[{level}] {message}" + (exception is null ? "" : $"\n{exception}"));
    public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;
}
```

### 4.2 桥接 M.E.Logging（控制台 / OTLP / Serilog…全家桶一次接入）

```csharp
using Mel = Microsoft.Extensions.Logging;

sealed class MelLoggerAdapter : TC.Tier.Core.Logging.ILogger
{
    private readonly Mel.ILogger _inner;
    public MelLoggerAdapter(Mel.ILogger inner) => _inner = inner;

    public void Log(LogLevel level, string message, Exception? exception = null)
        // 占位符包一层：我们的 message 是【已格式化终串】，不能当 MEL 模板（含 { 时会被再解释）
        => _inner.Log((Mel.LogLevel)level, exception, "{Msg}", message);

    public bool IsEnabled(LogLevel level) => _inner.IsEnabled((Mel.LogLevel)level);
}

sealed class MelFactoryAdapter : TC.Tier.Core.Logging.ILoggerFactory
{
    private readonly Mel.ILoggerFactory _factory;
    public MelFactoryAdapter(Mel.ILoggerFactory factory) => _factory = factory;
    public TC.Tier.Core.Logging.ILogger CreateLogger(string categoryName)
        => new MelLoggerAdapter(_factory.CreateLogger(categoryName));
}

// 宿主装配——MEL 生态全部可用（控制台一行 / OTLP / Serilog 换 builder 即可）：
TC.Tier.Core.Logging.ILoggerFactory logFactory = new MelFactoryAdapter(
    Mel.LoggerFactory.Create(b => b.AddConsole()));
```

> ★ 适配器放**宿主侧**——Core 不引用 M.E.Logging（依赖方向不变），桥接是宿主的自由。
> 日志要进可观测管线（OTel collector 等）同样走 MEL 的 Provider 生态，适配器不用改。

### 4.3 指标/追踪后端

`IMetricsSink` / `ITracer` 同理——实现接口即接任意后端（OTel Meter / Prometheus / Datadog），
现成模式与注意事项见 [`observability.md`](observability.md) §7。

## 5. 与 ObservabilityHub 的关系（勿混）

| | 日志 | 指标/追踪 |
|---|---|---|
| 接入 | `ILogger` 独立注入 | 经 `ObservabilityHub` |
| 开关 | 实现自决（`IsEnabled`） | `ObservabilityOptions` + sink/tracer + 维度（构造期折叠） |
| 配置位置 | 宿主日志系统 | `ObservabilityHub.Create(...)` |

❌ 不要把 `ILogger` 塞进 Hub、不要在 `ObservabilityOptions` 里找日志级别（那是反模式，
见 [`../COORDINATION.md`](../COORDINATION.md) §7）。

## 6. 铁律

- ✅ **默认直接调 `LogXxx`，不写 `IsEnabled`**——短路折叠内建在每个重载里（0~3 参关闭时零开销）。
- ✅ 唯一例外：**热路径**的 >3 参（params 重载数组+装箱在调用点白付）——手动保护，或拆成 ≤3 参/拆两条。
- ✅ 热路径避免字符串插值（`$"..."` 在调用点无条件拼接——用占位符重载，关了不花一分钱）。
- ✅ 异常日志用 `LogWarning/LogError(exception, message, ...)` 带异常重载（保堆栈），不要 `ex.ToString()` 拼进消息。
- ❌ 不自己 new 日志实现、不缓存 `IsEnabled` 结果（实现可动态变）。
- ❌ 不在 Core 里绑定具体日志后端——适配/桥接是宿主的事（§4 范例）。

---

## 关联文档

- 可观测（指标/追踪，经 Hub）：[`observability.md`](observability.md) ｜ 积木全景：[`../COORDINATION.md`](../COORDINATION.md) §3
