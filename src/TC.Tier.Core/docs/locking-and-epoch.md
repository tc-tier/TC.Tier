# 互斥、回收与原子更新（SpinRWLock / FairGate / LightEpoch / Atomic128）

> 这三个原语**职责不同、不可互相替代**，是 Core 里**最易拼错的地方**。
> 核心命题：**什么用锁、什么用 epoch、什么用 CAS——三者各管一摊；不该进 epoch 的绝不塞 epoch drain。**
> 完整积木全景见 [`../COORDINATION.md`](../COORDINATION.md)。

---

## 0. 怎么选：模式对比 + 实测 + 决策树

### 0.1 一句话决策

| 需求 | 用哪个 | 一句话 |
|------|--------|--------|
| **读侧零等待的状态查询**（读远多于写、数据可整体替换） | COW 快照发布 | 写侧 new 不可变对象 + volatile 引用发布；读者一次引用读 + 拷贝（`SegmentView`/`AtomicCompactReplace` 同形） |
| **单写者多读者的几个标量**（读者可保守回退慢路径） | seqlock | 版本奇偶 + 双读复验（`Segment.Projection` 四投影同形） |
| **对象的读写互斥**（读共享 vs 写/销毁排他，写不饿死） | `SpinRWLock` | 给对象挂一个 `SpinRWLock` 字段，读 `AcquireShared`，写/销毁 `AcquireExclusive`（写偏向：等待写者挡新读者） |
| **重试循环获取资源的到达顺序公平**（多写者竞争同资源不插队） | `FairGate` | fast path 查 `HasWaiters` 让位，慢路径 `TryAcquireSlow`，资源可获取后 `Wake()`（见 §1.5） |
| **对象/内存的延迟回收**（RCU：等所有读者退出再回收） | `LightEpoch` | drain action **只做轻量回收**（free 指针、归还 buffer） |
| **>64 位载荷的原子更新**（指针+标志 / 水位+ABA） | `Atomic128<T>` | 16B 对齐背板 + CAS，`Interlocked` 做不到的 16B 原子 |

**铁律：各管一摊、不可同需求重复上两套**（见 §4 反模式"双轨制"）。

### 0.2 六种模式完整对比（TC.Tier 全景）

> "读侧成本" = 一次完整读周期（进入+退出/快照读）非争用耗时；"读侧伸缩" = N 线程锤击聚合吞吐相对单线程倍数
> （i5-12400 6C/12T 实测：非争用=BDN SyncModesBench、伸缩/落地=--sync-probe，2026-08-20；原始数字见
> [perf/core-primitives-perf.md](perf/core-primitives-perf.md) §7）。数字是指示值，复跑取真值（§0.5）。

| 维度 | COW 快照 | seqlock | `LightEpoch` 周期 | `SpinRWLock` 共享 | `FairGate` | Monitor / RWLS |
|---|---|---|---|---|---|---|
| 读侧成本（非争用，BDN 每 op） | **0.7 ns**（volatile 引用读+5 标量拷贝） | 0.6 ns（版本双读+复验） | **9.6 ns**（本线程行两次写，零 interlocked） | 16.3 ns（进出各一次 CAS，同一字） | 无读路径（门不管资源） | 16.8 ns / 17.9 ns |
| **读侧并发伸缩** | **完美**（读者只读共享行） | **完美** | **×4.7 @6T**（每线程独占缓存行，近线性） | **×0.2 @6T**（单字 CAS 乒乓，聚合吞吐反降） | — | ×0.2 / ×0.7 |
| 6T 聚合读吞吐（ops/s） | N/A（无协议） | N/A | **787.7M** | 18.6M | — | 20.3M / 52.1M |
| 写侧成本 | 5 ns + **48 B/op 分配**（5 标量对象；高频发布有 GC 压力） | 0.6 ns（2 fence + 载荷写） | bump ~20ns + drain 扫描；**推进延迟 = 最慢读者临界区** | **26.1 ns**（进门 Or pending + 释放清双位，三次 locked op）；**落地延迟实测均值 0.1 µs**（1-11 读者锤击下不恶化，max ~16ms 为 OS 调度毛刺） | µs 级（Monitor 唤醒+5ms 让渡） | 非争用 16.8/20.0 ns；争用进内核 µs 级 |
| 写者饿死风险 | 无（无等待） | 无（读者重试） | 无（新读者即登记新 epoch） | **无**（pending 挡新读者） | 无（到达顺序服务） | Monitor 有（读者流下）/ RWLS 低 |
| 读者饿死风险 | 无 | 读者重试（短） | 无 | pending 写者期间新读者退避（有界：写临界区） | 队首写者后恢复 | RWLS 低 |
| 旧版本/死数据回收 | 托管靠 GC；**原生内存须配 epoch/hazard** | 无（原地写） | **内置**（drain action 延迟回收） | 不回收（只互斥） | 不回收 | 不回收 |
| 持有约束 | 读：任意长；写：拷贝期间无锁 | 读：短（重试）；写：短 | **Resume→Suspend 极短**、禁 await/阻塞/IO | 排他：短、纯内存、禁 await；共享：**可长持/跨 await**（读计划锁跨 IO 即此） | — | 任意（内核等待） |
| 写临界区跨 await | —（无临界区） | — | ❌ | ❌（排他）/ ✅（共享） | — | Monitor ❌ |
| TC.Tier 调用点 | `SegmentView`、`AtomicCompactReplace`、段表 COW 数组 | `Segment.Projection` 四投影 | 索引/元数据/Ring/队列延迟回收 | 段锁、MemoryFileSystem.Gate、读计划锁 | AcquireExtent 区间占用 | `_mutationLock`（段表结构）等结构性长临界区 |

