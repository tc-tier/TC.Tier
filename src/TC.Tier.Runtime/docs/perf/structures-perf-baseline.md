# Structures 性能基线（七子体系全景）

> **环境口径（★三机数字绝对值不可比，只看各自机内相对结论）**：
>
> | 环境 | 机器 | 介质 | 覆盖章节 |
> |---|---|---|---|
> | **A** | Windows 10，i7-10510U 4c/8t，SATA SSD | 真盘 DIO/Buffered | §2 Ring、§4 Log（Win 列）、§5 vs FASTER |
> | **B** | i5-12400，**mem 介质**（引擎 IO 噪声为零） | 内存 | §3 索引+组合（细账见 [kv-composition.md](kv-composition.md)） |
> | **C** | Ubuntu 24.04，Ryzen 9 6900HX 12c，NVMe | 真盘 DIO | §4 Log（Linux 列） |
>
> 引擎底盘（lease/段表/CAS 原语）基线另见 [storage-engine-perf-baseline.md](storage-engine-perf-baseline.md)；
> KV 组合完整细账（含四变体/坑档/优化空间）见 [kv-composition.md](kv-composition.md)，本文收口全子体系。
> BDN artifact 落 `bm-results/`——数字进文档必须能溯源（methodology 纪律）。

---

## 1. 基准清单（benchmarks/TC.Tier.Runtime.Benchmarks）

**现役**（当前架构，持续维护）：

| 组 | 文件 | 测什么 |
|---|---|---|
| Kv | KvCompositionBench / KvRecoveryBench | 组合点查/写、恢复（全量重放 vs 镜像载像） |
| Kv | FasterHotReadBench | FASTER 同形热读四变体对照（含 94.67ns 批口径） |
| Kv | MirrorProbe / ReplayAllocProbe（探针） | 镜像恢复阶段分解 / 重放·写路径分配分解 |
| Storage | 引擎/段表/lease 基准 | 见 storage-engine-perf-baseline.md |

```bash
dotnet run -c Release --project benchmarks/TC.Tier.Runtime.Benchmarks -- --filter "*KvComposition*"
dotnet run -c Release --project benchmarks/TC.Tier.Runtime.Benchmarks -- --filter "*KvRecovery*"
# 探针：--replay-alloc-probe / --mirror-probe
```

**历史注记**：§2/§4/§5 的 Ring/Log/vs-FASTER 单族数字出自旧架构基准套件
（Structures/{Index,Log,Ring} 40 文件——前泛型 API，2026-08-22 随缺口收尾整目录清理：
对当前类型不可编译，其历史数字由归档报告与本文收口保留）。当前架构的索引/Ring 基准面
= Kv 组合套件（§3）；需要单族微基准时按 KvCompositionBench 形态新写，勿复活旧套件。

---

## 2. Ring（环境 A，2026-08-02）

### 2.1 写吞吐与延迟

| 项 | 数字 | 备注 |
|---|---:|---|
| 稳态写 1M ops | **1.86M ops/s** | 较旧基线 +95% |
| 长稳 10M ops | **2.01M ops/s** | GC 0/0/0；CPU mean 14.6%、RSS 24MB |
| 4KB record 写 | 512-625 MB/s | **打满 SATA 带宽**；256B 490 MB/s |
| 写延迟 | 64B 192ns / 256B 299ns / 1KB 711ns / 4KB 5.7µs | |
| 并发写 | 1T 370K → 2T 986K（2.7×）→ 4T 1.32M（3.6×）→ 8T 745K | 8T 回落=4 核硬件上限；p99 < 3.2µs |

### 2.2 读

| 项 | 数字 | 备注 |
|---|---:|---|
| GetRecord 热路径 | **10.5ns**（Buffered）/ 11.7ns（DIO） | 优化前 22.6ns（2.2×） |
| 冷页读（PageBits16） | 26.7µs vs 热 38.9ns | 冷热差 ~700×——真实性能由冷读决定 |
| 32MB 页冷 | 17.2ms | 页越大冷读越贵（回源粒度） |
| 1MB 页冷读 DIO | 413µs ≈ 热读 Buffered 376µs | 大页下冷热接近 |
| 部分页冷（ColdReadRatio=0） | AllCold 58.7µs vs 默认 0.25 的 232.5µs | 页内未填充区回源概率旋钮生效 |

### 2.3 溢出（WiscKey 形态）与恢复

| 项 | 数字 |
|---|---:|
| 内联值 | 1.36µs |
| 溢出值 | 59.7-63.8µs（独立溢出引擎读） |
| Managed meta O(1) 恢复 | 9→7→11ms（100MB→1GB 恒定） |
| Embedded 倒扫恢复 | 6→7→133ms（1GB 从 1076ms 优化到 133ms） |
| 1GB 写入 | 1.5-2.0s（旧 4.8s，~2×） |

