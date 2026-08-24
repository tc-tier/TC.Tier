# 专用线程调度器使用指南（IsolatedTaskScheduler）

> 定位：**Core 级公共协调组件**——高频 / 关键路径后台 worker 的线程隔离调度器。固定 M 个**私有**线程执行任务，
> 与公共线程池（`TaskScheduler.Default`）完全隔离；任务内 `await` 的 continuation 自动回流私有线程（不经公共池）。
> 本文档讲**怎么用**：选型、获取实例、配置、生命周期、注意事项、故障排查。
> 性能实测与调参见 [`perf/dedicated-task-scheduler-perf.md`](perf/dedicated-task-scheduler-perf.md)；worker 积木用法见 [`worker-loop.md`](worker-loop.md)。

---

## 0. 一句话决策

```
高频 / 关键 worker（引擎建段、compact、段满事件处理）？
  → 注入 IsolatedTaskScheduler（引擎 own 实例：Create + Resources.Add）
低频周期 worker（CpuSampler、meta flusher）？
  → 公共池（BackgroundWorkerLoop 不传 scheduler，null = Task.Run）
两者都不许 new Thread 自建循环（见 worker-loop.md §0）。
```

---

## 1. 适用 / 不适用

| ✅ 适用 | ❌ 不适用（改用其它方案） |
|--------|--------------------------|
| 高频生产者会淹没公共池、拖垮无关异步工作 | **长同步阻塞调用**（霸占 M 线程之一，M 很小）→ 拆短 / 异步化 / 独立 `Thread` |
| 对 continuation 调度延迟有确定性要求（不与写者争池线程） | CPU 密集长计算且不让出（饿死 M 线程）→ 独立计算分区 |
| 协作式 async 负载（大量 `await` 让出） | 低频周期 worker → 公共池，不值得开专用线程 |
| 长生命周期 worker 循环 | 单次 fire-and-forget 任务 → 它不是通用任务池 |
| 并发度有限（M ≤ 核数） | 海量并发（超核数）→ 它是有界设计 |

---

## 2. 获取实例——只有两条受控入口

ctor 是 `internal`，**禁止裸 `new`**（每实例开 M 个真实 OS 线程，栈 ~1MB/线程——稀缺资源，受控创建）。

### 入口 A：`IsolatedTaskScheduler.Shared`（进程级单例）

```csharp
// 全默认 options，M = RecommendedThreadCount = Clamp(ProcessorCount, 2, 4)
// 调用方【不 Dispose】——进程生命期；私有线程 IsBackground=true 不挡进程退出
Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.None, IsolatedTaskScheduler.Shared);
```

适用：单引擎 / 纯异步轻量场景，所有专用 worker 共用 M 线程，进程级线程数最省。

### 入口 B：`IsolatedTaskScheduler.Create(options)`（独立分区，**引擎默认**）

```csharp
// 在 LifecycleBase 派生的 OnInitializeBegin/OnInitializeComplete 里：
_workerScheduler = IsolatedTaskScheduler.Create(new IsolatedSchedulerOptions
{
    ThreadCount = 2,                      // 引擎异步建段 worker 池
    Name = "Engine.WorkerScheduler",      // 线程名前缀 / 日志 / 指标 tag
    Logger = logger,
    // Hub = hub,                          // 需要指标时挂（默认 Disabled 零开销）
});
Resources.Add(_workerScheduler);           // 随引擎 Dispose 释放（own）

// worker 注入（BackgroundWorkerLoop 构造第一个参数）：
ConfigureBackgroundWorker(new SegmentBuilderWorker(scheduler: _workerScheduler, consumerCount: 2));
```

适用：多引擎互不干扰；**同步 worker（compact）与异步 worker（建段）分池**——不共用，避免互相饿死。

### 取舍表

| | `Shared`（全局单例） | `Create` + Resources（own） |
|---|---|---|
| 线程数 | 进程级固定 M（最省） | 每实例 M（N 引擎 = N×M） |
| 隔离粒度 | 与公共池隔离；**worker 间共享 M 线程** | 引擎 / worker 组之间互不干扰 |
| 生命周期 | 进程级，无需 Dispose | 随 owner Dispose |
| InstanceTracker | 不注册（进程意图资源） | 注册（泄漏/扩散可见） |
| 适用 | 单引擎 / 纯异步轻量 | **引擎默认**；多引擎；同步/异步分池 |