### 0.3 读写锁的三种策略——为什么 Core 只提供"写偏向 + 公平门"

| 策略 | 语义 | 代价 | Core 取舍 |
|---|---|---|---|
| **读优先** | 新读者零门槛进门（等待中的写者不挡读者） | 读者吞吐最大；**写者在持续读者流下无界饿死**（LockWord 旧语义的实锤） | ❌ **不提供**。想要"读者永不被挡"= 该用 COW/seqlock/epoch（读者零协议成本）；真需要互斥时读优先是把写者往死里逼 |
| **写偏向** | 等待写者挡新读者 | 写落地有界（实测均值 0.1µs）；pending 期间新读者退避一个写临界区 | ✅ `SpinRWLock` 内建（不可关）——安全默认。需要投机写用 `TryAcquireExclusive`（失败不挂闸） |
| **到达公平（FIFO）** | 到达顺序服务，读写都不饿死 | 队列/park 复杂度，µs 级 | ✅ `FairGate`（正交组合：锁外面的到达顺序门）。不做 CLH/MCS 队列 RW——无真实调用点 |

> 不做模式枚举（构造参数选 read-pref/write-pref）：偏向是**锁的协议属性**不是旋钮；要第二种行为就是第二种原语
> （单实现铁律）。三把独立锁 = 三套契约测试矩阵 + 选型负担，换不来真实收益。

### 0.4 决策树

```
读多写少，读侧要绝对零等待？
  ├─ 数据可整体替换、小或中等 → COW 快照（写侧 new+发布；原生内存回收配 LightEpoch）
  ├─ 单写者 + 几个标量 + 读者可回退 → seqlock
  └─ 读者持有期间要屏蔽结构变更 + 事后延迟回收 → LightEpoch（保护期极短，禁 await）
真需要"读者互斥"（共享临界区内做 IO / 长计算）？
  → SpinRWLock：读 AcquireShared（可跨 await 长持），写/销毁 AcquireExclusive（短、纯内存）
     写要"拿到就赚拿不到走人" → TryAcquireExclusive（投机，不挡读者）
重试循环抢同一资源，怕后来者插队饿死先到者？
  → FastGate：快路径查 HasWaiters 让位 + TryAcquireSlow + 资源可获取后 Wake
结构性长临界区 / 持锁跨越 IO 与 await 的排他段 / 低频控制路径？
  → Monitor（lock）——别用自旋锁扛长临界区（烧 CPU 换延迟）
>64 位载荷原子更新 → Atomic128<T>
```

### 0.5 实测环境与复跑

- 非争用精测：`dotnet run -c Release --project benchmarks/TC.Tier.Core.Benchmarks -- --filter "*SyncModesBench*"`（BDN，结果入 `docs/perf/core-primitives-perf.md` §7）
- 并发伸缩/写偏向落地：`dotnet run -c Release --project benchmarks/TC.Tier.Core.Benchmarks -- --sync-probe [秒]`
- 本页 0.2 表数字：2026-08-20 ｜ Win11 ｜ i5-12400（6C/12T）｜ .NET 8 ｜ 探针 3 轮取中位

