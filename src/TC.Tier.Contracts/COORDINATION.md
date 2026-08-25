# TC.Tier.Contracts 协调文档

> **纯契约层**——所有接口契约 + 数据契约（enum/DTO/带 `[BinaryLayout]` 的布局 struct）。**零 abstract class、零业务实现逻辑**。
> 核心原则：**契约与实现分离 + 依赖倒置**。实现依赖契约（Core→Contracts），不是反过来。
> 配合阅读：`../TC.Tier.Core/COORDINATION.md`（Core 原语：SpinRWLock/FairGate/Atomic128/KeyComparer…）。

---

## 1. 这个项目是什么

- **项目类型**：普通 `net8.0` 类库。
- **依赖**：仅 `TC.Tier.CodeGen.Abstractions`（特性）+ `TC.Tier.CodeGen`（源生成器，Analyzer 引用，为 `[BinaryLayout]` 生成 Codec）。
- **铁律：零 Core 依赖**。Contracts 不引 Core——否则依赖链成环（Core 要实现 Contracts 的 `ILifecycle`/`IRecovery` 实现依赖倒置）。所有类型只依赖 Contracts 自身 + `TC.Tier.CodeGen` 特性。

---

## 2. 子目录 / namespace 映射

| 子目录 | namespace | 内容 |
|--------|-----------|------|
| `Lifecycle/` | `.Lifecycle` | `ILifecycle<>`, `IRecovery<>`, `RecoveryPhase`, `RecoveryState`, `RecoveryProgress`, `EmptyHints` |
| `Common/` | `.Common` | `IAsyncOperation`, `IAsyncOperation{TResult}`, `AsyncOperationStatus` |
| `Storage/` | `.Storage` | `IStorageEngine`, `IStorageInfo`, `ISequentialReader`, `LogicalAddress`, `EngineRecoveryHints`, `IOMode`, `PersistenceMode`, `SnapshotMode`, `ReadDirection`, `CompactStatus`, `CompactResult`, `MagicLocation` |
| `Structures/` | `.Structures` | `IRecordStore<>`, `IRecordScanCursor`, `IStructureScanCursor` |
| `Meta/` | `.Meta` | `IMetaPolicy<,>`, `IMetaLayout<,>`, `IMetaHost`, `IMetaSink`, `MetaPolicyFactory`, `MetaPolicyKind` |
| `Transactions/` | `.Transactions` | `ITransactionLog`, `ITransactionParticipant` |
| `Layout/` | `.Layout` | `RecordFlags`, `RecordMagic`, `Crc32Footer`, `Crc64Footer`（均标 `[BinaryLayout]`，由源生成器产出 `*Codec`） |

namespace 一律 `TC.Tier.Contracts.{子目录}`。消费方在自己 `GlobalUsings.cs` 一次加全 6 个子命名空间即可。

---

## 3. 哪些放 Contracts，哪些不放（边界红线）

**放 Contracts**（纯契约/数据，无行为分支）：
- 所有 `IXxx` 接口（`ILifecycle`/`IStorageEngine`/`IMetaPolicy`/`ITransactionLog`…）。
- enum / DTO（`IOMode`/`PersistenceMode`/`CompactStatus`/`EngineRecoveryHints`…）。
- 带 `[BinaryLayout]` 的布局 struct（`LogicalAddress`/`Crc32Footer`/`RecordFlags`…）——跨层共享的数据契约，Codec 属编译期派生，不算业务逻辑。
- `EmptyHints`（默认 hints 单例——纯常量）。

**不放 Contracts**：
- ❌ **abstract class / 虚基类**——`LifecycleBase`/`RecoveryBase` 是通用实现，留 Core。
- ❌ **业务实现**——`TransactionLog`/`StructureScanCursorBase`/4 个 `MetaPolicy` 实现在 Runtime。
- ❌ **底层原语**——`SpinRWLock`/`FairGate`/`Atomic128`/`KeyComparer` 留 Core/Primitives。

---

## 4. `[BinaryLayout]` 在 Contracts 触发（核对项）

Contracts 的 `Layout/` 与 `Storage/LogicalAddress` 标了 `[BinaryLayout]`，经 `TC.Tier.CodeGen` 产出（`obj/.../generated/TC.Tier.CodeGen/TC.Tier.CodeGen.BinaryLayoutGenerator/`）：`LogicalAddressCodec`、`Crc32FooterCodec`、`Crc64FooterCodec`。

