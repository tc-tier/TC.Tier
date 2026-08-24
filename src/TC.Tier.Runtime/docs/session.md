# Session（统一协调协议层）使用指南

> 定位：**组合域的统一协调协议**——读写与检查点三 op 的唯一进出（设计稿
> 2026-08-22 拍板）。每个组合域（一个产品面实例）
> 一个 `SessionManager`；产品面（TierKv/Queue/TS/Blob/Meta）不自带排序/可见性/提交语义——
> 消费 Session 协议 + 结构公开面，无第三条自造协调的路。

## 0. 一句话总纲

**Session = 零自有存储的协调层**：写 = staged 物化委托经单飞提交管线（纯内存序 + 排空批合并，
FIFO 全序）；读 = 会话 scope（聚合 epoch + RYW 覆盖层）或地址直达（无会话零税）；检查点 = 管线内
串行回合。持久化真源是参与者各自的 meta 水位（2PC 六件套本就持久）——Session 不做任何内部结构
的持久化决策。

## 1. 快速上手（四步）

```csharp
// ① 建结构（照常——结构对会话零感知）
using var vol = new TestVolume();
using var ring = ...;  // BlittableRing / EntryLog / 索引…… Initialize + WaitForReady

// ② 建域（fs + 名字 + 参与者全集 = 全部）
var mgr = SessionManager.Create(vol.Fs, "kv",
    HangingResolution.ForwardCommit,          // 悬挂裁决域声明（缺省 forward-commit）
    ("ring", ring), ("index", index));        // 参与者全集——同结构写必经同一域
mgr.Initialize();                             // 悬挂裁决 + 管线启动
mgr.WaitForReady();

// ③ 写（档 B：staged 物化委托 → 提交管线）
using var session = mgr.OpenSession("writer-1");
session.Stage(() => ring.Write(key, value), tag: key);   // 结构零触碰（staged 仅存委托）
session.Stage(() => index.Insert(key, addr, begin), tag: key);
long seq = await session.CommitAsync();        // 入管线 → await 自己的回执（域 seq）

// ④ 读（两档）
using (var scope = session.EnterReadScope())   // 会话读：聚合域内 epoch（零拷贝 span 护栏）
    ring.GetValueSpan(addr);                    // 区内零拷贝读
var direct = ring.GetValueSpan(addr);           // 地址直达：无会话零税（一等公民）
```

检查点（管线串行——时机归协议，内容归组合层）：

```csharp
long watermark = await mgr.EnqueueCheckpoint(seq =>
{
    index.TryDump();           // 内容组合层自定（HashIndex/SortedIndex 主存储 dump）
    meta.WriteAnchor(seq);     // seq = 当前已提交水位（管线传入）
});
```

## 2. 三 op 语义

### 2.1 写（两档）

| 档 | 形态 | 何时用 |
|---|---|---|
| **A 直写** | 直接调结构写 API（`ring.Write` 等） | 无协调需求；**须过规则 W 检查（§5）** |
| **B 协调** | `Stage(物化委托)` ×N → `CommitAsync()` | 需要原子性/排序/跨结构一致 |

档 B 细节：

- **物化委托**在管线线程按 FIFO 序执行——应是纯缓冲写（Append/Insert 类）尽力不抛
  （抛 = 管线 Faulted，见 §4）；业务校验放 `Stage` **之前**（此时抛无副作用）。
- **批合并**：管线排空当前积压为一批——同批回合**共享批 seq**、整批一次 Prepare-all +
  Confirm-all（物化/Confirm 按入队序，FIFO 全序不变）。吞吐实测（2026-08-22 v2 真路径探针
  `SessionPipelineProbe`：真 SessionManager+真 EntryLog 参与者+Managed meta）：**mem 单会话
  5,618 / 8 会话 28,219 回合/s（批合并 5×）**；local 真磁盘 22 → 81 回合/s（3.7×，回程 p50
  42ms→84ms——磁盘数字=盘 fsync 物理水位）。吞吐基线永不回退（回归对照跑探针）。
