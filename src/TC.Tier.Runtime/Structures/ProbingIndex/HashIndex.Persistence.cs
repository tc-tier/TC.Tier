using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TC.Tier.Core.NativeInterop;
using NativeInt128 = TC.Tier.Core.NativeInterop.Int128;

namespace TC.Tier.Runtime.Structures.ProbingIndex;

/// <summary>
/// HashIndex 主存储格式布局 partial——机制归基类（<see cref="ProbingIndexBase{TKey}.TryDump"/> 编排/
/// 版本链/后台循环/帧走链），本件只实现格式：几何块 + 桶区/溢出池 fuzzy 逐槽拷贝 + 帧物化。
/// <para>★ 帧体布局：[几何 32B][桶区 size×128][溢出池 ofbCap×128]——几何写头时已知（帧长可推导）。</para>
/// <para>★ fuzzy 一致性（FASTERKV 同构）：逐槽 128bit 原子读拷贝（槽=LogicalAddress 16B 原子单元，
///   零撕裂）+ 跳 Tentative 只收 Occupied + 换代 stale-but-valid（dump 期间 GrowIndex 照常）。
///   正确性靠组合一致性底座：dump 表覆盖 [?, W] 完整折叠；> W 混入条目靠重放 (W, End] 幂等收敛
///   或恢复后 ring 裁决惰性失效（TryGetKey 读不到即 miss）。</para>
/// </summary>
public partial class HashIndex<TKey> where TKey : unmanaged, IEquatable<TKey>
{
    private const int PersistGeometrySize = ProbingIndexFormat.GeometrySize;   // 32B
    private const int PersistBucketSize = 128;                                 // [StructLayout(Size=128)] HashBucket

    // ════════════════════════════════════════════════════════════
    // === 子类钩子实现（格式布局）===
    // ════════════════════════════════════════════════════════════

    /// <summary>体长 = 几何 32B + 桶区 size×128B + 溢出池 ofbCap×128B（头 BodyLength 字段——写头时先知，帧长可推导）。</summary>
    /// <returns>体字节数。</returns>
    protected override long ComputeBodyLength()
    {
        var table = _table;
        return PersistGeometrySize + table.Size * PersistBucketSize
               + table.OverflowPool.LongLength * PersistBucketSize;
    }

    /// <summary>
    /// 写体：几何（表尺寸/溢出池容量/条目数）→ 桶区 → 溢出池（fuzzy 逐槽 128bit 原子读拷贝，
    /// Tentative/Empty 槽写零、Occupied/链指针原样收），分片经 <see cref="ProbingIndexBase{TKey}.WriteBodyChunk"/>。
    /// </summary>
    protected override void WriteBody()
    {
        var table = _table;                                   // ★ 单引用捕获（表+溢出池同代原子对）

        // 几何（32B——表骨架，恢复直接物化）
        Span<byte> geo = stackalloc byte[PersistGeometrySize];
        BinaryPrimitives.WriteInt64LittleEndian(geo, table.Size);
        BinaryPrimitives.WriteInt64LittleEndian(geo.Slice(8), table.OverflowPool.LongLength);
        BinaryPrimitives.WriteInt64LittleEndian(geo.Slice(16), Volatile.Read(ref _entryCount));
        WriteBodyChunk(geo);

        // 桶区 + 溢出池（fuzzy 逐槽 128bit 原子读——跳 Tentative，只收 Occupied/链指针）
        WriteBucketsFuzzy(table.TableRaw);
        WriteBucketsFuzzy(table.OverflowPool);
    }

    /// <summary>
    /// fuzzy 逐槽原子拷贝：128bit 原子读（CAS 环——零撕裂），Tentative/Empty 槽写零，Occupied/链指针原样收。
    /// <para>★ 溢出链指针（slot 7 = LogicalAddress(1,index)，SegId=1 且 state=Empty）必须保留——
    ///   过滤判据用 <see cref="HashEntry.IsEmpty"/>（全空谓词）而非裸 state==Empty。</para>
    /// </summary>
    private void WriteBucketsFuzzy(HashBucket[] buckets)
    {
        Span<byte> chunk = stackalloc byte[PersistBodyChunk];
        int chunkFill = 0;

        for (long b = 0; b < buckets.LongLength; b++)
        {
            ref var bucket = ref buckets[b];
            var slots = bucket.AsSpan();
            for (int s = 0; s < slots.Length; s++)
            {
                var entry = ReadSlotAtomic(ref slots[s]);
                if (HashEntry.IsEmpty(entry) || HashEntry.GetState(entry) == HashEntry.Tentative)
                    entry = LogicalAddress.Empty;

                MemoryMarshal.Write(chunk.Slice(chunkFill), in entry);
                chunkFill += 16;
                if (chunkFill == chunk.Length)
                {
                    WriteBodyChunk(chunk);
                    chunkFill = 0;
                }
            }
        }

        if (chunkFill > 0)
            WriteBodyChunk(chunk[..chunkFill]);
    }

