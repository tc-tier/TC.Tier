# 后台循环积木使用指南（BackgroundWorkerLoop / BackgroundWorkerLoop\<T\>）

> 定位：本仓库**唯一**合法的后台循环 / 队列消费者积木。任何「周期做事」「消费队列」「事件驱动后台处理」的需求
> 都基于它构建，**禁止 `new Thread` 自建循环**（§0）。本文覆盖：两层 API 全貌、执行器与并发度选型、
> 生命周期编排、全部钩子、背压、仓库真实范例（§6）、同类型内置组件 **CpuSampler**（§7）、铁律（§8）。
> 生命周期编排细节见 [`lifecycle.md`](lifecycle.md)；专用线程调度器见 [`dedicated-task-scheduler.md`](dedicated-task-scheduler.md)。

---

## 0. 为什么禁止 `new Thread` 自建后台循环

历史教训：旧整理（compact）worker、旧 epoch drain worker 各自 `new Thread`——生命周期分散、`Dispose` 顺序
各自管、testhost 跑完测试不退出、异常一个没接住整条线程静默蒸发。

`BackgroundWorkerLoop` 统一解决：**执行器**（公共池 / 隔离调度器注入）、**幂等启停**、**超时等待退出**、
**CAS 防双 Dispose**、**单周期异常隔离**（§4）——全部内建，子类只写业务钩子。

---

## 1. 两层选型

### 1.1 第一层 `BackgroundWorkerLoop`（纯循环骨架，无队列）

子类实现 **一个周期** `RunOneCycleAsync(ct)`，返回 `false` 停止。适合**时间驱动 / 信号驱动**：

```csharp
sealed class MyPoller : BackgroundWorkerLoop
{
    public MyPoller(TaskScheduler? scheduler = null, ILogger? logger = null)
        : base(scheduler, name: "MyPoller", logger: logger) { }

    protected override async ValueTask<bool> RunOneCycleAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), ct);   // 时间驱动（也可 PeriodicTimer / 等信号源）
        DoWork();
        return true;                                     // false = 本消费者停止
    }
}
```

构造参数（两层同签名）：

| 参数 | 默认 | 说明 |
|------|------|------|
| `TaskScheduler? scheduler` | `null` | 执行器：`null`=公共池（`Task.Run`，低频 worker）；非 null=注入调度器（高频/关键 worker，见 §2.1）。**本类不 own 调度器**——生命周期归注入方。 |
| `int consumerCount` | 1 | 消费者（循环 Task）数——协作跑在调度器线程上，**不是线程数**。范围 [1, `MaxConsumerCount`=max(核数×4,16)]，超界 throw。建议 N ≈ 调度器线程数 M（§2.2）。 |
| `string? name` | 类名 | 诊断标识（日志 / Start 时打印有效配置）。 |
| `TimeSpan? exitTimeout` | 5s | Dispose/WaitForExit 等退出超时。超时仅 LogWarning **不抛**（worker 卡死不挡进程退出）。须 > 0。 |
| `ILogger? logger` | null | 日志（启停配置 / 异常 / 超时告警）。 |

### 1.2 第二层 `BackgroundWorkerLoop<T>`（内建 5 档优先级队列）

继承第一层全部能力，追加内建 `BucketPriorityQueue<WorkerPriority, T>`。**标准后台任务模式开箱即用**——
子类只实现 `ProcessItemAsync`，生产者 `Enqueue`：

```csharp
sealed class MyFlusher : BackgroundWorkerLoop<FlushJob>
{
    public MyFlusher(TaskScheduler? scheduler = null, int consumerCount = 1, ILogger? logger = null)
        : base(scheduler, consumerCount, name: "MyFlusher", logger: logger) { }

    protected override async ValueTask ProcessItemAsync(FlushJob job, CancellationToken ct)
    { /* 处理一个 job；抛异常不杀 worker（走 OnCycleError） */ }
}

// 生产者（任意线程）：
flusher.Enqueue(job);                       // 默认 WorkerPriority.Normal = FIFO
flusher.Enqueue(urgent, WorkerPriority.Critical);
```

| 成员 | 说明 |
|------|------|
| `Enqueue(item, priority = Normal)` | 统一入队入口（无锁 + 异步唤醒等待的消费者）。**不阻塞、不拒绝**——背压见 §5。 |
| `QueueCount` | 队列近似元素数（诊断用，并发下非精确）。 |
| `Queue`（protected） | 内建队列本体——子类 override `RunOneCycleAsync` 或需要 `TryDequeue` 清队（见 §6.3 DrainPending）时直接用。 |
| `RunOneCycleAsync`（virtual） | 默认实现 = `DequeueAsync` → `ProcessItemAsync` → 继续。需要自定义循环逻辑（如 fire-and-forget 并发内核 + in-flight 限流）才 override；内建队列仍在。 |

