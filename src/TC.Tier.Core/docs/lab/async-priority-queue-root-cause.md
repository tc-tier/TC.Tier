# AsyncPriorityQueue 并发正确性根因分析——为什么这个算法这么难

> 状态：**已修复（Route A 落地，2026-08-14）**。本文是取证档案 + 算法原理分析，供研究决策复盘。
> 取证材料：楔死 dump `pqwedge.dmp`、三轮批量 dq 原始数据（head 32 层指针 / 398 节点 Id+Seq / F0+F5 边表）。

---

## 0. TL;DR

**难的不是跳表，是我们同时要三样论文没一起证明过的东西：lock-free（论文有）+ 零分配池化（论文假设 GC，没有）+ 微秒级节点回收（论文交给 GC/hazard pointer，没有）。**

每一次修复不收敛（SKIP→回收去重→出边 mark→0层先插→仍然楔死），不是修的人不行，而是**修复都发生在"行级 CAS"层面，而缺陷在"协议不变式"层面**——不变式不满足时，失效模式会在别的交织下换个马甲重新出现。dump 已拍到第 4 个马甲（null-splice 断链 + 悬空高层捷径）。

---

## 1. 事故时间线（失效模式的"打地鼠"史）

| 阶段 | 症状 | 当时归因 | 修复 | 结果 |
|---|---|---|---|---|
| v0 | 多生产者丢元素/挂起 | 未知 | **测试 SKIP 藏起来** | 问题隐形 |
| `d6fbc223` 前 | 100% CPU 挂死 | 重复回收→同节点多租→跳表成环 | ReclaimNode TryRemove 胜者校验 | 环修了，丢元素还在 |
| `d6fbc223` | 丢元素 | 入边 mark 察觉不到 pred 已删 | 出边 mark（Michael 协议）+ 0 层先插 | 单测过，**本轮实测仍楔死** |
| 本次 dump | 3 生产者热自旋（CPU 1517s），只消费 4/500 | —— | **null-splice 断链 + 悬空捷径**（见 §4） | 待修 |

**规律**：每个修复都对，但只关掉一种交织；下一种交织换个失效形态回来。这就是不变式层面缺陷的 signature。

---

## 2. 算法谱系——我们抄的是哪几篇论文

| 论文/实现 | 贡献 | 我们借用的部分 |
|---|---|---|
| Pugh 1990, *Skip Lists: A Probabilistic Alternative to Balanced Trees* | 跳表数据结构 | 层级概率结构 |
| **Harris 2001**, *A Pragmatic Implementation of Non-Blocking Linked-Lists* (DISC) | **删除协议**：mark 后继指针位 + 物理 splice；mark 令并发 insert 天然失败 | 出边 mark + splice 思想 |
| Herlihy, Lotan, Shavit 2000, *Using Concurrency to Improve Flexibility and Performance in a Priority Queue* | **跳表式无锁优先队列**（本组件的直接原型） | 整体形态：跳表 + 单点 dequeuer |
| **Michael 2002/2004**, *Safe Memory Reclamation for Dynamic Lock-Free Objects* | hazard pointer：读者护住节点，回收者等全部放手 | 我们用 **LightEpoch** 替代（FASTER 谱系） |
| Fraser & Harris 2007, *Concurrent Programming Without Locks* | 无锁跳表全文 + epoch 变体 | —— |
| **JDK `ConcurrentSkipListMap`**（工程范本） | marker 节点、lazy deletion、**完全依赖 GC 回收** | ——（我们没抄它的关键部分，见 §3-D2） |
| Chandramouli et al. 2018, *FASTER*（SIGMOD） | epoch protection 的工程化 | LightEpoch |

---

## 3. 论文不变式 vs 我们的实现——逐条对照

这是本文核心。论文算法的每条不变式都自带**隐含前提**；我们为了性能目标破坏前提，等于把 GC/回收器的证明义务接过来自己背。

