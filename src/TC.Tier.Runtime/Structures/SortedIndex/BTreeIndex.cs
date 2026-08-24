using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TC.Tier.Contracts.Structures;


namespace TC.Tier.Runtime.Structures.SortedIndex;

public partial class BTreeIndex<TKey> : SortedIndexBase<TKey> where TKey : unmanaged, IEquatable<TKey>
{
    private const int MaxEntries = 9;
    private const int MinEntries = 4;

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct BTreeNode
    {
        internal ushort Flags;
        internal ushort Count;
        internal uint Reserved;
        internal TKey Key0;
        internal LogicalAddress Value0;
        internal TKey Key1;
        internal LogicalAddress Value1;
        internal TKey Key2;
        internal LogicalAddress Value2;
        internal TKey Key3;
        internal LogicalAddress Value3;
        internal TKey Key4;
        internal LogicalAddress Value4;
        internal TKey Key5;
        internal LogicalAddress Value5;
        internal TKey Key6;
        internal LogicalAddress Value6;
        internal TKey Key7;
        internal LogicalAddress Value7;
        internal TKey Key8;
        internal LogicalAddress Value8;
        internal LogicalAddress Next;

        private const ushort IsLeafFlag = 0x01;
        private const ushort IsRootFlag = 0x02;

        internal bool IsLeaf
        {
            readonly get => (Flags & IsLeafFlag) != 0;
            set => Flags = value ? (ushort)(Flags | IsLeafFlag) : (ushort)(Flags & ~IsLeafFlag);
        }

        internal bool IsRoot
        {
            readonly get => (Flags & IsRootFlag) != 0;
            set => Flags = value ? (ushort)(Flags | IsRootFlag) : (ushort)(Flags & ~IsRootFlag);
        }

        /// <summary>★ 只读访问器必须带 readonly——ref readonly 接收者上调非 readonly 成员会触发防御性拷贝
        /// （160B/次），FindNoEpoch 零拷贝下降会被编译器静默吃掉。</summary>
        internal readonly TKey GetKey(int index)
        {
            return index switch
            {
                0 => Key0, 1 => Key1, 2 => Key2, 3 => Key3, 4 => Key4, 5 => Key5, 6 => Key6, 7 => Key7, 8 => Key8,
                _ => throw new InvalidOperationException($"BTree slot index {index} out of range (0..8) — internal invariant violated")
            };
        }

        internal void SetKey(int index, TKey key)
        {
            switch (index)
            {
                case 0: Key0 = key; break;
                case 1: Key1 = key; break;
                case 2: Key2 = key; break;
                case 3: Key3 = key; break;
                case 4: Key4 = key; break;
                case 5: Key5 = key; break;
                case 6: Key6 = key; break;
                case 7: Key7 = key; break;
                case 8: Key8 = key; break;
                default: throw new InvalidOperationException($"BTree slot index {index} out of range (0..8) — internal invariant violated");
            }
        }

        internal readonly LogicalAddress GetValue(int index)
        {
            return index switch
            {
                0 => Value0, 1 => Value1, 2 => Value2, 3 => Value3, 4 => Value4, 5 => Value5, 6 => Value6, 7 => Value7, 8 => Value8,
                _ => throw new InvalidOperationException($"BTree slot index {index} out of range (0..8) — internal invariant violated")
            };
        }

        internal void SetValue(int index, LogicalAddress value)
        {
            switch (index)
            {
                case 0: Value0 = value; break;
                case 1: Value1 = value; break;
                case 2: Value2 = value; break;
                case 3: Value3 = value; break;
                case 4: Value4 = value; break;
                case 5: Value5 = value; break;
                case 6: Value6 = value; break;
                case 7: Value7 = value; break;
                case 8: Value8 = value; break;
                default: throw new InvalidOperationException($"BTree slot index {index} out of range (0..8) — internal invariant violated");
            }
        }

        internal void ShiftRight(int fromIndex, int toIndex, int count)
        {
            for (int i = count - 1; i >= fromIndex; i--)
            {
                SetKey(i + toIndex - fromIndex, GetKey(i));
                SetValue(i + toIndex - fromIndex, GetValue(i));
            }
        }

        internal void ShiftLeft(int fromIndex, int toIndex, int count)
        {
            for (int i = fromIndex; i < count; i++)
            {
                SetKey(i + toIndex - fromIndex, GetKey(i));
                SetValue(i + toIndex - fromIndex, GetValue(i));
            }
        }

        internal readonly int FindPosition(TKey key, IKeyComparer<TKey> comparer)
        {
            for (int i = 0; i < Count; i++)
            {
                if (comparer.Equals(GetKey(i), key)) return i;
            }
            return -1;
        }
    }