---

## 1. `SpinRWLock` —— 写偏向 RW 自旋锁原语

- **机制**：单个 64-bit 原子整数（`_value`）CAS 自旋 + `SpinWait`/`Thread.Yield` 让步。2026-08-20 自 `LockWord` 重构而来（读优先→写偏向；Monitor 等待-通知职责删除——生产零调用）。
- **位布局**：bit 63 = 写持有（`WriterMask`）；bit 62 = **写等待（`PendingMask`，写偏向核心）**；bit 61..32 = 读计数（每读者 `+ReaderInc`，`ReaderInc = 1<<32`，30 位）；bit 31..0 保留。
- **粒度**：显式临界区（`Acquire`→`Release`）。**挂到哪个对象就协调哪个对象**——任何需要 RW 互斥的对象挂一个 `SpinRWLock` 字段即获得协调锁。
- **跨 await**：**排他临界区内不可 await**（线程关联，Debug 释放线程校验会抓）；**共享可长持/跨 await**（计数语义无线程亲和——读计划锁持共享做 IO 即此用法）。注意：写偏向下等待写者会挡新读者——共享持得越久，写者与后续读者的等待越久。

> 排他锁天然 = "等所有共享读完成" = 安全点。**需要"等读完成再销毁/重做"的，持 `AcquireExclusive` 直接做，不要绕去用 epoch。**
>
> **写偏向协议**（LockWord 读优先在高并发读下写者饿死的根治）：写者进门先 `Interlocked.Or` 置 pending 挡住**新读者**，再等在途读者退出后 CAS 置写持有位——写者最多等"在途读者的临界区"，不被持续读者流饿死；读者 fast path 仍是一次 CAS（blockMask 加 pending 位，成本不变）。多写者下 pending 幂等，释放时连 pending 一并清除、由仍在等的写者自行重登记。

### ★ 2026-08-14 根因修复：获取必须**递增**（`+`），不是置位（`|`）

`AcquireShared` 原实现 `s | ReaderInc`（OR 置位）——`ReaderInc` 是固定单 bit，OR 语义下第 2..N 个并发读者
"获取成功"却不加计数，而每个读者释放都 `−1` → 第二次释放读计数即下溢 `−1` → 借位到 bit 63 出**假写者位**
→ 全部等待方永久自旋楔死。现实现为 `s + acquireMask`（递增），并有下溢绊线响亮失败（见下）。这是
"原语零单测被推到集成层、被误读为压测不稳定数月"的教训直接产物（契约测试见 `SpinRWLockTests`）。

### 内建防护（修 bug 的同时装上的仪器，别拆）

| 防护 | 生效配置 | 行为 |
|------|---------|------|
| **读计数下溢绊线**（`ReleaseShared`） | **Debug + Release（永久）** | 读计数已为 0 = 无配对释放：<b>修改前检测直接抛（v2 无损伤化——旧版先减再回滚，减完到回滚之间锁字短暂呈现假写者位）</b>；stderr 打调用栈 ③抛 `InvalidOperationException` 携带 value + 调用栈（Debug 再带操作历史）。竞态下若误释放"偷"掉在途读者计数，对方的释放会在 count==0 兜底拦下——检测延迟一步，不会漏 |
| **释放线程校验**（`ReleaseExclusive`） | Debug | 释放线程 ≠ 获取线程（Debug.Assert）——**排他**临界区内 await 换线程释放 = 锁泄漏的运行时捕获 |
| **值示波器** | Debug | 最近 24 次原子操作环形记录（op/线程/before/after 十六进制），任何绊线异常自动携带——不必现场考古；仪器存第二缓存行，不污染热锁字 |
| **读计数溢出护栏**（30 位上限 2^30） | Debug + Release | 溢出会借位进 pending 位出假闸门——超限抛/退避（理论值，防御性） |

### API 全表

