# 原生互操作 facade（DiskNative / FileNative / MemoryNative）

> 平台 syscall 的 P/Invoke 封装——文件 IO、预分配、打洞、刷盘、内存锁定、扇区探测。
> ★ **本层是 [`TC.Tier.Core.IO`](io.md) 的内部实现底座，不对外**——上述能力 Core.IO 已**全部**封口
> （句柄生命周期 / 能力协商 / DIO 对齐基准 / 池化 / 预分配 / 打洞 / 扩展属性 / 耐久 rename·删除，映射表见 §0）。
> 🔒 **`DiskNative`/`FileNative`/`MemoryNative` 已声明 `internal`——编译期封堵**，任何外部程序集直调直接 CS0122；
> 单测/性能测试经 `InternalsVisibleTo` 照常可用。仓内仅两条**过渡** IVT 例外（`TC.Tier.Runtime` 及其
> Benchmarks——自带 Storage/IO 句柄层未迁 Core.IO，迁移完成即删，见 `TC.Tier.Core.csproj` 注释）。
> **任何代码（含 Core 内其它组件）不得直接调本层**：要 IO 用 Core.IO（[`io.md`](io.md)），外部业务再经
> `IStorageEngine`（Contracts）。唯一例外：Core.IO 自身扩新 syscall 时在本层加 P/Invoke。
> 本文档的读者因此只有两种——维护 Core.IO 的人（查 syscall 语义/平台差异）与给本层加新能力的人。
> 完整积木全景见 [`../COORDINATION.md`](../COORDINATION.md)。

---

## 0. 定位

这三个 static class 是**跨平台 syscall facade**（Windows 走 Kernel32/AdvApi32，Linux/macOS 走 LibC），把存储相关的原生能力统一成托管 API。它们：
- 不持有状态（纯 P/Invoke 包装 + 错误码转异常）。
- 处理跨平台差异（如 `FILE_FLAG_NO_BUFFERING` vs Linux `O_DIRECT`）。
- **唯一消费方是 `TC.Tier.Core.IO`**——本层能力与 Core.IO 公开 API 一一对应（外部永远用右列）：

| 本层能力 | Core.IO 对应（用这些） |
|---|---|
| `GetSectorSize` | `IFileSystem.Volume`（SectorSize / AllocationUnit 独立探测） |
| `OpenHandle`（无缓冲 / DIO） | `IFileSystem.Open`（`FileOpenOptions` + 能力协商 + 逐句柄 DIO 探测） |
| `FlushToDisk` / fdatasync | `IFileHandle.Flush()` / `FlushData()` |
| `MoveFileDurably` / `DeleteFileDurably` | `IFileSystem.Move` / `Delete`（耐久语义，能力位 DurableRename） |
| `PreallocateFile` / `PunchHole` / `EnumerateAllocatedRanges` | `IFileHandle.Preallocate()`（或 open 传 `PreallocateSize` 即幂等预分配）/ `PunchHole()` / `EnumerateAllocatedRanges()` |
| `WriteFileMeta` / `ReadFileMeta` / `DeleteFileMeta` | `IFileHandle.WriteExtendedAttribute` / `ReadExtendedAttribute`（不支持介质 no-op / 返回 null；mem 介质字典模拟） |
| `LockMemory` / `UnlockMemory` | `AlignedMemoryManager(lockPhysicalMemory: true)`（见 [`memory.md`](memory.md) §1） |

**铁律**：**禁止直接调本层**——Core 内其它组件与所有外部代码一律经 Core.IO（[`io.md`](io.md)）；外部业务再经
`IStorageEngine`（Contracts，[`../../TC.Tier.Contracts/COORDINATION.md`](../../TC.Tier.Contracts/COORDINATION.md) §5.3）。
给 Core.IO 加新原生能力是本层的唯一改动场景（新 P/Invoke + Core.IO 封口 + 双平台测试，缺一不可）。

---

## 1. `DiskNative` —— 磁盘信息

| 成员 | 说明 |
|------|------|
| `GetSectorSize(string path)` → `uint` | 查路径所在磁盘的扇区大小（DirectIO 对齐基准） |

---

## 2. `FileNative` —— 文件 IO / 空间回收 / 元数据（大头）

### 句柄与打开
| 成员 | 说明 |
|------|------|
| `OpenHandle(path, mode, access, options, share, disableBuffering, enableLinuxDirectIo, logger)` → `SafeFileHandle` | 打开文件句柄；`disableBuffering`（Win `FILE_FLAG_NO_BUFFERING`）/ `enableLinuxDirectIo`（Linux `O_DIRECT`）控制无缓冲 IO |
| `GetFileSize(handle, logger)` → `long` | 文件大小 |
| `GetFileAllocatedDiskSize(handle, logger)` → `long` | 实际分配的磁盘大小（含打洞后的空洞回收） |

