# TC.Tier

> 可快速上手、内核可组合的 .NET 高性能存储运行时。

[English](README.en.md) · 中文

## 项目介绍

TC.Tier 是一套 **纯 C# 从零自研、组合式架构设计的高性能存储运行时内核**。
项目完全基于现代数据结构论文、操作系统存储方法论与分层工程范式实现，无第三方内核黑盒依赖，全程可控、可审计、可 AOT 原生编译。

本项目为个人兴趣工程实现，主打底层存储内核的工程落地与技术验证，非商业产品、非通用开源服务型项目。

## 项目状态（重要）

- ✅ **底层 Runtime 运行时完全完成** —— 内存布局、无锁并发原语、自旋锁、128 位原子操作、分段存储、段表管理、WAL 日志、DirectIO 裸设备读写、多存储后端抽象、跨平台序列化、SourceGenerator 编译期生成体系，已完成本地全维度压测、优化与稳定性验证。
- ⚠️ **整体版本处于 Beta 阶段** —— 上层标准化应用产品（KV、队列、时序等）仍在组装定型中，磁盘持久化二进制格式存在迭代变更可能。请勿直接用于生产环境、核心业务数据、持久化重要数据。
- 所有实现均为工程验证性质，仅经过本地基准测试与压力测试，不保证全场景、全环境生产级稳定性。

## 开源定位与说明

1. 代码完全公开开源，基于 MIT 协议，可自由查阅、学习、参考、二次修改。
2. 项目无任何商业用途、不做推广、不引流、不提供私有服务。
3. 所有设计均来自公开学术论文、开源方法论与标准化工程实践，无封闭私有技术。

## 交流与答疑规则

1. 唯一交流渠道：GitHub [Issues](https://github.com/tc-tier/TC.Tier/issues) / [Discussions](https://github.com/tc-tier/TC.Tier/discussions)。
2. 无微信、无邮箱、无任何私人联系方式，不接受私下咨询。
3. 答疑为自愿、非义务行为，不保证回复时效，部分问题可直接忽略或关闭。
4. 优先讨论源码设计、架构思路、工程实现、技术疑问；入门引导、业务部署、生产适配、定制功能类问题可能不予回复。

任何真诚的问题、建议、批评，都欢迎提出——只是不承诺回复。

## 协议与风险声明

- 本项目基于 MIT 开源协议。
- 所有使用者自行承担使用风险，作者不承担任何数据丢失、故障、兼容、稳定性相关责任。
- Beta 阶段不承诺版本兼容、不承诺格式兼容、不承诺功能稳定。

## 核心能力亮点

- **纯托管 C# + Unsafe 自研内核** —— 零 Native 黑盒依赖
- **SourceGenerator 全编译期序列化** —— 完整 NativeAOT 支持、零反射
- **16B 统一逻辑地址空间** —— 跨段寻址、地址即身份，判等/哈希只比较地址本身
- **可组合存储模型** —— 索引（哈希 / B+树 / 跳表）· Ring · Log 中间层按需组合
- **显式内存布局** —— 字段对齐、跨平台大小端统一处理
- **无锁并发体系** —— 自旋读写锁、分片锁、128 位原子 CAS、无锁队列
- **统一存储抽象** —— 内存 / 文件 DirectIO / 裸设备 / S3 对象存储无缝切换
- **自研段式存储引擎** —— 自动碎片整理、WAL 崩溃恢复、逻辑地址寻址体系
- **全套 Benchmark 基准压测** —— 可复现、可对比、可审计

## 适用场景

- .NET 底层存储内核学习与源码参考
- 高性能、低 GC、无锁架构工程实践研究
- 个人/实验项目嵌入式存储底座
- 自定义中间件、自研组件技术参考

不适用任何生产业务、持久化核心数据场景。等待上层标准产品定型后，将冻结磁盘存储格式，迭代为正式 V1.0 稳定版。生产使用请关注 v1.0 正式版发布，或自行完成完整的故障注入与压测验证后再评估。

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

## 安装

```bash
# 运行时包（当前为 beta，含 Core / Contracts 依赖）
dotnet add package TC.Tier.Runtime --prerelease
```

正式版包（v1.0.x）：`TC.Tier.Contracts`、`TC.Tier.Core`、`TC.Tier.CodeGen`、`TC.Tier.Core.IO.S3`（网络文件系统 S3 实现）。

## 性能

以下为跨平台实测（.NET 8），**供参考**——已在 Windows（i5-12400）与 Linux（AMD 6900HX）双平台验证，实际表现请以你的硬件与负载为准。完整口径与细节见[性能文档](https://docs.mytzz.top/docs/src/TC.Tier.Runtime/docs/perf/storage-engine-perf-baseline.html)。

| 场景 | 结果 |
|---|---|
| 稳态复写 64B（单线程） | 532 ns/op |
| 复写 64B · 4 线程并行 | 约 176 ns/op（约 3.1× 加速比） |
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

- **在线文档站**：[docs.mytzz.top](https://docs.mytzz.top/)——使用文档、性能报告、API 参考与全文搜索
- **代码库**：[github.com/tc-tier/TC.Tier](https://github.com/tc-tier/TC.Tier)——源码、Issues、Discussions

## 贡献

见 [CONTRIBUTING.md](CONTRIBUTING.md)——构建、测试约定、代码规范（编译期强制：禁反射 TCSG030 / 禁 sync-over-async TCSG031）。

## License

MIT —— 详见 [LICENSE](https://github.com/tc-tier/TC.Tier/blob/main/LICENSE)。

核心实现全部自研；算法参考声明与第三方依赖说明见 [THIRD-PARTY-NOTICES](https://github.com/tc-tier/TC.Tier/blob/main/THIRD-PARTY-NOTICES)。
