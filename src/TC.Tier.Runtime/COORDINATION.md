# TC.Tier.Runtime 协调框架使用指南

> 本文档回答："Runtime 怎么用（数据结构是主面）、遇到 X 选哪个积木、什么绝对不要做"。
> 自足到**读完能开始用**；深入细节才看 §8 索引的独立文档。不重复 XML 注释（那是成员级语义）。

---

## 0. 一句话总纲

**TC.Tier.Runtime = 存储内核运行时。使用主面 = 数据结构层**（Ring / 索引两族 / Log / Metadata /
Mirror / Snapshot——结构自管引擎、meta、恢复，消费者只碰结构 API）；**存储引擎直连 = 外部高级
扩展**（自研结构/特殊存储形态）；段表是引擎内部的地址空间真相，业务不触达。横切件：Meta
（元数据）、Transactions（2PC/会话）、DataMirror（镜像桥）。

```
业务层（Products / TierKv）
   │   结构 API（Ring / 索引两族 / Log / Metadata / Mirror / Snapshot）      少数场景直连 IStorageEngine
   ▼
Structures/（数据结构层——使用主面；规范见 §4）
   │   每结构内建引擎（结构层 internal 构造，外部经 Options → Builder）
   ▼
Storage/（StorageEngine——IO 引擎：生命周期/池化句柄/恢复/Compact；文件系统 = 注入哪个 IFileSystem）
   │   唯一通道：lease 协议（六类型）                    统一事件 ←→ 建段回调
   ▼
AddressSpace/（段表——地址空间唯一真相：水位线 + 段 + 段区间）
```

| 读者路径 | 入口 |
|---|---|
| **用数据结构（主路径）** | §1 快速上手（可抄代码起步）→ [`docs/structures.md`](docs/structures.md) |
| 直接用存储引擎（高级扩展） | §5 → [`docs/storage-engine.md`](docs/storage-engine.md) |
| 结构元数据 / 跨结构原子·会话 | [`docs/meta.md`](docs/meta.md) / [`docs/session.md`](docs/session.md) |
| 引擎内部（段表/lease 协议） | [`docs/segment-table.md`](docs/segment-table.md) / [`docs/lease-protocol.md`](docs/lease-protocol.md) |

---

## 1. 快速上手（从零跑通——可直接抄）

**单结构直用**：

```csharp
using TC.Tier.Core.IO;
using TC.Tier.Runtime.Storage;
using TC.Tier.Runtime.Structures.Ring;

// ① 卷：文件系统 = 一根 spec（memory 极速 / local 本地 / virtual 单文件 / network 网络）
using var fs = TierFs.New("local:///data/myapp");

// ② 程序集声明一次封闭形态（[RingKey] 源生成器 → RingOfLong / HashOfLong 等；long 已内置）
[assembly: RingKey(typeof(long))]

// ③ Create = 构造 + Initialize + WaitForReady 一步到位
using var ring = RingOfLong.Create(
    new BlittableRingSettings(new StorageEngineOptions("my-ring")), fs);

// ④ 写读：追加写返回逻辑地址（多线程并发安全）；地址直达取值
var addr = ring.Write(42L, "hello"u8);
ring.GetValue(addr, buf);

// ⑤ 崩溃后重开同一卷：Create 内 Initialize 自动恢复（§4.2 三级回退）——零恢复代码
```

**组合 KV**（Ring=真相源，索引=派生——写先真相后派生，读两段合口径）：

```csharp
using var index = HashOfLong.Create(fs,
    new HashIndexSettings(new StorageEngineOptions("my-hash")), keyResolver: ring);

var addr = ring.Write(key, value);                    // 写：① Ring.Write 得地址
index.Insert(key, addr, LogicalAddress.Empty);        //     ② index.Insert（派生）

var hit = index.Find(key);                            // 读：① index.Find 命中
if (hit != LogicalAddress.Empty) ring.GetValue(hit, buf);   // ② ring.GetValue 取值
```

**生命周期三句话**：构造=配置（零 IO）→ `Initialize()` 启动后台恢复（非阻塞）→ `WaitForReady()`
等就绪（`Create` 工厂一步到位）；`using`/Dispose 幂等。恢复全自动，无需调用方代码。

---

## 2. 积木全景

**核心（结构 + 横切——使用主面）**：

