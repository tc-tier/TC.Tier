# 资源统一管理（ResourceGroup）

> **全部资源管理必须统一走 `ResourceGroup`**——禁止自建 `_disposables` 列表、禁止手动管释放顺序。
> 它解决两个老大难：**释放顺序**（自动逆序）与**泄漏**（构造期/异常路径漏释放）。
> 完整积木全景见 [`../COORDINATION.md`](../COORDINATION.md)。

---

## 0. 铁律（一句话）

**凡是随实例生命周期需要释放的资源（`IDisposable`/`IAsyncDisposable`），一律 `Resources.Add(...)` 进 `ResourceGroup`，绝不自己管。**

`LifecycleBase.Resources` 已内建一个 `ResourceGroup`——所有 `LifecycleBase` 派生类直接用 `Resources.Add(...)`，Dispose 时由基类**自动逆序**释放。

---

## 1. 为什么强制统一

| 自己管的坑 | `ResourceGroup` 的解法 |
|------------|------------------------|
| **释放顺序错**——先释放被依赖的资源，后释放依赖方 → use-after-dispose / 异常 | **逆序释放**：按注册的反序，天然满足"依赖方先释放" |
| **构造期异常泄漏**——构造到一半抛异常，已 new 的资源没人释放 | 进了 `Resources` 的，异常路径也由组统一回收 |
| **双释放**——外部注入的资源，自己 Dispose 又被外部 Dispose | `Referenced` 所有权：只跟踪诊断、不释放 |
| **散落的 `_disposables` 列表 / `_owner` 字段**——每个类各搞一套，生命周期分散 | 统一进 `Resources`，`LifecycleBase` 编排 |

---

## 2. 用法

### 2.1 注册（`Owned` vs `Referenced`）

```csharp
// Owned（默认）：Dispose 时由组释放。本实例 new 的、拥有所有权的资源。
Resources.Add(_engine);
Resources.Add(metaEngine, "meta");          // 带名（可按名 Get）

// Referenced：外部注入，只跟踪诊断、不释放（防双释放）。
Resources.Add(sharedEpoch, ownership: ResourceOwnership.Referenced);
```

### 2.2 注册时机

- 构造期 `new` 出的资源：构造器里 `Resources.Add(...)`（保证异常路径也能回收）。
- `OnInitializeBegin` 里才创建的资源：在那里 `Resources.Add(...)`。

> ⚠️ 不要在 `OnInitializeComplete`/使用期才注册核心资源——恢复失败路径可能漏释放。

### 2.3 查资源

```csharp
var engine = Resources.Get<IStorageEngine>("meta");   // 按名 + 类型，不按下标
```

---

## 3. 所有权判定（Owned vs Referenced）

| 场景 | 所有权 | 说明 |
|------|--------|------|
| 本实例 `new` 的、用完该由本实例释放 | `Owned`（默认） | Dispose 时组释放 |
| 外部注入、外部管生命周期（共享对象） | `Referenced` | 只跟踪诊断，**不释放**（否则双释放） |
| 不确定 | 倾向 `Referenced` | 双释放会崩；漏释放最多是泄漏（更安全） |

### 嵌套资源组（层级释放）

子组件自有 `ResourceGroup` 时，**子组以 `Owned` 注册到父组**，形成层级释放链——父组释放时触发子组完整释放，保证跨层级顺序正确。

```csharp
// 子组件
public sealed class ChildComponent : IDisposable
{
    private readonly ResourceGroup _resources = new();
    // ... 子组件自己的资源进 _resources
}

// 父组件注册子组件（整个子组作为一个 Owned 资源）
Resources.Add(childComponent);   // Owned——父 Dispose 时触发 child._resources 逆序释放
```

❌ **反模式**：把子组件的资源逐个拆出来注册到父组——破坏组件封装，极易出现顺序错误。

### 异步释放

`ResourceGroup` 完整实现 `IAsyncDisposable`——`DisposeAsync` 按注册逆序执行异步释放，聚合所有异步异常。含异步释放逻辑的资源（等刷盘完成、优雅退出后台线程）优先走 `DisposeAsync`，避免同步 `Dispose` 阻塞等待死锁。

---

## 4. 要求与铁律

- ✅ 资源必须实现 `IDisposable` 或 `IAsyncDisposable`（至少其一）。
- ✅ **全部**资源进 `Resources`——构造器里的、`OnInitializeBegin` 里的，一个都不能漏。
- ✅ 查资源用 `Resources.Get<T>("name")`，不按下标。
- ❌ **绝不**自建 `_disposables` 列表 / `_owner` 字段 / 手写 `Dispose` 释放顺序——全进 `Resources`。
- ❌ **绝不**把外部注入资源当 `Owned`（双释放）——用 `Referenced`。
- ❌ **绝不**绕过 `LifecycleBase.Dispose` 自管释放——核心清理基类已做，子类只 override `DisposeOverride(bool)` 做额外清理（见 [`lifecycle.md`](lifecycle.md)）。

---

## 5. 范式

```csharp
public sealed class MyStore : LifecycleBase<MyHints>
{
    private readonly IStorageEngine _engine;
    private readonly IStorageEngine _metaEngine;

    public MyStore(MyHints hints) : base(hints)
    {
        _engine = /* 自己 new 的 */ ...;
        _metaEngine = /* 自己 new 的 */ ...;
        Resources.Add(_engine);                       // Owned（默认）
        Resources.Add(_metaEngine, "meta");           // Owned + 命名
    }

    protected override void OnInitializeBegin()
    {
        var sharedEpoch = /* 外部注入的共享 epoch */;
        Resources.Add(sharedEpoch, ownership: ResourceOwnership.Referenced);  // 只跟踪不释放
        _engine.Initialize(/* hints */);
    }

    // 不写 Dispose 释放顺序——LifecycleBase + ResourceGroup 自动逆序释放。
    // 只在需要额外清理时 override：
    protected override void DisposeOverride(bool disposing) { /* 非托管额外清理 */ }
}
```