| 成员 | 语义 |
|------|------|
| `AcquireExclusive()` / `ReleaseExclusive()` | 排他锁（写/销毁路径）。获取设 bit63；阻塞条件 = 任一写者或读者在场。 |
| `AcquireShared()` / `ReleaseShared()` | 共享锁（读路径）。计数 `+ReaderInc`；阻塞条件 = 有写者。释放带上溢下溢防护（见上表）。 |
| `EnterExclusive()` / `EnterShared()` | scope 工厂（返回 `ref struct`，`using` 自动释放）——**常规临界区优先用**。 |
| `TryAcquireShared()` | 投机读：不自旋立即返回。拿不到走别路（如降级/快照）的场景用。 |
| `TryAcquireExclusive()` | 投机写：不自旋、**失败不挂 pending 闸**（不挡读者）——"拿到就赚"路径；需要写偏向保证必须用 `AcquireExclusive`。 |
| `IsHeldExclusive` / `ReaderCount` | 诊断属性（瞬时值，读出即可能过期）——仅调试/指标，**业务逻辑禁止依赖**。 |

> **等待-通知已删除**（2026-08-20）：`Wait`/`PulseAll`（Monitor 配合）自 2026-08-16 建段协调零锁化后
> 生产零调用，随 LockWord→SpinRWLock 重构删除。需要等待-通知用 `AsyncManualResetEvent`（异步）或
> `FairGate`（到达顺序协调）/Monitor 直接配合——不要再给 RW 锁塞第三职责（LockWord 三职责混杂正是
> 本次重构的动因）。

### SpinRWLock 铁律（违反 = 死锁）

1. **绝对不可重入**：同线程禁止重复 `AcquireShared`/`AcquireExclusive`。基于位计数/位掩码 CAS，重入共享锁看似安全，但中间穿插排他锁会直接自死锁。
2. **禁止锁升级**：持有共享锁的线程，禁止直接升级为排他锁（两线程同时持共享锁 + 同时升级 = 互相等 = 经典死锁）。需要写须先 `ReleaseShared` 再 `AcquireExclusive`，或一开始就持排他锁。
3. **排他临界区极短**：禁止包含任何 IO、内存分配、`await`（同步原语、线程关联——临界区内 await 导致线程切换后 Release 线程 ID 不匹配 = 锁泄漏，Debug 的释放线程校验会抓）。共享临界区可长（读计划锁跨 IO 持共享是设计内用法），但越长写者等待越久——能锁外做的移到锁外。

### `using` 自动释放锁模式（优先，避免手忘 Release → 死锁）

两种锁都提供 `using` 自动释放 scope——**常规临界区优先用 scope 工厂**（手动 `Acquire`/`Release` 易忘 Release → 死锁）：

| 锁 | scope 工厂（返回 `ref struct`，`using` 自动释放） | 用法 |
|----|-------|------|
| `SpinRWLock`（自研写偏向 CAS RW 锁） | `EnterShared()` / `EnterExclusive()` | `using var s = rwLock.EnterShared();` |
| `System.Threading.SpinLock`（.NET 互斥锁） | `SpinLockScope.Enter(ref spinLock)` | `using var s = SpinLockScope.Enter(ref _lock);` |

- `SpinRWLock` 另有**手动** `AcquireShared`/`AcquireExclusive` + `ReleaseShared`/`ReleaseExclusive`——释放时机需显式控制的长临界区用（如读计划锁跨 IO）；新代码常规临界区宜用 `Enter*` scope。
- `SpinLockScope`（`Primitives/SpinLockScope.cs`）是 BCL `System.Threading.SpinLock` 的 `using` 包装，与 `SpinRWLock` 是**不同的锁**（SpinRWLock=自研 RW 锁；SpinLockScope 包装 .NET 互斥锁）。

---

## 1.5 `FairGate` —— 重试获取资源的到达顺序公平门

`SpinRWLock` 解决"读写互斥谁进门"；**`FairGate` 解决"重试循环抢资源谁插队"**——资源占用/释放属调用方，
门只管两件事：**让后来者不插队**、**唤醒并让渡先手**。演进自 SegmentTable.AcquireExtent 手搓公平门
（2026-08-18 双写者长持锁窗口饥饿根治，7a9685aa），2026-08-20 行为保持式下沉 Core。

### 协议（三方角色，缺一不可）

