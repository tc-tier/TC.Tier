# TC.Tier.Core.Net 协调框架使用指南

> 本文件是 **Core.Net 层积木的"正确拼装"指南**：网络与共识机制零件的职责、正确用法、以及反模式。
> 它不重复每个类的 XML 注释，而是回答"**遇到 X 该用哪个积木、怎么用、什么绝对不要做**"。
>
> **Core.Net 的范围**（五层架构的网络层）：节点身份（`NodeId`）+ 线协议（`Wire/`——16B 帧头双
> CRC32C，全部由 CodeGen 生成物产出）+ 传输内核（`Transport/`——TCP 长连接/InProcess 枢纽/UDP
> 端点通告）+ 形态面（`Channels/`——数据报/请求回调/流式三形态，`IProtocolTransport` 介质无关
> 消费面）+ 机制面（`Raft/` 共识全家、`P2P/` HyParView、`Swarm/` 多源块同步）+ 装配面
> （`Hosting/`——ClusterBuilder/NetClientBuilder）+ 端口（`Ports/`——身份/机制挂载，零 IO）。
> **Core.Net 零产品知识**：永不依赖 `TC.Tier.Products`（编译期 E4 依赖锁封堵）；协议域注册区
> `0x60-0xAF` 归使用方自管。
>
> 配合阅读（项目内深读）：
> - [`docs/net.md`](docs/net.md) —— 使用指南（快速上手/三形态怎么选/传输调参/装配/raft 机制面/P2P 会员与广播；示例即测试）
> - [`docs/perf/loopback-baseline.md`](docs/perf/loopback-baseline.md) —— 形态面与 raft 引擎性能基线
> - [`../TC.Tier.Core/COORDINATION.md`](../TC.Tier.Core/COORDINATION.md) —— Core 层积木
>   （TaskSink/日志/可观测——Core.Net 的执行与观测全部消费 Core 积木，不自建）

---

## 0. 一句话总纲

**传输层提供介质无关的三形态契约，机制零件（raft/P2P/Swarm）全部装配其上；介质实现
（TCP/InProcess/UDP）是 internal 细节，消费方只碰 `IProtocolTransport`。**
选型错了（绕形态面直造 socket、把 raft 引擎当日志库用）会同时丢掉同构验收、故障注入和背压语义。

---

## 1. 核心积木全景