### 1.3 选型

```
纯周期轮询 / 定时任务 / 等单一信号源 → 第一层 BackgroundWorkerLoop
生产-消费队列（事件驱动批处理）       → 第二层 BackgroundWorkerLoop<T>
```

---

## 2. 执行器与并发度

### 2.1 `scheduler` 三选（决定你的循环跑在哪）

| 选择 | 消费者跑在 | 适用 | 例 |
|------|-----------|------|----|
| `null`（默认） | 公共线程池（`Task.Run`） | **低频**周期 worker（秒级间隔）——不值得开专用线程 | CpuSampler（1s 采样，§7）、定时 flusher（§6.2） |
| `IsolatedTaskScheduler.Shared` | 进程级共享的 M 个私有线程 | 单引擎 / 轻量，所有专用 worker 共池 | 简单宿主 |
| `IsolatedTaskScheduler.Create(...)` + `Resources.Add(...)` | 引擎 own 的 M 个私有线程 | **引擎默认**：高频/关键 worker（建段、compact）；多引擎互不干扰；同步/异步 worker 分池 | 高频事件 worker（§6.4）、同步整理 worker（§6.3） |

隔离调度器的完整旋钮（线程数/队列容量/watchdog/指标）、获取方式与注意事项见
[`dedicated-task-scheduler.md`](dedicated-task-scheduler.md)——**高频 worker 禁止打公共池**（污染全局池，
拖垮无关异步工作）。

### 2.2 `consumerCount`（N）与调度器线程数（M）解耦

- N 个消费者 = N 个**循环 Task**，协作跑在 M 个调度器线程上（`await` 让出时线程服务下一个）——不是 N 个线程。
- **N ≈ M 最优**；N ≫ M 只增内存/调度开销不增吞吐（Start 时自动 WARN 提示）。
- 需要更高并行度：**加大 `IsolatedSchedulerOptions.ThreadCount`（M）**，不是 consumerCount。
- N 超过 `MaxConsumerCount`（=max(核数×4,16)）ctor 直接 throw（fail-fast，防"外部传巨大 N → 静默卡死无人知因"）。

---

## 3. 生命周期

### 3.1 方法表

| 方法 | 语义 |
|------|------|
| `Start()` | 启动 N 个消费者。幂等（CAS，重复调只首次生效）。已 Dispose 则 throw。Start 时打印有效配置一行（scheduler/M/N——治"卡死无人知因"）。 |
| `Stop()` | 设停止标志 + `cts.Cancel()`（唤醒在 ct 上等待的周期）。**不等待**退出；幂等。 |
| `WaitForExit()` | 同步等全部消费者退出（每个带 `exitTimeout`，超时仅 WARN 不抛）。⚠️ **禁止在异步上下文调用**（同步阻塞 task 死锁）——用 `WaitForExitAsync()`。 |
| `WaitForExitAsync()` | 异步版，同超时语义。 |
| `Dispose()` / `DisposeAsync()` | CAS 防双 + Stop + WaitForExit(+Async) + cts.Dispose。**不释放注入的调度器**（归注入方：Shared 进程级 / 引擎 own 经 Resources）。 |

### 3.2 经 `LifecycleBase` 编排（推荐）

```csharp
// 在 LifecycleBase 派生的 OnInitializeBegin / OnInitializeComplete 里（⚠️ 不要在构造器里——this 未就绪）：
ConfigureBackgroundWorker(new MyFlusher(scheduler: WorkerScheduler, consumerCount: 2, logger: Logger));
```

注册后基类保证：
- **Start 时机**：恢复完成**之后**才 Start（不与恢复竞态），装配就绪后一定跑。
- **Dispose 顺序**：先 Stop → WaitForExit → worker.Dispose，**再** `Resources.Dispose()`——注入的 own 调度器
  在所有任务结束后才释放（满足 `IsolatedTaskScheduler.Dispose` 的前置条件）。
- ⚠️ `ConfigureBackgroundWorker` 只挂 **一个** worker（再次调用会把新传入的直接 Dispose）——多个后台职责
  用一个 `BackgroundWorkerLoop<T>` + 优先级队列表达，不要拆多个 worker。

### 3.3 自管模式

不经 LifecycleBase 的宿主（工具/测试/独立组件）自己按 **Stop → WaitForExit → Dispose** 顺序调即可；
CpuSampler 在引擎里即此模式：进 `Resources` 统一释放、恢复完成后手动 `Start()`（§7）。

