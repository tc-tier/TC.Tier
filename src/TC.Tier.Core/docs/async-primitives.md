# 异步同步原语与队列（AsyncManualResetEvent / AsyncCountDown / AsyncQueue）

> Task-based 异步同步原语 + 异步队列的用法。它们共享 `PooledValueTaskSource` 池化底座（零/低分配等待）。
> 完整积木全景见 [`../COORDINATION.md`](../COORDINATION.md)；原生内存与池见 [`memory.md`](memory.md)；
> 实测数字见 [`perf/core-primitives-perf.md`](perf/core-primitives-perf.md)。
> **优先队列族**（`BucketPriorityQueue`/`SkipListPriorityQueue`/`AsyncPriorityQueue`）**不在本文**——
> 独有文档：[`priority-queues.md`](priority-queues.md)（使用）+ [`perf/priority-queues-performance.md`](perf/priority-queues-performance.md)（实测）。

---

## 0. 选型决策

| 需求 | 用 | 线程安全 | 一句话 |
|------|----|--------|--------|
| **异步等事件**（多 waiter 广播） | `AsyncManualResetEvent` | 是 | `WaitAsync` 等、`Set` 唤醒所有、`Reset` 重置 |
| **等 N 个并行子任务全完成** | `AsyncCountDown` | 是 | `Add`/`Remove` 计数到 0 唤醒 |
| **异步生产-消费队列**（持续运行） | `AsyncQueue<T>` | 是（多生产多消费） | `Enqueue`/`DequeueAsync`（空时异步等） |
| 带优先级的队列 | 优先队列族 | — | 独有文档：[`priority-queues.md`](priority-queues.md) |

---

## 1. `AsyncManualResetEvent` —— 异步等事件（多 waiter 广播）

异步版 `ManualResetEventSlim`——暴露 `ValueTask` 等待 + 多 waiter 广播。已 set 时快速路径零分配。

### 用法

```csharp
var ev = new AsyncManualResetEvent(initialState: false);  // 初始未触发

// 等待方（可多个）
async Task Waiter()
{
    await ev.WaitAsync(ct);   // 未 set 时异步等；已 set 时立即返回（零分配）
    /* 事件已触发 */
}

// 触发方
ev.Set();        // 唤醒所有等待者
ev.Reset();      // 重置为未触发（可复用）

ev.IsSet;        // 查询状态
ev.Wait(ct);     // 同步阻塞版（慎用，通常用 WaitAsync）
```

### 关键 API
| 成员 | 说明 |
|------|------|
| `AsyncManualResetEvent()` / `(bool initialState)` | 构造（唤醒默认线程池异步调度——Set 可持锁，安全） |
| `(bool initialState, bool runContinuationsAsynchronously)` | 同上 + 唤醒调度模式：`false` = 内联模式（Set 调用者线程内联续体，真实唤醒 ~93ns vs 默认 ~843ns；⚠️ **仅限 Set 调用点不持锁**——持 SpinLock 等不可重入锁会自死锁） |
| `WaitAsync(ct)` → `ValueTask` | 异步等（已 set 零分配） |
| `Wait(ct)` | 同步阻塞版 |
| `Set()` | 触发，唤醒所有 waiter |
| `Reset()` | 重置为未触发 |
| `IsSet` | 状态查询 |

> ⚠️ 为每个 waiter 独立挂 `ValueTaskSource`（不能共享单消费者 source）；1:1 高频唤醒走单槽快路径（免分配免锁），多 waiter 溢出回退链表。
> 实测数字与选型见 [`perf/core-primitives-perf.md`](perf/core-primitives-perf.md) §3。

---

## 2. `AsyncCountDown` —— 等 N 个完成

`Add()`/`Remove()` 计数，到 0 唤醒等待者。纯 `Interlocked` 无锁。

### 用法

```csharp
var cd = new AsyncCountDown();
for (int i = 0; i < N; i++) cd.Add();   // 计数 +N

// 各子任务完成时 Remove
Parallel.For(0, N, i => { DoWork(i); cd.Remove(); });

await cd.WaitUntilEmptyAsync(ct);       // 计数归 0 时唤醒
```

### 关键 API
| 成员 | 说明 |
|------|------|
| `AsyncCountDown()` | 构造（唤醒默认线程池异步调度） |
| `AsyncCountDown(bool runContinuationsAsynchronously)` | 同上 + 唤醒调度模式：`false` = 内联（`Remove` 调用者线程内联续体，稳态 ~135ns vs 默认 ~846ns；⚠️ 仅限 `Remove` 调用点不持锁） |
| `Add()` / `Remove()` | 增/减计数（纯 `Interlocked`，纳秒级） |
| `WaitUntilEmptyAsync(ct)` → `ValueTask` | 等计数归 0 |

---

## 3. `AsyncQueue<T>` —— 异步生产-消费队列

`ConcurrentQueue<T>` + 池化等待节点。队列非空时出队零分配。

### 用法

```csharp
var q = new AsyncQueue<int>();

// 生产者
q.Enqueue(42);
q.Count;            // 当前元素数

// 消费者（空时异步等）
int item = await q.DequeueAsync(ct);
q.TryDequeue(out var x);   // 非阻塞尝试出队
```

### 关键 API
| 成员 | 说明 |
|------|------|
| `Enqueue(T)` | 入队 |
| `DequeueAsync(ct)` → `ValueTask<T>` | 出队（空时异步等） |
| `TryDequeue(out T)` → `bool` | 非阻塞尝试 |
| `Count` | 元素数 |

> ⚠️ **不是通用 Channel**——不支持 `Complete`/背压，适合「持续运行永不关闭」的消费循环。

---

## 4. 优先队列族——见独有文档

`BucketPriorityQueue` / `SkipListPriorityQueue` / `AsyncPriorityQueue`（含 V2/V3 实验线）的完整用法、
选型与 Linux 验证结论见 **[`priority-queues.md`](priority-queues.md)**；实测数字见
[`perf/priority-queues-performance.md`](perf/priority-queues-performance.md)；
根因档案存内部存档。

---

## 5. `PooledValueTaskSource` —— 池化 awaitable 底座

`IValueTaskSource` 的池化实现——thread-local 栈 + global 回退 + 批量搬运，常规 Rent/Return **零争用零堆分配**。是多 waiter 异步原语（上面的 Event/CountDown/Queue）的共同底座。

**通常不直接用**——除非你在写自定义异步同步原语。直接用户用上面的 Event/CountDown/Queue 即可。如需自定义，参考 `AsyncManualResetEvent` 的实现（为每个 waiter 独立挂 source）。

---

## 6. 决策速查

```
我要异步等一个事件（多 waiter）？
  → AsyncManualResetEvent。WaitAsync 等，Set 唤醒，Reset 复用。

我要等 N 个并行子任务全完成？
  → AsyncCountDown。Add N 次，各完成 Remove，WaitUntilEmptyAsync。

我要异步生产-消费队列（持续运行）？
  → AsyncQueue<T>。Enqueue/DequeueAsync。不支持 Complete/背压。

我要带优先级的队列？
  → 优先队列族——独有文档 priority-queues.md（§4）。
```
