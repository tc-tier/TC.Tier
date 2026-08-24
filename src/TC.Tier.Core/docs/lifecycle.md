# 生命周期正确拼装（LifecycleBase / RecoveryBase）

> 适用范围：**全部** `LifecycleBase<THints>` 派生类——宿主侧的数据结构基类（元数据 / 日志 / 环形 / Blob / 索引各家族）与 IO 引擎基类。
> 本文档只讲**核心复杂用法**：怎么继承、override 哪些钩子、什么绝对不要做。完整积木全景见 [`../COORDINATION.md`](../COORDINATION.md)。

---

## 0. 生命周期要解决的混乱

一个有生命周期的对象（引擎、数据结构）有四件事：**构造 → Initialize → 恢复 → Dispose**。如果这四件事的职责边界靠"约定"而非"类型"锁死，就会出现：
- 忘调恢复就读写 → 抛异常（footgun）；
- 各结构各自跑偏的启动范式（静态工厂 / 两步强制顺序 / 自动恢复三种混用）；
- 后台恢复 Task 外泄，调用方误 await 阻塞。

`LifecycleBase<THints>` + `RecoveryBase<THints>` 用**一套固定模板**消除这些：Initialize 是同步 void 入口、内部启后台恢复、Task 封装不外露；子类**不改流程，只 override 钩子**。

> 契约接口 `ILifecycle<THints>` / `IRecovery<THints>` 定义在 `TC.Tier.Contracts.Lifecycle`；Core 的 `LifecycleBase`/`RecoveryBase` **实现**它们（依赖倒置：Core→Contracts）。
>
> ★ **`Initialize` 不在 `ILifecycle` 接口面**（2026-08-24 用户裁定：接口面消除，不允许外部经接口直接调）——接口只保留观测/等待（`IsReady`/`RecoveryState`/事件/`WaitForReady*`/`CancelRecovery`）。启动入口由各持有者自己的装配面提供：引擎 = `StorageEngineBuilder.Start/StartAsync` 一步到位；结构层 = 组合器/生成代码经**具体类型**内部调用。`Initialize` 作为 `LifecycleBase` 的类面方法（模板）继续约束所有派生类。

---

## 1. LifecycleBase 的固定模板（前→中→后 三阶段）

`Initialize(hints)` 是 `non-virtual` 模板，子类**绕不过**（`new` 隐藏也无效）。流程：

```
Initialize(hints):
  CAS 幂等闸门（重复调 no-op）
  → OnInitializeBegin()          // 【前】子类：引擎 init + Resources.Add 装配
  → CreateRecovery()             // 工厂：子类返回 IRecovery（或 null=无需恢复）
  → 后台 task:
      await RecoverAsync(hints)  // 【中】恢复（见 §2）
      OnInitializeComplete()     // 【后】仅恢复成功后串行执行
      backgroundWorker.Start()   // worker 在【后】之后才启动
```

三阶段的**并发含义**是关键：
- `Initialize` 同步返回时，**恢复未必完成**——状态可能停在 `NotStarted`/`Recovering`。
- 调用方要"等恢复完"用 `WaitForReadyAsync`（见 §4），**不要**靠"Initialize 返回 = 就绪"假设。

---

## 2. 子类只 override 这些钩子（其余别动）

| 钩子 | 阶段 | 职责 |
|------|------|------|
| `OnInitializeBegin()` | 【前】 | 装配资源、初始化引擎、`Resources.Add(...)` |
| `CreateRecovery()` | 工厂 | `protected virtual`，返回 `new XxxRecovery(this)`（或 `null` = 无需恢复） |
| `OnInitializeComplete()` | 【后】 | 恢复成功后的装配（可安全读恢复产物） |
| `ConfigureBackgroundWorker(worker)` | 装配 | 在 Begin 或 Complete 里调，注册长生命周期 worker |
| `DisposeOverride(bool)` / `DisposeOverrideAsync(bool)` | 销毁 | 额外清理（核心清理基类已做） |
| `EnsureReady()` | 守卫 | 读写入口第一行——Ready 前读写由它抛 |

---

## 3. RecoveryBase 的恢复模板

`RecoveryBase.RecoverAsync` 同样是固定编排，子类**不 override 它**，只 override钩子：

```
RecoverAsync(hints, ct):
  CAS 闸门（Ready→no-op / Recovering→抛"不可重入"）
  → WaitForDependenciesAsync(ct)   // virtual：层间 join（等子引擎就绪），默认空
  → OnRecoveryStart()              // virtual：默认上报 Recovering/0%
  → OnRecoveryCoreAsync(hints, ct) // ★ abstract：唯一必 override，真正恢复算法
  → OnRecoveryComplete()           // virtual：默认上报 Completed/100%
  → MarkReady()
  （异常 → 置 Failed + 回退闸门 + 重抛）
```

子类只 override：
- `OnRecoveryCoreAsync(hints, ct)` —— **唯一必 override**：扫盘 / 读 meta / 回放 / 重建。进度用 `RaiseProgress(percent, detail)`，取消检查用 `ct.ThrowIfCancellationRequested()`。
- `WaitForDependenciesAsync(ct)` —— 需要等子层恢复完才读本层产物的，在此 `await owner._engine.WaitForReadyAsync(ct)`。
- `CancelRecovery()` —— 需要"显式取消清理"（停扫盘、释放扫描资源）的 override；纯 ct 轮询取消可不动。

---

