using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TC.Tier.Runtime.Structures.SortedIndex;

/// <summary>
/// SkipListIndex 主存储格式布局 partial——机制归基类（<see cref="SortedIndexBase{TKey}.TryDump"/> 编排/
/// 版本链/后台循环/帧走链），本件只实现格式：32B 几何 + 物化。
/// <para>★ 帧体布局：[几何 32B]——currentLevel(4) pad(4) entryCount(8) headAddr(16)。
///   节点本就写时持久化在自持引擎（节点变更即 WriteNodeToEngine）——帧不写节点流，
///   物化=head arena 落位 + 塔顶锚点 + 计数；层 0 链节点访问经 GetNode 引擎读回自愈。</para>
/// </summary>
public partial class SkipListIndex<TKey> where TKey : unmanaged, IEquatable<TKey>
{
    private const int PersistGeometrySize = SortedIndexConstants.GeometrySize;   // 32B

    // ════════════════════════════════════════════════════════════
    // === 子类钩子实现（格式布局）===
    // ════════════════════════════════════════════════════════════

    /// <summary>体长（32B 几何——头 BodyLength 字段，写头时先知）。</summary>
    /// <returns>几何体字节长。</returns>
    protected override long ComputeBodyLength() => PersistGeometrySize;

    /// <summary>写体（32B 几何——脏节点批量写回引擎 + currentLevel/entryCount/headAddr 塔顶锚点；分片经 WriteBodyChunk）。</summary>
    protected override unsafe void WriteBody()
    {
        // ★ 脏节点批量写回（链/值变更延迟到 dump——插入路径零额外引擎写；物化前引擎副本完整含链；
        //   写中断=帧 CRC 不过=fail-safe 全量重放，无中间态风险）
        foreach (var addr in _dirtyNodes)
        {
            ref readonly var hit = ref _cachedNodes.Find(addr);
            if (!Unsafe.IsNullRef(in hit))
                WriteNodeToEngine(addr, (byte*)hit);
        }
        _dirtyNodes.Clear();

        // 几何（32B——层/计数/塔顶锚点，恢复直接物化）
        Span<byte> geo = stackalloc byte[PersistGeometrySize];
        BinaryPrimitives.WriteInt32LittleEndian(geo, _currentLevel);
        BinaryPrimitives.WriteInt64LittleEndian(geo.Slice(8), Volatile.Read(ref _entryCount));
        MemoryMarshal.Write(geo.Slice(16), in _headAddress);
        WriteBodyChunk(geo);
    }

    // ════════════════════════════════════════════════════════════
    // === 帧物化（基类帧走链定位后调——读几何 → head arena 落位 → 计数）===
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 物化锚点帧（读几何 → head arena 落位 + 塔顶锚点 + 计数；recountNeeded 时沿层 0 链实收重数）。
    /// </summary>
    /// <param name="head">帧锚点地址（MinAddress 锚点槽）。</param>
    /// <param name="recountNeeded">重放窗口非空（W&lt;End）——dump 后插入混入层 0 链才需实收重数；false 时几何计数直接可信。</param>
    /// <param name="entryCount">输出：物化后条目数。</param>
    /// <returns>true = 物化成功；false = 帧内容不符（头校验/几何越界/head 空——走 fail-safe 全量重放）。</returns>
    protected override unsafe bool TryMaterializeFrame(LogicalAddress head, bool recountNeeded, out long entryCount)
    {
        entryCount = 0;
        int headerSize = _codec.HeaderSize;
        Span<byte> hdr = stackalloc byte[headerSize];
        if (_engine.Read(head, hdr) < headerSize) return false;
        if (!_codec.TryReadHeader(hdr, out var bodyLen)) return false;   // 格式全校验归 codec

        Span<byte> geo = stackalloc byte[PersistGeometrySize];
        if (_engine.Read(_engine.CalculationAddress(head, headerSize), geo) < PersistGeometrySize) return false;
        int currentLevel = BinaryPrimitives.ReadInt32LittleEndian(geo);
        long count = BinaryPrimitives.ReadInt64LittleEndian(geo.Slice(8));
        var headAddr = MemoryMarshal.Read<LogicalAddress>(geo.Slice(16));
        if (currentLevel < 1 || currentLevel > _maxLevel || count < 0) return false;
        if (bodyLen != PersistGeometrySize) return false;
        if (headAddr == LogicalAddress.Empty) return false;           // 空塔无帧（fail-safe 兜底）

        // ★ 物化=head arena 落位 + 塔顶锚点 + 计数（层 0 链访问经 GetNode 引擎读回自愈）
        var headHeader = ReadHeaderFromEngine(headAddr);
        if (headHeader.LevelCount != _maxLevel) return false;         // head 层契约（塔顶按满层建）——settings 不符=别的流
        _headPtr = AdmitNode(headAddr, in headHeader);
        _headAddress = headAddr;
        _currentLevel = currentLevel;

        // ★ 重数实收（fuzzy 帧：dump 后插入混入层 0 链——计数以实收为准，对齐 HashIndex）；
        //   W==End 零增量无混入 → 几何计数直接可信（跳过 O(n) 链遍历）
        if (recountNeeded)
        {
            long actual = 0;
            var next = ReadLevel(_headPtr, 0);
            while (next != LogicalAddress.Empty)
            {
                actual++;
                next = ReadLevel(GetNode(next), 0);
            }
            entryCount = actual;
        }
        else
        {
            entryCount = count;
        }
        return true;
    }

    /// <summary>物化后回调（基类 TryApplyMainStorage 物化成功后调）——设置写者条目计数。</summary>
    /// <param name="entryCount">物化实收条目数。</param>
    protected override void OnMaterialized(long entryCount) => _entryCount = entryCount;

    /// <summary>当前条目数（后台 dump 策略触发用——Volatile 读）。</summary>
    protected override long CurrentEntryCount => Volatile.Read(ref _entryCount);
}
