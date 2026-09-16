# TC.Tier.Products.Net 协调文档

> **raft × Tier 产品组装层**——Core.Net 共识引擎与 Products 存储在产品层的汇合点（spec-12 /
> D6 产品接线收口）：`TierRaftNode` 一个装配器成完整产品节点（TierWAL 持久化 + raft 共识 +
> apply 管道 + 多源快照分发/反熵 + 宿主调度）。
>
> 配合阅读（项目内深读）：
> - [`docs/raft-node.md`](docs/raft-node.md) —— TierRaftNode 使用指南（快速上手/传输形态/配置链/生命周期；示例即测试）
> - [`../TC.Tier.Core.Net/COORDINATION.md`](../TC.Tier.Core.Net/COORDINATION.md) —— raft 引擎语义（复制完成档/ReadIndex/快照安装/IRaftStore 需求面——引擎与存储互不泄露布局）
> - [`../TC.Tier.Products/COORDINATION.md`](../TC.Tier.Products/COORDINATION.md) —— TierWAL 产品（双水位/组提交/opaque 容器/快照——本适配器的被组合方）

---

## 0. 一句话总纲

**布局知识归适配器（`TierWalRaftStore`），装配归 `TierRaftNodeBuilder`，调度归宿主循环
（`TierRaftNode`）——引擎与存储互不泄露布局，节点之外零胶水。** 任何一侧变更布局/装配
路径都从本层单点收口，禁在消费方自拼。

## 1. 组件全景（五件套）

| 组件 | 职责 | 关键契约 |
|------|------|---------|
| `TierRaftNode` | 完整产品节点（`Wal`/`Raft`/`Apply`/`Swarm` 句柄暴露）；宿主循环三件调度：①快照压缩（全角色，日志增长 ≥ 阈值 → `TierWal.SnapshotAsync` 一体快照+截头）②快照发布（Swarm 装配时，N₀ 变更挂源）③反熵（仅 leader 发起、对端轮转） | 换届接线：`LeaderChanged` → SwarmSync 持有表清空/反熵停发恢复；`DisposeAsync` 有界分段收尾（宿主→raft→apply→多源→TierWal→内建传输） |
| `TierRaftNodeBuilder` | 三段式装配收口（Create → 链式 → StartAsync 一次成型）；传输**二选一**：`WithTransport`（注入现成——嵌入式同进程/调用方自组装）∥ `WithClusterTransport`（内建经 ClusterBuilder 组装 TCP）——双供给/零供给 fail-fast | `WithJoin`（learner 引导：本地配置强制 `[self learner]`，追平自动晋级 voter——`Raft.IsVoter` 翻真即就绪）；装配失败不残留半启动传输 |
| `TierRaftNodeOptions` | 产品装配选项（全部缺省即产品形态：TierWal 生产默认/raft 默认策略；多源/反熵/宿主压缩缺省**关**） | `HighResolutionTimer` 缺省 true（Windows timeBeginPeriod(1)——时序敏感路径不被 15.6ms 量子钉住） |
| `TierWalRaftStore` | `IRaftStore` 的 TierWAL 适配器——**帧格式知识唯一宿主**：载荷帧 `[Term 8B LE][Kind 1B][Content]`、opaque meta `[ver][term][votedFor][applied][snapshotIndex]`（均 `[BinaryLayout]` 声明式单点） | 语义映射：prevIndex 断言冲突 = `TruncateSuffixAsync`+`AppendBatchAsync` 一体；fsync 点 = `CommitAsync`+`WaitForPersistedAsync`；`TruncatePrefixTo` = 仅推快照水位（物理截头延迟至 TierWal 压缩调度）；append/truncate/import 经 `_gate` 串行 |
| `TierFsIdentityStore` | `IIdentitySource` 的 TierFs 身份文件适配器（25B 定长：`[Magic "TCID"][Ver][NodeId 16B][CRC32]`） | 首次生成原子保存（temp+rename，半写不可见）；损坏/截断 **fail-fast 抛**——禁静默重生成（重生成=静默换身份）；一个路径 = 一个节点进程 |

兼容壳：`TierRaftNode.StartAsync`（静态）签名不变——内部走 `TierRaftNodeBuilder`；
新代码一律走装配器（传输形态显式二选一）。

## 2. 适配器层语义红线（TierWalRaftStore）

1. **双水位下限钳制**：`AllocatedIndex` = max(WAL 真相, 记账视图)、`PersistedIndex` =
   max(WAL 真相, 快照视图)——快照安装/导入把视图推到 N₀ 后，raft 可见水位**不得低于视图**
   （否则 lane nextIndex 被钳回快照边界之下 → 安装触发与心跳互斥 → follower 选举窗到期 =
   换届风暴；HDD MultiNode 实判例）。
2. **快照边界 term 必须真 term**：prevLogIndex == N₀ 的 AppendEntries 携带 `_snapshotBoundaryTerm`
   （entry N₀ 的 term）——发 0 会被无快照 follower 的 term 比对拒绝（hint 走退→安装→换届风暴）。