---

## 3. 索引 + KV 组合（环境 B，2026-08-22；细账 kv-composition.md）

### 3.1 点查（index.Find + Ring.GetValue 两段合口径，100k 热区乱序）

| 索引族 | Mean | 备注 |
|---|---:|---|
| Hash | **161 ns** | 零分配；容量自适应附带红利（旧 191-222ns） |
| BTree | **~350 ns** | 零分配；扁平缓存+引擎读快路径+生长模式三段累计 5.3×（基线 1.93µs） |
| SkipList | **~816-850 ns** | 零分配；arena 化（旧 2.22µs，~2.2×），工作集 28.8→6.5MB 全 L3 |

写（含每迭代全新组合装配冷启动口径）：Hash 2.9-3.6µs / BTree 5.6-24.9µs（分配列跨线程归属
噪声见 kv-composition 坑档；同线程探针 621B/插）/ SkipList 3.8-4.6µs（363B/插，arena 零托管税）。

### 3.2 vs FASTER 同形热读（FasterHotReadBench 四变体，同机同轮）

| 变体 | ns/op |
|---|---:|
| FASTER 2.6.5 session.Read（100k×8B key+64B struct） | 126.96 |
| 本组合逐次 Find+GetValue | 158.2（1.25×） |
| scoped+span 逐次 | 167.9（**勿用**——每迭代 4 次 epoch 转换不划算） |
| **scoped+span 批量（256 查/invocation）** | **94.67（0.75×）——反超 FASTER 1.34× 并破百** |

批量反超来源：零拷贝（FASTER 付 64B 出参拷贝）+ 无逐 op session 机器 + epoch 摊薄。
FASTER 单线程随机热读在本机真实水位 = 133.4ns（逐次口径对照 158.4ns，差 1.19×/25ns）。

### 3.3 恢复（跨实例重开 + 50k 条）

| 索引族 | 全量重放 | 镜像载像（零增量） |
|---|---:|---:|
| Hash | 67-87 ms | 64.7 ms |
| BTree | 106-130 ms | 58.7 ms（2.2×） |
| SkipList | 93-132 ms | 49.0 ms（2.1×） |

镜像真收益 = W 距尾近的增量场景（重放量 ∝ 距尾距离，非数据总量）；Hash 全量重放分配已压到
**2.7MB（54B/条，-98.4%）**（容量自适应销案，旧 167.9MB 构造期常量）。

### 3.4 组合分解（Stopwatch 探针，100k 稳态，Hash 组合）

Ring.Write ~906ns ｜ HashIndex.Insert ~904ns ｜ Ring.GetValue ~74ns。

---

## 4. Log（环境 A Win / 环境 C Linux）

### 4.1 写吞吐（EntryLog 4KB payload 攒页）

| 配置 | Win (A) | Linux (C) |
|---|---:|---:|
| **DIO+WriteThrough 4M（生产推荐）** | **590 MB/s** | **1,469 MB/s** |
| DIO+WT 1M | 520 MB/s | 1,071 MB/s |
| DIO+None 4M | 630 MB/s | — |
| 达磁盘裸写 | 69% | 61% |

损耗 31-39% = CRC32C + 帧组帧 + padding。Log 攒页比引擎逐条 Append 快 6.8×（Buf+None）/ 33.8×
（Buf+WT）——攒页摊薄 lease+CAS+syscall。

### 4.2 小 payload WAL（DIO+WT 4M，真实业务形态）

| payload | 模式 | ops/s | p50 | p99 | p999 |
|---|---|---:|---:|---:|---:|
| 128B | 攒页 | **903K** | 0.4µs | 1.2µs | 15µs |
| 128B | 默认 gc(10ms) | 348K | 0.4µs | 0.6µs | 603µs |
| 128B | 单条强制 | 2,172 | 375µs | 1,041µs | 1,489µs |

攒页 vs 单条强制 = **416×**（group commit 用崩溃窗口换吞吐）；WAL 尾延迟 SLA 以默认 gc 的
p999（~1ms）为预算基线。写放大：128B 1.125× / 4KB 1.004×；fsync/MB：攒页 1 次 vs 单条 4,096 次。

### 4.3 回放 / 恢复 / 截断 / 提交

