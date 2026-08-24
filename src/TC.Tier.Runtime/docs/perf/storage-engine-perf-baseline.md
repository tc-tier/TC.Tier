# StorageEngine / 段表 性能基线（Linux）

> **日期**：2026-08-21　**环境**：Ubuntu 24.04（Linux 6.8，ext4 on /tmp），AMD Ryzen 9 6900HX 12C/12T，.NET 8.0.26，BenchmarkDotNet 0.13.12
> **性质**：绝对值随硬件浮动，**相对比较（比值列）是稳定结论**。
> **介质定位**：mem 不只用于测试——它是**极速运行时**介质（Append ~0.5 µs/op，磁盘 2.75 µs），
> 数据可随时经 `RootSpaceImage.Capture/Restore/Transfer` 导出镜像或转化为任意介质（磁盘/Raw/云，
> 见 Core io.md §10）。本文 mem 数据同时是"极速模式的生产基线"与各介质的对照下界。
> **复现**（`benchmarks/TC.Tier.Runtime.Benchmarks`，介质由 `TC_BENCH_FS_SPEC` 切换、零重编译）：
> ```bash
> dotnet build benchmarks/TC.Tier.Runtime.Benchmarks -c Release
> B=benchmarks/TC.Tier.Runtime.Benchmarks/bin/Release/net8.0/TC.Tier.Runtime.Benchmarks.dll
> dotnet $B --filter '*LeaseLayerLatencyBench*'      # 段表/lease 分层延迟（纯内存）
> dotnet $B --filter '*LeaseProtocolBench*'          # lease 协议五动词（Default vs Pooled）
> dotnet $B --filter '*StorageEngineIoBenchmarks*'   # 引擎端到端（mem 缺省）
> TC_BENCH_FS_SPEC=local:///tmp/tc-bench dotnet $B --filter '*NewLocalStorageEngineBench*'   # 真磁盘
> dotnet $B --filter '*SegmentCreationBench*'        # 建段吞吐
> dotnet $B --filter '*SpaceReclaimBench*'           # 回收/Compact
> ```
> **配套**：引擎用法见 `src/TC.Tier.Runtime/docs/storage-engine.md`；段表机制见
> `src/TC.Tier.Runtime/docs/segment-table.md`；lease 协议见 `lease-protocol.md`；
> 底层 IO（fs/介质/DIO/fsync）见 `src/TC.Tier.Core/docs/perf/io-performance.md`。
> **历史**：2026-07-29 Windows i5-12400 旧基线（lease 184B 拆解等设计分析）见 git history——
> 此后地址空间经 V2 重写 + exact-fill 区间统一，旧数字不作同机对照。

---

## 0. 结论速查

**核心场景 = 预分配 + 复写（推荐模式，§2.0）**：`Allocate` 大区一次（无 lease，256MB 仅 ~6 µs）→
址上 `Write` 复写（lease 协议）。Append 只是 WAL 顺序写便利路径（降低水位线管理复杂度）。

