# Core/Collections——并发优先级队列使用指南

> **定位**：三个**线程安全**优先级队列实现，覆盖不同优先级形态与并发模型。非线程安全场景用
> .NET 内置 `PriorityQueue<T,P>`（单线程基线，性能对照见 [perf/priority-queues-performance.md](perf/priority-queues-performance.md)）。
>
> **实验版本声明**：`AsyncPriorityQueueV2` / `AsyncPriorityQueueV3` 是**设计验证实验**
> （`[Experimental]` + `internal`，测试默认 Skip）——不可用于生产；档案见
> [lab/async-priority-queue-root-cause.md](lab/async-priority-queue-root-cause.md) 与
> 生产异步队列一律 `AsyncPriorityQueue<T>`（Route A 基线）。

---

## 1. 选型速查

| 你的场景 | 用什么 | 理由 |
|---------|--------|------|
| 优先级是少量离散枚举（4~16 级，如任务等级/调度带） | **`BucketPriorityQueue<TP,T>`** | 数组桶 + `ConcurrentQueue`——单次往返 ~52 ns，8 线程近线性（34.6 Mops/s）；think-time/毒丸柱塞全负载下最稳（perf §5）——**没有短板的全维度王** |
| 优先级任意取值 + 高吞吐 + 临界区短且持锁者不被外部长延迟 | **自建一把 `lock` + 内置 `PriorityQueue<T,long>`** | 2026-08-17 对照实测（perf §2/§4/§5.1）：8 线程 32.0 Mops/s、单线程 305ns、think-time 下 p50/max 全场最优。⚠️ **柱塞边界（perf §5.2）**：持锁者被抢占/GC/页错误时全队 max 放大数百倍——延迟敏感场景换 Bucket/Async |
| 优先级是任意 `int` 且需要免疫持锁者延迟（别人的延迟不通过锁传播） | **`AsyncPriorityQueue<T>`** | 无锁跳表——8 线程 12.6 Mops/s、256K 积压 1.3µs。⚠️ 分配率 1.3KB/op 在 Workstation GC 下尾延迟 22~170ms（perf §5）——延迟敏感需 Server GC 或 B' 零分配路线 |
| 优先级任意 `long` + 中低并发 / 零分配路径 / 无锁 `TryPeek` | **`SkipListPriorityQueue<T>`**（#PERF-004 修复后） | 单线程 466ns、8 线程 9.4 Mops/s、分配 92 B/op、think-time 135K ops/s——健康可用的细分锁选项（perf §4.2）。吞吐优先仍选自建大锁+堆（领先 3~4×，perf §2） |
| 需要 `DequeueAsync` 异步等待且高并发出队 | Bucket / Async / SkipList（修复后皆可） | 等待机制各异（§3）；SkipList 修复后 4C+4P 全收齐（修复前 30s 不收敛——perf §4 档案） |
| 单线程 / 无并发 | .NET 内置 `PriorityQueue<T,P>` | 非线程安全但最快（~11 ns） |
| 想用 V2/V3 实验版 | **禁止**（生产） | 实验——非生产；仅设计档案与对照实验 |

**共同契约**（三实现一致）：
- **值小者优先**（min-heap 语义）；同优先级 **FIFO**（sequence 单调递增保证）。
- `Enqueue` 从不阻塞；`TryDequeue/TryPeek` 非阻塞（空返 false）；`DequeueAsync(ct)` 异步等待。
- `Count` 并发下**近似值**（诊断/监控用，不做同步决策依据）。
- `Dispose` 幂等；唤醒全部等待者（等待者通常经 ct 退出，Dispose 是无 ct 调用者的兜底）。
- 入队/出队混合下的**长时间总量平衡**由调用方保证（无界队列不设背压——worker 模型经上游限流）。

---

## 2. 三实现语义细节

### 2.1 BucketPriorityQueue——离散枚举桶

```csharp
enum TaskLevel : short { Critical = 0, High = 1, Normal = 2, Batch = 3 }

using var q = new BucketPriorityQueue<TaskLevel, MyTask>();
q.Enqueue(task, TaskLevel.High);
if (q.TryDequeue(out var next)) { }          // 严格按枚举值升序：Critical → High → Normal → Batch
if (q.TryPeek(out var peek)) { }             // 只看不取
var item = await q.DequeueAsync(ct);         // 空则异步等待（许可模型，见 §3.1）
```

