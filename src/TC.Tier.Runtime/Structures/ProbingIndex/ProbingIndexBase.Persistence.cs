using System.Buffers.Binary;
using System.IO.Hashing;
using TC.Tier.Core.Primitives;
using TC.Tier.Core.Shared;

namespace TC.Tier.Runtime.Structures.ProbingIndex;

/// <summary>
/// 探测族主存储机制 partial——基类=机制容器（铁律 10，对齐 LogBase/RingBase/MirrorBase）：
/// 后台 dump 编排 + 版本链 N 版轮替 + 帧走链恢复载入 + 策略触发全在基类；子类只实现格式布局
/// （几何 + 桶区/溢出池逐槽拷贝 + 物化——三个抽象钩子）。
/// <para>★ 帧格式（设计稿 V2/index-persistence-evolution-design.md，codec 归子类注入）：
///   [头 headerSize][体 bodyLength][尾 footerSize] 紧邻连续；头=magic/版本/kind/体长（写头时已知），
///   尾=magic/W/CRC64（覆盖头+体+尾前缀）。帧长可推导 → 版本链免链指针，恢复帧走链 CRC 总验收。
///   格式细节（magic 值/字段布局/CRC 偏移）全在 <see cref="IProbingIndexCodec"/> 实现——基类零格式知识。</para>
/// <para>★ W = <see cref="IKeyResolver{TKey}.GetFlushedWatermark"/>（已落盘水位——组合层契约：
///   Insert 先于落盘、失败回滚，已落盘必已入索引；在途=未落盘=崩溃即失）。</para>
/// </summary>
public abstract partial class ProbingIndexBase<TKey>
    where TKey : unmanaged, IEquatable<TKey>
{
    // === 格式契约（子类构造期注入——codec 按结构名律，对齐 LogCodec/RingCodec）===
    private protected readonly IProbingIndexCodec ProbingIndexCodec;
    private protected ProbingIndexPersistencePolicy PersistencePolicy => _settings.PersistencePolicy;

    /// <summary>体分片（帧写/CRC/读共用——零分配流式）。</summary>
    protected const int PersistBodyChunk = 32 * 1024;

    // === 版本链账本（dump worker 单写者 + 恢复核心——启动前无并发）===
    private readonly List<LogicalAddress> _frameStarts = new();

    // === 后台落盘循环状态 ===
    private BackgroundWorkerLoop? _dumpWorker;
    private long _lastDumpTick;
    private long _entryCountAtLastDump;

    // ════════════════════════════════════════════════════════════
    // === 子类钩子（格式布局——机制归基类）===
    // ════════════════════════════════════════════════════════════

    /// <summary>体长（头 BodyLength 字段——写头时先知：几何 + 结构内容）。</summary>
    protected abstract long ComputeBodyLength();

    /// <summary>写体内容（几何 + 结构内容全在体内——fuzzy 逐槽拷贝归子类，分片经 <see cref="WriteBodyChunk"/>）。</summary>
    protected abstract void WriteBody();

    /// <summary>
    /// 物化最新完整帧（读几何 → 重建结构 → 重数实收）。返回 true 且 <paramref name="entryCount"/>
    /// 给出物化后条目数（fuzzy 帧实收为准）。
    /// </summary>
    protected abstract bool TryMaterializeFrame(LogicalAddress head, out long entryCount);

    /// <summary>当前条目数（后台策略触发用）。</summary>
    protected abstract long CurrentEntryCount { get; }

    // ════════════════════════════════════════════════════════════
    // === 帧写原语（子类 WriteBody 内经 WriteBodyChunk 分片调用）===
    // ════════════════════════════════════════════════════════════

    private LogicalAddress _frameHead;
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

    private sealed class ProbingIndexDumpWorker(ProbingIndexBase<TKey> owner) : BackgroundWorkerLoop(null, 1, "ProbingIndexDumpWorker")
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
        if (_settings.PersistenceKind != ProbingIndexPersistenceKind.Builtin) return;
        _lastDumpTick = Environment.TickCount64;   // ★ 首周期不立即触发（时间阈值从就绪起算）
        _dumpWorker = new ProbingIndexDumpWorker(this);
        ConfigureBackgroundWorker(_dumpWorker);
    }

    /// <summary>策略判定 + 触发（时间间隔 / 条目增量水位阈值，任一命中）。</summary>
    internal void TryDumpIfDue()
    {
        if (_settings.PersistenceKind != ProbingIndexPersistenceKind.Builtin) return;
        var policy = _settings.PersistencePolicy;
        var elapsed = TimeSpan.FromMilliseconds(Environment.TickCount64 - _lastDumpTick);
        long delta = CurrentEntryCount - Volatile.Read(ref _entryCountAtLastDump);
        if (policy.IsTriggered(elapsed, delta))
            TryDump();
    }

    // ════════════════════════════════════════════════════════════
    // === dump 编排（帧三拍——头/体/尾 + 版本链 + 轮替，机制归基类）===
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 快照当前结构为一代主存储帧（后台循环触发；测试可直调）。
    /// <para>★ W = KeyResolver 已落盘水位（组合层契约保证 ≤ W 记录必已入索引）。</para>
    /// </summary>
    internal bool TryDump()
    {
        ThrowIfDisposed();
        if (!IsReady) return false;
        if (_settings.PersistenceKind != ProbingIndexPersistenceKind.Builtin) return false;

        var W = KeyResolver.GetFlushedWatermark();            // ★ 已落盘水位锚点
        long bodyLen = ComputeBodyLength();
        int headerSize = ProbingIndexCodec.HeaderSize;
        int footerSize = ProbingIndexCodec.FooterSize;

        // ★ 引擎模式 A（MirrorBase 同构）：Allocate 随写随留 + Write 复写——帧间零间隙、帧长任意边界
        _frameHead = _engine.Allocate(headerSize + bodyLen + footerSize).Start;
        _frameWriteEnd = _frameHead;
        var crc = UnifiedCrc.CreateCrc64();

        // 头（写头时体长已知——帧长可推导的格式事实；magic/version/kind 由 codec 自填）
        Span<byte> hdr = stackalloc byte[headerSize];
        ProbingIndexCodec.WriteHeader(hdr, bodyLen);
        _engine.Write(_frameWriteEnd, hdr);
        crc.Append(hdr);
        _frameWriteEnd = _engine.CalculationAddress(_frameWriteEnd, headerSize);
        _frameCrc = crc;

        // 体（子类格式布局：几何 + 桶区/溢出池逐槽拷贝）
        WriteBody();

        // 尾（W + CRC64 总验收——覆盖头+体+尾前缀 [0, FooterCrcOffset)；magic 由 codec 自填）
        Span<byte> ftr = stackalloc byte[footerSize];
        ProbingIndexCodec.WriteFooter(ftr, W);
        crc.Append(ftr[..ProbingIndexCodec.FooterCrcOffset]);
        BinaryPrimitives.WriteUInt64LittleEndian(ftr.Slice(ProbingIndexCodec.FooterCrcOffset), UnifiedCrc.FinalizeCrc64(crc));
        _engine.Write(_frameWriteEnd, ftr);
        _frameCrc = null;

        _engine.Flush();                                      // 帧整体落盘（帧完整才可见——写尾中断=CRC 不过=无效帧）
        _frameStarts.Add(_frameHead);
        RotateVersions();
        Volatile.Write(ref _entryCountAtLastDump, CurrentEntryCount);
        _lastDumpTick = Environment.TickCount64;
        return true;
    }

    // ════════════════════════════════════════════════════════════
    // === 版本链轮替（N 版保留——ReclaimHead 回收最老帧）===
    // ════════════════════════════════════════════════════════════

    private void RotateVersions()
    {
        int keep = Math.Max(1, _settings.PersistenceKeepVersions);
        while (_frameStarts.Count > keep)
        {
            var oldest = _frameStarts[0];
            if (oldest > _engine.MinAddress)
                _engine.ReclaimHead(oldest);    // 释放 [MinAddress, oldest)——最老帧之前全回收
            _frameStarts.RemoveAt(0);
        }
    }

    // ════════════════════════════════════════════════════════════
    // === 恢复载入（统一生命周期三级回退中间级——Recovery.cs 调用）===
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// ★ 主存储载入（RecoveryBase 模板三级回退中间级——替代旧镜像分支）：
    /// 帧走链扫描选最新完整帧（CRC 总验收）→ W ∈ [Begin, End] 校验 → 子类物化。
    /// <para>★ false → 恢复核心走 InitializeIndex + Ring 全量重放 fail-safe（不变量不变）。</para>
    /// </summary>
    internal bool TryApplyMainStorage(LogicalAddress replayBegin, LogicalAddress replayEnd, out LogicalAddress effectiveW)
    {
        effectiveW = LogicalAddress.Invalid;
        if (_settings.PersistenceKind != ProbingIndexPersistenceKind.Builtin) return false;
        if (!ScanFrames(out var newestHead, out var newestW)) return false;
        if (newestW < replayBegin || newestW > replayEnd) return false;   // W 越界

        if (!TryMaterializeFrame(newestHead, out var entryCount)) return false;
        OnMaterialized(entryCount);
        effectiveW = newestW;
        return true;
    }

    /// <summary>物化后回调（子类设置条目计数——fuzzy 帧实收为准）。</summary>
    protected virtual void OnMaterialized(long entryCount) { }

    /// <summary>
    /// 前向逐帧走链扫描：从引擎 MinAddress 起，头（magic/version/kind）+ 帧长推导 + 尾 magic + CRC 总验收；
    /// 写中断的尾帧 CRC 不过即停——最新完整帧 = 最后一个通过验收的帧。账本同时重建（轮替用）。
    /// </summary>
    private bool ScanFrames(out LogicalAddress newestHead, out LogicalAddress newestW)
    {
        newestHead = LogicalAddress.Invalid;
        newestW = LogicalAddress.Invalid;
        _frameStarts.Clear();

        LogicalAddress cursor = _engine.MinAddress;
        int headerSize = ProbingIndexCodec.HeaderSize;
        int footerSize = ProbingIndexCodec.FooterSize;
        Span<byte> hdr = stackalloc byte[headerSize];
        Span<byte> ftr = stackalloc byte[footerSize];

        while (cursor < _engine.CommittedTail)
        {
            if (_engine.Read(cursor, hdr) < headerSize) break;
            if (!ProbingIndexCodec.TryReadHeader(hdr, out var bodyLen)) break;   // 格式全校验归 codec

            var footerAt = _engine.CalculationAddress(cursor, headerSize + bodyLen);
            if (footerAt > _engine.CommittedTail) break;                 // 体越界（写中断）
            if (_engine.Read(footerAt, ftr) < footerSize) break;
            if (!ProbingIndexCodec.TryReadFooter(ftr, out var w, out var crcStored)) break;

            // ★ CRC 总验收（头+体+尾前缀）——假 magic 提名由 CRC 裁决
            var crc = UnifiedCrc.CreateCrc64();
            crc.Append(hdr);
            if (!AppendFrameBodyCrc(crc, _engine.CalculationAddress(cursor, headerSize), bodyLen)) break;
            crc.Append(ftr[..ProbingIndexCodec.FooterCrcOffset]);
            if (UnifiedCrc.FinalizeCrc64(crc) != crcStored) break;       // 尾帧写中断——停，取前帧

            newestHead = cursor;
            newestW = w;
            _frameStarts.Add(cursor);
            cursor = _engine.CalculationAddress(cursor, headerSize + bodyLen + footerSize);
        }
        return newestHead.IsValid;
    }

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
}