| # | 论文不变式（隐含前提） | 论文怎么保证 | 我们的实现 | 偏离 → 失效模式 |
|---|---|---|---|---|
| **I1** | **节点身份不可变**：一旦链入，key/next 等语义字段终身不改写（前提：节点用完即弃，交给分配器） | fresh allocation + GC | **池化复用**：`RentNode`→`Reinitialize` 改写 Priority/Sequence/Forward | 复用窗口内他线程仍持节点引用 → 读到新旧混合状态；旧 Id 幽灵解析 |
| **I2** | **mark 与指针同原子域**：Harris 把 mark 塞进指针低位，CAS 一次管两个字段 | 指针位标记 | `MarkedReference` 128 位（Reference+Flags）CAS——**这条我们其实做对了** | ✅（NativeAtomic128 是对的） |
| **I3** | **splice 永远能读死节点的 next**（前提：边存**直接对象引用**，节点在 splice 完成前绝不被回收） | 直接引用 + GC/hazard 保证存活 | **边存 Id**，经 `_nodeMap` 间接寻址；**回收（TryRemove）可与 splice 并发** | Id 被 TryRemove 后 splice 读不到 next → **只能拔链（CAS→0）而不是接线** → **后缀整段丢失**。dump：`head.Forward[0]=(0,unmarked)`，376 活节点 0 层全体脱钩 |
| **I4** | **回收迟后于最后一个读者**（hazard/epoch 严格性） | hazard pointer 或全局 epoch 静默期 | LightEpoch——但回收与 splice 的交错没有被 epoch 覆盖（`ReclaimNode.TryRemove` 发生在 drain 回调里，splice 发生在别的线程 Search 中段） | I3 的竞态源头 |
| **I5** | **插入全层原子发布**（要么全链上、要么不可见；高层可见低层不可见=中间态非法） | Fraser 协议逐层 CAS + 失败全撤 | 0 层严格 + **高层 best-effort（失败忽略）** | 高层捷径**永久指向旧拓扑** → dump：`head.Forward[5]→522` 而 0 层已断，孤儿链只从这条僵尸捷径悬挂 |

**结论**：五条不变式我们实质满足的只有 I2。I1/I3/I4/I5 各缺一角，四种失效模式（错位链接、断链丢后缀、热自旋、僵尸捷径）分别对应——这不是巧合，是必然。

---

## 4. 楔死现场解剖（dump 证据链）

```
队列状态：_count=4（只消费4个） _sequenceCounter=406（入队406/500） Id计数=543
head 32层指针：F0=(0,unmarked) ← 0层主链从根断开
              F1..F4=0  F5=(id=522,unmarked) ← 唯一活着的入口：悬空捷径
              F6..F31=0
_nodeMap：376 条目（健康，Id 无爆炸——"双注册"假设已证伪）
活节点：398 个，F0/F5 静态图均无环
3 个生产者栈：Search ← Enqueue，其中一线程卡 Node.get_Key（key 步进循环）
CPU：1517 秒（热自旋）
```

**静态无环 + 热自旋 = 活体变异循环**——环不是快照里的固定结构，而是清理路径在并发改写下"动态维持"的：

```
自环形成路径（backward 边 + 剥 mark）：
  pred.F[L] = (M, marked)，且 M.F[L].Reference = pred.Id（后向边）
  → Search 清理：sr = (pred.Id, unmarked)，splice pred.F = sr
  → pred.F[L] 指向【自己】且未标记
  → key 步进循环 while(cn.Key < key)：cn=pred，nxt=pred.F=pred，cn=pred……
  → 永真热自旋（正是 dump 栈里的 Node.get_Key 帧）
```

后向边从哪来：I1 复用窗口 / I3 拔链残留 / I5 高层旧拓扑，三者都能造出。

---

## 5. 为什么"照论文抄"还是难——难度的真实来源

1. **论文的删除协议只在"直接引用 + GC"世界被证明过**。Harris 2001 第一节就假设 garbage-collected memory。我们的 id 寻址 + 池化组合没有论文可抄——等于在协议层做原创，而 lock-free 删除本来就是教科书里最难正确的一章。
2. **每处"性能优化"都是一次不变式破坏**，但破坏的代价不在破坏点显形：I3 的 TryRemove 竞态在千里之外的 `head.F0=0` 显形。定位成本 ≈ 本次一下午（dump + 三轮 dq + 图分析）。
3. **交织空间无界**，单测绿灯只覆盖采样到的那部分。证据：`d6fbc223` 修完单测全绿，真跑第一轮就楔死。
4. **JDK ConcurrentSkipListMap 为什么稳**：它放弃了我们全部四个激进目标中的三个——不池化（每节点 fresh，删除后扔给 GC）、不 epoch（GC 就是它的回收器）、marker 节点让 splice 永不读死节点 next 的语义字段。**它是用"放弃"换正确性的范本。**

