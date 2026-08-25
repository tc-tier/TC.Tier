# TC.Tier.Core 协调框架使用指南

> 本文件是 **Core 层协调积木的"正确拼装"指南**：每个公共组件的职责、正确用法、以及反模式。
> 它不重复每个类的 XML 注释，而是回答"**遇到 X 该用哪个积木、怎么用、什么绝对不要做**"。
>
> **Core 的范围**（五层架构的第三层）：`LifecycleBase`/`RecoveryBase`（实现 `TC.Tier.Contracts` 的 `ILifecycle`/`IRecovery`——依赖倒置）+ 基础设施目录（Epochs/NativeInterop/Logging/Metrics/Tracing/Observability）+ `Primitives/`（底层叶子积木：`SpinRWLock`/`FairGate`/`Atomic128`/`SectorAlignment`/`AlignmentConst`/`SpinLockScope`/`IKeyComparer`/`KeyComparer` + 原生内存/异步同步原语/计算计时叶子）+ `Collections/`（容器型复合体：队列/缓存/池）+ `Shared/` 非业务积木（`ResourceGroup`/`BackgroundWorkerLoop` 等）。接口/数据契约在 Contracts，源生成器特性在 `TC.Tier.CodeGen.Abstractions`。
>
> 配合阅读（项目内深读）：
> - [`docs/lifecycle.md`](docs/lifecycle.md) —— 生命周期 前→中→后 三阶段正确拼装（`LifecycleBase`/`RecoveryBase`）
> - [`docs/resource-management.md`](docs/resource-management.md) —— 资源统一管理（`ResourceGroup`，强制）
> - [`docs/worker-loop.md`](docs/worker-loop.md) —— 后台循环统一设计（`BackgroundWorkerLoop`）
> - [`docs/locking-and-epoch.md`](docs/locking-and-epoch.md) —— 互斥/回收/原子更新（`SpinRWLock`/`FairGate`/`LightEpoch`/`Atomic128`，epoch 易和锁混淆）
> - [`docs/version-scheme.md`](docs/version-scheme.md) —— 版本号协调状态机（`EpochProtectedVersionScheme`，epoch 保护下分阶段过渡版本；当前 Core 就绪、上层引擎未接入）
> - [`docs/memory.md`](docs/memory.md) —— 原生内存与对象池（`AlignedMemoryManager`/`NativeArena`/`PinnedBufferPool`/`OverflowPool`，⚠️ 全部非托管必须 Dispose）
> - [`docs/async-primitives.md`](docs/async-primitives.md) —— 异步同步原语与队列（`AsyncManualResetEvent`/`AsyncCountDown`/`AsyncQueue`；实测见 [docs/perf/core-primitives-perf.md](docs/perf/core-primitives-perf.md)）
> - [`docs/priority-queues.md`](docs/priority-queues.md) —— 优先队列族（`BucketPriorityQueue`/`SkipListPriorityQueue`/`AsyncPriorityQueue`，Linux 完整验证；实测见 [docs/perf/priority-queues-performance.md](docs/perf/priority-queues-performance.md)）
> - [`docs/cache-and-compute.md`](docs/cache-and-compute.md) —— 缓存/弱引用字典/CRC/计时/位运算/异常（`ClockCache`/`ShardLockWeakReference`/`UnifiedCrc`/`MicroTimer`/`Utility`/`ThrowHelper`）
> - [`docs/native-interop.md`](docs/native-interop.md) —— 原生 syscall facade（`DiskNative`/`FileNative`/`MemoryNative`，IO/预分配/打洞/刷盘/内存锁；★ Core.IO 的内部实现底座，**不对外**——能力已由 Core.IO 全部封口）
> - [`docs/io.md`](docs/io.md) —— 文件 IO 原语层（`IFileSystem`/`IFileHandle`/`FileHandlePool`/`MemoryFileSystem`/`FaultInjectingFileSystem`，两平面×四介质×能力协商；DIO 对齐/映射生命周期/memfs 模式选型等陷阱清单）
> - [`docs/virtual-file-system.md`](docs/virtual-file-system.md) —— **第四介质 Raw**（`RawFileSystem`：`.raw` 文件 / Linux 块设备——自持一致性 + 自管页缓存 + 多载体 + 采集还原管线；本地持久化推荐位；两档 IO/维护门闩/dd 快道/常见配方；性能见 [docs/perf/io-performance.md](docs/perf/io-performance.md) §8）
> - [`../TC.Tier.Core.IO.S3/COORDINATION.md`](../TC.Tier.Core.IO.S3/COORDINATION.md) —— S3 兼容对象存储客户端层（`S3ObjectStore`：SigV4 自写/零外部包，一个客户端覆盖 S3/COS/MinIO/OSS/R2；使用指南 [`../TC.Tier.Core.IO.S3/docs/network-file-system-s3.md`](../TC.Tier.Core.IO.S3/docs/network-file-system-s3.md)）
> - [`docs/observability.md`](docs/observability.md) —— 可观测（`ObservabilityHub`/`IMetricsSink`/`ITracer`：视图全景/采样/零开销契约/测试场景）
> - [`docs/logging.md`](docs/logging.md) —— 日志（`ILogger`/`LoggerExtensions`：重载矩阵/热路径规则/与 Hub 的边界）
> - [`docs/dedicated-task-scheduler.md`](docs/dedicated-task-scheduler.md) —— 专用线程调度器（`IsolatedTaskScheduler`；性能实测在 [`docs/perf/`](docs/perf/)）
> - [`../TC.Tier.Contracts/COORDINATION.md`](../TC.Tier.Contracts/COORDINATION.md) —— 接口/数据契约（Core 实现它们的 `ILifecycle`/`IRecovery`）

---

## 0. 一句话总纲

**Core 提供的协调积木都是对的、齐的。出问题的地方是上层（IO 引擎/数据结构）把积木拼错了。**
本文件的目的就是把"正确拼法"钉死，杜绝重蹈覆辙。

---

## 1. 核心积木全景