---

## 4. 钩子全表（两层通用）

| 钩子 | 调用时机 / 默认行为 |
|------|--------------------|
| `RunOneCycleAsync(ct)`（abstract） | 一个工作周期。返回 `false` = 本消费者停止（多消费者下不影响其它消费者）。**抛异常不杀 worker**——异常走 `OnCycleError`，循环继续。收到 `Stop` 触发的 `OperationCanceledException` = 正常退出（不进 OnCycleError）。 |
| `OnLoopStart()`（virtual） | 启动前一次（多消费者仅 `Start()` 调用线程跑一次）。初始化资源/状态。 |
| `OnLoopExitAsync(ct)`（virtual） | 退出后 drain/flush（残留处理、最后落盘）。多消费者下仅**末位退出者**执行一次；`ct` 恒为 `None`（退出清理不应被再次取消）；异常被捕获记 WARN。见 §6.2（退出前最后 flush）。 |
| `OnCycleError(ex)`（virtual） | 单周期异常钩子，默认 LogWarning 后继续。子类可 override 做重试/告警/计数/熔断。`OperationCanceledException` 不进此钩子。 |
| `OnCycleCompleted(elapsedMicros)`（virtual） | 每周期完成耗时（含 await 等待）。默认 >10ms LogDebug；**推荐 override 接 `ObservabilityHub.Metrics.Histogram`**——慢循环是后台 worker 最常见的性能问题，接上即默认可观测。 |

---

## 5. 优先级队列与背压

### 5.1 `WorkerPriority` 5 档（值小者先出）

| 档 | 值 | 场景 |
|----|----|------|
| `Critical` | 0 | 强制/紧急（如 Allocate 缺段强制建段） |
| `High` | 1 | 正常优先任务（如普通建段） |
| `Normal` | 2 | **默认**（段满事件、管道消息——多数场景 FIFO） |
| `Low` | 3 | 可延迟（如区间表压缩） |
| `Background` | 4 | 空闲时清理/维护 |

### 5.2 ⚠️ 内建队列**无界**——背压必须自己设计

`Enqueue` 不阻塞、不拒绝、队列无容量上限。生产者持续快于消费者 → backlog 无限增长 → **OOM**。
仅适用于「生产速度可控、峰值可消化」的场景。需要背压的三种做法：

1. **上游限流**（首选）：生产侧天然有限（如引擎写者的 in-flight 上限）。
2. **入队前查水位**：`QueueCount` 超阈值时拒绝/降级/阻塞生产者（调用方语义自定义）。
3. **CPU 背压**：挂 `CpuSampler`，按 `ThrottleFactor` 三档放行/降速/报过载（§7.3）。

> ⚠️ 不要把阻塞 IO 塞进 `LightEpoch` 的 drain action 来"借用"它的线程——epoch drain 是协作式、由读线程顺手
> 执行，塞毫秒级 IO 会降吞吐/死结（见 `../COORDINATION.md` §5）。阻塞重操作（PunchHole、段提升等）**可以**
> 投递给 `BackgroundWorkerLoop` 执行。

---

## 6. 典型用法范例（自包含，可直接套用）

> 本文档是 Core 组件文档，范例全部自包含——每个模式给完整可编译骨架 + 一句「学什么」。

### 6.1 `CpuSampler`——时间驱动 + 公共池（Core 内置，§7 详述）

`PeriodicTimer` 驱动采样，不传 scheduler（低频走公共池），单消费者。第一层最简形态的代表，也是 Core 唯一
开箱即用的 worker 组件（直接 new，不用继承）。

### 6.2 定时 flusher——动态间隔 + 退出前 flush（时间驱动，公共池）

```csharp
sealed class AdaptiveFlusher : BackgroundWorkerLoop
{
    public AdaptiveFlusher(ILogger? logger = null)
        : base(name: "AdaptiveFlusher", exitTimeout: TimeSpan.FromSeconds(10), logger: logger) { }
        //      ↑ 低频刷盘走公共池（不传 scheduler）    ↑ 重 IO 退出清理宽限到 10s（默认 5s）

    protected override async ValueTask<bool> RunOneCycleAsync(CancellationToken ct)
    {
        var sleepMs = ScheduleNextFlushMs();                  // 动态间隔（如按脏数据率自适应）
        await Task.Delay(sleepMs, ct).ConfigureAwait(false);  // ★ Stop → ct.Cancel 唤醒 Delay，正常退出
        if (HasDirty()) Flush();
        return true;
    }

    protected override ValueTask OnLoopExitAsync(CancellationToken ct)   // 退出前最后刷一次（不丢脏数据）
    {
        Flush();
        return ValueTask.CompletedTask;
    }
}
```