| 积木 | 位置 | 职责 | 何时用 |
|------|------|------|--------|
| `IProtocolTransport` | `Channels/` | **节点端点完整面**（连接面 `ITransport` × 协议域面 `IProtocol` 合一）——介质无关消费面 | 机制/装配/使用方实际持有的类型；换介质零改代码 |
| `InProcessTransportHub` / `InProcessNode` | `Transport/InProcess/` | 进程内介质（同构基准/语义参考实现——发送方上下文直排派发；内建注入矩阵） | 单元/契约测试主场、同进程多节点夹具 |
| TCP 介质（`ClusterTransport`/`PeerLink`） | `Transport/Tcp/` | TCP 长连接（三步握手/双优先写队列/读缓冲批拉/保活）；`ClusterTransport` public（直构可达——探测/benchmark 直连形态），`PeerLink` 🔒 internal——**装配一律走 `ClusterBuilder`** | 生产跨进程；测试用 `TransportIsomorphismTests` 形态对齐 |
| 数据报（`SendDatagramAsync`） | `Channels/` | 尽力送达：对端未连/未注册/注入丢弃 = 静默；handler 快进快出 | 心跳/探测/可容忍丢失的广播 |
| 请求回调（`SendRequestAsync`） | `Channels/` | 单请求 → 关联应答 → 超时（CorrId 传输生成）；目标未连抛 `NetIOException`；缺省 at-most-once，`RetryPolicy` 显式升级 at-least-once（重发 + 应答端去重窗口重放） | RPC 形态（raft AppendEntries、块请求） |
| 流式（`OpenStreamAsync`/`IWireStream`） | `Channels/` | 可靠有序会话 + 背压（写 await 传导）；GB 级 O(单帧) 不驻留；会话不跨重连 | 大块传输（快照安装/备份导出） |
| `RaftStateMachine` | `Raft/` | 共识引擎（选举/复制/提交/成员变更/ReadIndex 线性读；单写者事件循环 + per-peer 复制链 + apply 管道） | 需要 majority 提交语义的复制日志 |
| `ApplyPipeline` | `Raft/` | 提交→应用管道（单 worker 按日志序 apply + appliedIndex 节流落盘 + 配置条目分流） | raft 装配必配件（与状态机成对构造） |
| `IRaftStore` | `Raft/` | raft 对持久化的**全部**需求面（端口——Core.Net 零 IO） | 实现它接入任意存储；夹具 `InMemoryRaftStore`/`FsRaftStore` 即现成两实现 |
| `ReplicateAsync` / `ReplicateCommittedAsync` | `Raft/` | 复制完成档：**applied**（read-your-writes 默认档）∥ **committed**（多数派提交即返——吞吐档） | 需要"返回即可读到效果"用默认档；纯复制/灌数据用 committed 档 |
| 快照安装（`ISnapshotTransfer` 双实现） | `Raft/` | 增量无法接续时的全量追赶：单源流式（`SnapshotStreamTransfer`）∥ 多源 swarm（`SnapshotSwarmTransfer`——块清单/K 并行/换源重试） | follower 落后超过快照覆盖点；双形态同场景同断言 |
| `Membership` | `Raft/` | 成员变更提案面（single-server 变更两步提交） | 在线加/减成员 |
| `PeerController`（HyParView） | `P2P/` | 间歇性成员协议（partial view 维护/优先级保护/shuffle 混合；`PhiAccrualDetector` 故障检测） | 大规模对等网（节点发现/传播拓扑）；`builder.WithP2P(seed)` 挂载 |
| `SwarmBroadcast`（`IBroadcast`） | `P2P/` | gossip 广播（MsgId 去重/TTL 逐跳/活跃视图扇出——最终一致送达，不保序不保达） | 对等组通知/通告；`PeerOptions.WithBroadcast()` opt-in，`PeerMechanism.Broadcast` 句柄 |
| `SwarmSync` | `Swarm/` | 多源块同步协议（manifest 持有者协商/并行拉块/错块拦截）——协议族复用件 | 任意"分块内容多源分发"（快照 swarm 是第一消费者） |
| `ClusterBuilder` / `NetClientBuilder` | `Hosting/` | 三段式装配（Create → 链式 → `StartAsync` 一次成型返回 `NodeEndpoint`） | **一切节点/客户端的装配入口**——不手工构造传输 |
| `NodeEndpoint` | `Hosting/` | 装配产物（`IProtocolTransport` + 已挂载机制列表；机制生命周期随端点释放） | 使用方持有点位句柄 |
| `IIdentitySource` / `NodeIdentity` | `Ports/` | 身份供给端口（Core.Net 零 IO——持久化形态归组装层） | 跨重启稳定身份（TierFsIdentityStore 在产品侧实现端口） |
| `INodeMechanism` | `Ports/` | 机制挂载端口（内建与第三方/私有域同一挂载路径——零特权） | 把任何协议机制挂到节点（`WithMechanism`） |
| `ITransportFaultInjector` | `Transport/` | 故障注入常设面（延迟/分区/丢包/乱序——任一介质等价复跑） | 测试/对抗场景；`transport.Faults` 即得 |
| `NodeId` | 根 | 协议一等身份（16B 不透明字节串；Empty 哨兵/NewRandom/NewSequential/hex32 文本） | 全部 API 的节点标识；`Guid` 只在边界适配 |
| `NetIOException` | 根 | 网络层契约异常（不裸抛 BCL `IOException`/`SocketException`——inner 携带保留诊断） | 传输失败的消费面判定 |
| **— 安全（三档——见使用指南安全档节）—** | | | |
| `SecurityOptions` | `Security/` | 安全档配置（Plaintext ∥ KeyPair（推荐缺省——签名+ECDH+AEAD/MAC）∥ MutualTls；帧保护两粒度） | 装配期一次给全；防降级 fail-closed |
| `NodeKeyPair` | `Security/` | P-256 静态密钥对（P1363 定长签名/ECDH/SEC1 compressed 公钥——算法锁死无协商） | KeyPair 档本端身份 |
| `SecureSession`/`SecureRecordCodec` | `Security/` | KeyPair 档握手驱动（Noise IK 同构——静态签名+临时 ECDH 前向保密）与记录层（AES-GCM ∥ HMAC 两粒度+计数器防重放） | 引擎内部（PeerLink 接线）——测试/审计面 |
| `ITrustAnchorStore` | `Security/` | 信任锚端口（`PinnedTrustStore` 钉扎=推荐/mTLS 级信任强度；`InMemoryTrustStore` TOFU——首连有中间人窗口） | NodeId↔公钥绑定的持久化归组装层 |
| `CertificateNodeId` | `Security/` | mTLS SAN `nid:` 绑定（ASN.1 提取/绑定判定——无 nid/多绑定=拒绝） | mTLS 档证书签发与校验 |

