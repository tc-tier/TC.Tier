# 原生内存与对象池（AlignedMemoryManager / NativeArena / PinnedBufferPool / OverflowPool）

> 非托管内存与池化的用法。⚠️ **全部非托管，必须 Dispose**——忘 Dispose 即真泄漏（GC 不管）。
> 这些是存储引擎零分配热路径的基石（O_DIRECT / DMA / 批量缓冲）。完整积木全景见 [`../COORDINATION.md`](../COORDINATION.md)；
> 实测数字（OverflowPool 等）见 [`perf/core-primitives-perf.md`](perf/core-primitives-perf.md)。

---

## 0. 选型决策

| 需求 | 用 | 线程安全 | 一句话 |
|------|----|--------|--------|
| **单块对齐原生内存**（O_DIRECT/DMA/16B CAS 背板） | `AlignedMemoryManager` | 否（单块单线程读写） | `new(size, alignment)`，挂到对象即用，Dispose 释放 |
| **短生命周期 bump 分配**（批量/临时缓冲） | `NativeArena` | 否（单线程，建议 ThreadStatic） | 一次分配大块，线性 bump，`Reset` 复用 |
| **pinned/对齐 buffer 池**（高频 Rent/Return） | `PinnedBufferPool` | 是（全局池多线程安全） | 分桶 + thread-local 栈，热路径零分配 |
| **轻量固定容量对象池**（非内存对象） | `OverflowPool<T>` | 是（多线程安全） | `ConcurrentQueue`，满则 disposer 回收 |

**铁律**：内存类（`AlignedMemoryManager`/`NativeArena`/`PinnedBufferPool.RentAligned` 产物）**非托管，必须 Dispose**——进 `Resources`（见 [`resource-management.md`](resource-management.md)）或用 `using`。

---

## 1. `AlignedMemoryManager` —— 单块对齐原生内存

继承 `MemoryManager<byte>`，适配 .NET 异步 IO 生态（可给 `ReadAsync(Memory<byte>)`）。分配 `NativeMemory.AlignedAlloc`，可选锁定物理内存（防 swap）。

### 用法

```csharp
// 构造：大小 + 对齐（默认 4K）+ 是否清零 + 是否锁定物理内存
using var mem = new AlignedMemoryManager(size: 4096, alignment: AlignmentConst.Alignment4K);

// 取 Span（对外接口用 GetSpan——含 Dispose 校验）
Span<byte> span = mem.GetSpan();
Span<byte> part = mem.GetSpan(offset: 0, length: 64);
ref Header h = ref mem.GetRef<Header>(offset: 0);   // 强类型引用

// 热路径用 Unsafe 通道（零校验，调用方自证 offset/length 合法）
Span<byte> fast = mem.GetSpanUnsafe(0, 64);
ref ulong tag = ref mem.GetRefUnsafe<ulong>(8);

// 直接拿原生指针（unsafe，O_DIRECT pread/pwrite）
unsafe { fixed (byte* p = &mem.GetRef<byte>(0)) { /* pwrite(fd, p, 4096, off) */ } }
// 或：unsafe { byte* p = mem.BytePtr; }
```

### 关键 API

| 成员 | 说明 |
|------|------|
| `AlignedMemoryManager(int size, int alignment = 4K, bool zeroed = false, bool lockPhysicalMemory = false)` | 构造。`alignment` 须正且 2 的幂；`lockPhysicalMemory=true` 时 `alignment` 须 ≥ 系统页大小 |
| `GetSpan()` / `GetSpan(offset)` / `GetSpan(offset, length)` | 含校验的切片（对外接口用） |
| `GetRef<T>(offset)` | 含校验的强类型引用 |
| `GetSpanUnsafe(offset, length)` / `GetRefUnsafe<T>(offset)` | **热路径**零校验通道（调用方自证合法性） |
| `BytePtr` / `Ptr` | 原生指针（unsafe） |
| `Pin()` / `Unpin()` | `MemoryManager` 基础（pinned，Unpin no-op） |
| `IsDisposed` / `IsRented` / `IsMemoryLocked` | 状态查询 |

### ⚠️ 陷阱
- **无 finalizer**——不 Dispose = 真泄漏。务必 `using` 或进 `Resources`。
- **`lockPhysicalMemory=true`**：Linux 需 `CAP_IPC_LOCK` 或调高 `RLIMIT_MEMLOCK`；失败抛 `InvalidOperationException`（带错误码）。
- **池化产物别手动 Dispose 归还**：`PinnedBufferPool.RentAligned` 返回的 `AlignedMemoryManager` 带归属池 ID，归还走 `ReturnAligned`（池 Reset 复用），不是 Dispose。

---

## 2. `NativeArena` —— 线性 bump 分配

一次分配整块原生内存，内部只前进 bump（`Allocate` 推进 `_offset`），`Reset` 归零复用。O(1) 无碎片，适合批量/临时缓冲。

### 用法

```csharp
// 一次分配一块
using var arena = new NativeArena(size: 1 << 20);   // 1MB

// 线性 bump 分配（推进 offset，无法单块释放）
Span<int> nums = arena.Allocate<int>(count: 1024);
Span<byte> buf = arena.AllocateBytes(count: 4096);

// 复用：归零 offset（内存不释放，下次从头分）
arena.Reset();
Console.WriteLine($"used={arena.Used} remaining={arena.Remaining}");
```

