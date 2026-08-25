# Contributing to TC.Tier

欢迎参与 TC.Tier 的开发。本文档是公共贡献指南——构建、测试、代码规范、提 PR 的流程。

## 环境要求

- .NET SDK 8.0+（`global.json` 锁定版本）
- Windows / Linux / macOS 均可（DIO 对齐与介质切换在 Windows 上验证最充分）

## 构建

```bash
dotnet build -c Debug          # 全解决方案
dotnet build -c Release        # 发布构建（性能基准必须 Release）
```

## 测试

```bash
dotnet test tests/TC.Tier.Core.Tests/TC.Tier.Core.Tests.csproj
dotnet test tests/TC.Tier.Runtime.Tests/TC.Tier.Runtime.Tests.csproj
```

- **单元测试跑完不应超过 1-2 分钟**；超过 5 分钟视为卡死（wedge），先取证（`dotnet-stack` / `dotnet-dump`）再定位，不要反复重跑碰运气。
- 对抗性测试（故障注入/并发压测）在独立项目 `TC.Tier.Runtime.AdversarialTests`，单独跑，不与单元套件混跑。
- 测试临时目录用 `TC_TEST_TMP` 环境变量重定向到大盘（默认系统盘）。

## 代码规范（编译期强制）

`TC.Tier.CodeGen.Analyzers` 内置代码规范规则（`src/**` 下 Error 级）：

| 规则 | 内容 |
|---|---|
| `TCSG030` | 禁止运行时反射——白盒访问走 `InternalsVisibleTo`，反射破坏 AOT/裁剪/性能 |
| `TCSG031` | 禁止同步强制等待异步（`.GetAwaiter().GetResult()` / `.Wait()`）——同步阻塞后台 Task 会死锁 + 线程池耗尽 |

设计必需的同步等待（Dispose 契约、同步 API 落盘语义）需带理由的 `#pragma warning disable`，不允许裸写。

## 分支模型

- `main`：生产主干，只接收 PR 合并
- `fix/xxx`、`feat/xxx`：开发分支，从 main 创建
- PR 不允许带冲突（先解决再提）

## 提交规范

- 一个模块一个 commit，改动独立可验证
- 提交前必须编译 + 测试通过
- commit message 用 `类型(范围): 描述` 格式（`fix` / `feat` / `refactor` / `docs` / `perf` / `test` / `chore`）

## 文档

- 使用文档与 API 参考统一在独立文档站：https://docs.mytzz.top/（DocFX 自动生成，本地 docs-deploy.sh 部署）

## 许可

MIT License，见 [LICENSE](https://github.com/tc-tier/TC.Tier/blob/main/LICENSE)。