源生成器的**用法与注意事项**见 [`../TC.Tier.CodeGen.Abstractions/COORDINATION.md`](../TC.Tier.CodeGen.Abstractions/COORDINATION.md) §3。

---

## 5. 契约红线详解（使用易错点）

> 这些类型虽在 Contracts 定义，但**所有上层（Core/Runtime/Products）都用**——是跨层全局红线。简单 enum/DTO 不展开，查源码即可。

### 5.1 寻址原语 `LogicalAddress`（★★最易错）
`readonly struct` = SegId(4B) + Extension(4B ABA 防护) + Offset(8B)，标 `[BinaryLayout]`。在 Contracts 定义；地址表/段表实现在 Runtime（`SegmentTable`）。
- ⚠️ **`Empty` ≠ `Invalid`**：`Empty=(0,0,0)` 是**合法的 seg0 起点**，不是哨兵；`Invalid=(-1,-1,-1)` 才是"无值"。判空用 `IsValid`，**禁止 `== Empty`**。
- ⚠️ **Extension 不参与相等/哈希/排序**（ABA 字段刻意排除）——`(SegId,Offset)` 相同即判等。
- ⚠️ **跨段禁止手算**：`Offset` 是段内偏移，跨段地址相减无意义；算距离必须 `IStorageEngine.GetDistance`。

### 5.2 生命周期契约 `ILifecycle` / `IRecovery` / `RecoveryPhase`
- `Initialize` 是 **LifecycleBase 的类面方法**（同步 void，启后台恢复即返回，Task 不外露）——**不在 `ILifecycle` 接口面**（2026-08-24 裁定：接口面消除，启动经装配面一步到位——引擎 = `StorageEngineBuilder.Start/StartAsync`，结构 = 组合器内部经具体类型调用）；Ready 前读写由 `EnsureReady` 守卫抛（`EnsureReady` 由 Core 的 `LifecycleBase` 提供）。
- ⚠️ **`WaitForReady()` 禁止在 UI/ASP.NET 等同步上下文调**（同步阻塞后台 Task = 经典死锁）→ 必须 `WaitForReadyAsync`。`Failed` 时重抛恢复异常。
- `IRecovery`：统一 `RecoverAsync`；`CancelRecovery()` 是 DIM 默认空（纯 ct 轮询取消可不动，需显式停扫盘/释放才 override）。
- `RecoveryPhase`：**控制流只认终态**（Completed/Failed/NotStarted），`Recovering` 中间态仅供进度展示——别在中间态做业务分支。
- 正确实现/继承见 [`../TC.Tier.Core/docs/lifecycle.md`](../TC.Tier.Core/docs/lifecycle.md)（Core 的 `LifecycleBase`/`RecoveryBase`）。

### 5.3 存储引擎契约 `IStorageEngine`（★★三水位最易错）
- ⚠️ **三水位不变量**：`MinAddress ≤ CommittedTail ≤ AllocatedTail`。
  - `AllocatedTail`：含"租借未写"空洞，是 Append 起点——**不是真实已写水位**。
  - `CommittedTail`：真实已写（pwrite 后）。**Read/Scan/Reclaim 的合法上界用 `CommittedTail`，不要用 `AllocatedTail`**。
- ⚠️ **禁止手算地址**：跨段进位/借位走引擎方法（`GetDistance`/`CalculationAddress`），禁止手动对 Offset 加减。
- `SegmentFileName` 用引擎方法取，**禁止自拼路径**（单段 `{device}` / 多段 `{device}.{segId}` 差异由实现保证）。
- `Compact` 同步入口**必须带 timeout**；`IAsyncOperation` 句柄：**仅 Phase 1 可取消**，Phase 2（rename 后）不可取消；`WaitAsync(ct)` 只取消"等待"，取消 Compact 本身用 `Cancel()`。句柄冲突失败（FileIOException.SharingViolation）由引擎自动关句柄 + marker 续传（不重拷贝）。
- `SnapshotMode`：`Consistent`=区间读锁（恢复/备份/Compact 搬迁，数据全程不变）；`DirtyRead`=游标读锁（在线巡检）。**恢复必须 Consistent**。
- `PersistenceMode`：`None`（默认）不保证稳定存储（落盘靠 group commit 点显式 `Flush`）；`WriteThrough` 每写同步刷盘。选错 = 丢数据 或 拖性能。
- `IOMode`：**运行期查询**（非请求项），决定对齐是否强制（`Buffered` 不强制 4K）。