---

## 6. 修复路线对比（供决策）

| 路线 | 内容 | 正确性 | 性能代价 | 工作量 | 风险 |
|---|---|---|---|---|---|
| **A. 回归标准形态** | 边存**直接引用** + marker 节点（JDK `ConcurrentSkipListMap` 式：单引用 CAS + 哨兵节点表达 mark，不依赖 GC 以外的任何回收机制）；**不池化**，节点交给 GC；删 `_nodeMap`/epoch 整条回收线 | 论文已证明（JDK 同款，十余年生产验证） | 每入队一次分配（Gen0 极廉价，LOH 无压力） | 小——**删代码多于写代码** | 低 |
| **B'. 非移动内存 + 槽位寻址 + 128 位 CAS（保池化）** | 节点放**不动的内存**：POH slab（`GC.AllocateArray(pinned:true)` 大块）或 **NativeArena 原生内存**（仓库已有）；边存 `(slotIndex, generation)` 而非托管引用；`NativeAtomic128` CAS 照用（`MarkedReference` 换成 `slotRef+flags`）；池=空闲槽链 | 论文前提（节点地址不漂移、身份由 槽+代数 构成）**逐字复刻**；I1/I3 由构造消除；**ABA 由 generation 位天然免疫**——槽复用即代数+1，陈旧边解析出代数不匹配 → 失效可判定（fail-visible），不再静默腐蚀/楔死 | 保住零分配 | 中——节点分配器 + 代数解析 | 中——**I4（epoch 严格性）仍是承重墙**：槽复用必须等 epoch 静默；但失效从"楔死"降级为"断言触发"，可调试性质变 |
| **B. A + epoch 严格回收** | 在 A 基础上恢复池化（托管对象池） | 需自证 epoch 无洞 | 保住零分配 | 中 | 中——托管池化下失效仍不可见（无 generation 防线），不如 B' |
| **C. 保 id 寻址 + 延迟 TryRemove** | 节点带出边计数，splice 完才能回收 | 需自证（无先例可抄） | 计数维护开销 | 大 | **高，不推荐** |

**关于"我们不是有双 long CAS 吗"**（2026-08-14 讨论结论）：有——`NativeAtomic128`，且 `MarkedReference` 的 128 位 CAS 一直在用（即 I2，五条不变式里唯一已满足的）。但 I3 的墙**不是 CAS 宽度，是 GC interop**：把对象引用塞进 long 裸 CAS 会撞三堵墙——① 写屏障旁路（老→新引用漏记 card table → 新生代节点被提前回收 UAF）；② 压缩移动（GC 搬对象，裸指针悬垂）；③ 存活性不可见（GC 不认 long 里的地址，需外部根集）。**B' 路线正是用"非移动内存"拆掉这三堵墙**，让 128 位 CAS 的能力完整兑现——宽度我们早就有，缺的是不漂移的地基。

**建议**：A 先拿正确性基线（测试全绿、楔死消失、压测拿分配开销真实数字）；若零分配确为瓶颈，走 **B'**（不是 B——B' 的 generation 防线把 epoch 失效从楔死降级为断言，与 `aa564b24` 的 epoch 示波器互补：B' 负责"失效可见"，epoch 严格性负责"失效不发生"）。C 放弃。

**✅ 决策落地（2026-08-14）**：**Route A 已实现**——`AsyncPriorityQueue` 重写为 Fomitchev–Ruppert **marker 删除协议**：
边存直接对象引用（`Link` = `Node | Marker`），逻辑删除 = 把 `victim.Forward[L]` CAS 成 `Marker(真实后继)`，
物理摘除 = 前驱边绕过 victim 直连 `marker.Next`；**单引用 64 位 CAS**（连 I2 的 128 位打包都不再需要），
节点 fresh 分配交 GC（删 `_nodeMap`/`LightEpoch`/池化整条回收线）。五条不变式全部由构造消除，
`LightEpoch` 构造参数保留为兼容 no-op。零分配能力暂失，若压测证实为瓶颈再上 B'（该路线分析仍有效）。

**🔬 B' 验证落地（2026-08-14，`AsyncPriorityQueueV2`）**：按档案路线做了实现级验证——
槽位寻址（边 = `slotIndex<<16 | generation`，单 64 位 CAS）+ NativeArena 非移动内存 + 侵入式空闲链池 +
epoch 静默回收（**全层物理摘除后才入 pending、才 bump**）+ 预租 marker。结论：

