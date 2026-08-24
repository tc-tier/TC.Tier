# 段表（SegmentTable）使用指南

> **适用范围**：IO 引擎层（`Storage/`）开发者、lease 消费侧、恢复/Checkpoint 接入者。
> 回答："段表持有什么、我能碰什么、事件怎么接、回调怎么发、什么绝对不要做"。

---

## 0. 一句话总纲

**段表 = 地址空间的唯一真相之源**：它持有三支柱（水位线 / 段 / 段区间），只发事件、只收回调，
**不等待、不做 IO、不关心物理文件**。物理段是 IO 层对段表统一事件的实现。

```
                    统一事件（ISegmentHandler，段表 → IO 层）
段表（真相）  ────────────────────────────────────────────→  IO 层（物理资产）
  三支柱        ←────────────────────────────────────────────  worker 建段 / 预备池 /
                 建段回调（CreateSegmentCallback，IO 层 → 段表）  meta / 删文件
```

## 1. 三支柱（调用者必须建立的心智模型）

| 支柱 | 内容 | 关键规则 |
|---|---|---|
| **水位线** | `MinAddress`（头）/ `CommittedTail`（终态前缀）/ `AllocatedTail`（分配） | 架在区间表上的**粗游标**；外部**只读**；不变量链 `MinAddress ≤ CommittedTail ≤ AllocatedTail` |
| **段** | 每段 `MinOffset`/`MaxOffset` 两条段内水位 + `StableState` + `GrowthLimit` | **`RealSize = MaxOffset − MinOffset` 是数据大小，`GrowthLimit` 是容量**——两者不是一回事；`IsFull ⟺ MaxOffset ≥ GrowthLimit`（派生属性） |
| **段区间** | 每段一份区间表（`ExtentStateCode`：在途带 Src → 终态） | **lease 协议的核心状态机**；可占性 `IsOccupiable = Committed || Wasted` |

**读门**：区间的可读性看**区间表**（`IsRangeFullyReadable`），不是看水位游标——游标是粗裁剪，
区间表才是逐字节真相（游标 vs 区间双轨，勿混用）。

## 2. 段稳态（StableState）——准入语义

| 稳态 | 含义 | 进入 | 离开 |
|---|---|---|---|
| `Empty` | 逻辑已注册、物理未建——**物理门关**（不接受 chunk IO） | 注册即 Empty（`AppendSegmentRaw`） | 唯一出口：建段回调 |
| `Ready` | 物理就绪，chunk IO 合法 | 回调成功 `TryMarkReady` | 到顶自动→Full；→Compacting；→Invalid |
| `Full` | `MaxOffset ≥ GrowthLimit`：不可扩容、可覆写 | `AdvanceOffset` 到顶（**仅 Ready→Full**） | →Compacting；→Invalid |
| `Compacting` | 整理排他 overlay | Compact 进入 | Compact 完成 |
| `Broken` | 建段失败终态——物理门**永久关，不重试**；**不可分配（A7）**：分配区间不得含其任何字节，尾在 Broken 段内/请求跨段时**烧洞跳过**（水位 CAS 前推过洞，地址消费不交付） | 回调失败 `TryMarkBroken` | 终态（地址空间墓碑——占位不塌陷，同 Compact 后 Invalid 槽） |
| `Invalid` | 已删除：准入吊销、文件不存在 | ReclaimHead/Compact/启动修正 | 终态（幂等） |

三条设计决策（勿当缺陷）：
- **运行期 Full 只经 Ready→Full**。Empty 期被 Allocate 占位推满的段不转 Full 态——满语义由派生
  `IsFull` 承担，段满事件照发；Empty→Full 直达只存在于启动恢复装配。
- **回调双分支幂等**：`CreateSegmentCallback(segId, success)` 成功/失败均 CAS（`TryMarkReady` /
  `TryMarkBroken`），**非 Empty 一律 no-op**——重复/迟到回调不打断已迁移段。
- **Compact 后的段槽复用是设计路径**：整理压缩空洞 → 中间段 Invalid 出服务 → 退尾再分配时
  `EnsureSegmentsForLength` 重注册同 segId——这是地址空间回收复用机制。

## 3. 关键 API（调用者视角）

