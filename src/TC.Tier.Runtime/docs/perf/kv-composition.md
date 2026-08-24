# KV 组合性能基线（RingOfLong × 两族索引）

> Ring 泛型改版的直接目的。
> 数字实测于本机（i5-12400 / .NET 8.0.30 / x64 RyuJit AVX2 / BDN 0.13.12），介质=mem（组合层开销口径，引擎 IO 噪声为零；磁盘介质另行对照）。

## 配置

- Ring：`RingOfLong`（PageSize=8K / MemorySize=32MB / 全热区驱逐零干扰 / 无溢出 / meta Disabled）
- 值 64B；点查预填 100k 条乱序命中；写=每迭代全新组合、每 invocation 批量 256 条摊平计时粒度（`OperationsPerInvoke`——单条/invocation 会被计时粒度虚增成几十 µs 假象）；恢复=50k 条跨实例全量重建（W=Begin 拉流重放，无镜像）
- 运行：
  ```bash
  dotnet run -c Release --project benchmarks/TC.Tier.Runtime.Benchmarks -- --filter "*KvComposition*"
  dotnet run -c Release --project benchmarks/TC.Tier.Runtime.Benchmarks -- --filter "*KvRecovery*"
  ```

## 数字

### 点查（index.Find + Ring.GetValue 两段合口径，100k 条热区乱序命中）

| 索引族 | Mean | 备注 |
|---|---:|---|
| ProbingIndex (Hash) | **161 ns** | 零分配（2026-08-22 容量自适应附带红利：表 134MB→L3 量级，桶探测局部性提升——旧 191-222ns） |
| SortedIndex (BTree) | **~350 ns** | 零分配（2026-08-22 三段累计 5.3×：缓存扁平化+引擎读快路径+生长模式） |
| SortedIndex (SkipList) | **~816-850 ns** | 零分配（2026-08-22 arena 化：2.22µs→~816ns，工作集 28.8→6.5MB 全 L3+零拷贝跳链） |

### 写（Ring.Write + index.Insert 两步编排，全新组合冷启动口径）

| 索引族 | Mean/op | 备注 |
|---|---:|---|
| Hash | 2.3-2.8 µs（**批 2.05 µs，-28%**） | 稳态热写更低（分解见下表）；写口径含每迭代全新组合装配 |
| BTree | **3.3-4.3 µs（批 2.63 µs，-39%）** | **2026-08-24 脏节点延迟写回（3.6×）**：内容变更记脏标记（HashSet 地址集）、dump 时批量写引擎——插入/分裂路径零引擎写（即时写回版 14.6-18.9µs 已销案）；点查 378ns 回基线；分配列方法论警示见下 |
| SkipList | **2.3-2.6 µs** | 363B/插（arena 零托管分配税）；**2026-08-24 脏节点延迟写回**：链/值变更记脏标记、dump 时批量写回——插入路径零引擎写（即时写回版 9.4µs 已销案，见"已修复的性能坑"） |

> **★ 批量写（2026-08-24 落地，`BeginWriteBatch`）**：组合层大批量 key 写入的常用形态——批持锁+epoch
> 独占写窗口，批内 record 零锁零页检查（窗口耗尽跨页自处理）。组合口径 Hash -28% / BTree -39%
> （SkipList 噪声内）；地址语义与单条 Write 完全一致；单写者批语义（批期间其他写者阻塞）。
> 使用：`using var batch = ring.BeginWriteBatch(); ... batch.Append(k, v);`

> **★ 多写者无锁窗口（2026-08-24 落地）**：`BeginWriteBatch` 批持锁改<b>窗口领取</b>——tail 预留整页
> 段（批独占），批内零锁零 tail 推进。并发写吞吐（mem 介质探针 `--concurrent-write-probe`）：
> 8 写者 batch **2.05M → 6.07M op/s（3.0×）**、16 写者 5.90M（扩展饱和——内存带宽/CRC 并行度）、
> 单写者 1.64M 无回归；single-write（逐条）仍 lock 串行（1.7-2.0M）——多写者收益走批窗口。
> 索引侧：Hash 桶 CAS / SkipList 塔链 CAS 已就位——组合层多写者立得；BTree 单写者=真短板（待结构层
> 多写者协议）。