| 角色 | 调用 | 语义 |
|------|------|------|
| 获取方 fast path | `HasWaiters` | 有等待者时**不走快路径**——否则零间隙复占者永远插队，被唤醒者的调度延迟 > 让渡窗口时持续失手（实测 3/8 残余超时根因） |
| 获取方 slow path | `TryAcquireSlow(tryAcquire)` | 登记等待者 → 门锁内执行占用尝试 → 成功 true / 失败 park 等唤醒（50ms 兜底防丢失唤醒）后 false，调用方回到协议循环 |
| 释放方 | `Wake()` | 资源**已变为可获取后**再调（唤醒过早 = 被唤醒者双检必败）：PulseAll + 让渡 5ms 先手（唤醒者随后的复占是热自旋 µs 级，被唤醒者无先手则每次 Pulse 后仍被抢回） |

### 约束与成本

- `tryAcquire` 在门锁内执行：必须**纯内存、无异常、快速返回**；其中不得获取与本门逆序的锁。
- 无等待者：fast path 一次 volatile 读（~1ns），`Wake` 零阻塞直返。有等待者走 Monitor（µs 级）——
  用于"临界区外就是 IO"的场景足够；**不做队列自旋**（CLH/MCS 公平 RW 无真实调用点，不造）。
- 50ms park / 5ms 让渡为 7a9685aa 调优值（Windows 定时器量子 ~15ms，µs 级释放间隙靠兜底轮询补）。

> 现使用方：`SegmentTable._extentGate`（AcquireExtent 区间占用公平性）。契约测试 `FairGateTests`
> （计数配对/唤醒活性/8 线程单槽长持窗口无饿死压测）。

---

## 2. `LightEpoch` —— epoch RCU 延迟回收

- **粒度**：线程级"我正在读"标记（不是对象锁）。
- **适合**：**对象/内存延迟回收**——等所有读者退出 epoch 再回收（RCU 语义）。**仅上层结构组件**用（index 节点回收、metadata 版本回收）。
- **跨 await**：**不可**（线程关联的 entry 表，Resume/Suspend 必须同线程）。

### 2.1 机制：一分钟看懂它怎么工作

三块拼图（`Epochs/LightEpoch.cs`，无锁、热路径仅几次指针写）：

1. **全局单调 epoch 计数器** `_currentEpoch`（从 1 起，`Interlocked.Increment` 推进）；
2. **每线程 entry 表**（pinned 数组、64B 缓存行对齐、≥128 槽）：每个活跃保护区占用一槽，槽里记
   `localCurrentEpoch`（本线程进入保护区时看到的 epoch 值）+ 线程 ID + 6 个 marker；
3. **drain 列表**（(epoch, action) 对，16 槽）。

```
读者 A：Resume()  → entry.localCurrentEpoch = 当前 E（"我在 E 及之后读"）
        ...读共享对象...（保护期内，E 之前摘除的旧对象保证不被回收）
        Suspend() → entry 清 0

写者：物理摘除旧对象 → BumpCurrentEpoch(() => 旧对象.Dispose())
        → _currentEpoch: E → E+1；action 绑定旧 epoch E
        → 待所有 entry 的 localCurrentEpoch > E（= E 及更早进入的读者全退出）
          → action 被"恰好此刻触发 drain 检查的线程"顺手内联执行
```

- **safeToReclaimEpoch = min(所有 entry 的 localCurrentEpoch) − 1**：早于等于它的 epoch 都已静默、可回收。
- **drain 是协作式**：没有后台线程——action 由 bump 调用者 / 后续 `Resume`/`Suspend` 的参与者线程**顺手**
  执行（这是"drain action 必须纯内存、非阻塞"的根源，见 §2.5）。
- ⚠️ **命名反直觉**（对齐 FASTER 术语）：**`Resume` = 进入保护区**、**`Suspend` = 退出保护区**——
  不是"恢复/挂起线程"。

### 2.2 API 全表

