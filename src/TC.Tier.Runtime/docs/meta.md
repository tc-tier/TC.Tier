# Meta（统一元数据核心）使用指南

> **适用范围**：所有需要持久化结构化元数据的使用方（Log/Ring/Metadata/Mirror/Snapshot 及任何新场景）。
> 回答："Meta 是什么、三大类四语义怎么选、怎么配置怎么用、块格式标准、opaque 搭车规范、
> Disabled 时结构是否完整、水位线什么关系、什么绝对不要做"。
> Meta 是**独立能力**——结构化水位 + 外部 opaque 扩展 + CRC 完整性，不限于任何特定结构。

---

## 0. 一句话总纲

**Meta = 四段自描述块的统一元数据持久化协议**：

```
[统一 Header 12B 纯规范][内部水位线（结构化）][外部 opaque（实际用量）][统一 Footer CRC 4B]
```

你只配两项（`Settings` 基类公共配置）：**存哪**（`MetaPolicyKind`，三大类四语义）和
**opaque 多大**（`MetaOpaqueBytes`，写侧容量）。剩下的（自描述定位、CRC、opaque 搭车水位线
原子落盘、跨重启容量调整）全部由策略负责。**头部自描述是统一二进制布局的核心**——变化的值
（容量等启动配置）绝不参与盘上几何。

---

## 1. 三大类、四语义（选型矩阵）

| # | 大类 | 语义 | meta 写到哪 | 主流水位线影响 | 流增长代价 | 恢复 |
|---|---|---|---|---|---|---|
| 1 | `Disabled`（默认） | 无 meta | 不写 | 零 | 零 | 扫盘重建（见 §5） |
| 2 | `Managed` | 自管隔离 | 独立 `.meta` 引擎（主引擎名+".meta" 子目录，恒单段） | **零**（主流完全不知情） | 零 | O(1) 读水位 |
| 3a | `Transport` + 注入 `IMetaTransport` | **外部隔离** | 调用方的外部介质（KV/远程/单槽文件） | **零**（结构外介质，主流零接触） | 零 | O(1)（传输读最后块） |
| 3b | `Transport` 未注入（回落 MetaHost） | **自流嵌入** | 带 `IS_META` 标记的 record/entry 写进结构自身流 | **纠缠**（见 §6.3） | **每次水位提交追加一块**（靠 TruncatePrefix/头截断回收） | 倒扫主流找最后 meta 块 |

选型要点：
- **要 O(1) 恢复且不想让调用方管介质** → `Managed`。
- **meta 要跟业务数据放一起（外部一致性/主备）** → `Transport` + 注入传输（外部隔离）。
- **不想多一个文件/外部依赖，接受主流被 meta 块穿插** → `Transport` 不注入（自流嵌入）。
  代价：流按提交频率增长（每块 ≈ align4K(12B+水位+opaque+4B)），必须定期头截断回收。
- **不需要跨重启水位（可扫盘重建 / 上层自管 hints）** → `Disabled`（§5 完整性说明）。

---

## 2. 快速上手

### 2.1 配置（Settings 基类统一，全部结构同款）

```csharp
var settings = new WholeMirrorSettings(
        new StorageEngineOptions("my-engine", 64L << 20, enableSegmentation: false))
{
    MetaPolicyKind = MetaPolicyKind.Managed,   // 三大类：Disabled（默认）/ Managed / Transport
    MetaOpaqueBytes = 256,                     // 外部可写 opaque 容量；启动后不可改，重启可调
};
using var mirror = new WholeMirror(vol.Fs, settings);
mirror.Initialize();          // 构造期已装配策略（构造=配置）；恢复核心 Load
mirror.WaitForReady();
```

### 2.2 opaque 搭车（外部记录随水位线原子落盘）

```csharp
mirror.SetOpaqueMeta(myRecordSpan);   // ★ 登记（stage）——不是独立落盘！
// ……之后任意一次水位提交（数据路径 commit / 2PC ConfirmCommitted）都会
//    把 当前水位 + opaque 写进同一块（同 CRC，原子）。
byte[] got = mirror.ReadOpaqueMeta().ToArray();    // 读最近已提交块的 opaque（Empty=无）
```

- **确定性持久化点**：显式触发一次提交——Log 用 `CommitAsync()`；Metadata/Mirror/Snapshot 走
  2PC `Prepare`+`ConfirmCommitted`（纯 opaque 无数据时提交的仍是**完整块**：当前水位原样携带 + opaque）。
- **Disabled 拦截**：`SetOpaqueMeta` 抛 `InvalidOperationException`（禁用即报错，不静默吞）；
  `ReadOpaqueMeta` 恒 Empty（空即答案，读侧不抛）。
