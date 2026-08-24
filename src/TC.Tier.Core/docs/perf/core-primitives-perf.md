# Core 基础原语性能报告（异步原语 / 队列 / 池 / 缓存）

> 覆盖：`AsyncManualResetEvent` · `AsyncCountDown` · `AsyncQueue<T>` · `PooledValueTaskSource` ·
> `OverflowPool<T>` · `ClockCache<TKey,TValue>`（优先队列族单独成文，见 async-primitives.md §4）。
> 使用指南：[`../async-primitives.md`](../async-primitives.md) / [`../memory.md`](../memory.md) / [`../cache-and-compute.md`](../cache-and-compute.md)
>
> ⚠️ 指示值（单机开发环境实测；Artifacts 不入库，本文即存档）；机器/负载不同会变，§1 命令复跑取真值。

## 1. 复跑命令

```bash
dotnet run -c Release --project benchmarks/TC.Tier.Core.Benchmarks -- --filter "*AsyncPrimitivesBench*"
# 同理：*AsyncQueueBench* / *OverflowPoolBench* / *ClockCacheBench* / *SyncModesBench*（同步模式非争用）
# 并发伸缩/写偏向落地（非 BDN，独立探针）：--sync-probe [秒]
# （★ BDN 0.13.12 单个 --filter 不支持 '|' 多模式——逐类跑或多次 --filter）
```

## 2. 环境

2026-08-17 ｜ Linux 6.8（Ubuntu）｜ AMD Ryzen 9 6900HX（8C/16T）｜ .NET 8.0.420 ｜ BenchmarkDotNet 0.13.12（warmup 3 / iteration 5）

## 3. 异步原语（AsyncPrimitivesBench，基线=SemaphoreSlim 等价用法）

| 操作 | 本组件 | SemaphoreSlim 基线 | 结论 |
|---|---:|---:|---|
| `AsyncManualResetEvent.WaitAsync`（已 set 快路径） | **15.2 ns，0 分配** | 25.7 ns | **1.7× 快**——非 async 快路径直接返回，不进状态机 |
| `AsyncMRE` Set+Wait 周期（已 set 快路径往返） | **18.3 ns，0 分配** | 25.9 ns | **1.4× 快**（修前 66.7ns——Set 曾无条件 Monitor.PulseAll，现已按需） |
| `AsyncMRE` 真实唤醒 1:1（挂起 waiter，**默认异步调度**） | 843 ns / 333 B | **37.3 ns** / 88 B | ⚠️ 默认模式为"Set 可持锁"安全语义付一次线程池往返（状态机装箱 + 调度）——本机首测真实唤醒数字 |
| `AsyncMRE` 真实唤醒 1:1（**内联模式** opt-in） | **93.1 ns / 80 B** | 37.3 ns / 88 B | 2.5× 慢但**分配少 8 B**；内联模式限制 Set 调用点不持锁（持 SpinLock 会自死锁）——单槽快路径 + GetResult 清理钩子零装箱 |
| `PooledValueTaskSource` Rent+SetResult+Return | 42.5 ns，0 分配 | —（底座自证） | 池化闭环纳秒级、零分配 |
| 广播 N=1 / 4 / 16 waiter（µs 级） | 1.96 / 3.88 / 10.6 µs | 1.85 / 3.73 / 11.2 µs | 多 waiter 广播两者**相当**，分配相近 |
| `AsyncCountDown` Add+Wait+Remove 稳态（默认 / 内联） | 846 / **135 ns**，341 / 112 B | — | 原 1005ns/430B 的构成实测是**唤醒调度**（默认线程池往返），非构造——旧基准每轮 `new`+`.AsTask()` 的构造成本仅 ~30ns，原文档归因已修正；纯计数 Add/Remove 仍是 Interlocked 纳秒级 |

> ★ 2026-08-17 变更（#PERF-002）：① `Set()` 按需 Pulse（`_syncWaiters` 计数，异步场景免白付）；② 单槽快路径
> + 内联唤醒模式（`runContinuationsAsynchronously: false`，构造参数 opt-in）；③ 完成先于注册安全协议
> （`MarkOrComplete`/`MarkOrFault`）——修复了 Set/取消与注册竞态在 `ManualResetValueTaskSourceCore`
> CompletionSentinel 上的进程级崩溃类（回归测试：`SetVsRegistrationRace_NoCompletionSentinelCrash` 等）。