| 积木 | 位置 | 职责 | 何时用 / 指向 |
|---|---|---|---|
| **Structures 数据结构层** | `Structures/` | Ring / 索引两族（Hash·BTree·SkipList）/ Log / Metadata / Mirror / Snapshot | 使用主面 → [`docs/structures.md`](docs/structures.md) |
| **TransactionLog** | `Transactions/` | 跨结构 2PC 协调（独立 commit record 文件） | 需要原子跨结构变更、自带中央 record 裁决时 |
| **SessionManager** | `Transactions/` | 统一协调协议层：读写检查点三 op 唯一进出——staged 物化 + 单飞提交管线（批合并 FIFO 全序）+ 会话读 scope + 检查点回合 + 悬挂裁决 | 组合域（产品面）编排 → [`docs/session.md`](docs/session.md) |
| **Meta 统一元数据核心** | `Meta/` | 三模式（Disabled/Managed/Transport）+ `IMetaTransport` 传输契约——结构化元数据持久化的独立能力 | 任何需持久化元数据的场景 → [`docs/meta.md`](docs/meta.md)（引擎侧段元组已内联 FileExtra，两者无关） |

**存储引擎与地址空间（高级扩展 / 引擎内部——业务一般不直接触达）**：

| 积木 | 位置 | 职责 | 何时用 / 指向 |
|---|---|---|---|
| **StorageEngine** | `Storage/` | 唯一引擎类（sealed partial）：生命周期 + worker + 池化句柄 + IO + 恢复 + Compact + Reclaim；文件系统 = 构造哪个 `IFileSystem`（四类文件系统平权），引擎零文件系统分支 | 外部高级扩展（自研结构/特殊形态）→ [`docs/storage-engine.md`](docs/storage-engine.md) |
| **SegmentTable** | `AddressSpace/` | 地址空间真相：水位线 + 段 + 段区间；只发事件只收回调 | 引擎内部 → [`docs/segment-table.md`](docs/segment-table.md) |
| **lease 六类型** | `AddressSpace/Leases/` | Append/Write/Reclaim/ReclaimHead/ReclaimTail/Compact——IO↔段表唯一协议 | 引擎内部 → [`docs/lease-protocol.md`](docs/lease-protocol.md) |
| **段元组（FileExtra）** | 段文件自身 | per-segment 元组（State/水位/extent 摘要）内联段文件同步强一致直写 | 引擎内部（建段/段满/Reclaim/Dispose 五时机点） |
| **段预备池** | `Storage/…SegmentPool.cs` | lookahead 物理预建（写者零等待）+ build-gate single-flight | 引擎内部；默认开 |
| **MagicLocator** | `Storage/MagicLocator.cs` | 方向性 magic 定位（First/Last + `[from,to)` 使用方范围 + Linear/Monotone 两档）——不透明字节，零/非零全是有效数据，零格式假设 | 恢复扫盘定位粗锚点（Ring 找尾 / Mirror 尾锚与帧走链 / Metadata 链头）；**禁非零谓词扫描** |

---

## 3. 选型路由

| 需求 | 用 | 不用 |
|---|---|---|
| 存 KV / 可重建索引 | Ring（真相源）+ 索引两族组合（范式 §1；写=Ring.Write→index.Insert，读=index.Find→GetValue） | 自造 hash 表直连引擎 |
| 点查（极省内存）/ 有序遍历·range | `HashIndex<TKey>`（探测族，判等经 KeyResolver 回源）/ `BTreeIndex`·`SkipListIndex`（比较族，key 物化） | 拿错族（选族即选消费形态） |
| WAL / 单值元数据 / 镜像 / 快照文件 | `EntryLog` / `VersionedMetadata` / `WholeMirror`·`PagedMirror` / `StreamSnapshot` | 直连引擎手搓 |
| 结构元数据 / opaque 搭车水位 | `Meta/` 三模式（[`docs/meta.md`](docs/meta.md)） | 直写文件 |
| 借用同类型结构的能力（镜像存储/元数据/…） | **桥接模式**：注入公共契约（`ITransferPersistence` / `IMetaTransport`）+ 横切桥接器（`Runtime/DataMirror/` / `Runtime/Meta/`） | 为借能力私建存储引擎（铁律 8） |
| 恢复扫盘定位（找头/找尾） | `MagicLocator.Locate`：零富集/未知布局 → Linear（恒正确）；稠密 record 流/前缀洞（含 magic 页单调，使用方断言）→ Monotone（O(log 页数)） | 非零谓词扫描（"零=没数据"是格式判断） |
| **——以下为引擎直连（高级扩展）——** | | |
| 追加写 | `AppendLease`；高频批量 → Allocate+WriteLease（快 ~23%） | 手拼地址 + WriteLease 越过 CommittedTail |
| 覆写已知地址 | `WriteLease` | 直接写文件（绕过区间状态机） |
| 释放空间 | `ReclaimLease` 打洞 | 手改区间记录 |
| 删最老数据 / truncate | `ReclaimHeadLease` / `ReclaimTailLease` | 互为替代（一个动头一个动尾，语义不同） |
| 空洞压缩 | `CompactLease`（写放大恒 1.0×） | 手搬数据 + 手替换段 |
| 引擎构造 | `new StorageEngine(fs, options)`（结构层 internal）或外部 `options.Builder(fs).StartAsync()`——文件系统 = 构造哪个 `IFileSystem`（四类文件系统平权，换文件系统 = 换一行 fs 构造） | 引擎子类/文件系统枚举分支 |