> 分配列方法论警示（BTree 写）：BDN 分配数跨线程归属（引擎后台 worker 分配随机记入测量窗），跨轮 623B-60KB 不可信——同线程探针口径 **621B/插**（ReplayAllocProbe）。

### 恢复（跨实例重开 Ring + 50k 条全量重放，整段墙钟）

| 索引族 | 全量重放 | 主存储帧/镜像载入（W=尾，零增量）* | 探针分配（诚实口径） |
|---|---:|---:|---:|
| Hash | 67-87 ms | 64.7 ms（镜像口径，主存储待重测） | 全量 **2.7 MB（54B/条）**（容量自适应销案） |
| BTree | 106-130 ms | **58.7 ms（2.2×）** | — |
| SkipList | 93-132 ms | **49.0 ms（2.1×）** | — |

*★ 持久化两形态（2026-08-24：SortedIndex 镜像退役，主存储同构落地——设计稿 V2/index-persistence-evolution-design）：
HashIndex 主存储（内置，`TryDump()` fuzzy 逐槽原子拷贝三段式帧[PIHD/PIFT]，W=ring 已落盘水位
`GetFlushedWatermark()`，后台策略自动 dump + 版本链 N 版轮替；恢复帧优先+只重放 (W,End]）；
SortedIndex 主存储（2026-08-24 同构落地：固定锚点帧 32B 几何（BTree=BIHD/BIFT、SkipList=SLHD/SLFT 独有 magic）——节点写时持久化在自持
引擎、物化只设根+计数，族私有 codec 契约）；恢复核心帧优先——有效且 W∈[Begin,End] 载入+只重放
(W,End]，无帧/损坏/越界 fail-safe 全量同路。**零增量基准只展示恢复共同底盘**
（ring 重开+引擎重开+载入 IO，~45-60ms/50k 量级）；真收益在 **W&lt;尾的增量场景**
（重放量 ∝ 距尾距离，非数据总量）。三族载入零 key 回读/零结构重建（条目/塔链/子指针=地址一等公民）。
★坑档：持久化引擎的 DeleteOnClose 归组合根选择——true 的引擎重开恒无帧走全量
（bench 实测 applied=False 谜底）；跨实例持久组合须 false。

### 分解参考（Stopwatch 探针，100k 稳态，Hash 组合）

| 分解项 | ns/op |
|---|---:|
| Ring.Write 单独 | ~906 |
| HashIndex.Insert 单独 | ~904 |
| Ring.GetValue 单独 | ~74 |

## 怎么选（现状口径）

- **极省内存点查选 Hash**：161ns 一跳命中+取值。**FASTER 同形对照实测（2026-08-22，FasterHotReadBench，
  同机同轮：FASTER 2.6.5 纯内存 hlog 100k×(8B key+64B struct value) 会话热随机 Read = 133.4ns，
  本组合 = 158.4ns——差距 1.19×/25ns**。单线程随机热读 133ns 即 FASTER 在 i5-12400 的真实水位
  （"FASTER 级热读 100-500ns"的窄端；多线程吞吐摊薄的 ~50ns/op 是另一口径，勿混读）。
  25ns 差构成：两段组合的二次地址解码/就绪检查（Find→GetValue 分两次）+ 桶 128B 双 cache line
  （FASTER 桶 64B 恰一行）+ 接口分派（IKeyComparer/IKeyResolver vs 泛型特化）。代价=构造期必注入
  IKeyResolver（判等闭环）。
- **★ 地址一等公民教义（设计决策）**：16B LogicalAddress（seg/offset/ext）不可拆、不做
  8B 紧凑编码——第一性理由：<b>8B 永远无法正确解决无限地址空间的语义</b>（打包把几何烙进地址：
  段号占 N 位 ⇒ 段数 ≤2^N 且段大小 ≤2^(64−N)——要么现在锁死段数量+单段大小，要么段几何一改编码直接挂）；
  16B 是无限空间的正交表达（2^31 段 × 每段 2^63B，编码与几何解耦）。次级：判等忽略 ext、
  128b CAS 原语、跨段寻址零换算。
  上层自缓冲地址后<b>直达取值永久跳过 hash</b>：hash 路径只是<b>发现地址的一次性成本</b>，此后地址即句柄。
