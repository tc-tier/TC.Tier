# 存储引擎（StorageEngine）使用指南

> **给谁看**：用 StorageEngine 存取数据的上层（Structures / Transactions / 应用与测试）。
> **只讲引擎自己的能力怎么用**：引擎怎么建、数据怎么写读、怎么落盘、怎么回收、怎么恢复。
> 介质与文件系统（fs/spec/TierFs/四介质差异、内存镜像持久化等）**不在本档**——
> 见 `src/TC.Tier.Core/docs/io.md`（§1–§2 介质构造与 spec 语法）。

---

## 1. 引擎该怎么用：两种使用模式

引擎地址空间是**一块可复写的持久化内存**（地址 = 段号+段内偏移，`LogicalAddress`）。
使用方式先分清两条路，**默认走模式 A**：

| | **模式 A：预分配 + 复写（推荐）** | 模式 B：Append（WAL 简单路径） |
|---|---|---|
| 心智 | **自管地址**：先 `Allocate` 圈地，后按址 `Write` 填充/复写 | **引擎管尾**：只管追加，地址是返回值 |
| 写前动作 | `Allocate(len)` 预留（无 lease 协议，256MB 仅 ~6 µs） | 无 |
| 写入 | `Write(addr, data)`——址可**原地无限次重写**（页回收再利用） | `Append(data)`——地址只进不退 |
| 并发 | **不相交区真并行**（4 线程 2.5× 扩展，lease 区间所有权） | 小包并发反扩展（全局尾串行，16T 0.87×） |
| 吞吐 | 64KB 块 **10.5 GB/s** | 64KB 块 1.6 GB/s |
| 适合 | 页式/记录式数据结构（Ring/索引页/对象存储）、高并发写、原地更新 | 只想记日志、不想管地址、纯顺序写 |
| 代价 | 自己管理"哪块地址放什么"（水位线/自由表归上层） | 地址空间单调增长，靠 Reclaim/Compact 回收 |

**选型口诀：数据有"页/槽"概念、要复写要并行 → A；纯顺序日志 → B。**
大多数数据结构应该是 A；B 是引擎提供的便利 WAL 能力（每笔付完整 lease+双尾推进）。

```csharp
using TC.Tier.Core.IO;
using TC.Tier.Runtime.Storage;
using TC.Tier.Contracts.Storage;

await using var engine = await StorageEngine.CreateAndInitializeAsync(fs, "my-engine");

// ── 模式 A（推荐）：预分配 + 复写 ─────────────────────────────
var (region, _) = engine.Allocate(RegionBytes);          // ① 圈地（近免费）
var slotAddr = engine.CalculationAddress(region, slotIndex * SlotSize);
engine.Write(slotAddr, slotData);                        // ② 按址填充/复写（可无限次重写）
engine.Read(slotAddr, buffer);                           // ③ 按址读回

// ── 模式 B（WAL）：Append ────────────────────────────────────
var logAddr = engine.Append(record.Span);                // 顺序追加，返回起始地址
engine.Flush();                                          // 批末落盘
```

> 全部性能数字与依据见 [perf/storage-engine-perf-baseline.md](perf/storage-engine-perf-baseline.md) §0/§2.0。

---

## 2. 建引擎

### 2.1 三个入口

| 入口 | 语义 |
|---|---|
| `Create(root, options, …)` | 只构造不初始化——想自己观察恢复过程（`Initialize(hints)` 后盯 `RecoveryState`/`WaitForReady`）再用 |
| `CreateAndInitialize(…)` | 构造 → 恢复 → 就绪，一步到位（同步）。**禁止在 UI/ASP.NET 同步上下文调用**（死锁） |
| `CreateAndInitializeAsync(…)` | 同上，异步版，**首选** |

引擎名可直接传字符串（隐式转 options）：
`StorageEngine.CreateAndInitializeAsync(fs, "my-engine")`。
一个 fs 卷里放多个引擎：不同 `EngineName`（各占一个子目录，互相隔离）。

