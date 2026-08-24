using System.Buffers.Binary;
using System.IO.Hashing;

namespace TC.Tier.Runtime.Structures.SortedIndex;

/// <summary>
/// 比较族主存储机制 partial——基类=机制容器（铁律 10，对齐 ProbingIndexBase/LogBase/RingBase/MirrorBase）：
/// 后台 dump 编排 + 固定锚点覆写 + 恢复载入 + 策略触发全在基类；子类只实现格式布局
/// （几何 + 物化——三个抽象钩子）。
/// <para>★ 帧格式（设计稿 V2/index-persistence-evolution-design.md，codec 归子类注入）：
///   [头 headerSize][体 bodyLength][尾 footerSize] 紧邻连续；头=magic/版本/kind/体长（写头时已知），
///   尾=magic/W/CRC64（覆盖头+体+尾前缀）。格式细节（magic 值/字段布局/CRC 偏移）全在
///   <see cref="ISortedIndexCodec"/> 实现——基类零格式知识。</para>
/// <para>★ 固定锚点（区别于探测族帧走链——比较族节点与帧在引擎内混排，走链不成立）：
///   首开时在引擎头部预留 84B 锚点槽（节点分配自然在其后，MinAddress 恒为锚点），
///   dump 覆写锚点（帧格式不变），恢复直读 MinAddress 锚点 + CRC 总验收。
///   写中断=CRC 不过=fail-safe 全量重放（单帧 84B，无版本链需求）。</para>
/// <para>★ 帧体 = 32B 几何（根/head 指针 + 计数 + 结构元信息）——节点本就写时持久化在自持引擎内
///   （节点变更即 WriteNodeContent，引擎副本恒完整），物化只需设根 + 计数，零逐节点流。</para>
/// <para>★ W = <see cref="IKeyResolver{TKey}.GetFlushedWatermark"/>（已落盘水位——组合层契约：
///   Insert 先于落盘、失败回滚，已落盘必已入索引；在途=未落盘=崩溃即失）。</para>
/// </summary>
public abstract partial class SortedIndexBase<TKey>
    where TKey : unmanaged, IEquatable<TKey>
{
    // === 格式契约（子类构造期注入——对齐 MirrorBase `_codec` 律：族实现类 SortedIndexCodec，字段避同名）===
    private protected readonly ISortedIndexCodec _codec;
    private protected SortedIndexPersistencePolicy PersistencePolicy => _settings.PersistencePolicy;

    /// <summary>固定锚点槽地址（首开=MinAddress 预留；重开=旧锚点原位）——dump 覆写/恢复直读。</summary>
    private LogicalAddress _anchorAddress;

    // === 后台落盘循环状态 ===
    private BackgroundWorkerLoop? _dumpWorker;
    private long _lastDumpTick;
    private long _entryCountAtLastDump;

    /// <summary>锚点槽长（头+体+尾——帧长固定，覆写无需轮替）。</summary>
    private int AnchorLength => _codec.HeaderSize + SortedIndexConstants.GeometrySize + _codec.FooterSize;

    /// <summary>
    /// ★ 锚点槽预留（恢复核心在 InitializeIndex 前调用——引擎已就绪、节点未分配）：
    /// 首开空引擎=第一个分配（MinAddress，节点分配自然在其后）；重开=旧锚点原位
    /// （MinAddress 恒为锚点——节点 ReclaimTail 不动 MinAddress）。
    /// </summary>
    private protected void EnsureAnchorReserved()
    {
        if (_settings.PersistenceKind != SortedIndexPersistenceKind.Builtin) return;
        if (_anchorAddress != LogicalAddress.Empty) return;
        _anchorAddress = _engine.MinAddress == _engine.AllocatedTail
            ? _engine.Allocate(AnchorLength).Start
            : _engine.MinAddress;
    }

    // ════════════════════════════════════════════════════════════
    // === 子类钩子（格式布局——机制归基类）===
    // ════════════════════════════════════════════════════════════

    /// <summary>体长（头 BodyLength 字段——写头时先知：几何 + 结构内容）。</summary>
    protected abstract long ComputeBodyLength();

    /// <summary>写体内容（几何 + 结构内容全在体内——比较族=32B 几何，分片经 <see cref="WriteBodyChunk"/>）。</summary>
    protected abstract void WriteBody();

    /// <summary>
    /// 物化锚点帧（读几何 → 重建结构 → 重数实收）。
    /// <paramref name="recountNeeded"/> = 重放窗口非空（W&lt;End）——dump 后插入混入树才需重数；
    /// W==End（零增量）无混入 → 几何计数直接可信，跳过 O(n) 遍历（实测 SkipList 物化 72.9ms 大头）。
    /// 返回 true 且 <paramref name="entryCount"/> 给出物化后条目数。
    /// </summary>
    protected abstract bool TryMaterializeFrame(LogicalAddress head, bool recountNeeded, out long entryCount);

    /// <summary>当前条目数（后台策略触发用）。</summary>
    protected abstract long CurrentEntryCount { get; }

    // ════════════════════════════════════════════════════════════
    // === 帧写原语（子类 WriteBody 内经 WriteBodyChunk 分片调用）===
    // ════════════════════════════════════════════════════════════

    private LogicalAddress _frameWriteEnd;
    private Crc64? _frameCrc;

    /// <summary>写体分片（子类 WriteBody 内调用——CRC 边写边累积，帧长任意边界）。</summary>
    protected void WriteBodyChunk(ReadOnlySpan<byte> chunk)
    {
        if (_frameCrc is null)
            throw new InvalidOperationException("帧未开始（先 TryDump 进入帧写会话）。");
        if (chunk.IsEmpty) return;
        _engine.Write(_frameWriteEnd, chunk);
        _frameCrc.Append(chunk);
        _frameWriteEnd = _engine.CalculationAddress(_frameWriteEnd, chunk.Length);
    }

    // ════════════════════════════════════════════════════════════
    // === 后台落盘循环（BackgroundWorkerLoop——Core 唯一合法后台循环）===
    // ════════════════════════════════════════════════════════════

    private sealed class SortedIndexDumpWorker(SortedIndexBase<TKey> owner) : BackgroundWorkerLoop(null, 1, "SortedIndexDumpWorker")
    {
        protected override async ValueTask<bool> RunOneCycleAsync(CancellationToken ct)
        {
            await Task.Delay(1000, ct).ConfigureAwait(false);   // 1s 轮询粒度（后台低频）
            owner.TryDumpIfDue();
            return true;
        }
    }

    /// <summary>恢复完成 + 装配就绪后启动后台 dump worker（LifecycleBase 编排：Complete 后 Start）。</summary>
    protected override void OnInitializeComplete()
    {
        if (_settings.PersistenceKind != SortedIndexPersistenceKind.Builtin) return;
        _lastDumpTick = Environment.TickCount64;   // ★ 首周期不立即触发（时间阈值从就绪起算）
        _dumpWorker = new SortedIndexDumpWorker(this);
        ConfigureBackgroundWorker(_dumpWorker);
    }

    /// <summary>策略判定 + 触发（时间间隔 / 条目增量水位阈值，任一命中）。</summary>
    internal void TryDumpIfDue()
    {
        if (_settings.PersistenceKind != SortedIndexPersistenceKind.Builtin) return;
        var policy = _settings.PersistencePolicy;
        var elapsed = TimeSpan.FromMilliseconds(Environment.TickCount64 - _lastDumpTick);
        long delta = CurrentEntryCount - Volatile.Read(ref _entryCountAtLastDump);
        if (policy.IsTriggered(elapsed, delta))
            TryDump();
    }

    // ════════════════════════════════════════════════════════════
    // === dump 编排（帧三拍——头/体/尾覆写固定锚点，机制归基类）===
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 快照当前结构为锚点帧（后台循环触发；测试可直调）。
    /// <para>★ W = KeyResolver 已落盘水位（组合层契约保证 ≤ W 记录必已入索引）。</para>
    /// </summary>
    internal bool TryDump()
    {
        ThrowIfDisposed();
        if (!IsReady) return false;
        if (_settings.PersistenceKind != SortedIndexPersistenceKind.Builtin) return false;
        if (KeyResolver is null) return false;   // ★ 无恢复数据面（比较族判等不需要 resolver）——帧无 W 锚点，跳过

        var W = KeyResolver.GetFlushedWatermark();                   // ★ 已落盘水位锚点
        long bodyLen = ComputeBodyLength();
        int headerSize = _codec.HeaderSize;
        int footerSize = _codec.FooterSize;

        // ★ 覆写固定锚点槽（首开预留/重开原位——节点分配在锚点之后，MinAddress 恒为锚点）
        _frameWriteEnd = _anchorAddress;
        var crc = UnifiedCrc.CreateCrc64();

        // 头（写头时体长已知——帧长可推导的格式事实；magic/version/kind 由 codec 自填）
        Span<byte> hdr = stackalloc byte[headerSize];
        _codec.WriteHeader(hdr, bodyLen);
        _engine.Write(_frameWriteEnd, hdr);
        crc.Append(hdr);
        _frameWriteEnd = _engine.CalculationAddress(_frameWriteEnd, headerSize);
        _frameCrc = crc;

        // 体（子类格式布局：32B 几何——根/head 指针 + 计数）
        WriteBody();

        // 尾（W + CRC64 总验收——覆盖头+体+尾前缀 [0, FooterCrcOffset)；magic 由 codec 自填）
        Span<byte> ftr = stackalloc byte[footerSize];
        _codec.WriteFooter(ftr, W);
        crc.Append(ftr[.._codec.FooterCrcOffset]);
        BinaryPrimitives.WriteUInt64LittleEndian(ftr.Slice(_codec.FooterCrcOffset), UnifiedCrc.FinalizeCrc64(crc));
        _engine.Write(_frameWriteEnd, ftr);
        _frameCrc = null;

        _engine.Flush();                                      // 锚点整体落盘（帧完整才可见——写尾中断=CRC 不过=无效帧）
        Volatile.Write(ref _entryCountAtLastDump, CurrentEntryCount);
        _lastDumpTick = Environment.TickCount64;
        return true;
    }

    // ════════════════════════════════════════════════════════════
    // === 恢复载入（统一生命周期三级回退中间级——Recovery.cs 调用）===
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// ★ 主存储载入（RecoveryBase 模板三级回退中间级——替代旧镜像分支）：
    /// 直读 MinAddress 锚点帧（头校验 + 体 + CRC 总验收）→ W ∈ [Begin, End] 校验 → 子类物化。
    /// <para>★ false → 恢复核心走 InitializeIndex + Ring 全量重放 fail-safe（不变量不变）。</para>
    /// </summary>
    internal bool TryApplyMainStorage(LogicalAddress replayBegin, LogicalAddress replayEnd, out LogicalAddress effectiveW)
    {
        effectiveW = LogicalAddress.Invalid;
        if (_settings.PersistenceKind != SortedIndexPersistenceKind.Builtin) return false;

        // ★ 锚点固定位 = 首开时第一个分配（MinAddress）；无锚点（空引擎首开）= 无帧
        var anchor = _engine.MinAddress;
        if (anchor == _engine.AllocatedTail) return false;   // 空引擎——从未 dump

        int headerSize = _codec.HeaderSize;
        int footerSize = _codec.FooterSize;
        Span<byte> hdr = stackalloc byte[headerSize];
        if (_engine.Read(anchor, hdr) < headerSize) return false;
        if (!_codec.TryReadHeader(hdr, out var bodyLen)) return false;   // 格式全校验归 codec

        // 体（几何）+ 尾 CRC 总验收（头+体+尾前缀）——写中断/损坏 = 无效帧 → fail-safe
        var crc = UnifiedCrc.CreateCrc64();
        crc.Append(hdr);
        var bodyAt = _engine.CalculationAddress(anchor, headerSize);
        if (!AppendFrameBodyCrc(crc, bodyAt, bodyLen)) return false;
        Span<byte> ftr = stackalloc byte[footerSize];
        if (_engine.Read(_engine.CalculationAddress(bodyAt, bodyLen), ftr) < footerSize) return false;
        if (!_codec.TryReadFooter(ftr, out var w, out var crcStored)) return false;
        crc.Append(ftr[.._codec.FooterCrcOffset]);
        if (UnifiedCrc.FinalizeCrc64(crc) != crcStored) return false;
        if (w < replayBegin || w > replayEnd) return false;              // W 越界

        if (!TryMaterializeFrame(anchor, w < replayEnd, out var entryCount)) return false;
        OnMaterialized(entryCount);
        effectiveW = w;
        return true;
    }

    /// <summary>物化后回调（子类设置条目计数）。</summary>
    protected virtual void OnMaterialized(long entryCount) { }

    private bool AppendFrameBodyCrc(Crc64 crc, LogicalAddress at, long bodyLen)
    {
        Span<byte> buf = stackalloc byte[PersistBodyChunk];
        var off = at;
        long remaining = bodyLen;
        while (remaining > 0)
        {
            int n = (int)Math.Min(remaining, buf.Length);
            int got = _engine.Read(off, buf[..n]);
            if (got <= 0) return false;
            crc.Append(buf[..got]);
            off = _engine.CalculationAddress(off, got);
            remaining -= got;
        }
        return true;
    }

    /// <summary>读体分片（子类物化用——整读/分段读 helper）。</summary>
    protected bool ReadBodyChunk(LogicalAddress at, Span<byte> dst, out int got)
    {
        got = _engine.Read(at, dst);
        return got > 0;
    }

    /// <summary>体分片（帧写/CRC/读共用——零分配流式）。</summary>
    protected const int PersistBodyChunk = 32 * 1024;
}