| 场景 | 数据支撑 | 结论 |
|------|---------|------|
| **页预留成本** | 一次性 Allocate 256MB = **~6 µs**（纯 CAS 推水位，无 lease） | 页缓冲模型的预留近免费，摊到字节≈0 |
| **核心模式·稳态复写** | 532 ns/op（64B）/ 754 ns（4KB）/ 5.7 µs（64KB，**11.5 GB/s**） | 复写的 lease 协议成本 µs 级（2026-08-21 并发修复批次+微优化后：单线程小包 +6~10% 税，并行/大块持平或更快，见 §6） |
| **核心模式·并行复写** | 4T 不相交区 **2.55× 扩展**（64B 213 ns/op；64KB 20.4 GB/s） | lease 区间所有权给真并行——核心模式受益（扩展比不因修复退化） |
| **复写 vs Append** | 64B 同量级（544 vs 600 ns）；64KB 处 **5.6×**（11.1 vs 2.0 GB/s） | 大粒度下核心模式全面占优；Append 地址只进不退 |
| **✅ 复写·同址反复（已修）** | 单址 10 万次复写**恒 2.3-2.8 µs/op 平稳**（修复前 42→299 µs 线性恶化）；extent 记录恒 1 条（修复前每次 +1 泄漏至 10 万条）；BDN Write_Memory **1.37 s→4.73 ms（290×）** | **段表 extent 泄漏已修**（覆写 Commit 归并被覆盖旧记录，2026-08-21 当日销案）——见 §2.0 |
| 关心 lease 协议的入场费 | 纯尾 CAS 221 ns vs 完整 AppendLease 1.52 µs/op（6.9×，1952 B/op） | 并发安全的代价在 lease 对象与段表占位，不在 CAS 本身 |
| Append（WAL 路径） | mem 511 ns/op（64B） | 日志型补充；端到端被 lease 层主导，内存搬运税 ~3% |
| 磁盘 Append（ext4，512B） | 2.75 µs vs mem 1.1 µs（**2.5×**） | syscall 税显著但可控；换介质零代码改动 |
| **极速吞吐选内存模式** | mem 511 ns vs 磁盘 2.75 µs（**5.4×**，64B）；mem 数据可经 `RootSpaceImage` 导出/转任意介质 | 进程内极速运行时 + 随时落盘自由度——mem 是生产选项，非仅测试 |
| 异步 Append | 比同步 **+23%**（631 vs 511 ns） | 内存介质上 async 纯开销；异步价值在真 IO 介质 |
| 并发 Append（mem，64B） | 4T ≈ 1.09× / 16T ≈ 0.87× 单线程 | **Append 小包并发反扩展**（全局尾 CAS 串行）——要并行用核心模式的分区复写 |
| 纯段表并发（4KB 单元） | 8T 最好 **~2×**（656K→1.30M ops/s），Lock 争用 0→195 | 同上：争用界约 2×；生产路径 IO 占比大，实际扩展更好 |
| 建段成本 | 预备池命中后 Append **零建段等待**；连续建段 ~235 µs/段（mem） | 建段全部在 worker/池后台化，写者不付这笔钱 |
| 回收/Compact | ReclaimHead 整段 122 ms / Compact 127 ms（16MB 段 × 预填） | 威胁操作量级（整段搬迁），同步入口必带 timeout |

---

## 1. 段表 / lease 层（纯内存 `SegmentTable`，handler=null——隔离 IO）

### 1.1 分层延迟（LeaseLayerLatencyBench，单笔与 10K 批均值）

| 层 | 单笔 Mean | 10K 批均摊 | 分配/op | vs 纯 CAS（均摊） |
|---|---------:|----------:|--------:|----------:|
| 1. 纯尾 CAS（Atomic128） | 221 ns | — | 736 B | 1× |
| 2. AllocateLease（CAS+段表+MarkWasted） | 3.83 µs | **443 ns** | 1448 B | 2.0× |
| 3. AppendLease（完整 lease） | 9.44 µs | **1.52 µs** | 1952 B | 6.9× |
| 4. AppendLease.NoCommit（创建+Dispose） | 6.83 µs | 683 ns | 1952 B | 3.1× |
| 6. Batch.AppendLease | — | 1.52 µs | 168 B* | 6.9× |
| 7. Batch.WriteLease（Allocate 大空间+10K 写） | — | **1.08 µs** | 168 B* | 4.9× |
| 8. Batch.Allocate+Write | — | 1.65 µs | 168 B* | 7.5× |

*批模式下 lease 对象复用，均摊分配从 ~2 KB/op 降到 ~168 B/op（ExtentLease 数组摊薄）。