### 持久化与刷盘
| 成员 | 说明 |
|------|------|
| `FlushToDisk(handle, logger)` | 刷盘（Win `FlushFileBuffers` / Unix `fsync`） |
| `FlushParentDirectory(path)` | 刷父目录（保证 rename/crash 一致） |
| `MoveFileDurably(source, dest, overwrite)` | 持久化 rename（rename + 刷目录） |
| `DeleteFileDurably(path)` | 持久化删除 |
| `WriteThroughImpliesFlushed` → `bool` | 平台是否「WriteThrough 即已刷盘」（决定 WriteThrough 模式要不要额外 fsync） |

### 预分配与空间回收（★ Compact WA=1.0 的底座）
| 成员 | 说明 |
|------|------|
| `PreallocateFile(handle, size, logger)` → `PreallocateResult` | 预分配（`RealAlloc` 真实分配 / `SparseFallback` 稀疏回退 / `Failed`） |
| `PunchHole(handle, offset, length, logger)` → `PunchResult` | 打洞回收空间（`Punched` 成功 / `ZeroFilled` 平台用填零替代 / `Failed`） |
| `EnumerateAllocatedRanges(handle, logger)` → `List<(long Start, long End)>` | 枚举实际分配的区间（含打洞后的空洞） |
| `ProbeUnbufferedIo(handle, filePath, disableBuffering, enableLinuxDirectIo, logger)` → `UnbufferedIoSupport` | 探测平台无缓冲 IO 支持度 |

### 扩展属性（文件元数据，如 magic 标记）
| 成员 | 说明 |
|------|------|
| `WriteFileMeta(filePath, data, attName, logger)` → `bool` | 写扩展属性（Win 备用数据流 / Linux xattr） |
| `ReadFileMeta(filePath, attName, logger)` → `byte[]?` | 读 |
| `DeleteFileMeta(filePath, attName, logger)` | 删 |
| `ProbeFileMetaSupport(probeFilePath, attName, logger)` → `FileMetaSupport` | 探测支持度 |

### 结果 enum
| enum | 值 | 含义 |
|------|----|------|
| `PreallocateResult` | `RealAlloc` / `SparseFallback` / `Failed` | 预分配结果 |
| `PunchResult` | `Punched` / `ZeroFilled` / `Failed` | 打洞结果 |
| `UnbufferedIoSupport` | `NotRequested` / `Supported` / `BestEffort` / `Ignored` | 无缓冲 IO 支持度 |
| `FileMetaSupport` | `NotProbed` / `Supported` / `Unsupported` | 扩展属性支持度 |

### ⚠️ 注意
- **错误处理**：多数方法失败时抛异常（带 P/Invoke 错误码）；返回 bool/enum 的（`LockMemory`/`Preallocate`/`PunchHole`）失败不抛，调用方查 `Marshal.GetLastPInvokeError()` 或看返回 enum。
- **`PunchHole` 不可回退**——Failed 时调用方须据返回值决定重试区间（见 Contracts `IAsyncOperation.Failed`）。
- **DirectIO 对齐**：`disableBuffering`/`O_DIRECT` 要求 buffer + offset + size 都按扇区对齐（用 `SectorAlignment` + `AlignedMemoryManager`，见 [`memory.md`](memory.md)）。

---

## 3. `MemoryNative` —— 内存锁定

| 成员 | 说明 |
|------|------|
| `LockMemory(void* address, nuint size)` → `bool` | 锁定物理内存（禁 swap）：Win `VirtualLock` / Unix `mlock`。失败返回 false（查 `Marshal.GetLastPInvokeError`；Linux 需 `CAP_IPC_LOCK` 或调高 `RLIMIT_MEMLOCK`） |
| `UnlockMemory(void* address, nuint size)` → `bool` | 解锁 |

> 勿直调——`AlignedMemoryManager(lockPhysicalMemory: true)` 已封装（见 [`memory.md`](memory.md) §1）。

---

## 4. 决策速查（答案全部是 Core.IO——本层仅标注底层对应）

```
我要查扇区大小（DirectIO 对齐基准）？
  → IFileSystem.Volume（SectorSize / AllocationUnit）。          ← DiskNative.GetSectorSize

我要打开无缓冲/DIO 句柄、fsync、持久 rename、耐久删除？
  → IFileSystem.Open（无缓冲/DIO 选项）+ IFileHandle.Flush/FlushData；
    IFileSystem.Move / Delete（耐久语义）。                       ← FileNative.OpenHandle/FlushToDisk/MoveFileDurably

我要预分配 / 打洞回收空间（Compact 的底座）？
  → IFileHandle.Preallocate()（或 open 传 PreallocateSize）/ PunchHole()。
    不可回退，失败按返回语义重试。                                ← FileNative.PreallocateFile/PunchHole

我要锁定物理内存（防 swap）？
  → AlignedMemoryManager(lockPhysicalMemory: true)。             ← MemoryNative.LockMemory

我要写文件扩展属性（magic 标记）？
  → IFileHandle.WriteExtendedAttribute / ReadExtendedAttribute。 ← FileNative.WriteFileMeta/ReadFileMeta
```

> 任何一条都不该以直接调本层结尾——需求在 Core.IO 找不到对应 API 时，正确动作是**给 Core.IO 提能力**
> （本层加 P/Invoke + Core.IO 封口 + 双平台测试），不是绕过 Core.IO 直调。