- **回执**=域 seq（批内相同）。失败回执后**会话 Faulted**（重开 `OpenSession`）；
  `staged` 已清空——重试须重新 Stage。
- **ct 取消** = 排队撤销（出队丢弃、seq 零消耗）；在途回合不可打断（等终态）。
- **`Abort()`**：清 staged + 排队回合丢弃；在途回合等终态。Abort 后会话保持 Active。
- **复制回合**（Raft WAL 域）：`CommitReplicatedAsync(awaitDecision, …)`——物化 →
  Prepare-all（fsync-before-replicate）→ `await awaitDecision(候选 seq)`（多数派共识注入位）
  → true: Confirm-all（**不可回退点**）；false/超时/异常: Abort 已 Prepare 者 → `RollbackException`。

### 2.2 读（两档）

| 档 | 形态 | 何时用 |
|---|---|---|
| **地址直达** | 自缓冲句柄直接读结构（`GetValueSpan` 等） | 永远可用——一等公民，零会话税 |
| **会话读** | `session.EnterReadScope()` | 需要协调的读：RYW 覆盖层 / scope 批量 / 未来路由 |

会话读纪律（**IEpochProtected 保护区契约**）：

- scope 聚合域内全部 epoch 读保护参与者（一次进出，防漏进单结构 scope）；
- **区内只做无自保护的零拷贝读**（Ring `GetValueSpan` 族）；自带 epoch 进出的 API
  （Ring 写路径、Index 逐次 `Find`/`Insert`）区内调用 = 同实例重入，**Debug 绊线立即抛**——
  此类操作在保护区外做，或用各自"scope 内单查"形态（`IndexScope.Find`）；
- **RYW 覆盖层**：组合层在 `session.State` 上自管（staged 命令表/批号映射）——Runtime 只定
  协议不定内容；读序 = 先查覆盖层再下结构。

### 2.3 检查点

`EnqueueCheckpoint(Action<long> plan)`：plan 收**当前已提交水位**（不消耗新 seq），与事务回合
天然串行（管线全序）；plan 抛 = 回执原异常，**管线续跑**（检查点无结构悬干）。

## 3. seq 语义

- 域 seq 是**内存排序序**（每域独立，从裁决后水位 +1 起）——不承担跨重启唯一性/fencing
  （分布式 fencing 归共识层 term，正统）。
- 批合并下**同批共享一个 seq**；跨批严格递增无空洞（被撤销回合零消耗）。
- 参与者 `LastCommittedSeq` 才是持久真源（随 ConfirmCommitted 推进，按参与者自身策略持久化）。

## 4. 故障模型

| 故障 | 行为 |
|---|---|
| 回合**物化**抛 | 失败回合回执原异常、同批其余回执批中止 → **管线 Faulted**（域报废重建：悬干无法安全清除，续跑会被后续批"洗白"）；恢复=进程重启/新域（结构恢复语义清尾）。⚠️ 与设计稿 §6"管线续跑"的差异：续跑仅对 Prepare 失败成立——物化失败必须 Faulted，这是实施期裁定的防洗白强化 |
| 回合 **Prepare** 抛 | 协调器自动 Abort 已 Prepare 者（吞次级异常）→ 批回执异常、会话 Faulted → **管线续跑**。已知边界：未轮到 Prepare 的参与者本轮物化残留尾部——由窗口契约在下一轮 Abort 或恢复时清除（档 B 物化委托应幂等可重放） |
| 复制决策 false/超时/异常 | Abort 已 Prepare 者（D2 截断）→ `RollbackException` 回执 |
| Confirm 在决策后抛 | 管线 Faulted（不可回退点之后水位可能分裂） |
| 崩溃（任意时点） | 无中央 record——恢复 = 参与者自身语义 + 域声明裁决（§6） |
| 排队中 Abort/Dispose/ct | 出队丢弃——结构零触碰、seq 零消耗 |
| 提交线程死亡 | 管线 Faulted：排水在飞请求 |
| Dispose | 有界排水（15s 上限，超时强制排水）——已入队回合全部回执 |

