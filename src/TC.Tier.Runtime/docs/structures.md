# Structures（数据结构层）使用指南

> **适用范围**：`src/TC.Tier.Runtime/Structures/` 全目录——Ring / ProbingIndex / SortedIndex / Log /
> Metadata / Mirror / Snapshot 七个子体系 + 共享件（`Contracts/` 两族横切：IIndex、IKeyResolver、
> ProbingIndexFormat；RecordCodec、StructureScanCursorBase、Settings 基类）。
> 各子目录私有件在各自小节描述；跨层底层原语见 Core 文档体系。
> 回答："七种结构各自是什么、怎么选、怎么组合成 KV、生命周期与恢复怎么走、线程模型是什么、
> 哪些边界尚未完成"。
> 每结构自有引擎（StorageEngine），介质 = 构造传哪个 `IFileSystem`；meta 持久化统一协议见
> [`docs/meta.md`](docs/meta.md)（本文不重复）。性能数字见
> [`docs/perf/structures-perf-baseline.md`](docs/perf/structures-perf-baseline.md)。

---

## 0. 一句话总纲

**2 主结构 + 4 搭配件 = 产品发生器：Ring（数据真相源）/ Log（操作流）是主结构，索引/元数据/
镜像/快照是搭配件（经桥单向依赖、可摘——存在性=优化非正确性）。恢复统一模型 = 载快照/镜像
（到 W）+ 重放 (W, 尾]。镜像与快照同族（完整状态基线），核心差异 = 截断坐标系：快照按字节截断
（纯流式），镜像按版本号截断（版本链 N=2/回跳/回退）。** 组合编排归组合层（先 Ring.Write 得
地址、再 index.Insert），Structures 自身不设组合类（组合模型全文见 COORDINATION §4.0）。

```
组合层（TierKV 形态——Products 将来的薄封装）
   │  写：ring.Write(key,value)→addr ──▶ index.Insert(key,addr)
   │  读：index.Find(key)→addr ──▶ ring.GetValue(addr)（两段合口径）
   ▼
Ring（BlittableRing）──实现──▶ IKeyResolver<TKey> ──喂给──▶ 两族索引（判等/恢复回源/已落盘水位）
   │                                                    HashIndex / BTreeIndex / SkipListIndex
   │ GetFlushedWatermark()（已落盘水位 W）               │ HashIndex：主存储 dump（内置）
   ▼                                                    ▼
StorageEngine（每结构主引擎唯一）                SortedIndex：ITransferPersistence（可空注入）──桥接──▶ WholeMirror
```

跨结构原子提交用 `Transactions/TransactionLog`（独立 commit record 文件驱动
`ITransactionParticipant`，见 §10）。

---

## 1. 积木全景

| 积木 | 类型 | 一句话职责 | src 内消费者 |
|---|---|---|---|
| **Ring** | `BlittableRing<TKey>` | 追加式 KV 环：页池 + 单调逻辑地址 + 8 水位指针 + 原地 RCU + 环形驱逐 | 组合层（测试组合根） |
| **ProbingIndex** | `HashIndex<TKey>` | 探测族：条目=地址+tag 不物化 key，判等经 KeyResolver 回读；极省内存点查 | 组合层 |
| **SortedIndex** | `BTreeIndex<TKey>` / `SkipListIndex<TKey>` | 比较族：条目物化 (key,地址)，有序遍历/range scan | 组合层 |
| **Log** | `EntryLog` / `DeltaLog` | WAL（组提交/恢复水位）/ 临时 checkpoint delta | 组合层预备 |
| **Metadata** | `VersionedMetadata` | 版本链追加式单值元数据，N=2 轮转回收 | `Meta/MetadataMetaTransport`（3a 推荐实现） |
| **Mirror** | `WholeMirror` / `PagedMirror` | 字节镜像，两种一套统一帧格式（双魔术值+推导长度：WMHD/WMFT·CRC64 / PMVH/PMFT·CRC32C），机制归 MirrorBase | 组合层预备 |
| **Snapshot** | `StreamSnapshot` | 结构快照的流式帧文件读写（帧头 SNHD + 帧尾 CRC64） | 组合层预备 |
| ProbingIndexFormat | public static | 索引结构像共享格式：三段式（头先行校验/体自定界/尾 CRC64 总验收）——`Contracts/`（两族横切） | 两族索引（ImageWriter/ImageReader） |
| RecordCodec | public static | 定长族 record 共用 CRC 工具（算法/位置由 flags 决定） | Log 帧 / Metadata / Ring record（Mirror 帧体系自带 CRC 工厂，不经此） |
| StructureScanCursorBase | public abstract | 游标骨架：Direction + MoveNext/MoveNextAsync/Dispose | Ring 游标 |
| Settings | abstract | 全结构配置基类：Name/PreallocateFile/DeleteOnClose + MetaPolicyKind/MetaOpaqueBytes | 全部子体系 Settings |