### 关键 API
| 成员 | 说明 |
|------|------|
| `NativeArena(int size)` | 构造，分配 size 字节 |
| `Allocate<T>(count)` / `AllocateBytes(count)` | bump 分配；空间不足抛 `InvalidOperationException` |
| `Reset()` | offset 归零（内存复用，不释放） |
| `Pointer` / `Size` / `Used` / `Remaining` / `IsDisposed` | 状态查询 |

### ⚠️ 陷阱
- **只能前进分配**——无单块 `Free`，要回收只能 `Reset` 整块。
- **非线程安全**——多线程并发 `Allocate` 须外部同步。
- **有 finalizer 兜底**（`~NativeArena`），但不保证时效——仍应主动 Dispose。
- `Allocate<T>` 的 `T` 须 `unmanaged`（struct）。

---

## 3. `PinnedBufferPool` —— pinned/对齐 buffer 池

旗舰内存池。两类池合一：pinned `byte[]` + 对齐 `AlignedMemoryManager`。分桶（power-of-2）+ thread-local 栈（热路径无锁、LIFO、零分配）+ 全局栈（跨线程批量搬运）。

### 用法

```csharp
// 构造（maxPerBucket：每桶本地栈软上限）
using var pool = new PinnedBufferPool(maxPerBucket: 64);

// ── pinned byte[] 池 ──
byte[] buf = pool.Rent(size: 4097, zeroMemory: false);   // size 向上取整到 2 的幂（4097→8192）
try { /* 用 buf */ }
finally { pool.Return(buf, zeroBeforeReturn: false); }    // 归还（别 Dispose，归还即可）

// ── 对齐原生内存池 ──
AlignedMemoryManager mem = pool.RentAligned(size: 4096, alignment: AlignmentConst.Alignment4K);
try { Span<byte> s = mem.GetSpan(); /* 用 */ }
finally { pool.ReturnAligned(mem); }                      // 归还（池 Reset 复用，非 Dispose）
```

### 关键 API
| 成员 | 说明 |
|------|------|
| `PinnedBufferPool(int maxPerBucket = 64)` | 构造 |
| `Rent(int size, bool zeroMemory = false)` → `byte[]` | 借 pinned 数组 |
| `Return(byte[]?, bool zeroBeforeReturn = false)` | 还 |
| `RentAligned(int size, int alignment, bool zeroMemory = false)` → `AlignedMemoryManager` | 借对齐原生内存 |
| `ReturnAligned(AlignedMemoryManager?, bool zeroBeforeReturn = false)` | 还（带归属池校验） |
| `CacheHits` / `CacheMisses` | 命中率诊断 |

### ⚠️ 陷阱
- **size 向上取整到 2 的幂**（最多 50% padding）——别指望精确大小。
- **别和 `ArrayPool<byte>.Shared` 混用**——归还非本池的 buffer 会被静默忽略/Dispose。
- **`ReturnAligned` 带池身份校验**：归还非本池产出的 `AlignedMemoryManager` 会被拒——别交叉归还。
- **Dispose 释放所有 buffer**（含全局栈 + 各 thread-local 栈）。

---

## 4. `OverflowPool<T>` —— 轻量固定容量对象池

`ConcurrentQueue<T>` 承载，满则调 disposer 回收。适合非内存对象（会话、上下文）的复用；容量是**软约束**（高并发下瞬时可能超几个，无正确性影响）。

### 用法

```csharp
// 构造：容量 + 满时的回收回调（null = no-op）
using var pool = new OverflowPool<MyContext>(size: 32, disposer: ctx => ctx.Cleanup());

// 借：命中返回 true；池空返回 false（调用方自行 new）
if (pool.TryGet(out MyContext? ctx))
    ctx.Reuse();
else
    ctx = new MyContext();

// 还：未满入池返回 true；满/已释放调 disposer 回收，返回 false
pool.TryAdd(ctx);

// 诊断
var (hits, misses, count, size, overflows) = pool.GetStats();
```

### 关键 API
| 成员 | 说明 |
|------|------|
| `OverflowPool(int size, Action<T>? disposer = null)` | 构造 |
| `TryGet(out T?)` | 借（命中 true / 池空 false） |
| `TryAdd(T)` | 还（入池 true / 满→disposer 回收 false） |
| `GetStats()` | `(hits, misses, count, size, overflows)` |
| `Count` / `Hits` / `Misses` / `Overflows` | 诊断 |

### ⚠️ 陷阱
- **容量软约束**——`Count` 快照与 `Enqueue` 非原子，高并发瞬时可能超 `size` 几个。
- **disposer 默认 no-op**——若对象非托管，必须传回收回调，否则池满丢弃即泄漏。

---

## 5. 决策速查

```
我要单块对齐内存（O_DIRECT / CAS 背板）？
  → new AlignedMemoryManager(size, alignment)。using / Resources。热路径 GetSpanUnsafe。

我要批量临时缓冲（用完整体丢）？
  → new NativeArena(size)。Allocate/AllocateBytes，用完 Reset 复用或 Dispose。

我要高频 Rent/Return 复用 buffer？
  → PinnedBufferPool。Rent/Return（pinned 数组）或 RentAligned/ReturnAligned（对齐原生内存）。
    size 取整 2 幂；别跨池归还；Dispose 释放全部。

我要复用非内存对象（会话/上下文）？
  → OverflowPool<T>(size, disposer)。TryGet/TryAdd；满则 disposer 回收。
```
