using System.Buffers;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
namespace TC.Tier.Core.Collections;

/// <summary>
/// 高性能固定内存池，提供 pinned 数组与对齐原生内存两类池。
/// </summary>
/// <para><b>分桶模型</b>：按 power-of-2 分桶（<c>bucketIndex = log2(size 向上取整)</c>），
/// 桶数组在构造时一次性预分配，查找是纯数组索引（无锁、无字典竞争）。
/// size 会被向上取整到最近的 2 的幂（如 4097→8192），最多 50% padding。
/// 与 <see cref="ArrayPool{T}"/> 同构（多 pinned + 对齐 + 池身份校验）。</para>
/// <para><b>并发模型</b>：每桶持有 thread-local <see cref="Stack{T}"/>（主热路径，无锁 + LIFO + 零分配，
/// 与 <see cref="ArrayPool{T}"/> 的 TLS 设计同构），辅以一个全局 <see cref="ConcurrentStack{T}"/>
/// 作为跨线程溢出/复用的回退。LIFO 保证取到最近归还的 buffer，缓存命中友好。</para>
/// <para><b>GC 表现</b>：thread-local 栈用 <see cref="Stack{T}"/>（数组-backed，Push/Pop 不分配新对象）；
/// 分桶是预分配数组索引，零字典锁竞争。仅全局回退栈在跨线程归还时偶发分配。</para>
public sealed class PinnedBufferPool : IDisposable
{
    /// <summary>
    /// 单个桶：thread-local 本地栈（无锁热路径）+ 全局栈（跨线程批量交互 + Dispose 释放点）。
    /// </summary>
    /// <para><b>批量搬运架构</b>（解决 thread-local 非托管内存泄漏 + 提升本地命中率）：
    /// Rent 本地空时从全局批量搬 <see cref="TransferBatch"/> 个到本地；Return 本地满时批量溢出到全局。
    /// Dispose 遍历全局栈释放所有 buffer；本地栈通过首次访问注册到 <see cref="Local"/>，
    /// Dispose 时一并清空，彻底释放非托管内存。</para>
    private sealed class Bucket<T> : IDisposable where T : class
    {
        // 每线程本地栈：热路径无锁、零分配、LIFO。trackAllValues:false（避免 .Value 慢路径）。
        // valueFactory 内做 AllLocals 注册——这样热路径只需一次 .Value 访问，无需额外 IsValueCreated 检查。
        public readonly ThreadLocal<Stack<T>> Local;
        public ConcurrentBag<Stack<T>> AllLocals { get; } = [];

        public Bucket()
        {
            // 闭包捕获 this，在每线程首次访问时创建栈并注册到 AllLocals
            Local = new ThreadLocal<Stack<T>>(valueFactory: () =>
            {
                var s = new Stack<T>();
                AllLocals.Add(s);
                return s;
            });
        }
        // 全局栈：跨线程批量交互。Dispose 释放点之一。
        public readonly ConcurrentStack<T> Global = new();

        /// <summary>释放 ThreadLocal（清理每线程槽位），由 <see cref="PinnedBufferPool.Dispose"/> 调用。</summary>
        public void Dispose() => Local.Dispose();
    }

    // 批量搬运粒度：本地空时从全局一次搬入；本地满时向全局一次搬出。
    private const int TransferBatch = 8;
    // 本地栈软上限：超过则批量溢出到全局。取 maxPerBucket 与 TransferBatch 的较大值。
    private readonly int _localCapacity;

    // 池实例唯一 ID（供 AlignedMemoryManager.PoolId 归属校验）。0 保留给"非池分配"。
    private readonly int _poolId = Interlocked.Increment(ref _nextPoolId);
    private static int _nextPoolId;

    // ── 分桶数组：按 log2(roundedSize) 索引。roundedSize = 2^index，index 范围 [0, MaxBits) ──
    // 支持的 size 上限 = 2^(MaxBits-1)。32 位足够覆盖到 2GB（实际远超业务所需）。
    private const int MaxBits = 32;
    private readonly Bucket<byte[]>[] _arrayBuckets;
    private readonly Bucket<AlignedMemoryManager>?[] _alignedBuckets;
    private readonly int _maxPerBucket;
    private bool _disposed;