学什么：`Task.Delay(ms, ct)` 传 ct——Stop 即醒不卡 `exitTimeout`；`OnLoopExitAsync` 做退出前落盘；
重 IO 清理给更大的 `exitTimeout`。

### 6.3 同步整理 worker——队列模式 + own 调度器 + 清队（第二层）

```csharp
sealed class CompactionWorker : BackgroundWorkerLoop<CompactionJob>
{
    public CompactionWorker(IsolatedTaskScheduler scheduler, ILogger? logger)
        : base(scheduler, name: "compaction", logger: logger) { }
        //      ↑ own 注入的隔离调度器：同步整理任务与异步建段 worker 分池，互不饿死
        //        （同步任务霸占共享池的 M 线程会饿死异步 worker——见 dedicated-task-scheduler.md §2）

    protected override ValueTask ProcessItemAsync(CompactionJob job, CancellationToken ct)
    {
        RunJobSafe(job);              // ★ 只实现单项处理——队列/唤醒/异常隔离/多消费者全由基类
        return ValueTask.CompletedTask;
    }

    public void DrainPending()        // Queue 属性直用：外部清理状态时清空待处理队列
        { while (Queue.TryDequeue(out _)) { } }
}
```

学什么：标准后台任务模式只写 `ProcessItemAsync`；同步 worker 用**单线程 own 调度器**分池；
`Queue.TryDequeue` 做清队。

### 6.4 高频事件 worker——多消费者 + own 调度器（第二层，引擎级标准姿势）

```csharp
sealed class SegmentEventWorker : BackgroundWorkerLoop<SegmentEvent>
{
    public SegmentEventWorker(IsolatedTaskScheduler scheduler, int consumerCount, ILogger? logger)
        : base(scheduler, consumerCount, name: "segment-events", logger: logger) { }
        //        ↑ N 个消费者 Task 协作跑调度器 M 个私有线程（N ≈ M，与 M 解耦，§2.2）

    protected override async ValueTask ProcessItemAsync(SegmentEvent evt, CancellationToken ct)
        => await HandleAsync(evt, ct);
}

// 装配（LifecycleBase 派生的 OnInitializeBegin / OnInitializeComplete 里）：
WorkerScheduler = IsolatedTaskScheduler.Create(new IsolatedSchedulerOptions { Name = "worker" });
Resources.Add(WorkerScheduler);                       // 随引擎 Dispose 释放（先停 worker 后释放调度器，§3.2）
ConfigureBackgroundWorker(new SegmentEventWorker(WorkerScheduler,
    consumerCount: WorkerScheduler.ThreadCount, logger: Logger));

// 生产者（任意线程）：
worker.Enqueue(evt);                                  // 默认 Normal = FIFO
worker.Enqueue(urgent, WorkerPriority.Critical);      // 紧急插队
```

学什么：高频/关键路径 worker 的完整装配链——own 调度器 + Resources + ConfigureBackgroundWorker +
多消费者（N=M）；continuation 回流私有线程不与写者争公共池。

---

## 7. 同类型内置组件：`CpuSampler`（CPU 采样 + 限流系数）

`Core/Shared` 内置的**可复用**组件（非抽象骨架，直接 new 即用）：本进程 CPU 利用率采样 + 分段限流系数计算，
继承 `BackgroundWorkerLoop`（公共池、1s 周期、启停 Dispose 全由基类）。

### 7.1 计算模型

- **进程口径**：`raw = ΔProcess.TotalProcessorTime / (Δ墙钟 × ProcessorCount)`，归一化 0~1（1.0 = 跑满所有核）。
  其它进程造成的系统饱和**看不见**（整机口径需性能计数器，跨平台不可用，故明确取进程口径）。
- **EMA 平滑**：`ema' = α·raw + (1−α)·ema`，首样本直接取 raw（标志位初始化，不拿 0 当哨兵——空闲→高载第一个
  样本直接跳变是旧实现的根因）。
- **限流系数 `ThrottleFactor`（0~1）**：分段线性——CPU ≤ lowCutoff → 0（不限流）；low~high → 线性 0→1（软降速）；
  ≥ highCutoff → 1（强阻塞）。纯函数映射，数学契约由单测锁定。

### 7.2 旋钮（ctor 全量校验 fail-fast，非法即 throw）

