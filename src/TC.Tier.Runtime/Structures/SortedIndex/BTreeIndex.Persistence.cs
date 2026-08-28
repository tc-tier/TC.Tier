using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TC.Tier.Runtime.Structures.SortedIndex;

/// <summary>
/// BTreeIndex 主存储格式布局 partial——机制归基类（<see cref="SortedIndexBase{TKey}.TryDump"/> 编排/
/// 版本链/后台循环/帧走链），本件只实现格式：32B 几何 + 物化。
/// <para>★ 帧体布局：[几何 32B]——rootAddr(16) entryCount(8) structSize(4) pad(4)。
///   节点本就写时持久化在自持引擎（节点变更即 WriteNodeContent，引擎副本恒完整）——
///   帧不写节点流，物化=设根 + 引擎读回根节点，零结构调整（无分裂/无重放重建）。</para>
/// <para>★ 正确性（对齐 HashIndex 同构）：dump 表覆盖 [?, W] 完整折叠；> W 混入条目靠重放 (W, End]
///   幂等收敛（帧内 root 恒为 dump 时刻最新根——fuzzy 无逐槽拷贝，几何原子三字段）。</para>
/// </summary>
public partial class BTreeIndex<TKey> where TKey : unmanaged, IEquatable<TKey>
{
    private const int PersistGeometrySize = SortedIndexConstants.GeometrySize;   // 32B

    // ════════════════════════════════════════════════════════════
    // === 子类钩子实现（格式布局）===
    // ════════════════════════════════════════════════════════════

    /// <summary>体长（32B 几何——头 BodyLength 字段，写头时先知）。</summary>
    /// <returns>几何体字节长。</returns>
    protected override long ComputeBodyLength() => PersistGeometrySize;

    /// <summary>写体（32B 几何——脏节点批量写回引擎 + rootAddr/entryCount/structSize 布局自检；分片经 WriteBodyChunk）。</summary>
    protected override void WriteBody()
    {
        // ★ 脏节点批量写回（内容变更延迟到 dump——插入/分裂路径零引擎写；物化前引擎副本完整；
        //   写中断=帧 CRC 不过=fail-safe 全量重放，无中间态风险）。
        //   取值不变式：脏节点必在驻留缓存（WriteNodeContent 前 RefreshCache/根特例）
        foreach (var addr in _dirtyNodes)
        {
            if (addr == _rootAddress)
            {
                WriteNodeContentNow(addr, _cachedRoot);
                continue;
            }
            ref readonly var hit = ref _nodeCache.Find(addr);
            if (!Unsafe.IsNullRef(in hit))
                WriteNodeContentNow(addr, hit);
        }
        _dirtyNodes.Clear();

        // 几何（32B——root/计数/布局自检，恢复直接物化）
        Span<byte> geo = stackalloc byte[PersistGeometrySize];
        MemoryMarshal.Write(geo, in _rootAddress);
        BinaryPrimitives.WriteInt64LittleEndian(geo.Slice(16), Volatile.Read(ref _entryCount));
        BinaryPrimitives.WriteInt32LittleEndian(geo.Slice(24), Unsafe.SizeOf<BTreeNode>());
        WriteBodyChunk(geo);
    }

    // ════════════════════════════════════════════════════════════
    // === 帧物化（基类帧走链定位后调——读几何 → 设根+引擎读回根节点 → 计数）===
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 物化锚点帧（读几何 → 设根 + 引擎读回根节点 + 计数；recountNeeded 时全树实收重数）。
    /// </summary>
    /// <param name="head">帧锚点地址（MinAddress 锚点槽）。</param>
    /// <param name="recountNeeded">重放窗口非空（W&lt;End）——dump 后插入混入物化树才需实收重数；false 时几何计数直接可信。</param>
    /// <param name="entryCount">输出：物化后条目数。</param>
    /// <returns>true = 物化成功；false = 帧内容不符（structSize 不符/几何越界/根空——走 fail-safe 全量重放）。</returns>
    protected override bool TryMaterializeFrame(LogicalAddress head, bool recountNeeded, out long entryCount)
    {
        entryCount = 0;
        int headerSize = _codec.HeaderSize;
        Span<byte> hdr = stackalloc byte[headerSize];
        if (_engine.Read(head, hdr) < headerSize) return false;
        if (!_codec.TryReadHeader(hdr, out var bodyLen)) return false;   // 格式全校验归 codec

        Span<byte> geo = stackalloc byte[PersistGeometrySize];
        if (_engine.Read(_engine.CalculationAddress(head, headerSize), geo) < PersistGeometrySize) return false;
        var rootAddr = MemoryMarshal.Read<LogicalAddress>(geo);
        long count = BinaryPrimitives.ReadInt64LittleEndian(geo.Slice(16));
        int structSize = BinaryPrimitives.ReadInt32LittleEndian(geo.Slice(24));
        if (structSize != Unsafe.SizeOf<BTreeNode>()) return false;   // TKey 布局不符=别的流
        if (count < 0) return false;
        if (bodyLen != PersistGeometrySize) return false;
        if (rootAddr == LogicalAddress.Empty) return false;           // 空树无帧（fail-safe 兜底）

        // ★ 物化=设根 + 引擎读回根节点（节点写时持久化——引擎副本恒完整，缓存只驻根起步）
        var root = ReadNodeContent(rootAddr);
        _rootAddress = rootAddr;
        _cachedRoot = root;
        _nodeCache.Clear();
        _nodeCache.Upsert(rootAddr, in root);

        // ★ 重数实收（fuzzy 帧：dump 后插入覆写节点、可能混入物化树——计数以实收为准，对齐 HashIndex）；
        //   W==End 零增量无混入 → 几何计数直接可信（跳过 O(n) 遍历）
        entryCount = recountNeeded ? CountEntries() : count;
        return true;
    }

    /// <summary>物化后回调（基类 TryApplyMainStorage 物化成功后调）——设置写者条目计数。</summary>
    /// <param name="entryCount">物化实收条目数。</param>
    protected override void OnMaterialized(long entryCount) => _entryCount = entryCount;

    /// <summary>当前条目数（后台 dump 策略触发用——Volatile 读）。</summary>
    protected override long CurrentEntryCount => Volatile.Read(ref _entryCount);
}
