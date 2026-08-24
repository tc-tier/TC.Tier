# 同步-异步桥接（AsyncOperation / SyncAsyncBridge / 分层等待策略）

> 状态：**P0/P1/P2 已交付（2026-08-18，本仓库 Core 层）**——P3（生命周期链，需 Contracts 变更）与 Runtime 层接入
> 留待下一阶段统一实施（用户决策：先只改 Core 层）。
> 定位：**Core 级公共协调组件**——把"同步线程原地阻塞等异步操作"统一改造为
> 「**异步信号 + 可轮询状态句柄 + 分层等待 + 独立执行池**」的通用基建。
> 关联：[`async-primitives.md`](async-primitives.md)（AsyncManualResetEvent 等底座）｜
> [`dedicated-task-scheduler.md`](dedicated-task-scheduler.md)（独立池）｜[`lifecycle.md`](lifecycle.md)（生命周期链）｜[`io.md`](io.md)（远程介质同步 API）。

---

## 0. 一句话结论

**要做，但不是三件全新的事，而是"一件新抽象 + 两处复用"：**

1. **新抽象**：`AsyncOperation`——发起即返回的状态句柄（可轮询 / 可异步等 / 可同步兜底等），把散落各处的 `Task` + `.Wait()` / `GetAwaiter().GetResult()` 收敛成一个有状态机契约、有诊断、有超时纪律的类型；
2. **复用一**：同步等待策略 = `AsyncManualResetEvent.Wait(timeout)` 已实现的「自旋 → park 分片」分层等待（**不是**裸 `Thread.Yield()` 循环，见 §5 的修正理由）；
3. **复用二**：独立池 = `IsolatedTaskScheduler`（桥默认持有一个进程级 well-known 实例），**不重造线程池**。

---

## 1. 背景：现状的三条 sync-over-async 链

调研（2026-08-18，src/ 全量）结论——真正的同步等待热点集中在三条链：

| 链 | 热点（文件:行） | 现状 |
|----|----------------|------|
| **① IO 同步 API 桥** | `IO/IObjectStore.cs:214-251`（9 个同步便捷包装）、`IO/Remote/RemoteFileHandle.cs:113,135,360,401,500,524`（同步 IFileHandle over 异步 staging）、`RemoteFileHandle.cs:574-605`（multipart：`throttle.Wait()` + `Task.WhenAll(...).GetAwaiter().GetResult()`）、`IO/Remote/RemoteFileSystem.cs:121-211` + `:298-373`（fencing 锁同步 Head/Get/Put + `Thread.Sleep(15)` 轮询） | 调用线程（可能是公共池线程）原地阻塞等异步对象存储操作 |
| **② Runtime 屏障** | `LogBase.Write.cs:204,224,296,490`（同步 Flush 等 `_inFlightFlush`）、`BlobBase.WriteSession.cs:261`、`CompactorBase.cs:182`（现 `DefaultCompactor.cs`）、`EntryLog.cs:333`（**名义异步实为锁内 `Monitor.Wait(10)`**） | 同步 flush/compact 轨阻塞等异步页落盘 |
| **③ 生命周期链** | `Shared/LifecycleBase.cs:145,155`（`WaitForReady` → `_recoverTask.Wait()`）、`:247`（重试守卫 `priorTask.Wait()`）、`:449`（Dispose 等 recover task）；`StorageEngineFactory.cs:50,86`（`engine.WaitForReady()` 兜底）；结构层 `BlobBase.cs:148` 等 5 处 `_engine.WaitForReady()` | 同步等待后台恢复 task |

**（本提案的直接动因）**：引擎恢复实质同步、`Task.Factory.StartNew(LongRunning)` 调度延迟，
导致 `Initialize` 已返回而 `RecoveryState` 停在 `NotStarted`——`WaitGuardPreCheck`
（`LifecycleBase.cs:179-188`）抛"恢复任务尚未启动"。根因归类为
**「状态转移依赖了被调度」：发起线程返回时，连'已受理'这个事实都观测不到。**

---

## 2. 目标 / 非目标

**目标：**