    // 命中/未命中计数：per-thread HitCounter 注册到 per-instance 列表，热路径零分配零共享。
    // 热路径：[ThreadStatic] 缓存本线程对本池的计数器引用 → 直接 ++（无 Interlocked、无锁）。
    // 首次访问：创建 HitCounter 并注册到本池的 _registeredCounters（每线程每池仅一次）。
    // 聚合：CacheHits/Misses 遍历 _registeredCounters（低频，仅诊断/测试）。
    [ThreadStatic]
    private static (PinnedBufferPool? pool, HitCounter? counter) _tCurrent;
    // 本池各线程注册的计数器（聚合用）。ConcurrentBag 线程安全添加，遍历无需额外锁。
    private readonly ConcurrentBag<HitCounter> _registeredCounters = [];

    /// <summary>
    /// 获取池的命中数（租用时从本地栈成功取到 buffer 的次数）。
    /// </summary>
    public long CacheHits
    {
        get
        {
            long sum = 0;
            foreach (var c in _registeredCounters) sum += c.Hits;
            return sum;
        }
    }

    /// <summary>
    /// 获取池的未命中数（租用时本地栈空，需从全局栈或新分配 buffer 的次数）。
    /// </summary>
    public long CacheMisses
    {
        get
        {
            long sum = 0;
            foreach (var c in _registeredCounters) sum += c.Misses;
            return sum;
        }
    }

    /// <summary>每线程命中计数器。</summary>
    private sealed class HitCounter
    {
        public long Hits;
        public long Misses;
    }