| 类别 | 成员 | 说明 |
|---|---|---|
| **只读查询** | `AllocatedTail` / `CommittedTail` / `MinAddress` / `SegCount` / `MaxSegId` / `GetSegment(segId)` / `TryGetSegment` / `IsRangeFullyReadable` / `GetExtentRanges` | 全部无锁安全；`GetSegment` 返回只读 `SegmentView`（不存在返回 `Hollow`） |
| **地址算术** | `AdvanceAddress` / `RetreatAddress` / `GetDistance`（+ `SegmentGrowthLimit`） | 纯计算，不改状态——语义见 §3.1 |
| **lease 入口** | `AppendLease` / `AllocateLease` / `WriteLease` / `ReclaimLease` / `ReclaimHeadLease` / `ReclaimTailLease` / `CompactLease` | **外部改状态的唯一合法通道**，见 [lease-protocol.md](lease-protocol.md) |
| **协调（IO 层专用）** | `WaitSegmentReady` / `CreateSegmentCallback` / `PulseAllSegmentsReady` | 建段协调；`WaitSegmentReady` 的**唯一调用方是 lease 协议**（物理门），IO 层不直接调 |
| **持久化** | `SaveAddressTable` / `LoadAddressTable`（一对）+ `SetStartupTails`（启动水位） | 恢复/落盘专用，见 §3.2 |
| **锁开放面** | `ExecuteUnderLock` / `TryGetLock` | 三级锁中必须开放的两级，见 §3.3 |

### 3.1 地址算术（三个接口，纯计算无状态）

| 接口 | 语义 | 边界规则 |
|---|---|---|
| `AdvanceAddress(start, length)` | 前进 length 字节，跨段**进位** | 只前进（length<0 抛，回退调 Retreat）；**返回的 Extension 恒为 0——调用方须显式保留 start.Extension**；恰好填满一段时**停驻段末边界 `(segId, GrowthLimit)`**（区间统一，见 §3.1.1） |
| `RetreatAddress(start, length)` | 回退 length 字节，跨段**借位** | 只回退；回退到段表头部之前（低于 `MinAddress`）返回 `LogicalAddress.Invalid`；落点恒为真实字节位置 |
| `GetDistance(from, to)` | from→to 字节距离，跨段累加 | from > to 返回**负值**；不做 AllocatedTail 上界校验（调用方保证合法）；对段末规范形与旧哨兵形输入同点同值 |
| `SegmentGrowthLimit(segId)` | 取段生长上限 | 段存在用段的；**Hollow 段（已回收/未建）用生命周期上限**——地址空间连续，被回收段在逻辑地址上仍占位，算术跨过它不塌陷 |

三个算术共享同一条不变量：**跨段进位/借位按每段各自的 GrowthLimit 计算**（Compact 后段大小可能不同，
不用全局值）。边界用例（恰好填满停驻 / 旧哨兵形输入借位 / Hollow 借位 / 跨段负距离）有专项测试钉死。

### 3.1.1 区间表示规范（半开区间 [start, end)——统一规范）

**区间定义**：地址空间的一切区间都是**半开 `[start, end)`**——覆盖的字节是 `start ≤ b < end`；
`end` 是"已占用字节之后第一个位置"（边界位置），`start` 是第一个字节。

**边界规范形（唯一写法）**：恰好填满一段时，边界停驻**段末 `(seg, GrowthLimit)`**——

```
(0,0) + 1000  →  (0,1000)     // 段长 1000，恰好填满：停在段末，不给你下一段地址
(0,800) + 200 →  (0,1000)     // 同上
(0,0) + 2000  →  (1,1000)     // 跨两段且第二段恰满：停在 seg1 段末
```

- `(seg, GrowthLimit)` 是**边界位置**（不是字节）：`offset == GrowthLimit` 时最后一个字节即该段末字节。
- 旧哨兵形态 `(seg+1, 0)`（把段末写成"下一段头"）**已废除**——只作为存量盘/外部输入的兼容形态被
  接受（恢复钳制归一、消费方表示无关处理），新算术**永不产出**。
- `(N, 0)` 保留唯一身份：**段首字节 / 地址空间原点**——不再兼任"段末边界"的写法。两态各归其位：
  段末 = `(seg, limit)`，段首 = `(N, 0)`。

**镜像一致性**：`Retreat(Advance(x, n), n) == x` 在段满边界精确成立（统一后无需"归一头"特判）；
Advance 可产出边界位置（end 语义），Retreat 落点恒为真实字节（start 语义）——这正是半开区间的体现。