- **超容量**：策略抛 `ArgumentException`（不是截断）。
- **跨重启容量调整**：合法。块自描述（§3），读侧按盘上 PayloadLength 解读；缩容启动时盘上
  超容 opaque 照常交付（读侧），写侧归零按新容量。

### 2.3 结构内装配（三段式——Core 完整生命周期）

结构基类内部统一三阶段（详见 `src/TC.Tier.Core/docs/lifecycle.md` §3.5）：

```csharp
// 构造（= 配置，零虚调用）：Managed 的 meta 引擎纯 Create（零 IO）；
//   引擎单段容量 = align4K(12B + 水位Struct + MetaOpaqueBytes + 4B) + 1 页（按容量算，不硬编码）
// OnInitializeBegin（= 启动）：主引擎 + meta 引擎并行非阻塞 Initialize
// 恢复核心（= join）：WaitForDependenciesAsync 双 await → MetaPolicy.LoadAsync
```

### 2.4 引擎直连消费方（事务日志/测试——自管引擎全生命周期）

```csharp
using var metaEngine = StorageEngine.CreateAndInitialize(fs, metaOptions);
using var policy = new ManagedMetaPolicy<MyHeader, MyPayload>(layout, metaEngine);
if (!policy.Load())   // false = 空/无/损坏 三态（全新初始化本就从 false 开始）
{
    policy.WriteHeader(layout.CreateDefaultHeader());
    policy.WritePayload(new MyPayload { /* 初始水位 */ });
    policy.Commit();
}
var watermarks = policy.ReadMetaPayload()!.Value;
```

---

## 3. 块格式标准规范（四段自描述）

```
[Header 12B 纯规范][内部水位线 StructSize 固定][外部 opaque 实际用量][Footer CRC32C 4B]
 ← magic/version/flags →  ← 水位 struct（每结构自定义）→ ← PayloadLength-水位 →
        PayloadLength = 水位字节数 + opaque 实际用量（自述锚点）
        footer 位 = HeaderSize + PayloadLength；CRC 覆盖 Header+水位+实际 opaque
```

**铁律**：
1. **头部自描述**——所有位置（水位/opaque 范围/尾）由 `PayloadLength` 实际值推出；
   **容量（`MetaOpaqueBytes`）零参与盘上几何**，只做写侧约束（写入上限/缓冲/引擎段容量）。
2. **跨启动容量随便调**：扩容/缩容重启，水位无条件恢复；opaque 按盘自述交付（缩容超容照读），
   写侧归零（下次 Commit 按新容量覆写，旧块 stale 尾巴无害）。
3. CRC in Footer（`FLAG_CRC_IN_FOOTER`），Header 只有纯规范字段（Magic/Version/Flags/PayloadLength）。
4. Header/Footer 走 `[BinaryLayout]` 源生成 codec，禁止手写 BinaryPrimitives。

---

## 4. opaque 使用规范（搭车语义）

| 规则 | 内容 |
|---|---|
| 语义 | `SetOpaqueMeta` = **登记进策略缓冲**；落盘时机归水位线提交链——同块同 CRC 原子携带 |
| 唯一独立刷盘 | 调用方显式提交（Log `CommitAsync` / 2PC `ConfirmCommitted`）——此时一块 = 当前水位 + opaque |
| 禁止 | 不存在第二条 opaque 提交路径（自拍水位独立成块 = 并发水位回退 + 被内部提交冲掉，已废除） |
| 读 | `ReadOpaqueMeta` 读**最近已提交块**（read-last-committed；未提交前读到上一已提交值/Empty） |
| Disabled | 写抛（明确报错）；读恒 Empty |
| 持续性 | 登记一次持续随**每次**水位提交携带，直到下次 `SetOpaqueMeta` 覆盖 |
| 纯 opaque 提交 | 零数据 + 显式提交 = 数据为空但 meta 块完整（水位原样携带；嵌入语义下水位=数据尾、物理尾含 meta 块，见 §6.3） |

---

## 5. Disabled 时数据结构是否完整？

**完整。** meta 是加速与跨重启水位持久化层，不是功能依赖——Disabled 下全部结构能力可用：

| 方面 | Disabled 下的行为 |
|---|---|
| 读写/2PC/截断 | 全部正常（策略 no-op，`WriteMeta` 零副作用） |
| 恢复 | 回退扫盘：Log 前向走帧找尾；Metadata/Mirror 按 magic/版本号扫版本链；Snapshot 从尾向头找 FooterMagic（O(1) 块级）。**撕裂尾天然断链**（magic 不匹配即停），一致性安全 |
| 悬干裁决 | **不可用**（prepared>committed 只有 meta 知道）——扫到什么接受什么（对 WAL/版本链语义正确：未提交本就该丢，扫盘边界即安全边界） |
| 跨重启 tx seq | 不持久化（恢复后 -1，新会话重新编号）；上层已知提交点可经 `Initialize(hints)` 注入 |
| 代价 | 恢复 O(链长/盘) 而非 O(1)——GB/TB 级结构（Snapshot 尤甚）强烈建议开 Managed/Transport |