- **单笔 vs 批均摊差 ~6×**：单笔路径含冷缓存/未内联的 lease 构造；稳态消费（上层连续写）取批均摊列。- lease 完整协议 ≈ 1.5 µs/op 是 mem 介质的协议地板——引擎端到端（§2）几乎全部叠在其上。

### 1.2 lease 协议五动词（LeaseProtocolBench，Default vs Pooled 工厂）

| 动词 | Default | Pooled | 分配 |
|---|--------:|-------:|-----:|
| AppendLease.Create+Commit | 6.53 µs | 7.99 µs | 1.21 KB |
| AllocateLease.CAS | 2.10 µs | 2.25 µs | 1.05 KB |
| WriteLease.Overwrite | 6.67 µs | 6.50 µs | 1.21 KB |
| ReclaimLease.PunchHole | 8.00 µs | 8.42 µs | 1.38 KB |
| ReclaimTail+ReAppend | 12.10 µs | 9.73 µs | 1.38 KB |

- **Pooled 与 Default 同量级**（±20% 噪声带内）：池化收益在均摊分配（见 1.1 批模式），不在单笔延迟。
- ReclaimTail 最贵（双尾回退 + 版本 CAS + 段偏移回退三步）。

### 1.3 纯段表并发扩展（4KB 单元 × 10K/线程）

| 线程 | 总耗时 | 吞吐 | vs 1T | Lock 争用 |
|-----:|------:|--------:|------:|----------:|
| 1 | 15.2 ms | 656 K ops/s | 1.0× | 0 |
| 2 | 19.8 ms | 1.01 M ops/s | 1.5× | 0 |
| 4 | 42.2 ms | 948 K ops/s | 1.4× | 20 |
| 8 | 61.6 ms | **1.30 M ops/s** | **2.0×** | 195 |

- **扩展上限 ~2×**：双尾 128-bit CAS 是单一串行点，8 线程已现锁争用（195 次）。
- 这是"纯分配"的最坏视角：生产写路径叠上 IO（syscall/拷贝）后 CAS 占比下降，端到端并发表现更好（§2）。
- 提吞吐的正道是**加大单笔**（摊薄每 op 协议成本），不是加线程。

## 2. 引擎端到端——mem 介质（StorageEngineIoBenchmarks，64B × 10K ops）

### 2.0 核心场景：地址分配 + 复写（页缓冲模型）——推荐模式

**定位（决策）**：预分配+复写是引擎的**推荐核心使用模式**（地址分配无 lease 协议——纯 CAS 推水位；
复写才付 lease 协议成本）；Append 只是降低水位线管理复杂度的 WAL 顺序写便利路径。

**CorePatternBench**（预分配 256MB 区一次 + 址上复写，mem 介质；复现
`dotnet $B --filter '*CorePattern*'`）：

| 路径 | 64B 记录 | 4KB 页 | 64KB 大块 |
|---|--------:|-------:|---------:|
| 一次性 Allocate 256MB | **6.6 µs/区** | 5.8 µs/区 | 6.0 µs/区（摊到字节≈0） |
| 稳态复写（单线程） | **500 ns/op**（2.0M ops/s） | 822 ns/op（1.2M ops/s） | 6.26 µs/op（**10.5 GB/s**） |
| 稳态复写（4T 不相交区） | **198 ns/op（2.5×扩展）** | 327 ns/op（2.5×） | 3.17 µs/op（2.0×，**20.7 GB/s**） |
| Append 对照（同 payload） | 548 ns/op | 5.0 µs/op | 40 µs/op（1.6 GB/s） |

- **页预留近免费**：256MB 一次 Allocate 仅 ~6 µs（纯 CAS 推水位，无 lease 对象）。
- **复写 vs Append**：小粒度同量级（500 vs 548 ns）；**粒度越大差距越大**——64KB 处复写 10.5 GB/s
  vs Append 1.6 GB/s（**6.6×**）：Append 每笔付完整 lease+双尾管理且不可覆写（地址空间只进不退）。