索引持久化两形态（COORDINATION §4 铁律 8 桥接判据）：**HashIndex 自建主存储**（结构核心能力——
后台 dump 三段式帧 + 版本链，可关闭；见 §5.4）；**SortedIndex 桥接注入**（构造期可空注入
`ITransferPersistence`，内置桥 `Runtime/DataMirror/WholeMirrorPersistence` 托管 Mirror 子体系
的 WholeMirror（版本链/CRC64/N=2/2PC 全复用）；不注入 = 纯重放 fail-safe）。镜像通道契约与
裁决记录见内部设计存档。

---

## 2. 快速上手：组合 KV（三步）

### 2.1 一行声明封闭形态（[RingKey] 源生成器）

Ring/索引的构造函数与 `Create` 工厂全部 `protected internal`——开放泛型不外泄，消费面只经
`[RingKey]` 生成的封闭薄类（编译期封闭 + ctor 转发 + Create 工厂）：

```csharp
[assembly: RingKey(typeof(long))]   // 程序集级声明，可多条
// 产出四个 sealed 薄类：
//   RingOfLong : BlittableRing<long>（Create 工厂 = 构造+Initialize+WaitForReady 一步到位）
//   HashOfLong : HashIndex<long>
//   BTreeOfLong / SkipListOfLong : BTreeIndex<long> / SkipListIndex<long>
```

基元类型用 C# 关键字拼型（`long`→`RingOfLong`），其余取类型名（`OrderId`→`RingOfOrderId`）。

### 2.2 写读编排（组合层两行原语）

```csharp
using var ring  = RingOfLong.Create(ringSettings, vol.Fs);       // 真相源
using var index = new HashOfLong(vol.Fs, indexSettings, keyResolver: ring);  // 判等闭环必注入

// 写：先 Ring.Write 得地址（真相源）、再 index.Insert（派生）——两步正序
static LogicalAddress KvPut(RingOfLong ring, IIndex<long> index, long key, ReadOnlySpan<byte> value)
{
    var addr = ring.Write(key, value);
    index.Insert(key, addr, LogicalAddress.Empty);
    return addr;
}

// 点查两段合口径：index.Find 命中 → Ring.GetValue 取值
static bool KvTryGet(RingOfLong ring, IIndex<long> index, long key, Span<byte> buf, out int len)
{
    len = 0;
    var addr = index.Find(key);
    if (addr == LogicalAddress.Empty) return false;
    len = ring.GetValue(addr, buf);
    return true;
}
```

**终态读形态（批量最快）**：`ring.EnterReadScope()` + `index.EnterScope()` 一批一进出 +
`IndexScope.Find` + `Ring.GetValueSpan`（零拷贝切片；溢出 record 回退 thread-static 拷贝）。
批口径实测反超 FASTER（见 perf 文档 §3）。

### 2.3 恢复（两段式协议）

```
① Ring.Initialize + WaitForReady → 恢复水位；锚点 W = ReadOpaqueMeta()
   （无锚点 / 损坏 / W 越过当前尾 → 回退 BeginAddress——宁可旧多重放）
② index 拉流重放：ScanAsync(W, TailAddress) 逐条 Insert 自建
   （HashIndex/SortedIndex 有主存储帧时优先载帧：见 §5.4）
```

锚点 W 就是索引上次主存储 dump/重放完成的水位，经 Ring 的 opaque 搭车持久化（随水位线原子
落盘，见 meta.md §4）。每次 dump/定期重放后 `SetOpaqueMeta(W)` 即可。

---

## 3. Ring（BlittableRing）——追加式 KV 环

**本质**：固定槽数环形页池 + 全局单调逻辑地址 + 8 水位指针状态机
（mutable→readonly→flushed→evicted）；可原地 RCU（UpdateValue）+ 环形驱逐复用的混合日志。
页池每页经 PinnedBufferPool 钉住，热区全内存、冷区按页回源；溢出值（OverflowPolicy）另建
溢出引擎（WiscKey 形态）。直接实现 `IKeyResolver<TKey>`（无适配层）。

