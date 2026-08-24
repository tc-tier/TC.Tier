# 缓存、字典与计算工具（ClockCache / ShardLockWeakReference / UnifiedCrc / MicroTimer / Utility / ThrowHelper / KeyComparer）

> 高并发缓存、弱引用字典、CRC、微秒计时、位运算/CAS 辅助、异常抛出的用法。
> 完整积木全景见 [`../COORDINATION.md`](../COORDINATION.md)；原生内存/池见 [`memory.md`](memory.md)；异步原语/队列见 [`async-primitives.md`](async-primitives.md)；
> 实测数字（ClockCache 含 miss 代价与调参）见 [`perf/core-primitives-perf.md`](perf/core-primitives-perf.md)。

---

## 0. 选型决策

| 需求 | 用 | 线程安全 | 一句话 |
|------|----|--------|--------|
| **高并发 LRU 缓存**（`TKey` 值类型 + `TValue` 引用类型） | `ClockCache<TKey,TValue>`（V1） | 是 | CLOCK 近似 LRU，零分配热路径，无全局锁；甜区 = 容量 ≥2× 工作集 |
| **高并发 LRU 缓存**（miss 延迟须任意负载恒定） | `ClockCacheV2<TKey,TValue>` | 是 | 组相联 CLOCK——miss 恒 ≤8 路扫描，无探测链悬崖 |
| **弱引用分片字典**（值被 GC 回收后自动失效） | `ShardLockWeakReference<TKey,TValue>` | 是 | 16 分片独立锁，Value 弱引用 |
| **CRC32C / CRC64**（硬件加速） | `UnifiedCrc` | 静态方法安全 | x86 Sse42 ~1GB/s；支持增量 |
| **微秒级零分配计时** | `MicroTimer` | 是（readonly struct） | 整数无浮点；`active=false` 时 JIT 消除整段 |
| **位运算 / 哈希 / 单调 CAS** | `Utility` | 是（纯静态无状态） | 通用写法：De Bruijn log2 / Knuth 哈希 / PreviousPowerOf2 / 单调 CAS |
| **热路径抛异常** | `ThrowHelper` | 是（纯静态） | `[DoesNotReturn]+[NoInlining]`，隔离 throw 让调用方 JIT 内联 |

---

## 1. `ClockCache<TKey, TValue>` / `ClockCacheV2<TKey, TValue>` —— CLOCK 近似 LRU 缓存（两个版本）

| | V1 `ClockCache`（开放寻址） | V2 `ClockCacheV2`（组相联） |
|---|---|---|
| 结构 | 环形数组 + 线性探测 | sets × 8 ways 固定槽组 + 组内 CLOCK |
| miss | 随负载发散（满载 681ns@1024） | **恒定 ≤8 次读（7ns）** |
| hit / update | **3.7 / 2.9 ns（极致）** | 5.0 / 17.6 ns |
| 驱逐 | 满载 ~785ns（全表扫描） | ~70ns |
| 选型 | 容量 ≥2× 工作集可保证时 | 容量≈工作集、命中率不可控、Core 库默认 |

实测对照见 [`perf/core-primitives-perf.md`](perf/core-primitives-perf.md) §6。

### 用法（两版 API 对齐）

```csharp
using var cache = new ClockCacheV2<int, byte[]>(capacity: 1024, onEvict: (k, v) => v.Dispose());
// V1：new ClockCache<int, byte[]>(capacity: 1024, onEvict: ...)
// ⚠️ capacity 须 2 的幂；TKey : struct, IEquatable；TValue : class
// V2 可选第 3 参 ways（组内路数，2 的幂，默认 8；超过 capacity 自动减半钳制）

// 写
cache.Put(key, value);

// 取（命中则刷新访问位）
if (cache.TryGet(key, out var val)) { /* 用 val */ }

// 删
cache.Remove(key);

// 诊断
var stats = cache.GetStats();   // hits/misses/evictions/hitRate（V2 返回 ClockCacheV2Stats）
```