- **构造即定桶**：桶数 = 枚举值数量（`Enum.GetValues`），运行期不可变。枚举定义新增值 = 桶数变化——
  **队列是实例级绑定，跨版本序列化场景注意**。
- 入队 = `ConcurrentQueue.Enqueue`（无锁）；出队 = 按枚举值升序扫桶（单消费者接近 wait-free）。
- **性能特征**：出队成本 O(桶数/扫描命中)，与元素数无关——积压百万项时优势扩大。

### 2.2 SkipListPriorityQueue——任意 long 优先级

```csharp
using var q = new SkipListPriorityQueue<Deadline>(maxLevel: 31);   // 默认 31，一般不动
q.Enqueue(work, deadlineTicks);        // 任意 long（如 DateTime.UtcNow.Ticks）
if (q.TryDequeue(out var earliest)) { }   // 值最小（最早截止）先出
```

- **key = (priority << 48) | sequence**：priority 占高 16 位、sequence 低 48 位——同优先级严格 FIFO。
  ★ 由此 **priority 实际有效域是 16 位**（-32768~32767 之外的值会互相重叠）——用 Ticks 这类宽值域
  时必须先映射/压缩到 16 位桶（如"分钟级时间片 + 槽内序号"）。
- 细粒度锁：每节点一把 `SpinLock`，hand-over-hand——不同区间的插入/删除并行；同区间操作排队。
  **全层级 lazy validate**（Herlihy 论文协议）：标记删除前校验所有层 preds 仍指向 victim——标记即完整摘除，
  marked 节点永为瞬态（曾只校验 level-0，高层 marked 滞留导致 Find 退化 O(n)，2P+2C 20 万项 86s→0.2s）。
- **maxLevel 护栏**：构造校验 `[1, 31]`；⚠️ 层级不足时查找退化 O(n)（2^maxLevel 应 ≥ 预期条目数——
  实测 maxLevel=5 @ 20 万条 = 33µs/op vs 31 层 = 0.3µs/op）。
- **内存回收简单**：unlink + 解锁后节点安全可回收（GC 语义，无 epoch/hazard pointer）。
- **并发扩展真相**：严格优先级 + FIFO 语义使同优先级全部尾插同一点、所有消费者抢同一个 min——
  吞吐上限由这两个串行热点决定，而非锁粒度。⚠️ 优先队列负载（DeleteMin 全打队头）令细粒度锁的
  "区间并行"收益为零，validate 重试是纯浪费功——与"一把 `lock` + 内置 `PriorityQueue`"基线相比
  **全维度落后**（对照见 [perf/priority-queues-performance.md](perf/priority-queues-performance.md) §2/§4，
  ⚠️ 该档案为 #PERF-004 修复前压测，修复后本机 2P+2C 混合 86.9s→0.2s、8P Enqueue 7.1 M/s，数字待复测）。
  本类定位是**细分锁实现**（简单、任意 long 优先级、消费者少、无锁 `TryPeek`）；
  **无锁高吞吐场景用 `AsyncPriorityQueue`**（Route A 生产基线），离散枚举优先级用 `BucketPriorityQueue`。
- 算法依据：Herlihy & Shavit, "A Simple Optimistic Skip-List Algorithm" (DISC 2006, lazy 版本)。

### 2.3 AsyncPriorityQueue——无锁跳表（生产基线 Route A）

```csharp
using var q = new AsyncPriorityQueue<WorkItem>();    // epoch 参数保留兼容——Route A 后 GC 回收，传 null 即可
q.Enqueue(item, priority: 3);                        // int 优先级
if (q.TryDequeue(out var top)) { }
```

- **无锁出队**：跳表 + Marker 逻辑删除（delete-min 标记后 splice 物理摘除）——无锁队列的
  DeleteMin 经典难点由 marker 协议解决。
- **2026-08-17 协议修复（压测驱动，共三处）**：① 尾插（succs==null）高层也 CAS 链接——否则持续
  尾插负载下高层索引永不建立，Find 退化线性扫描（256K 积压实测 ≥1.9ms/op，修复后 1.5µs）；
  ② 发布纪律——node 全部 Forward 字段在 level-0 发布前写完（发布后的普通写会覆盖并发删除者的
  Marker，令已删节点"复活"）；③ 高层标记 CAS 重试到落地——标记会被 Find 的 helping splice
  同字段竞态打败，留下"已出队但高层未标"的悬挂僵尸，最终演化为全体自旋的**活性死锁**
  （取证全程见 lab 档案 §8）。修复后单线程 423ns（原 1156ns）、8 线程 13.3 Mops/s。
