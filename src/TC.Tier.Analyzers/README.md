# TC.Tier.Analyzers

配置驱动的分层治理与代码纪律分析器包。规则、作用域、豁免全部经 `.editorconfig` 声明——
**零默认诊断**：装包未配置 = 零报告，对消费方零打扰。

> 使用指南（快速上手/规则清单/豁免纪律/反模式）：[docs/tier-analyzers.md](https://docs.mytzz.top/docs/codegen/tier-analyzers.html)

## 安装

```xml
<PackageReference Include="TC.Tier.Analyzers" Version="1.0.*" PrivateAssets="all" />
```

## 规则族与诊断 ID

### 件一：分层依赖（`tier_layer.*`）

| ID | 规则 | 配置键 |
|---|---|---|
| TCSG130 | 禁用引用（元数据引用 / using 命名空间双形态） | `tier_layer.forbidden_reference = A => B1 \| B2 ; …` |
| TCSG131 | 零内部依赖违约 | `tier_layer.zero_internal_refs = X1 ; X2` |
| —（131 依赖） | 内部家族前缀定义 | `tier_layer.internal_assembly_prefix = TC.Traffic.` |
| TCSG132 | 引用白名单越界 | `tier_layer.allowed_reference = X => Y1 \| Y2 ; …` |
| TCSG133 | 命名空间归属越界 | `tier_layer.namespace_prefix = X => P ; …` |
| —（132 依赖） | 框架豁免前缀追加 | `tier_layer.allowed_framework_prefix = Foo \| Bar` |

### 件二：禁用模式（`tier_forbidden.*`）

| ID | 规则 | pack 令牌 |
|---|---|---|
| TCSG134 | 裸线程原语（new Thread / Thread.Sleep / Task.Run / PeriodicTimer / SemaphoreSlim / Mutex / ManualResetEvent(Slim)） | `bare_threads` |
| TCSG135 | 热路径分配纪律（LINQ / 装箱 / string 分配——启发式契约） | `hotpath_discipline` |
| TCSG136 | 禁止运行时反射 | `reflection` |
| TCSG137 | 禁 sync-over-async（`.GetAwaiter().GetResult()` / `.Wait()`） | `sync_over_async` |
| TCSG138 | 禁 fire-and-forget 丢弃（`_ =` 丢弃 Task/ValueTask） | `fire_and_forget` |

```editorconfig
tier_forbidden.pack = reflection | sync_over_async | fire_and_forget | bare_threads | hotpath_discipline
tier_forbidden.scope.hotpath_discipline = members_with([Acme.HotPathAttribute])
tier_forbidden.exempt.bare_threads = Acme.Execution | Acme.Infrastructure.Scheduler
```

## 值编码

- 单键值内 `;` 分隔多条规则；`|` 列举多目标 / pack 令牌；空白容忍。
- 箭头对 `左值 => 右值1 | 右值2`；左值 = 程序集名（精确，或 `前缀*` 通配），不匹配任何程序集名时按
  命名空间前缀解释（仅 using 形态生效——命名空间粒度作用域）。
- **配置非法 fail-fast（TCSG139）**：未知键、坏语法、规则依赖键缺失 = Error，绝不静默忽略。

## 想深入

- 诊断 ID 段位规划见仓库 `docs/design/analyzer-configuration-design.md`（内部文档）。