### 关键 API（两版相同，V2 多 `ways`）
| 成员 | 说明 |
|------|------|
| `ClockCache(int capacity, Action<TKey,TValue>? onEvict = null)` | 构造；`capacity` 须 2 的幂 |
| `ClockCacheV2(int capacity, Action<TKey,TValue>? onEvict = null, int ways = 8)` | 构造；`capacity`/`ways` 须 2 的幂 |
| `Put(TKey, TValue)` | 写（满/组满按 CLOCK 淘汰） |
| `TryGet(TKey, out TValue)` → `bool` | 取（命中刷新访问位） |
| `Remove(TKey)` → `bool` | 删（置 tombstone，后续插入复用） |
| `GetStats()` / `Hits`/`Misses`/`Evictions`/`HitRate`（V2 另有 `Ways`） | 诊断 |
| `Clear()` / `Dispose()` | 清空/释放 |

### ⚠️ 陷阱
- **`capacity` 必须 2 的幂**。
- **`TKey : struct, IEquatable<TKey>` 且 `TValue : class`**。
- **V1 铁律**：容量 ≥ 2× 预期工作集，否则 miss 探测链随负载发散（50%→49ns、90%→462ns、100%→681ns）。
- **V2 组偏斜会提前淘汰**：某组键数 > ways 时未满容量即淘汰（均匀负载实际驻留 ≈ 容量的 85-90%；不变量
  `Count == 插入 - 淘汰 - 移除` 恒成立）。这是组相联的本质（CPU 缓存同理），不是 bug——`capacity` 是
  entry 上限而非精确驻留数。
- 两版 tombstone 语义：V1 保探测链不断；V2 用于 Remove 并发窗口防护（无探测链）。

---

## 2. `ShardLockWeakReference<TKey, TValue>` —— 弱引用分片字典

16 分片独立锁（位运算定位），Value 弱引用——不阻止 GC 回收值，回收后条目失效。

### 用法

```csharp
var dict = new ShardLockWeakReference<string, object>(shardCount: 16);
// ⚠️ shardCount 须 2 的幂

dict.AddOrUpdate(key, value);                  // 加/更新（值弱引用持有）
if (dict.TryGet(key, out var val)) { /* val 可能 null（已被 GC） */ }

dict.Remove(key);
int dead = dict.CleanupDeadReferences();       // 清理已被 GC 回收的死引用，返回清理数
foreach (var v in dict.AllValues) { /* 遍历存活值 */ }
```

### 关键 API
| 成员 | 说明 |
|------|------|
| `ShardLockWeakReference()` / `(int shardCount, IEqualityComparer<TKey>? = null)` | 构造 |
| `AddOrUpdate(TKey, TValue)` | 加/更新 |
| `TryGet(TKey, out TValue?)` → `bool` | 取（值可能已被 GC → null） |
| `Remove(TKey)` → `bool` | 删 |
| `CleanupDeadReferences()` → `int` | 清死引用，返回清理数 |
| `Clear()` / `GetTotalEntryCount()` / `AllValues` | 维护/查询 |

### ⚠️ 陷阱
- **`shardCount` 须 2 的幂**（位运算定位分片）。
- **需定期 `CleanupDeadReferences()`**——否则被 GC 回收的 Value 留下死条目（内存渐涨）。
- **`TryGet` 返回的值可能为 null**（GC 已回收）——调用方须容忍。

---

## 3. `UnifiedCrc` —— CRC32C / CRC64

CRC32C（x86 走 `Sse42.X64.Crc32` 硬件加速 ~1GB/s，ARM 走 `Crc32`，否则软件表）+ CRC64（软件）。支持增量、零拷贝。

### 用法