## 4. AsyncQueue（AsyncQueueBench，基线=SemaphoreSlim+ConcurrentQueue 版）

| 操作（ItemCount=100） | AsyncQueue | 基线 | 结论 |
|---|---:|---:|---|
| Enq+Deq（item ready） | **74.8 ns** | 98.9 ns | 1.3× 快 |
| 串行生产-消费 100 项 | **7.76 µs / 2.0 KB** | 12.06 µs / 8.4 KB | **1.55× 快 + 分配 4.2× 少**——稳态循环优势最大 |
| 空队等待→唤醒（1:1） | 1.57 µs / 1.1 KB | **1.39 µs** / 1.0 KB | 真实唤醒路径基线略快（同 MRE 结论）；差距小 |

## 5. OverflowPool（OverflowPoolBench，PoolSize=64/256 两组结果一致）

| 操作 | 耗时 | 分配 |
|---|---:|---:|
| TryAdd+TryGet 命中往返 | **16.7 ns** | 0 |
| TryGet 空（miss） | 11.4 ns | 0 |
| 串行 fill+drain 1000 项 | 16.8 µs（~17 ns/项） | 0 |
| 溢出路径（TryAdd 超容量，disposer 回收） | 6.9 µs/1000 | 0 |
| 并发生产-消费 1000 项 | 145.6 µs（~146 ns/项，含跨核同步） | 576 B |

结论：命中/未命中/吞吐全零分配、纳秒~十几纳秒级——轻量池目标完全达成；溢出路径有 disposer 调用但依旧 ~7 ns/项。

## 6. ClockCache / ClockCacheV2（ClockCacheBench，基线=ConcurrentDictionary+LinkedList LRU）

⚠️ 本节"每 op"= 表值 ÷ 循环项数（基准整循环跑 N=Capacity/100 个 key）。

> ★ 2026-08-17：新增 `ClockCacheV2`（组相联），与 V1（开放寻址）并列。两个组件同台对照：
> V1 甜区 = 铁律配置（容量 ≥2× 工作集）下极致热路径；V2 = 任意负载延迟恒定。

### 6.1 ClockCache V1（开放寻址，基线对照）

| 操作 | V1 每 op | 基线每 op | 结论 |
|---|---:|---:|---|
| TryGet **命中**（128 / 1024 容量） | **3.6 / 3.7 ns** | 29.6 / 29.7 ns | **~8× 快**，零分配、无锁读 |
| Put **更新**（128 / 1024） | **2.8 / 2.9 ns，0 分配** | 58.1 / 53.7 ns，7 KB GC | **~19× 快 + 零分配**（基线每次 LinkedList 摘链+挂链） |
| TryGet **未命中**（128 / 1024） | 90.5 / **668.7 ns** | **11.8 / 12.2 ns** | ⚠️ **miss 显著慢**——满载开放寻址探测链 + 随机访存；容量越大越贵 |
| Put 驱逐（单 op） | 152.1 / **996.4 ns** / 48 B | **119.6 / 129.4 ns** / 152 B | ⚠️ ★ 已修冗余全表扫描（修前 1248 ns，修复后 785~996 ns——多次复测波动）；剩余 = 满载下存在性检查全表扫描 + 时钟扫描，仍 ~6-8× 慢于基线 |
| 混合 80% 命中（128 / 1024） | 35.6 µs / **181.9 µs**（每千次） | 35.2 / 37.4 µs | ⚠️ 128 容量持平；**1024 容量混合落后 4.9×**——miss 路径拖垮 |

**负载因子曲线（探针实测，capacity=1024）**：miss 随负载非线性发散——50% → **49 ns**、75% → 110 ns、
90% → 462 ns、100% → 681 ns。⚠️ 触发悬崖的是**负载因子（工作集/容量）**而非命中率：80% 命中率 +
容量 ≥ 2× 工作集时 miss 仍是 49 ns，无悬崖。基准的 miss/混合行是在 100% 满载下测的，对"铁律配置"不公平。

