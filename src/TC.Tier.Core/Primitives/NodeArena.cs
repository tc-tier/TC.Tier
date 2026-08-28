using System.Runtime.CompilerServices;

namespace TC.Tier.Core.Primitives;

/// <summary>
/// 节点指针竞技场——变长节点的非托管驻留内存分配原语（上层节点形态的基座，如 SkipList arena 化节点）。
/// <para>★ 分块 CAS-bump：4MB 块内 Interlocked 递增分配（<b>读者侧 admit 也安全</b>——无锁快路径），
///   块满建新块（lock 下双重检查）。块只增不减、<b>指针恒稳</b>（永不重分配搬移）——缓存进
///   上层缓存映射表持有的裸节点指针跨块增长长命有效。</para>
/// <para>★ 无释放单语义：块内存生命周期=arena 生命周期（Dispose 全释放）——索引节点缓存不逐出
///   （节点即数据教义），无需 per-node free。并发 admit 竞争的重复节点=有界字节浪费，非泄漏。</para>
/// <para>★ 单写者+并发读者契约（同索引缓存族）：Alloc 可并发；Dispose 由结构层 Resources 收口
///   （确定性先于读者终结——读者经索引生命周期闸门保证）。</para>
/// </summary>
public sealed unsafe class NodeArena : IDisposable
{
    /// <summary>块大小对数位（22 → 每块 4MB）。</summary>
    public const int ChunkShift = 22;
    /// <summary>单块字节数（4MB——大块摊薄建块开销，块内 CAS bump 无锁分配）。</summary>
    public const int ChunkSize = 1 << ChunkShift;

    private byte*[] _chunks = [ (byte*)System.Runtime.InteropServices.NativeMemory.Alloc(ChunkSize) ];
    private int _chunkCount = 1;
    private long _top;                 // 当前块内 bump 偏移（8 对齐）
    private readonly object _gate = new();
    private int _disposed;

    /// <summary>分配 8 对齐的 <paramref name="size"/> 字节（指针恒稳，无需释放）。</summary>
    public byte* Alloc(int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        int aligned = (size + 7) & ~7;
        if (aligned > ChunkSize)
            return AllocOversized(aligned);

        while (true)
        {
            // ★ CORE-02 发布序（安全发布模式）：先 acquire 读 count（见新 count ⟹ 数组 store 必可见——
            // 写侧数组先发布、count 后发布；acquire 保证后续数组读不提前于 count 读）
            int count = Volatile.Read(ref _chunkCount);
            var chunks = _chunks;
            if (count <= 0 || chunks.Length < count)
                continue;   // 防御：发布窗口（旧数组 + 新 count）——重读
            var chunk = chunks[count - 1];
            long top = Volatile.Read(ref _top);
            long newTop = top + aligned;
            if (newTop <= ChunkSize)
            {
                // ★ CAS bump：竞争输家重试；若期间建了新块（_top 归零）CAS 必败——旧块尾隙浪费有界
                if (Interlocked.CompareExchange(ref _top, newTop, top) == top)
                    return chunk + top;
                continue;
            }
            AddChunk(count);
        }
    }

    private byte* AllocOversized(int aligned)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var newChunks = new byte*[_chunkCount + 1];
            Array.Copy(_chunks, newChunks, _chunkCount);
            var block = (byte*)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)aligned);
            newChunks[_chunkCount] = block;
            // ★ CORE-02：先发布数组、后发布 count（读侧 acquire 见新 count ⟹ 数组必可见——旧序写反
            //    = 读侧旧数组 + 新 count → chunks[count-1] 越界，8 线程实测复现 IndexOutOfRange）
            Volatile.Write(ref _chunks, newChunks);
            Volatile.Write(ref _chunkCount, _chunkCount + 1);
            return block;
        }
    }

    private void AddChunk(int seenCount)
    {
        lock (_gate)
        {
            if (_chunkCount != seenCount) return;   // 双重检查：竞争者已建
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var newChunks = new byte*[_chunkCount + 1];
            Array.Copy(_chunks, newChunks, _chunkCount);
            newChunks[_chunkCount] = (byte*)System.Runtime.InteropServices.NativeMemory.Alloc(ChunkSize);
            // ★ CORE-02：先发布块表、后发布 count（读侧 acquire 序——旧实现 count 先发布 = 越界窗口）
            Volatile.Write(ref _chunks, newChunks);
            Volatile.Write(ref _chunkCount, _chunkCount + 1);
            Volatile.Write(ref _top, 0);   // ★ 后归零 bump（外部 CAS 者以 _top 为准串行化）
        }
    }

    internal int ChunkCount => Volatile.Read(ref _chunkCount);

    /// <summary>已分配字节（当前块 top + 此前整块）——仪器/测试口径。</summary>
    internal long UsedBytes => (long)(ChunkCount - 1) * ChunkSize + Volatile.Read(ref _top);

    /// <summary>释放竞技场——一次性释放全部块（含 oversized 块）的原生内存（幂等，线程安全）。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var chunk in _chunks)
            System.Runtime.InteropServices.NativeMemory.Free(chunk);
        _chunks = [];
        _chunkCount = 0;
        global::System.GC.SuppressFinalize(this);
    }

    /// <summary>终结器兜底释放（正常 Dispose 后经 SuppressFinalize 不再触发）。</summary>
    ~NodeArena() => Dispose();
}
