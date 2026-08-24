# Lease 协议使用指南（六类型 · 三阶段 · 三态迭代）

> **适用范围**：IO 引擎层（`Storage/IO`）开发者、所有调 `SegmentTable.{X}Lease(...)` 的人。
> 回答："我要做追加/覆写/回收/截断/整理——用哪个 lease、怎么遍历 chunk、什么时候 Commit、失败怎么办"。

---

## 0. 一句话总纲与三条铁律

**lease = IO 层与段表之间的唯一交互协议**。每个操作类型是一个独立的类型化 lease，
对段表 / 物理段 / 稳态的要求由类型自身表达。

1. **等待只有一个合法宿主：lease 协议**。物理门（Empty→Ready）的等待点 = chunk 流水线第一拍 +
   类型声明的提交扫尾。段表不等待、worker 不等待、池不等待。
2. **类型即协议**。禁止 kind 字节路由、禁止合并协议、禁止把某类型的要求放进共享路径。
3. **段表是核心**，物理段 = IO 层对段表统一事件的实现（见 [segment-table.md](segment-table.md)）。

## 1. 选型表（需求 → lease 类型）

| 我要… | 用 | 关键语义 |
|---|---|---|
| 追加写新数据（地址由段表分配） | `AppendLease(length)` | 推双尾；物理门有 |
| 只分配地址不写（预留空间） | `AllocateLease(length)` | 无 lease 对象，返回 `(Start, End)`；占位 Committed+sparse |
| 覆写已提交区间（地址已知） | `WriteLease(start, length)` | 不动水位；物理门有（快路径） |
| 回收中间区间（打洞归零抹数据） | `ReclaimLease(from, to)` | **[from,to) 可跨段**——按段切 chunk 逐块 PunchHole；水位不动；无门 |
| 回收头部（删整段） | `ReclaimHeadLease(to)` | 跨段删除 + 尾段打洞；`MinAddress` 推进；无门 |
| 截断尾部（退水位） | `ReclaimTailLease(newTail)` | **仅退双尾**（无打洞）；独占双尾（不并发）；无门 |
| 整理空洞（搬移压缩） | `CompactLease(from, to)` | overlay 原子替换段（一把锁内 invalidate+replace） |

**主推模型**（性能最优，实测快 ~23%）：一次 `AllocateLease` 大空间定地址 + 批量 `WriteLease` 写
（Write 不推 CommittedTail）。

## 2. 通用协议结构（全部类型共享，`LeaseBase`）

三阶段生命周期：

```
① 占住（构造即完成）          ② 锁外 IO（你写数据的地方）        ③ 终态（Commit/Rollback/Dispose）
  AcquireExtentsForLease        遍历 chunk → 每块 IO →             整体 Commit() / Rollback()
  逐 chunk 占区间（段表切分）    chunk.Commit()/Rollback()           最后增量者触发 FinalizeTerminalCore
```

- **chunk 终态仲裁 doneMask 位掩码 exactly-once**：部分/整体 × 提交/回滚四路径共用单一仲裁——
  每个 chunk 恰好一次终态，重复 Commit/Rollback 是 no-op，**不会重复扣账**。

**区间终态的分界线 = 物理真相**（不是"成功/失败"——这是最容易记错的一点）：

| 终态 | 适用场景 | 读门 | 为什么 |
|---|---|---|---|
| `Committed+sparse` | 打洞成功 / Allocate 占位 | ✅ 可读（读零）| 物理上**确实全是零**（PunchHole 已归零 / 新空间从未写过） |
| `Committed` | Append/Write chunk 提交成功 | ✅ 可读（真实数据）| 内容为真 |
| `Wasted` | **Append/Write chunk 回滚** | ❌ 阻断（可被 Write 覆写）| 地址已消费，但内容**物理未知**——Write 覆写旧数据失败时部分 chunk 可能已落盘，宣称读零 = 读出垃圾 |
| `Aborted` | Reclaim 打洞失败 | ❌（只 Compact 修）| 永久洞 |

判定口诀：**物理上真是零 → Committed+sparse；内容未知 → Wasted**。"失败 = 提交 0"只在
物理上真是零的场景成立（打洞成功、占位）；回滚覆写旧数据的场景不成立。

- **Dispose 语义**：未终态 chunk 的回滚扫尾 + 终态收敛——`using` 忘了显式 Commit 不会泄漏区间
  （按回滚处理）。
- **`LeaseState`**：`Active`（占住/在途）→ `Committed`（全员 chunk 提交，最后增量者触发）/
  `RolledBack`（整体回滚）。