- **并行复写真扩展（4T 2.5×）**：lease 区间所有权让不相交区并行写——与 Append 的小包并发退化
  （16T 0.87×，全局尾 CAS 串行）形成对照。多页缓冲并发写者的核心模式受益直接。
- 介质提示：以上为 mem（协议成本裸值）；磁盘叠加 syscall 税（§3：512B 处 ~2.5×）。

**✅ 同址反复复写退化已修（2026-08-21 当日销案）**：纯段表探针实锤——每复写一次
`_extentList` 净 +1（10 万次 = 10 万条），根因是**段表 extent 泄漏**：WriteLease 占位的
`SplitOverlappingExtents` 只拆边界不齐的旧 Committed（完全重合的不删），Commit 的 Eager
合并又只认严格相接（`prev.End==start`/`next.Start==end`）——同区间重合条永不归并。单 op
O(记录数) → 总 O(n²)。修复：`CompleteAndMergeEager` 收口清理被最终区间完全覆盖的旧条目
（原仅 sparse 模式清理，放开为无条件；在途态不删——排他保证无重叠）。验证：extent 恒 1 条、
10 万次恒 2.3-2.8 µs/op、BDN Write_Memory **1.37s → 4.73ms（290×）**、mem/local 全量 644 双绿。
取证：`test_out/repro`（反射计数）/`repro2`（引擎曲线）。ledger L9 已销案。

### 2.1 全动词表

| 动词 | 总耗时 | 每 op | 分配/op |
|---|--------:|------:|--------:|
| Allocate_Null（纯分配下界） | 780 µs | 78 ns | 63 B |
| Allocate_Memory | 763 µs | 76 ns | <1 B |
| Append_Null（分配+零搬运） | 4.96 ms | 496 ns | 313 B |
| **Append_Memory（端到端）** | 5.12 ms | **511 ns** | 297 B |
| AppendAsync_Memory | 6.30 ms | 631 ns | 360 B |
| ConcurrentAppend 4T | 4.70 ms | 470 ns | 291 B |
| ConcurrentAppend 16T | 5.86 ms | 586 ns | 285 B |
| Read_Memory | 2.37 ms | 237 ns | 539 B |
| Write_Memory（200 址轮复·含退化） | 1.37 s | ~137 µs | 242 B |
| PageBuffer_AllocateThenWrite | 5.05 ms | 505 ns | 359 B |
| SegmentCreation（4KB 段连续建） | 2.48 ms | 2.5 µs/op | 154 B |
| ConcurrentSegmentCreation 4T | 3.04 ms | 3.0 µs/op | 155 B |

- **Null vs Memory 差 ~3%**：小 payload 下内存搬运近免费，端到端延迟 ≈ lease 协议地板（§1.1）。
- **并发不扩展**（4T 1.09× / 16T 0.87×）：与 §1.3 同根——64B 太小，协议成本全暴露在尾 CAS 串行点上。
- Read 比 Append 便宜（无 lease 提交、双游标直读）；分配差异主要来自读缓冲租借。
- Write_Memory 的 137 µs 是 200 址 × 8 迭代累积退化后的均值（核心成本见 §2.0 净表行）。
- 建段路径（4KB 段 = 每 64 op 跨段）只让 Append 均摊 +0（预备池命中），连续建段速率 ~2.5 µs/op 内消化。

## 3. 引擎端到端——真磁盘 vs mem（NewLocalStorageEngineBench，ext4 /tmp）

| 动词 | payload | mem | 磁盘(ext4) | 磁盘税 |
|---|--------:|----:|-----------:|-------:|
| Append | 512 B | 1.10 µs | 2.75 µs | 2.5× |
| Append+Read | 512 B | 2.70 µs | 5.48 µs | 2.0× |
| Allocate+Write+Read | 512 B | — | 5.46 µs | — |
| Append | 4 KB | 4.70 µs | 5.46 µs | 1.2× |
| Append+Read | 4 KB | 13.9 µs | 16.6 µs | 1.2× |

