# TC.Tier.Products 协调文档

> **官方产品组合层**——把 Core/Core.IO 的介质原语与 Runtime 的结构积木组合成开箱即用的
> 数据产品：**TierKv**（本地持久 KV）× **TierQueue**（消息队列）× **TierWAL**（预写日志）。
> 产品层只做**装配、语义收口与组合域协调**——引擎与结构的全部机制（页池/重放/2PC/meta）
> 是 Runtime 积木，本层零重写、零旁路。
>
> 配合阅读（项目内深读）：
> - [`docs/tierkv.md`](docs/tierkv.md) / [`docs/tierqueue.md`](docs/tierqueue.md) / [`docs/tierwal.md`](docs/tierwal.md) / [`docs/tierblob.md`](docs/tierblob.md) / [`docs/tierseries.md`](docs/tierseries.md) —— 产品使用指南（随包发布）
> - [`docs/perf/tierkv.md`](docs/perf/tierkv.md) / [`docs/perf/tierwal.md`](docs/perf/tierwal.md) —— 性能基线（随包发布）
> - [`../TC.Tier.Runtime/COORDINATION.md`](../TC.Tier.Runtime/COORDINATION.md) —— 结构层积木（Ring/索引/EntryLog/Session——产品的真实源载体）
> - [`../TC.Tier.Core/COORDINATION.md`](../TC.Tier.Core/COORDINATION.md) —— Core 原语（介质/执行/可观测）
> - [`../TC.Tier.Contracts/COORDINATION.md`](../TC.Tier.Contracts/COORDINATION.md) —— 跨层契约（ILifecycle/LogicalAddress/2PC）
> - [`../TC.Tier.Products.Net/COORDINATION.md`](../TC.Tier.Products.Net/COORDINATION.md) —— raft × TierWAL 产品节点（本层 TierWAL 的复制域组合）

---

## 0. 一句话总纲

**真实源在积木（Ring/EntryLog），语义在产品（一致性/恰好一次/持久化档）——消费方只碰
产品面，绕过产品直捅积木 = 丢掉全部组合语义。** 产品间互不复用内部组件（各产品持有
独立装配链），共享的只有底层积木与契约。

## 1. 产品全景

| 产品 | 组合（积木 → 语义） | 消费面入口 | 使用文档 |
|------|--------------------|-----------|---------|
| **TierKv** | Ring（数据唯一真源）× 主索引三族（Hash 缺省/BTree/SkipList）× KvSession（三档读一致性 + 原子批）× KvFunctions（RMW）× 检查点快速恢复 × 日志回收 × Watch × 范围索引 | `[KvStore]` 源生成器封闭形态（推荐）/ `TierKvBuilder` | [docs/tierkv.md](docs/tierkv.md) |
| **TierQueue** | RingOfQueueKey（真相源）+ 组位点域（VersionedMetadata 原子提交）+ 消费组（fencing/可见性/退避）+ 死信（独立 `{name}.dlq` 实例）+ 延迟（BTree 就绪索引）+ 幂等（HashIndex 判重）+ Session 2PC 恰好一次 | `TierQueueBuilder`（`CreateGroupAsync` 返回消费者） | [docs/tierqueue.md](docs/tierqueue.md) |
| **TierWAL** | EntryLog（追加/组提交/重放/截断）+ 自定 opaque 容器（段表 + 协议元数据共用）+ 镜像快照（IncrementalSnapshot + 导出/导入传输面） | `TierWalOptions.Builder(fs)` / `TierWalBuilder` | [docs/tierwal.md](docs/tierwal.md) |
| **TierBlob** | 段引擎（帧 + CRC64）× 对象表（独立 meta 引擎）× 写会话（定长/动态 + 2PC）× 删除墓碑 | `TierBlobBuilder` | [docs/tierblob.md](docs/tierblob.md) |
| **TierTimeSeries** | RingOfTimeKey（数据 Ring）+ BTreeOfTimeKey（时间索引）+ VersionedMetadata 水位——乱序吸收/范围序/点查/Rollup 组合糖/retention 后台轮/恢复对账 | `TierTimeSeriesBuilder` | [docs/tierseries.md](docs/tierseries.md) |

代码布局：`Kv/`、`Queue/`、`Wal/`、`Blob/`、`TimeSeries/` 五目录一 csproj（单包 `TC.Tier.Products`）；
`KvValueFraming` / `DeadLetterEnvelope` / `WalOpaqueLayout` 等布局单点在各产品目录内。

## 2. 组合纪律（产品层红线）

1. **真源唯一，派生可重建**：Ring/EntryLog 是唯一数据真源；索引（TierKv 主索引/范围索引）、
   组位点、延迟/幂等索引全是可重建派生——一切"恢复/切换/重建"路径走真源重放（fail-safe
   全量回退），**禁止旁路写真源之外的第二持久化真相**。