| 积木 | 位置 | 职责 | 何时用 |
|------|------|------|--------|
| `LifecycleBase<THints>` | `Shared/LifecycleBase.cs` | 生命周期骨架：Initialize/Dispose 模板、worker 启停编排、状态查询 | **所有**有生命周期的对象（IO 引擎、数据结构）都继承它 |
| `RecoveryBase<THints>` | `Shared/RecoveryBase.cs` | 恢复模板：RecoverAsync 编排 + CAS 状态机 + 进度上报 | 需要"启动恢复"的对象；继承后只 override `OnRecoveryCoreAsync` |
| `ResourceGroup` | `Shared/ResourceGroup.cs` | 资源统一释放：按名注册、逆序释放、聚合异常 | `LifecycleBase.Resources` 已内建一个；子类 `Resources.Add(...)` |
| `BackgroundWorkerLoop` / `BackgroundWorkerLoop<T>` | `Shared/BackgroundWorkerLoop.cs` | 后台执行：循环骨架（公共池/隔离调度器注入 + 多消费者）+ 内建优先级队列 | **所有**后台循环/队列消费者——禁止 `new Thread` 自建 |
| `LightEpoch` | `Epochs/LightEpoch.cs` | RCU 式延迟回收：epoch 保护 + 协作 drain | **上层结构组件**（index/metadata 回收）；存储读写互斥**用 SpinRWLock** |
| `NativeAtomic128` | `NativeInterop/NativeAtomic128.cs` | **128 位 CAS**（x86 `lock cmpxchg16b` / ARM64 `ldaxp-stlxp`），解决 >64 位载荷无 `Interlocked` | 大载荷原子更新（指针+标志 / 水位+ABA version）；⚠️ `location` 须 **16B 对齐**；底层原语 |
| `DiskNative`/`FileNative`/`MemoryNative` | `NativeInterop/` | 跨平台 syscall facade（扇区探测/无缓冲 IO/预分配/打洞/刷盘/内存锁/扩展属性） | 🔒 **`internal`（编译期封堵，仅 Core.IO 消费）**；需要 IO 的组件用 `TC.Tier.Core.IO`（[`docs/io.md`](docs/io.md)），外部业务经 `IStorageEngine`；syscall→Core.IO 映射表与仓内过渡 IVT 例外见 [`docs/native-interop.md`](docs/native-interop.md) |
| `IFileSystem`/`IFileHandle`/`FileHandlePool`/`DiskFileSystem`/`MemoryFileSystem` | `IO/`（单一命名空间 + 职责子目录 Disk/Mem/Shared/Testing——★ 本层目录≠命名空间，全仓唯一例外） | **文件 IO 原语层**（两平面 × 介质多态；能力协商、DIO 逐句柄探测对齐、文件级 Append 预留、字节范围锁、内存映射、memfs 双分配模式、池 Acquire/Release 归还协议）。**公开面=契约+枚举+错误+池+两介质 fs 工厂；句柄实现/FaultInjecting/共享工具全部 internal**（facade 纪律——FaultInjecting 是测试设施，生产禁用） | 需要文件级 IO 的所有组件；外部通常经 `IStorageEngine` 间接用，直接用先读 [`docs/io.md`](docs/io.md) 陷阱清单（⚠️ mem Dispose=拔盘方向差异置顶） |
| `Atomic128<T>` | `Primitives/Atomic128.cs` | **标准 128 位 CAS 单槽封装**（16B 对齐背板 + 探测降级 + 裸读不撕裂）——`NativeAtomic128` 之上的易用层 | **优先用它**（别裸调 `NativeAtomic128`）；`T` 须 16B blittable struct；标准范式见 [`docs/locking-and-epoch.md`](docs/locking-and-epoch.md) §3 |
| `SpinRWLock` | `Primitives/SpinRWLock.cs` | **写偏向** CAS 自旋 RW 锁原语（bit63 写持有 + **bit62 写等待挡新读者** + 读计数递增；下溢绊线 + Debug 值示波器）。任何需要 RW 互斥的对象挂一个 `SpinRWLock` 字段即获得协调锁——写者不被持续读者流饿死（2026-08-20 自 LockWord 重构，读优先→写偏向，Monitor 等待-通知职责删除） | 对象读写互斥：读 `AcquireShared`，写/销毁 `AcquireExclusive`（标准范式见 [`docs/locking-and-epoch.md`](docs/locking-and-epoch.md) §1） |
| `FairGate` | `Primitives/FairGate.cs` | **到达顺序公平门**——重试循环获取资源的协调器：fast path 查 `HasWaiters` 让位、慢路径 `TryAcquireSlow`、资源可获取后 `Wake`（PulseAll + 5ms 让渡先手） | 多写者竞争同资源不插队（现使用方：AcquireExtent 区间占用）；见 [`docs/locking-and-epoch.md`](docs/locking-and-epoch.md) §1.5 |
| `CpuSampler` | `Shared/CpuSampler.cs` | CPU 采样限流（进程口径 EMA + 三档 `ThrottleFactor`） | 写热路径 CPU 背压；用法见 [`docs/worker-loop.md`](docs/worker-loop.md) §7 |
| `IsolatedTaskScheduler` | `Shared/IsolatedTaskScheduler.cs` | 隔离线程 Task 调度器（M 私有线程 + watchdog + 死亡重启 + 指标） | 高频/关键 worker 的执行器（引擎 own 实例）；见 [`docs/dedicated-task-scheduler.md`](docs/dedicated-task-scheduler.md) |
| `InstanceTracker` | `Shared/InstanceTracker.cs` | 实例级泄漏跟踪 | `LifecycleBase` 构造自动注册，无需手动 |
| **— 可观测（见 §3）—** | | | |
| `ILogger` / `ILoggerFactory` | `Logging/` | 极简日志（去掉 M.E.Logging 的 `TState`/`formatter`）；全 36 重载 null 安全 + `IsEnabled` 短路；`NullLogger` 零开销默认 | 上层注入 factory；用法见 [`docs/logging.md`](docs/logging.md) |
| `IMetricsSink` | `Metrics/` | 指标三原语 Counter/Histogram/Gauge（`ReadOnlySpan<KeyValuePair>` tags 零分配热路径） | 经 `ObservabilityHub` 视图走，**不直接调** |
| `ITracer` / `ISpan` | `Tracing/` | 分布式追踪（`AsyncLocal` 父子 span）；`NullTracer` 默认；AOT 友好（无反射/Emit） | `if (TracingEnabled) BeginSpan`；`using` 自动收尾 |
| `ObservabilityHub` | `Observability/` | **唯一可观测接入点**：聚合 Metrics + Tracing（⚠️ **不含 Logging**），两级短路 + 采样 + 5 维度视图（Metrics/Storage/Log/Index/**SegmentAllocator**） | `ObservabilityHub.Create(sink, tracer, opts)` 或 `Disabled`；完整指南 [`docs/observability.md`](docs/observability.md) |
| **— 高性能工具集（见 §4，全部性能验证过）—** | | | | |
| `Primitives/` 叶子工具 + `Collections/` 容器 | `Primitives/` · `Collections/` | 异步同步原语 / 原生内存 / 优先队列 / 内存池 / 缓存 / 计时 / CRC——零分配、无锁、池化、硬件加速 | **先查 §4.1 选型表，禁止自己造**。叶子（原生内存/异步同步/计算计时）在 `Primitives/`，容器（队列/缓存/池）在 `Collections/` |
| **— 核心契约与原语（见 §6）—** | | | |
| `Primitives/` | `Primitives/` | **Core 原语**：`SpinRWLock`/`FairGate`/`Atomic128`/`SectorAlignment`/`AlignmentConst`/`SpinLockScope`/`IKeyComparer`/`KeyComparer` | §5（`SpinRWLock`/`FairGate`/`Atomic128`/`LightEpoch`）+ §6（`KeyComparer`/对齐/`SpinLockScope`）。⚠️ 接口/数据契约（`ILifecycle`/`IStorageEngine`/`LogicalAddress`…）在 Contracts，使用红线见 [`../TC.Tier.Contracts/COORDINATION.md`](../TC.Tier.Contracts/COORDINATION.md) §5 |

---

## 2. 三大生命周期支柱的正确用法

### 2.1 `LifecycleBase<THints>` —— 生命周期骨架

**契约**：Initialize 是固定模板，子类**不改流程、只 override 钩子**：

```
Initialize(hints):
  CAS 幂等闸门
  → OnInitializeBegin()          // [前] 子类：引擎 init + Resources.Add 装配
  → CreateRecovery()             // 工厂：子类返回 IRecovery（或 null=无需恢复）
  → 后台 task:
      await RecoverAsync(hints)  // [中] 恢复
      OnInitializeComplete()     // [后] 仅恢复成功后串行执行
      backgroundWorker.Start()   // worker 在 [后] 之后启动
```

**子类只碰这些**（其余别动）：
- `OnInitializeBegin()` —— 装配资源、初始化引擎、`Resources.Add(...)`
- `CreateRecovery()` —— `protected virtual`，返回 `new XxxRecovery(this)`（或 `null`）
- `OnInitializeComplete()` —— 恢复成功后的装配（可安全读恢复产物）
- `ConfigureBackgroundWorker(worker)` —— 在 Begin 或 Complete 里调，注册长生命周期 worker
- `DisposeOverride(bool)` / `DisposeOverrideAsync(bool)` —— 额外清理（核心清理基类已做）
- `EnsureReady()` —— 读写入口第一行守卫

**铁律**：
- ❌ **绝不**自己写 `Initialize` / `Dispose`（它们是 `non-virtual` 模板，绕不过；`new` 隐藏也无效）。
- ❌ **绝不**在构造器里启动线程/后台循环并把 `this` 暴露（构造未完成竞态）。
- ✅ 长生命周期 worker 一定走 `ConfigureBackgroundWorker`——基类保证它在 **恢复完成后**才 Start，且 Dispose 时按正确顺序 Stop+WaitForExit。

### 2.2 `RecoveryBase<THints>` —— 恢复模板

**契约**：`RecoverAsync` 是固定编排，子类**不 override**，只 override 钩子：

```
RecoverAsync(hints, ct):
  CAS 闸门（Ready→no-op / Recovering→抛"不可重入"）
  → WaitForDependenciesAsync(ct)   // virtual：层间 join（等子引擎就绪），默认空
  → OnRecoveryStart()              // virtual：默认上报 Recovering/0%
  → OnRecoveryCoreAsync(hints, ct) // ★ abstract：唯一必 override，真正恢复算法
  → OnRecoveryComplete()           // virtual：默认上报 Completed/100%
  → MarkReady()
  （异常 → 置 Failed + 回退闸门 + 重抛）
```

**子类只 override**：
- `OnRecoveryCoreAsync(hints, ct)` —— **唯一必 override**：扫盘/读 meta/回放/重建。进度用 `RaiseProgress(percent, detail)`，取消检查用 `ct.ThrowIfCancellationRequested()`。
- `WaitForDependenciesAsync(ct)` —— 需要等子层恢复完才读本层产物的，在此 `await owner._engine.WaitForReadyAsync(ct)`。
- `CancelRecovery()` —— 需要"显式取消清理"（停扫盘、释放扫描资源）的 override；纯 ct 轮询的不用动。

**铁律**：
- ❌ **绝不**自己维护 `_state` / CAS 闸门 / `MarkReady` 调用顺序——全在基类。
- ✅ 状态查询一律用 `RecoveryState` / `IsReady`，**不**用"Recovery 非 null"判断是否就绪。

### 2.3 `ResourceGroup` —— 资源统一管理（**强制**）

**全部资源管理必须统一走 `ResourceGroup`**（`LifecycleBase.Resources` 已内建一个）——禁止自建 `_disposables`、禁止手动管释放顺序。它解决两个老大难：**释放顺序**（自动逆序）与**泄漏**（构造期/异常路径漏释放）。

- `Owned`（默认）：本实例拥有的，Dispose 时组释放。
- `Referenced`：外部注入的共享资源，只跟踪诊断、**不释放**（防双释放）。

正确拼法、所有权判定、范式见 [`docs/resource-management.md`](docs/resource-management.md)。

### 2.4 `BackgroundWorkerLoop` —— 后台执行（**唯一**合法的后台循环）

两层：
- **`BackgroundWorkerLoop`**（第一层）：纯循环骨架。子类实现 `RunOneCycleAsync(ct)`（一个周期，返回 false 停止）。钩子：`OnLoopStart` / `OnLoopExitAsync` / `OnCycleError`。构造注入 `TaskScheduler? scheduler`（null=公共池 `Task.Run`，低频 worker；高频/关键 worker 注入 `IsolatedTaskScheduler`）。
- **`BackgroundWorkerLoop<T>`**（第二层）：内建 5 档优先级队列。子类只实现 `ProcessItemAsync(item, ct)`，生产者 `Enqueue(item, priority)`，开箱即用。

```csharp
// 标准后台任务（队列消费者）——只实现 ProcessItemAsync
sealed class MyFlusher : BackgroundWorkerLoop<FlushJob>
{
    protected override async ValueTask ProcessItemAsync(FlushJob job, CancellationToken ct)
    { /* ... */ }
}
// 在 OnInitializeBegin 或 OnInitializeComplete 里注册：
ConfigureBackgroundWorker(new MyFlusher(scheduler: myScheduler, name: "meta-flusher"));
// scheduler：null=公共池（低频）；高频/关键 worker 注入 IsolatedTaskScheduler（见 docs/worker-loop.md §2）
```

**铁律**（后台循环的核心教训）：
- ❌ **绝不** `new Thread(...)` 自建后台循环——自建循环生命周期分散、`Dispose` 不统一、异常无人接住、进程退出不干净。
- ✅ **所有**后台循环继承 `BackgroundWorkerLoop`，经 `ConfigureBackgroundWorker` 注册——基类统一管 Start（恢复后）/ Stop / WaitForExit / Dispose 顺序。
- ✅ 单周期异常**不要**杀 worker——基类已隔离（走 `OnCycleError`），循环自动继续。

---

## 3. 可观测基础设施

Core 的另一条线——**不参与协调，但同样是所有上层共享的底层积木**。
核心原则：日志/指标/追踪**三信号各自独立配置**；指标 + 追踪经 `ObservabilityHub` 统一接入、两级短路。
**完整指南（视图方法清单 / 采样算法 / 零开销契约 / 测试场景 / 自定义 sink）：
[`docs/observability.md`](docs/observability.md)（可观测）+ [`docs/logging.md`](docs/logging.md)（日志）。**

### 3.1 日志 `ILogger`（独立，**不经** Hub）
- 极简接口（`Log` / `IsEnabled`），刻意去掉 `Microsoft.Extensions.Logging` 的 `TState`/`formatter`。
- **零开销**：`LoggerExtensions` 每个重载先 `IsEnabled` 再格式化——**调用方默认无需手写 IsEnabled**；0/1/2/3 参用强类型重载避免装箱；**超过 3 参**走 `params` 兜底（数组+装箱在调用点发生，**仅热路径**才值得手动 `IsEnabled` 保护）。
- 命名占位符 `{name}` 自动按顺序映射 `{0},{1},...`（源生成正则）。
- `NullLogger`/`NullLoggerFactory` 单例——无注入时零开销。
- ⚠️ **Logging 不归 `ObservabilityHub` 管**——由上层独立注入 `ILoggerFactory`，`ObservabilityOptions` 明确不控制日志级别。
- ★ **宿主桥接很简单**：`LogLevel` 与 M.E.Logging 枚举值 1:1（直接强转），控制台 / MEL 全家桶 / 可观测后端十几行适配完——现成范例见 [`docs/logging.md`](docs/logging.md) §4。

```csharp
logger.LogInformation("append ok entry={entry} size={size}", entryId, size);   // 命名占位
if (logger.IsEnabled(LogLevel.Debug))                                         // 仅热路径 4+ 参需要手动保护
    logger.LogDebug("...{a} {b} {c} {d}", a, b, c, d);                        // （params 调用点分配）