1. 提供通用操作句柄 `AsyncOperation`：发起 → 立即返回句柄；三种消费模式（轮询 / 异步等 / 同步兜底等）；
2. 同步等待一律走**分层策略**（自旋 → park 分片），全程**有界**（强制超时）；
3. "写路径必须同步转异步"的场景（IO 同步 API 桥）由**桥专用独立池**承载，异步工作的推进**不依赖公共池可用性**；
4. 状态机**可见性原则**：发起线程在返回前同步完成"受理"状态转移（消灭 NotStarted 窗口）；
5. 全链路诊断：等待计数、等待时长、超时现场（对齐 SpinRWLock 值示波器哲学）。

**非目标（明确不做）：**

- ❌ 不重造线程池 / 信号量 / MRES——复用 `IsolatedTaskScheduler` + `AsyncManualResetEvent`；
- ❌ 不改造内存序屏障类等待（SpinRWLock / epoch drain / freeze / 段表双尾水位，见 §9 不改清单）；
- ❌ 不消灭 Dispose 终局 join——Dispose 需要确定性结束，只加超时与诊断；
- ❌ 不做"任务编排框架"（DAG / 重试 / 级联取消）——那是 RecoveryBase / worker-loop 的领地。

---

## 3. 总体设计（三层）

```
┌────────────────────────────────────────────────────────────┐
│ 消费层（调用方）                                             │
│   轮询 op.Status / IsCompleted        —— 多消费者安全        │
│   await op.WaitAsync(ct)              —— 异步一等公民        │
│   op.Wait(timeoutMs)                  —— 同步兜底（有界）    │
├────────────────────────────────────────────────────────────┤
│ 句柄层  AsyncOperation（Core.Primitives，新）                 │
│   状态机: Running → Succeeded | Failed | Canceled（CAS 单次）│
│   内嵌 AsyncManualResetEvent（复用）作完成信号                │
│   异常槽 + 诊断环形缓冲（#if DEBUG）                          │
├────────────────────────────────────────────────────────────┤
│ 桥接层  SyncAsyncBridge（Core.Shared，新）                    │
│   Start(work) → 独立池执行 work → 完成/异常/取消 → ReportX   │
│   独立池 = IsolatedTaskScheduler（进程级 well-known 实例）    │
│   再入防护（AsyncLocal 深度计数）+ 强制有界默认超时            │
└────────────────────────────────────────────────────────────┘
```

三层职责一句话：**桥负责"去哪跑"，句柄负责"怎么样了"，等待策略负责"怎么等"。**

---

## 4. AsyncOperation 契约（句柄层）

> ★ **通用后台操作原语（2026-08-24 升级）**：AsyncOperation 从"同步桥专用"升级为全库后台操作句柄基座——
> Storage 层 IReclaimOperation/ICompactOperation 两套同构句柄合一为 **Contracts.IAsyncOperation /
> IAsyncOperation{TResult}**（依赖方向：Contracts 定义接口、Core 实现；公开 API 返回接口，
> 运行时对象即 AsyncOperation 实例），Reclaim/Compact 后台操作统一 `new AsyncOperation(...)` + `Report*`，
> 不再手写 TCS/事件/时序包装。新增契约：**事件**（Progress/Failed + 成功钩子 OnSucceeded）、
> **取消**（Cancel + CancellationToken，构造可链接外部取消——引擎 Dispose 自动取消在途操作）、
> **进度**（ReportProgress）、**结果**（泛型变体 AsyncOperation{TResult}）。
> ★ **接口消费面 = 类消费面全集**（2026-08-24 补全）：IAsyncOperation 含轮询
> （Status/IsCompleted/Exception——AsyncOperationStatus 枚举迁入 Contracts.Storage）、
> 异步等（WaitAsync）、同步兜底等（Wait(int, ct)）、终态取异常（ThrowIfFailed）——调用方只看接口，
> 无需查类 API。完成侧（Report*）不进接口（实现类成员）。

### 4.1 API