### 2.2 选项（`StorageEngineOptions`）

| 选项 | 默认 | 说明 |
|---|---|---|
| `EngineName` | `"tier-engine"` | 引擎子目录名 + 段文件名前缀 |
| `SegmentGrowthLimit` | 256MB | 单段增长上限，到顶自动切下一段 |
| `EnableSegmentation` | `true` | `false` = 单段模式（只有 seg0，适合小数据量） |
| `PreallocateFile` | `true` | 建段真实预分配；`false` = 稀疏按需增长 |
| `DeleteOnClose` | `false` | Dispose 时删除本引擎全部产物（测试清理常用） |
| `Hints` | `None` | `WriteThrough` = 每写同步落盘；`NoBuffering` = 请求 DIO。见 §3.4 |
| `Optimization` | — | 调优参数（`IndexCapacity`/`SpinMilliseconds`/`WarnEvery`），一般不动 |

流式配置：`.WithSegment(limit, enable)` / `.WithPreallocateFile(b)` /
`.WithDeleteOnClose(b)` / `.WithHints(hints)`。

### 2.3 Dispose

`await using` 即可。内部编排（停 worker → 停 epoch → 清段池 → 补写段元组 →
按需清目录）自动完成，无需调用方配合。

---

## 3. 读写

### 3.1 先建立三水位心智模型

```
MinAddress ≤ CommittedTail ≤ AllocatedTail
```

| 水位 | 含义 | 用途 |
|---|---|---|
| `CommittedTail` | **真实已写**水位 | **读 / 扫描 / 回收的合法上界**（记住这条就够了） |
| `AllocatedTail` | 已预留水位（含未写空洞） | Append 的内部起点；算剩余容量用 |
| `MinAddress` | 最小有效地址 | 头部回收后后移 |

模式 A 的"圈地"推 `AllocatedTail`（占位即 Committed+sparse：可读零、可覆写）；
`Write` 落实际数据。两模式的读/扫描/回收完全一致。

### 3.2 动词速查

| 动词 | 语义 | 注意 |
|---|---|---|
| `Allocate(len)` | **圈地**：预留区间返回 `(start, end)` | 无 lease 协议（纯 CAS），近免费；模式 A 第一步 |
| `Write/WriteAsync` | **按址复写**（模式 A 主力） | 址可无限次重写；目标须 ≤ CommittedTail（Allocate 过即可） |
| `CalculationAddress(addr, ±len)` | 址上推算（跨段进位/借位） | 圈地内定位槽位的唯一正道；**禁手算 Offset 差** |
| `Append/AppendAsync` | 尾部追加（模式 B） | 每笔付 lease+双尾推进；地址只进不退 |
| `Read/ReadAsync` | 按址读，跨段自动切分 | 返回实际读数（0 = 到尾）；地址已被回收则抛异常 |
| `OpenSequentialReader` | 顺序游标，读/跳分离、可双向 | 全量扫描（`usePageCache`/`snapshotMode` 按需选） |
| `Flush()` / `Flush(upTo)` | 落盘（fsync 族） | **仅同步**（OS 无异步 fsync）；upTo 版自动对齐段边界 |
| `GetDistance` | 两址距离（跨段正确） | 与 CalculationAddress 同为地址算术正道 |
| `ReclaimHead/Tail/Reclaim` | 回收区间释放空间 | 模式 B 日志消费后收头；`StartReclaim` 后台版（0 等待）带事件+进度 |
| `StartCompact` / `StartRangeCompact` | 碎片整理（整段搬迁） | **一律后台句柄**（2026-08-24 决策：同步入口废除——强制等待有死锁风险）；超时经 `await op.WaitAsync(ct)` 调用方自控；句柄冲突（rename 撞共享违例）由引擎自动关句柄+marker 续传，不重拷贝 |
| `GetHoleRatio` | 查区间物理空洞占比 | 0.0 全实 / 1.0 全洞 |