```csharp
// 一次性
uint crc32 = UnifiedCrc.ComputeCrc32C(buffer);     // 0 初值
ulong crc64 = UnifiedCrc.ComputeCrc64(buffer);

// 增量（跨多段）
uint acc = UnifiedCrc.ComputeCrc32C(initialCrc: 0, span1);
acc = UnifiedCrc.ComputeCrc32C(acc, span2);        // 续算
```

### 关键 API
| 成员 | 说明 |
|------|------|
| `ComputeCrc32C(ReadOnlySpan<byte>)` → `uint` | 一次性（初值 0） |
| `ComputeCrc32C(uint initialCrc, ReadOnlySpan<byte>)` → `uint` | 增量续算 |
| `ComputeCrc64(ReadOnlySpan<byte>)` → `ulong` | CRC64（软件） |

### ⚠️ 陷阱
- 硬件加速依赖平台（无 Sse42/ARM Crc32 时回退软件表）。
- CRC64 实例方法（若有）非线程安全——静态 `ComputeCrc64` 无状态可并发。

---

## 4. `MicroTimer` —— 微秒级零分配计时

`readonly struct`，整数换算无浮点；`IsActive=false` 时 JIT **自动消除整段计时逻辑**。

### 用法

```csharp
var t = MicroTimer.Start(active: true);
/* 被计时的工作 */
long us = t.ElapsedMicros();
long ms = t.ElapsedMillis();
long ticks = t.ElapsedTicks();

// 热路径零分配格式化（避免 ElapsedReadable 的字符串分配）
if (t.TryFormat(stackalloc char[32], out int written)) { /* 用 span */ }

// 热路径关闭：active=false 让 JIT 消除整段
var off = MicroTimer.Start(active: false);
off.ElapsedMicros();   // 编译期消除，零开销
```

### 关键 API
| 成员 | 说明 |
|------|------|
| `MicroTimer.Start(bool active = true)` | 工厂（构造 + 计时起点） |
| `ElapsedMicros()` / `ElapsedMillis()` / `ElapsedTicks()` | 读耗时 |
| `TryFormat(Span<char>, out int)` | 零分配格式化（热路径用） |
| `ElapsedReadable()` → `string` | 可读字符串（**会分配**，非热路径用） |
| `IsActive` | 是否激活 |

### ⚠️ 陷阱
- **`ElapsedReadable()` 分配字符串**——热路径用 `TryFormat(Span<char>)`。
- **关计时用 `Start(active:false)`**——JIT 消除整段，比 `if` 守卫更彻底。

---

## 5. `Utility` —— 位运算 / 哈希 / 单调 CAS

静态通用工具集（位运算、哈希、单调 CAS、容量字符串解析等标准写法）。

> **可见性规则**：无指针的（`ParseSize`/`GetLogBase2`/`IsPowerOfTwo`/`MonotonicUpdate`/`WithCancellationAsync`…）= `public`；带指针/`unsafe` 的（`IsEqual`/`Copy`/`HashBytes`/`XorBytes`）= `internal`（当前上层未完成时不开，待需要时再提升为 `public`）。

### 用法

```csharp
int log2 = Utility.GetLogBase2(1024);              // → 10（public）
long size = Utility.ParseSize("4K");                // → 4096（public）

// 单调 CAS 推进水位（拒回退）
if (Utility.MonotonicUpdate(ref watermark, newValue: 100, out long old))
    /* 推进成功（新>旧） */

// 带指针的方法当前为 internal（Core 内/测试可见，经 InternalsVisibleTo）
// unsafe { ulong h = Utility.XorBytes(src, len); }   // internal，待上层完成再 public
```

### 关键 API
| 成员 | 可见性 | 说明 |
|------|--------|------|
| `GetLogBase2(int)` / `(ulong)` | public | De Bruijn 对数 |
| `ParseSize(string)` / `PrettySize(long)` | public | 容量字符串 ↔ 数值 |
| `MonotonicUpdate(ref long, long, out long)` / `(ref int, ...)` | public | CAS 单调推进（新>旧才写，返回是否推进） |
| `WithCancellationAsync(Task<T>, ct)` | public | 给 Task 加取消支持 |
| `IsEqual` / `Copy` / `HashBytes` / `XorBytes` | **internal** | 带指针/unsafe（待上层完成再 public） |

