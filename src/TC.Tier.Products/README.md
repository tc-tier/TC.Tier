# TC.Tier.Products

TC.Tier 的**官方存储产品组合层**——把 Runtime 结构积木组合成开箱即用的数据产品：**TierKv**（本地持久 KV）× **TierQueue**（消息队列）× **TierWAL**（预写日志）。产品层只做装配、语义收口与组合域协调，引擎与结构零重写。

## 能力

- **TierKv**：Ring 数据唯一真源 × 主索引三族（Hash/BTree/SkipList）× 会话（三档读一致性 + 多 key 原子批）× RMW Functions × 检查点快速恢复 × 日志回收 × Watch 变更流 × 范围扫描 × TTL——`[KvStore]` 源生成器一行声明成封闭形态
- **TierQueue**：消费组（fencing/可见性超时/重试退避）× ack 即位点持久 × 死信与回放 × 延迟队列 × 幂等生产 × Session 2PC 恰好一次 × 积压治理
- **TierWAL**：协议中立 WAL（raft/p2p 存储中线）——批/单条追加 × 组提交三维度 × 随机起点重放（定位 O(1)）× 头/尾截断 × 元数据 Opaque 槽 × 镜像快照（增量段存储 + 跨节点导出/导入）
- **TierBlob**：对象存储（内容句柄 = 逻辑地址）× 整对象单发写 + 流式会话（CRC64 逐帧）× 对象表点查/枚举 × 删除墓碑
- **TierTimeSeries**：Ring × BTree 时间序列——一次一序列 × 乱序吸收 × 范围序/最新点查 × Rollup 七算子降采样 × retention 治理 × 水位自恢复

## 快速开始

```csharp
using TC.Tier.Core.IO;
using TC.Tier.Products.Wal;

var fs = TierFs.New("memory:");                 // 生产换 local:/// 零代码改动
await using var wal = await TierWalOptions.Default
    .WithWalName("raft-log")
    .Builder(fs)
    .StartAsync();                               // 构建 + 恢复 + 就绪一步到位

var r = await wal.AppendSingleAsync(new byte[] { 1, 2 }, default);
await wal.CommitAsync(default);                  // 一次 fsync = 一批持久化
Console.WriteLine(r.StartIndex);
```

TierKv / TierQueue 的快速上手见各自使用文档（下方链接）。

## 依赖

- TC.Tier.Runtime（结构积木：Ring/索引/EntryLog/Session）
- TC.Tier.Core（介质原语/执行/可观测）
- TC.Tier.CodeGen（源生成器，编译期横切）

## 文档

- 使用指南：[tierkv.md](https://docs.mytzz.top/docs/products/tierkv.html) · [tierqueue.md](https://docs.mytzz.top/docs/products/tierqueue.html) · [tierseries.md](https://docs.mytzz.top/docs/products/tierseries.html) · [tierblob.md](https://docs.mytzz.top/docs/products/tierblob.html) · [tierwal.md](https://docs.mytzz.top/docs/products/tierwal.html)（在线：[docs.mytzz.top](https://docs.mytzz.top/)）
- 性能基线：[perf/tierkv.md](https://docs.mytzz.top/docs/products/perf/tierkv.html) · [perf/tierwal.md](https://docs.mytzz.top/docs/products/perf/tierwal.html)
- 组合纪律与红线：[COORDINATION.md](https://docs.mytzz.top/docs/coordination/src/TC.Tier.Products/COORDINATION.html)

## 状态

Beta——上层产品组装定型中，持久化二进制格式存在迭代变更可能，暂未发布 NuGet 包（发布面见根 README 安装节）。