### 3.3 模式 A 完整范式：预分配 + 复写

```csharp
// ── 启动：圈地（页缓冲模型的开销仅 ~6 µs / 256MB）──────────────
var (region, _) = engine.Allocate(PageCount * PageSize);

// ── 定位槽位：CalculationAddress（圈地内唯一正道）──────────────
var page3 = engine.CalculationAddress(region, 3 * PageSize);
var slot  = engine.CalculationAddress(page3, slotNo * SlotSize);

// ── 写：填充 / 原地更新（复写不增长空间，页回收后重写即复用）────
engine.Write(slot, data);

// ── 并发写：每写者认领不相交区（lease 区间所有权 → 真并行 2.5×）──
var regions = Enumerable.Range(0, writers).Select(w => engine.Allocate(RegionSize)).ToArray();
// 每线程只写自己的 regions[w]，互不干扰

// ── 读：按址直读；全量走游标 ─────────────────────────────────
engine.Read(slot, buf);
using var reader = engine.OpenSequentialReader(engine.MinAddress, engine.CommittedTail);
```

**上层自管什么**：槽位→地址映射（自由表/位图）、页水位、脏页回写时机。
引擎保证：地址稳定（复写不改址）、区间并发安全、崩溃后圈地与已写数据都在。

### 3.4 模式 B 范式：Append WAL

```csharp
var addr = engine.Append(frame.Span);    // 帧格式自带 CRC/长度/序号（上层协议）
// ……
engine.Flush(upTo: lastApplied);         // 按应用水位落盘
// 消费完的头部区间回收：
engine.ReclaimHead(consumedUpTo);
```

### 3.5 持久化姿势（引擎自有开关）

- **默认（`Hints=None`）**：写进页缓存攒批，调 `Flush()` 才保证落盘——吞吐高，
  按事务/批次 Flush 即可；
- **`WithHints(FileOpenHints.WriteThrough)`**：每写同步落盘——每笔都必须稳的
  场景（如 WAL 提交点），牺牲吞吐。

`WithHints(FileOpenHints.NoBuffering)` 是请求直 IO（绕页缓存）；真实生效与否
经 `engine.UnbufferedSupport` 报告（部分介质不吃 DIO，报 `Ignored`）——
它是探测结果，**别拿它当介质判断做分支**。

---

## 4. 崩溃恢复：默认全自动，不用管

`CreateAndInitialize*` 内部已做：扫盘重建段表与地址表 → 重建容量计数 → 启动保护。
空目录 / 新卷自动从零开始，不报错。恢复进度经 `RecoveryState` 可观察
（只有"要观察恢复过程"才需要 `Create` + 手动 `Initialize` + `WaitForReady`）。

两模式恢复语义：
- **模式 A**：圈地与已写槽位原样恢复（Allocate 占位即 Committed+sparse，持久）；
  上层自管的槽位映射从自己的元数据恢复。
- **模式 B**：物理尾部自动截断半写帧由上层帧协议裁定；引擎侧高级口
  `EngineRecoveryHints`（`CommittedTailHint`）——**上层比引擎知道更多时**
  （如事务日志持有自己的提交水位）修正双尾，防尾部半写数据被当作有效。
  它只属于**直接构造引擎的消费者**；数据结构内部建引擎时一律不带 hints
  （物理真相引擎自恢复，逻辑水位结构自管，两回事）。

---

## 5. 介质选型与测试接线

### 5.1 极速吞吐：内存模式不只是测试用

`memory:` 是**生产级极速介质**——直址零拷贝、纳秒级元数据，引擎端到端 ~0.5 µs/op（磁盘 2.75 µs）。
需要极速吞吐、数据生命周期 = 进程生命周期的场景（缓存、计算中间态、高频写临时区）首选内存模式。

数据不是"Dispose 即丢"：`RootSpaceImage`（Core IO）随时把内存卷**导出为镜像/转化为任意介质**——