---

## 4. 数据结构规范（核心——契约 + 只碰这些 + ❌/✅）

> 对齐 Core §2 的写法。细节见 [`docs/structures.md`](docs/structures.md) 与 [`docs/meta.md`](docs/meta.md)。

### 4.0 组合模型（2 主结构 + 4 搭配件 = 产品发生器）

```
主结构（持真相数据——产品骨架）          搭配件（派生/外挂/加速——经桥单向依赖、可摘）
├─ Ring = 数据真相源（record 流）        ├─ 索引（Hash/BTree/SkipList）——桥：IKeyResolver
└─ Log  = 操作流（EntryLog WAL/DeltaLog）├─ 元数据（Meta/VersionedMetadata）——桥：IMetaTransport/opaque 搭车
                                         ├─ 镜像（Mirror）= 完整状态基线·版本链形态——桥：ITransferPersistence
                                         └─ 快照（StreamSnapshot）= 完整状态基线·纯流式形态——流源：OpenSnapshotReader

双骨架配方（产品=配方，零新存储件）：
  Ring 骨架（数据产品）：Ring + 索引(加速) + 镜像(恢复加速) + 元数据(水位)     → KV/Queue/TimeSeries
  Log 骨架（WAL/协议产品）：Log + 元数据(协议状态·与日志原子) + 快照(日志压缩)  → Raft WAL 同构
    （快照落盘 → TruncatePrefix 截日志前缀 = 日志压缩；Raft 产品零新存储）

恢复统一模型：载快照/镜像/主存储帧（到水位 W）+ 重放 (W, 尾]——HashIndex 主存储载帧+增量重放、
SortedIndex 镜像载像+增量重放 已是实例（索引持久化=结构核心能力）。

Checkpoint 统一概念（结构角色 × 落点形态两参数）：派生结构×引擎内版本链=加速（可摘——
重放兜底）；主结构×外部工件=容灾（核心能力——主数据完整重建的唯一路）。四实例：索引主存储
（HashIndex 内置 dump；BTree 自有节点持久化）、结构元数据水位、主结构备份导出、卷级镜像
（Fs 层 RootSpaceImage）。

结构主存储与传输通道两域分立：主存储=每个结构自建自管（Ring 页池/Log 帧流/BTree 节点/
Metadata·Mirror 版本链/HashIndex 主存储【可关——派生红利】），格式全族三段式帧、落盘时机各异
（写路径组提交/会话 checkpoint/后台协作 dump）；传输通道（ITransferPersistence）=主数据结构的
迁移/同步通道（终局：分布式数据面全量迁移+增量同步；备份=本地特例）。

三段式传输公共对（Contracts/Structures——ICommonReaderWriter + ITransfer*/IAsyncTransfer* 家族）：
头=格式先行声明（读侧只认自己的头）、体=不透明、尾=总验收+原子完成点（Complete(false)=Abort）
——"只信任自己的格式"的接口化。**轨道判据：IO 经过内存 → 同步轨；不落内存（冷设备 IO）→ 异步轨；
通道按本性实现单轨（镜像桥=同步、快照=异步、导出=异步）；管道两侧同轨直连，跨轨适配归组合层**。

镜像 vs 快照（同族=完整状态基线；核心差异=截断坐标系）：
  快照 = 按字节截断（纯流式存储——区间读写、字节位置裁剪，无版本概念）；
  镜像 = 按版本号截断（版本链——N=2 轮替/PreviousVersion 回跳/Abort 回退版本，机制全为版本坐标系服务）。

两条硬约束：① 主结构对搭配件零知识（Ring 不知道索引存在——搭配方向永远单向）；
② 搭配件存在性=优化非正确性（摘掉任何搭配件产品仍正确——正确性只由主结构+重放模型保证；
Log 骨架的元数据例外：协议状态是正确性的一部分，须与日志原子持久化——opaque 搭车/2PC）。
```