**边界起步的合法位置**：写入起点可以是段末边界 `(seg, limit)`（= 数据恰满上一段后继续写）——
**首字节在下一段**。消费方须按"边界跳过"处理（分块迭代/压缩切分/迁移映射均已按此规；
`end.Offset == 0` 的段回推消费族对两形态输入表示无关）。

**为什么拒绝闭区间写法**（`[]` 前后闭合 / `(]` 前开后闭——已讨论定稿）：
1. **0/空区间**：半开下"空"天然是 `start == end`（含原点 (0,0) 处，零特判）；闭合写法下"空"无合法
   表示（end 需 (seg,-1) 或跨段借位），段首处尤其如此——闭合写法**制造** 0 特判而非消除它。
2. **热路径零算术**：Append 的写起点就是 end 本身；闭合写法每次要 `end+1`（段边界处恰是进位算术），
   等于把边界进位焊回每条写路径。
3. **比较格全翻转**：水位比较（`newTail < AllocatedTail`、`end <= cur` 才推进、`SetLength(offset)`
   保留 `[0, offset)`）全库按半开格建设，闭合写法全部 off-by-one 重审。

**存量兼容**：旧二进制持久化的 `(seg+1, 0)`（compact marker 的 `to`）由恢复期钳制归一（`StorageEngine.Recovery.cs`）；
盘上格式零迁移，混代读写安全。

### 3.2 持久化（一对读写 + 一个水位修正）

| 接口 | 语义 | 时机 |
|---|---|---|
| `SaveAddressTable(IAddressTableWriter)` | 落盘段表：逐段 `SegmentSpec`（min/max/growthLimit/稳态）+ footer（**双尾水位是权威**——footer 记 Allocated/Committed） | Checkpoint / 关闭 |
| `LoadAddressTable(IAddressTableReader)` | 装配：三段式（头部 → 段载荷循环 → 尾部直读 `LoadTail`——footer 是水位权威，不重算） | 恢复期，一次性 |
| `SetStartupTails(StartupParameters)` | **启动期双尾水位设定**（可大可小：小=截断回收——整段 MarkInvalid+摘索引；大=覆盖旧数据推水位）。单值构造=双尾同址（截断/重置）；双值=扫盘恢复形态（committed < allocated）。**生命周期参数不在此——构造期经 `SegmentTableSettings` 传入**（构造=配置，启动=双尾） | **仅启动阶段**（首次 Allocate 之前，之后调用直接抛） |

恢复顺序铁律：`LoadAddressTable` → `SetStartupTails`（若扫盘结果与持久化水位不一致）→ 首次 Allocate 锁定运行期。
**无持久化启动**：构造 → `SetStartupTails` 定双尾 → 直接 Allocate 运行——`LoadAddressTable` 全程可选。
（IO 引擎侧的 `EngineRecoveryHints` 是引擎恢复输入，引擎恢复流程负责翻译为 `SetStartupTails`；两层类型不共用。）

### 3.3 锁模型（三级——开放两级，区间锁不开放）

| 级 | 锁 | 保护对象 | 开放面 | 谁用 |
|---|---|---|---|---|
| 表级 | `_mutationLock`（Monitor） | 段数组/索引**结构变更**（建段、摘索引、Compact 原子替换） | ✅ `ExecuteUnderLock(Action)` | 外部批量自洽结构操作（整批变更对外不可见） |
| 段级 | `SegmentLock`（SpinRWLock 写偏向：CAS 排他 + 共享读；2026-08-20 自 LockWord 换型——Monitor 等待职责已删） | **段级读写互斥 / Compact/截断排他**（真互斥语义；写偏向保证排他在持续读者流下有界落地） | ✅ `TryGetLock(segId, out SpinRWLock?)`（裸锁：Acquire/Release Exclusive/Shared——读计划锁与 SequentialReader 即此用法） | 读者与写者互斥、Compact 与一切互斥 |
| 段内 | 区间锁（struct SpinLock，微秒级临界区） | 段内**区间表**（Insert/CompleteAndMerge/MarkWasted 等区间手术） | ❌ **不开放**——lease 协议封装（占住/提交/回滚内部自动持有） | 只有 lease 协议 |

**为什么区间锁不开放**：区间表的每次变更都是 lease 三阶段协议的一部分（占住→锁外 IO→提交/回滚）——
开放区间锁 = 外部绕过协议直改区间表 = 状态机与事件契约脱钩（正是要杜绝的后门）。需要区间排他？
拿一个 lease，协议替你持锁。