## 3.5 第 n 引擎三段式（多引擎结构统一规范——全结构一套）

结构持有的**每一个**引擎（主引擎 / Managed meta 引擎 / 未来任何引擎）统一三段，禁止走偏：

| 阶段 | 动作 | 禁止 |
|------|------|------|
| **构建在构造** | `StorageEngine.Create(fs, options)` 纯构造零 IO；条件分支**内联**（如 Managed meta 引擎在 `if (Kind==Managed)` 里建）；进 Resources（owned） | ❌ 抽 CreateMetaEngine 式间接层；❌ 构造里调虚方法 |
| **启动在 OnInitializeBegin** | 所有引擎 `Initialize()` **并行非阻塞**启动 | ❌ CreateAndInitialize/WaitForReady 同步等待 |
| **等待在恢复核心** | `WaitForDependenciesAsync` 里逐个 `await engine.WaitForReadyAsync(ct)`（全异步轨 join）→ 之后才 LoadAsync/扫盘 | ❌ 恢复线程里同步 Wait |

meta 策略装配同在构造：`metaPolicyFactory ??= CreateMetaPolicyDefault; MetaPolicy = factory(Kind)`
——命名委托 `MetaPolicyFactory`（kind→policy，Contracts/Meta/MetaPolicyFactory.cs），方法组收口
禁匿名 lambda，永非 null 纯读。成立前提：几何（SectorSize）来自 `_fs.Volume`（FS 静态属性，
构造期可用）；虚方法不进构造（子类字段未初始化）——子类定制唯一通道 = 构造注入工厂。
范式样板：`Structures/Mirror/MirrorBase.cs` 构造；文档详述见 `src/TC.Tier.Runtime/docs/meta.md` §4.2。

## 4. 铁律（踩过的坑——禁止重蹈）

- ❌ **绝不**自己写 `Initialize` / `Dispose`——它们是 `non-virtual` 模板。只 override §2 的钩子。
- ❌ **绝不**在构造器里启动线程/后台循环并把 `this` 暴露（构造未完成竞态）。长生命周期 worker 走 `ConfigureBackgroundWorker`——基类保证它在**恢复完成后**才 Start，Dispose 时按正确顺序 Stop + WaitForExit。（后台循环详见 [`worker-loop.md`](worker-loop.md)）
- ❌ **绝不**自己维护 `_state` / CAS 闸门 / `MarkReady` 调用顺序——全在 `RecoveryBase` 基类。
- ✅ 状态查询一律用 `RecoveryState` / `IsReady`，**不**用"Recovery 非 null"判断是否就绪。
- ✅ 控制流**只认终态**（Completed/Failed/NotStarted），`Recovering` 中间态仅供进度展示——别在中间态做业务分支。
- ⚠️ **`WaitForReady()` 禁止在 UI/ASP.NET 等同步上下文调**（同步阻塞后台 Task = 经典死锁）→ 必须 `WaitForReadyAsync`。`Failed` 时重抛恢复异常。
- ❌ **禁止在子类构造器中注册 `Owned` 资源**——构造未完成时抛异常，`LifecycleBase.Dispose` 不会执行，已注册的非托管资源永久泄漏。所有资源注册放 `OnInitializeBegin`（Initialize 模板有异常路径）。
- ❌ **第 n 引擎统一三段式**——构建在构造（纯 Create 内联）、启动在 OnInitializeBegin（并行非阻塞）、等待在 WaitForDependenciesAsync（异步 join）；禁同步等异步/两段式装配/匿名 lambda（见 §3.5）
- ❌ **恢复依赖链必须是有向无环图（DAG）**——`WaitForDependenciesAsync` 禁止双向依赖（A 等 B 且 B 等 A = 永久阻塞无报错）。
- ✅ **`EnsureReady` 是 volatile 读**（全内存屏障）——保证就绪状态跨线程可见（ARM64 弱内存序安全），热路径开销 ~1ns 可忽略。

---

## 5. 最小正确范式

```csharp
public sealed class MyStore : LifecycleBase<MyHints>
{
    private readonly IStorageEngine _engine;
    private long _recoveredTail;

    public MyStore(MyHints hints) : base(hints)
    {
        _engine = /* ... */;
        Resources.Add(_engine);                    // 进 Resources，自动逆序释放
    }

    protected override void OnInitializeBegin()
    {
        _engine.Initialize(/* hints */);          // 【前】装配
    }

    protected override IRecovery<MyHints>? CreateRecovery() => new MyRecovery(this);

    protected override void OnInitializeComplete()
    {
        // 【后】可安全读 _recoveredTail（恢复产物）
    }

    public long Read(...)
    {
        EnsureReady();                             // 读写第一行守卫
        /* ... */
    }

    private sealed class MyRecovery : RecoveryBase<MyHints>
    {
        private readonly MyStore _owner;
        public MyRecovery(MyStore owner) => _owner = owner;

        protected override async Task OnRecoveryCoreAsync(MyHints hints, CancellationToken ct)
        {
            _owner._recoveredTail = await ScanAsync(ct);   // ★ 唯一必 override
            ct.ThrowIfCancellationRequested();
        }
    }
}
```

调用方：
```csharp
var store = new MyStore(hints);
store.Initialize(hints);            // 同步返回，恢复后台跑
await store.WaitForReadyAsync(ct);  // 等恢复完成（绝不在同步上下文用 WaitForReady）
store.Read(...);                    // Ready，安全
```