### 3.1 公开面（数自查：写 2 对 + 读 5 形态 + 批量 1 + 水位 8 + Flush 1 对 + 截断 1 + 扫描 2 + 快照 4）

| 组 | 成员 | 说明 |
|---|---|---|
| 写 | `Write` / `WriteAsync` | 追加 record 返回地址；**多线程并发安全**（页分配 CAS） |
| 写 | `UpdateValue` / `UpdateValueAsync` | 原地 RCU 覆写已知地址的 value（同尺寸场景） |
| 读 | `TryGetKey` / `GetKey`（+`TryGetKeyAsync` / `GetKeyAsync`） | 地址→key（IKeyResolver 契约面） |
| 读 | `GetValue` / `GetValueAsync` | 地址→value 拷贝出参（自保护，无需 scope） |
| 读 | `GetValueSpan` | **零拷贝切片**——生命周期契约 = ReadScope 内消费（页驱逐经 epoch 排水恒稳）；溢出 record 回退 thread-static 拷贝（下次同线程调用覆盖） |
| 读 | `GetSpan` / `GetFields` | 底层 record 原始切片 / 头字段（keyLen、payloadLen、flags） |
| 读 | `EnterReadScope` → `ReadScope`（ref struct） | 持 epoch（Resume/Suspend）——零拷贝读的护栏 |
| 批量 | `GetRecords<THandler>(addresses, handler)` | 按页号聚簇批量读，减少冷区回源次数 |
| 水位 | `BeginAddress` `HeadAddress` `TailAddress` `FlushedUntilAddress` `ReadOnlyAddress` `SafeReadOnlyAddress` `SafeHeadAddress` `ClosedUntilAddress` | 8 指针只读视图（Safe* = 排水完成可安全消费的版本） |
| Flush | `FlushUntil` / `FlushUntilAsync` | 把 [尾, untilAddress) 落盘推进 |
| 截断 | `TruncatePrefix(address)` | 头截断物理回收（整段删除 + 段内 PunchHole，经引擎 ReclaimHead） |
| 截断 | `TruncateSuffix(address)` | **D2 尾截断**——放松"地址单调不回退"铁律的唯一异常路径：引擎 ReclaimTail 物理销毁 + 内存水位条件回退 + 页池清零 + 冷缓存失效；目标落入已驱逐区（< SafeHeadAddress）抛 |
| 扫描 | `ScanAsync` ×2 重载 / `OpenScanCursor` | 异步迭代器 (Key,Address) 流 / 游标（CurrentAddress/NextAddress/GetFields/CurrentRecordSize；同步 MoveNext + 异步 MoveNextAsync + Dispose 双轨） |
| 快照 | `OpenSnapshotReader` / `OpenSnapshotWriter` + `Reader` / `Writer` 工厂 | 区间字节流导出/导入（Read/Write + Complete；冷热区压缩导出） |
| 2PC | `Prepare` `PrepareAsync` `ConfirmCommitted` `OnCommitted` `Abort` `AbortAsync` | 六件套全实现（Abort=D2 尾截断到上一提交边界，见 §10.2） |
| meta | `MetaPolicy` + SetOpaqueMeta/ReadOpaqueMeta 等 | 统一走 meta.md |

### 3.2 Settings 旋钮（RingSettings，默认值即生产值）

| 旋钮 | 默认 | 说明 |
|---|---|---|
| `PageSize` | 32MB | 页大小（2 的幂） |
| `MemorySize` | 16GB | 页池内存上限（决定热区大小） |
| `MaxPageCount` | 8192 | 页池槽数上限 |
| `MutableFraction` | 0.9 | 可变页占比（RCU 原地更新窗口） |
| `OverflowPolicy` | Disabled | 值溢出策略（Inline/溢出引擎分离——WiscKey 形态） |
| `MinOverflowSize` | 0 | 超过即走溢出引擎的最小值尺寸 |
| `ClockCacheCapacity` | null | 冷页缓存页数（null=默认时钟算法） |
| `ColdReadRatio` | 0.25 | 部分页冷读比例（页内未填充区回源概率） |
| `ColdRecordBufferLimit` | 1MB | 冷读 thread-static 缓冲上限 |

### 3.3 线程模型

- **写**：多线程并发安全——地址分配经 CAS，页封经协议；并发写实测 4 线程 3.6× 扩展（perf §2）。
- **零拷贝读**（GetSpan/GetValueSpan）：必须在 `ReadScope` 内消费——scope 持 epoch，驱逐经
  排水等待读者退出；scope 外持有 span = 使用已回收内存。
