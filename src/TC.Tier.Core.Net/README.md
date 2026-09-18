# TC.Tier.Core.Net

TC.Tier 的**网络与共识层**——节点身份 + 统一线协议 + 介质无关传输三形态 + 共识/对等机制全家（raft / HyParView / Swarm），三段式装配成节点。**零产品知识**（永不依赖 TC.Tier.Products——编译期依赖锁封堵）。

## 能力

- **线协议（Wire）**：统一帧（16B 帧头双 CRC32C，`[BinaryLayout]` 生成 codec）+ 握手协商（集群标签/安全档/特征位）
- **传输三形态（Channels）**：数据报（尽力送达）× 请求回调（CorrId 关联 + 超时 + 可选 at-least-once）× 流式（可靠有序 + 背压，GB 级 O(单帧)）——`IProtocolTransport` 介质无关消费面
- **传输介质（Transport）**：TCP 长连接（三步握手/双优先写队列/保活）∥ InProcess 枢纽（同构基准）∥ UDP 数据报承载——故障注入面（延迟/分区/丢包/乱序）介质等价
- **raft 共识（Raft）**：选举/复制/提交/成员变更/ReadIndex 线性读；单写者事件循环 + per-peer 复制链 + apply 管道；快照安装（单源流式 ∥ 多源 swarm）
- **P2P 会员（P2P）**：HyParView partial view 维护 + Phi Accrual 故障检测 + gossip 广播
- **多源块同步（Swarm）**：manifest 协商 + K 并行拉块 + 错块拦截 + 反熵对账——快照 swarm 的协议族复用件
- **安全（Security）**：Plaintext ∥ KeyPair（签名+ECDH+AEAD，推荐缺省）∥ MutualTls——防降级 fail-closed
- **装配（Hosting）**：`ClusterBuilder`（集群成员）/ `NetClientBuilder`（直连客户端）→ `StartAsync` 一次成型 `NodeEndpoint`

## 快速开始

```csharp
await using var endpoint = await ClusterBuilder.Create(nodeId)
    .ClusterTag(0x74696572)                          // 错集群握手期拒绝
    .Listen("0.0.0.0", 9400)
    .Peers(peers)                                    // NodeId → 端点
    .WithMechanism(myMechanism)                      // 机制显式 opt-in
    .StartAsync(ct);

await endpoint.SendRequestAsync(target, protocolId, payload,
    new RequestOptions { Timeout = TimeSpan.FromSeconds(2) }, ct);
```

## 依赖

- TC.Tier.Core（执行原语/日志/可观测——不自建）

## 文档

- 使用指南：[docs/net.md](docs/net.md)（在线：[docs.mytzz.top](https://docs.mytzz.top/docs/core/net.html)）
- 性能基线：[docs/perf/loopback-baseline.md](docs/perf/loopback-baseline.md)
- 积木拼装指南与红线：[COORDINATION.md](COORDINATION.md)

## 状态

Beta—— raft 语义与线协议已按场景矩阵验证；生产部署请以产品层（TC.Tier.Products.Net）完整故障注入验证为准。