    private readonly int _nodeSize = 256;
    private readonly int _minFillPercent;


    private LogicalAddress _rootAddress;
    private BTreeNode _cachedRoot;
    /// <summary>节点缓存（节点即数据教义：无上限生长，含叶子——100k 条 ≈ 2.2MB 全 L3 常驻）。</summary>
    private readonly LogicalAddressMap<BTreeNode> _nodeCache;
    /// <summary>★ 写者维护条目计数（主存储策略触发 + O(1) EntryCount——对齐 SkipList/HashIndex）。</summary>
    private long _entryCount;

    /// <summary>
    /// ★ 脏节点地址集（延迟写回——插入/分裂路径零引擎写；dump 时批量写引擎）。
    /// <para>只存地址（16B/项——值快照版 240B×n 超 L3 污染点查，已销案）；写回从缓存取值——
    ///   不变式：<b>每次 WriteNodeContent 前节点必已入缓存</b>（RefreshCache/根特例 _cachedRoot，
    ///   PromoteRoot 删 Clear 保旧根条目）→ 读路径 miss=从未变更=引擎内容最新，无需脏兜底。</para>
    /// </summary>
    private readonly HashSet<LogicalAddress> _dirtyNodes = new();

    private static int ComputeNodeSize(int configuredSize, int nodeStructSize)
    {
        return Math.Max(nodeStructSize, Math.Max(256, configuredSize));
    }

    /// <summary>ctor 收 protected internal——开放泛型不落消费面闸门（同 BlittableRing）：外部用 [RingKey] 封闭类型。</summary>
    protected internal BTreeIndex(IFileSystem fileSystem, BTreeIndexSettings settings,
        LightEpoch? epoch = null,
        IKeyResolver<TKey>? keyResolver = null)
        : base(BTreeIndexCodec.Instance, fileSystem, settings, epoch, keyResolver: keyResolver)
    {
        _nodeSize = ComputeNodeSize(settings.NodeSize, Unsafe.SizeOf<BTreeNode>());
        _minFillPercent = settings.MinFillPercent;
        _nodeCache = new LogicalAddressMap<BTreeNode>(settings.NodeCacheInitialCapacity, growable: true);
        _rootAddress = LogicalAddress.Empty;
    }

    protected override void InitializeIndex()
    {
        _beginAddress = _engine.MinAddress;

        if (_rootAddress == LogicalAddress.Empty)
        {
            var root = new BTreeNode { IsLeaf = true, IsRoot = true, Count = 0 };
            _rootAddress = AllocateNode(_nodeSize);
            WriteNodeContent(_rootAddress, root);
            _cachedRoot = root;
        }
    }

    private void WriteNodeContent(LogicalAddress addr, BTreeNode node)
    {
        // ★ 脏节点延迟写回（对齐 SkipList 销案）：内容变更只记脏标记（地址集），
        //   dump 时批量写引擎——插入/分裂路径零引擎写。安全论证：节点必在驻留缓存
        //   （RefreshCache/根特例——不变式），读路径不受影响；崩溃窗口（dump 前）由恢复重放 (W, End] 修复。
        _dirtyNodes.Add(addr);
    }

    /// <summary>真写引擎（dump 批量写回用——WriteBody 调）。</summary>
    private void WriteNodeContentNow(LogicalAddress addr, BTreeNode node)
    {
        Span<byte> buf = stackalloc byte[_nodeSize];
        MemoryMarshal.Write(buf, in node);
        WriteNode(addr, buf);
    }

    private BTreeNode ReadNodeContent(LogicalAddress addr)
    {
        Span<byte> buf = stackalloc byte[_nodeSize];
        var read = ReadNode(addr, buf);
        if (read < _nodeSize) buf.Slice(read).Clear();
        return MemoryMarshal.Read<BTreeNode>(buf);
    }
}
