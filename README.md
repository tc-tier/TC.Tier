# TC.Tier

> 开箱即用、内核可组合的 .NET 高性能存储运行时。

[English](README.en.md) · 中文

TC.Tier 提供两个层次的存储能力：

- **直接可用**：经 `StorageEngine` 即可获得原地复写（KV 语义）、顺序追加（WAL 语义）、Blob 大值分离、镜像/快照等能力；
- **可组合**：基于统一的 16B 逻辑地址空间，组合索引（哈希 / B+树 / 跳表）、Ring、Log 等中间层，构建自定义存储模型。

项目仍在快速演进中：运行时包当前为 beta，API 可能调整。任何问题、建议、甚至批评，都欢迎到 [Issues](https://github.com/tc-tier/TC.Tier/issues) 或 [Discussions](https://github.com/tc-tier/TC.Tier/discussions) 提出。

---

## 核心特性

- **16B 逻辑地址空间** —— 跨段寻址、地址复用无截断；判等/哈希只比较地址本身
- **两种写入模型** —— 模式 A：预分配 + 原地复写（KV 语义）；模式 B：顺序追加（WAL 语义）
- **多索引可插拔** —— 哈希 / B+树 / 跳表按需切换
- **并发友好** —— 读路径无锁；不重叠区间的写入可并行（4 线程实测约 3.1× 扩展）
- **原生 C# 零反射** —— 源生成器替代反射；热点路径使用原生内存，NativeAOT 兼容
- **四类文件系统统一抽象** —— 本地文件系统（`local://`，Direct IO）/ 内存文件系统（`memory:`）/ 虚拟文件系统（`virtual://`，.raw 载体）/ 网络文件系统（`network:///s3`，S3 协议契约）；经 `TierFs` 工厂切换，代码零改动
- **后台碎片整理** —— Compact 整段搬迁在后台执行，不阻塞读写，失败自动续传

---

## 安装

```bash
# 运行时包（当前为 beta，含 Core / Contracts 依赖）
dotnet add package TC.Tier.Runtime --prerelease
```

正式版包（v1.0.x）：`TC.Tier.Contracts`、`TC.Tier.Core`、`TC.Tier.CodeGen`、`TC.Tier.Core.IO.S3`（网络文件系统 S3 实现）。

## 快速上手

```csharp
using TC.Tier.Contracts.Storage;
using TC.Tier.Core.IO;
using TC.Tier.Runtime.Storage;

// ① 选文件系统——换 URI 即切换，代码不变
using var vol = TierFs.New("memory:");

// ② 三段式装配：Options 配置 → Builder 构建 → StartAsync 启动（含恢复，异步优先）
await using var engine = await new StorageEngineOptions("demo").Builder(vol).StartAsync();

// ③ 写入与读回：AppendAsync 顺序追加（WAL 语义），WriteAsync 原地覆写（KV 语义）
ReadOnlyMemory<byte> payload = "hello, tier!"u8;
var addr = await engine.AppendAsync(payload, CancellationToken.None);   // 返回起始逻辑地址
await engine.WriteAsync(addr, payload, CancellationToken.None);         // 原地覆写同一地址
var buf = new byte[payload.Length];
var n = await engine.ReadAsync(addr, buf, CancellationToken.None);      // 读回
```

组合索引 / Ring / Log 构建自定义存储模型的更多示例，见[使用文档](https://docs.mytzz.top/docs/src/TC.Tier.Runtime/docs/storage-engine.html)。

---

## 性能

以下为 Windows 本机实测（.NET 8），**供参考**——实际表现请以你的硬件与负载为准。完整口径与细节见[性能文档](https://docs.mytzz.top/docs/src/TC.Tier.Runtime/docs/perf/storage-engine-perf-baseline.html)。

| 场景 | 结果 |
|---|---|
| 稳态复写 64B（单线程） | 532 ns/op |
| 复写 64B · 4 线程并行 | 约 176 ns/op（约 3.1× 扩展） |
| 大块复写 64KB | 11.5 GB/s |
| 点查（Hash 索引） | 158 ns/次；批量口径 94.7 ns/次（零分配） |
| Log 恢复 | 50 万条约 9 ms |
| Ring 并发批量写（8 写者） | 6.07M op/s（较单写者 3.0×） |

---

## 架构

```
官方产品（规划中）：TierKV / TierWAL / TierBlob / TierQueue / TierTimeSeries
────────────────────────────────────────────────
Storage Runtime（可组合内核）
  Index（Hash/BTree/SkipList）· Ring · Log · Blob（元数据/镜像/快照）
────────────────────────────────────────────────
Storage Engine（Options → Builder → Start/StartAsync 装配，16B 逻辑地址空间）
────────────────────────────────────────────────
文件系统层（local:// / memory: / virtual:// / network:///s3）
```

依赖单向无环：`TC.Tier.CodeGen.Abstractions` → `TC.Tier.Contracts` → `TC.Tier.Core` → `TC.Tier.Runtime` → `TC.Tier.Products`。源生成器（`TC.Tier.CodeGen`）横切——BinaryLayout / 注册桥编译期生成，零运行时反射。

- 引擎使用指南：[storage-engine.md](https://docs.mytzz.top/docs/src/TC.Tier.Runtime/docs/storage-engine.html)
- 结构层总览：[structures.md](https://docs.mytzz.top/docs/src/TC.Tier.Runtime/docs/structures.html)
- 生命周期模型：[lifecycle.md](https://docs.mytzz.top/docs/src/TC.Tier.Core/docs/lifecycle.html)

---

## 文档

使用文档与性能报告在 [docs.mytzz.top](https://docs.mytzz.top/)——在线文档站，含 API 参考与全文搜索。

## 交流与反馈

- **Issues** —— bug 报告与功能请求：[tc-tier/TC.Tier/issues](https://github.com/tc-tier/TC.Tier/issues)
- **Discussions** —— 用法讨论、架构探讨、任何想法：[tc-tier/TC.Tier/discussions](https://github.com/tc-tier/TC.Tier/discussions)

我们仍在快速迭代中，欢迎任何形式的反馈——问题、建议、批评都可以。

## 贡献

见 [CONTRIBUTING.md](CONTRIBUTING.md)——构建、测试约定、代码规范（编译期强制：禁反射 TCSG030 / 禁 sync-over-async TCSG031）。

## License

MIT —— 详见 [LICENSE](https://github.com/tc-tier/TC.Tier/blob/main/LICENSE)。

核心实现全部自研；算法参考声明与第三方依赖说明见 [THIRD-PARTY-NOTICES](https://github.com/tc-tier/TC.Tier/blob/main/THIRD-PARTY-NOTICES)。
