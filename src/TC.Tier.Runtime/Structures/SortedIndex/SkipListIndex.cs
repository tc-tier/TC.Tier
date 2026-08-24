using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TC.Tier.Runtime.Structures.SortedIndex.Contracts;
using TC.Tier.Core.Primitives;
using TC.Tier.Contracts.Structures;


namespace TC.Tier.Runtime.Structures.SortedIndex;

public partial class SkipListIndex<TKey> : SortedIndexBase<TKey> where TKey : unmanaged, IEquatable<TKey>
{
    /// <summary>
    /// 引擎节点字节形态（Sequential/Pack=8）——codec 与 arena 驻留节点共用此布局。
    /// <para>★ arena 驻留指针的偏移契约全系于此：Key@0、SegId@8、Offset@16、Extension@24、
    /// LevelCount@28、Level_i@32+16i——改布局必须同步 <see cref="ReadKey"/>/<see cref="ReadValue"/>/
    /// <see cref="ReadLevel"/>/<see cref="LevelRef"/> 的偏移常量。</para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct SkipListNodeHeader
    {
        internal TKey Key;
        internal int SegId;
        internal long Offset;
        internal int Extension;
        internal byte LevelCount;
        internal byte Reserved0;
        internal short Reserved1;
        internal LogicalAddress Level0;
        internal LogicalAddress Level1;
        internal LogicalAddress Level2;
        internal LogicalAddress Level3;
        internal LogicalAddress Level4;
        internal LogicalAddress Level5;
        internal LogicalAddress Level6;
        internal LogicalAddress Level7;
        internal LogicalAddress Level8;
        internal LogicalAddress Level9;
        internal LogicalAddress Level10;
        internal LogicalAddress Level11;
        internal LogicalAddress Level12;
        internal LogicalAddress Level13;
        internal LogicalAddress Level14;
        internal LogicalAddress Level15;
    }

    /// <summary>层指针数组在节点内的字节偏移（布局契约，见 <see cref="SkipListNodeHeader"/>）。</summary>
    private const int LevelArrayOffset = 32;
    private const int LevelCountOffset = 28;
    private const int ValueSegIdOffset = 8;
    private const int ValueOffsetOffset = 16;
    private const int ValueExtensionOffset = 24;

    private const int HeaderSize = 32;

    private readonly int _maxLevel;
    private readonly int _highLevelCacheThreshold;
    private readonly int _maxRetryCount;
    private readonly long _safeReclaimDelayMs;

    // ★ TKey 大小比较靠 Comparer<TKey>.Default；相等比较靠 EqualityComparer<TKey>.Default.Equals。

    /// <summary>★ 节点驻留 arena：变长（32+16×实际层高）非托管块——工作集从 288B/节点 压到均值 ~64B/节点
    /// （100k 条 28.8MB→~6.5MB，L3 常驻）；块只增不搬移=指针恒稳。</summary>
    private readonly NodeArena _nodeArena = new();
    /// <summary>★ addr → 驻留节点指针（8B 值）——跳一跳=探测+指针直访，零结构拷贝
    /// （旧形 LogicalAddressMap&lt;SkipListNodeHeader&gt; 每跳 288B 槽→局部拷贝）。</summary>
    private readonly LogicalAddressMap<nint> _cachedNodes;
    /// <summary>head 节点指针（InitializeIndex 时 arena 落位——生命周期闸门保证先于一切读写发布）。</summary>
    private unsafe byte* _headPtr;
    /// <summary>head 节点的引擎地址（镜像序列化用——塔顶锚点）。</summary>
    private LogicalAddress _headAddress;

    private readonly Random _rng = new();
    private int _currentLevel;
    private long _entryCount;
    private readonly Dictionary<TKey, LogicalAddress> _reclaimedNodes = new();

    /// <summary>
    /// ★ 脏节点集合（链/值变更延迟写回——插入路径零额外引擎写；dump 时批量写回）。
    /// <para>安全论证：脏节点必在驻留缓存（变更前经 GetNode/AdmitNode 登记）→ 读路径缓存命中，
    ///   引擎副本只服务物化（dump 批量写回后读）；崩溃窗口（dump 前）由恢复重放 (W, End] 修复——
    ///   W=上次 dump 水位，重放补齐全部变更，引擎旧链无害。</para>
    /// </summary>
    private readonly HashSet<LogicalAddress> _dirtyNodes = new();

    /// <summary>ctor 收 protected internal——开放泛型不落消费面闸门（同 BlittableRing）：外部用 [RingKey] 封闭类型。</summary>
    protected internal SkipListIndex(IFileSystem fileSystem, SkipListIndexSettings settings,
        LightEpoch? epoch = null,
        IKeyResolver<TKey>? keyResolver = null)
        : base(SkipListIndexCodec.Instance, fileSystem, settings, epoch, keyResolver: keyResolver)
    {
        _maxLevel = settings.MaxLevel;
        _highLevelCacheThreshold = settings.HighLevelCacheThreshold;
        _maxRetryCount = settings.MaxRetryCount;
        _safeReclaimDelayMs = settings.SafeReclaimDelayMs;
        _cachedNodes = new LogicalAddressMap<nint>(settings.NodeCacheInitialCapacity, growable: true);
        Resources.Add(_nodeArena);   // Dispose 全释放非托管块

        // ★ head 是哨兵节点：Find/Insert/Delete 都从 head 的 Level(i) 读 next 开始遍历，
        //   从不读取/比较 head.Key 本身（参见各方法实现）。Key 设 default 不影响正确性。
        _currentLevel = 1;
    }

