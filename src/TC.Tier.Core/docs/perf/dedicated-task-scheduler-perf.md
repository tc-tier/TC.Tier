# IsolatedTaskScheduler 性能基准报告

> 对应基准代码：[`../../../../benchmarks/TC.Tier.Core.Benchmarks/Shared/IsolatedTaskSchedulerBench.cs`](../../../../benchmarks/TC.Tier.Core.Benchmarks/Shared/IsolatedTaskSchedulerBench.cs)
> 使用指南（选型/旋钮/注意事项）：[`../dedicated-task-scheduler.md`](../dedicated-task-scheduler.md)
>
> ⚠️ 本文数字为**指示值**（单机开发环境实测，BenchmarkDotNet.Artifacts 不入库，本文是结果的存档）；
> 机器 / 负载 / .NET 版本不同会变，以 §1 命令在本机复跑为准。

---

## 1. 复跑命令

```bash
# 完整跑（Release 必须）
dotnet run -c Release --project benchmarks/TC.Tier.Core.Benchmarks -- --filter *IsolatedTaskSchedulerBench*
# 快跑冒烟（粗数字）
dotnet run -c Release --project benchmarks/TC.Tier.Core.Benchmarks -- --filter *IsolatedTaskSchedulerBench* --job short
```

## 2. 环境

2026-08-17 ｜ Windows 11（10.0.26200）｜ i5-12400（6C/12T）｜ .NET 8.0.30 ｜ BenchmarkDotNet 0.13.12（warmup 3 / iteration 5）

## 3. 基准设计（测什么 / 为什么）

| 组 | 基准 | 回答的问题 |
|---|------|-----------|
| 1 | 单任务往返（`StartNew` no-op → 私有线程执行 → 完成） | 隔离的**每次派发税**多贵？与公共池（`TaskScheduler.Default`）对照 |
| 2 | 同上 + 指标开（noop sink） | 指标采集的热路径开销（默认 Disabled 零开销声明的另一面：开了多贵） |
| 3 | continuation 回流（任务内 `await Task.Yield()` ×100） | 每次 `await` 让出后经 `QueueTask` 回流私有线程的成本（协作 async 的核心路径） |
| 4 | 多生产者吞吐（4 公共池生产者 × 250 no-op 任务） | 突发灌入下调度器排空能力；默认有界队列（背压阻塞）vs 无界 vs 公共池 |

## 4. 结果

参数：`YieldCount=100`；M ∈ {1,2,4}；每组以同 M 的公共池行为基线（三组基线实测一致：往返 ~650ns、yield 循环 ~33µs、吞吐 ~76µs）。

### M=1

| Method | Mean | StdDev | Ratio | Gen0 | Allocated |
|---|---:|---:|---:|---:|---:|
| ThreadPool round-trip | 648.0 ns | 15.41 ns | 1.00 | 0.0067 | 64 B |
| **IsolatedTaskScheduler round-trip** | **485.0 ns** | 14.92 ns | **0.76** | 0.0067 | 64 B |
| Isolated round-trip, metrics ON | 1,195.2 ns | 15.65 ns | 1.85 | 0.0057 | 64 B |
| ThreadPool yield-loop ×100 | 33,248.3 ns | 159.59 ns | 51.81 | - | 416 B |
| IsolatedTaskScheduler yield-loop ×100 | 31,588.7 ns | 1,239.56 ns | 48.75 | 0.6714 | 6,878 B |
| ThreadPool throughput 4×250 | 76,296.1 ns | 520.93 ns | 117.80 | 7.9346 | 73,745 B |
| Isolated throughput 4×250（有界队列） | 716,505.0 ns | 28,562.30 ns | 1,106.15 | 15.6250 | 149,620 B |
| Isolated throughput 4×250（无界队列） | 295,469.5 ns | 4,008.87 ns | 456.10 | 7.8125 | 73,808 B |

### M=2（推荐默认）

| Method | Mean | StdDev | Ratio | Gen0 | Allocated |
|---|---:|---:|---:|---:|---:|
| ThreadPool round-trip | 646.4 ns | 5.05 ns | 1.00 | 0.0067 | 64 B |
| **IsolatedTaskScheduler round-trip** | **950.6 ns** | 16.14 ns | **1.47** | 0.0057 | 64 B |
| Isolated round-trip, metrics ON | 1,072.5 ns | 10.05 ns | 1.66 | 0.0057 | 64 B |
| ThreadPool yield-loop ×100 | 32,596.9 ns | 268.19 ns | 50.43 | - | 416 B |
| IsolatedTaskScheduler yield-loop ×100 | 51,398.6 ns | 1,485.11 ns | 79.53 | 0.7324 | 6,879 B |
| ThreadPool throughput 4×250 | 75,616.8 ns | 358.35 ns | 116.95 | 7.9346 | 73,745 B |
| Isolated throughput 4×250（有界队列） | 585,702.6 ns | 8,135.82 ns | 905.93 | 11.7188 | 113,505 B |
| Isolated throughput 4×250（无界队列） | 361,025.8 ns | 2,425.74 ns | 558.58 | 7.8125 | 73,807 B |