```csharp
namespace TC.Tier.Core.Primitives;   // ★ 枚举 AsyncOperationStatus 在 TC.Tier.Contracts.Storage（2026-08-24 迁入）

public enum AsyncOperationStatus
{
    Running,     // 已受理并在途（★ 发起线程返回前同步置位——可见性原则，见 §7）
    Succeeded,
    Failed,
    Canceled,
}

public abstract class AsyncOperationBase : IAsyncOperation   // 机制基座（事件/取消/进度/状态机）
{
    // === 状态查询（多消费者安全；全部 volatile / Interlocked 实现）===
    public AsyncOperationStatus Status { get; }
    public bool IsCompleted { get; }              // 终态短路（Succeeded/Failed/Canceled）
    public Exception? Exception { get; }          // Failed/Canceled 的异常（观察不重抛）

    // === 事件（后台操作通用契约）===
    public event EventHandler<double>? Progress;       // 进度（ReportProgress 触发，订阅者异常隔离）
    public event EventHandler<Exception>? Failed;     // 失败/取消终态（取消回滚完成 = Failed 语义）
    public void ReportProgress(double progress);

    // === 取消（后台操作通用契约）===
    public CancellationToken CancellationToken { get; }  // 操作取消令牌（worker 检查点响应）
    public void Cancel();                                // 触发取消（幂等；构造可链接外部取消）

    // === 完成侧（CAS 单次生效，后续 Report 为 no-op）===
    public virtual void ReportSucceeded();
    public void ReportFailed(Exception ex);
    public void ReportCanceled(OperationCanceledException oce);

    // === 消费侧：三种模式 ===
    // ① 异步等（一等公民——零分配，委托内嵌 AsyncManualResetEvent.WaitAsync）
    public ValueTask WaitAsync(CancellationToken ct = default);

    // ② 同步兜底等：分层等待（§5），全程有界
    //    完成（含失败）→ true / 抛失败异常；超时 → false；取消 → OCE
    public bool Wait(int timeoutMs, CancellationToken ct = default);

    // ③ 轮询方终态取异常（IsCompleted 后调；Failed/Canceled 重抛，Succeeded no-op）
    public void ThrowIfFailed();
}

// 无结果变体（原 AsyncOperation——API 全保留，SyncAsyncBridge 消费方零回归）
public sealed class AsyncOperation : AsyncOperationBase
{
    public AsyncOperation(string? name = null, ILogger? logger = null,
        CancellationToken externalCancellation = default);
}

// 有结果变体（仿 Task{T}，如 CompactResult；实现 IAsyncOperation{TResult}）
public sealed class AsyncOperation<TResult> : AsyncOperationBase
{
    public event EventHandler<TResult>? Completed;     // 成功事件（携带结果，先于 WaitAsync 唤醒）
    public void ReportSucceeded(TResult result);       // ★ 必须带结果（无参版 override 防御）
    public TResult Result { get; }                     // 终态后读（失败/取消重抛）
    public new ValueTask<TResult> WaitAsync(CancellationToken ct = default);   // 等完成拿结果
}
```

**★ 事件时序契约（Storage 历史 flaky 根因区收口，L2/L22）**：终态事件（Failed / 泛型 Completed）
在完成信号置位（AsyncManualResetEvent.Set）**之前**同步触发（订阅者异常隔离）——`WaitAsync`
等待者苏醒时事件必已投递；订阅竞态经 `IsCompleted` 兜底（先订阅后查，true = 完成早于订阅）。

**★ 命名纪律**：`Async` 后缀只留给可 await 的方法（返回 Task/ValueTask）；
启动后台操作并返回句柄的方法用 Start 动词（`StartReclaim`/`StartCompact`），等待走句柄的
`WaitAsync`。

### 4.2 状态机语义表

| 转移 | 执行者 | 时机 | 契约 |
|------|--------|------|------|
| → `Running` | **发起线程** | 句柄对外可见**之前**（同步） | 可见性原则（§7）：调用方拿到句柄时状态必为 Running，绝无"句柄已到手但状态未知"窗口 |
| → `Succeeded`/`Failed`/`Canceled` | 完成侧（桥 worker / 手工） | work 结束 | **CAS 单次**：首个终态生效；后续 Report 幂等 no-op（防御重复完成） |
| 终态 | — | — | **不可逆**。需要重试 = 新建操作，不改状态机（对齐 RecoveryBase"Failed 实例销毁重建"哲学） |

### 4.3 实现要点

- 内嵌一个 `AsyncManualResetEvent`（默认构造，Set 调用点不持锁 → 也可选内联模式）作完成信号；
  `WaitAsync` 直通 `event.WaitAsync`，`Wait(timeout)` 直通 `event.Wait(timeout)`——**等待机制零新代码**；