**锁序（必须遵守）**：`_mutationLock` > 段 SpinRWLock > 区间 SpinLock——只能从上往下拿，不可逆序
（逆序 = 死锁）；区间占用公平性由 `_extentGate`（Core.FairGate，2026-08-20 下沉）管。SpinRWLock **排他**临界区内**绝对禁止 await**
（线程关联原语，跨 await 释放即泄漏——与 Core SpinRWLock 同源规则；共享锁可跨 await 长持，读计划锁即此用法）。

**与物理门的分工**：锁管互斥（谁在改），单向状态闩（§2 Empty→Ready/Broken/Invalid 的
`_physicalReady`）管协调（等谁就绪）——纯状态协调不用锁，这是固化规范 §6.1 的决策。

## 4. 事件契约（段表 → IO 层，`ISegmentHandler`）

| 事件 | 时机 | IO 层职责 |
|---|---|---|
| `OnSegmentCreate(segId, growthLimit, isHighPriority)` | 注册时 / 段满预建下一段 | 正式建段或池预建（build-gate single-flight：**同一 segId 恰好一次物理构建**）；`isHighPriority=true` 用 Critical 优先级 |
| `OnSegmentFull(segId, finalSize, growthLimit)` | 段满（含占位推满——派生 `IsFull` 判定） | 写 Full-meta + 补池 |
| `OnSegmentDelete` / `OnSegmentReplace` / `OnSegmentReclaim` | 删段/Compact 替换/回收通知 | 物理联动（引擎子系统自管） |
| `SubmitBackgroundWork(work)` | 段表自洽低频任务 | worker 顺序执行 |

**回调契约（IO 层 → 段表）**：物理构建完成（无论正式任务还是池预建）后调
`CreateSegmentCallback(segId, success)`。建段任务唯一职责 = 为「已注册且 Empty」的段完成物理构建并回调；
**非 Empty / 已摘索引 → 作废绝不重建**（Invalid 段重建 = 已删文件复活）。

## 5. 生命周期与阶段门禁

- 构造（`SegmentTableSettings`：`GrowthLimit`/`MinSegId`/`IndexCapacity`/`SpinMilliseconds`/`WarnEvery`/
  `EnableSingleSegment`）→ 启动阶段（`LoadAddressTable` 装配（可选）+ `SetStartupTails` 定双尾，**仅 Allocate 之前可调**
  ——三接口语义见 §3.2）→ 运行期（首次 Allocate 成功即 CAS 锁定，不可逆）。
- handler 传入与否决定出生态：**带 handler 出生 Empty**（等物理回调）；`null`（纯内存/测试）出生 Ready。
- 等待参数统一走 `SegmentTableSettings`（`SpinMilliseconds` 预算 / `WarnEvery` 告警间隔）——段表内无策略常量。

## 6. 铁律（违反 = 数据丢失 / 死锁 / 复活已删段）

1. **外部永远拿 `SegmentView`，不碰 `Segment` 引用**；改状态唯一入口 = lease 协议（`ILeaseSource`/
  `IExtentLeaseSource` 显式接口实现，外部类型引用上不可见）。
2. **段表不等待、不做 IO**。任何"等建段完成再…"的诉求都是 lease 协议的事（物理门），
   绝不往段表/worker 里加等待——历史死锁家族全部源于等待放错层。
3. **建段 single-flight**：同一 segId 的物理构建恰好一次（build-gate + 过期守卫与声明同临界区）。
   取用、守卫、声明三者原子。
4. **索引/数组扩容 build-then-publish**（先填 -1 后 `Volatile.Write` 单点发布），**禁用 `Array.Resize`**——
   其零填充窗口会被无锁读者读到幽灵索引 → 段表永久空洞 → 重开截断数据不可达。
5. **发布-读取 acquire/release 全对称**（ARM 弱序合规）：写侧 `_segIndex`/`_segments`/`_segCount`/槽位
   全 `Volatile.Write` 发布；读侧 `GetSegment`/`SegToIndex`/`TryGetSegmentRaw` 对索引**字段与槽位**均
   `Volatile.Read`（acquire 链：索引字段→槽位→`_segments` 字段→段引用，任一环 plain load 在 ARM 上
   可见中间态；x64 TSO 恰安全但不得依赖）。
6. **`WaitSegmentReady` 唯一调用方 = lease 协议**（chunk 第一拍 / 提交扫尾）；有界放弃
   （预算/告警走 Settings）。