- **★ 发现路径四变体终局（2026-08-22，FasterHotReadBench，同机同轮）**：
  FASTER session.Read 126.96ns ｜ 本组合逐次 Find+GetValue 158.2ns（1.25×）｜
  scoped+span <b>逐次</b> 167.9ns（1.33×——每迭代进出双 scope ~4 次 epoch 转换不划算，勿用此形态）｜
  **scoped+span 批量（256 查/invocation，scope 一次进出摊薄）94.67ns（0.75×）——反超 FASTER 1.34×并破百**。
  组合层读的<b>终态形态</b>=`ring.EnterReadScope()`+`index.EnterScope()` 一批一进出 +
  `IndexScope.Find` + `Ring.GetValueSpan`（零拷贝切片；溢出 record 回退 thread-static 拷贝；
  span 生命周期契约=scope 内消费，页驱逐经 epoch 排水恒稳）。
  批口径 94.67 反超来源：零拷贝（FASTER SimpleFunctions Read 付 64B 出参拷贝）+ 无逐 op session 机器 +
  epoch 摊薄。残差项：双次地址解码（Find 内 key 回读+GetValueSpan 各一次，~10ns，需 IKeyResolver
  三方法定稿扩法——接口未动，已非必要）。
- **hash 发现路径的残差可再省 ~20ns**（非必须）：①零拷贝值交付（GetSpan/ref 直访热页，省 64B 拷贝
  ~10-15ns）②Find 一体化取值（省二次地址解码 ~10ns）——合计可望 ~135-140ns 追平 FASTER 同形。
  **①已落地**（GetValueSpan+ReadScope+IndexScope.Find，2026-08-22）：批口径 **94.67ns 反超 FASTER 1.34×
  并破百**（见上节四变体终局）——②的接口扩法已非必要。
- **有序遍历/range scan 选 Sorted**：BTree ~350ns / SkipList ~816ns——BTree 点查已进亚µs；SkipList 写比 BTree 便宜（无分裂重排）。
- **恢复速度**：无镜像全量重建 50k 条 67-130ms；**HashIndex 主存储已落地**（2026-08-23）——
  `TryDump()` 三段式帧 + 重开载帧+增量重放 (W,End)（零增量封顶于恢复底盘；真收益=W 距尾近的增量场景）。

## 已修复的性能坑（留档防复发）

- **SkipList 纪元式层分配（2026-08-21 销案）**：currentLevel 曾随条目数增长、节点层被钳制在插入时刻的 currentLevel——早插入 key 全被钳 level-1，早区链稀疏，查早 key 退化线性扫（50k 条实测平均 386 跳/Find，正常 ~25）。修复=层分配纯几何分布与时间解耦，currentLevel 改由实际节点层驱动。点查 15.8µs→2.22µs（7×）、恢复 625→118ms（5.3×）。
- **SkipList Insert updates 数组堆分配**：元素曾携带 ~288B 节点拷贝（~5KB/次分配，50k 重放 464MB/Gen0×30）——改 stackalloc 只存前驱地址（256B）。
- **SkipList 读后不回填缓存**：重开/重放场景下降路径全程引擎读——ReadNodeFromEngine 读后回填（缓存无上限=与索引数据同量级，节点即数据）。
- **写基准计时粒度假象**：IterationSetup 强制 InvocationCount=1 下单条/invocation 测出 49µs 假数——OperationsPerInvoke=256 批量摊平修正。
- **★ 索引节点缓存字典形态（2026-08-21 销案，perf 优化项 #1）**：BTree 内部节点/SkipList 全节点缓存原为
  `Dictionary<LogicalAddress, 大结构体>`——每层/每跳桶数组→条目数组两跳依赖 cache miss + out/局部两次
  160-176B 结构拷贝。修复=`LogicalAddressMap<T>`（Runtime/Structures 扁平开放寻址原语，13 项契约测试）：
  键值扁平数组线性探测一步直达、命中单次拷贝、定容（BTree）/生长（SkipList）双模式、单写者+并发读者
  （Tables 单引用装载一致快照、发布序先值后键、空槽=Invalid 而 Empty=合法键）。BTree 点查 1.93µs→1.67µs。
  附带销案：BTreeNode 只读访问器必须 `readonly`——ref readonly 接收者调非 readonly 成员触发 160B 防御性拷贝，
  零拷贝优化会被编译器静默吃掉。