---

## 6. 水位线关系

### 6.1 每结构的 meta 载荷（水位 struct，字段即布局）

| 结构 | 持久化水位 |
|---|---|
| Log | BeginAddress / TailAddress / CommittedOffset / LastCommittedSeq / LastPreparedSeq / **PreparedTailAddress**（2PC Abort 回退点=上一提交边界尾） |
| Ring | Begin/FlushedUntil/SafeReadOnly/ReadOnly/Tail + 两 seq + OverflowTail + KeySize 锚点 + **CommittedTailAddress**（D2 Abort 回退点=上一提交边界尾） |
| Metadata | Highest/Lowest VersionAddress + LastCommittedSeq/LastPreparedSeq |
| Mirror | Highest/Lowest VersionAddress + 两 seq（PagedMirror 页链头始终靠扫盘重建——meta 只加速全局水位/seq） |
| Snapshot | Write/PhysicalWrite/Truncated/CommittedWrite + 两 seq |

Log/Ring 的 seq 与提交边界在恢复时**还原到实例**（悬干对 TransactionLog.LoadAndReconcile
可见——prepared > committed 即驱动 Abort 截断到提交边界尾）；旧块（字段追加前）按盘上
payload 实长零扩展解读，新字段缺省 Empty = 无回退窗口。

### 6.2 恢复优先级（谁先谁后）

全部结构统一（用户裁定）：**hints（`Initialize(hints)` 外部主动注入——调用方最强知识，最高优先级）
→ meta（结构自管持久化水位）→ 扫盘兜底**。Log 的 hints 内部再分 TailAddress（精确）→ FileSize（近似，
DeltaLog 临时文件场景）。meta 无论命中与否都会 Load（O(1)）——供恢复后 ReadOpaqueMeta/水位读取。

### 6.3 四语义的水位线影响（重点）

- **Disabled / Managed / 外部隔离**：meta 写入对主流水位线**零影响**——主流引擎的
  Min/Committed/Allocated 尾与 meta 完全无关。
- **自流嵌入（3b）**：meta 块本身是主流里的一个 `IS_META` record/entry——
  - **写入即推进物理尾**（TailAddress 含 meta 块）；
  - **水位（块内记录的 CommittedOffset/Tail）= 数据尾**（拍于 meta 块写入之前），
    所以 `物理尾 > 水位`，差值就是 meta 块自身；
  - Replay/扫描跳过 `IS_META`（对数据消费者透明）；
  - 崩溃恢复：倒扫找到最后 meta 块 → 水位取块内值 → 其后未确认数据按悬干处理。

---

## 7. IMetaTransport——外部传输契约（3a 自定义介质时实现它）

```csharp
public interface IMetaTransport
{
    void WriteBlock(ReadOnlySpan<byte> block);                  // 写完整块（last-write-wins）
    ValueTask WriteBlockAsync(ReadOnlyMemory<byte> block, CancellationToken ct);
    ReadOnlySpan<byte> ReadLastBlock();                         // Empty = 无
    ValueTask<ReadOnlyMemory<byte>> ReadLastBlockAsync(CancellationToken ct);   // 空 Memory = 无
}
```

四条契约：① 读写统一 Span/Memory 视图（不用 byte[]/null）；② **空 = 无数据**（无 null 通道）；
③ 返回视图有效至本传输**下一次调用**（调用方需要留存立即拷贝——策略侧已如此）；
④ 传输不理解块内容、不承担完整性（格式与 CRC 全在策略侧，你只搬字节）。
**外部介质全程不碰主流**——主流水位线零影响（§6.3）。

（镜像持久化是同构第二例横切注入契约：`Contracts/Structures/ITransferPersistence` 三段式流式读写 +
`Runtime/DataMirror/WholeMirrorPersistence` 桥接器。）