`mgr.IsFaulted` / `mgr.FaultReason` 观测；Faulted 后 OpenSession/入队抛。

## 5. 规则 W（混用防线）

**存在开放事务会话期间（`mgr.OpenTxCount > 0`），该域参与者结构的档 A 直写必须 fail-fast。**
产品面写入口统一调：

```csharp
mgr.EnsureNoOpenTransaction();   // CallerMemberName 自动携带操作名
```

裸调结构公开面 = 专家模式自担。会话协调写（staged→管线）不经此检查（档 B 本身即协调路径）。

## 6. 恢复协议（每域一次，启动序收口）

```
1. 该域结构构造 + Initialize + WaitForReady（悬干以"已恢复尾"形态在各自结构里）
2. SessionManager.Create(fs, 参与者全集) + Initialize()
     ├ Register×N（集合唯一登记点）
     └ 悬挂裁决（域声明 HangingResolution）：
        · ForwardCommit（缺省）：悬干推到各自的 prepared seq——跨参与者一致
          （同批共享批 seq），适合 WAL/队列/时序/帧（宁可前推不丢）
        · DropTail（水位一致档）：悬干截断回已确认水位——域要求强确认时
          （参与者策略配 Prepare 即落盘）
3. OpenSession×N（会话=运行期概念，无持久身份）
```

注意结构 2PC 语义：**Confirm 不落盘**（committed seq 的持久化由下一次 Prepare 的 meta 快照捎带）
——"Confirm 后崩溃"恢复为悬干形态（prepared > committed），forward-commit 前推即得正确水位。

**注入档**：`SessionManager.Create(ITransactionLog txn, participants)`——外部 txn 作协调器
（测试假件 / record 持久化语义域）；恢复裁决 = `txn.LoadAndReconcile()`；**不支持
CommitReplicatedAsync**（seq 真源在 txn 内部无法分段预订）——复制域用默认档。

## 7. 会话契约

- **单线程会话契约**（FASTER 同款）：一个会话同一时刻单线程使用（staged 缓冲与覆盖层无锁）——
  违规立即抛；`CommitAsync` 入队后释放门闩（await 回执不占）。
- 会话状态机：`Active → Faulted → Disposed`；`Dispose` = 隐式 Abort，幂等。
- 一个 Runtime 实例多 SessionManager（多域）：管线互不共享；**同结构写必经同一域**（§2 窗口契约）。

## 8. 禁忌

1. **产品面自带排序/可见性/提交语义**——协调语义只在 Session 一处定义（不各自为政）。
2. **给 Session 加任何自有存储/持久化决策**（record/oracle/独立文件）——纯协调层（用户裁定）。
3. **保护区（EnterReadScope/EnterEpoch）内调自带 epoch 的 API**（Ring 写/Index 逐次 Find）——
   同实例重入，Debug 绊线立即抛。
4. **物化委托里做业务校验/可抛逻辑**——校验放 Stage 前；物化抛 = 域报废。
5. **给地址直达读加会话**——地址即句柄教义，直达读永远零会话税。
6. **多域共享结构写**——同结构写必经同一域（窗口契约）。

## 9. 想深入？指路

- 设计稿（拍板全文）：（内部存档）
- 2PC 六件套与 Abort 窗口契约：本文档同目录 `structures.md` §10
- 契约测试：`tests/TC.Tier.Runtime.Tests/Transactions/`（管线/复制检查点/恢复/读 scope 五文件）
- 吞吐探针（回合/s 入档口径）：`benchmarks/.../Kv/SessionPipelineProbe.cs`