- **Route A 内存策略**：节点交给 **GC** 回收（不再依赖 epoch——构造参数 `LightEpoch?` 仅为兼容保留，
  传共享实例或 null 均可）。V2/V3 探索的槽位复用/非移动内存是其实验方向（见 lab 文档）。
- **DEBUG 自检**：每 64 次操作巡检 level-0 链不变式（key 严格递增/marker 链长 ≤1/步数护栏）——
  Release 零开销；结构损坏在 DEBUG 构建当场爆，不带病运行。**注意**：活性死锁不破坏链结构，
  校验器对它不报警——活性回归靠 `--pq-wedge` 复现器与楔死看门狗测试兜底。

---

## 3. DequeueAsync 等待机制差异（三个实现三种模型）

| 实现 | 等待机制 | 唤醒语义 | 适用注意 |
|------|---------|---------|---------|
| Bucket | **一项一许可** `SemaphoreSlim`（上限 int.MaxValue） | MPMC **公平逐项唤醒**：每次 Enqueue 精确唤醒一个等待者 | 多消费者公平性最好；许可与项 1:1，拿许可后 TryDequeue 必中（极端竞争错过则重试自洽） |
| SkipList | `AsyncManualResetEvent` 模型：Reset → 再试 → Wait | Set 广播——全部等待者醒来抢（败者回睡） | 惊群规模 = 等待者数；消费者少时无差别 |
| Async | 同 SkipList（fast-path 直返 + 慢路径事件等待） | 同上 | DequeueAsync 有 fast-path：队列非空零等待开销 |

```csharp
// 典型 worker 消费循环（三实现通用）：
while (!ct.IsCancellationRequested)
{
    WorkItem item;
    try { item = await q.DequeueAsync(ct); }
    catch (OperationCanceledException) { break; }    // ct 退出是正道；Dispose 唤醒是兜底
    Process(item);
}
```

## 4. 常见陷阱

1. **SkipList priority 是 16 位有效域**（key 编码所限）——宽值域（Ticks/大权重）必须先压缩映射，
   否则不同优先级互相折叠、顺序错乱。
2. **Bucket 的枚举即桶布局**——枚举值 discontinuous（如 `A=1, B=100`）会建 101 个桶（中间全空扫），
   定义尽量从 0 连续。
3. **`Count` 是近似值**（各实现内部 Interlocked 读，瞬时态）——不能用于"等 Count==0 再做 X"的同步协议，
   用 `TryDequeue` 返回值或外部信号。
4. **Dispose 不清空元素**——只唤醒等待者并封禁后续操作（`ObjectDisposedException`）；残余元素由 GC
   回收（引用类型元素若持有非托管资源需先排空再 Dispose）。
5. **无背压**——入队永不阻塞不拒绝；生产速率失控时内存无界增长，须由上游限流/有界信箱约束。
6. **单线程别用这三者**——线程安全的代价（Bucket 4.7× / SkipList 30× / Async 38× 于内置
   `PriorityQueue`，见 perf 文档）；单线程场景内置队列碾压。
7. **SkipList 的 maxLevel 必须够用**——2^maxLevel 应 ≥ 预期条目数（构造有护栏），层级不足时查找
   退化 O(n)（实测 maxLevel=5 @ 20 万条 = 33µs/op vs 31 层 = 0.3µs/op）。多消费者高吞吐负载吞吐
   落后大锁 3~4×（perf §2），吞吐优先自建大锁+内置堆。
8. **V2/V3 在 Release 程序集里 internal**——编译期不可达（`[Experimental]` 诊断拦截 + 测试默认 Skip）；
   引用它们 = 引用实验，生产代码审查直接拒绝。

## 5. 与其他文档的关系

- 性能基线（单线程混合往返 / **并发吞吐矩阵 / 积压敏感性 / 并发正确性压测**）：[perf/priority-queues-performance.md](perf/priority-queues-performance.md)
- V2/V3 实验根因与设计档案：[lab/async-priority-queue-root-cause.md](lab/async-priority-queue-root-cause.md)
- worker 消费循环模式：[worker-loop.md](worker-loop.md)