- **syscall 税随 payload 摊薄**：512B 时 2.5×，4KB 时 1.2×——小写密集场景介质差异最大。
- Write 走 `Allocate`+`Write` 正位写法（Write 契约：目标 ≤ CommittedTail）。

## 3.5 DIO vs 非 DIO 模式矩阵（真磁盘 ext4，128MB 顺序，Mode×BlockSize）

Mode（methodology.md §1）：**A**=缓存写 / **B**=缓存+写透(O_DSYNC) / **C**=DIO 绕缓存 / **D**=DIO+写透。
复现：`dotnet $B --filter '*NewDeviceModeMatrixBench*'`（写）／`--filter '*NewDeviceReadMatrixBench*'`（读），配 `TC_BENCH_FS_SPEC=local:///…`。

### 写（吞吐 MB/s）

| 块大小 | A 缓存 | B 缓存+写透 | C DIO | D DIO+写透 |
|------:|-------:|-----------:|------:|----------:|
| 4K | 722 | 14 | 58 | 16 |
| 64K | 1,109 | 146 | 746 | 260 |
| 256K | 1,600 | 366 | **1,907** | 807 |
| 1M | 1,581 | 572 | **2,932** | 2,008 |
| 4M | 1,621 | 821 | **7,045** | 5,356 |

### 读（热读吞吐 GB/s——Buffered=页缓存命中，DIO=盘直读）

| 块大小 | Buffered（页缓存） | DIO（直读） | 缓存增益 |
|------:|------------------:|-----------:|---------:|
| 4K | 1.9 | 1.2 | 1.6× |
| 64K | 6.2 | 1.9 | 3.3× |
| 1M | 7.4 | 2.6 | 2.9× |
| 4M | 9.0 | 2.5 | 3.6× |

### 结论

- **写交叉点 ~256K**：小块缓存写快（page cache 聚合后异步刷盘）；≥256K 后 DIO 反超（免双缓冲
  拷贝 + 免内核 writeback 线程竞争），4M 处 **DIO = 缓存写的 4.3×**。大块顺序写密集负载应开 DIO。
- **写透（WT）小块是灾难**（4K 处 14-16 MB/s，钉在 fsync 地板）；且 **DIO+WT 全面快于缓存+WT**
  （1M 处 2,008 vs 572 = 3.5×）——每写必落盘的场景直接选 Mode D。
- **读走缓存**：页缓存命中比 DIO 直读快 1.6-3.6×——常规读不开 DIO；DIO 读留给自管缓存/大扫描。
- 引擎开关：`.WithHints(FileOpenHints.NoBuffering / WriteThrough)`；mem 等介质不吃 DIO
  （`UnbufferedSupport=Ignored` 自动降级，无分支代码）。
- 基准修复留痕：旧读基准把 128MB 预填写算进读计时（WT 模式被污染 40×）——已拆
  `NewDeviceReadMatrixBench`，预填全部进 `IterationSetup`（计时外）。
- 并发梯度矩阵（Mode × BlockSize × 1/6/12/24 线程）用 `NewDeviceIoParallelBench`：
  `dotnet $B --filter '*NewDeviceIoParallelBench*'`。

## 3.6 虚拟文件系统（virtual: 单文件卷）vs 磁盘目录（local:）

`TC_BENCH_FS_SPEC=virtual:///…`（BenchVolume 自动每卷唯一 .raw 文件）零重编译切换，同套基准。

### 小 IO（NewLocalStorageEngineBench，Append/roundtrip）

| 动词 | payload | mem | local 目录 | virtual 单文件卷 |
|---|--------:|----:|-----------:|---------------:|
| Append | 512 B | 1.10 µs | 2.75 µs | **1.74 µs** |
| Append+Read | 512 B | 2.70 µs | 5.48 µs | **3.80 µs** |
| Append | 4 KB | 4.70 µs | 5.46 µs | **5.02 µs** |
| Append+Read | 4 KB | 13.9 µs | 16.6 µs | **17.8 µs** |

