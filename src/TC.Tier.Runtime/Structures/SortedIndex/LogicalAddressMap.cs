using System.Numerics;
using System.Runtime.CompilerServices;

namespace TC.Tier.Runtime.Structures.SortedIndex;

/// <summary>
/// LogicalAddress → unmanaged 值的扁平开放寻址表（线性探测）——索引节点缓存形态
/// （BTree 定容内部节点缓存 / SkipList 无上限节点缓存共用）。
/// <para>★ 替换 Dictionary&lt;LogicalAddress, TValue&gt;：键值各自扁平数组、探测一步直达
///   （无桶数组→条目数组两跳依赖 cache miss），<see cref="Find"/> 返回槽引用由调用方定拷贝时机。</para>
/// <para>★ 并发契约（与被替换的 Dictionary 同界，不更宽）：单写者 + 并发读者容忍。
///   键/值数组由不可变 <see cref="Tables"/> 持有、读者<b>单次引用装载</b>即得一致快照——
///   与并发 Resize 竞态时持旧表全量探测（旧表更短，索引恒在界内），miss 落引擎读。
///   写路径发布序=先值后键——x86-TSO 下读者见到完整键即可见到完整值；键 16B 非原子，
///   读者可能见到半新键——比较不中即 miss（结果正确，仅慢）。
///   无删除（不设墓碑），<see cref="Clear"/> 全清（键数组回 <see cref="LogicalAddress.Invalid"/>）。</para>
/// <para>★ 键相等对齐 <see cref="LogicalAddress"/> 相等：仅 SegId+Offset 参与，Extension 不参与。
///   空槽标记 = <see cref="LogicalAddress.Invalid"/>（真实地址永不等于它）；
///   <see cref="LogicalAddress.Empty"/>（seg0@0）是<b>合法键</b>——BTree 根节点常驻首分配位，
///   零值键与空槽的区分全系于此（<see cref="Clear"/> 不可零填充）。</para>
/// </summary>
internal sealed class LogicalAddressMap<TValue> where TValue : unmanaged
{
    /// <summary>装载上限（探测质量与空间浪费的折中——同 Dictionary 预留）。</summary>
    private const double MaxLoadFactor = 0.72;

    /// <summary>键/值数组的不可变持有者——读者单次引用装载即得一致快照（Resize 整体换表）。</summary>
    private sealed class Tables(LogicalAddress[] keys, TValue[] values)
    {
        internal readonly LogicalAddress[] Keys = keys;
        internal readonly TValue[] Values = values;
        internal int Slots => Keys.Length;
    }

    private static readonly Tables EmptyTables = new(Array.Empty<LogicalAddress>(), Array.Empty<TValue>());

    private Tables _tables;
    private readonly int _admissionLimit; // 定容准入上限；growable = int.MaxValue
    private readonly bool _growable;
    private int _count;

#if DEBUG
    /// <summary>探测健康度仪器：历史最长探测链（契约测试断言有界——满表不得死循环）。</summary>
    internal int MaxProbeLength;
#endif

