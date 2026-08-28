# TC.Tier.Runtime

TC.Tier 的**存储运行时**——存储引擎 + 结构层（Ring / Log / Index / Metadata / Mirror / Snapshot / SortedIndex），基于统一的 16B 逻辑地址空间。依赖 `TC.Tier.Core` + `TC.Tier.Contracts`。

## 能力

- **存储引擎**：Options/Builder 装配（构造 → 启动一步到位）、16B 逻辑地址、零写放大 Compact、全并发区间所有权
- **结构层**：Ring（WiscKey 值分离）/ Log（WAL）/ 索引（Hash/BTree/SkipList）/ Metadata（版本链）/ Mirror（镜像）/ Snapshot（大流）
- **TierFs 四介质**：local（Direct IO）/ memory / virtual（.tier）/ network（S3）

## 快速开始

```csharp
using TC.Tier.Contracts.Storage;
using TC.Tier.Core.IO;
using TC.Tier.Runtime.Storage;

using var vol = TierFs.New("memory:");   // 生产换 local:/// 或 network:///s3 零代码改动
using var engine = new StorageEngineOptions("demo", segmentGrowthLimit: 64 * 1024 * 1024)
    .WithPreallocateFile(false)
    .Builder(vol)
    .Start();

var addr = engine.Append("hello, tier!"u8);
```

## 依赖

- TC.Tier.Core
- TC.Tier.Contracts

## 文档

- 完整文档站：https://docs.mytzz.top/
- 存储引擎指南：https://docs.mytzz.top/docs/src/TC.Tier.Runtime/docs/storage-engine.html
- 结构层总览：https://docs.mytzz.top/docs/src/TC.Tier.Runtime/docs/structures.html