- **拷贝读**（GetValue/TryGetKey/GetFields 等）：内部自保护（epoch 短临界或拷贝完成即退），
  无 scope 要求。
- **TruncatePrefix**：与写完全并行（操作地址空间两端，引擎 lease 协议互不重叠）。

---

## 4. 索引两族——共同契约（IIndex）

```csharp
public interface IIndex<in TKey>   // 五成员最小协议（组合层消费面）
{
    LogicalAddress Find(TKey key);                 // Empty = 不存在
    LogicalAddress Insert(TKey key, LogicalAddress valueAddress, LogicalAddress beginAddress);
    bool Delete(TKey key);
    long EntryCount { get; }
    long IndexSize { get; }                        // 索引内存占用（字节）
}
```

两族基类（`ProbingIndexBase<TKey>` / `SortedIndexBase<TKey>`）均实现 `IIndex<TKey>` +
`ITransactionParticipant`，均提供：`BeginAddress`（恢复窗口起点）、`EnterScope()` → `IndexScope`
（FindNoEpoch——跳 epoch 的 scope 形态点查）、`FindBatch(keys, results)`（批量填结果数组）。
**持久化分形态**：HashIndex 自建主存储（`PersistenceKind` 可关，内置后台 dump——见 §5.4）；
SortedIndex 可空注入镜像通道 `ITransferPersistence`（不注入=纯重放）。

**2PC 是接口显式实现 + seq 记账**（volatile 推进 Prepared/Committed；Abort = 回退 prepared 到
committed，无 IO——索引是派生数据可重建）。注意：`OnCommitted` 对尚未提交的 seq **不缓存
回调**（已提交则立即触发，未提交直接丢弃）——索引无提交副作用，勿依赖其回调时序。

---

## 5. 索引选型：Hash vs BTree vs SkipList

| | HashIndex | BTreeIndex | SkipListIndex |
|---|---|---|---|
| 条目形态 | 地址+tag（**不物化 key**） | (key, 地址) 物化 | (key, 地址) 物化，NodeArena 变长驻留 |
| 有序遍历 | ❌ | ✅ `CreateScanCursor(direction)` | ✅ 同左 |
| KeyResolver | **构造期必注入非空**（判等闭环：tag 命中后回读真 key 校验） | 可选（判等不需要；**恢复重放需要**——有窗口无 resolver 恢复期 fail-fast） | 同左 |
| 点查 | **161 ns**（最快最省内存） | ~350 ns | ~816-850 ns（写无分裂重排，比 BTree 便宜） |
| 并发插入 | 128bit 桶 CAS | 单写者（节点引擎写） | 单写者（arena CAS-bump） |
| 持久化 | 主存储 dump（内置，可关） | 主存储锚点 dump：32B 几何帧（节点在引擎内） | 同左 |

### 5.1 HashIndex 特有

- **tag 机制**：条目 16B LogicalAddress 打包 [State(2b)][Tag(14b)][Version(16b)]（静态助手
  `CreateTentative/CreateOccupied/GetState/GetTag/GetVersion`）；Tentative 条目对 Find 不可见。
- **容量自适应 GrowIndex**：装载 >0.7 时函数式构建新代表（表+溢出池同代对）+ 单引用原子发布，
  并发读者持旧代 stale-but-valid 探测，旧代归 GC。默认容量 1<<14 起步按需翻倍。
- **铁律**：增长必须在**插入之前**检查阈值——增长时刻的表内条目必可经 KeyResolver 解析
  （插入后触发会踩"条目尚未注册进 resolver"的窗口，rehash 静默丢条目）。

### 5.2 BTreeIndex / SkipListIndex 特有

- 节点持久化在自有引擎（引擎寄生形态），节点缓存经 `LogicalAddressMap`（BTree 定容生长双模式
  旋钮 `NodeCacheInitialCapacity`——两族共用，SortedIndex 私有件）；SkipList 节点驻留
  `NodeArena`（变长 32+16×层高——Core.Primitives 底层原语，见 Core COORDINATION §4.4）。
- `CreateScanCursor(ReadDirection)` → `IIndexScanCursor<TKey>` 有序遍历（Forward/Backward）。
- SkipList 布局偏移契约钉在 SkipListNodeHeader（Key@0/SegId@8/Offset@16/…/Level_i@32+16i）——
  改布局必须同步指针访问器。

### 5.3 恢复（两段式，见 §2.3）

索引=派生数据，恢复核心拉 `KeyResolver.ScanAsync` 流自建；hints（`ProbingIndexRecoveryHints` /
`SortedIndexRecoveryHints`）携带重放窗口 [Begin, End)——有窗口而无 resolver 者 fail-fast。