- ✅ **可行性与零分配证实**：`Allocation_V2` 2 万次出入队 **0 字节**托管分配（A 对照 >0）；契约测试族 17/19 稳定通过。
- ✅ **generation fail-visible 兑现**：所有失效都以断言/异常响亮暴露（陈旧引用/双归还/环破坏），无静默腐蚀、无楔死。
- ⚠️ **I4 承重墙实锤**（与预言一致）：高压多线程下仍有**罕见一代陈旧引用竞态**（slot 复用恰在读者持边窗口），
  验证过程中揪出并修复 8 个具体缺陷：摘除擦 mark（复活已删节点）、空闲链 ABA（读 Key 当索引 → AV）、
  16 位 tag 溢出（污染 head 位）、ring 水位缺屏障（回绕覆盖 drain 中块 → 双归还）、保护区内 bump 自锁死
  （16 槽 drain 打满）、未链接层 stale 边（best-effort 高层链接的 succs 无人清理）、gen 写读缺 happens-before 对、
  Find pred 跨层携带。
- **裁决**：B' 架构成立、可继续攻坚（剩余竞态在摘除/回收窗口的 epoch 覆盖上），但**生产仍用 A**；
  V2 保留为验证基线与回归绊线（重型压力测试即该竞态的探测器，偶发失败 = 残余竞态命中，非新回归）。

**🔬 边内 mark 变体验证（同日，已证伪回收）**：为排除"marker 暂存后继"机制，另做了一版 **Harris 原版
边内 mark**（16B 边 `(slotRef, flags)` + 仓库既有 `NativeAtomic128`/`AlignedMemoryManager` 64B 对齐背板，
mark 在前驱边、摘除时 mark 随边转移、victim 自身边发布后永不改写——I1 完全恢复）。结果：
**残留竞态依旧**（同为"一代陈旧引用"，另多出一种楔死模式）——证明残余问题<b>不在 mark 机制</b>，
而在**槽位复用世界的 epoch/摘除窗口本身**（"边指向已删节点"的窗口期与回收的交错）。该变体已回退，
结论归档：B' 若继续，方向是**依赖追踪式回收**（摘除完成显式计数，Route C 类）或换"删除即完整移除"的协议，
而不是继续换 mark 表示。仓库既有 128 位 CAS 基件经此验证确认可直用于该场景（整数载荷，无 GC 三墙）。

**配套验证器（无论哪条路线都要加，对齐"Debug 跟踪"铁律）**：
- **DEBUG 链校验器**：任意操作后可 O(n) 走 `head.F0`，断言 key 严格递增、无 0 之外的非法跳——自环/后向边当场抓获；
- 契约测试族：多生产者不丢不重（已恢复的 2 个）、单生产者序、消费到空、楔死看门狗（生产者侧也要超时——本次生产者无超时导致 suite 挂死）。

---

## 8. 2026-08-17 压测驱动修复——Route A 自身的三个协议缺陷（尾插退化 / 发布覆盖 / 标记-splice 竞态）

> 状态：**已修复并全量复测**。本节是 Route A 落地后第一次全功能压测（Windows 真机，独立计时探针
> `--pq-probe` + 楔死复现器 `--pq-wedge`）的取证与修复档案。数据见
> [../perf/priority-queues-performance.md](../perf/priority-queues-performance.md) §0。

### 8.1 发现路径（按取证顺序）

1. **积压敏感性实验**暴露缺陷一：Async 在 8K+ 积压下超线性爆炸（64K ≈ 602µs/op，与 64K×~9ns/步的
   level-0 线性扫描成本精确吻合）——**尾插不建高层索引**：`Enqueue` 高层循环 `if (succs[i] is not null) TryLink(...)`
   把"插到层尾"（succs==null）的链接直接跳过——持续尾插负载（如最大优先级段持续入队）下高层索引永不建立。
2. 修掉缺陷一后 **`Stress_EnqueueDequeueRounds` 8/10 次活性死锁**（120s 看门狗超时，CPU 低——非热自旋）。
   两轮 `dotnet-dump` 走链取证：head 层 1/2 边指向 **key 小于层 0 队头的已删节点**（悬挂僵尸），其
   F0/F1 是 Marker 但 F2 是普通引用；队头 victim 已标未摘；消费者与生产者**双侧自旋**（dotnet-stack）。
   DEBUG 链校验器全程未爆——**结构不断裂，是活性死锁不是结构损坏**。
