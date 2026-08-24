using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TC.Tier.Core.NativeInterop;

using TC.Tier.Core.Primitives;
using NativeInt128 = TC.Tier.Core.NativeInterop.Int128;
using TC.Tier.Contracts.Structures;


namespace TC.Tier.Runtime.Structures.ProbingIndex;

public partial class HashIndex<TKey> : ProbingIndexBase<TKey> where TKey : unmanaged, IEquatable<TKey>
{
    internal static class HashEntry
    {
        public const int StateShift = 30;
        public const int TagShift = 16;
        public const int StateMask = 3;
        public const int TagMask = 0x3FFF;
        public const int VersionMask = 0xFFFF;

        public const int Empty = 0;
        public const int Tentative = 1;
        public const int Occupied = 2;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static LogicalAddress CreateTentative(int segId, long offset, ushort tag, int version)
            => new(segId, (Tentative << StateShift) | (tag << TagShift) | (version & VersionMask), offset);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static LogicalAddress CreateOccupied(int segId, long offset, ushort tag, int version)
            => new(segId, (Occupied << StateShift) | (tag << TagShift) | (version & VersionMask), offset);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsEmpty(LogicalAddress e) => e.SegId == 0 && e.Offset == 0 && GetState(e) == Empty;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetState(LogicalAddress e) => (e.Extension >> StateShift) & StateMask;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort GetTag(LogicalAddress e) => (ushort)((e.Extension >> TagShift) & TagMask);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetVersion(LogicalAddress e) => e.Extension & VersionMask;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int NextVersion(LogicalAddress e) => (GetVersion(e) + 1) & VersionMask;
    }

    [StructLayout(LayoutKind.Sequential, Size = 128)]
    internal struct HashBucket
    {
        internal LogicalAddress Slot0;
        internal LogicalAddress Slot1;
        internal LogicalAddress Slot2;
        internal LogicalAddress Slot3;
        internal LogicalAddress Slot4;
        internal LogicalAddress Slot5;
        internal LogicalAddress Slot6;
        internal LogicalAddress Slot7;

        internal Span<LogicalAddress> AsSpan()
        {
            return MemoryMarshal.CreateSpan(ref Slot0, 8);
        }
    }

    /// <summary>
    /// 哈希表代（table+overflow 池的<b>原子发布对</b>）。
    /// <para>★ 池与表同代共存亡：增长=纯函数式构建新代（不扰动旧代）+ <c>_table</c> 单引用发布——
    ///   并发读者持旧代引用继续一致探测（stale-but-valid，条目仍真、仅缺发布后新插），旧代归 GC，
    ///   无需 epoch 排水。溢出指针 (1, poolIndex) 只在同年内解引用。</para>
    /// </summary>
    internal sealed class InternalHashTable
    {
        internal required long Size;
        internal required ulong SizeMask;
        internal required int SizeBits;
        internal required HashBucket[] TableRaw;
        internal required HashBucket[] OverflowPool;
        internal int OverflowCount;          // 池 bump 指针（写者单线程递增；读者不触）
    }

    private const int MaxOverflowSlots = 7;

    /// <summary>增长触发装载率（条目数/桶数超过即翻倍——rehash 均摊 O(1)/插）。</summary>
    private const double GrowthLoadFactor = 0.7;

    private InternalHashTable _table;
    private long _entryCount;                // 写者维护（增减与 dup 覆写区分）——增长触发 + O(1) EntryCount

    private readonly object _overflowLock = new();

    /// <summary>
    /// ctor 收 <see cref="protected internal"/>——开放泛型不落消费面闸门（同 BlittableRing）：
    /// 外部直接 new 即 CS0122，用 [RingKey] 生成封闭类型（HashOfT 经 protected 肢）；内核测试经 IVT。
    /// </summary>
    protected internal HashIndex(IFileSystem fileSystem, HashIndexSettings settings,
        LightEpoch? epoch = null,
        IKeyResolver<TKey>? keyResolver = null)
        : base(HashIndexCodec.Instance, fileSystem, settings, keyResolver!, epoch)
    {
        // ★ tag-only 桶判等闭环强依赖 KeyResolver：tag 命中后必须读回 record 的真 key 校验，否则 tag 冲突静默错误。
        ArgumentNullException.ThrowIfNull(keyResolver);

        var capacity = settings.HashTableCapacity;
        if (!BitOperations.IsPow2(capacity))
            throw new ArgumentException($"HashTableCapacity must be power of 2, got {capacity}");

        _table = BuildTable(capacity, overflowCapacity: settings.OverflowPoolCapacity);
    }

    private static InternalHashTable BuildTable(long size, int overflowCapacity)
        => new()
        {
            Size = size,
            SizeMask = (ulong)(size - 1),
            SizeBits = BitOperations.Log2((uint)size),
            TableRaw = new HashBucket[size],
            OverflowPool = new HashBucket[overflowCapacity],
        };

    protected override void InitializeIndex()
    {
        _beginAddress = _engine.MinAddress;
    }
}
