# TC.Tier.CodeGen.Analyzers

TC.Tier 仓库专属的**编译期纪律分析器**（Roslyn DiagnosticAnalyzer，随编译自动注入：根 Directory.Build.props 全局挂载，标注项目除外）。

> 通用规则族（分层依赖/禁用模式）已迁入可打包的 **TC.Tier.Analyzers**（配置驱动，TCSG130-139）——
> 本项目只保留仓库专属规则；Tier 仓规则经根 `.editorconfig` 的 `tier_layer.*`/`tier_forbidden.*` 键声明。

## 分析器与诊断号

| 诊断 | 分析器 | 规则 |
|---|---|---|
| **TCSG120-122** | `TierFsSpecAnalyzer` | `TierFs.New/Open` 常量实参编译期诊断（scheme 非法/参数未知/参数×介质违规）——与运行时校验构成纵深防御 |

真值表/规则同源性由 Core 契约测试钉死（漂移即红）。

## 使用

随 `TC.Tier.CodeGen` 自动注入（Analyzer 引用），无需手工安装；豁免规则遵循标准 `.editorconfig` 严重级配置。

## 依赖

- 无项目引用（独立 Roslyn 分析器程序集）

## 文档

- 使用指南（三规则触发样例/机制/豁免）：[docs/tierfs-spec.md](docs/tierfs-spec.md)
- 通用纪律族（TCSG130-139）：[../TC.Tier.Analyzers/docs/tier-analyzers.md](../TC.Tier.Analyzers/docs/tier-analyzers.md)
- 代码规范（TCSG136/137/138 等）：[CONTRIBUTING.md](../../CONTRIBUTING.md)
- 在线文档站：https://docs.mytzz.top/