3. **TruncatePrefixTo ≠ 物理截头**：只推 opaque 水位，快照区数据保留（spec-03 快照流供给）；
   物理截头归 TierWal 宿主压缩调度（`SnapshotGrowthThresholdEntries`）。
4. **串行化域**：append/truncate/import 跨 await 经可等待门串行（prevIndex 断言到落位非原子）；
   读面走 WAL 租约（线程安全）+ 门内状态守卫——改这段先跑对抗套件。

## 3. 依赖位置

```
Core.Net（raft 引擎/传输/装配） ─┐
                                ├─→ Products.Net（本层：适配 + 节点收口）→ 使用方
Products（TierWAL 存储）       ─┘
```

本层是**唯一的引擎 × 存储汇合点**：Core.Net 零产品知识、Products 零协议知识（各自编译期
锁死）——布局翻译只许发生在这里。`InternalsVisibleTo` 仅 `TC.Tier.Net.AdversarialTests`。

## 4. 反模式（禁止重蹈）

### ❌ 绕装配器手拼节点
- **症状**：自己 `new TierWal(...)` + `new TierWalRaftStore(...)` + `new RaftStateMachine(...)`
  + `new ApplyPipeline(...)` 拼节点。
- **为什么错**：装配顺序（TierWal 恢复 → 适配 → apply → raft → 宿主调度）、换届接线、
  生命周期收尾（有界分段）全是装配器的职责——手拼必漏，且漏的部分只在故障时暴露。
- **正解**：`TierRaftNodeBuilder`；静态 `StartAsync` 薄壳仅为存量兼容保留。

### ❌ 传输双供给/零供给
- **症状**：`WithTransport` 与 `WithClusterTransport` 同时给（或都不给）期待缺省形态。
- **正解**：二选一，fail-fast 教用法（装配形态显式）。

### ❌ 在本层之外再造 IRaftStore 适配器 / 解析 WAL 帧
- **症状**：消费方自写 raft↔WAL 适配，或直接解 `[Term][Kind][Content]` 帧。
- **正解**：帧/meta 布局知识归 `TierWalRaftStore` 单点；换存储 = 在 Core.Net `IRaftStore`
  端口另写适配器（不进本层），WAL 侧变动先看本层语义红线 §2。

### ❌ Swarm 未装配却期待快照追赶
- **症状**：缺省 options 部署集群，follower 落后越过快照边界后卡死无法追平。
- **正解**：生产集群 `WithSwarm(...)`（快照安装传输面 + 发布/反熵的装配前提）。

### ❌ peers 表缺自己 / 身份文件静默重生成
- **症状**：内建 TCP 形态 peers 漏本节点；身份文件 CRC 损坏后删文件重跑"修复"。
- **正解**：peers 表含全体成员；身份断裂 = fail-fast 排查（重生成 = 静默换节点身份，
  votedFor/成员表绑定全毁）。

### ❌ 把本层当通用 raft 库二次封装
- **症状**：在 TierRaftNode 之上再包一层"更易用"的节点类。
- **正解**：装配缺口直接补 `TierRaftNodeBuilder`/`TierRaftNodeOptions`（组合而非复制——
  内建形态经 Core.Net 装配器组装，产品层不重写传输拼装样板）。

## 5. 决策树

```
我要一个 raft 复制节点（持久化日志 + 状态机）？
  → TierRaftNodeBuilder.Create(id, fs, config, machine) + 传输二选一 → StartAsync。

我要节点加入既有集群？
  → WithJoin(引导同伴/端点)——learner 引导，IsVoter 翻真即就绪。

我要快照追赶/反熵对账？
  → WithSwarm +（可选）WithAntiEntropyInterval / WithSnapshotGrowthThresholdEntries。

我要换掉 TierWAL 存储实现？
  → 在 Core.Net IRaftStore 端口另写适配器（布局知识归适配器单点——参考本层 TierWalRaftStore）。

我要节点跨重启稳定身份？
  → TierFsIdentityStore（fs + 路径）→ ClusterBuilder.Create(identitySource)。

我要调持久化/选举时序？
  → WithWal（TierWalOptions 组提交三维度/hints——磁盘 raft 建议 DIO+WriteThrough）
    / WithRaft（选举窗/复制攒批）；Windows 定时器分辨率缺省已提至 1ms。
```

## 6. 产品协议域号分配表

产品协议域 = `IProtocolTransport` 上的注册面（0x60+ 归产品，TierRaftHost 文档口径）——
复制档组路由（`GroupReplicaRouter.GetOrCreate(transport, protocolId)`）按域挂载，一域一表：

| 域号 | 产品 | 状态 |
|------|------|------|
| `0x60` | TierQueue 复制版（`QueueReplicaProtocol.Domain`） | 在用 |
| `0x61` | TierKV 复制档（CasCmd 等） | 预留 |
| `0x62+` | 后续复制档递增 | 空闲 |

**分配纪律**：新复制档取号必须先在本表登记（下一空闲号顺序递增，禁跳号禁复用）；
组内线格式一经发布即冻结（跨版本升级窗归 Server P0 #2 命令族版本化统一定）。