---

## 6. `ThrowHelper` —— 热路径异常抛出

`namespace TC.Tier.Core.Primitives`（`[DoesNotReturn]+[NoInlining]`），把 throw 隔离出热路径，让调用方法被 JIT 内联。放在 Primitives，经 `global using` 免 using。

### 用法

```csharp
// 直接调（不需 using——global using TC.Tier.Core.Primitives 兜底）
if (offset < 0) ThrowHelper.ThrowArgumentOutOfRange(nameof(offset));
if (_disposed) ThrowHelper.ThrowObjectDisposed(nameof(MyObj));
ThrowHelper.ThrowInvalidOperationException("state invalid");
```

### 关键 API
| 成员 | 说明 |
|------|------|
| `ThrowArgumentOutOfRange(string? paramName = null)` / `(string?, string?)` | 抛 `ArgumentOutOfRangeException` |
| `ThrowObjectDisposed(string objectName)` | 抛 `ObjectDisposedException` |
| `ThrowInvalidOperationException(string message)` | 抛 `InvalidOperationException` |

> `[DoesNotReturn]` 让编译器知道后续不可达，`[NoInlining]` 把抛异常的代码物理隔离——调用方方法体不含 throw，可被内联。

---

## 7. `IKeyComparer<TKey>` / `KeyComparer<TKey>` —— 64 位哈希键比较器

索引类结构（HashIndex / BTree / SkipList）的 key 比较原语。**为什么是 64 位 hash**：32 位 hash 在
大索引下生日碰撞不可忽视；64 位取高位当 tag（熵充分）、低位当 bucket index，两段独立。

### 关键 API（`TKey : unmanaged`）

| 成员 | 说明 |
|------|------|
| `GetHashCode64(TKey)` → `ulong` | **性能命脉**：默认实现 XxHash64 over key 字节（blittable 零装箱，8 字节 key ~3ns） |
| `Equals(TKey, TKey)` | 判等（HashIndex 路径用 hash + 判等） |
| `Compare(TKey, TKey)` | 全序比较（BTree / SkipList 路径用） |

### 用法

```csharp
// 默认实现开箱即用（结构的字节即哈希输入）
var cmp = new KeyComparer<long>();
ulong h = cmp.GetHashCode64(key);

// 有序结构注入（Compare 路径）
class MyIndex : IKeyComparer<long> 注入点 —— 构造收 IKeyComparer<TKey>，默认 KeyComparer<TKey>。
```

### 何时自定义

变长 key 的前缀哈希、特定分布优化（如递增 long 的 avalanche）、复用非默认等价语义——
实现 `IKeyComparer<TKey>` 注入即可，结构与 `SpinRWLock`/`Atomic128` 同级的**可注入原语**。

---

## 8. 决策速查

```
我要高并发 LRU 缓存？
  → ClockCache（TKey 值类型, TValue 引用类型, capacity 2 的幂）。

我要弱引用字典（值被 GC 自动失效）？
  → ShardLockWeakReference（shardCount 2 的幂，定期 CleanupDeadReferences，TryGet 值可能 null）。

我要 CRC？
  → UnifiedCrc.ComputeCrc32C（硬件加速）/ ComputeCrc64（软件）。增量用 initialCrc 续算。

我要微秒计时？
  → MicroTimer.Start。热路径 TryFormat(Span) + active=false 消除；非热路径 ElapsedReadable。

我要位运算 / 容量字符串 / 单调 CAS？
  → Utility（GetLogBase2 / ParseSize·PrettySize / MonotonicUpdate）。

我要热路径抛异常？
  → ThrowHelper.ThrowXxx（[DoesNotReturn]+[NoInlining]，global using 免 using）。
```