> ⚠️ **实测**：compact 等同步 worker 与异步建段 worker 共用 `Shared` 的 M 线程时，同步 worker
> 霸占线程 → 饿死异步建段（43+ 测试失败）。**同步 worker 必须 own 独立实例（甚至单线程）与异步 worker 分池**。

> ⚠️ 防扩散护栏：进程内 `Create` 实例数 > 4 → WARN「疑似滥用——考虑用 Shared」。

---

## 3. 与 BackgroundWorkerLoop 集成

`BackgroundWorkerLoop` 构造第一个参数 `TaskScheduler? scheduler = null`：

```csharp
sealed class MyWorker : BackgroundWorkerLoop
{
    public MyWorker(TaskScheduler? scheduler = null, int consumerCount = 1, string? name = null)
        : base(scheduler, consumerCount, name) { }

    protected override async ValueTask<bool> RunOneCycleAsync(CancellationToken ct)
    { /* 一个周期 */ return true; }
}
```

- `scheduler: null` → 公共池（`Task.Run`）——低频 worker。
- `scheduler: IsolatedTaskScheduler` → 消费者循环 Task 跑私有线程，`await DequeueAsync` 等 continuation 回流该调度器，全程不碰公共池。
- **`consumerCount`（循环 Task 数 N）与调度器线程数 M 彻底解耦**：N 个消费者由 M 线程协作跑（async 让出复用）。建议 N ≈ M；N ≫ M 时 Start 会 WARN（只增开销不增吞吐）。需要更高并行度调 `ThreadCount`，不是 consumerCount。
- `BackgroundWorkerLoop` **不 own** 注入的调度器——生命周期归注入方（`Shared` 进程级不释放；own 实例随 `Resources` 释放）。

---

## 4. 配置旋钮（`IsolatedSchedulerOptions` 全量）

| 旋钮 | 默认 | 含义 / 调参建议 |
|------|------|----------------|
| `ThreadCount` (M) | `Clamp(ProcessorCount,2,4)` | 私有线程数。`<1` throw；`>ProcessorCount` throw（cooperative async 下超核纯增上下文切换）；`>ProcessorCount/2` WARN（占核过半，与写者叠加易超订）。 |
| `QueueCapacity` | `0` | 调度器任务队列：`0`=自动有界 `max(M*4,16)`；`>0`=指定有界；`<0`=无界（永不阻塞，仅监控）。**背压主战场在 worker 工作项队列**，此处仅防御性有界（见 §7 注意事项 3）。 |
| `Name` | `"isolated"` | 诊断名：私有线程名前缀（`{Name}-{i}`）、日志、指标 tag。**必填一个可读名**（own 实例）。 |
| `Logger` | `null` | WARN/ERROR 日志（防扩散 / 过半核 / 队列满 / 慢任务 / 死锁 / 重启）。生产建议挂。 |
| `Hub` | `null` → `ObservabilityHub.Disabled` | 指标（§6）。默认零开销；**要观测必须显式挂**且 `Metrics.Enabled=true`。 |
| `WatchdogInterval` | 5s | watchdog 周期。`≤0` **关闭**（纯隔离无监控无自愈的最轻模式）。 |
| `TaskTimeout` | 30s | 单任务超此判「慢任务」（WARN + 计数）。高频快任务建议下调（如 1~5s）更早暴露霸占；重 IO worker 上调。 |
| `DeadlockConfirmTicks` | 3 | 疑似死锁防抖：连续 N 个 tick（≥2 线程慢且）无任何推进才判。误报多可上调。 |
| `RestartPolicy` | `Always` | 线程死亡：`Always`（重启维持 M，关键路径默认）/ `RestartOnce`（再死即降级）/ `None`（死亡即降级，故障快失败）。 |

---

## 5. 生命周期与 Dispose 顺序（铁律）

```
引擎 Dispose 模板（LifecycleBase 已内置）：
  ① 停 worker：Stop → WaitForExit（所有任务结束）
  ② Resources.Dispose() → 调度器 Dispose（CompleteAdding + Join 私有线程）
```