### 5.4 数据持久化（恢复加速——HashIndex 自建主存储；SortedIndex 桥接注入镜像）

**HashIndex 主存储（Builtin——设计稿 V2/index-persistence-evolution-design.md）**：持久化是结构
核心能力（可关闭 `PersistenceKind=None` 走纯重放）。`TryDump()`：fuzzy 逐槽 128bit 原子读拷贝
（跳 Tentative 只收 Occupied——FASTERKV 同构，零写停顿）+ 三段式帧（`ProbingIndexFormat`：
20B 头[PIHD/kind/体长先行校验] + 几何 32B + 桶区/溢出池体 + 32B 尾[PIFT/W + CRC64 总验收]）。
W = `IKeyResolver.GetFlushedWatermark()`（Ring 已落盘水位——组合层契约：Insert 先于落盘、失败
回滚，已落盘必已入索引）。后台 `BackgroundWorkerLoop` 按策略（时间间隔/条目增量阈值）自动 dump；
版本链 N 版保留（帧长可推导：20+几何+size×128+ofbCap×128+32，尾锚=帧走链 CRC 总验收，
ReclaimHead 轮替最老帧）。
恢复**主存储帧优先**（三级回退中间级）：帧有效且 W∈[Begin,End] → 载帧物化 + 只重放 (W,End]；
无帧/损坏/W 越界 → fail-safe 全量重放同路。载帧零 key 回读、零结构重建（地址一等公民）；
>W 混入条目靠重放幂等收敛或 ring 裁决惰性失效（TryGetKey 读不到即 miss，无需显式清理）。

**SortedIndex 主存储（Builtin——2026-08-24 落地，镜像通道退役）**：自持有引擎即自建主存储。
`TryDump()`：覆写固定锚点帧（首开在引擎头部预留 84B 锚点槽——节点分配自然在其后，`MinAddress`
恒为锚点；节点与帧引擎内混排故不走帧走链）+ 三段式帧（20B 头[magic/kind/体长先行校验] +
**32B 几何体** + 32B 尾[magic/W + CRC64 总验收]）。几何=根/head 指针 +
计数——节点本就写时持久化在自持引擎（BTree 节点全量、SkipList 节点+链变更均写回），**物化只设
根 + 计数**，零逐节点流。恢复**锚点帧优先**（三级回退中间级）：帧有效且 W∈[Begin,End] → 物化 +
只重放 (W,End]；无帧/损坏/W 越界 → fail-safe 全量重放同路。**fuzzy 语义同 HashIndex**：dump 后
插入覆写节点/混入链 → 物化树可能多出 >W 条目——重数实收（计数以实收为准）+ 重放 (W,End] 幂等
收敛。格式契约族私有（`ISortedIndexCodec`——IXXXCodec 各族私有禁跨族共用），**一个子类一个
Codec 实现 + 独有头尾 magic**（`BTreeIndexCodec`/BIHD·BIFT、`SkipListIndexCodec`/SLHD·SLFT——
配错数据文件在头先行校验即失败，杜绝 Magic+CRC 全过的静默误读）。

**v2 已实施**（接口层与本节用法零变更）：Mirror 体系统一流式帧（双魔术值、零长度字段、CRC 落尾——
两镜像一套格式）+ 写会话真流式化（引擎模式 A Allocate 随写随留 + Write 复写）+ 恢复尾锚主路径 +
MagicLocator 方向性收口（First/Last + 范围 + Linear/Monotone 两档）+ LeadingHoleLocator 退役 +
**基类机制归一**（子类只实现 codec 格式布局——铁律 10）。

---

## 6. Log（EntryLog / DeltaLog）

**本质**：WAL 页帧流（帧头 magic/长度/CRC，组提交策略可注入）。

| | EntryLog | DeltaLog |
|---|---|---|
| 定位 | 通用 WAL（常驻 retention） | KV checkpoint delta（临时） |
| meta 缺省 | Disabled（基类默认；三大类四语义全配） | Transport 未注入（= 自流嵌入 3b，meta 块嵌 log 自身流，merge 时随删） |
| 组提交 | ✅ CommittedOffset / CommitAsync / WaitForCommitAsync | ❌ |
| 截断 | ✅ TruncatePrefix / TruncateSuffix | ❌ |
| 回放 | ✅ Replay / ReplayAsync（verifyCrc 可选） | ❌（游标读） |