- **物理门**（`EnterChunkPhysicalGate`，virtual）：基类无门；**Append/Write override 等 Empty→Ready**；
  Reclaim 系无门。门在迭代器/索引器里自动过，调用者无需手动调。

## 3. chunk 遍历三态模式（API：`ChunkScope`）

每个 lease 的区间被段表切成 ≥1 个 chunk（跨段必多块）。`ChunkScope` 是只读几何 + 分段终态：

| 成员 | 说明 |
|---|---|
| `SegId` / `SegOff` / `SegEnd` / `Length` | 本块几何（段内偏移，`[SegOff, SegEnd)`） |
| `Commit()` | 本块提交（在途 → Committed，IO 成功后调） |
| `Rollback()` | 本块回滚（在途 → Wasted/Aborted，IO 失败时调） |

三种遍历（**都自动过物理门**）：

```csharp
using var lease = table.AppendLease(length);

// ① foreach——完整迭代器（推荐默认）
foreach (var chunk in lease)
{
    await WriteAsync(chunk.SegId, chunk.SegOff, data);   // 锁外 IO（你的部分）
    chunk.Commit();                                       // 本块终态
}

// ② for——按下标（需要随机访问/重试单块时）
for (var i = 0; i < lease.ChunkCount; i++)
{
    var chunk = lease[i];            // 索引器含物理门
    await WriteAsync(chunk.SegId, chunk.SegOff, data);
    chunk.Commit();
}

// ③ while——显式迭代器（保留模式，需要对枚举器本身操作时）
var iter = lease.GetEnumerator();
while (iter.MoveNext())
{
    var chunk = iter.Current;        // MoveNext 已过物理门
    await WriteAsync(chunk.SegId, chunk.SegOff, data);
    iter.CommitCurrent();
}
```

**流水线规则**：chunk 逐块提交可以乱序（doneMask 仲裁），但**整体 `lease.Commit()` 前所有块必须已终态**
——Append 的扫尾会对未提交块补物理门再收尾。整块失败 → `lease.Rollback()`（或直接 Dispose）。

## 4. 各类型速查卡（权威表压缩版）

### AppendLease（追加——最频繁）

| 维度 | 语义 |
|---|---|
| 范围 | 从 `AllocatedTail` 分配 [start,end)；Empty 段合法（注册即 Empty，不阻塞分配） |
| 物理门 | **有**——每个 chunk IO 前等 Empty→Ready（Append 全部 chunk 都需要物理段） |
| chunk 提交 | Leased→Committed，`AdvanceOffset`；到顶自动 Ready→Full + `OnSegmentFull` + 预建下一段 |
| chunk 回滚 | →Wasted（可覆写空洞），**水位照推**（地址已消费不可逆） |
| 整体收敛 | `AppendFinalize(End)`：推 `CommittedTail`（precise-prefix，max-CAS 幂等） |

### WriteLease（覆写）

| 维度 | 语义 |
|---|---|
| 范围 | 覆写 [start,start+length)；**目标区间必须 ≤ CommittedTail 且可占（Committed/Wasted）** |
| 物理门 | **有**（显式声明，不靠"目标段已 Ready"的隐式前提——常态走快路径零开销） |
| chunk 提交 | Leased→Committed（去 sparse，落真实覆写） |
| chunk 回滚 | →Wasted |
| 整体收敛 | **无**（目标区间本就 ≤ CommittedTail，推尾即错误前进） |

### ReclaimLease（中间回收 = 段上打洞）

| 维度 | 语义 |
|---|---|
| 范围 | [from,to) 打洞（**可跨段**——与 Append/Write 同一套按段切 chunk 机制，逐块 PunchHole 物理归零抹除数据）；区间**保持 Committed+sparse**（读零/可覆写） |
| 物理门 | **无**（作用于已提交区间，物理已存在） |
| chunk 提交 | →Committed+sparse（打洞成功终态记录） |
| chunk 回滚 | →Aborted（**永久洞，只 Compact 修**） |
| 三支柱 | 只动**段区间**；段/水位不变 |

### ReclaimHeadLease（头部回收 = 打洞 + 跨段删除 + 推头）

| 维度 | 语义 |
|---|---|
| 范围 | [MinAddress, to)：to 前整段删除 + to 段 [0,to.Offset) 打洞 |
| 物理门 | **无**（删除不依赖物理就绪） |
| Commit/Rollback | **都推 `MinAddress`**（物理删段不可逆） |
| 物理联动 | 删段文件由引擎在 lease 外执行（先 meta Remove+Flush 再删）；lease 不做 IO |

### ReclaimTailLease（尾截断 = 仅退双尾）