```csharp
using var mem = TierFs.New("memory:");            // 极速运行时
// …引擎满速读写…
using (var out_ = File.Create("snapshot.tca"))
    RootSpaceImage.Capture(mem, out_);            // 导出存档（静默快照，稀疏/Extra 全保真）
using var disk = TierFs.New("local:///data/x");
RootSpaceImage.Restore(File.OpenRead("snapshot.tca"), disk);         // 转化为磁盘目录
// 或 Restore 进 virtual:///x.raw = 单工件活卷；Transfer(mem, target) 介质直转
```

内存 → 存档 → 任意落点（磁盘 / Raw 单文件卷 / 云），运行时性能与持久化自由度兼得。
详见 Core IO `io.md` §4.4 / §10。

### 5.2 测试接线

整套测试可用环境变量切介质、零重编译：
`TC_TEST_FS_SPEC=local:///tmp/tier-test dotnet test …`（缺省 `memory:`——CI 零盘极速）。
范式见 `tests/TC.Tier.Runtime.Tests/TestVolume.cs`。测试常用：
`new StorageEngineOptions("x").WithDeleteOnClose(true)` 自动清理产物。

---

## 6. 禁忌

| # | 规矩 |
|---|---|
| 1 | **别绕过引擎直开段文件**——句柄池与水位契约会被打穿 |
| 2 | 读/扫/回收上界用 `CommittedTail`，不是 `AllocatedTail`（后者含未写空洞） |
| 3 | 地址距离/推算只用 `GetDistance`/`CalculationAddress`，禁手算 `Offset` 差 |
| 4 | "已写完" ≠ 持久化——持久化以 `Flush()` 返回为准 |
| 5 | `Compact` 同步入口必带 timeout（威胁操作：整段搬迁） |
| 6 | 依赖段文件名格式做外部扫描（段命名是引擎私有规则） |
| 7 | fs 用法（介质构造/spec/options）归 Core IO——本层代码只收 `IFileSystem`，不自建、不判断介质 |
| 8 | **别上来就 Append**——数据有页/槽概念就用模式 A（预分配+复写）；Append 只给纯顺序日志 |

---

## 7. 决策速查

| 问题 | 答案 |
|---|---|
| **怎么用？** | 默认模式 A：`Allocate` 圈地 → `CalculationAddress` 定位 → `Write` 复写；纯 WAL 才模式 B `Append` |
| 换介质？ | 改 spec 一行（fs 层的事，见 Core IO io.md §2）；引擎/业务零改动 |
| 大批量随机读写？ | 模式 A 直接循环 `Write/Read`（无批量面，外部循环等价） |
| 顺序扫全量？ | `OpenSequentialReader` |
| 每笔必稳？ | `WithHints(FileOpenHints.WriteThrough)`；否则默认 + 批末 `Flush()` |
| 要 DIO？ | `WithHints(FileOpenHints.NoBuffering)` 请求，`UnbufferedSupport` 看真实结果 |
| 空间回收？ | 模式 A 槽位复用（原地重写，无需回收）；模式 B `ReclaimHead` 收头 / `Compact` 整理 |
| 一个卷多个引擎？ | 不同 `EngineName`（各占一个子目录） |
| 性能基线？ | [perf/storage-engine-perf-baseline.md](perf/storage-engine-perf-baseline.md) |

---

## 8. 想深入？指路

| 想懂什么 | 去哪 |
|---|---|
| **介质/fs/spec 用法**（四介质、TierFs、options、卷镜像） | `src/TC.Tier.Core/docs/io.md` |
| spec 协议全文 / 介质设计 | （内部存档） |
| 段表（地址空间怎么切） | [segment-table.md](segment-table.md) |
| 段句柄租约 | [lease-protocol.md](lease-protocol.md) |
| 引擎内部机制（恢复/池/worker） | 源码 `src/TC.Tier.Runtime/Storage/` 各 partial 注释 |