### 6.1 公开面（LogBase + EntryLog；数自查：追加 1 对 + 批 1 + Flush 1 对 + 截 2 + 回放 4）

| 组 | 成员 | 说明 |
|---|---|---|
| 追加 | `Append` / `AppendAsync` | 返回 LogicalAddress；超单页抛（entry 不跨页） |
| 批量 | `BeginAppendBatch()` → ref struct AppendBatch | 批内多次 `Append` + 一次 Dispose（页满自动提交契约不因批取消） |
| Flush | `Flush` / `FlushAsync` | 强制当前页落盘 |
| 提交 | EntryLog：`CommitAsync` / `WaitForCommitAsync` / `CommittedOffset` / `LastCommitError` | 显式提交 = flush + 推进 CommittedOffset + meta.Commit |
| 回放 | EntryLog：`Replay` ×2 / `ReplayAsync` ×2 | 只读已提交（不读未提交尾）；verifyCrc 开关 |
| 游标 | `OpenCursor` → ILogCursor | 顺序读 |
| 截断 | `TruncatePrefix` / `TruncateSuffix` | 头截断物理回收 / 尾回退 |
| 2PC | `Prepare` `PrepareAsync` `ConfirmCommitted` `OnCommitted` `Abort` `AbortAsync` | 六件套全实现（Abort=TruncateSuffix 到上一提交边界，见 §10.2） |

组提交策略 `GroupCommitPolicy`（internal，构造注入）：字节量/条数/时间间隔三维阈值任一命中
即提前提交页。

### 6.2 线程模型（★单写者铁律）

| 操作 | 线程安全 |
|---|---|
| Append/AppendAsync/Flush/CommitAsync | ✅ **并发安全（2026-08-24 写路径粗锁）**——串行化保证不损坏；单写者语义保持（-3%）；多生产者直写无需汇聚 |
| TruncatePrefix | ✅ 与 Append 完全并行（Log 层不碰页缓冲；引擎层 lease 区间不重叠） |
| TruncateSuffix | ❌ 与 Append 串行 |
| 读（OpenCursor/Replay） | ✅ 多 reader 并发（各游标独立；ClampReadable 截断在途区间防脏读，不靠锁） |
| 后台 group commit 循环 | ✅ 与写线程共存（只读已落盘水位） |

---

## 7. Metadata（VersionedMetadata）

**本质**：单值元数据的版本化持久化——每次 Write 追加新版本到版本链，N=2 轮转回收
（`ReclaimOldVersions`）；崩溃安全天然（写到一半 = 旧版本完好，magic/CRC 断链容错）。

公开面（数自查：写读 7 + 回收 1 + 2PC 六件套全实现）：
`Write(data)→地址` / `Persist()` / `Read(dst)` / `ReadNoEpoch(dst)` / `AsSpan()` /
`GetRef<T>()` / `GetSpan<T>()` / `ReclaimOldVersions()` + 完整 2PC（Abort 已实现，零 IO 回滚）。

两个消费形态：
1. **直接用**——跨重启的单值状态（水位/配置/锚点）。
2. **3a 外部隔离传输**——`Meta/MetadataMetaTransport` 把任意结构的 meta 块托管到
   VersionedMetadata（meta.md §7 推荐实现，勿自写单槽文件）。

约束：`PayloadSize` ≥ 托管块上界，超限写抛（fail-fast 不截断）；跨重启调大小合法（历史版本
按盘上真实大小交付）。

---

## 8. Mirror（WholeMirror / PagedMirror）

**本质**：任意字节区间的外部镜像存储原语（帧三拍写入 + 帧几何账面定位 + 子类读门面
（ReadChunk/ReadPage），多版本保留 + 2PC 链头推进 + `ReclaimOldVersions`）。

**定位（组合模型）**：完整状态基线的**版本链形态**——按版本号截断（N=2 轮替/PreviousVersion
回跳/Abort 回退版本）；与快照（按字节截断的纯流式形态）同族异坐标系。

**一套格式一套机制（2026-08-23 用户裁决，铁律 10）**：两种镜像统一帧格式（共享
`MirrorFrameHeader` 32B 瘦头 + `MirrorFrameFooter` 40B——双魔术值、零长度字段、帧长=尾位−头推导）；
**机制全部在 MirrorBase**（帧三拍 `BeginFrame/AppendFrameChunk/EndFrame`、帧几何账面、恢复编排、
MetaHost 嵌入、N=2、2PC），**子类唯一实现点 = codec**（magic / CRC 算法位 / ChainKind 链拓扑声明）
+ 业务钩子（链头推进、per-page 字典）：