### Mode 矩阵（128MB 顺序，吞吐 MB/s）

写：

| 块 | local A | local C(DIO) | virtual A | virtual C(DIO) |
|--:|--------:|------------:|----------:|---------------:|
| 4K | 722 | 58 | 251 | **268** |
| 64K | 1,109 | 746 | 300 | 353 |
| 256K | 1,600 | 1,907 | 415 | 226 |
| 1M | 1,581 | 2,932 | 312 | 241 |
| 4M | 1,621 | 7,045 | 228 | 339 |

读（热读，Buffered=页缓存 / DIO=直读，GB/s）：

| 块 | local Buffered | local DIO | virtual Buffered | virtual DIO |
|--:|--------------:|----------:|----------------:|------------:|
| 4K | 1.9 | 1.2 | 1.3 | 1.2 |
| 64K | 6.2 | 1.9 | 7.5 | 9.9 |
| 1M | 7.4 | 2.6 | 9.4 | **13.5** |
| 4M | 9.0 | 2.5 | 10.0 | **10.4** |

### 结论

- **virtual 单文件卷的小 IO 优于磁盘目录**：512B Append 1.74 vs 2.75 µs（**快 37%**）——
  Raw 卷内自管元数据（页缓存直址），少一层目录文件系统开销。极速运行时（mem+导出）/嵌入式/单工件分发场景收益直接。
- **virtual 的 Buffered 大块写是弱项**（≤415 MB/s，仅 local 的 1/4）：Raw 卷写日志 + 元数据
  双写路径；**但 virtual 的 DIO 读反超一切**（64K-1M 处 9.9-13.5 GB/s vs local DIO 1.9-2.6）——
  Raw 页缓存 64MB 自管命中路径更短。大块写密集选 local 目录 + DIO；读密集/单工件选 virtual。
- **virtual 对 WT（每写落盘）不友好**（Mode B/D 全面慢于 local：写透钉在卷日志双 fsync）——
  每写必稳的 WAL 类负载当前介质选 local。
- 与 Core 侧结论一致（io-performance.md §9：本地三介质数据面带内持平、差异在元数据面与
  一致性语义）——引擎层叠加后：**按语义选介质——极速吞吐 mem（随时 Capture/Transfer 转出）、小 IO/读密集 virtual、大块写/写透 local**。

## 4. 段生命周期操作（mem，16MB 段 / 1MB 段）

| 操作 | Mean | 说明 |
|---|-----:|---|
| 连续建段（1MB × 8，无预分配） | 1.89 ms | **~235 µs/段**，全部在 worker/预备池路径 |
| 连续建段（1MB × 8，预分配） | 2.63 ms | 预分配多 ~30%（真实写文件 vs 稀疏） |
| 纯水位推进（无建段） | 24–33 µs | Allocate.Watermark——建段成本与水位成本比 ~10× |
| ReclaimHead 整段（预填后） | 123 ms | 含预填摊销；回收本体 = 删段+水位推进 |
| Reclaim 打洞 | 66 ms | 区间级 PunchHole |
| Compact 迁移 | 127 ms | 整段搬迁（威胁操作量级，同步入口必带 timeout） |
| Compact 后续写 | 131 ms | Compact+Append——Compact 不中断写入路径 |

## 5. 本轮修复记录（2026-08-21，压测挖出）