| 成员 | 语义 / 契约 |
|------|------------|
| `LightEpoch()` | 建实例（组件级——所有协作线程共用一个，见 §2.3 装配）。 |
| `Resume()` | **进入保护区** = `Acquire`（首次在本实例上 Reserve 线程槽位）+ `ProtectAndDrain`（登记当前 epoch；若有 pending drain 顺手执行）。 |
| `Suspend()` | **退出保护区** = `Release`（entry 清 0）+ 若有 pending drain 做 `SuspendDrain`（最后退出的线程可能顺手清空 drain 列表）。 |
| `ProtectAndDrain()` | `Resume` 的后半段（仅登记+drain，不动槽位引用计数）。外部一般不单用——直接 `Resume`/`Suspend`。⚠️ 调用前线程必须已 `Acquire`（未 Resume 单调它 = Debug 绊线）。 |
| `BumpCurrentEpoch(Action onDrain)` | 推进 epoch 并注册延迟 action（绑定旧 epoch）。**调用线程必须已在保护区**（先 `Resume`）。drain 列表满时会自旋等槽位（见 §2.6 重入约束）。 |
| `CurrentEpoch` | 当前全局 epoch（单调递增）。给回收调度方给 pending 对象打 epoch 标签用。 |
| `SafeToReclaimEpoch` | 最近可安全回收的 epoch。**仅在 drain action 回调栈内读取才有意义**（Drain 执行 action 前刚算完）；保护区外读无同步保证。 |
| `ThisInstanceProtected()` | 当前线程是否正持有本实例的保护（写侧自检用）。 |
| `Mark(markerIdx, version)` / `CheckIsComplete(markerIdx, version)` | 每 entry 附带 6 个 marker 槽的"活动推进上报/核查"设施：各线程 `Mark` 自己推进到的版本，`CheckIsComplete` 检查**所有活跃线程**都已上报到该版本。给版本协调状态机用（见 [`version-scheme.md`](version-scheme.md)），普通延迟回收用不到。 |
| `NestedBumpCount`（静态） | 累计嵌套 bump 次数。正常恒 0；非 0 = 潜在 epoch 自死锁（Release 线上排查指标）。 |
| `Dispose()` | 组件销毁时调。Debug 构建有绊线：仍有线程持保护未退出 → 立即抛（槽位泄漏会拖死后续一切 drain）。 |

> 实现底座补充：entry 表用 POH pinned 数组（持有强引用防 GC 回收→悬挂指针）；线程槽位静态共享、跨实例复用；
> 同目录 `FastThreadLocal<T>`（internal）是独立的槽位式 thread-local 工具，与 LightEpoch 无耦合。

### 2.3 标准用法（自包含骨架——读保护 + 延迟回收）

```csharp
sealed class DeferredReclaimer
{
    private readonly LightEpoch _epoch = new();          // 组件级单实例，所有线程共用
    private Node? _head;                                  // 无锁读的共享结构

    // ── 读侧（任意线程、高频）：保护期内旧节点保证存活 ──
    public Node? Peek()
    {
        _epoch.Resume();                                  // 进入保护区
        try { return _head; }                             // 读共享状态（不可 await！见 §2.6）
        finally { _epoch.Suspend(); }                     // 同线程配对退出
    }

    // ── 写侧（摘除旧对象 + 延迟回收）──
    public void Replace(Node newNode)
    {
        _epoch.Resume();                                  // ★ bump 调用线程自己也必须在保护区
        try
        {
            var old = Interlocked.Exchange(ref _head, newNode);
            _epoch.BumpCurrentEpoch(() => old.Dispose()); // 旧 epoch 静默后执行；执行线程任意
        }
        finally { _epoch.Suspend(); }
    }

    public void Dispose() => _epoch.Dispose();            // 组件销毁（Debug 绊线把关无人持保护）
}
```

要点：
- **实例装配**：一个协作域一个 `LightEpoch`（组件 own、随组件 Dispose）——不是全局单例、不按线程建。
  多实例各自独立计数（互不推动对方 epoch）。
- **保护期极短**：`Resume`→读→`Suspend` 之间只做指针读写级的事；**绝不 await / 阻塞**（§2.6）。
- **回收时机不确定、执行线程不确定**——action 只依赖"旧 epoch 已静默"这一事实，不得假设何时/何线程执行。
- 真实使用方（Core 内，可对照）：`EpochProtectedVersionScheme`（版本协调，[`version-scheme.md`](version-scheme.md)）、
  `AsyncPriorityQueueV2/V3`（节点延迟回收）、`MemoryFileSystem`。

### 2.4 协议契约：Debug 绊线全表（违反 = 立即抛 + 示波器）

`LightEpoch` 有**两层防护**（2026-08-14 AsyncPriorityQueue 挂死事故后建立，对齐 `SpinRWLock` 的值示波器范式）：