| | WholeMirror | PagedMirror |
|---|---|---|
| codec | WMHD/WMFT、CRC64、Single 链 | PMVH/PMFT、CRC32C、PerKey 链（PageId 为键） |
| 写门面 | 会话三拍：`BeginSession()` → `AppendChunk(bytes)`×n → `EndSession()`（引擎模式 A：Allocate 随写随留 + Write 复写——**不知尺寸、零缓冲**；EndSession flush） | 逐页 `WritePage(page, startPage, bytes)`（每页一个帧，走同一三拍；不逐页 flush） |
| 读门面 | `ReadChunk(frameHead, offsetInPayload, dst)` + `GetFrameInfo`/`GetPayloadLength` + `Verify` | `ReadPage(page, startPage, dst)` → (bytesRead, isValid)（帧几何推导 payload 长 + 全帧 CRC） |
| 恢复（基类编排） | **尾锚主路径**：`Locate(尾 magic, Last)` 直达最新数据帧 → 倒扫头 + CRC 全验 → PreviousVersion 回跳 N=2 第二新（旧代再烂遮不住新代）；空手走链兜底 | **全走链**：逐帧（验头→找尾→头尾版本一致）按 PageId 重建各页链头；假命中重同步 |
| 场景 | 整体快照外存（尺寸写时未知的流式场景） | 分页增量镜像 |

两形态 2PC 六件套**全实现**（Abort = 丢弃未确认会话，幂等）。

---

## 9. Snapshot（StreamSnapshot）

**本质**：完整状态基线的**纯流式形态**——按字节截断（区间读写/字节位置裁剪，无版本概念）；
与镜像（按版本号截断的版本链形态）同族异坐标系。SnapshotBase 提供地址化读写面，StreamSnapshot
落成帧（帧头 SNHD + 帧尾 CRC64，反向扫帧尾找文件尾）。

- SnapshotBase 面数自查：写 2 对（Append/Overwrite）+ 读 1 对（Read）+ 截 2 + 回收 1 + 2PC 六件套
  （全实现）。
- StreamSnapshot：`OpenWrite` / `OpenWriteRange` → StreamFrameWriter（Write + Complete），
  `OpenRead` / `OpenReadRange` → StreamFrameReader。
- 与 Ring 自带的 `OpenSnapshotReader/Writer`（§3.1）无引用关系——那是 Ring 区间导出能力；
  Snapshot 子体系是独立文件形态的快照存储。

---

## 10. 生命周期与 2PC

### 10.1 生命周期（全部结构统一，LifecycleBase 骨架）

```
构造（=配置，零 IO、零虚调用）
  → Initialize(hints?)（启动：引擎/镜像引擎并行非阻塞 Initialize）
  → WaitForReady（恢复核心 join：WaitForDependenciesAsync → meta Load → 扫盘兜底）
  → 使用 → Dispose（幂等）
```

**恢复优先级（全部结构统一）**：hints（调用方最强知识）→ meta（自管持久化水位）→ 扫盘兜底
（细节 meta.md §6）。每结构 RecoveryHints：Log/Ring/Metadata/Mirror/Snapshot/ProbingIndex/
SortedIndex 各一组（窗口/水位字段不同，语义同构）。

### 10.2 跨结构 2PC（TransactionLog 编排）

`Transactions/TransactionLog` 写独立 commit record 文件驱动参与者的
`ITransactionParticipant` 六件套：`Prepare(seq)` → `ConfirmCommitted(seq)` / `Abort(seq)`
（+Async 各一）+ `OnCommitted(seq, callback)`（已提交到更高 seq 则立即触发）。
协调器三处会调 Abort：显式 `TransactionLog.Abort()`、Commit Phase-1 失败（自动回滚已
Prepare 的参与者）、`LoadAndReconcile` 恢复裁决（悬干丢弃）。

**全部结构六件套齐**（D2 落地后 Log/Ring 也支持回滚）：

| 结构 | Abort 语义 |
|---|---|
| Log | TruncateSuffix 回退到**上一已确认提交边界**（meta PreparedTailAddress 持久化；EntryLog 的 CommittedOffset 一并夹回） |
| Ring | TruncateSuffix 回退到**上一已确认提交边界**（meta CommittedTailAddress 持久化；引擎回收+水位条件回退+页池清零+冷缓存失效） |
| Metadata | 零 IO 回滚（丢弃未确认版本，幂等） |
| Mirror | 丢弃未确认写会话（幂等） |
| Snapshot | 丢弃未确认追加 |
| 两族索引 | seq 记账复位（派生数据可重建，无 IO） |