### 4.1 生命周期与引擎装配（三段式，全结构一套）

```
构造（= 配置，零 IO）：new StorageEngine(fs, settings.MainEngine)（结构层 internal）+ meta 策略装配
    （metaPolicyFactory ??= CreateMetaPolicyDefault——方法组，禁匿名 lambda）
  → OnInitializeBegin：全部引擎（主/溢出/meta）并行 Initialize（非阻塞、不等待）
  → 恢复核心 WaitForDependenciesAsync：双 await join 子引擎就绪（全异步轨，零同步阻塞）
```

第 n 个引擎同规。**只碰这些**：构造器注入引擎与策略；OnInitializeBegin 启动；恢复核心 join。

- ❌ 构造期 IO / 同步等待就绪；❌ 外部隔层参与引擎内部事务（Fs 是空间根，引擎是结构内部细节——
  外部水位注入的唯一正位 = 结构 `Initialize(hints)`）。

### 4.2 恢复编排（RecoveryBase 模板 + 三级回退）

恢复算法 = `RecoveryBase<THints>` 派生，只 override `OnRecoveryCoreAsync`（唯一必项）与
`WaitForDependenciesAsync`（层间 join）——CAS 闸门/状态机/进度/MarkReady 全在模板
（**骨架=信任边界**）。恢复优先级全结构统一：

```
hints（调用方最强知识） → meta.Load（O(1) 水位） → 扫盘兜底（magic 定位候选 + 结构/CRC 裁决）
```

- ❌ 裸写 IRecovery 手搓状态机——模板已锁死时序（恢复水位应用前放行 = 满套挂/隔离绿 = 时序 bug）。
- ❌ 结构 Settings 透传引擎恢复尾水位 hint（设小 = 引擎按它截断物理尾 → 有效数据被切）。
  物理真相引擎自恢复，逻辑水位结构自管。

### 4.3 写模型（模式 A 默认 / 模式 B 专用）

- **模式 A（默认）**：`Allocate` 圈地（近免费）→ `CalculationAddress` 定槽 → `Write` 复写
  （址可无限次重写）。数据有页/槽概念的结构一律 A——不相交区真并行、吞吐 11.5 GB/s（64KB）。
- **模式 B（Append）**：纯顺序 WAL 专用（每笔付 lease+双尾推进，1.6 GB/s）。
- ❌ 别上来就 Append（storage-engine.md 禁忌 8）；❌ 手算 Offset 差（地址算术唯一正道 =
  `CalculationAddress` / `GetDistance`）。

### 4.4 记录格式与 codec 契约（内统一、外桥接）

- **持久化结构定义标准（勿另造）**：struct + `[StructLayout(Explicit)]` + `[BinaryLayout]` 源生成
  （`{Name}Codec` 偏移/读写编译期生成）；magic 统一登记 `RecordMagic`（uint32 全树唯一 ASCII 可辨识）；
  版本 `(major<<8)|minor`；一结构一文件；Settings 字段名对齐惯例（引擎选项=MainEngine、
  meta 族=MetaPolicyKind/MetaOpaqueBytes）。
- **流式帧统一**（Mirror 体系范本）：双魔术值（头+尾）+ **推导长度**（帧长=尾位−头，格式零长度
  字段）；帧判定链零长度依赖——magic 只提名候选，结构+CRC 才是裁决，假命中重同步。
  范本：WholeMirror / PagedMirror 共享 `MirrorFrame`（差异只在 codec：WMHD/WMFT vs PMVH/PMFT、
  CRC64 vs CRC32C、Single vs PerKey 链）。
- **基类=机制容器**：子类唯一实现点 = codec（格式布局）+ 业务钩子（数据结构语义，如 per-page
  字典）。机制按子类分叉 = 基类空心化 = 两套格式两套校验两套扫描。
- **跨体系格式互不相认，桥是唯一握手点**：像格式↔镜像存储（`ITransferPersistence`）、meta 块↔宿主流
  （`IMetaTransport`）、索引↔Ring（`IKeyResolver`）——桥只做相位/协议映射，内容有效性只由消费方
  格式裁决。解耦判据：改任一侧格式，另一侧零感知。新增跨格式协作先问"能不能变成桥"。

### 4.5 版本链 / N=2 / 2PC