| 项 | 数字 | 备注 |
|---|---:|---|
| 回放（128B 无 CRC） | **10.96M entries/s**（1,338 MB/s） | CRC 开关 ~2× 差（128B+CRC 3.56M/s） |
| 回放冷读（DIO，= 崩溃重启场景） | 4KB 冷热比 0.99-1.10×；128B+CRC 冷读 +35%（仍 <0.4µs/条） | 恢复回放不构成瓶颈 |
| 恢复（meta Miss 二分定位） | 4MB 47ms → 64MB 60ms | O(log N)：数据 16× 恢复仅 +28% |
| CommitAsync 显式提交 | ~1.4-2.2ms（三 meta 策略同量级） | 受限同一次数据 flush；事务 SLA 预算 ~2ms |
| TruncatePrefix | ~1ms（纯打洞）→ 8.1ms（1GB 删 8 段+打洞） | 字节级物理销毁实测；**与 Append 完全并行** |
| 10 分钟长稳 | avg 102.5 MB/s / 42 万 ops/s；p99=2.4µs、p999=788µs（周期 flush 尖峰） | Gen2 仅 3 次；RSS 稳定 29MB 净增 0 |

### 4.4 并发（Channel 单写线程模型）

1/2/4/8 生产者吞吐 172K-205K ops/s 稳定，p99 全部 ≤1.3µs——单写线程串行化消除锁竞争尾延迟，
p999 周期尖峰（470-810µs）与 group commit flush 同源。

---

## 5. 全套 KV vs FASTER（环境 A，2026-08-04，FullKvVsFasterBench）

| 场景 | 本方 | FASTER | 倍数 |
|---|---:|---:|---:|
| 热区点查 KeyOnly（16B，预存 addr） | 4.4ns | 61.8ns | ~7×（口径含预存地址，FASTER 每次过 hash） |
| 热区点查 ValueOnly / KeyValue | 5.2 / 8.7ns | — | |
| 冷区点查 | 14.7µs | 63.4µs | ~4× |
| 扫描 L1 raw byte（10k 条） | **0.95ns/条** | 无此能力 | 地址流裸读形态 |
| 扫描 L2 地址流 / L3 key(+value) | 23.9 / 28.9-29.0ns | 15.7ns | 默认顺序游标慢于 FASTER 内置 scan——批形态见 §3.2 |
| 值上限 | Segment 1GB + overflow | record ≤ PageSize（不跨页） | 架构差异 |

架构面：地址一等公民可缓存复用跳 hash（FASTER 地址 internal 锁死）；游标可注入。
冷读是热读的 ~2800×——真实性能由冷读决定。

---

## 6. Metadata / Mirror / Snapshot

- **meta 三策略 CommitAsync**：Disabled/Embedded/Managed 均 ~1.4-2.2ms（§4.3 表），策略差异在
  噪声内——meta 开销不构成提交瓶颈；Recover：Embedded≈Disabled（同页扫）< Managed（+meta
  引擎重开 ~20-40ms）。
- **镜像恢复底盘**（环境 B）：零增量载像 49-65ms 封顶于恢复共同底盘（主/镜像双引擎重开+载像
  IO），优化空间（双 join 并行、Hash 载像免拷贝、CRC 硬件加速）合计可望再 -20-30ms——见
  kv-composition.md §优化空间。
- **Snapshot**：无专属基准（测试覆盖 12 例）；帧 IO 量级与 Log 回放同形（§4.3 冷读结论适用）。

---

## 7. 已修复的性能坑（防复发，全档见 kv-composition.md）

- 索引节点缓存字典形态 → `LogicalAddressMap` 扁平原语（BTree 点查 1.93µs→812ns 起步）。
- 引擎读协议每读 ~0.5KB 堆分配 → 单段零分配快路径（812ns→361-452ns，分配清零）。
- SkipList 纪元式层分配（早区链稀疏 386 跳/Find）→ 几何分布解耦（15.8µs→2.22µs）+ arena 化
  （→816ns）。
- Hash 恢复 334MB 构造期常量 → GrowIndex 容量自适应（167.9→2.7MB）。
- 写基准计时粒度假象 → OperationsPerInvoke=256 批量摊平；分配论断用同线程探针复核。

## 8. 优化空间（未做，按预期收益排）

1. 恢复共同底盘 -20-30ms（§6）；
2. Ring 写路径 906ns 分解（CRC/页池/水位 CAS）——组提交摊薄，引擎架构项；
3. SkipList 残差 ~25 跳链追逐本征——要再压需换布局，收益/复杂度比低，暂缓。

## 9. 维护纪律

- 改动 Ring 写/读路径、索引 Find/Insert、重放核心、Log 组提交后**必须重跑对应基准并更新本文
  与 kv-composition.md**（性能论断须实测）。
- 新增子体系基准进 `benchmarks/TC.Tier.Runtime.Benchmarks`（探针记得在 Program.cs 接线）。
- 三机口径纪律：跨机数字只作量级参考，同机同轮才是对照（换机绝对值不可比）。