```

### 3.2 指标 `IMetricsSink`（经 Hub，**不直接调**）
- 三原语：`Counter`（单调累计）/ `Histogram`（分布，延迟/大小）/ `Gauge`（瞬时值，队列深度）。
- tags 用 `ReadOnlySpan<KeyValuePair<string,string>>`——零分配热路径。
- 命名约定：点号分层 + 单位后缀（`device.read.latency_us`）。
- `NullMetricsSink.Instance` 默认（`IsEnabled => false`）；真实实现（Prometheus/OTel/Datadog）由上层注入。
- ❌ **不要直接调 `IMetricsSink`**——经 `ObservabilityHub` 的维度视图走，才能拿到两级短路 + 采样。

### 3.3 追踪 `ITracer`（经 Hub，AOT 友好）
- `BeginSpan` 返回 `ISpan`（`using` 自动收尾）；`Current` 走 `AsyncLocal` 父子链，子方法无需传参。
- `NullTracer.BeginSpan` 返回**非 null 的 `NullSpan.Instance`**（修复了"返回 null 导致 `using` 块 NRE"的缺陷——`NullTracer.cs:4-6`）。
- `SpanKind`：Internal/Server/Client/Producer/Consumer；`SpanStatus`：Ok/Error（无 Unset）。
- 纯接口 + 枚举 + AsyncLocal，**无反射/Emit，AOT 友好**。
- 热路径先 `if (TracingEnabled)` 再 `BeginSpan`。

### 3.4 `ObservabilityHub` —— 唯一可观测接入点
- **聚合 Metrics + Tracing**（⚠️ **不含 Logging**——见 §3.1）。
- **两级短路**：`Options.Enabled × sink/tracer.IsEnabled × 维度开关` 三重 AND 在**构造期折叠成单个 bool**，热路径只读一个 bool（`ObservabilityHub.cs:47-53`）。
- 维度视图（方法清单与指标名见 [`docs/observability.md`](docs/observability.md) §2）：
  - `Metrics`——三原语透传（自定义指标走这）；
  - `Storage`——Read/Write/Flush/Compact/Reclaim/Throttle/QueueDepth（`storage.*`）；
  - `Log`——Append/Commit/Truncate/BufferFull/Recover（`log.*`）；
  - `Index`——Find/Insert/Upsert/Delete/Scan（`index.*`）；
  - `SegmentAllocator`——**段表专用**：Segment 分配/释放/FreeList 深度（`segment_allocator.*`；开关默认 false）。
  > 命名约定：文件名 = 类名（去 `View` 后缀）= Hub 属性名。⚠️ `SegmentAllocator` 是段表专用视图（非通用
  > Allocator 原语——曾经的 `AllocatorView` 命名冒充了抽象角色，已正名）；需要新维度加独立 partial 视图，不泛化旧的。
- **采样**：每操作独立计数器取模（`ShouldSample`，`Interlocked` 线程安全）；**背压/错误信号全采、不走采样**（`OnThrottle`/`OnBufferFull`/`ReportError`）。
- 工厂：`Create(sink, tracer, opts)`（null 参数 → Null 单例零开销）/ 高级 `Create(..., sampleRate)` / `Disabled` 全默认单例。

```csharp
var hub = ObservabilityHub.Create(myMetricsSink, myTracer, opts);   // 注入真实实现
var hub0 = ObservabilityHub.Disabled;                               // 零开销默认