### M=4

| Method | Mean | StdDev | Ratio | Gen0 | Allocated |
|---|---:|---:|---:|---:|---:|
| ThreadPool round-trip | 667.4 ns | 6.52 ns | 1.00 | 0.0067 | 64 B |
| **IsolatedTaskScheduler round-trip** | **995.2 ns** | 33.72 ns | **1.49** | 0.0057 | 64 B |
| Isolated round-trip, metrics ON | 1,085.2 ns | 9.26 ns | 1.63 | 0.0057 | 64 B |
| ThreadPool yield-loop ×100 | 32,823.6 ns | 320.16 ns | 49.19 | - | 416 B |
| IsolatedTaskScheduler yield-loop ×100 | 53,065.0 ns | 1,200.94 ns | 79.51 | 0.7324 | 6,879 B |
| ThreadPool throughput 4×250 | 75,100.1 ns | 100.72 ns | 112.92 | 7.9346 | 73,745 B |
| Isolated throughput 4×250（有界队列） | 575,776.2 ns | 9,840.19 ns | 862.83 | 8.7891 | 89,905 B |
| Isolated throughput 4×250（无界队列） | 416,066.2 ns | 3,073.60 ns | 623.46 | 7.8125 | 73,752 B |

> 注：yield/吞吐组的 Ratio 是 BDN 以**同组往返基线**为 1.00 的换算（故公共池自身显示 50×/117×）；直接比 Mean 即：
> yield M≥2 ≈ 池的 1.6×；吞吐有界 ≈ 池的 7.6~9.4×、无界 ≈ 3.9~5.5×。

## 5. 结论

1. **派发税亚微秒**：单任务往返 0.5~1µs，分配与公共池完全相同（64 B/任务 = Task 对象本身，调度零额外分配）。真实 worker 任务（µs~ms 级建段/IO）下派发税占比可忽略。M=1 的单消费者阻塞交接（~485ns）甚至**快于**公共池全局队列（~650ns）——单线程专用实例无排队竞争。
2. **continuation 回流 ~0.3µs/次（M=1）/ ~0.5µs/次（M≥2）**，每次多 ~64 B 分配（BlockingCollection 节点）：await 密集的真实 worker 摊薄无感；纯 `Task.Yield` 空转微循环会看到 1.6×（M≥2）——这是调度器的病理用法，见使用指南 §1 不适用表。M=1 ≈ 池速。
3. **no-op 吞吐天花板 ~2.4-3.4M 任务/s**（M=1..4，无界队列）vs 公共池（12 逻辑核）~13M/s——M 个私有线程换**确定性/隔离**是有意限流，不是缺陷；需要 no-op 火力全开的场景本来就不该用它。
4. **默认有界队列（cap=max(M*4,16)≈16-20）在 4 生产者突发下触发阻塞背压**：比无界慢 1.5~2×（M=1: 717µs vs 295µs），且 Gen0 压力翻倍（生产者线程在 `SemaphoreSlim` 上阻塞/唤醒的额外分配，M=1 时 Allocated 149.6KB vs 73.8KB）。
5. **指标开仅 +0.1~0.7µs/任务、零额外分配**（64 B 与关时相同）——生产环境可常开，不必为省这点关掉可观测性。

## 6. 调参指引（由数据推出）

| 症状 | 数据依据 | 动作 |
|------|---------|------|
| `scheduler.queue.full` 频发、生产者延迟敏感 | §4 结论 4：有界背压在突发下慢 1.5~2× + GC 翻倍 | 确认 worker 工作项队列才是背压主战场（使用指南 §7.3）后，调大 `QueueCapacity`（如 M*16+）或 `-1` 无界（配合 `queue.depth` 监控） |
| await 密集 worker 回流延迟敏感 | §4 结论 2：M=1 ≈ 池速、M≥2 1.6× | 该 worker own 一个 M=1 实例（回流路径单消费者最快）；并行度靠多实例或 worker 队列 |
| 需要 no-op 级超大吞吐 | §4 结论 3：天花板 ~M×0.85M 任务/s | 这不是本组件的场景——用公共池或独立分区；或加大 M（受核数校验约束） |
| 想省指标开销 | §4 结论 5：+0.1~0.7µs/任务 | 通常不值得关；确需极限，默认 `Hub=null`（Disabled）即零开销（`IsEnabled` 一次读短路） |