- ❌ **绝不**在有在飞任务时 Dispose 调度器——`CompleteAdding` 后入队的 continuation（孤儿 Task）被静默丢弃，等它的 awaiter 永远挂起。
- ❌ **绝不** Dispose `Shared`（进程意图资源；谁都不 own 它）。
- ✅ own 实例经 `Resources.Add(scheduler)` 注册，让引擎 Dispose 模板保证「先停 worker 再释放调度器」的顺序。
- ✅ 私有线程 `IsBackground=true`：即使泄漏未 Dispose，也不挡进程退出（但会泄漏线程 + InstanceTracker 可见）。

---

## 6. 可观测性

### 指标（挂 `Hub` 且 `Metrics.Enabled=true` 时才发；默认 Disabled **热路径零开销**——一次 `IsEnabled` 读短路）

| 类型 | 指标名 | 含义 |
|------|--------|------|
| Counter | `scheduler.task.enqueued` | 任务入队数 |
| Counter | `scheduler.task.executed` | 任务执行数 |
| Histogram | `scheduler.task.exec_us` | 任务执行耗时（μs） |
| Gauge | `scheduler.queue.depth` | 队列深度（每次执行后采样） |
| Counter | `scheduler.queue.full` | 队列满、生产者被背压阻塞（>1ms）次数 |
| Counter | `scheduler.task.slow` | 慢任务命中数 |
| Counter | `scheduler.deadlock.suspected` | 疑似死锁次数 |
| Counter | `scheduler.threads.restarted` | 线程死亡重启次数 |
| Counter | `scheduler.threads.degraded` | 线程死亡不再重启（降级）次数 |

### 日志事件（挂 `Logger` 才有）

| 级别 | 事件 |
|------|------|
| WARN | 实例数 > 4（防扩散）；ThreadCount 过半核；队列满背压（含阻塞 μs）；慢任务（含已执行 ms / taskTimeout）；线程死亡已重启 |
| ERROR | 疑似死锁（含线程状态全量转储）；线程死亡不再重启（降级） |

```csharp
IsolatedTaskScheduler.Create(new IsolatedSchedulerOptions
{
    Name = "Engine.WorkerScheduler",
    Logger = logger,
    Hub = ObservabilityHub.Create(myMetricsSink, tracer: null,
        new ObservabilityOptions { Metrics = new MetricsConfig { Enabled = true } })
});
```

> sink 的 `IsEnabled=true` + `MetricsConfig.Enabled=true` 两者都开才发指标（热路径检查 `_hub.Metrics.IsEnabled`）。

---

## 7. 注意事项（铁律 / 陷阱清单）

1. **禁止裸 `new`**——ctor `internal`，只走 `Shared` / `Create`（稀缺 OS 线程，受控创建 + 防扩散护栏）。
2. **禁止长同步阻塞任务**（`Thread.Sleep`、同步重 IO、`GetAwaiter().GetResult()`）——M 很小，一个阻塞任务就吃掉 1/M 的并行度，多个直接饿死整个 worker 组。拆短 / 异步化；确实无法异步化的同步 worker → **own 单线程实例**分池，别混进异步池。
3. **队列满 = 阻塞生产者**（`QueueTask` 无法拒绝/转交——Task 已绑定本调度器）。默认容量 `max(M*4,16)` 很小，突发入队会背压阻塞入队线程。**背压主战场在 worker 工作项队列**（`BackgroundWorkerLoop<T>` 的 `Enqueue` 策略），别把调度器队列当限流器用。
4. **线程死亡救不回在飞任务**（A3）：死亡时正在执行的 Task 的 continuation 链断裂，awaiter 挂起——重启只服务未来任务。靠 worker 层 lease / 超时兜底（如 `WaitSegmentReady`）。
5. **疑似死锁是保守启发式、可能误报**（A4）：两个互不相关的慢任务与真互锁在指标上无法区分。watchdog 只 ERROR + 转储上报，**不自动处置**——看到告警先看诊断转储再判断。
6. **不能强杀卡死的线程**：现代 .NET 无 `Thread.Abort`。卡在任务中途的线程只能告警 + 等待；强制终止需进程级介入。`SOE` 进程级不可恢复；`OOM` 重启只是尽力而为。
7. **watchdog 不占私有线程**（跑公共池 Timer）——「被看的不能在看病的人身上」；`WatchdogInterval ≤ 0` 关闭 = 最轻模式，但线程死亡无人重启、慢任务无人告警。
8. **`Shared` 不 Dispose**；own 实例 Dispose 前必须所有任务已结束（§5 顺序）。`CompleteAdding` 后的孤儿入队被吞 `InvalidOperationException`（不抛到入队线程，但任务丢失）。
9. **指标默认零开销也零可见**——不挂 `Hub` 就没有 `queue.depth` / 慢任务计数；生产环境建议挂（开指标的单任务开销见 §8 基准）。
10. **不用它跑单次 / 海量 fire-and-forget**——它是长生命周期 worker 的执行器，不是通用任务池；一次性负载走公共池。