---

## 2. 三形态怎么选（数据报 / 请求回调 / 流式）

### 2.1 一页选型表

| 需求 | 用 | 语义 |
|------|----|------|
| 可容忍丢失的短消息（心跳/探测/通告） | `SendDatagramAsync` | 尽力送达——丢弃静默，协议域自愈 |
| 一问一答（RPC） | `SendRequestAsync` | CorrId 关联 + 超时；未连**快速失败**（抛，不静默浪费超时窗） |
| 需要确认到达但可接受重复 | `SendRequestAsync` + `RetryPolicy` | at-least-once（重发 + 应答端去重窗口缓存重放——不重复执行） |
| 大块/持续传输（快照/备份） | `OpenStreamAsync` | 可靠有序 + 背压；**GB 级 O(单帧) 内存**；会话不跨重连（断 = Reset 重开） |

### 2.2 请求回调的正确拼法

```csharp
// 服务端：注册 handler（协议域号 = 使用方自管空间 0x60-0xAF）
transport.RegisterRequestHandler(MyProtocolId, new MyHandler());

sealed class MyHandler : IRequestHandler
{
    // 同步回调——快进快出（重活自排队）；应答经 reply（零次调用 = 对端超时）
    public void OnRequest(NodeId from, ReadOnlyMemory<byte> payload, IReplyContext reply)
        => _ = reply.ReplyAsync(Process(payload));
}

// 客户端
var resp = await transport.SendRequestAsync(target, MyProtocolId, payload,
    new RequestOptions { Timeout = TimeSpan.FromSeconds(2) }, ct);
```

- **载荷生命周期契约**：传输 handler 的入站载荷为每帧独立缓冲，**可安全持有/异步消化**；
  raft `IStateMachine.ApplyAsync` 的 `command` 相反——**仅调用期间有效，禁跨调用持有**
  （需要保留必须拷贝）。
- 目标未连 = `NetIOException`（快速失败）；注入丢弃/链路断 = 超时（尽力语义）。
- raft 的 AppendEntries 就跑在这条路径上（`ProtocolIds.Raft`）——机制面自己也是三形态的消费者。

### 2.3 流式的正确拼法

```csharp
// 发起端
await using var stream = await transport.OpenStreamAsync(target, MyProtocolId, ct: ct);
await stream.WriteAsync(chunk, ct);        // 每帧可靠有序；await = 背压传导（对端慢则本端等待）
await stream.CompleteAsync(ct);            // 正常收尾（End 帧）

// 接受端（RegisterStreamAcceptor 的 OnStream 回调里起消费循环）
await foreach (var frame in stream.ReadAllAsync(ct))
    Process(frame);                        // 逐帧处理——GB 级 O(单帧) 不驻留
```

- 读侧 `ReadAllAsync` 逐帧交付——**实现承诺 O(单帧) 内存**，消费方自己也不要攒全量。
- 正常收尾 = `CompleteAsync`（对端枚举自然结束）；异常中止 = Dispose（Reset 帧，双方终止）；
  会话不跨重连（断 = Reset，上层重开）。

---

## 3. raft 机制面

### 3.1 装配（夹具模式）

```csharp
var store = new InMemoryRaftStore();                 // 或 FsRaftStore（真持久化语义）
await store.InitializeAsync(ct);
var machine = new MyStateMachine();                  // 业务状态机（IStateMachine）
var pipeline = new ApplyPipeline(store, machine, ApplyPipelineOptions.Default);
var raft = new RaftStateMachine(id, store, transport, pipeline, RaftOptions.Default);
pipeline.SetConfigCallback(raft.PostConfigChanged);  // 配置条目 apply 产物回流
pipeline.StartAsync();
await raft.StartAsync(new ClusterConfig(members));   // 一次成型
```

- **成对装配**：`RaftStateMachine` + `ApplyPipeline` 必须成对（提交与应用解耦是设计——apply
  落后安全，只延迟读服务）。
- 存储经 `IRaftStore` 端口注入——同套场景测试在 `InMemoryRaftStore` 与 `FsRaftStore` 上复跑，
  与产品适配器对拍。
- 停止顺序：`raft.StopAsync` → `pipeline.DisposeAsync` → `transport.DisposeAsync` → `store.DisposeAsync`。

### 3.2 复制完成档