    /// <summary>
    /// 构造固定内存池。
    /// </summary>
    /// <param name="maxPerBucket">每个桶的最大容量</param>
    /// <exception cref="ArgumentOutOfRangeException">当 maxPerBucket 为负数或零时抛出</exception>
    public PinnedBufferPool(int maxPerBucket = 64)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPerBucket);
        _maxPerBucket = maxPerBucket;
        // 本地栈容量：足够容纳一次批量搬入，且不超过全桶上限。批量搬出阈值用此值。
        _localCapacity = Math.Max(TransferBatch, Math.Min(maxPerBucket, TransferBatch * 2));
        // 构造时一次性预分配桶数组（无运行期字典创建/锁竞争）
        _arrayBuckets = new Bucket<byte[]>[MaxBits];
        for (int i = 0; i < MaxBits; i++) _arrayBuckets[i] = new Bucket<byte[]>();
        // aligned 桶按需懒创建（对齐维度多，预分配全部浪费；仅命中的 index 才创建）
        _alignedBuckets = new Bucket<AlignedMemoryManager>[MaxBits];
    }

    /// <summary>
    /// 将 size 向上取整到最近的 2 的幂，并返回其 log2（即桶数组索引）。
    /// 例：1→0, 2→1, 3..4→2, 4096→12, 4097..8192→13。
    /// </summary>
    /// <param name="size">要取整的大小</param>
    /// <returns>向上取整到最近的 2 的幂的 log2（桶数组索引）</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SizeToIndex(int size)
    {
        // size 已在调用入口校验 > 0
        int rounded = RoundUpToPowerOf2(size);
        return BitOperations.Log2((uint)rounded);
    }

    /// <summary>向上取整到最近的 2 的幂（size 已是 2 的幂时不变）。</summary>
    /// <param name="size">要取整的大小</param>
    /// <returns>向上取整到最近的 2 的幂的值</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int RoundUpToPowerOf2(int size)
    {
        // 1 的情况特殊：BitOperations.Log2(0) 无定义，但 size>0 保证至少 1
        if (size <= 1) return 1;
        // 已是 2 的幂直接返回
        if ((size & (size - 1)) == 0) return size;
        // 否则取下一个 2 的幂
        return 1 << (32 - BitOperations.LeadingZeroCount((uint)size));
    }

    /// <summary>
    /// 将 size 向上取整到最近的 2 的幂，并返回该值。
    /// </summary>
    /// <param name="size">要取整的大小</param>
    /// <returns>向上取整到最近的 2 的幂的值</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int RoundedSize(int size) => 1 << SizeToIndex(size);

    // ── 命中/未命中计数：[ThreadStatic] 零开销，按池身份校验防多池串扰 ──
    [MethodImpl(MethodImplOptions.NoInlining)]
    private HitCounter RegisterCounter()
    {
        var c = new HitCounter();
        _registeredCounters.Add(c);
        _tCurrent = (this, c);
        return c;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RecordHit()
    {
        var cur = _tCurrent;
        var c = (cur.pool == this ? cur.counter : null) ?? RegisterCounter();
        c.Hits++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RecordMiss()
    {
        var cur = _tCurrent;
        var c = (cur.pool == this ? cur.counter : null) ?? RegisterCounter();
        c.Misses++;
    }

    /// <summary>
    /// 租用 pinned 数组，size 会被向上取整到最近的 2 的幂（如 4097→8192），最多 50% padding。
    /// </summary>
    /// <param name="size">要租用的数组大小</param>
    /// <param name="zeroMemory">是否清零内存</param>
    /// <returns>租用的 pinned 数组</returns>
    /// <exception cref="ArgumentOutOfRangeException">当 size 为负数或零时抛出</exception>
    /// <exception cref="ObjectDisposedException">当池已被释放时抛出</exception>
    public byte[] Rent(int size, bool zeroMemory = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        ObjectDisposedException.ThrowIf(_disposed, this);

        int rounded = RoundedSize(size);
        var bucket = _arrayBuckets[SizeToIndex(size)]; // 纯数组索引，无锁

        if (TryRent(bucket, out var buf))
        {
            RecordHit();
            if (zeroMemory)
                Array.Clear(buf, 0, size);
            return buf;
        }

        RecordMiss();
#if NET5_0_OR_GREATER
        buf = GC.AllocateUninitializedArray<byte>(rounded, pinned: true);
#else
        buf = GC.AllocateArray<byte>(rounded, pinned: true);
#endif
        if (zeroMemory)
            Array.Clear(buf, 0, size);
        return buf;
    }

    /// <summary>
    /// 归还 pinned 数组到池中，非本池分配的 buffer（Length 非 2 的幂或越界）会被忽略。
    /// </summary>
    /// <param name="buffer">要归还的 pinned 数组</param>
    /// <param name="zeroBeforeReturn">是否在归还前清零内存</param>
    public void Return(byte[]? buffer, bool zeroBeforeReturn = false)
    {
        if (buffer is null || _disposed) return;
        // buffer.Length 已是 rounded size（Rent 时按 rounded 分配），直接索引对应桶
        // 非本池分配的 buffer（Length 非 2 的幂或越界）会被忽略——与旧 TryGetValue 行为等价
        int len = buffer.Length;
        if (!TryGetArrayIndex(len, out int idx)) return;
        var bucket = _arrayBuckets[idx];

        if (zeroBeforeReturn)
            Array.Clear(buffer, 0, len);
#if DEBUG
        // 毒化检测：归还后填 0xCC——若调用方归还后继续读写，会读到 0xCC 立即暴露 use-after-return。
        else
            Array.Fill(buffer, (byte)0xCC);
#endif

        ReturnToBucket(bucket, buffer);
    }

    /// <summary>
    /// 租用对齐内存块，size 会被向上取整到最近的 2 的幂（如 4097→8192），最多 50% padding。
    /// </summary>
    /// <param name="size">要租用的内存块大小</param>
    /// <param name="alignment">内存对齐大小</param>
    /// <param name="zeroMemory">是否清零内存</param>
    /// <returns>租用的对齐内存块</returns>
    /// <exception cref="ArgumentOutOfRangeException">当 size 为负数或零时抛出</exception>
    /// <exception cref="ObjectDisposedException">当池已被释放时抛出</exception>
    public AlignedMemoryManager RentAligned(int size, int alignment, bool zeroMemory = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        ObjectDisposedException.ThrowIf(_disposed, this);

        int rounded = RoundedSize(size);
        var bucket = GetOrCreateAlignedBucket(SizeToIndex(size));

        if (TryRent(bucket, out var buf))
        {
            RecordHit();
            buf.PoolId = _poolId; // 归属标记（命中复用时也刷新，防跨池串用）
            buf.ResetForRent(zeroMemory);
            return buf;
        }

        RecordMiss();
        // 用 rounded size 构造，保证 buffer.Size == bucket key（ReturnAligned 一致性）
        var newBuf = new AlignedMemoryManager(rounded, alignment, zeroed: true);
        newBuf.PoolId = _poolId; // 标记归属本池
        newBuf.TryMarkRented(); // 新分配一定成功
        if (zeroMemory)
            newBuf.GetSpanUnsafe(0, newBuf.Size).Clear();
        return newBuf;
    }

    /// <summary>
    /// 归还对齐内存块到池中，非本池分配的 buffer（PoolId 不匹配）会被释放。
    /// </summary>
    /// <param name="buffer">要归还的对齐内存块</param>
    /// <param name="zeroBeforeReturn">是否在归还前清零内存</param>
    public void ReturnAligned(AlignedMemoryManager? buffer, bool zeroBeforeReturn = false)
    {
        if (buffer is null || _disposed || buffer.IsDisposed) return;
        // 池身份校验：非本池分配的 buffer（PoolId 不匹配）直接释放，不污染桶。
        // 防止外部 new 的 AlignedMemoryManager 或其他池的 buffer 误归还导致对齐规格混乱。
        if (buffer.PoolId != _poolId)
        {
            buffer.Dispose();
            return;
        }
        if (!buffer.TryMarkReturned()) return; // 防止重复归还或释放

        // buffer.Size 已是 rounded size（RentAligned 时按 rounded 构造）
        if (!TryGetArrayIndex(buffer.Size, out int idx))
        {
            buffer.Dispose();
            return;
        }
        // aligned 桶可能未创建（此 size 从未 RentAligned 过），非创建式查找
        var bucket = Volatile.Read(ref _alignedBuckets[idx]);
        if (bucket is null)
        {
            buffer.Dispose();
            return;
        }

        if (zeroBeforeReturn)
            buffer.GetSpanUnsafe(0, buffer.Size).Clear();
#if DEBUG
        // 毒化检测：归还后填 0xCC——若调用方归还后继续读写，会读到 0xCC 立即暴露 use-after-return。
        else
            buffer.GetSpanUnsafe(0, buffer.Size).Fill(0xCC);
#endif

        if (!ReturnToBucket(bucket, buffer, onOverflow: static b => b.Dispose()))
            buffer.Dispose();
    }

    /// <summary>aligned 桶按需创建（仅首次 RentAligned 某 index 时）。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private Bucket<AlignedMemoryManager> GetOrCreateAlignedBucket(int idx)
    {
        var existing = Volatile.Read(ref _alignedBuckets[idx]);
        if (existing is not null) return existing;
        // 懒创建：用 Interlocked.CompareExchange 保证只创建一次（无锁）
        var created = new Bucket<AlignedMemoryManager>();
        return Interlocked.CompareExchange(ref _alignedBuckets[idx], created, null) is null
            ? created
            : _alignedBuckets[idx]!;
    }

    /// <summary>
    /// 校验 length 是否是本池产生的合法 rounded size（2 的幂），并返回桶索引。
    /// 用于 Return 路径：非本池分配或非法 length 会被拒绝（与旧字典 TryGetValue 等价）。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryGetArrayIndex(int length, out int index)
    {
        // 必须是 2 的幂（本池只产生这种 length），且在数组范围内
        if (length <= 0 || (length & (length - 1)) != 0)
        {
            index = 0;
            return false;
        }
        index = BitOperations.Log2((uint)length);
        return index < MaxBits;
    }

    // ── 批量搬运 Rent：本地空时从全局批量搬入 TransferBatch 个 ──
    // 热路径：本地非空时纯 Pop（无锁）；本地空才走全局批量搬入（跨线程，但低频）。
    // 注：bucket.Local.Value 在每线程首次访问时由 valueFactory 创建栈并注册到 AllLocals，
    // 此后同线程访问直接返回缓存的栈（trackAllValues:false 的快路径），无需额外注册检查。
    private static bool TryRent<T>(Bucket<T> bucket, out T item) where T : class
    {
        var local = bucket.Local.Value!; // 诊断：直接 Value，绕过 GetLocal
        if (local.Count > 0)
        {
            item = local.Pop();
            return true;
        }
        // 本地空 → 从全局批量搬入（减少跨线程锁竞争次数）
        if (TryBatchTransferFromGlobal(bucket, local, out item))
            return true;
        item = null!;
        return false;
    }

    // 从全局批量搬入：一次取最多 TransferBatch 个到本地，返回第一个给调用方。
    private static bool TryBatchTransferFromGlobal<T>(Bucket<T> bucket, Stack<T> local, out T item) where T : class
    {
        if (!bucket.Global.TryPop(out var first)) { item = null!; return false; }
        // 继续搬入剩余的（填满本地到 TransferBatch），减少后续 Rent 撞全局的次数
        for (int i = 1; i < TransferBatch && bucket.Global.TryPop(out var extra); i++)
            local.Push(extra);
        item = first;
        return true;
    }

    // ── 批量搬运 Return：本地满时向全局批量溢出 TransferBatch 个 ──
    /// <summary>归还入桶（onOverflow=null 便捷重载——桶满静默拒绝）。</summary>
    /// <returns><c>true</c> 表示已入桶；<c>false</c> 表示桶已满，调用方需自行处理（如 Dispose）。</returns>
    private bool ReturnToBucket<T>(Bucket<T> bucket, T item) where T : class
        => ReturnToBucket(bucket, item, onOverflow: null);

    private bool ReturnToBucket<T>(Bucket<T> bucket, T item, Action<T>? onOverflow) where T : class
    {
        var local = bucket.Local.Value!; // 诊断：直接 Value，绕过 GetLocal
        // 限流：本地+全局合计达上限 → 拒绝（本地 Count 廉价，Global.Count 近似）
        if (local.Count + bucket.Global.Count >= _maxPerBucket)
        {
            onOverflow?.Invoke(item);
            return false;
        }

        // 本地未满 → 直接入本地（LIFO、无锁）
        if (local.Count < _localCapacity)
        {
            local.Push(item);
            return true;
        }

        // 本地满 → 批量溢出到全局（腾出本地空间），再把本次 item 入本地
        BatchTransferToGlobal(bucket, local);
        local.Push(item);
        return true;
    }

    // 批量溢出：把本地栈顶 TransferBatch 个搬到全局栈，供其他线程复用。
    private static void BatchTransferToGlobal<T>(Bucket<T> bucket, Stack<T> local) where T : class
    {
        int n = Math.Min(TransferBatch, local.Count);
        // 逐个 Push 到全局栈（n 很小，最多 TransferBatch=8；ConcurrentStack.Push 内部有锁但批量足够小）
        for (int i = 0; i < n; i++)
            bucket.Global.Push(local.Pop());
    }

    /// <summary>
    /// 预分配 aligned 内存块到池中，size 会被向上取整到最近的 2 的幂（如 4097→8192），最多 50% padding。
    /// </summary>
    /// <param name="size">要预分配的内存块大小</param>
    /// <param name="alignment">内存对齐大小</param>
    /// <param name="count">要预分配的内存块数量</param>
    /// <param name="zeroMemory">是否清零内存</param>
    /// <exception cref="ObjectDisposedException">当池已被释放时抛出</exception>
    /// <exception cref="ArgumentOutOfRangeException">当 count 为负数或零时抛出</exception>
    public void PreAllocateAligned(int size, int alignment, int count, bool zeroMemory = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        var rounded = RoundedSize(size);
        var bucket = GetOrCreateAlignedBucket(SizeToIndex(size));

        for (var i = 0; i < count; i++)
        {
            if (bucket.Global.Count >= _maxPerBucket) break;
            // 用 rounded size 构造，保持与 RentAligned 一致
            var buf = new AlignedMemoryManager(rounded, alignment, zeroed: zeroMemory);
            bucket.Global.Push(buf);
        }
    }

    /// <summary>计算单个桶的缓冲区总数（Global.Count + AllLocals 各栈 Count）。低频诊断用。</summary>
    private static int CountBucket<T>(Bucket<T> bucket) where T : class
    {
        int total = bucket.Global.Count;
        foreach (var local in bucket.AllLocals)
            total += local.Count;
        return total;
    }

    /// <summary>
    /// 获取池的统计信息：命中数、未命中数、pinned 数组总数、aligned 内存块总数。
    /// </summary>
    /// <returns>一个元组，包含命中数、未命中数、pinned 数组总数、aligned 内存块总数</returns>
    public (long hits, long misses, int pooledArrayCount, int pooledAlignedCount) GetStats()
    {
        long hits = CacheHits, misses = CacheMisses;
        int arrayCount = 0, alignedCount = 0;
        foreach (var b in _arrayBuckets)
            arrayCount += CountBucket(b);
        foreach (var b in _alignedBuckets)
            if (b is not null) alignedCount += CountBucket(b);
        return (hits, misses, arrayCount, alignedCount);
    }

    /// <summary>
    /// 清空池中所有 pinned 数组和 aligned 内存块，释放非托管内存。
    /// </summary>
    /// <exception cref="ObjectDisposedException">当池已被释放时抛出</exception>
    public void Clear()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var b in _arrayBuckets)
            ClearBucket(b, disposeItem: false);
        foreach (var b in _alignedBuckets)
            if (b is not null) ClearBucket(b, disposeItem: true);
    }

    /// <summary>
    /// 裁剪池中所有 pinned 数组和 aligned 内存块，使每个桶的总数不超过指定的 targetCount。
    /// </summary>
    /// <param name="targetCount">每个桶的目标最大数量，如果为 null，则使用默认的 _maxPerBucket</param>
    /// <exception cref="ObjectDisposedException">当池已被释放时抛出</exception>
    public void TrimExcess(int? targetCount = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var limit = targetCount ?? _maxPerBucket;
        foreach (var b in _arrayBuckets)
            TrimBucket(b, limit, disposeItem: false);
        foreach (var b in _alignedBuckets)
            if (b is not null) TrimBucket(b, limit, disposeItem: true);
    }

    /// <summary>
    /// 释放池：清空全部桶（全局栈 + 所有线程本地栈）释放非托管内存，并释放桶的 ThreadLocal（幂等，线程安全）。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        lock (_arrayBuckets)
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var b in _arrayBuckets)
            {
                ClearBucket(b, disposeItem: false);
                b.Dispose();
            }
            foreach (var b in _alignedBuckets)
            {
                if (b is not null)
                {
                    ClearBucket(b, disposeItem: true);
                    b.Dispose();
                }
            }
        }
    }

    // ── 桶清空/裁剪：遍历全局栈 + 所有本地栈（AllLocals），彻底释放非托管内存 ──

    private static void ClearBucket<T>(Bucket<T> bucket, bool disposeItem) where T : class
    {
        // 全局栈
        while (bucket.Global.TryPop(out var item))
        {
            if (disposeItem && item is IDisposable d) d.Dispose();
        }
        // 所有已注册的本地栈（每线程首次访问时注册到 AllLocals）——解决 thread-local 非托管内存泄漏
        foreach (var local in bucket.AllLocals)
        {
            while (local.Count > 0)
            {
                var item = local.Pop();
                if (disposeItem && item is IDisposable d) d.Dispose();
            }
        }
    }

    private static void TrimBucket<T>(Bucket<T> bucket, int limit, bool disposeItem) where T : class
    {
        // 先裁全局栈
        while (bucket.Global.Count > limit && bucket.Global.TryPop(out var item))
        {
            if (disposeItem && item is IDisposable d) d.Dispose();
        }
        // 再裁各本地栈到 limit（近似，跨线程读取 Count）
        foreach (var local in bucket.AllLocals)
        {
            while (local.Count > limit && local.Count > 0)
            {
                var item = local.Pop();
                if (disposeItem && item is IDisposable d) d.Dispose();
            }
        }
    }
}
