# TC.Tier 开发指南——架构与设计思想

> **给谁看**：贡献者，以及想深入源码、自研组件或组合自己存储模型的使用者。
> **讲什么**：全库的设计思想——价值取向、组合式架构、数据布局与反腐机制、代码组织范式。
> 使用问题（怎么配、怎么调）看[在线文档站](https://docs.mytzz.top/)；构建/测试/PR 流程见 [CONTRIBUTING.md](CONTRIBUTING.md)。

---

## 1. 设计哲学：生产可用优先

TC.Tier 的存储内核（结构层 + 存储引擎 + 文件系统层）是**为生产长期稳定运行而全套设计的工程**，不是为 benchmark 跑分优化的。价值取向上的根本差异：

| 维度 | 跑分优先取向 | **生产可用优先（TC.Tier）** |
|---|---|---|
| 数据正确性 | 冷区可能返回驱逐脏数据 | 完整 CRC32C 校验 + 数据反腐 + torn write 边界定位 |
| 二进制布局 | 位打包（不自描述、无 CRC、布局耦合） | 统一 `[BinaryLayout]` 源生成布局 + 自描述 + 完整校验 |
| 地址模型 | long 位打包（物理布局焊死） | 复合 `LogicalAddress(SegId, Offset, Ext)`——地址与物理解耦，多载体兼容 |
| 持久化恢复 | 复杂 checkpoint 体系，恢复 O(N) 全扫 | 统一恢复优先级（hints → meta 水位 → 扫盘）+ 倒序优化 |
| 超大 value | 不支持 | WiscKey 式溢出分离 + 独立溢出恢复 |
| 冷读 | 冷区不保证正确，无读写分离 | 读写分离（冷读不冲刷热区）+ 部分页回源 |
| 存储载体 | 单一后端 | 本地 / 内存 / 虚拟 / 网络文件系统统一抽象 |
| 崩溃耐久 | 依赖 checkpoint | WAL 语义追加 + 显式耐久档 + 跨实例恢复 |
| 资源可预测 | 内存随数据增长 | 固定内存页池 + 稳态零 GC + 内存占用有界 |

**评估口径**：稳定性优先于峰值——重点考察变异系数、p99/p999 尾延迟、读写分离、长稳衰减，而非单次峰值跑分。性能数字一律以 [`src/*/docs/perf/`](https://docs.mytzz.top/) 的实测基线为准（环境、配置、复现命令齐全），本文不重复数字。

**全套自研 + 每组件独立验证**：所有底层算法与公共组件（对齐内存池、epoch 保护、原子原语、CRC、源生成序列化……）全部自主实现，且每个公共组件在投入上层使用前都有独立的性能基准与单元测试正确性验证。上层结构的性能与正确性建立在**经过独立验证的可靠组件**之上，而非黑盒依赖。

## 2. 组合式分层架构（双支柱）

TC.Tier 由**两套各自完整的支柱**组成，共用同一套 Core 积木基座：**存储支柱**（单机持久化内核）与**网络支柱**（分布式协议层），接缝只有两处——raft 状态机的持久化需求与快照传输通道。

```
存储支柱
组合产品（TierKV / TierWAL / TierQueue；时序等组装中）
    ── 产品 = 主结构 × 搭配件的组合发生器，零新结构
结构层（可组合内核）
    Ring / Log 两大主结构 ＋ 搭配件（索引 Hash·BTree·SkipList / 镜像 / 元数据 / 快照）
    ── 16B 逻辑地址空间；地址即身份
存储引擎（Options → Builder → StartAsync 三段式装配）
    ── WAL 语义追加 / 原地覆写 / 崩溃恢复，结构可内建引擎
文件系统层（local:// · memory: · virtual:// · network:///s3）
    ── 换 URI 即切换载体，代码不变

网络支柱
产品节点（TierRaftNode——raft × TierWAL 完整产品节点）
    ── 共识引擎接持久化存储 + 提交应用管道 + Swarm 快照分发与反熵；节点成集群只差传输
分布式协议层（TC.Tier.Core.Net）
    ── raft 共识引擎 × 节点通信三形态 × 对等机制（HyParView 会员 / Swarm 反熵）
    ── 介质无关：Wire 统一帧编码，Transport 可换（TCP / UDP / QUIC）
```

依赖单向无环：存储支柱 `CodeGen.Abstractions → Contracts → Core → Runtime → Products`；网络支柱 `Core → Core.Net → Products.Net`（复用 Products 的存储产品）；S3 文件系统（`Core.IO.S3`）与镜像网络传输桥（`Core.IO.Net`）为 Core 之上的可选件。源生成器（`TC.Tier.CodeGen`）编译期横切，零运行时反射。

### 2.1 组合模型

- **主结构对搭配件零知识**——Ring/Log 不感知镜像/快照/索引的存在；搭配件是优化手段，不是正确性依赖。摘掉任何搭配件，主结构的数据与恢复语义不变。
- **产品 = 组合发生器**——TierWAL 是 Log 的产品化形态，TierQueue 是 Ring × 消费组 × 位点域的组合，TierKV 是 索引 × Ring 的 KV 闭环。产品层不发明新结构，能力全部来自结构层既有件。
- **恢复统一模型**——载基线（W）+ 重放 (W, 尾]，所有结构与搭配件同一套恢复语义（优先级：hints → meta 持久化水位 → 扫盘）。

### 2.2 地址一等公民

`LogicalAddress` 是贯穿全库的身份标识：key → 地址的发现只发生一次（索引），此后可持久化、可传输、可直达取值（跳过索引再发现）。地址与物理布局解耦（段号 + 段内偏移 + 扩展），因此同一套上层代码可在不同存储载体间无缝迁移——这是"分层可组合"能成立的地址学基础。

### 2.3 索引可换

索引层是独立维度，不是内核焊死件：内存充足用 Hash（O(1) 点查）、内存受限或需范围扫描用 BTree、高写入并发用 SkipList。按场景换结构，上层组合代码不变。

### 2.4 网络支柱：分布式协议层（Core.Net）

Core.Net 是**介质无关的完整分布式协议层**，一个 `NodeEndpoint` 装配成型，三块能力正交组合：

- **节点通信三形态**——数据报（fire-and-forget）、请求回调（RPC）、流式（快照/批量传输），共用 Wire 统一帧编码；传输可换（TCP / UDP / QUIC），协议层零介质知识。
- **raft 共识引擎**——选举、日志复制（多完成档应答）、快照安装、成员变更、learner 角色齐备；引擎与存储互不泄露布局（`RaftStateMachine` 只依赖 WAL 语义持久化面），因此可以接 TierWAL 也可以接任何满足语义的存储。
- **对等机制**——HyParView 部分会员协议（Swarm）维护集群视图，Merkle 反熵只拉差异块修复漂移；周期循环与换届接线在装配面完成。

**产品节点 `TierRaftNode`**（Products.Net）把两支柱接成开箱节点：TierWAL 持久化 + raft 共识 + 提交应用管道（ApplyPipeline）+ 可选 Swarm 快照分发与反熵 + 宿主调度循环（压缩/发布/反熵周期）。`TierRaftNodeBuilder` 装配，传输经 `WithTransport` 注入（显式指定 > 注入实现 > 默认）。

## 3. 数据布局与反腐

所有持久化帧走**统一透明化二进制布局**：`[BinaryLayout]` 标注的 struct 由源生成器在编译期生成读写与校验代码，字段偏移/对齐/嵌套全部编译期确定、布局可审计；header 自描述（magic 自识别 + 版本号 + 长度字段），任意二进制位置可自解析。

配套的数据反腐机制：

- **torn write 边界定位**——恢复扫盘遇 CRC 失败立即停止，返回最后一个校验通过的 record 尾 = 撕裂写的精确边界；损坏数据不会被当作有效数据。
- **防错算法选择**——meta 块的 CRC 算法由配置决定而非从字节流读取，防损坏记录用错算法导致误判。
- **坏帧隔离**——扫描遇坏帧返回已验证有效区间，不抛异常、不污染恢复结果。
- **页槽身份校验**——内存页池回收复用页槽，读页后必须校验页内身份与期望地址一致，防回收页残留误读。

## 4. 与 FasterKV 的关系

Ring/Log/索引/引擎层是**全新独立设计与实现**，与 FasterKV 没有代码关系；FasterKV 仅作为性能对照基准（同机同轮背靠背，NuGet 2.6.5）。

独立重写时从设计上规避的内核复杂度（节选）：

| FasterKV 机制 | 本库处理 |
|---|---|
| hash index 焊死内核 | 抽成独立索引层，可换 hash / B+树 / 跳表 |
| fixed/varlen/object 三套 allocator | 单一 record header + 变长 key/value，单一代码路径 |
| ClientSession + CPR phase 协议 | epoch 保护（LightEpoch），无 session 开销 |
| 三套 checkpoint 体系 | 统一 meta 策略 + 恢复优先级模型 |
| record 级 ReadCache | 页级 LRU（ClockCache），页粒度更省内存 |

继承并对等实现的核心能力（hybrid log 分层、原地更新、页驱逐复用、epoch 保护回收、异步读、可变长 record），并新增 FasterKV 不具备的能力：WiscKey 式超大 value 溢出分离、自描述二进制布局与完整 CRC 反腐、多载体存储抽象。

## 5. 代码组织范式：基类扩展

结构层的基类（Ring/Log 及搭配件基类）如何组织"能力"的代码结构，由**一个判断标准**决定：

| 能力是否访问基类私有成员？ | 代码结构 | 范本 |
|---|---|---|
| **是**（写主载体 / 读写水位 / 调 `private protected` IO 原语） | **partial + 嵌套类**（泛型嵌套抽象基类 + 默认实现 + 子类继承） | `LogBase.Recovery.cs` |
| **否**（自管载体/缓冲、纯字节读写、委托接口） | **独立顶级类**（实现接口，构造注入） | 各 meta policy / codec |

### 5.1 嵌套类范式（访问基类私有能力）

嵌套类天然访问包含类的所有成员（含 `private`），因此**不需要把任何成员提升到 `internal`**——可见性零泄露：

```csharp
// 文件：LogBase.Recovery.cs —— internal/public abstract partial 开头，按能力分文件
public abstract partial class LogBase
{
    // 基类对外入口（工厂 + 委托嵌套策略实例）
    protected override IRecovery<LogRecoveryHints> CreateRecovery() => new DefaultLogRecovery(this);

    // 默认实现（嵌套，primary constructor 持 Owner）
    private sealed class DefaultLogRecovery(LogBase owner) : LogRecovery<LogBase>(owner);

    // 泛型嵌套抽象基类：持有 Owner，完整状态机在基类，子类只 override 钩子
    protected abstract class LogRecovery<TLogBase>(TLogBase owner) : RecoveryBase<LogRecoveryHints>
        where TLogBase : LogBase
    {
        protected async ValueTask WaitForDependenciesAsync(CancellationToken ct)
            => await owner._engine.WaitForReadyAsync(ct).ConfigureAwait(false);  // ★ 直接访问 Owner 私有
    }
}

// 子类专属策略：继承嵌套基类，只 override 钩子（文件 ImplName.Capability.cs，partial of Impl）
```

范式要点：

1. 文件名 `BaseName.Capability.cs`（`LogBase.Recovery.cs` / `LogBase.Cursor.cs` / `LogBase.Transaction.cs`…），按能力分 partial 文件；
2. 泛型嵌套抽象基类 `Capability<TBase> where TBase : BaseClass`，持有 `Owner`；
3. 默认实现嵌套在基类内；子类专属版本嵌套在实现类内、只 override 钩子；
4. 完整状态机/编排放基类，子类只填钩子；
5. **零可见性提升**——IO 原语与水位保持 `private protected`，同程序集非子类/非嵌套的类碰不到，调用方无法绕过 codec 裸写格式。

### 5.2 独立顶级类范式（不访问基类私有能力）

能力自管设备/缓冲、不碰基类私有字段时，独立顶级类实现接口即可，构造注入：

```csharp
// 文件：XxxMetaPolicy.cs —— 顶级 internal sealed class
internal sealed class ManagedMetaPolicy : IMetaPolicy
{
    private readonly IStorageEngine _engine;        // ★ 自管引擎，不访问基类私有
    private readonly AlignedMemoryManager _buffer;  // ★ 自管缓冲
}
```

基类用 `virtual` 方法或工厂委托按 settings 创建实例。

### 5.3 性能铁律

1. **实现类全部 `sealed`**——JIT 对 sealed 类型做去虚化，接口/虚方法调用编译为直接调用直至内联。这是"接口可替换 + 热路径零虚分发"设计的性能前提。
2. **私有/嵌套的热路径短方法标 `[MethodImpl(AggressiveInlining)]`**——判断标准：热路径（IO 循环内 / CRC / header 读写 / 缓冲拷贝）且方法体短小 → 标注；冷路径（构造/Dispose/编排）或方法体大 → 不标（避免调用方代码膨胀）。

### 5.4 决策流程

```
新增一个能力
    ↓
是否需要访问基类私有成员（私有字段 / private protected IO / 水位）？
    ├─ 是 → 嵌套类范式：BaseName.Capability.cs + 泛型嵌套抽象基类 + 默认实现
    └─ 否 → 独立顶级类范式：XxxCapability.cs + 实现接口 + 构造注入
```

**差异源于分析，不是照抄**：同一个能力工厂，在 A 基类是 `abstract`（存在需访问主载体私有的嵌套实现，必须由实现类决定），在 B 基类是 `virtual` 默认实现（全部策略自管，基类按 settings 选即可）。不要因为"另一个基类这么做了"就照抄——先分析本基类是否存在该模式。

## 6. 想深入

| 主题 | 去处 |
|---|---|
| 使用文档（怎么用） | [docs.mytzz.top](https://docs.mytzz.top/) |
| 各层工程规范总概 | `src/*/COORDINATION.md` |
| 结构层（组合模型/结构清单） | `src/TC.Tier.Runtime/docs/structures.md` |
| 网络与共识层（通信三形态/raft/Swarm） | `src/TC.Tier.Core.Net/docs/net.md` |
| raft 产品节点（TierRaftNode） | `src/TC.Tier.Products.Net/docs/raft-node.md` |
| 存储引擎使用 | `src/TC.Tier.Runtime/docs/storage-engine.md` |
| 性能基线与复现 | `src/*/docs/perf/` |
| 构建/测试/提交流程 | [CONTRIBUTING.md](CONTRIBUTING.md) |
| 文档体系与写作规范 | `docs/documentation-rules.md`（内部） |