---

## 8. 性能特征

**结论速览**（完整数据、环境与调参指引见 [`perf/dedicated-task-scheduler-perf.md`](perf/dedicated-task-scheduler-perf.md)）：

- **派发税亚微秒**：单任务往返 0.5~1µs，分配与公共池相同（64 B/任务）；M=1 的单线程交接甚至**快于**公共池——真实 worker 任务（µs~ms 级）下可忽略。
- **continuation 回流 ~0.3-0.5 µs/次**（M≥2；M=1 ≈ 池）——await 密集的真实 worker 摊薄无感；no-op 级吞吐天花板约为公共池的 1/6~1/11（M 个线程换隔离与确定性，有意限流）。
- **指标开仅 +0.1~0.7 µs/任务、零额外分配**——生产可常开。
- **默认有界队列在多生产者突发下有阻塞背压代价**（慢 ~1.5-2×、GC 翻倍）——`scheduler.queue.full` 频发时的处理见性能文档 §6 调参指引。

基准代码：`benchmarks/TC.Tier.Core.Benchmarks/Shared/IsolatedTaskSchedulerBench.cs`
（复跑：`dotnet run -c Release --project benchmarks/TC.Tier.Core.Benchmarks -- --filter *IsolatedTaskSchedulerBench*`）。

---

## 9. 故障排查

| 症状 / 信号 | 可能原因 | 处置 |
|------|------|------|
| `scheduler.queue.full` 频繁 + 生产者被阻塞 WARN | 消费太慢：任务长 / M 小 / consumerCount 低 | 拆短任务；升 M（不超核）；查是否有任务霸占（§7.2） |
| `scheduler.task.slow` WARN | 单任务超 `TaskTimeout` | 看线程名和已执行时长：真慢任务拆短；病理霸占（死锁前兆）看 §9 deadlock |
| `scheduler.deadlock.suspected` ERROR + 转储 | ≥2 线程慢 + 连续无推进（**可能误报**） | 看转储线程状态；真死锁需人工介入（不能自动解开）；频繁误报上调 `DeadlockConfirmTicks` |
| `scheduler.threads.restarted` WARN | 私有线程异常死亡（OOM 等） | 查死亡原因日志；重启已自动完成，但死亡瞬间的在飞任务已丢（§7.4）——检查上层 lease 兜底 |
| `scheduler.threads.degraded` ERROR | 死亡且策略不再重启（`RestartOnce` 再死 / `None`） | 调度器已降级（M-1 线程）——尽快安排 owner 重建 |
| Dispose 卡住 | 有任务未结束就 Dispose（Join 等私有线程退出） | 检查 §5 顺序：先 Stop → WaitForExit 再释放调度器 |

---

## 关联文档

- 性能实测与调参：[`perf/dedicated-task-scheduler-perf.md`](perf/dedicated-task-scheduler-perf.md)
- worker 积木用法：[`worker-loop.md`](worker-loop.md) ｜ 生命周期编排：[`lifecycle.md`](lifecycle.md)
- 单测：`tests/TC.Tier.Core.Tests/Shared/IsolatedTaskSchedulerTests.cs` ｜ 覆盖矩阵：[`unit-test-coverage.md`](unit-test-coverage.md)