- **★ 引擎读协议每读 ~0.5KB 堆分配（2026-08-21 销案，随诊揭出）**：上面缓存换扁平后点查 3144B/op 分配
  纹丝不动——真凶在 `StorageEngine.Read` 全量计划路径：`new List<ReadPlanChunk>` + `new SpinRWLock[]` +
  `plan.All(lambda)` 闭包×2（自旋重试路径每圈分配）+ 每读 `new FileOpenOptions`（record 类）+ 段名插值。
  修复=单段零分配快路径（请求整体在单段可见前缀内——节点/记录读绝对主流；锁协议对齐全有或全无/终验/
  64 圈活性守卫）+ IsReadPlanReadable 手写循环 + 读选项/段名记忆化。BTree 点查 1.67µs→812ns、分配清零；
  恢复 169.5→128.7ms、319.5→194.1MB。
- **★ BTree 定容缓存漏掉深层节点（2026-08-22 销案）**：定容 1024 下深层 internal+叶子恒走引擎读
  （~250ns/读 ×3/FIND 主宰 812ns）。实验翻生长模式（节点即数据，两族同教义）→ 361-452ns、
  100k 条仅 2.2MB 全 L3 常驻。旋钮上移基类 `NodeCacheInitialCapacity`（两族共用器官共用旋钮；
  旧名 InternalNodeCacheSize 说谎已废——缓存的从来不只是 internal，叶子同经下降路径进缓存）。
- **★ BDN 分配列跨线程归属噪声（2026-08-22 记档）**：写基准分配数跨轮 623B-60KB 漂移——
  MemoryDiagnoser 计全线程分配，引擎后台 worker（flush/采样）的分配随机记入 1-2ms 量级测量窗。
  判读纪律：分配论断用同线程探针口径（`GC.GetAllocatedBytesForCurrentThread` 段差，
  `--replay-alloc-probe`）复核；写基准时延列 ±7-10µs 常态只作量级读数。
- **★ Hash 恢复 334MB=构造期常量（2026-08-22 定案，同日销案）**：分配分解探针 + mock resolver 对照
  实锤全在 Hash 机械构造（HashTableCapacity 1<<20×128B=134.2MB+溢出池 33.5MB，与数据量无关）。
  **销案=容量自适应**：GrowIndex 从死器官复活为 Insert 触发的活器官（装载>0.7 翻倍，函数式构建
  新代表+单引用原子发布——表与溢出池同代对，并发读者持旧代一致探测 stale-but-valid，无需 epoch 排水，
  旧代归 GC）；默认容量 1<<20→1<<14。探针口径重放分配 167.9MB→**2.7MB（-98.4%，54B/条）**；
  点查 191-222→161ns（表缩 L3 的局部性红利）。
- **★ 增长触发的时序契约（2026-08-22 坑档）**：自动增长必须在<b>插入之前</b>检查阈值——插入后触发
  会踩"刚落位条目尚未被调用方注册进 resolver"的窗口（Insert 返回前），rehash 逐条 TryGetKey 失败
  即静默丢弃（实测 64 桶×200 条丢 3=3 次增长边界）。契约：**增长时刻的所有表内条目必可经
  KeyResolver 解析**——由"只在上一次调用返回后检查"保证。
- **★ SkipList 节点 arena 化（2026-08-22 销案）**：缓存值从 288B 全量 header（16 层塔恒占）
  换 arena 变长驻留（32+16×实际层高，均值 ~64B）——工作集 28.8→6.5MB 全 L3；跳一跳=
  探测+8B 指针直访（逐跳零拷贝，旧形每跳 288B 槽→局部拷贝 ×25 跳）。基座=`NodeArena`
  （分块 CAS-bump，读者侧 admit 安全、指针恒稳、无 per-node free）+ `LogicalAddressMap<nint>`
  （addr→指针）。点查 1.77-2.0µs→816ns（~2.2×）、恢复 117.6→93.5ms、分配 259.8→190MB。
  **布局偏移契约**（Key@0/SegId@8/Offset@16/Extension@24/LevelCount@28/Level_i@32+16i）钉在
  SkipListNodeHeader 注——改布局必须同步指针访问器。