3. **DEBUG 示波器断言**（发布点 F0 快照 vs 高层循环现值）抓到实锤：发布后 F0 被改写为普通 Node——
   **"第四写点"是 Find 的 helping splice**（`CAS(pred.Forward[level], m2.Next, curNode)` 写普通后继引用）。

### 8.2 三个缺陷的机理与修复

**缺陷一：尾插不建索引（性能）**。修复：去掉 `is not null` 条件——`TryLink` 的 CAS 对 null succ 天然
支持（比较值 null，仅当该边仍为 null 时成功）。正确性不变（失败即忽略的 best-effort 语义）。
效果：256K 尾插从 ≥1.9ms/op → 1.5µs/op；单线程（1024 积压）从 1156ns → 423ns。

**缺陷二：发布后的普通写可覆盖 Marker（协议，缺陷一的修复使其暴露）**。原代码先发布 level-0、
后写高层 `node.Forward[i] = succs[i]`（普通写）——发布后删除者可立即标记其高层（读到未写完的 null），
随后本线程的普通写把 Marker 抹掉——已删节点"复活"为未标节点。修复：**发布纪律**——node 全部
Forward 字段在 level-0 发布（唯一使节点可达的动作）之前写完，发布后绝不再写 node 自身。

**缺陷三：标记 CAS 被 splice 打败 → 悬挂僵尸 → 活性死锁（并发，最严重）**。删除者标记 victim 高层
的 CAS 与 Find 的 helping splice CAS **竞争同一字段**：splice 打败标记后留下"F0 已标（元素已出队）但
高层未标"的节点——它在高层索引里且 Find 无法识别为已删（边是普通引用）→ 永不摘除 → 悬挂僵尸
（key 已小于层 0 队头）。后续 Enqueue 的 Find 从僵尸的旧世界边出发，preds 落在旧世界上，发布 CAS
恒失败 → Enqueue 永旋；队头已标 victim 无人 splice → TryDequeue 永旋——**全体冻结，无死锁环、
无结构断裂、CPU 低**（都在 SpinWait 的 Sleep 降级里等对方）。修复：**标记循环重试到 Marker 落地**
——splice 使 victim.F[i] 沿链前进有限步后停止（后继未删时无 splice），此后标记 CAS 是唯一写者必然成功。

### 8.3 复测结论（全绿）

- 楔死复现器 `--pq-wedge` 10/10 通过（修复前 8/10 楔死）；
- `Stress_EnqueueDequeueRounds` 10/10（~180ms；修复前 8/10 次 120s 超时）；
- Debug 正确性压测 12/12（链校验器巡检无损坏）+ Release 11/12（唯一失败是 SkipList 的固有活性特征，与本组件无关）；
- Core 全量单测 1046/1047（1 个与本改动无关的既有 flaky：IsolatedTaskScheduler 实例跟踪器）。

### 8.4 教训（对齐 §0 的"打地鼠"规律）

Route A 回归论文前提后**协议本体正确**，但三处工程细节（尾插跳过、发布后写、标记不重试）各自
违反了协议的隐含前提——**每一次"性能优化式省略"都是一次不变式破坏**（§3 的结论在 Route A 身上
再次应验，只是失效形态从"断链/楔死"降级为"索引退化/活性死锁"）。活性死锁对 DEBUG 链校验器
**隐形**（结构不断裂）——活性回归必须靠**楔死看门狗测试 + 独立复现器**兜底，两者已入库。

---

## 7. 给研究者的阅读清单（按因果顺序）

1. Harris 2001——删除协议本体（mark + splice）
2. Herlihy & Shavit, *The Art of Multiprocessor Programming*, 链表章节——不变式推导
3. JDK `ConcurrentSkipListMap` 源码（`doRemove` + Marker 节点）——工程化范本
4. Michael 2002 / FASTER 2018——"回收迟于读者"的两条路线
5. 本文 §3 对照表——我们的四个偏离点，各配一个失效案例

---

*取证：2026-08-14，ZCode + 唐远能（d6fbc223 / aa564b24 两轮修复与 LightEpoch 示波器）。dump 与 dq 原始数据可复核。*