### 6.2 ClockCacheV2（组相联，sets × 8 ways）

| 操作 | V2 每 op | 基线每 op | 结论 |
|---|---:|---:|---|
| TryGet **命中**（128 / 1024 容量） | **5.0 / 5.1 ns** | 29.6 / 29.7 ns | **5.8~5.9× 快**，零分配、无锁读 |
| Put **更新**（128 / 1024） | **18.6 / 17.7 ns，0 分配** | 58.1 / 53.7 ns，7 KB GC | **~3× 快 + 零分配** |
| TryGet **未命中**（128 / 1024） | **7.0 / 7.0 ns** | **11.8 / 12.2 ns** | **反超基线 1.7×**——任意负载恒定 8 路扫描，无探测链 |
| Put 驱逐（单 op） | **66.5 / 67.7 ns** / 48 B | **119.6 / 129.4 ns** / 152 B | **反超基线 ~1.8×**——组内 CLOCK ≤16 次操作，无全表扫描 |
| 混合 80% 命中（128 / 1024） | **12.5 / 12.5 µs**（每千次） | 35.2 / 37.4 µs | **反超基线 ~3×**（V1 满载时 181.9 µs 落后 4.9×） |

**V1 → V2 对比（同机同基准）**：miss 90.5/668.7 ns → **7.0/7.0 ns（~95×）**；驱逐 152.1/996.4 ns →
**66.5/67.7 ns（~15×）**；混合 35.6/181.9 → **12.5/12.5 µs/千次（~14.6×）**。代价：hit 3.7 → 5.0 ns
（fmix 终混 + 固定 8 路扫描，仍 5.9× 快于基线）、update 2.9 → 17.7 ns（仍 3× 快于基线、达标 spec ≤50ns）。

### 6.3 选型结论

| 场景 | 选谁 |
|---|---|
| 工作集已知且容量 ≥ 2× 工作集可保证、热路径极致（hit/update 最高频） | **ClockCache（V1）**：hit 3.7 / update 2.9ns 无出其右 |
| 容量接近/等于工作集、命中率不可控、miss 延迟不可发散、Core 库对外默认推荐 | **ClockCacheV2**：全路径反超基线、任意负载恒定 |
| V2 注意事项 | 组偏斜会提前淘汰（均匀负载实际驻留 ≈ 容量 85-90%，`ways=16` 可减半损失）；调 `ways`（默认 8）适配负载 |

Ring/Metadata 当前沿用 V1（miss 代价被 I/O 遮盖，热路径 hit 更快）。

## 7. 同步模式（SyncModesBench 非争用 + --sync-probe 并发伸缩，2026-08-20）

选型指南主表见 [`../locking-and-epoch.md`](../locking-and-epoch.md) §0.2。本节存档原始数字。

### 7.1 非争用（BDN ShortRun，每方法 100 次循环——表值为整循环，每 op = ÷100）

| 方法（100 次循环） | 整循环 | **每 op** | 分配 |
|---|---:|---:|---:|
| SpinRWLock 共享 Acquire+Release | 1,627 ns | **16.3 ns** | 0 |
| SpinRWLock 排他 Acquire+Release | 2,609 ns | **26.1 ns** | 0 |
| SpinRWLock TryShared（成功） | 1,636 ns | 16.4 ns | 0 |
| SpinRWLock TryExclusive（成功） | 1,700 ns | 17.0 ns | 0 |
| Monitor lock | 1,677 ns | 16.8 ns | 0 |
| RWLS 读 | 1,789 ns | 17.9 ns | 0 |
| RWLS 写 | 2,002 ns | 20.0 ns | 0 |
| LightEpoch Resume+Suspend | 957 ns | **9.6 ns** | 0 |
| COW 快照读（volatile 引用+5 标量拷贝） | 70.5 ns | **0.7 ns** | 0 |
| COW 快照发布（new 5 标量对象+volatile 写） | 499.8 ns | 5.0 ns | 48 B/op |
| seqlock 读（版本双读+复验） | 60.1 ns | **0.6 ns** | 0 |
| seqlock 写（奇偶翻转+2 字段） | 56.2 ns | 0.6 ns | 0 |