## 7. 反模式（实案，禁止重蹈）

- ❌ **Invalid 段重建**：持过期复查的任务去 claim 已回收段 = 复活已删文件 + 容量重计。
  正解：守卫与声明同临界区，四态（Consumed/Claimed/InFlight/Abandoned）一锤定音。
- ❌ **锁内的"正确"证明不了无锁读者的中间态**：五层静态核验可以全"自洽"而缺陷在跨线程发布顺序里。
  共享数组变更先想读者看到什么。
- ❌ **把 `StableState.Full` 当满判据**：满 = 派生 `IsFull`（`MaxOffset ≥ GrowthLimit`）；
  `StableState` 是准入/物理语义，不是容量语义。
- ❌ **运行期调 `SetStartupTails`** / 在 `Allocate` 后裸写水位——破坏 CAS 不变量，直接抛。
- ❌ **给段表加物理概念**（文件路径、句柄、池深度……）——物理是 IO 层的事；段表 Settings 只收逻辑参数。

## 8. 最小正确范式（IO 引擎接线）

```csharp
// ① 引擎构造段表：带 handler（出生 Empty），等待参数走 Settings
_table = new SegmentTable(
    new SegmentTableSettings(growthLimit: SegmentGrowthLimit, SpinMilliseconds: 30_000),
    SegmentHandler,          // ISegmentHandler 实现（事件 → worker/池）
    Logger);

// ② handler 收到注册事件 → 入队正式建段（或池命中同步转正）
public void OnSegmentCreate(int segId, long growthLimit, bool isHighPriority)
{
    if (TryConsumePooledSegment(segId))          // 池命中：物理现成
        _table.CreateSegmentCallback(segId, success: true);   // 同步转正（幂等）
    else
        EnqueueCreateTask(segId, growthLimit, isHighPriority); // worker 正式建
}

// ③ worker 建段任务体：只对「已注册且 Empty」构建，成败都回调，绝不等待/重试
private async ValueTask EnsureSegmentPhysicalAsync(int segId, long growthLimit, CancellationToken ct)
{
    switch (TryConsumeOrClaimPhysicalBuild(segId, out var gate))   // 取用/守卫/声明同临界区
    {
        case PhysicalBuildClaim.Consumed:
            _table.CreateSegmentCallback(segId, success: true);  return;
        case PhysicalBuildClaim.Claimed:
            try { await CreateSegmentCoreAsync(segId, growthLimit, ct); 
                  _table.CreateSegmentCallback(segId, success: true); }
            catch (Exception ex) { Logger.LogError(ex, "建段失败 {SegId}", segId);
                                  _table.CreateSegmentCallback(segId, success: false); } // 幂等，重复不抛
            finally { CompletePhysicalBuild(segId, gate, pooled: false); }
            return;
        case PhysicalBuildClaim.InFlight:   // 池在途——其完成者代执行同一回调
        case PhysicalBuildClaim.Abandoned:  // 非 Empty/已摘索引——绝不重建
        default: return;
    }
}
```

**纯内存/测试**：`handler: null` → 段出生即 Ready，无物理协调（`SegmentTableLeaseProtocolTests` 全套示例）。

## 9. 决策速查

| 我要… | 用 |
|---|---|
| 查某段是否存在/多大 | `GetSegment`/`TryGetSegment` → `SegmentView`（只读） |
| 判断区间可读 | `IsRangeFullyReadable`（读门 = 区间表，不是游标） |
| 地址前进/回退/距离 | `AdvanceAddress` / `RetreatAddress` / `GetDistance`（§3.1——注意返回 Extension 恒 0） |
| 跨段区间切分 | `GetExtentRanges` |
| 改任何状态（写/收/删） | lease 入口 → [lease-protocol.md](lease-protocol.md) |
| 段表落盘/装配/启动水位 | `SaveAddressTable` / `LoadAddressTable` / `SetStartupTails`（§3.2，仅启动阶段） |
| 批量结构变更对外不可见 | `ExecuteUnderLock`（表级，§3.3） |
| 段级读写/Compact 互斥 | `TryGetLock`（段级裸锁，§3.3；ExecuteUnderExclusiveLocks 已删——零调用死代码） |
| 区间排他 | **不直接拿锁——走 lease 协议**（§3.3 锁模型） |
| 诊断 | `GetActiveLeases`/`SnapshotSegmentExtents`（只读；`ForceRelease` 仅受控回滚） |