| 维度 | 语义 |
|---|---|
| 范围 | 截断 [newTail, AllocatedTail)——**无跨段打洞，段区间状态机不参与**（游标即裁决） |
| 前置 | `MinAddress ≤ newTail < AllocatedTail`；**独占双尾**（与其它 ReclaimTail 不并发；Append 只是退避不阻塞） |
| Commit/Rollback | 都退水位（物理截断不可逆） |
| 物理联动 | IO 层跟随 `SetLength`；段表侧到此为止 |

### CompactLease（整理 = overlay 拷贝 + 段表原子替换）

**使用契约**：**创建（独占占住全部目标区间）→ 完成全部物理 IO/搬移 → 最后一次性 `Commit()`**。
区间码 `CompactLeased` 是独占模式——占住持续整个整理期间，不支持 chunk 级提交/释放（部分释放会破坏
"一把锁内全部段原子替换"的不变量，所以协议根本不提供 chunk 提交）；**物理 IO 未全部完成不得提交**
（完整性绊线拦截 Pending chunk，但正确用法就是"全做完再交"）。代价模型：目标区间在整理全程对写者
关闭（引擎侧 Compacting 排他）——大范围整理用 RangeCompact 分片。

**独立协议**：`sealed class CompactLease : IDisposable`——**不继承 LeaseBase**（无 chunk 流水线 /
doneMask / 物理门，模型是"占住 → 搬移 → 整体替换"两阶段）。调用方是引擎 Compact 子系统
（compactor），业务侧入口是引擎的 `StartCompact()` / `StartRangeCompact()`。

| 维度 | 语义 |
|---|---|
| 范围 | [from,to)——全量 Compact = [MinAddress, **CommittedTail**)（上界按 CommittedTail 校验，不是整段 GrowthLimit——未写满的尾段不含未提交区）；可跨段，逐段成 chunk（`CompactChunk` 携带旧段 GrowthLimit） |
| 区间码 | `CompactLeased`（Src=Compact，排他占住） |
| 段表前置 | 目标区间已提交可占；引擎侧先 `TryEnterCompactingOrFail` 拿 Compact 排他（段稳态 overlay `Compacting`，不接受新写入） |
| **物理门** | **无**——overlay 拷贝的是已提交数据（物理已存在） |
| **阶段①（搬移）** | 使用方（compactor）对**每个 chunk 恰好调一次** `SetReplacement(newMin, newMax, newGrowthLimit, state)`（该段压缩后重建）或 `MarkInvalid()`（该段数据全部搬空，删除）——两者互斥、一次性流转，未填 = `Pending` |
| **阶段②（提交）** | `Commit()`：先过**完整性绊线**（任何 chunk 仍 Pending → 拒绝提交抛异常，防半填 lease 入表）；CAS Active→Committed；逐 chunk 释放占住；聚合 → `CompactCommit` → **`AtomicCompactReplace`**（一把 `_mutationLock` 内：invalidate 段 MarkInvalid + 替换段槽位 `Volatile.Write` 发布，锁外发 `OnSegmentDelete`/`OnSegmentReplace`）——中间状态不可见 |
| chunk 回滚 | 不适用（无 chunk 分阶段提交——**必须整体原子**） |
| 整体终态收敛 | `Rollback()`/`Dispose()`：全部 chunk 释放占住 + `CompactRollback`；Dispose = 未终态自动回滚（同其他类型） |
| 三支柱 | **段**：invalidate（删）+ replace（新 min/max/容量原子换槽）；**水位线**：不动；**段区间**：新段由 lease 内部推导布局（Committed 打包前缀 + sparse 腾空区），发布即生效 |

## 5. Reclaim 家族辨析（三兄弟只差三件事——"跨段"不是差异点，三者区间都可跨段）

| | 段上打洞 | **整段删除** | 水位线 |
|---|---|---|---|
| ReclaimLease（中间） | ✓ 逐块打洞归零 | ✗（段都保留） | 不动 |
| ReclaimHeadLease（头） | ✓ 尾段部分区间 | ✓ to 之前的整段 | `MinAddress` 推进 |
| ReclaimTailLease（尾） | ✗ | 尾后段随尾消失 | **仅退双尾**（回退≠打洞） |

## 6. 铁律与反模式

1. **物理门不手动调**——它在迭代器/索引器里。绕过迭代器直摸 `ExtentsInternal` = 绕过门 + 绕过仲裁。
2. **禁止合并协议 / kind 路由**——任一类型的要求泄漏成其它类型的隐藏前提，是 IO 引擎失稳的根因层。
3. **跨段 lease 逐块处理**，不要假设单 chunk（`growthLimit` 边界必然切块）。
4. ❌ **对 Reclaim 打洞失败置之不理**——Aborted 永久洞只 Compact 能修，洞多了读门会拦（`IsRangeFullyReadable`）。
5. ❌ **ReclaimTail 当打洞用**——运行期退尾不删区间记录；真要删数据走 Reclaim（中间）/启动修正。
6. ❌ **把 lease 对象跨线程共享**——lease 非线程安全；并发写各拿各的 lease（地址唯一性由 CAS 保证）。
7. **Compact 整体提交**：`CompactLease` 没有 chunk 级部分提交语义。