    /// <summary>128bit 原子读（CAS 环：compare-exchange 同值——成功=读到一致快照；失败=槽被写者触碰，重读）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static LogicalAddress ReadSlotAtomic(ref LogicalAddress slot)
    {
        ref var loc = ref Unsafe.As<LogicalAddress, NativeInt128>(ref slot);
        while (true)
        {
            var v = Unsafe.As<LogicalAddress, NativeInt128>(ref slot);
            if (NativeAtomic128.CompareExchange(ref loc, v, v))
                return Unsafe.As<NativeInt128, LogicalAddress>(ref v);
        }
    }

    // ════════════════════════════════════════════════════════════
    // === 帧物化（基类帧走链定位后调——读几何 → 重建表+溢出池 → 重数实收）===
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 物化最新完整帧：读头（codec 全校验）→ 读几何（尺寸 2 的幂/池容量校验 + 体长核对）→
    /// 整读体重建表+溢出池 → 重数实收（fuzzy 帧内可能混入 dump 期间新插条目，计数以实收为准）。
    /// </summary>
    /// <param name="head">帧头地址（基类帧走链定位的最新完整帧）。</param>
    /// <param name="entryCount">物化后条目数（实收计数——仅成功返回时有意义）。</param>
    /// <returns>true = 物化成功；false = 任一校验失败（恢复核心走全量重放 fail-safe）。</returns>
    protected override bool TryMaterializeFrame(LogicalAddress head, out long entryCount)
    {
        entryCount = 0;
        int headerSize = ProbingIndexCodec.HeaderSize;
        Span<byte> hdr = stackalloc byte[headerSize];
        if (_engine.Read(head, hdr) < headerSize) return false;
        if (!ProbingIndexCodec.TryReadHeader(hdr, out var bodyLen)) return false;   // 格式全校验归 codec

        Span<byte> geo = stackalloc byte[PersistGeometrySize];
        if (_engine.Read(_engine.CalculationAddress(head, headerSize), geo) < PersistGeometrySize) return false;
        long size = BinaryPrimitives.ReadInt64LittleEndian(geo);
        long ofbCap = BinaryPrimitives.ReadInt64LittleEndian(geo.Slice(8));
        if (size <= 0 || (size & (size - 1)) != 0) return false;          // 2 的幂校验
        if (ofbCap < 0) return false;
        long expectBody = PersistGeometrySize + size * PersistBucketSize + ofbCap * PersistBucketSize;
        if (bodyLen != expectBody) return false;

        var tableRaw = new HashBucket[size];
        var pool = new HashBucket[ofbCap];

        // ★ 整读体（分片）——桶区 + 溢出池（体起点 = 头 + 几何，几何已在上面读过）
        var bodyAt = _engine.CalculationAddress(head, headerSize + PersistGeometrySize);
        if (!ReadBodyExact(bodyAt, MemoryMarshal.AsBytes(tableRaw.AsSpan()))) return false;
        if (!ReadBodyExact(_engine.CalculationAddress(bodyAt, size * PersistBucketSize),
                MemoryMarshal.AsBytes(pool.AsSpan()))) return false;

        _table = new InternalHashTable
        {
            Size = size,
            SizeMask = (ulong)(size - 1),
            SizeBits = System.Numerics.BitOperations.Log2((ulong)size),
            TableRaw = tableRaw,
            OverflowPool = pool,
        };

        // ★ 重数实收（fuzzy 帧内可能混入 dump 期间新插入条目——计数以实收为准）
        for (long b = 0; b < size; b++)
        {
            var slots = tableRaw[b].AsSpan();
            for (int s = 0; s < slots.Length; s++)
                if (HashEntry.GetState(slots[s]) == HashEntry.Occupied) entryCount++;
        }
        return true;
    }

    /// <summary>物化后回调——重数实收结果写回写者维护的条目计数（fuzzy 帧实收为准）。</summary>
    /// <param name="entryCount">物化实收条目数。</param>
    protected override void OnMaterialized(long entryCount) => _entryCount = entryCount;

    /// <summary>当前条目数（Volatile 读写者计数——后台 dump 策略触发用）。</summary>
    protected override long CurrentEntryCount => Volatile.Read(ref _entryCount);

    private bool ReadBodyExact(LogicalAddress at, Span<byte> dst)
    {
        var off = at;
        int done = 0;
        while (done < dst.Length)
        {
            int n = (int)Math.Min(dst.Length - done, PersistBodyChunk);
            if (!ReadBodyChunk(off, dst.Slice(done, n), out int got)) return false;
            done += got;
            off = _engine.CalculationAddress(off, got);
        }
        return true;
    }
}