**Abort 窗口契约（Log/Ring）**：上一提交点之后的全部追加必须都属于被回滚的事务——标准
2PC WAL 契约，TransactionLog 协议天然满足（Prepare 与终态之间无写入）；混入非事务写会被
一并回退。守卫矩阵：已提交 seq → no-op；陈旧 seq / 无既有提交边界（首事务）/ 无悬干数据 →
仅复位记账不截断。**恢复还原**：meta 里的 LastCommittedSeq/LastPreparedSeq/提交边界在重开时
还原（悬干对 `LoadAndReconcile` 可见——prepared > committed 即驱动 Abort）。

### 10.3 Session 统一协调协议（组合域编排层）

跨结构 2PC 的**上层编排**归 `Transactions/SessionManager`（统一协调协议层——读写检查点三 op
的唯一进出；结构对会话零感知，六件套仍是它们的全部配合面）：写=staged 物化委托经单飞提交管线
（排空批合并、同批共享 seq、FIFO 全序）；检查点=管线串行回合；悬挂裁决按域声明
（forward-commit 前推缺省 / DropTail）。结构另暴露 epoch 读保护协议（`IEpochProtected`：
`EnterEpoch`/`ExitEpoch`——Ring/两族索引基类实现，ref struct scope 同真源）供会话读 scope
聚合。使用指南与故障模型见同目录 **`session.md`**。

---

## 11. 完成度现状（2026-08-22 缺口收尾后）

| 项 | 状态 |
|---|---|
| 六族结构 2PC 六件套 | ✅ 全实现（Log/Ring Abort=D2 已落地，见 §10.2；13 个 Abort 测试 + TransactionLog Phase-1 失败集成测试全绿） |
| Log/Ring 恢复事务水位还原 | ✅ meta 的 seq/提交边界重开还原——悬干对 LoadAndReconcile 可见并驱动 Abort |
| Structures 测试门禁 | ✅ 整目录默认编译（Runtime 全量 843 例绿·mem ~15s）；大规格 Scale 类物理归 AdversarialTests（Category=Scale，手动/Nightly） |
| 旧架构遗留 | ✅ 已清（死契约 ILogRecovery、csproj 陈旧排除、孤儿测试 RingCapacityBoundary/RingRecoveryScale 旧 API 版、旧架构基准 40 文件） |
| Log/Mirror/Snapshot 的 src 消费者 | ⏳ 成品件待组合层接线（测试覆盖完备：36/24/12 例）——设计如此（组合归组合层），非缺口 |

其余：全解决方案构建零错误；magic 注册表 23 项集中自描述；每持久化头三件套
（Magic/Version/Flags-CRC）齐备。

---

## 12. 铁律与反模式

1. **写编排正序**：先 `ring.Write` 得地址、再 `index.Insert`——反向 = 索引指向不存在的数据。
2. **零拷贝 span 只活在 scope 内**：`GetValueSpan` 返回值禁止逃逸 `ReadScope.Dispose()`；
   溢出形态的 span 下次同线程调用即覆盖。
3. **地址一等公民**：16B LogicalAddress（seg/offset/ext）不可拆、不做 8B 紧凑编码；上层
   自缓冲地址后直达取值永久跳过 hash——hash 路径只是发现地址的一次性成本。
4. **Log 单写者**：多生产者经队列汇聚单写线程；勿并发调 Append。
5. **索引增长前检查阈值**（§5.1）；增长时刻表内条目必可经 KeyResolver 解析。
6. **跨实例组合 DeleteOnClose=false**（§5.4 SortedIndex 镜像坑；HashIndex 主存储同要求）。
7. **Abort 窗口契约**（§10.2）：上一提交点之后的追加必须都属于被回滚的事务；Abort/TruncateSuffix
   与写入串行调用（事务终态点）。
8. ❌ **绕过 [RingKey] 直接继承开放泛型**——封闭薄类是唯一消费面（分析器诊断不满足 unmanaged
   约束的声明）。
9. ❌ **为索引手搭 checkpoint 引擎/存储**——HashIndex 走自建主存储（内置，结构核心能力）；
   SortedIndex 镜像走 `ITransferPersistence` 注入（铁律 8 桥接判据）。主存储/dump 设计见
   §5.4。
10. **每结构 Settings 统一继承 Settings 基类**：MetaPolicyKind/MetaOpaqueBytes 是公共配置，
    meta 语义全档见 meta.md，勿在结构层自写 meta 落盘。
