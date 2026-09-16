using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TC.Tier.Core.NativeInterop;

using TC.Tier.Core.Primitives;
using NativeInt128 = TC.Tier.Core.NativeInterop.UInt128Pair;
using TC.Tier.Contracts.Structures;


namespace TC.Tier.Runtime.Structures.ProbingIndex;

/// <summary>
/// HashIndex 主 partial——开放寻址哈希内核（槽位状态位域 <c>HashEntry</c>、128B 桶 <c>HashBucket</c>、
/// 表+溢出池同代原子对 <c>InternalHashTable</c>、条带写锁与构造装配）。
/// </summary>
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

        /// <summary>构造 Tentative 态槽值（旧两阶段插入协议遗留——当前写路径 CAS 单步直落 Occupied，不再产生 Tentative；dump 过滤判据仍识别该态）。</summary>
        /// <param name="segId">value record 所在段号。</param>
        /// <param name="offset">段内偏移（字节）。</param>
        /// <param name="tag">14 位哈希指纹（64 位 hash 高 14 位——仅加速判等，命中后须回读真 key 校验）。</param>
        /// <param name="version">条目版本号（低 16 位打包，16 位回绕）。</param>
        /// <returns>打包后的 Tentative 态槽值（state/tag/version 编入 Extension）。</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static LogicalAddress CreateTentative(int segId, long offset, ushort tag, int version)
            => new(segId, (Tentative << StateShift) | (tag << TagShift) | (version & VersionMask), offset);

        /// <summary>构造 Occupied 态槽值（segId+offset 定位 value record；state/tag/version 编入 Extension——CAS 单步落位格式）。</summary>
        /// <param name="segId">value record 所在段号。</param>
        /// <param name="offset">段内偏移（字节）。</param>
        /// <param name="tag">14 位哈希指纹（64 位 hash 高 14 位——仅加速判等，命中后须回读真 key 校验）。</param>
        /// <param name="version">条目版本号（同 key 覆写换绑时 +1，16 位回绕）。</param>
        /// <returns>打包后的 Occupied 态槽值。</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static LogicalAddress CreateOccupied(int segId, long offset, ushort tag, int version)
            => new(segId, (Occupied << StateShift) | (tag << TagShift) | (version & VersionMask), offset);

        /// <summary>全空判定（SegId=0 且 Offset=0 且 state=Empty——溢出链指针 SegId=1/state=Empty 不算空，判据不可降级为裸 state）。</summary>
        /// <param name="e">待判定槽值。</param>
        /// <returns>true = 槽完全为空（可作落位候选）；false = Occupied/Tentative/溢出链指针任一。</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsEmpty(LogicalAddress e) => e.SegId == 0 && e.Offset == 0 && GetState(e) == Empty;

        /// <summary>取槽状态（Extension 位 [31..30) 两位——Empty/Tentative/Occupied 三态）。</summary>
        /// <param name="e">槽值。</param>
        /// <returns>状态值：0=Empty、1=Tentative、2=Occupied。</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetState(LogicalAddress e) => (e.Extension >> StateShift) & StateMask;

        /// <summary>取 14 位哈希指纹 tag（Extension 位 [29..16)——64 位 hash 高 14 位，仅加速判等不保证唯一）。</summary>
        /// <param name="e">槽值。</param>
        /// <returns>tag 指纹（0..0x3FFF）。</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort GetTag(LogicalAddress e) => (ushort)((e.Extension >> TagShift) & TagMask);

        /// <summary>取条目版本号（Extension 低 16 位——同 key 覆写换绑时递增，参与 CAS 全等比较使覆写前后槽值可区分）。</summary>
        /// <param name="e">槽值。</param>
        /// <returns>版本号（0..65535，16 位掩码回绕）。</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetVersion(LogicalAddress e) => e.Extension & VersionMask;

        /// <summary>计算下一版本号（当前版本 +1 后按 16 位掩码回绕——同 key 覆写换绑打包用）。</summary>
        /// <param name="e">当前槽值。</param>
        /// <returns>下一版本号（0..65535）。</returns>
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
    private long _entryCount;                // 写者维护（增减与 dup 覆写区分）——增长触发 + O(1) EntryCount；Interlocked 增减

    // ★ #218：溢出分配全局锁 → 链级条带锁——不同源桶的溢出链互不相交（每溢出桶属且仅属一条链），
    //   条带并发无结构冲突；计数 bump 走 Interlocked（分配原子），挂链按源桶条带串行（同链必同源桶）。
    internal const int OverflowLockCount = 16;
    internal const int OverflowLockMask = OverflowLockCount - 1;
    private readonly object[] _overflowLocks = Enumerable.Range(0, OverflowLockCount).Select(_ => new object()).ToArray();
    private object OverflowChainLock(int sourceBucketIndex) => _overflowLocks[sourceBucketIndex & OverflowLockMask];

    private readonly object _growLock = new();   // ★ 增长单飞（并发 Insert 同时判超阈——只一个构建，其余重判断）

    /// <summary>条带写锁数量（Insert/Delete 按 hash 低位分条——同 key 必同条带，跨条带并发无碍）。</summary>
    internal const int SlotLockCount = 64;
    internal const int SlotLockMask = SlotLockCount - 1;
    private readonly object[] _slotLocks = CreateSlotLocks();

    private static object[] CreateSlotLocks()
    {
        var locks = new object[SlotLockCount];
        for (int i = 0; i < locks.Length; i++)
            locks[i] = new object();
        return locks;
    }

    /// <summary>
    /// ctor 收 <c>protected internal</c>——开放泛型不落消费面闸门（同 BlittableRing）：
    /// 外部直接 new 即 CS0122，用 [RingKey] 生成封闭类型（HashOfT 经 protected 肢）；内核测试经 IVT。
    /// <para>★ <paramref name="keyComparer"/> 可选注入（默认 <c>KeyComparer&lt;TKey&gt;</c> 全字段判等）；
    ///   点查 latest 版本语义（如 TierKV 同 key 多版本——HashIndex 只关心 latest 地址）可注入
    ///   版本无关比较器（仅 HashCode 判等/哈希）。</para>
    /// </summary>
    /// <param name="fileSystem">文件系统抽象（主存储引擎底层 IO 面）。</param>
    /// <param name="settings">配置（表初始桶数/溢出池桶数/持久化机制/引擎选项）。</param>
    /// <param name="epoch">共享 epoch 实例（null=索引自建并持有）。</param>
    /// <param name="keyResolver">判等闭环数据面（tag 命中后回读真 key 校验 + 恢复拉流）——必填，null 抛 ArgumentNullException。</param>
    /// <param name="keyComparer">键比较器（null=默认 KeyComparer&lt;TKey&gt; 全字段判等）。</param>
    protected internal HashIndex(IFileSystem fileSystem, HashIndexSettings settings,
        LightEpoch? epoch = null,
        IKeyResolver<TKey>? keyResolver = null,
        IKeyComparer<TKey>? keyComparer = null)
        : base(HashIndexCodec.Instance, fileSystem, settings, keyResolver!, epoch, keyComparer)
    {
        // ★ tag-only 桶判等闭环强依赖 KeyResolver：tag 命中后必须读回 record 的真 key 校验，否则 tag 冲突静默错误。
        ArgumentNullException.ThrowIfNull(keyResolver);

        var capacity = settings.HashTableCapacity;
        if (!BitOperations.IsPow2(capacity))
            throw new ArgumentException($"HashTableCapacity must be power of 2, got {capacity}");

        var overflowCapacity = settings.OverflowPoolCapacity;
        if (overflowCapacity > 0 && !BitOperations.IsPow2(overflowCapacity))
            throw new ArgumentException($"OverflowPoolCapacity must be power of 2, got {overflowCapacity}");

        _table = BuildTable(capacity, overflowCapacity);
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

    /// <summary>恢复钩子：索引初始化——记录引擎 MinAddress 为探测起始地址（探测索引只在内存，只锚定地址空间起点）。</summary>
    protected override void InitializeIndex()
    {
        _beginAddress = _engine.MinAddress;
    }
}
