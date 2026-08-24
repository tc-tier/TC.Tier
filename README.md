# TC.Tier

> **.NET 生态首款「开箱即用 + 内核可组合」的高性能存储运行时。**

TC.Tier 既是可直接使用的存储产品（KV / WAL / 队列 / Blob / 时序），也是开放的存储运行时内核——基于统一的 16B 逻辑地址空间，组合索引、Ring、Log、Blob 中间层构建自定义存储模型。

**核心定位**：开箱即用的存储产品 + 可组合的存储内核。大值分离、WAL 组提交、持久队列、流式大对象、版本链镜像、2PC、零写放大回收、四介质（local / memory / virtual / network-S3）均为平台原生能力。

---

## 核心特性

- **16B 全局统一逻辑地址** —— 跨段、可复用、无截断，地址是一等公民（判等/哈希忽略 ABA 字段）
- **零写放大碎片整理** —— RangeCompact 写放大恒为 1.0×，长期运行稳定
- **多索引可插拔** —— 哈希 / B+树 / 跳表按需切换，统一抽象
- **全并发区间所有权** —— 读路径完全无锁，不重叠区间写天然并行
- **原生 C#，绕开 GC** —— 零反射（源生成器替代）、热点路径全 pinned/原生内存，NativeAOT 兼容
- **四介质平权** —— `local:///`（Direct IO）/ `memory:`（纯内存）/ `virtual:///`（.raw 文件）/ `network:///s3`（对象存储），换介质零代码改动

---

## 安装

```bash
dotnet add package TC.Tier.Runtime     # 运行时（含 TC.Tier.Core + TC.Tier.Contracts 依赖）
```

## 快速上手

```csharp
using TC.Tier.Contracts.Storage;
using TC.Tier.Core.IO;
using TC.Tier.Runtime.Storage;

// 介质 = 内存（生产换 local:/// 或 network:///s3 零代码改动）
using var vol = TierFs.New("memory:");

// 三段式装配：Options 配置 → Builder 构建 → Start 启动（含恢复）
using var engine = new StorageEngineOptions("demo", segmentGrowthLimit: 64 * 1024 * 1024)
    .WithPreallocateFile(false)
    .Builder(vol)
    .Start();

// 顺序追加（WAL 语义）
var addr = engine.Append("hello, tier!"u8);

// 随机覆写 / 读取
var written = engine.Write(addr, "hello, tier!"u8);
Span<byte> buf = stackalloc byte[12];
engine.Read(addr, buf);
```

基于结构层组合自定义存储模型（KV / WAL / 队列 / 时序）的示例见 [文档](src/TC.Tier.Runtime/docs/)。

---

## 性能（同机实测，.NET 8）

| 场景 | 结果 |
|---|---|
| 稳态复写 64B | 462 ns/op（4T 并行 208 ns/op，2.4× 扩展） |
| 大块复写 64KB | 11.5 GB/s |
| 点查（Hash 索引） | 158 ns；**批口径 95 ns，全零分配** |
| Log 恢复 | 200K 条 8 ms |
| Ring 并发批量写 | 4.45M op/s（8 写者，批量 3.2×） |
| Compact | 写放大 WA=1.00×（零写放大） |

完整基线见 `src/TC.Tier.Runtime/docs/perf/` 与 `src/TC.Tier.Core/docs/perf/`。

---

## 架构

```
          TierKV / TierWAL / TierBlob / TierQueue / TierTimeSeries   ← 官方产品（组合提供）
          ───────────────────────────────────────────────────
          Storage Runtime（可组合内核）
            Index(Hash/BTree/SkipList)  Ring  Log  Blob(Metadata/Mirror/Snapshot)
          ───────────────────────────────────────────────────
          Storage Engine（Options/Builder 装配，16B 逻辑地址空间）
          ───────────────────────────────────────────────────
          Direct IO（local / memory / virtual / network-S3 四介质）
```

五层依赖单向无环：`TC.Tier.CodeGen.Abstractions` → `TC.Tier.Contracts` → `TC.Tier.Core` → `TC.Tier.Runtime` → `TC.Tier.Products`。源生成器（`TC.Tier.CodeGen`）横切——BinaryLayout/注册桥编译期生成，零运行时反射。

- 引擎使用指南：[storage-engine.md](src/TC.Tier.Runtime/docs/storage-engine.md)
- 结构层总览：[structures.md](src/TC.Tier.Runtime/docs/structures.md)
- 生命周期模型：[lifecycle.md](src/TC.Tier.Core/docs/lifecycle.md)

---

## 文档

- **使用文档**（现状权威）：`src/*/docs/`
- **性能报告**：`src/*/docs/perf/`
- 在线文档站（DocFX，API 参考 + 使用文档）：*即将上线*

## 贡献

见 [CONTRIBUTING.md](CONTRIBUTING.md)——构建、测试约定、代码规范（编译期强制：禁反射 TCSG030 / 禁 sync-over-async TCSG031）。

## License

MIT — 详见 [LICENSE](LICENSE)

生产代码全部自研，算法参考声明与第三方依赖说明见 [THIRD-PARTY-NOTICES](THIRD-PARTY-NOTICES)。
