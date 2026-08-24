# 版本号协调状态机（EpochProtectedVersionScheme）

> epoch 保护的版本号过渡机制（EPVS）—— 让多线程在**版本号保护的临界区**里安全协作推进版本。
> 解决的问题：结构需要"整体过渡到新版本"（如 index 扩容/checkpoint 分阶段切换），过渡期间不能有读者用旧版本，又不能简单全局锁死。
> 完整积木全景见 [`../COORDINATION.md`](../COORDINATION.md)；epoch 基础设施 `LightEpoch` 见 [`locking-and-epoch.md`](locking-and-epoch.md)。

---

## 0. 它解决什么

经典两难：
- **写过渡**要"没人再用旧版本"才能动数据结构（否则读者看到半成品）。
- 但**不能全局锁**——那读吞吐全没了。

EPVS 的做法：用 `LightEpoch`（RCU 式延迟回收）做"版本保护"+ 一个**状态机**驱动"从旧版本分步过渡到新版本"。读者 `Enter()` 拿到一个**保证不变的版本状态**，在其保护期内该版本不会被改；写方跑状态机，每个中间态等所有旧 epoch 读者退出后再推进，临界区在互斥下执行。读者让出后，版本才真正前进。

> **用途定位**：这是 `LightEpoch`（纯 RCU 回收）之上的**版本协调**层。当前 Core 已就绪、有测试，但**上层引擎尚未接入**（checkpoint/index 扩容等过渡场景待接入）。

---

## 1. 核心类型

| 类型 | 职责 |
|------|------|
| `EpochProtectedVersionScheme`（EPVS） | 主入口。持 `LightEpoch` + 一个 `VersionSchemeState`，提供 `Enter`/`Leave`/`Refresh`/版本推进 |
| `VersionSchemeState` | 8 字节状态（`Phase:byte` 高位 + `Version:long` 低位）。`Phase == VersionSchemeState.Rest(0)` = 静止稳定态 |
| `VersionSchemeStateMachine`（abstract） | 自定义多步过渡的基类：实现 `GetNextStep`/`OnEnteringState`/`AfterEnteringState` |
| `StateMachineExecutionStatus` | 推进结果枚举：`OK`（成功）/ `RETRY`（有活跃状态机，可重试）/ `FAIL`（目标版本已被超过，不可再试） |

---

## 2. 读者协议（Enter / Leave / Refresh）

```csharp
var epoch = new LightEpoch();
var epvs = new EpochProtectedVersionScheme(epoch);

// 进入保护——返回"截至进入时刻"的状态（保证在此保护期内不变）
VersionSchemeState state = epvs.Enter();
try
{
    // 按 state.Version 读数据结构——此期间版本不会前进
    /* ... read ... */

    // 长临界区内，需要"刷新到当前版本"时：
    state = epvs.Refresh();   // 等价于 Leave+Enter 但更快
}
finally
{
    epvs.Leave();   // 必须在同一线程释放（thread-static epoch）
}
```

要点：
- **`Enter` 返回的 state 在保护期内有效**——EPVS 保证你不会处于中间态（`IsIntermediate()` 必为 false），它会自旋等过渡完成。
- **`Leave` 必须同一线程调**（`LightEpoch` 是 thread-static）。
- **`Refresh`**：长循环里周期性刷新（= 让版本能前进 + 重新拿当前 state），比 `Leave`+`Enter` 快。

---

## 3. 版本推进（单步——已验证可用）

最常见：**带一个临界区、推进到下一版本**。两条 API：

| API | 阻塞？ | 用途 |
|-----|--------|------|
| `AdvanceVersionWithCriticalSection(criticalSection, targetVersion?, spin?)` | 阻塞直到完成（`spin:true`）或仅发起（`spin:false`） | 期望"推进完成"的同步调用 |
| `TryAdvanceVersionWithCriticalSection(...)` | 非阻塞，返回 `StateMachineExecutionStatus` | 并发发起、按 `OK/RETRY/FAIL` 自行处理 |

```csharp
long capturedOld = -1, capturedNew = -1;
bool ok = epvs.AdvanceVersionWithCriticalSection(
    (fromVersion, toVersion) =>          // 临界区：互斥执行，旧版本所有读者已退出
    {
        capturedOld = fromVersion;
        capturedNew = toVersion;
        /* 搬数据 / 切指针 */
    },
    spin: true);                          // 自旋等到过渡完成

// ok==true：成功（版本 +1）；ok==false：目标版本 targetVersion 已被超过（FAIL）
```