2. **内容零知识面**：TierWAL 对 entry 与 opaque 内容零解析（raft/p2p 是纯消费方）；
   帧/信封格式知识归唯一 codec 单点（Kv 值帧、Queue 死信 envelope、WalRaft 帧在
   Products.Net）——新格式进单点，禁散落解析。
3. **完成语义一处定义**：TierKv 的 KvCommitPolicy 三档、TierQueue 的 Ack=位点持久、
   TierWAL 的 CommitAsync 组提交——各产品的持久化语义只在自己产品内定义，不跨产品
   借用语义（复制域的多数派语义归 Products.Net 组合，见下）。
4. **布局契约生成**：跨节拍滚动的二进制布局（WalOpaqueHeader/WalSnapshotHeader/
   DeadLetterEnvelopeHeaderLayout 等）一律 `[BinaryLayout]` 声明式单点 + CodeGen 生成
   codec——禁手写偏移。
5. **诊断口径不进产品面**：探针/benchmark 走 `benchmarks/TC.Tier.Products.Benchmarks`
   （InternalsVisibleTo 白名单），产品公开面不带诊断后门。

## 3. 三产品怎么选

```
点查/范围读写 KV（会话一致性/RMW/Watch/TTL）        → TierKv
消息队列（消费组/重投/死信/延迟/幂等/恰好一次）      → TierQueue
复制日志/共识存储中线（组提交/截断/快照导入导出）    → TierWAL（经 Products.Net 接 raft 成节点）
```

TierWAL 单机也可直接用（WAL 语义自足）；要多数派复制 = `TC.Tier.Products.Net` 的
`TierRaftNode`（raft 引擎 × TierWAL 适配器），不要自己拿 TierWAL 拼共识。

## 4. 依赖位置

```
Contracts ← Core ← Runtime ← Products（本层，+ CodeGen 分析器：[KvStore]/[BinaryLayout] 生成）
                                ↑
                     Products.Net（raft 组合：TierWalRaftStore 适配 TierWAL）
Core.Net 零产品知识（编译期 E4 依赖锁）——本层永不反向依赖 Core.Net；
协议域 × 产品的汇合只发生在 Products.Net。
```

## 5. 反模式（禁止重蹈）

### ❌ 绕产品面直捅积木
- **症状**：拿 `RingOfLong`/EntryLog 自己拼"KV/队列/WAL"。
- **为什么错**：丢掉会话一致性、原子批、恢复窗口、完成语义、回收联动——产品层的全部
  价值就是这些语义收口；积木只保证单结构正确。
- **正解**：消费产品公开面；积木直用仅限产品内部与 Runtime 包文档覆盖的组合场景。

### ❌ 在产品之间互踩存储空间
- **症状**：同卷同名混用（两个 TierKv 同 `KvName`）；直接读写 TierQueue 的 DLQ 引擎目录。
- **正解**：一产品实例一 `KvName/QueueName/WalName` 子目录；DLQ 是宿主队列自管实例
  （经 `DeadLetterQueue` 消费）。

### ❌ 把 Products.Net 的多数派语义搬进单机产品
- **症状**：把 TierKv 的 `Committed` 档理解成"多数派提交"；或单机场景自己实现raft。
- **正解**：`Committed` = 本地持久化档（复制组合下才升格为多数派语义——由 Products.Net
  组装层定义）；共识组合走 `TierRaftNode`。

### ❌ mem 卷照抄大缺省
- **症状**：`MemorySize` 抄 16GB 级缺省上 mem 卷跑测试。
- **为什么错**：mem 卷把页池跨度物化成真实内存（OOM 判例）。
- **正解**：mem 测试用小页池（TierKv 64MB / TierQueue 64MB 缺省已按 mem 收敛）；磁盘
  大积压按需调大。

### ❌ 恢复窗口手注 hints 当常规路径
- **症状**：`KvRecoveryHints`/`WalRecoveryHints` 常态化手工注入。
- **正解**：缺省让底层自恢复；hints 只用于进程外已知水位的冷启动注入。

### ❌ fire-and-forget 丢弃产品面 Task
- **症状**：`_ = kv.PutFormattedAsync(...)` 丢弃。
- **正解**：await（根 AGENTS.md 丢弃纪律——TaskSink 受控提交）。

## 6. 决策树

```
我要持久 KV / 队列 / 预写日志？
  → 对应产品 + 使用文档（docs/tierkv|tierqueue|tierwal.md）；装配形态见各文档"装配怎么选"。

我要多数派复制的日志/状态机？
  → 不要在本层拼——去 TC.Tier.Products.Net（TierRaftNode 一站成节点）。

我要给产品加新持久化结构（新索引/新信封格式）？
  → 布局进 [BinaryLayout] 单点 + CodeGen；真源语义核对红线 §2.1；文档进 src/*/docs/。

我要改产品公开 API/语义？
  → 先读对应使用文档的"反模式"与语义契约节——语义变更必须同步文档与 perf 口径。
```