if (hub.Storage.IsEnabled) {
    using var t = hub.Storage.BeginReadSample();   // MicroTimer，命中采样才计
    /* do read */
    hub.Storage.OnRead(bytes, t.Microseconds, errorCode);
}
using var span = hub.BeginSpan("wal.append", SpanKind.Producer);  // TracingEnabled=false → null
```
⚠️ `hub.BeginSpan` 返回 `ISpan?`（关闭时 null），与裸 `ITracer.BeginSpan`（非 null）不同——调用方须容忍 null 或先判 `TracingEnabled`。

### 3.5 可观测/日志的测试场景（组件测试怎么验证）

| 场景 | 模式 |
|------|------|
| 指标确实发了 | 挂**捕获 sink**（并发安全集合——任务可能在私有线程上发）+ `MetricsConfig.Enabled=true`；执行侧指标用 `SpinWait.SpinUntil` 等待（入队/执行指标有时序差） |
| 关了就零发射 | sink 挂上但 `Metrics.Enabled=false` → 断言 sink 全空（验证构造期短路折叠） |
| 维度开关独立 | `EnableSegmentAllocatorMetrics=false`（默认）→ 仅该视图 `IsEnabled=false` |
| 采样确定性 | `ShouldSample` 每 100 精确采 rate 个（单测锁 0/1/50/99/100 边界）；各视图 `ShouldSampleXxx` 计数独立 |
| 背压/错误全采 | `OnThrottle`/`OnBufferFull`/`ReportError` 不受 `SampleRate` 影响 |
| 日志 | 捕获 logger（`List<string>` + lock）断言消息；null logger 全链路无异常（36 重载 null 安全） |

> Core 内现行范例：`ObservabilityHubTests`（89 测试）、`IsolatedTaskSchedulerTests.Metrics_*`、`CpuSamplerTests`。

---

## 4. 高性能工具集（选型指南）

这些工具**每一个都经过性能验证**（零分配 / 无锁 / 池化 / 硬件加速）。**优先用它们，禁止自己造**——手写的替代品几乎必然更差（多一次堆分配、多一把全局锁、没硬件加速、没池化）。

> **物理位置**：叶子积木（原生内存 / 异步同步原语 / 计算计时 / static helper）在 `Primitives/`（namespace `TC.Tier.Core.Primitives`）；容器型复合体（队列 / 缓存 / 池）在 `Collections/`（namespace `TC.Tier.Core.Collections`）。下文按**功能**分组讲选型，不按目录——选型时查 §4.1 表即可，定位时看类型所在的子节标注。

### 4.1 一页选型表（需求 → 用哪个 → 替代什么）

| 需求 | 用这个 | 替代的 BCL / 手写 |
|------|--------|-------------------|
| 异步等事件（多 waiter 广播） | `AsyncManualResetEvent` | `ManualResetEventSlim` / `SemaphoreSlim(1,1)` / 手写 TCS 广播 |
| 等 N 个并行子任务全完成 | `AsyncCountDown` | `CountdownEvent` + `Task.WhenAll`（每次堆分配） |
| 异步生产-消费队列 | `AsyncQueue` | `Channel` / `BlockingCollection` / `ConcurrentQueue+SemaphoreSlim` |
| **离散枚举**优先级队列（最快） | `BucketPriorityQueue<TItem,TEnum>` | `PriorityQueue<T,Enum>` + 锁 |
| **任意 long** 优先级队列 | `SkipListPriorityQueue` | `PriorityQueue<T,long>` + 全局锁 |
| 极高并发 **lock-free** 优先队列 | `AsyncPriorityQueue` | 手写 lock-free PQ（极易写错） |
| pinned / 对齐内存池 | `PinnedBufferPool` | `ArrayPool<byte>.Shared`（无 pinned/对齐） |
| 单块对齐原生内存（O_DIRECT/DMA） | `AlignedMemoryManager` | `NativeMemory.AlignedAlloc` + 手 pin |
| 短生命周期 bump 分配 | `NativeArena` | `stackalloc`（不能跨方法）/ `Marshal.AllocHGlobal` |
| 通用轻量对象池 | `OverflowPool` | 手写 `ConcurrentBag` 池 / `ObjectPool<T>` |
| 高并发 LRU 缓存 | `ClockCache` | `MemoryCache`（重）/ `ConcurrentDictionary`（无淘汰） |
| 微秒级零分配计时 | `MicroTimer` | `Stopwatch.StartNew`（毫秒粒度 + 易装箱） |
| 校验和 CRC32C/CRC64 | `UnifiedCrc` | `System.IO.Hashing.Crc32`（无硬件加速）/ 手写表 |
| 分片锁弱引用字典 | `ShardLockWeakReference` | `ConcurrentDictionary<K,WeakReference<V>>` |
| 池化 awaitable（基础设施） | `PooledValueTaskSource` | `TaskCompletionSource<bool>` |
| 热路径抛异常 | `ThrowHelper` | 内联 `throw new`（阻碍 JIT 内联） |
| 位运算/哈希/单调 CAS | `Utility` | `BitOperations` / 手写 CAS 循环 |

### 4.2 异步同步原语（共享 `PooledValueTaskSource` 底座）
- **`PooledValueTaskSource`**：池化的一次性 `IValueTaskSource`，thread-local 栈 + global 回退 + 批量搬运，常规 Rent/Return **零争用零堆分配**。是多 waiter 异步原语（下面的 Event/Queue/PriorityQueue）的共同底座。
- **`AsyncManualResetEvent`**：对标 `ManualResetEventSlim` 但暴露 `ValueTask` 等待 + 多 waiter 广播；已 set 时快速路径零分配。⚠️ 不能共享单一 `ManualResetValueTaskSourceCore`（单消费者），本类为每个 waiter 独立挂 source。
- **`AsyncCountDown`**：`Add()`/`Remove()` 计数到 0 唤醒；纯 `Interlocked` 无锁。
- **`AsyncQueue<T>`**：`ConcurrentQueue<T>` + 池化等待节点，队列非空时出队零分配。⚠️ **不是通用 Channel**——不支持 `Complete`/背压，适合"持续运行永不关闭"。

### 4.3 优先级队列（三个递进选型；完整指南 [`docs/priority-queues.md`](docs/priority-queues.md)）
- **`BucketPriorityQueue`**：N 个 `ConcurrentQueue` 桶，离散枚举优先级，无锁入队，最快。约束 `TPriority : struct, Enum`。⚠️ 严格优先级 → 高优先级非空时低优先级**饥饿**。
- **`SkipListPriorityQueue`**：任意 `long` 优先级，lazy skip-list + 细粒度 hand-over-hand SpinLock，`TryPeek` 完全不加锁，`[ThreadStatic]` xorshift PRNG。⚠️ SpinLock 不可重入。
- **`AsyncPriorityQueue`**：lock-free 跳表（Fomitchev–Ruppert marker 删除协议——直接引用 + marker 节点 + GC 回收，单引用 64 位 CAS）。⚠️ 多消费者最小序竞争性；`LightEpoch` 构造参数兼容保留（Route A 后不使用）。
- **选型**：枚举优先级 → `Bucket`；任意 long + 一般并发 → `SkipList`；极高并发 + 无锁 → `Async`。

### 4.4 内存与池化（⚠️ 非托管，必须 Dispose）
- **`PinnedBufferPool`**：旗舰内存池，纯数组索引分桶 + thread-local 栈热路径 + 批量搬运（与 `ArrayPool<T>` TLS 同构，多了 **pinned + 对齐 + 池身份校验**）。⚠️ size 向上取整到 2 的幂（最多 50% padding）；**别和 `ArrayPool.Shared` 混用**（归还非本池 buffer 会被静默忽略/Dispose）。
- **`AlignedMemoryManager`**：单块对齐原生内存（`NativeMemory.AlignedAlloc`，可选锁定物理内存防 swap），hot path `GetSpanUnsafe`/`GetRefUnsafe` 零校验。O_DIRECT / DirectIO / DMA 必备。⚠️ 无 finalizer，不 Dispose = 真泄漏。
- **`NativeArena`**：线性 bump 分配 + `Reset` 复用，O(1) 无碎片；适合批量/临时缓冲。⚠️ 只能前进分配（无单块 Free），非线程安全，有 finalizer 兜底但不保证时效。
- **`OverflowPool<T>`**：轻量固定容量对象池（`ConcurrentQueue`），满则调 disposer 丢弃，带 hits/misses/overflows 指标。⚠️ 容量是软约束（高并发下瞬时可能超几个）。
- **`NodeArena`**：变长节点的非托管驻留分配——4MB 块 CAS-bump（**并发 Alloc 安全**，无锁快路径）、块满建新块（lock 双检）、**指针恒稳**（块只增不减、永不搬移——上层缓存持有的裸指针跨块增长长命有效）；**无释放单语义**（块生命周期 = arena 生命周期，Dispose 全释放；并发竞争的重复分配 = 有界字节浪费，非泄漏）。单写者+并发读者契约，Dispose 由持有方 Resources 收口。与 `NativeArena` 分野：NativeArena=单线程线性 bump + `Reset` 复用（批量临时缓冲）；NodeArena=并发安全 + 只增不减 + 指针恒稳（长命驻留对象，如 SkipList 节点）。

### 4.5 缓存 / 计时 / 校验
- **`ClockCache<TKey,TValue>`**：CLOCK 近似 LRU，环形值类型数组 + 访问位 + 开放寻址，零分配热路径，无全局锁，命中率 90–95%。⚠️ capacity 必须 2 的幂；`TKey : struct, IEquatable` 且 `TValue : class`；tombstone 保探测链不断。
- **`MicroTimer`**：`readonly struct` 微秒计时，整数换算无浮点；`active=false` 时 JIT **自动消除整段计时逻辑**。⚠️ `ElapsedReadable()` 会分配字符串，热路径用 `TryFormat(Span<char>)`。
- **`UnifiedCrc`**：CRC32C（x86 走 `Sse42.X64.Crc32` 硬件加速 ~1GB/s，ARM 走 `Crc32`，否则软件表）+ CRC64（软件）。支持增量、零拷贝。⚠️ 硬件加速依赖平台；CRC64 实例非线程安全。

### 4.6 杂项
- **`ThrowHelper`**：`[DoesNotReturn]` + `NoInlining`，把 throw 隔离出热路径，让调用方法可被 JIT 内联（放在 `namespace System` 便于无 using 使用）。
- **`ShardLockWeakReference<TKey,TValue>`**：16 分片独立锁（位运算定位），Value 弱引用不阻止 GC。⚠️ shardCount 须 2 的幂；需定期 `CleanupDeadReferences()`。
- **`Utility`**：位运算/哈希通用工具（`PreviousPowerOf2`、`GetLogBase2` De Bruijn、`XorBytes` 8 字块、Knuth 哈希）、`ParseSize`/`PrettySize`、`MonotonicUpdate`（CAS 单调推进水位，拒回退）。

---

## 5. 互斥、回收与原子更新（SpinRWLock / FairGate / LightEpoch / Atomic128）

这四者**职责不同、不可互相替代**，是 Core 里**最易拼错的地方**（epoch 和锁尤其易混）。完整决策矩阵、标准范式、反模式见 [`docs/locking-and-epoch.md`](docs/locking-and-epoch.md)——本节只给速查。

| 需求 | 用 | 一句话 |
|------|----|--------|
| 对象读写互斥（读共享 vs 写/销毁排他，写不饿死） | `SpinRWLock` | 给对象挂一个 `SpinRWLock` 字段：读 `AcquireShared`，写/销毁 `AcquireExclusive`（写偏向） |
| 重试循环抢资源的到达顺序公平 | `FairGate` | fast path 查 `HasWaiters` 让位 + 慢路径 `TryAcquireSlow` + 释放后 `Wake`（§1.5） |
| 对象/内存延迟回收（RCU） | `LightEpoch` | drain action **只做轻量回收**，绝不塞阻塞 IO |
| >64 位载荷原子更新（指针+标志 / 水位+ABA） | `Atomic128<T>` | 16B 对齐背板 + CAS（`Interlocked` 做不到的 16B） |

**核心准则**：
1. **对象读写互斥 → `SpinRWLock`**。排他锁即"等所有共享读完成" = 安全点——**别再上 epoch**。
2. **延迟回收 → `LightEpoch`**（仅上层结构组件）。★ **不该进 epoch 的绝不塞 epoch drain**：阻塞 IO（PunchHole/fsync）、销毁排序都不行——该 IO 的投递 `BackgroundWorkerLoop` 或持锁直接做。drain 只回收、不 IO、不排序。
3. **大载荷原子更新 → `Atomic128<T>`**（16B 对齐背板，热路径用 Unsafe 快路径）。

---

## 6. Core 原语（Primitives）

> `SpinRWLock`/`FairGate`/`Atomic128`/`LightEpoch`/`SpinLockScope` 见 §5 与 [`docs/locking-and-epoch.md`](docs/locking-and-epoch.md)。本节讲 `IKeyComparer`/`KeyComparer`、`SectorAlignment`。

### 6.1 索引 key 比较器 `IKeyComparer<TKey>` / `KeyComparer<TKey>`
- **64 位 hash 是性能命脉**：高位取 tag（14 位，熵充分），低位取 bucket index，两段独立——避免 32 位 hash 的生日碰撞。
- `IKeyComparer<in TKey>`（`TKey : unmanaged`）：`GetHashCode64` / `Equals` / `Compare`（HashIndex 用 hash+判等；BTree/SkipList 用 Compare）。
- 默认实现 `KeyComparer<TKey>`：XxHash64 over TKey 字节（unmanaged/blittable，零装箱，8 字节 key ~3ns）+ `Comparer`/`EqualityComparer.Default`。它是 `Primitives/` 的通用 hash64 原语，同 `SpinRWLock`/`Atomic128` 同级——可注入自定义（变长 key 前缀哈希、特定分布优化等）。

### 6.2 对齐 `SectorAlignment` / `AlignmentConst`（**强制使用，禁止手写**）

- **铁律：所有对齐运算一律用 `SectorAlignment`，所有对齐常量一律用 `AlignmentConst`——禁止手写 `(... + align - 1) & ~(align - 1)` 之类位运算，禁止硬编码 `4096`/`512` 等对齐数值。** 内置已覆盖全部场景（`AlignUp`/`AlignDown` 多重载 + `Alignment4K`/`Alignment64B`/`Alignment2G`… 常量）；手写只会引入错位/魔术数。
- `SectorAlignment.AlignUp` / `AlignDown`：**入口已强制校验 `BitOperations.IsPow2`**（非 2 幂 / ≤0 抛 `ArgumentOutOfRangeException`——防 `alignment=0` 静默返回 0 等错位）。
- ⚠️ 两个 `long` 重载默认值不同（`(long,int)` 默认 4K，`(long,long)` 默认 2G），重载解析注意。

---

## 7. 反模式（我们踩过的坑——禁止重蹈）

### ❌ 反模式 1：`new Thread` 自建后台循环
- **症状**：自建 `new Thread` 后台循环；生命周期分散；`Dispose` 顺序各自管；进程退出不干净。
- **正解**：继承 `BackgroundWorkerLoop`，经 `ConfigureBackgroundWorker` 注册。

### ❌ 反模式 2：把阻塞 IO 塞进 epoch drain action
- **症状**：`PunchHole`（阻塞磁盘系统调用）、段提升（重操作）被注册为 `LightEpoch.BumpCurrentEpoch(action)` 或塞进 epoch drain worker 的队列。
- **为什么错**：LightEpoch 的 drain 是**协作**式——action 由"任意触发 drain 的线程"（可能是读线程）顺手执行。把毫秒级 IO 塞进去 = IO 落在读热路径，违背"纳秒级 epoch 不降吞吐"的设计；且"同步等 drain 完成"和 epoch 的异步协作本性冲突，会死结。
- **正解**：销毁 IO 持段排他锁直接执行（准则 3），或投递到 `BackgroundWorkerLoop`。

### ❌ 反模式 3：在未持 epoch 的线程调 `BumpCurrentEpoch`
- **症状**：线程没 `Resume`/`Acquire` 就调 `Epoch.BumpCurrentEpoch(action)`。
- **后果**：违反 LightEpoch 协议 → **Debug 立即抛 `InvalidOperationException`**（绊线已从 `Debug.Assert` 升级为立即抛，异常携带线程/entry/epoch 状态 + 32 次协议操作示波器）；Release 下 `ProtectAndDrain` 写入保留 entry0（静默腐败）。
- **正解**：`BumpCurrentEpoch` 的调用线程必须先 `Resume()`（= `Acquire + ProtectAndDrain`），用完 `Suspend()`（完整骨架见 [`docs/locking-and-epoch.md`](docs/locking-and-epoch.md) §2.3）。

### ❌ 反模式 4：互斥"双轨制"——同需求上 SpinRWLock + epoch 两套
- **症状**（现状）：普通 `Read` 用 `SpinRWLock` 段共享锁，`DirtyRead` 用 `LightEpoch`，于是销毁方为同时和两条读路径互斥，被迫段排他 lease **外加** epoch drain 两套都上。
- **为什么错**：互斥来源不单一；epoch drain 那套引出反模式 2/3。
- **正解方向**：存储读写互斥**统一用 SpinRWLock**（含 DirtyRead 改持段共享锁）；`LightEpoch` 退出存储路径，只留上层结构组件。

### ❌ 反模式 5：绕过 `LifecycleBase` 自管 Initialize/Dispose
- **症状**：子类 `new` 隐藏 `Initialize`/`Dispose`、自建 `_disposables`、自管 worker 启停。
- **正解**：只 override 钩子；资源进 `Resources`；worker 进 `ConfigureBackgroundWorker`。

### ❌ 反模式 6：绕过 Core/IO 自造文件访问 / 手写 IO 错误分类
- **症状**：直接 `File.*`/`FileStream` 做引擎文件 IO；或 catch 原生异常后自己 switch HResult 分类。
- **为什么错**：丢掉能力协商（DIO 对齐/稀疏/回退语义）与统一 `FileIOException` 分类；两处维护同一平台矩阵必然漂移；实测陷阱——在运行时已绑定 IOCP 的 OVERLAPPED 句柄上裸调 `LockFileEx` 等 overlapped 型 API 会向完成端口投递伪造结构导致**进程崩溃**（Core/IO 已根治）。
- **正解**：文件访问一律经 `TC.Tier.Core.IO`（`IFileSystem`/`IFileHandle`/`FileHandlePool`）；错误分类直接消费 `FileIOException.Error`；故障测试用 `FaultInjectingFileSystem`。

### ❌ 反模式 7：直接调 `IMetricsSink` / 绕过 `ObservabilityHub`
- **症状**：业务代码直接 `sink.Counter(...)`，或热路径每次构造 tag 数组。
- **正解**：经 `ObservabilityHub` 维度视图（`hub.Storage.OnRead` 等），享受构造期折叠的两级短路 + 采样；tag 用 `ObservabilityHub.Kv(k,v)` 零分配。

### ❌ 反模式 8：可观测热路径不短路
- **症状**：日志 `string.Format`/`$"..."` 插值后传参、追踪不判就 `BeginSpan`、指标不判视图 `IsEnabled` 就构造 tag 上报、**热路径** >3 参 params 日志不手动保护（调用点数组+装箱白付）。
- **正解**：日志用占位符重载（短路内建，**无需手写 IsEnabled**）；追踪先 `if (TracingEnabled)`；指标先 `if (hub.XxxView.IsEnabled)`。

### ❌ 反模式 9：以为 `ObservabilityHub` 管 Logging
- **症状**：在 `ObservabilityOptions` 里找日志级别开关，或把 `ILogger` 塞进 Hub。
- **正解**：Hub 只聚合 Metrics + Tracing；Logging 由上层独立注入 `ILoggerFactory`，级别由日志实现自决。

### ❌ 反模式 10：自己造 utility 轮子
- **症状**：`new ConcurrentQueue` 当优先队列、手写 object pool、手写 CRC 表、`Stopwatch` 毫秒计时、`TaskCompletionSource` 做广播。
- **正解**：先查 §4.1 选型表——`Primitives/`+`Collections/` 的工具全部性能验证过（零分配 / 无锁 / 池化 / 硬件加速），手写替代品几乎必然更差。内存类（`AlignedMemoryManager`/`NativeArena`/`PinnedBufferPool.RentAligned`）非托管，必须 Dispose。

### ❌ 反模式 11：SpinRWLock **排他**临界区内 await
- **症状**：持**排他**锁时调 `await`——线程切换后 `ReleaseExclusive` 线程 ID 不匹配，锁永久泄漏（Debug 释放线程校验会抓）。
- **根因**：排他持有是线程关联的同步原语，不支持异步上下文跨越。
- **正解**：**排他**临界区内绝对禁止 await；异步互斥用 `AsyncManualResetEvent` 等异步原语（见 [`docs/async-primitives.md`](docs/async-primitives.md)）。共享锁可长持/跨 await（计数语义无线程亲和——读计划锁持共享做 IO 即此用法），但持得越久写者与被挡读者等待越久，能锁外做的移到锁外。

### ❌ 反模式 12：恢复过程中直接修改运行时状态
- **症状**：`OnRecoveryCoreAsync` 中直接修改引擎共享字段、挂接数据结构——恢复失败回滚时状态无法复原 → 对象半可用。
- **正解**：恢复中间结果存本地上下文，恢复成功后在 `OnInitializeComplete` 原子提交到运行时状态；失败则本地上下文丢弃，无副作用（见 [`docs/lifecycle.md`](docs/lifecycle.md)）。

### ❌ 反模式 13：跨组件直接调用 NativeInterop
- **症状**：上层结构组件 / 业务直接调 `DiskNative`/`FileNative`——绕开 IO 引擎的生命周期、错误处理、指标埋点。
- **正解**：原生 syscall 仅 Core.IO 调用（NativeInterop 是其实现底座）；Core 内组件经 `TC.Tier.Core.IO`，外部业务再经 `IStorageEngine` 抽象（见 [`docs/native-interop.md`](docs/native-interop.md) §0 映射表）。

### ❌ 反模式 14：对齐计算使用硬编码常量
- **症状**：代码出现 `(size + 4095) & ~4095` 手写对齐，或硬编码 `4096`/`512`。
- **根因**：不同平台扇区大小不同，硬编码跨平台错位；手写位运算易出符号位/溢出问题。
- **正解**：所有对齐运算统一走 `SectorAlignment`，所有对齐常量统一用 `AlignmentConst`——禁止任何手写实现（见 §6.2）。

---

## 8. 决策树

```
我要做后台循环/队列消费？
  → 继承 BackgroundWorkerLoop( + ConfigureBackgroundWorker)。绝不 new Thread。