- **版本链 + N=2 轮替**：每次提交追加新版本，Confirm 后立即头截断回收最老（文件恒定 2 倍空间）；
  Abort 尾截断物理回退悬干。链尾哨兵 = `LogicalAddress.Invalid`（Empty 是合法 seg0@0 不能当哨兵）。
- **持久化两形态**（只此两种）：完全注入接口（段表 IAddressTableReader/Writer 范本）或结构内建
  引擎（MirrorBase 双引擎范本）——禁 helper 自持引擎 + 迷你生命周期。
- **2PC**：结构实现 `ITransactionParticipant` 六件套；跨结构原子走 `TransactionLog`（独立 commit
  record 裁决）。

### 4.6 Meta 持久化（三模式 + opaque 搭车）

| 模式 | 形态 | 何时用 |
|---|---|---|
| **Disabled** | 无 meta，恢复走扫盘兜底 | 临时/派生数据（DeltaLog） |
| **Managed** | 独立 meta 引擎（块几何定单段容量：align4K(header+水位+OpaqueCapacity+footer)） | 默认持久化水位 |
| **Transport** | `IMetaTransport` 注入；未注入回落 MetaHost 嵌入宿主流（嵌入 = 宿主格式 + IS_META） | meta 块寄宿主流（Log/Mirror） |

- **opaque 搭车**：`SetOpaqueMeta` 搭结构水位同一块同一 CRC 原子提交（无独立提交路径）；
  需确定性持久化点走 Prepare/ConfirmCommitted。
- ❌ 直写文件存元数据；❌ 自建单槽文件（3a 托管 = `Meta/MetadataMetaTransport` 推荐实现）。

---

## 5. 存储引擎（高级扩展）——内部边界与路由

> 引擎直连是外部高级扩展场景（自研结构/特殊存储形态）；数据结构使用者经结构 API 间接使用引擎，
> 本节不必读。使用 → [`docs/storage-engine.md`](docs/storage-engine.md)；内部机制 →
> [`docs/segment-table.md`](docs/segment-table.md) / [`docs/lease-protocol.md`](docs/lease-protocol.md)。

### 5.1 核心架构边界（三条）

1. **逻辑层 / 物理层边界**：段表不关心文件、句柄、池、线程——物理概念全部留在 `Storage/`。
   反向同样成立：IO 层**只能**经 lease 协议改段表状态（`ILeaseSource` 显式接口，外部类型不可见），
   经事件契约感知段表变化，经 `CreateSegmentCallback` 回报物理结果。
2. **等待的唯一宿主是 lease 协议**：物理门在 chunk 第一拍/提交扫尾；worker 零等待零重试、
   池零等待、段表零等待。
3. **类型即协议**：六 lease 各自表达对段表/物理段/稳态的要求；禁止 kind 路由、禁止合并、
   禁止把某类型的要求漏进共享路径。

### 5.2 lease 决策树

```
要改地址空间状态？
├─ 否（只读）→ SegmentTable 只读查询（GetSegment/IsRangeFullyReadable/GetExtentRanges）
└─ 是 → 走哪个 lease？
    ├─ 写 → 地址谁定？段表定 → AppendLease ｜ 已知（≤CommittedTail）→ WriteLease
    │        （高频小写批量场景：AllocateLease 定地址 + WriteLease 批量）
    ├─ 收空间 → 中间区间 ReclaimLease ｜ 头部 ReclaimHeadLease ｜ 尾部 ReclaimTailLease
    ├─ 压缩空洞 → CompactLease（整体提交）
    └─ 物理段怎么就绪？→ handler 事件 → worker/池（single-flight）→ CreateSegmentCallback
```

---

## 6. 铁律（Runtime 全域）

1. **外部拿 `SegmentView`，不拿 `Segment`**；改状态唯一入口 = lease 协议。
2. **建段 single-flight**：同一 segId 物理构建恰好一次——取用/守卫/声明同临界区（四态一锤定音）。
3. **共享数组发布 build-then-publish + acquire/release 全对称**（ARM 弱序合规）：写侧
   `_segIndex`/`_segments`/`_segCount`/槽位全 `Volatile.Write` 单点发布；读侧对索引**字段与槽位**均
   `Volatile.Read`——任一环 plain load 在 ARM 上可见中间态。