- 状态用 `int` + `Interlocked.CompareExchange`（状态枚举压 int），`Exception` 槽 volatile 写；
- **池化预留**：对齐 `PooledValueTaskSource` 的归还协议设计，但首版用普通 sealed class（池化是
  #PERF-002 级优化，等基准证明需要再做——避免过早设计）；
- `#if DEBUG`：状态转移环形记录（最近 16 次：时间戳 + 线程 id + 转移方向 + 等待者计数），
  超时 / 异常自动携带历史（对齐 SpinRWLock 值示波器）。

---

## 5. 分层等待策略（对"短周期自旋+让步"的修正）

### 5.1 为什么**不是**裸 `Thread.Yield()` 循环

提案原稿设想"必要时短周期自旋 + 让步（Thread.Yield）而不是直接 .Wait()"。**方向对，形态要修正：**

| 方案 | 问题 |
|------|------|
| 无界 `while (!done) Thread.Yield();` | ① 等待 >1ms 时纯烧 CPU（每次 Yield 一个完整调度周期）；② **Yield 不保证完成者获得 CPU**——单核 / 超订 / 完成者需要池线程而池被同步等待者占满时，Yield 循环和 `.Wait()` 一样死锁，还多烧一个核；③ 无超时 = 无界楔死，违反有界纪律 |
| `SpinWait.SpinOnce()` 循环 | ✅ 自带 CPU 自旋 → **自动升级 Yield** → 偶发 `Sleep(1)` 的完整阶梯；`NextSpinWillYield` 给出明确的阶段切换点 |
| park（`Monitor.Wait` 分片） | ✅ 真正让出核；分片自醒兜底丢脉冲 |

**结论**：同步兜底等待 = **`SpinWait` 自旋段（微秒级）→ park 分片（50ms 自醒 + 完成侧 Pulse）→ 全程超时上限**。
这正是 `AsyncManualResetEvent.Wait(int, CancellationToken)`（`Primitives/AsyncManualResetEvent.cs:232-268`）
已实现并验收过的轨道——**直接复用，不新写**。

### 5.2 与 `.Wait()` / `GetAwaiter().GetResult()` 的本质区别

同步桥真正的死锁根源不是 `Task.Wait()` 这个调用形式，而是三个结构性条件（本设计逐一拆解）：

| 死锁条件 | 本设计的拆法 |
|----------|--------------|
| (a) 等待者占公共池线程，完成者也需要公共池 → 池饿死互等 | 异步工作跑**独立池**（IsolatedTaskScheduler，continuation 回流私有线程），推进不依赖公共池（§6） |
| (b) UI/ASP.NET 同步上下文，continuation 要回流等待线程 | 等待者 park 在事件上（无 continuation）；完成侧 Set 默认 `runContinuationsAsynchronously: true`，不内联 |
| (c) 等待者持有完成者需要的锁 | 契约禁止（§8 铁律 3）+ 独立池让"锁的持有者"与"完成者"大概率不同线程池，冲突更早暴露为超时而非静默死锁 |

另有两点工程收益：**事件等待不依赖 Task 调度器内部行为**（无完成边缘内联续体的语义模糊）；
**多消费者可轮询**（Task 是单消费者模型，`IsCompleted` 轮询 + 二次 `Wait` 会消耗结果）。

---

## 6. SyncAsyncBridge（桥接层）

### 6.1 API 与执行模型

```csharp
namespace TC.Tier.Core.Shared;

public static class SyncAsyncBridge
{
    /// <summary>在桥独立池上发起异步工作，立即返回状态句柄。
    /// ★ 句柄返回时状态已同步置 Running（可见性原则）。</summary>
    public static AsyncOperation Start(
        Func<CancellationToken, ValueTask> work,
        SyncBridgeOptions? options = null,
        CancellationToken ct = default);
}

public sealed class SyncBridgeOptions
{
    public string Name { get; init; } = "sync-bridge";  // 诊断名（对齐调度器命名纪律）
    public TaskScheduler? Scheduler { get; init; }      // null = 默认桥池；可注入 own 实例
    public int DefaultTimeoutMs { get; init; } = 15_000; // 同步 Wait 默认上限（★ 强制有界）
}
```

执行模型（时序）：