我有资源要随实例释放？
  → Resources.Add(...)。绝不自建 _disposables。

我要写一个有"启动恢复"的对象？
  → 继承 LifecycleBase + 写一个 RecoveryBase 派生（CreateRecovery 返回它），
    只 override OnRecoveryCoreAsync。

我要做任意对象的读写互斥（段 / 区 / 任意协调对象）？
  → 给它挂一个 SpinRWLock 字段：读 AcquireShared，写/销毁 AcquireExclusive（写偏向通用原语，写不饿死）。

我要让多写者重试抢同一资源不插队（到达顺序公平）？
  → Core.FairGate：fast path 查 HasWaiters 让位 + TryAcquireSlow + 释放后 Wake（AcquireExtent 同形）。

我要做"对象/内存延迟回收"（RCU）？
  → LightEpoch：drain action 只做轻量回收，绝不塞阻塞 IO。

我要"等读完成再销毁段/打洞"？
  → 持段 AcquireExclusive（=等所有共享读退出），然后直接执行物理动作。
    不要用 epoch drain 排序销毁。

我要记日志？
  → ILogger（上层注入 factory）+ LogXxx 占位符重载（短路内建，无需手写 IsEnabled）。
    热路径 >3 参才手动保护。不经 ObservabilityHub。

我要记指标 / 追踪？
  → ObservabilityHub 维度视图（hub.Storage.OnRead 等）+ 先判 IsEnabled / TracingEnabled。