| # | 违反 | Debug 表现 | Release 表现（为什么不能靠线上撞） |
|---|------|-----------|----------------------------------|
| 1 | `BumpCurrentEpoch` 调用线程未先 `Resume` | 立即抛 | 写保留 entry0 = epoch 表静默腐败 |
| 2 | `Suspend` 未与同实例 `Resume` 配对（含**跨线程 Suspend**：Resume 在线程 A、await 后线程 B 里 Suspend——entry 腐败最致命源） | 立即抛 | 槽位残留旧 epoch → safe 永久停摆 → 全局 drain 阻塞 |
| 3 | 同实例保护区重入（未 Suspend 又 Resume） | 立即抛 | 计数/状态错乱 |
| 4 | 嵌套 `BumpCurrentEpoch`（外层 bump 回调内再 bump） | 立即抛（回滚深度计数） | epoch 自死锁风险（自己等自己退出）；`NestedBumpCount` 计数 |
| 5 | `Dispose` 时仍有线程持保护 | 立即抛（列出 entry/epoch） | 槽位泄漏，拖死后续 drain |
| 6 | drain action 抛异常 | 包装重抛 + 上下文 | 异常炸到"顺手执行 drain 的无辜线程" |

异常消息自动携带线程/entry/epoch 状态 + **最近 32 次协议操作历史（示波器：Acquire/Release/ProtectAndDrain/Bump/Drain-Action 环形记录）**——不必现场考古。Release 构建两层全部编译掉（零开销），线上排查靠 `NestedBumpCount` 等指标。

### 2.5 ★ 什么该 / 不该给 epoch 处理（最易错）

**该给 epoch 处理的**（drain action 唯一职责）：
- ✅ **轻量回收**：free 一个指针、归还一个 buffer、丢一个引用。
- ✅ 动作必须**非阻塞、纳秒~微秒级**。

**不该塞进 epoch 的**（违反 = 降吞吐 / 死结）：
- ❌ **阻塞 IO**（`PunchHole` 磁盘系统调用、`fsync`、段提升等重操作）——drain 是**协作**式，action 由"任意触发 drain 的线程"（可能是读线程）顺手执行；塞毫秒级 IO = IO 落在读热路径。
- ❌ **"同步等 drain 完成"的逻辑**——和 epoch 的异步协作本性冲突，会死结。
- ❌ **用 epoch drain 给销毁排序**——销毁排序持段排他锁直接做（见 §1）。

> 一句话：**epoch drain 只回收、不 IO、不排序。** 该 IO 的投递到 `BackgroundWorkerLoop`（见 [`worker-loop.md`](worker-loop.md)）或持锁直接做。

### 2.6 epoch 临界区负面清单（违反 = 死锁/降吞吐）

epoch 保护的临界区内（`Resume`→`Suspend` 之间），**绝对禁止**：
- ❌ 执行任何阻塞操作（IO、锁等待、`Thread.Sleep`）；
- ❌ `await` / 让出线程（Resume/Suspend 必须同线程配对——await 后 continuation 到别的线程 = 跨线程 Suspend，绊线 #2）；
- ❌ 调用 `BumpCurrentEpoch` 或触发 drain 的逻辑（重入死锁；写侧的 bump 在自己保护区内调一次是合法的，drain action **内部**再 bump 不行——绊线 #4）；
- ❌ 分配大量托管内存（触发 GC 拉长临界区）。

**drain 执行线程**：drain action 可能由**任意业务线程**执行（触发 drain 的读线程顺手执行），因此 action 必须**纯内存、无异常、无副作用**——绝对不能假设执行线程。

---

## 3. `Atomic128<T>` / `NativeAtomic128` —— 128 位 CAS

`SpinRWLock`/`LightEpoch` 之上还有一类需求：**>64 位载荷的原子更新**（"指针+标志""水位+ABA version"无法用 `Interlocked`）。`NativeAtomic128.CompareExchange` 解决它——x86-64 `lock cmpxchg16b` / ARM64 `ldaxp-stlxp`。

### 标准用法

★ **优先用 `Atomic128<T>` 封装**（`Primitives/Atomic128.cs`）——16B 对齐背板 + 探测降级 + 裸读不撕裂已内置。裸调 `NativeAtomic128` 仅在需要自定义对齐背板/特殊场景。