**3a 推荐实现——`MetadataMetaTransport`（内置适配器，meta 托管 VersionedMetadata）**：
需要外部独有 meta 存储时，不要自写单槽文件/KV——独自写盘要自理 torn write 原子性、
落盘顺序、崩溃恢复一致性，还得自搭 2PC 提交链路。托管到 VersionedMetadata（版本链稳定存储）：
每次写块 = 版本链追加新版本（写到一半崩 = 旧版本完好，magic/CRC 断链天然容错）；
`WriteBlock` 即持久化点（Write + Persist + flush，对齐 meta fsync 语义）；N=2 轮转自动回收
（空间有界）；读 = 内存工作副本零 IO；需要跨结构原子提交时底层已实现 ITransactionParticipant
（经 `Storage` 属性注册进 TransactionLog）。配置约束：`PayloadSize` ≥ meta 块上界
（12B 头 + 水位 struct + MetaOpaqueBytes + 4B 尾），超限写抛（fail-fast 不截断）。
跨重启调 `PayloadSize` 合法：历史块恢复按其盘上真实大小交付（不截断不补零），
`ReadCore` 按统一布局自述裁剪为变长精确块——旧块照常读出。

```csharp
using var ext = new MetadataMetaTransport(vol.Fs, new VersionedMetadataSettings(
    new StorageEngineOptions("my-log.meta", 1L << 20, enableSegmentation: false)) { PayloadSize = 4096 });
using var log = new EntryLog(vol.Fs, logSettings, metaTransport: ext);   // 3a 外部隔离
```

---

## 8. IMetaPolicy 契约（所有实现必须一致）

| 契约 | 语义 |
|---|---|
| 统一布局 | §3 四段自描述；水位走 Payload，Header 只有纯规范字段 |
| 未 Load 的读 | `ReadHeader`/`ReadMetaPayload` 返回 null，`ReadPayload` 返回 Empty |
| Commit 后可读 | Commit 成功即视为已 Load（同实例立即可读） |
| 重新 Load 全量重置 | 上一轮 header/payload/opaque 不残留（含 opaque 记账） |
| Load false 三态等价 | 空 / 无数据 / 校验失败——不区分原因 |
| opaque 长度记账 | 策略内部字段记账（从 Header 倒推会读到旧值）；`PayloadLength = 水位 + 实际 opaque` |
| Dispose 幂等 | 重复调用不抛 |

---

## 9. 自定义策略（命名委托注入）

子类/调用方定制唯一通道 = 构造注入 **`MetaPolicyFactory<THeader, TPayload>`**（按模式构造，
`Contracts/Meta/MetaPolicyFactory.cs`）——禁止匿名 lambda/虚方法（虚方法不进构造：子类字段未初始化）：

```csharp
using var mirror = new WholeMirror(vol.Fs, settings,
    metaPolicyFactory: kind => kind switch {   // 命名委托：MetaPolicyKind → IMetaPolicy
        MetaPolicyKind.Managed => new MyManagedPolicy(...),
        _ => new DisabledMetaPolicy<MirrorMetaHeader, MirrorMetaPayload>(),
    });
```

---

## 10. 性能与部署特征

| 语义 | 写路径 | 读路径 | 附注 |
|---|---|---|---|
| Disabled | no-op | no-op | 恢复扫盘 |
| Managed | 固定块 4K 对齐覆盖写 + Flush | 单次读 + CRC | 独立文件；与数据流完全解耦 |
| 外部隔离 | 变长精确块经传输 | 传输读最后块 + CRC | 嵌主流时不污染数据流；自定义介质 |
| 自流嵌入 | 变长精确块作 IS_META entry 追加主流 | 倒扫最后 meta 块 | 主流按提交频率增长（§1 代价） |

CRC32C 由 `UnifiedCrc` 硬件加速；块 ≤4K 时 Managed 走单次对齐读写。

---

## 11. 铁律与反模式

1. **头部自描述**——水位/opaque/尾位置全由 `PayloadLength` 实际值推出；**容量零参与盘上几何**
   （变化的值不能当固定值用——与"段大小参与地址计算"同坑）。
2. **opaque 搭车水位线**——只登记，随水位提交原子落盘；唯一独立刷盘 = 显式提交；
   **禁止第二条 opaque 提交路径**。
3. **水位一律走 Payload**——Header 只有纯规范字段；Magic/Version/Flags 由布局保证正确。
4. **传输不解析块**——完整性（CRC）永远在策略侧。
5. ❌ **null 表达"无数据"**——空 Span/Memory 是唯一通道。
6. ❌ **持有 ReadLastBlock 返回的视图跨调用**——要留存立即拷贝。
7. ❌ **opaque 超容量写入**——抛 `ArgumentException`（不是截断）；Disabled 写 opaque——结构层
   `InvalidOperationException`（禁用即报错，不静默吞）。
8. ❌ **把 Load false 当错误**——三态（空/无/损坏）都是正常态，全新初始化本就从 false 开始。
9. ❌ **匿名委托/虚方法定制策略**——命名委托 `MetaPolicyFactory` 是唯一通道。