1. **AppendLease 并发校验误判（引擎正确性 bug）**：多线程 Append 撞段边界随机抛
   `AppendLease: [segN@0xF80, segN@0x1000) 超过上界 segN@0x80`。根因：`AppendLease` 在
   CAS 成功后重读 `AllocatedTail` 做防御校验，`Atomic128.ReadUnsafe` 无屏障裸读被 JIT
   CSE 成循环内旧快照，校验拿 stale 值误判越界。修复：校验上界改用 CAS 确定值
   （`src/TC.Tier.Runtime/AddressSpace/SegmentTable.Lease.cs` `AppendLease`）。
   验证：18 组合 × 300 轮并发探针 508 败 → 0；全量测试 mem 644/644、local 644/644、
   Core 1657/1657 绿。
2. **基准腐烂修复**：17 处 `Initialize()` 后缺 `WaitForReady()`（V2 异步恢复残留）；
   `WriteAndRead` 基准违反 Write 契约（全新引擎直写 (0,0)）改 `Allocate`+`Write` 正位写法。
3. **介质统一**：全部基准/探针改 `TierFs` + spec（`BenchVolume`），`TC_BENCH_FS_SPEC`
   环境变量切介质零重编译——本档 §2/§3 同一二进制产出。
4. **核心场景基准补齐**：新增 `Write_Memory`（复写）/`PageBuffer_AllocateThenWrite`（页缓冲
   端到端）；CPU 采样节流对秒级满载微基准的干扰用 `Optimization.SampleInterval=1h` 关闭
   （节流是设计内"高负载让路"，测协议成本须排除）。
5. **发现同址复写线性退化**（净表 0.5 µs → 10 万次后 299 µs，O(n²) 总成本）——extent 记录
   随复写累积，覆写路径合并不彻底。详见 §2.0 + ledger L9（待修）；裸读 CSE 残余风险记 ledger L10。

## 6. 并发修复批次的性能税（2026-08-21，L11–L16 全销案 + 热路径微优化后）

修复内容与验证见 ledger L11–L16。热路径微优化：AcquireExtent 段重取移到重试路径
（首轮引用本就新鲜）+ 版本只在锁内/FairGate 捕获点读——快路径仅 +1 volatile 读。
两轮复测（方差 ±3%）对照修复前基线：

| 路径 | 修复前 | 修复后（两轮） | 差 |
|---|------:|------:|---:|
| Allocate 256MB | 6.6 µs | 5.7 µs | 无税 |
| 复写 64B 单线程 | 500 ns | 532–549 ns | **+6~10%**（唯一税格） |
| 复写 64B 4T | 198 ns | 176–177 ns | **−11%（更快）**，扩展比 2.5×→**3.1×** |
| 复写 4KB | 822 ns | 753–755 ns | **−8%（更快）** |
| 复写 4KB 4T | 327 ns | 316 ns | −3% |
| 复写 64KB | 6.26 µs | 5.70–5.95 µs | **−5~9%（更快，11.5 GB/s）** |
| Append 64B | 548 ns | 536–602 ns | 噪声带内持平（该行方差 ±10%） |
| Append 64KB | 40 µs | 40.7 µs | 持平 |
| 每 op 分配（复写） | 242 B | 187 B | **−23%** |

**结论修正**：性能税只在"单线程小包"一格（~30-45ns = 版本哨兵 + 保守投影 + 物化后
校验的必要正确性成本）；核心模式的主力形态（并行复写、大块）不降反升——4T 扩展比
2.5×→3.1×、每 op 分配 −23%。真实负载（多写者/大记录）不受税，纯单写者微记录场景
+6~10%。

**反弹实录（留档防复发）**：L13 首版校验用裸 `_tailSlot.Allocated` 单读——exact-fill 段界处
下一推进 segId+offset 双变，撕裂/CSE 读出旧值 → 纯并发 Append（无任何 ReclaimTail）假阳性
"退水"异常（ConcurrentAppend 基准 NA 实录）。修复：`IsAllocatedBelow`——no-op CAS 原子快路径 +
MemoryBarrier 稳定双读慢路径。与 L10（AppendLease CSE）同族：**16B 裸读一律不得用于越界判定**。