| 参数 | 默认 | 说明 |
|------|------|------|
| `sampleInterval` | 1s | 采样周期，须 > 0 |
| `emaAlpha` | 0.5 | 平滑系数 ∈ (0,1]——大=跟手、小=平滑 |
| `throttleLowCutoff` | 0.70 | 不限流阈值；须 0 ≤ low < high ≤ 1（倒挂 throw——否则斜率为负/除零，反向限流） |
| `throttleHighCutoff` | 0.90 | 强阻塞阈值 |
| `hub` | Disabled | 每采样发布 `cpu.utilization` / `cpu.throttle.factor` Gauge |
| `name` / `logger` | "CpuSampler" / null | 档位切换（正常→限流→强阻塞→恢复）打 WARN 日志 |

### 7.3 消费模式（写热路径限流的典型接法）

```csharp
// Start 后任意线程读（Volatile，热路径零开销）：
if (sampler.ThrottleFactor <= 0.0) return;    // 档 0：正常——放行
// 档 1（0<factor<1）：软降速——短暂自旋/让出等 CPU 回落（配尝试上限）
// 档 2（factor>=1）：强阻塞——自旋超时后报过载错误（拒绝本次操作）
```

### 7.4 生命周期

两种接法（`CpuSampler` 无组件依赖，怎么挂都安全）：

- **经 `ConfigureBackgroundWorker` 注册**：恢复完成后自动 Start，随基类 Dispose 统一停（§3.2）；
- **自管**（独立组件/工具场景）：实例进 `Resources` 统一释放，装配完成后手动 `Start()`（幂等）。

读端 `CpuUtilization` / `ThrottleFactor` 热路径 `Volatile.Read`，Hub 未注入时零开销。

---

## 8. 铁律（禁止清单）

- ❌ **绝不** `new Thread(...)` 自建后台循环——所有后台循环继承 `BackgroundWorkerLoop`（两层之一）。
- ❌ **绝不**在构造器里 `ConfigureBackgroundWorker` 或 `Start`（构造未完成、`this` 未就绪）——放 `OnInitializeBegin`/`OnInitializeComplete`。
- ❌ **绝不**高频/关键 worker 打公共池（不传 scheduler）——污染全局池，拖垮全进程异步工作（§2.1）。
- ❌ **绝不**同步重 IO worker 与异步 worker 共用一个调度器实例——同步任务霸占 M 线程饿死异步（2026-08-14 实测教训，见 dedicated-task-scheduler.md §2）。
- ❌ **绝不**在异步上下文调 `WaitForExit()`（同步阻塞死锁）——用 `WaitForExitAsync()`。
- ❌ **绝不**把无界队列裸奔上线——`BackgroundWorkerLoop<T>` 队列**默认无界**，生产者快于消费者 = OOM；背压三招见 §5.2。
- ❌ **绝不**在 `LightEpoch` drain action 里塞阻塞 IO（协作式，读线程顺手执行）——投给 worker 循环。
- ✅ 单周期异常**不要**杀 worker——基类已隔离（异常走 `OnCycleError`），循环自动继续；确需致命退出才在 `OnCycleError` 里返回停止。
- ✅ 所有后台 worker 经 `ConfigureBackgroundWorker` 注册（单实例；多职责用优先级队列表达）——统一 Start 时机 / Dispose 顺序。
- ✅ 推荐override `OnCycleCompleted` 接 Hub Histogram——慢循环默认可观测。

---

## 9. 一句话决策

```
我要做后台循环 / 队列消费？
  → 时间/信号驱动：BackgroundWorkerLoop；生产-消费队列：BackgroundWorkerLoop<T>
  → 低频（秒级）不传 scheduler 走公共池；高频/关键注入 IsolatedTaskScheduler（引擎 own）
  → 经 ConfigureBackgroundWorker 注册（或 Resources 自管），绝不 new Thread
我要 CPU 背压 / 降速？
  → 直接用内置 CpuSampler（§7），读 ThrottleFactor 三档消费
```

---

## 关联文档

- 生命周期编排（Start 时机/Dispose 顺序/恢复流程）：[`lifecycle.md`](lifecycle.md)
- 专用线程调度器（隔离执行器）：[`dedicated-task-scheduler.md`](dedicated-task-scheduler.md)｜性能：[`perf/dedicated-task-scheduler-perf.md`](perf/dedicated-task-scheduler-perf.md)
- 积木全景：[`../COORDINATION.md`](../COORDINATION.md)｜单测：`BackgroundWorkerLoopTests.cs` / `CpuSamplerTests.cs`（矩阵见 [unit-test-coverage.md](unit-test-coverage.md)）