我要内存缓冲 / 池化？
  → PinnedBufferPool（pinned+对齐旗舰池）/ AlignedMemoryManager（单块对齐，O_DIRECT）/ NativeArena（bump，临时）。⚠️ 非托管必须 Dispose。

我要优先队列？
  → 枚举优先级 BucketPriorityQueue / 任意 long SkipListPriorityQueue / 极高并发 AsyncPriorityQueue（注入 epoch）。

我要缓存 / 计时 / CRC / 异步信号？
  → ClockCache（TKey 值类型, TValue 引用类型）/ MicroTimer（零分配微秒）/ UnifiedCrc（硬件加速）/ AsyncManualResetEvent·AsyncQueue。

我要等实例恢复完成（阻塞）？
  → 异步用 WaitForReadyAsync（⚠️ 禁在 UI/ASP.NET 同步上下文调 WaitForReady，死锁）。

我要判 LogicalAddress 空值 / 算跨段距离 / 存储读上界（三水位）？
  → 契约红线，见 [`../TC.Tier.Contracts/COORDINATION.md`](../TC.Tier.Contracts/COORDINATION.md) §5（`Empty`≠`Invalid` / `IsValid` / `GetDistance` / `CommittedTail`）。

我要判断实例是否就绪（非阻塞）？
  → IsReady / RecoveryState（绝不靠 "Recovery 非 null" 判断）。
```