| API | 完成语义 | 用 |
|-----|---------|----|
| `ReplicateAsync` | **applied**（read-your-writes——返回即可从业务存储读到效果） | 默认档 |
| `ReplicateCommittedAsync` | **committed**（多数派提交即返，不等 apply） | 吞吐档（纯复制/批量灌数据） |
| `ReplicateBatchAsync` | 一批一次追加，批尾 applied | 批量写 |
| `ReadIndexAsync` | 线性读（多数派身份确认后返回 readIndex） | 读到最新已提交状态 |

- 非 Leader 立即抛 `NotLeaderException`——调用方重路由新 Leader 重试（客户端标准模式：
  catch → 查 `LeaderId` → 重试）。
- ★ 分区内旧 Leader 依旧自认 Leader（收不到新 term 是 raft 正确行为）——它的提交只会挂起
  等多数派，**不会**快速失败；`NotLeaderException` 只在感知换届后出现。测试断言注意这个窗口。
- 换届在途等待者全部以 `NotLeaderException` 取消（重路由语义，不是 bug）。

### 3.3 可观测面

- `raft.CommitIndex` / `raft.IsLeader` / `raft.LeaderId` / `raft.CurrentTerm`——跨线程观测。
- `LeaderChanged` / `EntryCommitted` 事件——换届/提交推进通知。
- 帧级指标经 Core `ObservabilityHub` Net 视图（`hub.Net`——帧收发采样/CRC 失败/握手失败/
  数据报丢弃；per-protocol tag）。

---

## 4. 装配面（Hosting）

### 4.1 `ClusterBuilder`——集群成员节点

```csharp
await using var endpoint = await ClusterBuilder.Create(nodeId)          // 或 Create(identitySource)
    .ClusterTag(0x74696572)                                             // 错集群 fail-fast（握手期拒绝）
    .Listen("0.0.0.0", 9400)                                            // 监听（成员制/直连共用一口）
    .Peers(peers)                                                       // 成员表（NodeId → 端点）
    .WithUdp(9401)                                                      // 可选：UDP 数据报承载
    .WithTransport(o => o.WithReconnect(                                // 可选：全旋钮管道（可叠加）
        TimeSpan.FromMilliseconds(100), 2.0, TimeSpan.FromSeconds(1)))
    .WithMechanism(myMechanism)                                         // 机制显式 opt-in（P2P: WithP2P(seed)）
    .WithLogger(logger)
    .StartAsync(ct);
// endpoint 即 IProtocolTransport + 已挂载机制；Dispose 一并收口
// endpoint.Options = 生效配置（显式方法 > 注入/管道 > 缺省——装配自证诊断面）
```

### 4.2 `NetClientBuilder`——直连客户端

```csharp
await using var client = await NetClientBuilder.Create(nodeId)
    .Connect("node1.example.com", 9400)     // 地址制直连（身份从握手得知）
    .ClusterTag(0x74696572)                 // 拨入带标签集群必须匹配（缺省 0）
    .RegisterRequestHandler(MyProtocolId, handler)   // 注册可前置（先于拨号）
    .Listen(9500)                            // 可选：开监听成对称节点（服务端可反向请求）
    .StartAsync(ct);
var remote = client.RemoteId;                // 握手学得的对端身份
```

- **安全档（客户端预设）**：`WithKeyPair(SecurityOptions)`/`WithMutualTls(...)` 强制档位校验——
  null/错档配置抛 `ArgumentNullException`/`ArgumentException`（fail-closed——不静默降级明文）；
  带配置的服务端安全档装配见 `net.md` 安全档节。
- 拨号归属：成员制 = NodeId 较小方拨号（双向对拨防御）；地址制 = 有地址者拨号。

---

## 5. 故障注入与测试

- `transport.Faults`（`ITransportFaultInjector` 常设面）：`SetLatency` / `Partition`（双向断）/
  `Drop(rate)` / `Reorder` / `Reset`——**InProcess 与 TCP 语义等价**（对抗场景族双介质复跑）。
- InProcess 枢纽构造可注 `Random`（固定种子——丢包测试确定性）。
- raft 场景测试夹具 = `InMemoryRaftStore`（快，缺省）∥ `FsRaftStore`（真持久化语义：重启恢复/
  掉电）——**公共组件的自由消费者，禁依赖产品**。

---

## 6. 反模式（禁止重蹈）

### ❌ 反模式 1：绕形态面直接造 socket
- **症状**：机制/业务代码自己 `TcpClient`/`Socket` 连节点。
- **为什么错**：丢掉介质无关（同构验收）、三形态契约（超时/关联/背压）、故障注入、帧协议
  （双 CRC 防半帧）、可观测——全部要自己重造且必然漂移。
