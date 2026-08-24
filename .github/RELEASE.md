# TC.Tier NuGet 发布流程

## 发布方式

NuGet **Trusted Publishing**（OIDC 零密钥）：nuget.org 策略 `TC.Tier publish` 绑定本仓库的 `publish.yml` 工作流——打 tag 即自动构建、打包、推送，无需 API key。

## 包与版本策略

| 包 | 版本线 | 说明 |
|---|---|---|
| `TC.Tier.Contracts` | 正式（1.0.0+） | 契约层（零依赖），稳定冻结 |
| `TC.Tier.Core` | 正式（1.0.0+） | 内核基础设施，稳定冻结 |
| `TC.Tier.CodeGen` | 正式（1.0.0+） | 源生成器（analyzer 包），稳定冻结 |
| `TC.Tier.Core.IO.S3` | 正式（1.0.0+） | S3 兼容对象存储（SigV4 自写，network:///s3 介质），稳定冻结 |
| `TC.Tier.Runtime` | beta（0.0.1-beta 起） | 运行时（引擎/结构层），渐进演进；依赖自动带 Contracts/Core 正式版 |

## 发布步骤

### 1. 发正式包（Contracts / Core / CodeGen / Core.IO.S3）

```bash
git tag v1.0.0            # 版本号 = tag 去 v 前缀
git push origin v1.0.0
```

`publish.yml` 检测 `v*` tag → 构建 + 测试 → pack 四正式包（版本 1.0.0）→ 推送 nuget.org。

### 2. 发 Runtime beta 包

```bash
git tag runtime-v0.0.1-beta
git push origin runtime-v0.0.1-beta
```

`publish.yml` 检测 `runtime-v*` tag → pack 仅 `TC.Tier.Runtime`（版本 `0.0.1-beta`）→ 推送。

### 3. 验证

- nuget.org 搜索 `TC.Tier` 确认三个/四个包上线
- `dotnet add package TC.Tier.Runtime --version 0.0.1-beta` 冒烟

## 前置条件（已完成）

- [x] nuget.org Trusted Publishing key（`TC.Tier publish`：owner=tc-tier、repo=tc-tier/TC.Tier、workflow=publish.yml、glob=`TC.Tier*`）
- [x] 打包元数据（Directory.Build.props 通用 + 各 csproj Description/Tags）
- [x] 源生成器 analyzer 包配置（TC.Tier.CodeGen → analyzers/dotnet/cs）

## 注意事项

- tag 推送到**公开仓库**（tc-tier/TC.Tier）触发发布——内部仓库的 tag 不触发（workflow 在公开仓库）
- 版本号必须语义化（NuGet 规则）：`1.0.0`、`0.0.1-beta` 合法；`v1.0.0`（带 v）非法
- 发布前跑全量测试（CI 内自动跑，红则 tag 不发布）