结论：①共享锁进出对与 Monitor 相当、比 epoch 贵 1.7×（两次 interlocked 打同一字 vs 本线程行两次普通写）；
②排他比共享贵 60%（进门 Or pending + 释放清双位，三次 locked op——写偏向的固定代价）；
③COW/seqlock 读比任何锁便宜 **20-50×**——热读路径选型的硬数字；④COW 写侧 48B/op 分配——高频发布会产 GC 压力，
写频繁的场景选 seqlock/原地 CAS 而非 COW。

### 7.2 并发伸缩（--sync-probe 1.5s × 3 轮取中位，同机）

读者伸缩（N 线程纯读锤击聚合吞吐 ops/s，×n = 相对 1T 倍数）：

| 模式 | 1T | 2T | 4T | 6T |
|---|---:|---:|---:|---:|
| SpinRWLock 共享 | 89.7M | 44.3M（×0.5） | 20.5M（×0.2） | 18.6M（×0.2） |
| **LightEpoch 周期** | 167.5M | 301.3M（×1.8） | 527.0M（×3.1） | **787.7M（×4.7）** |
| Monitor | 90.8M | 71.3M（×0.8） | 22.9M（×0.3） | 20.3M（×0.2） |
| RWLS 读 | 70.0M | 67.2M（×1.0） | 58.8M（×0.8） | 52.1M（×0.7） |

结论：**单字共享锁（SpinRWLock/Monitor）聚合吞吐随并发反降**（缓存行乒乓主导，6T 落到 1T 的 20%）；
LightEpoch 每线程独占缓存行，**近线性伸缩，6T 聚合 = SpinRWLock 的 42×**——"热读路径不该用共享锁"的实测铁证；
RWLS 内部读者计数聚拢，伸缩缓降但仍远逊 epoch。SpinRWLock 共享的正确定位：**低并发协调 + 结构性互斥**
（段锁/Gate/读计划锁），不是高并发读吞吐工具。

写偏向落地（N 读者锤击 + 1 写者排他循环，1.5s）：

| 读者数 | 写者落地次数 | 平均 | 最大 |
|---|---:|---:|---:|
| 1 | 12.0M | **0.1 µs** | 16.4 ms |
| 3 | 10.8M | **0.1 µs** | 16.8 ms |
| 5 | 9.0M | **0.1 µs** | 16.0 ms |
| 11 | 7.6M | **0.1 µs** | 16.7 ms |

结论：写偏向兑现——pending 挡新读者后写者只等在途读者，**平均落地 0.1µs 且不随读者数恶化**（读优先语义下
此数字无界，LockWord 时代 ChaseCompaction/ConcurrentReadWrite 的饿死形态即源于此）；max ~16ms 为 OS
调度毛刺（SpinWait 退化 Yield/Sleep 的定时器量子），非锁协议成本。

## 8. 汇总一页

| 组件 | 甜区 | 已知短板（选型时看这列） |
|---|---|---|
| `AsyncManualResetEvent` | 已 set 快路径 15.2ns（反超基线）/ 广播相当 / 内联真实唤醒 93ns | 默认模式真实唤醒 843ns（Set 可持锁的安全语义付线程池往返）；内联模式限制 Set 不持锁 |
| `AsyncCountDown` | 纯计数（Interlocked）；内联稳态 135ns | 默认稳态 846ns（唤醒调度）；等待注册有分配（等待者少/静态等待场景无所谓） |
| `AsyncQueue<T>` | 稳态生产-消费（1.55× 快、分配 4.2× 少） | 空转→唤醒路径基线略快 |
| `PooledValueTaskSource` | 31ns 零分配闭环（底座） | —（自定义原语才直用） |
| `OverflowPool<T>` | 全路径零分配、命中 17ns | 溢出走 disposer（仍 ~7ns/项） |
| `ClockCache`（V1） | 铁律配置下极致热路径（hit 3.7ns / update 2.9ns、0 分配） | **miss 探测链**：满载 681ns@1024、驱逐 785ns——须守"容量 ≥2× 工作集"铁律 |
| `ClockCacheV2` | 任意负载恒定（hit 5 / miss 7 / 驱逐 70ns）、全路径反超基线 | 组偏斜损失 ~10-15% 有效容量；hit/update 比 V1 略慢 |