```
同步调用线程                          桥独立池线程（IsolatedTaskScheduler）
────────────                         ──────────────────────────────
op = new AsyncOperation()            （Start 内）
op → Running（同步 CAS）
StartNew(Wrapper, scheduler)   ───►  await work(ct)   ←—— continuation 回流池私有线程
return op     ←—— 立即              │ 成功 → op.ReportSucceeded() → event.Set()
                                     │ OCE   → op.ReportCanceled(oce)
                                     │ 异常  → op.ReportFailed(ex)
...调用方继续干别的（轮询/其他逻辑）...
op.Wait(timeout)   ←—— 最后时刻才等（park，事件唤醒）
```

**关键点：work 是协作式异步契约**（`await` 让出、不阻塞）。work 内部禁止同步阻塞——独立池的
M 很小（§6.2），阻塞任务直接饿死同池操作（对齐 dedicated-task-scheduler.md §7.2）。
真正无法异步化的同步重 IO，由调用方注入 own 单线程实例（`Scheduler = IsolatedTaskScheduler.Create(ThreadCount=1, ...)`）。

### 6.2 默认桥池选型

| 决策 | 值 | 理由 |
|------|----|------|
| 池类型 | `IsolatedTaskScheduler.Create(...)`，进程级 well-known 单例（`SyncAsyncBridge.DefaultScheduler`，惰性创建，不 Dispose——进程意图资源，对齐 `Shared`） | 桥是"写必须同步转异步"的统一出口，值得一个专属分区；不占用 `Shared`（那是高频关键 worker 的） |
| M（线程数） | `Clamp(ProcessorCount, 2, 4)` | work 是协作式异步，少量线程即可高并发；对齐 `Shared` 默认 |
| Watchdog | 开（`TaskTimeout` 下调至 5s） | 桥操作应有界；慢任务告警是超时诊断的先导信号 |
| 防扩散 | 桥自身 1 个实例 + 调用方注入受 `Create` 现有护栏（>4 实例 WARN）管控 | 不新增护栏机制 |

### 6.3 再入防护（自死锁防御）

**死锁场景**：work（跑在桥池的 M 个线程之一）内部又调了同步 API → 该同步 API 再走桥 →
`Start` 把新 work 入队 → 新 work 需要空闲桥池线程 → M 个线程全在 park 等待嵌套 work → **池自锁**。

防护：`AsyncLocal<int> _bridgeDepth`——Wrapper 执行 work 前置 1（随 await 流动），`Start` 时检测：

```
_bridgeDepth > 0 且 Scheduler == 同一池（默认池或同实例）
  → throw InvalidOperationException("桥工作体内禁止再经同一桥同步等待——注入独立 Scheduler 或改异步")
```

显式注入**不同的** Scheduler（分池）即豁免——这是嵌套场景的正解（对齐"compact 同步 worker 与异步建段分池"）。

### 6.4 诊断

- **指标**（挂 Hub 才发，默认零开销——对齐调度器哲学）：`bridge.op.started` / `bridge.op.completed` /
  `bridge.wait.parked`（同步等待进入 park 的次数）/ `bridge.wait.timeout` / `bridge.op.duration_us`；
- **日志**：超时 WARN（含 op 名、年龄、状态、当前等待者数 + DEBUG 环形历史）；
- **泄漏绊线**：终态为 Failed/Canceled 且从未被任何消费模式观察的操作，finalizer 告警
  （对齐 LifecycleBase 终结器泄漏探测）。

---

## 7. 状态机可见性原则

> **发起线程在把句柄/对象交给调用方之前，必须同步完成"受理"状态转移；完成状态归完成侧线程。**

这是 NotStarted 问题（§1）的根因修法，也是 `AsyncOperation → Running` 同步置位的依据。同样适用
于生命周期链（P3 落地项）：

**现状**：`LifecycleBase.Initialize`（`Shared/LifecycleBase.cs:278-307`）调度后台 task 后立即返回，
`NotStarted → Recovering` 的转移发生在**后台 task 真正开跑** `OnRecoveryStart` 时——调度延迟期间
调用方观测到 `NotStarted`，`WaitGuardPreCheck` 只能靠抛异常防御（`:179-188`）。

**修法（需 Contracts 小扩展）**：`IRecovery` 增加 `MarkScheduled()`（或等价入口），
`Initialize` 在 `Task.Factory.StartNew` **之前**在调用线程同步调用（`RecoveryBase` 实现为
CAS `NotStarted → Recovering`）。效果：`Initialize` 返回时状态必为 `Recovering` 或更晚，
"恢复任务尚未启动"窗口从根上消灭；`WaitGuardPreCheck` 保留为防御性绊线（不变量验证）。