- **正解**：一切收发经 `IProtocolTransport` 三形态；传输实现是 internal 细节。

### ❌ 反模式 2：把 raft 引擎当日志库直接写
- **症状**：绕过 `RaftStateMachine` 直接 append `IRaftStore`。
- **为什么错**：丢掉多数派提交/安全性（term 检查/Figure 8 约束/换届截尾）——raft 的全部价值
  在引擎层，存储只是它的话筒。
- **正解**：写 = `ReplicateAsync`/`ReplicateCommittedAsync`；读业务效果走 apply 管道产物。

### ❌ 反模式 3：机制零件手工构造传输
- **症状**：`new ClusterTransport(...)` 手工直构（public 可达但绕过装配——选项校验/机制挂载全自担）；或手工拼握手参数。
- **正解**：`ClusterBuilder`/`NetClientBuilder` 三段式——选项校验 fail-fast、机制按序挂载、
  中途失败自动清理。

### ❌ 反模式 4：raft apply 载荷跨调用持有
- **症状**：`IStateMachine.ApplyAsync` 里把 `command` 的 `ReadOnlyMemory` 存进字段/队列稍后处理。
- **为什么错**：apply 载荷是存储读取视图，**仅调用期间有效**——存储复用缓冲后读到别处数据。
  （传输 handler 的入站载荷相反——每帧独立缓冲，可安全持有。）
- **正解**：需要保留立即拷贝（`ToArray()`）。

### ❌ 反模式 5：吞掉 `NetIOException` 当成功
- **症状**：catch `IOException` 后继续跑，把"未连快速失败"当成"静默送达"。
- **为什么错**：请求回调的未连是**快速失败**（调用方有应答期待）；静默吞 = 白等超时窗。
- **正解**：`NetIOException` = 不可达，立即处理（重路由/上报）；超时 = 尽力送达失败（重试）。

### ❌ 反模式 6：用注册区外的协议号
- **症状**：机制面硬编码 `0x10`（核心区）当自己的协议号；或两个使用方撞号。
- **正解**：使用方在注册区 `0x60-0xAF` 自管（`ProtocolIds.IsUserRegistrable` 校验）；
  机制内部区经程序集内 internal 挂载口（`ICoreProtocolPort`）——公开口放行即抛。

### ❌ 反模式 7：fire-and-forget 丢弃任务
- **症状**：`_ = transport.SendRequestAsync(...)` 丢弃返回 Task。
- **为什么错**：异常未观测静默吞、无超时治理、ValueTask 池化 token 丢弃不归还（详见根
  AGENTS.md 丢弃纪律）。
- **正解**：await 它；确需后台 = Core `TaskSink.SubmitFast` 受控提交。

---

## 7. 决策树

```
我要收发网络消息？
  → 三形态选型表（§2.1）：丢失可容忍=数据报；一问一答=请求回调；大块/持续=流式。

我要装配一个节点/客户端？
  → ClusterBuilder（成员制集群）/ NetClientBuilder（地址制直连）——绝不手工构造传输。

我要给节点挂自己的协议机制？
  → 实现 INodeMechanism + WithMechanism（零特权挂载）；协议号在注册区 0x60-0xAF 自管。

我要多数派复制的日志？
  → RaftStateMachine + ApplyPipeline + IRaftStore 实现（夹具两实现现成）；
    写=ReplicateAsync（read-your-writes）或 ReplicateCommittedAsync（吞吐档）。

我要 follower 追赶全量？
  → 增量无法接续时自动走快照安装（单源流式 ∥ 多源 swarm——同场景双形态）。

我要多源分发分块内容？
  → SwarmSync（manifest 协商 + K 并行 + 换源重试）——快照 swarm 的协议族复用件。

我要测试网络故障行为？
  → transport.Faults（延迟/分区/丢包/乱序——InProcess 与 TCP 等价复跑）。

我要节点跨重启稳定身份？
  → IIdentitySource 端口（Core.Net 零 IO——TierFsIdentityStore 在产品侧实现）。

我要给网络层加指标？
  → Core ObservabilityHub 的 Net 视图（hub.Net）——不直接调 sink，不绕 Hub。

我要集群流量加密/认证？
  → KeyPair 档（推荐缺省——WithKeyPair + 公钥钉扎表）∥ mTLS（CA/合规——WithMutualTls）；
    防降级 fail-closed：本端配置档与对端不匹配即断连，永不静默降级明文。
```