    /// <param name="capacity">定容模式=准入条数上限（超出静默不进）；生长模式=初始容量（满则倍增重散列）。</param>
    /// <param name="growable">true=无上限生长（SkipList 节点即数据量级）；false=定容（BTree 内部节点缓存）。</param>
    internal LogicalAddressMap(int capacity, bool growable)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);

        _growable = growable;
        if (capacity == 0)
        {
            _tables = EmptyTables;
            _admissionLimit = 0;
            return;
        }

        int slots = growable
            ? Math.Max(16, (int)BitOperations.RoundUpToPowerOf2((uint)capacity))
            : (int)BitOperations.RoundUpToPowerOf2((uint)Math.Ceiling(capacity / MaxLoadFactor));
        _tables = new Tables(CreateInvalidKeyArray(slots), new TValue[slots]);
        _admissionLimit = growable ? int.MaxValue : capacity;
    }

    internal int Count => _count;

    /// <summary>命中返回槽引用；未命中返回 null-ref（<see cref="Unsafe.IsNullRef{T}(ref T)"/> 判别）——零拷贝读。</summary>
    internal ref readonly TValue Find(LogicalAddress key)
    {
        var t = _tables;
        var keys = t.Keys;
        if (keys.Length == 0 || !key.IsValid)
            return ref Unsafe.NullRef<TValue>();

        var values = t.Values;
        uint mask = (uint)(keys.Length - 1);
        int i = SlotOf(key, keys.Length);
        int probe = 1;
        while (true)
        {
            var k = keys[i];
            if (k.SegId == key.SegId && k.Offset == key.Offset)
            {
                TrackProbe(probe);
                return ref values[i];
            }
            if (!k.IsValid)
            {
                TrackProbe(probe);
                return ref Unsafe.NullRef<TValue>();
            }
            i = (int)((uint)i + 1 & mask);
            probe++;
        }
    }

    /// <summary>读拷贝形态（与 <see cref="Find"/> 等价，值拷贝一次经 out 带出）。</summary>
    internal bool TryGetValue(LogicalAddress key, out TValue value)
    {
        ref readonly var slot = ref Find(key);
        if (Unsafe.IsNullRef(in slot))
        {
            value = default;
            return false;
        }
        value = slot;
        return true;
    }

    /// <summary>
    /// GetOrAdd：命中返回<b>既有</b>槽引用（不覆写）；未命中且有空间则写入并返回新槽引用；
    /// 定容满且不存在则不写入、返回 null-ref。写入发布序=先值后键（见类注并发契约）。
    /// </summary>
    internal ref TValue GetOrAdd(LogicalAddress key, scoped in TValue value)
    {
        if (!key.IsValid)
            throw new ArgumentException("键必须是有效地址——Invalid 保留作空槽标记。", nameof(key));

        var t = _tables;
        var keys = t.Keys;
        if (keys.Length == 0)
            return ref Unsafe.NullRef<TValue>();

        var values = t.Values;
        uint mask = (uint)(keys.Length - 1);
        int i = SlotOf(key, keys.Length);
        int probe = 1;
        while (true)
        {
            var k = keys[i];
            if (k.SegId == key.SegId && k.Offset == key.Offset)
            {
                TrackProbe(probe);
                return ref values[i];
            }
            if (!k.IsValid)
            {
                if (!_growable && _count >= _admissionLimit)
                {
                    TrackProbe(probe);
                    return ref Unsafe.NullRef<TValue>();
                }
                if (_growable && _count + 1 > t.Slots * MaxLoadFactor)
                {
                    Resize();
                    return ref GetOrAdd(key, in value);
                }

                values[i] = value;   // ★ 先值
                keys[i] = key;       // ★ 后键（发布序——读者见键即见值）
                _count++;
                TrackProbe(probe);
                return ref values[i];
            }
            i = (int)((uint)i + 1 & mask);
            probe++;
        }
    }

    /// <summary>upsert：命中覆写值；未命中同 <see cref="GetOrAdd"/>（定容满且不存在=静默丢弃）。</summary>
    internal void Upsert(LogicalAddress key, in TValue value)
    {
        ref var slot = ref GetOrAdd(key, in value);
        if (Unsafe.IsNullRef(ref slot))
            return;
        slot = value;   // 新写入槽已持 value（幂等）；命中既有槽则此为覆写
    }

    /// <summary>全清：键数组必须显式回 <see cref="LogicalAddress.Invalid"/>——零填充=(0,0,0)=Empty=合法键。</summary>
    internal void Clear()
    {
        if (_count == 0)
            return;
        Array.Fill(_tables.Keys, LogicalAddress.Invalid);
        _count = 0;
    }

    private void Resize()
    {
        var old = _tables;
        int newSlots = old.Slots * 2;
        var @new = new Tables(CreateInvalidKeyArray(newSlots), new TValue[newSlots]);
        _tables = @new;   // 原子换表——新读者即刻见新表，旧读者持旧表快照继续探测

        var oldKeys = old.Keys;
        var oldValues = old.Values;
        for (int j = 0; j < oldKeys.Length; j++)
        {
            var key = oldKeys[j];
            if (key.IsValid)
                InsertFresh(@new, key, oldValues[j]);
        }
    }

    /// <summary>重散列直插（重散列上下文键必不存在、空间必充足——不走准入/生长分支）。</summary>
    private void InsertFresh(Tables t, LogicalAddress key, TValue value)
    {
        var keys = t.Keys;
        uint mask = (uint)(keys.Length - 1);
        int i = SlotOf(key, keys.Length);
        while (keys[i].IsValid)
            i = (int)((uint)i + 1 & mask);
        t.Values[i] = value;
        keys[i] = key;
    }

    /// <summary>Fibonacci 高位散列——段号/偏移各自乘大奇常数异或，取高 log2(slots) 位
    /// （节点地址按 NodeSize 对齐致低位同构，高位取 Index 抗聚类）。</summary>
    private static int SlotOf(LogicalAddress key, int slots)
    {
        int shift = 64 - BitOperations.TrailingZeroCount((uint)slots);
        ulong h = ((ulong)(uint)key.SegId * 0x9E3779B97F4A7C15UL)
                  ^ ((ulong)key.Offset * 0xC2B2AE3D27D4EB4FUL);
        return (int)(h >> shift);
    }

    private static LogicalAddress[] CreateInvalidKeyArray(int slots)
    {
        var keys = new LogicalAddress[slots];
        Array.Fill(keys, LogicalAddress.Invalid);
        return keys;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void TrackProbe(int probe)
    {
#if DEBUG
        if (probe > MaxProbeLength)
            MaxProbeLength = probe;
#endif
    }
}