★ **热路径用 Unsafe 快路径**（`TryCompareExchangeUnsafe` / `ReadUnsafe`）——跳过 `CasEnabled`/`IsDisposed` 检查，压到 ≈ 直接 native CAS 的 ~11ns（safe `TryCompareExchange` 经实测 ~16ns）。调用方须保证：① native CAS 可用、② 未 `Dispose`（违反 → AV/抛异常）。

### ⚠️ 硬约束

- `location` 必须 **16 字节对齐**（cmpxchg16b 要求，不对齐 → 硬件异常）。用 `AlignedMemoryManager` 分配（对齐见 [`../COORDINATION.md`](../COORDINATION.md) §6.2）。
- 返回值 marshaling 必须 `U1`（C99 bool 1 字节）——曾误用 `Bool`(4B) 读 EAX 残留，CAS 失败误判成功 → 并发租借重叠。
- **每水位一个独立 64B 块**（不要合并成 128B 块：Intel Spatial Prefetcher 会绑定相邻缓存行，实测 2.19M vs 1.35M ops/s）。

### 标准范式（`TailWatermarkSlot`——双尾水位 CAS）

1. **载荷设计**：`LogicalAddress`(16B = SegId 4B + Extension 4B + Offset 8B)，Extension 兼当 ABA version。
2. **对齐分配**：`new AlignedMemoryManager(AlignmentConst.Alignment64B, AlignmentConst.Alignment64B, zeroed:true)`。
3. **零拷贝 reinterpret**：`ref var loc128 = ref Unsafe.As<LogicalAddress, Int128>(ref loc);` 再喂 CAS。
4. **CAS 循环**：裸读当前 → 条件检查 → 算新值（version+1 防 ABA）→ CAS → 失败 `SpinWait.SpinOnce()` 重试。
5. **ABA 防护**：回退时 `Extension+1`。
6. **能力探测 + 降级**：`ProbeNativeCas()` 失败 → 降级 `lock`（生产永不触发，但必须提供）。

```csharp
ref var loc = ref mem.GetRefUnsafe<LogicalAddress>(0);            // 16B 对齐裸指针
ref var loc128 = ref Unsafe.As<LogicalAddress, Int128>(ref loc);  // 零拷贝 reinterpret
return NativeAtomic128.CompareExchange(ref loc128,
    Unsafe.As<LogicalAddress, Int128>(ref expected),
    Unsafe.As<LogicalAddress, Int128>(ref value));
```

**别处用法**：`AsyncPriorityQueue` 曾用 `MarkedReference`（指针 + marked 打包 16B）——Route A（marker 协议）后已不再打包，见 [lab/async-priority-queue-root-cause.md](lab/async-priority-queue-root-cause.md) §6 决策。

---

## 4. 反模式（禁止重蹈）

### ❌ 把阻塞 IO 塞进 epoch drain action
- **症状**：`PunchHole`、段提升等被注册为 `LightEpoch.BumpCurrentEpoch(action)` 或塞进 drain worker 队列。
- **为什么错**：见 §2——IO 落在读热路径 + 与异步协作本性冲突死结。
- **正解**：销毁 IO 持段排他锁直接执行，或投递 `BackgroundWorkerLoop`。

### ❌ 在未持 epoch 的线程调 `BumpCurrentEpoch`
- **症状**：线程没 `Resume`/`Acquire` 就调 `Epoch.BumpCurrentEpoch(callback)`。
- **后果**：违反 LightEpoch 协议 → Debug 立即抛 `InvalidOperationException`（绊线 #1，见 §2.4）；Release 下 `ProtectAndDrain` 写入保留 entry0（静默腐败）。
- **正解**：调用线程先 `Resume()`，用完 `Suspend()`（完整骨架见 §2.3）。

### ❌ 互斥"双轨制"——同需求上 SpinRWLock + epoch 两套
- **症状**：普通 `Read` 用 `SpinRWLock` 段共享锁，`DirtyRead` 用 `LightEpoch`，销毁方被迫段排他 lease **外加** epoch drain 两套都上。
- **为什么错**：互斥来源不单一；epoch drain 那套引出上面两个反模式。
- **正解方向**：存储读写互斥**统一用 `SpinRWLock`**（含 DirtyRead 改持段共享锁）；`LightEpoch` 退出存储路径，只留上层结构组件的延迟回收。