- **★ SkipList 链写回性能税（2026-08-24 销案）**：主存储落地时链变更即时写回引擎
  （每插入 ~level 次额外引擎写）——实测写 3.8-4.6µs→**9.38µs（~2× 退化）**。
  修复=**脏节点延迟写回**：链/值变更记 `_dirtyNodes`（HashSet 去重），`WriteBody`（dump）时
  批量写回——插入路径零引擎写。实测写 **2.57µs（反超旧基线 1.5×）**；点查 844ns 无回归。
  安全性：脏节点必在驻留缓存（变更前经 GetNode/AdmitNode 登记）→ 读路径缓存命中不受影响；
  引擎副本只服务物化（dump 批量写回后读）；崩溃窗口（dump 前）由恢复重放 (W, End] 修复
  （W=上次 dump 水位，引擎旧链无害）。
- **★ BTree 写引擎写税（2026-08-24 销案）**：节点变更即时写引擎（每次插入/分裂 ~1-3 次
  256B 写）——实测写 14.6-18.9µs。修复=**脏节点延迟写回**（同 SkipList 模式）：
  `WriteNodeContent` 记 `_dirtyNodes`（HashSet 地址集），dump 时批量写。实测写 **3.3-4.1µs（3.6×）**、
  点查 378ns 回基线。**不变式**：WriteNodeContent 前节点必已入缓存（RefreshCache/根特例
  `_cachedRoot`；PromoteRoot 删 Clear 保旧根条目）→ 读路径缓存 miss=从未变更=引擎内容最新
  （热路径 FindNoEpoch 内联展开，无脏兜底）；值快照字典版 240B×100k 超 L3 污染点查（470ns）已销案。
  调试实录：字典版引发 Stack overflow（根叶分裂后旧根不在缓存、引擎零内容读成垃圾 internal
  无限下降——InsertRecursive 299 次）→ 脏兜底读 → 不变式化（根特例+RefreshCache+PromoteRoot 保条目）。
- **★ 物化重数 O(n) 税（2026-08-24 销案）**：Sorted 族物化重数实收（SkipList 层 0 链 50k 次
  引擎读 / BTree CountEntries 递归）≈ 重放成本——主存储载入 72.9ms 无加速优势。修复=**W==End
  零增量跳过重数**（无 fuzzy 混入 → 几何计数可信）：物化 72.9→63ms（-14%，测量噪声 ±10-20ms）；
  W&lt;End 有重放 → 重数保留（混入以实收为准）。

## 优化空间（未做，按预期收益排）

- **恢复共同底盘**（主存储零增量口径的 45-60ms 构成）：帧校验+物化合并读（Hash 16.8MB 读 2 遍→1 遍，
  ~5-8ms，中成本）；帧 CRC64 软件（~10ms）分块并行（PCLMULQDQ 折叠复杂，暂缓）。
  ★ 双引擎并行（ring/index 引擎恢复重叠）**已决定不做（2026-08-24）**：收益虚（引擎重开 mem/本地盘
  仅几 ms，且 index 恢复核心依赖 ring 恢复完成——hints 需 Begin/Tail，并行只重叠引擎启动一小段），
  成本高（生命周期骨架 API 拆分——信任边界改动），恢复是一次性冷启动非热路径。
- **Ring 写路径分解**（906ns 构成：CRC/页池/水位 CAS）——**批量写已落地（2026-08-24，`BeginWriteBatch`）**：
  独占写窗口摊薄逐 record 锁/页检查/epoch——组合口径 Hash -28% / BTree -39%；剩余（CRC/页缓冲写本体）
  为记录固有成本，组提交批 API 即正路（已交付）。
- **SkipList 残差**（点查 ~816-850ns vs BTree ~350ns）：~25 跳链追逐本征 + 每跳 map 探针的依赖 miss——
  结构形态决定，非形态税；要再压需换布局（如数组化塔/分层节点），收益/复杂度比低，暂缓。

## 维护

- 改动 Ring 写路径 / 索引 Find/Insert / 重放核心后必须重跑本基准并更新数字（性能论断须实测教义）。
- 镜像加速落地后在"恢复"表补增量重放一行，并记"镜像体加载 vs 全量重建"切换建议。
