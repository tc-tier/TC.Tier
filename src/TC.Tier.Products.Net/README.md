# TC.Tier.Products.Net

TC.Tier 的 **raft × TierWAL 官方产品节点**——Core.Net 共识引擎与 Products 存储在产品层的汇合点：`TierRaftNode` 一个装配器成完整节点（TierWAL 持久化 + raft 共识 + 提交应用管道 + 多源快照分发/反熵 + 宿主调度）。

## 能力

- **一站装配**：`TierRaftNodeBuilder` 三段式成节点——传输二选一（注入现成传输 ∥ 内建 TCP 组装），双供给/零供给 fail-fast
- **存储适配**：`TierWalRaftStore`（IRaftStore × TierWAL）——帧格式知识唯一宿主，组提交/截断/快照语义单点翻译
- **多源与反熵**：快照块化发布、多源并行拉取、leader 发起的对端轮转对账（Swarm 装配时启用）
- **宿主调度**：日志增长阈值触发一体快照压缩 + 发布 + 反熵的公共循环；换届自动接线
- **稳定身份**：`TierFsIdentityStore` 身份文件（原子保存 + CRC 校验，损坏 fail-fast）
- **复制档组路由**：`GroupReplicaRouter` 通用面——传输绑定 + 组分发 + 提案转发 + 状态封套四件事单点承载，副本类实现 `IGroupProposalHandler` 即挂载（域号分配表见 [COORDINATION.md](COORDINATION.md) §6）

## 快速开始

```csharp
using TC.Tier.Core.IO;
using TC.Tier.Core.Net.Channels;

var node = await TierRaftNodeBuilder.Create(id, TierFs.New("local:///data/raft"), config, myMachine)
    .WithClusterTransport(
        listen: new IPEndPoint(IPAddress.Any, 7001),
        peers: peerEndpoints)                    // 全体成员（含自己）
    .StartAsync();

await node.Raft.ReplicateAsync(command);         // 复制完成档见 Core.Net raft 语义
```

嵌入式同进程（测试）用 `.WithTransport(hub.Register(id))` 注入传输；既有集群加入用 `.WithJoin(...)`。

## 依赖

- TC.Tier.Core.Net（raft 引擎/传输三形态/装配）
- TC.Tier.Products（TierWAL 存储）
- System.IO.Pipelines

## 文档

- 使用指南：[docs/raft-node.md](docs/raft-node.md)（在线：[docs.mytzz.top](https://docs.mytzz.top/)）
- 共识引擎语义：Core.Net 使用指南 [net.md](../TC.Tier.Core.Net/docs/net.md)
- 组装层红线：[COORDINATION.md](COORDINATION.md)

## 状态

Beta——暂未发布 NuGet 包；磁盘持久化格式存在迭代变更可能，勿用于生产。