    protected override unsafe void InitializeIndex()
    {
        int headSize = ComputeNodeSize(_maxLevel);
        var head = new SkipListNodeHeader
        {
            Key = default!,
            LevelCount = (byte)_maxLevel,   // Value 字段全零 = LogicalAddress.Empty
        };
        var headAddr = AllocateNode(headSize);
        _headAddress = headAddr;
        _headPtr = AdmitNode(headAddr, in head);
        WriteNode(headAddr, new ReadOnlySpan<byte>(_headPtr, headSize));
    }

    private static int ComputeNodeSize(int levelCount) => HeaderSize + levelCount * 16;

    // ═══ 节点管道：arena 驻留 + 缓存 admit ═══

    /// <summary>
    /// 取节点驻留指针（Find/Insert/Delete/Cursor 全路径统一口）。
    /// <para>★ miss=引擎读到栈→arena 落位→admit（读后回填不变量的 arena 形）。arena Alloc 是
    /// CAS-bump（读者侧 admit 安全）；竞争重复 admit 输家块字节浪费有界（map 留先入指针）。</para>
    /// <para>★ 并发契约（单写者+并发读者容忍，与被替换的 header 拷贝形同界）：写者经
    /// <see cref="WriteLevel"/>/<see cref="LevelRef"/> CAS 改塔链，读者指针读 16B LogicalAddress
    /// 可能见半新半旧——比较不中即 miss 语义，落到引擎读兜底。</para>
    /// </summary>
    private unsafe byte* GetNode(LogicalAddress addr)
    {
        ref readonly var hit = ref _cachedNodes.Find(addr);
        if (!Unsafe.IsNullRef(in hit))
            return (byte*)hit;

        var header = ReadHeaderFromEngine(addr);
        return AdmitNode(addr, in header);
    }

    /// <summary>arena 落位 + 缓存 admit（返回 map 认定的指针——竞争输家块成为有界孤块）。</summary>
    private unsafe byte* AdmitNode(LogicalAddress addr, in SkipListNodeHeader header)
    {
        int size = ComputeNodeSize(header.LevelCount);
        var node = _nodeArena.Alloc(size);
        Unsafe.CopyBlockUnaligned(node, Unsafe.AsPointer(ref Unsafe.AsRef(in header)), (uint)size);
        ref var slot = ref _cachedNodes.GetOrAdd(addr, (nint)node);
        return (byte*)slot;
    }

    /// <summary>纯引擎读（栈缓冲，无回填）——GetNode miss 的取源。</summary>
    private SkipListNodeHeader ReadHeaderFromEngine(LogicalAddress addr)
    {
        Span<byte> buf = stackalloc byte[Unsafe.SizeOf<SkipListNodeHeader>()];
        buf.Clear();
        var read = ReadNode(addr, buf);
        if (read < HeaderSize) return default;
        return MemoryMarshal.Read<SkipListNodeHeader>(buf);
    }

    /// <summary>驻留节点写回引擎（按实际层高截写）。</summary>
    private unsafe void WriteNodeToEngine(LogicalAddress addr, byte* node)
    {
        int nodeSize = ComputeNodeSize(node[LevelCountOffset]);
        WriteNode(addr, new ReadOnlySpan<byte>(node, nodeSize));
    }

    /// <summary>脏标记（链/值变更延迟写回——插入路径零引擎写，dump 时批量）。</summary>
    private void MarkDirty(LogicalAddress addr) => _dirtyNodes.Add(addr);

    // ═══ 驻留节点字段访问器（偏移契约见 SkipListNodeHeader 注）═══

    private static unsafe TKey ReadKey(byte* node) => Unsafe.Read<TKey>(node);

    private static unsafe LogicalAddress ReadValue(byte* node)
        => new(*(int*)(node + ValueSegIdOffset), *(int*)(node + ValueExtensionOffset), *(long*)(node + ValueOffsetOffset));

    private static unsafe void WriteValue(byte* node, LogicalAddress value)
    {
        *(int*)(node + ValueSegIdOffset) = value.SegId;
        *(long*)(node + ValueOffsetOffset) = value.Offset;
        *(int*)(node + ValueExtensionOffset) = value.Extension;
    }

    private static unsafe LogicalAddress ReadLevel(byte* node, int level)
        => Unsafe.Read<LogicalAddress>(node + LevelArrayOffset + 16 * level);

    private static unsafe void WriteLevel(byte* node, int level, LogicalAddress addr)
        => Unsafe.Write(node + LevelArrayOffset + 16 * level, addr);

    private static unsafe ref LogicalAddress LevelRef(byte* node, int level)
        => ref Unsafe.AsRef<LogicalAddress>(node + LevelArrayOffset + 16 * level);

    /// <summary>几何层分配（p=1/2，封顶 _maxLevel）——与插入时间/条目数解耦（纪元式钳制的线性扫教训）。</summary>
    private int RandomLevel()
    {
        int level = 1;
        while (level < _maxLevel && (_rng.Next() & 1) == 0)
            level++;
        return level;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool CasLevel(ref LogicalAddress slot, LogicalAddress expected, LogicalAddress desired)
        => CasSlot(ref slot, expected, desired);
}