### 5.4 恢复 hints `EngineRecoveryHints`
- 两水位 hint：`null = 不修正`（用扫盘默认）；非 null = 校验后覆盖**且不得小于段表真实水位**。`AllocatedTailHint` 可高于 `CommittedTailHint`（跳历史/预留间隙）。
- `SegmentGrowthLimit ≤ 0` 时 fallback 256MB。
- `EnableSegmentation`：`true`=多段（`{device}.{segId}`）；`false`=单段（`{device}`，写满抛 address space exhausted）。
- `implicit operator long` → `engine.Initialize(N)` 直接生效。

### 5.5 读 / 记录 / 扫描契约
- `ISequentialReader`：越界返回**实际所读字节数**（0=EOF），**不抛**；`Seek` 到已删除段抛 `PartitionInvalidException`（硬错误）。
- `IRecordStore<TKey>`：`TKey` 必须 `unmanaged`（blittable）；**key 单份原则**（只在 record 存一份，HashIndex tag-only 桶不存 key，tag 命中后回 record 判等）；`ReadKeys` **必须聚簇 IO**（按地址排序批量，非逐条回源——契约要求非建议）。
- Scan Cursor（`IRecordScanCursor`/`IStructureScanCursor`）：零拷贝 Span **仅在下一次 `MoveNext` 前有效**，用完即弃。
- 注：索引 key 比较器 `IKeyComparer<TKey>` 在 Core/Primitives（见 [`../TC.Tier.Core/COORDINATION.md`](../TC.Tier.Core/COORDINATION.md)）。

### 5.6 事务 2PC（`ITransactionLog` / `ITransactionParticipant`）
- `ITransactionLog.CommitAsync` 真 2PC：Phase 1 `foreach Prepare`（任一失败→全 Abort）→ 持久化 commit record（原子点）→ Phase 2 `foreach ConfirmCommitted`。**协调者只持久化 commit record**，参与者数据各自 flush。`LoadAndReconcile` 双向裁决（committedSeq=0 全 Abort；正向 ConfirmCommitted / 反向 Abort 悬干）。Register 顺序：**底层先，上层后**。
- `ITransactionParticipant`：**`PrepareAsync` 必须含 `Flush`**（`WriteAsync` 不保证落盘，易漏）；**`Abort` 必须幂等**（恢复时可能多次调）；Abort 语义按 IO 模型分（追加式 ReclaimTail 截断 / Meta 内存回退 / Mirror 尾截断 / Index no-op）。`LastPreparedSeq=-1` 表示从未 Prepare。
- 默认协调者实现 `TransactionLog` 在 Runtime（`TC.Tier.Runtime.Transactions`）。

### 5.7 二进制布局常量（`Layout/`）
- `RecordFlags`：低 8 位跨类型统一（CRC 位 / payload 长度 / meta 模式 / per-entry 标记），高 8 位类型自定义；⚠️ **`FLAG_VALUE_OVERFLOW` 用 bit13(`0x2000`) 刻意避开 `FLAG_FOOTER_MAGIC`(`0x1000`)**——新增标志位不能撞 `0x1000`。
- `RecordMagic`：统一 magic 登记表（`uint32`，落盘 LE，hex dump 读 ASCII 可辨识类型）；新增不能撞值。
- `Crc32Footer`/`Crc64Footer`：CRC 覆盖范围 = **Header + Payload + padding**（padding 由 `Header.PaddingLength` 定）。

### 5.8 Meta 契约（`Meta/`，演进中）
- `IMetaHost`（Embedded，只搬字节不算 CRC）/ `IMetaSink`（External，⚠️ 同步方法**禁 sync-over-async**，同步用 Span / 异步用 Memory）。
- `IMetaLayout`/`IMetaPolicy` 统一布局（Header 纯规范，水位归 Payload）。4 个 `IMetaPolicy` 实现在 Runtime（`TC.Tier.Runtime.Meta`）。

---

## 6. 依赖链位置

```
TC.Tier.CodeGen.Abstractions
        ↑
TC.Tier.Contracts            ← 你在这里（引 CodeGen.Abstractions 特性 + CodeGen 生成器）
        ↑
TC.Tier.Core                  （LifecycleBase/RecoveryBase 实现 ILifecycle/IRecovery——依赖倒置）
        ↑
TC.Tier.Runtime → TC.Tier.Products
```

- 谁引用它：`Core`（依赖倒置）、`Runtime`/`Products`/Tests/Benchmarks（用业务接口 + LogicalAddress 等）。
- 改 Contracts = 动所有上层的契约面。接口签名变更必须全解重新编译。