---

## 8. 铁律（踩过的坑——禁止重蹈）

1. ❌ **同步等待必须有界**——所有 `Wait(timeout)` 强制超时；发现无界 `.Wait()` / `GetAwaiter().GetResult()`
   新增点 = review 阻断项。超时不是失败，是**诊断入口**（带现场 WARN）。
2. ❌ **禁止在持锁期间同步等桥操作**——等待者持锁 + 完成者需要该锁 = 必死。桥不做锁检测（成本高），
   靠契约 + 违例时快速超时暴露。
3. ❌ **桥 work 内禁止同步阻塞 / 再入同桥**——work 是协作式异步契约；再入防护自动抛（§6.3），
   但"work 内 `Thread.Sleep` / 同步重 IO"只能靠 review 与 watchdog 慢任务告警。
4. ❌ **禁止把屏障等待改造成桥**——内存序屏障（SpinRWLock / epoch / freeze / 段表水位）等待的是
   微秒级内存不变量，完成者就在本进程临界区生态里；跨线程桥接 = 正确性灾难 + 延迟数量级恶化。
   它们的自旋/yield 现状就是正解。
5. ✅ **先消除、再桥接**——高频热点（如 `S3ObjectStore.cs:353` 每请求签名路径的凭据
   `GetAwaiter().GetResult()`）的正解是**消除同步等待**（凭据缓存 / 改真异步签名），不是给坏结构加桥。
6. ✅ **发起即受理**——新写的"启动后台操作"API，返回前必须同步置 Running（§7）；
   状态查询多消费者安全，控制流只认终态（对齐 lifecycle.md §4）。
7. ✅ **测试与源 1:1 + 契约测试**（见 §10）——本组件属 Core 并发原语，无契约测试不许合入。

---

## 9. 落地映射（分批，一点一点来）

> 铁律对齐：每阶段独立 commit + 全绿再进下一阶段；行为变更项（P2/P3）须带回归测试。

| 阶段 | 范围 | 改造 | 模式 | 状态 |
|------|------|------|------|------|
| **P0** | 新增 `Primitives/AsyncOperation.cs` + `Shared/SyncAsyncBridge.cs` + 契约测试（§10） | 新基建 | — | ✅ 2026-08-18（另增 `Run`/`Run&lt;T&gt;` 便捷入口——Start+有界 Wait+失败重抛，同步包装改造的一行式形态） |
| **P1** | `IO/IObjectStore.cs:214-251` 9 个同步包装 | 内部改走桥（静态共享 `SyncBridgeOptions` 免每调分配），对外签名与行为不变 | 桥接 | ✅ 2026-08-18（Copy 族 60s 预算） |
| **P2** | `RemoteFileHandle` 同步族（Write/Read/PunchHole/CopyRange 快路径/Flush）+ `RemoteFileSystem` 同步族（Open/Exists/Delete/Move/Enumerate）+ fencing 三处（TryAcquireOrTakeover/Heartbeat/ReleaseLease） | 逐调用桥接；**`FlushAsync` 真异步化**（lock→SemaphoreSlim 异步门、multipart 全 await——异步调用方不再被伪异步阻塞，此为超出原稿的必要改造：桥 work 契约禁止内部阻塞）；fencing 锁不变量：桥完成者（对象层 IO）绝不触碰 `_lockGate`（`Thread.Sleep(15)` 重试退避保留——受调用方 deadline 有界） | 桥接 + 状态句柄 | ✅ 2026-08-18（Flush 600s / Copy 60s 预算；验证：Core 1209 + Runtime 501 + S3 契约 30 全绿） |
| **P3** | 生命周期链：`LifecycleBase.WaitForReady` / `priorTask.Wait()`（`:145,155,247`）内部改 tiered 等待；`IRecovery.MarkScheduled()` + `Initialize` 状态同步置位（§7）；`EntryLog.WaitForCommitAsync` 名义异步问题（`:333`）单独评审 | 状态可见性修法 | 状态句柄 | ⏳ 待下一阶段（需 Contracts 变更，与 Runtime 接入同期） |
| **不改** | 内存序屏障（SpinRWLock/epoch/freeze/段表）；Dispose 终局 join（只补超时诊断）；`S3ObjectStore` 凭据热点（走"消除"路线：凭据缓存） | — | 见 §8.4/8.5 | — |