- **临界区参数**：`(fromVersion, toVersion) => ...`——在互斥、旧读者已退出的条件下执行真正的数据搬迁。
- **`targetVersion`**：默认 `-1` = 推进到"当前+1"；指定正数 = 推进到具体版本。目标 ≤ 当前版本时返回 `FAIL`（不会回退）。
- **`spin`**：`true` = 阻塞等过渡完成；`false` = 仅发起，不阻塞（调用方需自己观察 `CurrentState()`）。

### 验证过的行为（对应 `EpochProtectedVersionSchemeTests`）
- 初始 `Rest@Version=1`；每次 `Advance` 版本 +1；指定 `targetVersion:10` 后落到 `Version=10`。
- 目标版本 ≤ 当前 → 返回 `false`（FAIL）。
- 在保护区内（`Enter` 后）调 `Advance` → 抛 `InvalidOperationException`（安全保护：不能在保护下又驱动过渡）。
- 并发 4 线程 `Enter/Refresh/Leave` 无腐败；并发 `TryAdvance` 至少部分成功。

---

## 4. 自定义多步状态机（已可用）

EPVS 支持自定义多步状态机（如 checkpoint 的 `Prepare → Commit → Rest` 多阶段）：继承 `VersionSchemeStateMachine` 实现 `GetNextStep`/`OnEnteringState`/`AfterEnteringState`，再用 `ExecuteStateMachine`/`TryExecuteStateMachine` 驱动。

### 步骤自动链接（绝不嵌套 bump）

每步过渡 = 「一次 drain + 一次临界区」：`MakeTransition(旧态 → 中间态)` 后，`BumpCurrentEpoch` 的 drain action 在旧 epoch 所有读者退出后执行 `OnEnteringState`（互斥）+ `MakeTransition(中间态 → 新态)` + `AfterEnteringState`。多步机器靠**步骤自动链接**走完全程，但链接点被刻意安排在 bump 之外——`LightEpoch.BumpCurrentEpoch` 的嵌套守卫（`_tBumpDepth`，见 [`locking-and-epoch.md`](locking-and-epoch.md)）禁止 drain action 内二次 bump，自动链接分三种情形、全部**不嵌套**：

- **内联触发**（无并发读者，action 在当前线程的 bump 内执行完毕）：`StepMachineHeavy` 在 `BumpCurrentEpoch` **返回后**检查过渡是否完成，再推进下一步——bump 与 bump 是顺序关系，不在同一调用栈。
- **推迟触发**（action 由其它线程的 `SuspendDrain`/`Resume` 触发，该线程不在任何 bump 栈内）：action 用 `LightEpoch.IsInsideBump()` 判断自身不处于 bump 内，完成后**直接**推进下一步——读者线程顺手接力，机器不依赖外部驱动也能走完。
- **action 在无关 bump 内触发**（罕见）：机器暂停在稳定态，由后续 `Enter`/`Refresh`/`SignalStepAvailable`/spin 循环继续驱动。

### 验证过的行为（对应 `EpochProtectedVersionSchemeCustomMachineTests`）

- 3 步状态机 `Rest@v → Prepare@v → Commit@v → Rest@(v+1)`：三步 `OnEnteringState` 按序触发、最终落点 `Rest@(v+1)`。
- `toVersion:-1`：自动推进到当前版本 +1。
- 4 个并发读者 + 多步机器同时执行：无异常、`Enter` 全程观察不到中间态。
- `TryExecuteStateMachine`（非自旋）+ 读者活跃：机器靠自动链接完成全部三步。

> 历史限制：此前自动链接在 drain 回调内递归推进下一步，形成**嵌套 bump**，触发 `LightEpoch` 嵌套守卫（Debug 抛 `Nested BumpCurrentEpoch detected`）。现已把链接移出 bump 栈，该限制解除。

---

## 5. API 速查

| 需求 | API |
|------|-----|
| 进入版本保护 | `var state = epvs.Enter();`（返回非中间态 state） |
| 释放保护 | `epvs.Leave();`（同线程） |
| 长临界区刷新 | `state = epvs.Refresh();` |
| 单步推进+临界区（阻塞） | `epvs.AdvanceVersionWithCriticalSection((from,to)=>..., spin:true)` |
| 单步推进+临界区（非阻塞） | `epvs.TryAdvanceVersionWithCriticalSection(...)` → `OK/RETRY/FAIL` |
| 查当前状态 | `epvs.CurrentState()` → `VersionSchemeState{Phase, Version, IsIntermediate()}` |
| 自定义多步（checkpoint 式） | 继承 `VersionSchemeStateMachine` + `ExecuteStateMachine`/`TryExecuteStateMachine`（见 §4） |
