# TC.Tier.CodeGen.Abstractions

TC.Tier 源生成器的**公共标注面**——全部 generator 特性 attribute（零实现、零依赖、零生成行为）。消费程序集与生成器共享的类型契约：引用本包即可声明标注，生成逻辑在 `TC.Tier.CodeGen`（Analyzer 形态注入，零运行时反射）。

## 标注总览

| 标注 | 产出 | 典型用法 |
|---|---|---|
| `[KvStore(keyType, valueType)]` | `TierKvOfXxx` 封闭产品类（formatter/装配全自动） | `[assembly: KvStore(typeof(long), typeof(Payload))]` |
| `[RingKey]` | Ring/Hash/BTree/SkipList 封闭薄类四件套 | 自定义 unmanaged key 一行开放 |
| `[BinaryLayout]` | 布局 struct 的声明式 codec（读写/尺寸/校验） | 持久化布局单点（禁手写偏移） |
| `[WireMessage]` / `[WireArray]` | 线协议消息 codec（tag/length 前缀/CRC 面） | Core.Net Wire 帧与载荷 |
| `[TierProtocolExported]` / `[NetworkProtocol(protocol)]` / `[MediumOptions(nature)]` / `[SpecParam]` | TierFs 介质协议注册面（引用即注册，消费方零配置） | S3 等外部协议接入 |
| `[ValidRange]` / `[ValidNonDefault]` / `[ValidHasFlags]` | 布局字段校验声明（生成期进入 codec） | 数据契约防呆 |
| `[ConstantRegistry]` | 常量注册表分区（zones） | 协议号/魔数登记 |

布局变更走 attribute 单点 + 生成器产出——**禁手写偏移/手写 codec**（产品层红线，见各项目 COORDINATION.md）。

## 依赖

- 无（依赖链最底层——`CodeGen.Abstractions` → `Contracts` → `Core` → …）

## 文档

- 生成器实现与模板：[../TC.Tier.CodeGen](../TC.Tier.CodeGen/README.md)
- 在线文档站：https://docs.mytzz.top/