## 7. 最小正确范式（含失败路径）

```csharp
// 追加写（生产范式：try/using 覆盖失败路径）
try
{
    using var lease = _table.AppendLease(data.Length, ct);
    var off = 0L;                                  // lease 内数据游标（chunk.SegOff 是段内相对量，跨段会归零——勿用它算 lease 内偏移）
    foreach (var chunk in lease)
    {
        await WriteChunkAsync(chunk.SegId, chunk.SegOff, data.Slice((int)off, (int)chunk.Length), ct);
        chunk.Commit();                            // 成功一块提一块（乱序合法）
        off += chunk.Length;
    }
    lease.Commit();                                // 整体：推 CommittedTail（内部最后增量者收敛）
}
catch (OperationCanceledException) { throw; }
catch (Exception)
{
    // 未 Commit 的 lease 经 Dispose 自动回滚扫尾（Wasted 空洞，地址不回收）——无需手动 Rollback
    throw;
}

// 主推批量模型：Allocate 定地址 + Write 批量写（快 ~23%）
var (start, end) = _table.AllocateLease(totalLength, ct);          // 纯地址（Committed+sparse 占位）
for (var off = 0L; off < totalLength; off += chunkSize)
{
    using var w = _table.WriteLease(start + off, chunkSize, ct);   // 覆写占位区间
    foreach (var chunk in w) { await WriteChunkAsync(chunk.SegId, chunk.SegOff, ...); chunk.Commit(); }
    w.Commit();   // Write 整体 Commit 无段表副作用（不推尾）——终态收敛在 lease 内部完成
}
```

// Compact（compactor 视角——业务侧经引擎 StartCompact()/StartRangeCompact()，不直接拿 lease）
using var lease = _table.CompactLease(from, to);
foreach (var chunk in lease.Chunks)
{
    var packed = PlanPacking(chunk);                     // 搬移规划：数据前移压缩
    if (packed.NewMaxOffset == 0)
        chunk.MarkInvalid();                             // 该段全部搬空 → 整段删除
    else
        chunk.SetReplacement(packed.NewMin, packed.NewMax,  // 压缩后重建（新容量/水位）
                             packed.NewGrowthLimit, StableState.Ready);
    CopyData(chunk, packed);                             // 物理搬移（overlay，锁外）
}
lease.Commit();   // 完整性绊线：有 chunk 漏填 SetReplacement/MarkInvalid → 此处抛，lease 仍 Active 可回滚
// 失败路径：异常未提交 → Dispose 自动回滚（全部占住释放，段表零改动）

## 8. 性能画像（2026-08-16 三环境实测基线：Windows 笔记本 / Windows 桌面历史参照 / 12 核 Linux）

| 指标 | 数值（2026-08-16 三环境基线） |
|---|---|
| 单次 lease 全协议 | ~1.5µs / 107-112B（纯地址 438ns；批量 WriteLease 最优） |
| 稳定性 | 10 万次 Gen0=1-2、Gen1/2=0；区间表保持 1 条；p50 无退化 |
| 并发 | 2 线程近线性（三环境复现）；零 Monitor 争用；分配不随并发膨胀 |
| IO 占比 | Buffered ~7%、WriteThrough ~0.4%——lease 不是写路径瓶颈 |

**池化裁定**：`LeaseFactory.Default` = 每次 new（**默认不池化**——对象小，池化成本更高）；
`LeaseFactory.Pooled` 仅诊断/对比用。`WithDiagnostics` 仅测试用（+开销）。

## 9. 决策速查

| 场景 | 做法 |
|---|---|
| 高频小写 | `AppendLease` 逐条（便捷）或 Allocate+Write 批量（最快） |
| 大块顺序写 | Allocate 大空间 + WriteLease 批量 |
| 覆写已知地址 | `WriteLease`（先确认区间 ≤ CommittedTail） |
| 释放空间不缩边界 | `ReclaimLease` 打洞 |
| 删最老数据 | `ReclaimHeadLease` |
| truncate 语义 | `ReclaimTailLease` |
| 空洞压缩 | `CompactLease`（整体提交） |
| 写失败 | 块级 `chunk.Rollback()` 或整体 Dispose（自动扫尾） |