交付备注：
- 桥级指标（§6.4 `bridge.*` 系列）首版未实施——依赖 ObservabilityHub 接线，属后续增强；
  watchdog 慢任务告警（TaskTimeout=5s）已随默认池生效。
- 遗留 flaky 修复顺带交付：`BackgroundWorkerLoopTests.CycleExceptionIsolation` 固定 `Delay(200)` 改轮询对齐（delay 型同步在并行套压池时假失败，实测踩中一次）。

---

## 10. 测试与验收（契约测试矩阵）

> 铁律对齐：测试与源 1:1（`src/X/Y.cs` ↔ `tests/X/YTests.cs`）；并发原语必须有契约测试，禁止 SKIP。

**`tests/TC.Tier.Core.Tests/Primitives/AsyncOperationTests.cs`**

| 类别 | 用例要点 |
|------|----------|
| 计数/状态语义 | 终态 CAS 单次生效；重复 Report 幂等 no-op；异常槽观察不重抛；`ThrowIfFailed` 三分支 |
| 唤醒协议 | N waiter 广播全醒；完成先于等待零丢失；取消传播（等待中 ct 触发 → OCE）；超时边界（deadline 前完成 → true） |
| 配对绊线 | Failed/Canceled 且无观察 → finalizer 告警（泄漏可见） |

**`tests/TC.Tier.Core.Tests/Shared/SyncAsyncBridgeTests.cs`**

| 类别 | 用例要点 |
|------|----------|
| 基本功能 | 成功/失败/取消三轨；`Start` 返回时 `Status == Running`（★ 可见性原则的机器验证） |
| 再入防护 | work 内嵌套 `Start`+`Wait`（同池）→ 必抛 InvalidOperationException；注入独立 Scheduler → 通过 |
| 池饿死回归 | **专项**：打满公共池（N 个 `Task.Run(() => block)`）后桥操作仍在超时内完成——独立池价值的验收测试 |
| 超时纪律 | 全部同步等待有界；超时返回 false + WARN 现场含 op 名/年龄/状态 |
| 并发压测 | M 线程 × K 操作并发 Start/Wait，无丢失唤醒、无状态机错乱（计数器校验） |

**性能基线**（`benchmarks/TC.Tier.Core.Benchmarks/`，对齐既有基准文化）：
桥派发税目标——完成即等（无 park）路径 ≤ 2µs（IsolatedTaskScheduler 派发 0.5-1µs + 句柄开销）；
`WaitAsync` 已完成快路径 ≤ 100ns。超基线回写 perf 文档。

---

## 11. 备选方案与否决理由

| 备选 | 否决理由 |
|------|----------|
| 全部改 `Task` + `WaitAsync`，不做句柄 | 轮询（多消费者状态查询）与"Start 早、Wait 晚"的延迟等待模式无法用单消费者 Task 表达；且裸 Task 无状态机契约与诊断纪律 |
| 裸 `Thread.Yield()` 循环替代 `.Wait()` | 见 §5.1——无界、不保证完成者获 CPU、无超时；是"看起来异步友好"的反模式 |
| 桥等待用 `Task.Wait(timeout)`（等 Task 而非事件） | 保留对公共池 continuation 的依赖（work 的 await 后续仍可能排公共池——除非 work 全程 `ConfigureAwait(false)` 且 IO 真异步）；事件等待 + 独立池执行把依赖面收到最小 |
| 每调用方自建独立池 | 违反防扩散护栏；well-known 桥池 + 显式注入是受控中间态 |
| 状态机加 `Pending` 态 | 发起即受理（§7）后 Pending 不可观测——不可观测的状态是复杂度，砍 |

---

## 关联文档

- 异步原语底座：[`async-primitives.md`](async-primitives.md)
- 独立池使用与陷阱：[`dedicated-task-scheduler.md`](dedicated-task-scheduler.md)
- 生命周期编排：[`lifecycle.md`](lifecycle.md) ｜ 远程介质同步 API：[`io.md`](io.md)
- 覆盖矩阵：[`unit-test-coverage.md`](unit-test-coverage.md)