4. **回调幂等**：`CreateSegmentCallback` 双分支 CAS，非 Empty no-op——重复/迟到回调不打断已迁移段。
5. **Compact 后段槽复用是设计路径**（中间段 Invalid → 退尾再分配重注册）——不是缺陷，勿"修"。
6. **排他**锁临界区内**绝对禁止 await**（SpinRWLock 线程关联，同 Core 反模式 11；共享锁可跨 await
   长持——读计划锁即此用法）。
7. **16B 裸读不得用于判定**：`Atomic128.ReadUnsafe` 与其它无屏障 16B 读可被 JIT CSE/重排、可撕裂——
   越界判定/水位比较/几何决策一律走屏障稳定读；CAS 的 expected 值、同线程因果内读可裸读但须注释
   说明。跨线程可变几何（`GrowthLimit` 等）读侧必须 `Volatile.Read`。
8. **桥接判据（借能力 ≠ 持引擎）**：借用同类型结构的能力走桥接模式（注入公共契约 + 横切桥接器），
   **禁止为此私建/私持存储引擎**——只有承载本结构核心数据的引擎才允许持有（判据不是引擎数量，
   是引擎里装的是谁的什么）。
9. **记录格式：长度不进格式，推导是事实**——流式帧统一（双魔术值头+尾，帧长=尾位−头推导）；
   "写时已知长度"是写侧便利与内存账面，不进盘上格式（禁止为存长度前置询问尺寸）。详见 §4.4。
10. **基类=机制容器，子类只填 codec 格式布局**——机制逻辑（恢复扫描/嵌入 meta/尾锚/几何推导）
    禁止做成子类 override；格式差异（magic/头尾布局/链拓扑/CRC 算法位）收敛到 codec。详见 §4.4。

---

## 7. 反模式（禁止重蹈）

**数据结构层**：

- ❌ **裸写 IRecovery 手搓状态机**（§4.2）：骨架=信任边界——满套挂+隔离绿+复现 = 模板钩子漏，
  不是"压测不稳定"。
- ❌ **非零谓词扫描**（"零=没数据"是格式判断）：零是合法数据形态（索引空桶区 99% 为零）——
  定位一律 magic（`MagicLocator`）。
- ❌ **Empty 地址当"没有值"哨兵**：Empty = 合法 seg0@0（首 record 就在那）——无值表示用
  `LogicalAddress.Invalid`，存在性用标志位/字典存在性判。

**引擎内部**：

- ❌ **在段表上开物理后门**（塞文件路径/池深度/等待进段表）——历次 IO 失稳的根因层；
  正解走事件契约 + 回调。
- ❌ **等待放错层**：worker/池等 Ready、段表内自旋等建段——历史死锁家族全部源于此；
  等待只属于 lease（物理门）。
- ❌ **Invalid 段重建**：过期任务 claim 已回收段 = 复活已删文件；守卫并入声明临界区。
- ❌ **`Array.Resize` 扩共享索引**：零填充窗口 → 无锁读者见幽灵索引 → 段表永久空洞；
  build-then-publish 单点发布。
- ❌ **合并 lease 协议 / kind 路由**：任一类型要求泄漏成其它类型隐藏前提。
- ❌ **以为 lease 是瓶颈去微优化**：实测 ~1.5µs/112B、IO 占比 <7%、2 线程近线性零锁争用
  ——优化预算在 IO 引擎层，不在协议层。

---

## 8. 文档索引

| 文档 | 状态 |
|---|---|
| [`docs/structures.md`](docs/structures.md) | ✅ Structures 使用指南（组合 KV/选型/恢复协议/反模式） |
| [`docs/storage-engine.md`](docs/storage-engine.md) | ✅ 存储引擎使用指南（快速上手/两模式/持久化/恢复/禁忌） |
| [`docs/segment-table.md`](docs/segment-table.md) | ✅ 段表使用指南（三支柱/稳态/事件契约/铁律/范式） |
| [`docs/lease-protocol.md`](docs/lease-protocol.md) | ✅ lease 协议使用指南（六类型/三阶段/三态迭代/性能） |
| [`docs/meta.md`](docs/meta.md) | ✅ Meta 统一元数据核心使用指南（三模式/IMetaTransport/契约矩阵） |
| [`docs/session.md`](docs/session.md) | ✅ 会话管理使用指南（三 op 编排/批合并/悬挂裁决） |
| [`docs/perf/storage-engine-perf-baseline.md`](docs/perf/storage-engine-perf-baseline.md) | ✅ 引擎/段表性能基线 |
| [`docs/perf/structures-perf-baseline.md`](docs/perf/structures-perf-baseline.md) | ✅ Structures 性能基线 |
