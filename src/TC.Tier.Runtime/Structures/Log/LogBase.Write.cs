using System.Runtime.CompilerServices;

namespace TC.Tier.Runtime.Structures.Log;

/// <summary>
/// LogBase 写引擎 partial——★ 地址窗口模型（Allocate + CalculationAddress + Write）。
/// <para>★ 心智模型（engine-migration-rewrite.md §3.6/§4.5）：</para>
/// <para>IO 底层 = 持久化的内存。Log 不碰段/对齐/落盘细节，只在引擎地址空间上做 record 格式 + group commit 调度。</para>
/// <para>★ 地址窗口（替代旧 Append 模型）：</para>
/// <para>  1. engine.Allocate(PageSize)     → 上层主动申请地址空间（零 IO，CAS 推水位）</para>
/// <para>  2. engine.CalculationAddress     → 在已分配空间内精确推算 entry 地址（纯逻辑，不 IO）</para>
/// <para>  3. engine.Write(addr, data)      → 在已分配空间内覆写（lease 保护，DIO 对齐）</para>
/// <para>★ 为什么不用 Append：entry 写入时必须返回真实 LogicalAddress 用于索引/Read/恢复，
///   Append 返回的地址在 pwrite 之后才知——无法满足"entry 写入时即知地址"的需求。</para>
/// <para>★ DIO 三重对齐（IOMode.Enabled 下硬责任）：</para>
/// <para>  - fileOffset：窗口起点（Allocate 返回，段内扇区对齐）</para>
/// <para>  - length：AlignUp 到扇区（padding 补零）</para>
/// <para>  - buffer 地址：AlignedMemoryManager（扇区对齐分配）</para>
/// </summary>
public abstract partial class LogBase
{
    // === ★ 地址空间窗口 + 页攒批（Allocate + CalculationAddress + Write 模型）===
    //   三层结构：地址空间窗口（大 Allocate 区间）→ 页（PageSize 攒批单位）→ entry。
    //   页满组装 frame Write 到窗口当前写入位置；窗口剩余放不下一页时 Allocate 新窗口。
    //   frame 在窗口内连续排列 → cursor 顺序读跨 frame 无空洞。
    // ★ 双页 ping-pong（base.md §22）：异步轨 FlushPageAsync 提交当前页 WriteAsync 后不等待，
    //   立即切另一页继续写（IO 重叠）。同步轨 FlushPage 原地复用不变。
    private AlignedMemoryManager? _pageA;        // 页 A 缓冲（扇区对齐，攒 entry）
    private AlignedMemoryManager? _pageB;        // 页 B 缓冲
    private int _pageUsedA;                      // 页 A 已用字节
    private int _pageUsedB;                      // 页 B 已用字节
    private int _activePage;                     // 0=A, 1=B（当前写页）
    private Task? _inFlightFlush;                // 在途 FlushPageAsync 任务（限 1 个，背压）
    /// <summary>★ 写路径锁（2026-08-24 并发安全化——粗锁方案）：Append/Flush/TruncateSuffix 串行化——
    /// 单写者语义完全保持（水位/TailAddress 零改动），并发调用安全（不损坏）；批持锁（批内零锁）。</summary>
    private readonly object _writeLock = new();
    // ★ frame 缓冲池——DIO 要求 frame buffer 指针扇区对齐（ArrayPool/new byte[] 无保证）。
    //   池化的 AlignedMemoryManager 保证 NativeMemory.AlignedAlloc 分配，DIO 三重对齐之一。
    private PinnedBufferPool? _framePool;
    private LogicalAddress _spaceStart;          // 地址空间窗口起点（Allocate 返回）
    private long _spaceWriteOffset;              // 窗口内已写字节（连续 frame 累计）
    private long _spaceCapacity;                 // 窗口总容量（Allocate 大小）
    // ★ Log 自管真实水位线——最后一个已提交 frame 的尾地址（不含 Allocate 预留空洞）。
    //   引擎 AllocatedTail 含 Allocate 预留（空洞），不能做 Log 读回边界。
    //   对外水位（TailAddress）+ cursor 扫描终点 + EntryLog CommittedOffset 初始值 都用此真实水位。
    private LogicalAddress _logicalTail;
    // ★ 是否已 Allocate 窗口——不能用 _spaceStart == LogicalAddress.Empty 判断：
    //   引擎从空地址空间首次 Allocate 返回的起点正是 seg#0@0x0（== Empty），会导致每次 Append
    //   误判"窗口未分配"而重新 Allocate，旧窗口里还没落盘的 entry 被静默丢弃。
    private bool _spaceAllocated;

    // ★ 当前活跃页的 AlignedMemoryManager（非 null 前提：调用方确保已调 InitializeForWrites）。
    private AlignedMemoryManager ActivePage => _activePage == 0 ? _pageA! : _pageB!;
    private int ActivePageUsed
    {
        get => _activePage == 0 ? _pageUsedA : _pageUsedB;
        set { if (_activePage == 0) _pageUsedA = value; else _pageUsedB = value; }
    }

    /// <summary>
    /// ★ 地址空间窗口的 Allocate 大小（性能权衡：大窗口少 Allocate 次数，小窗口省地址空间）。
    /// <para>默认 = PageSize × 16：一个窗口放 16 页 frame，摊薄 Allocate(CAS) 开销。</para>
    /// </summary>
    private long SpaceAllocSize => (long)PageSize * 16;

    /// <summary>★ 当前写游标 = 最后一个已 Append entry 尾地址（含内存页内未 FlushPage 的 entry）。</summary>
    /// <remarks>未初始化写或空页时 = _logicalTail（已落盘水位）。</remarks>
    private LogicalAddress GetCurrentWriteTail()
    {
        if (_pageA is null || ActivePageUsed == 0) return _logicalTail;
        var pageFrameStart = _engine.CalculationAddress(_spaceStart, _spaceWriteOffset);
        return _engine.CalculationAddress(pageFrameStart, LogPageFrameHeaderCodec.StructSize + ActivePageUsed);
    }

    /// <summary>
    /// ★ 初始化写模式：分配页内存，地址空间窗口懒分配（首次 Append 时 Allocate）。
    /// </summary>
    protected void InitializeForWrites()
    {
        EnsureNotDisposed();
        _pageA = new AlignedMemoryManager(PageSize, (int)SectorSize);
        _pageB = new AlignedMemoryManager(PageSize, (int)SectorSize);
        _framePool = new PinnedBufferPool();
        _pageUsedA = 0;
        _pageUsedB = 0;
        _activePage = 0;
        _spaceStart = LogicalAddress.Empty;
        _spaceWriteOffset = 0;
        _spaceCapacity = 0;
        _spaceAllocated = false;
        _logicalTail = LogicalAddress.Empty;
    }

    /// <summary>计算 entry padding 长度（对齐到 codec.Alignment，保证后续 entry 起始对齐）。</summary>
    private int ComputePaddingLength(int payloadLength)
    {
        int contentLen = HeaderSize + payloadLength;
        return contentLen.AlignUp(Alignment) - contentLen;
    }

    // ═══════════════════════════════════════════════════════════════
    // ★ Append — 热路径：Allocate + CalculationAddress + Write
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// ★ 追加单条 entry（同步）：地址窗口模型。
    /// <para>★ 单 entry 不跨页契约：totalSize 必须 ≤ PageSize。</para>
    /// </summary>
    public LogicalAddress Append(ReadOnlySpan<byte> payload) => AppendCore(payload, isMeta: false);

    /// <summary>★ 追加单条 entry 核心（含 meta entry，isMeta=true 供 WriteMetaPayload 用）。</summary>
    private protected LogicalAddress AppendCore(ReadOnlySpan<byte> payload, bool isMeta)
    {
        lock (_writeLock) { return AppendCoreLocked(payload, isMeta); }
    }

    /// <summary>★ 写锁内实现（Monitor 可重入——OnAppended 提前提交链内层调用不阻塞）。</summary>
    private protected LogicalAddress AppendCoreLocked(ReadOnlySpan<byte> payload, bool isMeta)
    {
        EnsureNotDisposed();
        EnsureWriteInitialized();
        EnsureSpaceAllocated();
        int paddingLength = ComputePaddingLength(payload.Length);
        int totalSize = HeaderSize + payload.Length + paddingLength;
        if (totalSize > PageSize)
            throw new InvalidOperationException(
                $"Entry size {totalSize} exceeds page size {PageSize} (Header={HeaderSize} Payload={payload.Length}). " +
                "Single entry MUST fit within one page — split large objects into multiple entries at the caller.");

        if (ActivePageUsed + totalSize > PageSize)
        {
            FlushPage();
        }
        EnsureSpaceForNextPage();

        var pageFrameStart = _engine.CalculationAddress(_spaceStart, _spaceWriteOffset);
        var entryStart = _engine.CalculationAddress(pageFrameStart, LogPageFrameHeaderCodec.StructSize + ActivePageUsed);

        var page = ActivePage.GetSpan(ActivePageUsed, totalSize);
        payload.CopyTo(page[HeaderSize..]);
        page.Slice(HeaderSize + payload.Length, paddingLength).Clear();
        LogCodec.WriteHeader(page, payload.Length, paddingLength, isMeta);
        ActivePageUsed += totalSize;

        if (ActivePageUsed >= PageSize)
        {
            FlushPage();
        }

        OnAppended(entryStart, payload.Length, isMeta);
        return entryStart;
    }

    /// <summary>★ 追加单条 entry（异步轨）。</summary>
    public async ValueTask<LogicalAddress> AppendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default)
        => await AppendCoreAsync(payload, isMeta: false, ct).ConfigureAwait(false);

    private protected async ValueTask<LogicalAddress> AppendCoreAsync(ReadOnlyMemory<byte> payload, bool isMeta, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // ★ 写路径同步锁内完成（无 await 的写段——Monitor 在 async 内不可跨 await 持有；
        //   页满异步轨 FlushPageAsync 的 IO 重叠在此让位于锁串行——并发安全优先）
        return AppendCoreLocked(payload.Span, isMeta);
    }

    /// <summary>同步 helper：在当前页缓冲的 offset 处写入 entry（header + payload）。</summary>
    private void WriteEntryToPageBuffer(int offset, ReadOnlySpan<byte> payload, int payloadLength, int paddingLength, bool isMeta)
    {
        var totalSize = HeaderSize + payloadLength + paddingLength;
        var page = ActivePage.GetSpan(offset, totalSize);
        payload.CopyTo(page[HeaderSize..]);
        page.Slice(HeaderSize + payloadLength, paddingLength).Clear();
        LogCodec.WriteHeader(page, payloadLength, paddingLength, isMeta);
    }

    // ═══════════════════════════════════════════════════════════════
    // Flush 屏障（group commit 落盘用）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Flush 同步屏障：提交当前页（末页）+ engine.Flush（fsync 落盘）。
    /// </summary>
    public void Flush()
    {
        EnsureNotDisposed();
        lock (_writeLock)
        {
#pragma warning disable TCSG031 // 设计必需：同步写/截断 API 契约——返回前数据必须已落盘（_inFlightFlush 等待）
            if (_inFlightFlush is { } task) { task.GetAwaiter().GetResult(); _inFlightFlush = null; }
#pragma warning restore TCSG031

            if (ActivePageUsed > 0) FlushPage();
        }
        _engine.Flush();
    }

    /// <summary>Flush 异步屏障。</summary>
    public async ValueTask FlushAsync(CancellationToken ct = default)
    {
        EnsureNotDisposed();
        lock (_writeLock)
        {
#pragma warning disable TCSG031 // 设计必需：同步写/截断 API 契约——返回前数据必须已落盘（_inFlightFlush 等待）
            if (_inFlightFlush is { } task) { task.GetAwaiter().GetResult(); _inFlightFlush = null; }
#pragma warning restore TCSG031

            if (ActivePageUsed > 0) FlushPage();
        }
        ct.ThrowIfCancellationRequested();
        _engine.Flush();
    }

    /// <summary>★ 范围 flush（group commit / 2PC Prepare 用）。</summary>
    protected void FlushUntil(LogicalAddress untilAddress)
    {
        EnsureNotDisposed();
        lock (_writeLock)
        {
#pragma warning disable TCSG031 // 设计必需：同步写/截断 API 契约——返回前数据必须已落盘（_inFlightFlush 等待）
            if (_inFlightFlush is { } task) { task.GetAwaiter().GetResult(); _inFlightFlush = null; }
#pragma warning restore TCSG031

            if (ActivePageUsed > 0) FlushPage();
        }
        _engine.Flush(untilAddress);
    }

    /// <summary>★ 范围 flush（异步轨）。</summary>
    protected async ValueTask FlushUntilAsync(LogicalAddress untilAddress, CancellationToken ct = default)
    {
        EnsureNotDisposed();
        lock (_writeLock)
        {
#pragma warning disable TCSG031 // 设计必需：同步写/截断 API 契约——返回前数据必须已落盘（_inFlightFlush 等待）
            if (_inFlightFlush is { } task) { task.GetAwaiter().GetResult(); _inFlightFlush = null; }
#pragma warning restore TCSG031

            if (ActivePageUsed > 0) FlushPage();
        }
        ct.ThrowIfCancellationRequested();
        _engine.Flush(untilAddress);
    }

    // ═══════════════════════════════════════════════════════════════
    // 模板方法钩子
    // ═══════════════════════════════════════════════════════════════

    /// <summary>每次 entry 追加完成后调用。</summary>
    protected virtual void OnAppended(LogicalAddress entryAddress, int payloadLength, bool isMeta) { }

    /// <summary>★ 窗口提交引擎完成后调用（数据已 Write 到 CommittedTail）。</summary>
    protected virtual void OnPageFlushed(LogicalAddress committedTail) { }

    /// <summary>★ 窗口提交引擎完成后调用（异步轨）。</summary>
    protected virtual ValueTask OnPageFlushedAsync(LogicalAddress committedTail)
    {
        OnPageFlushed(committedTail);
        return ValueTask.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════
    // FlushPage — ★ 提交当前页为一个 PageFrame，Write 到地址空间窗口当前位置
    // ═══════════════════════════════════════════════════════════════

    private static readonly int PageFrameFooterSize = Crc32FooterCodec.StructSize;

    /// <summary>
    /// PageFrame 外壳开销 = header(8B) + footer(4B) = 12B（非扇区对齐，破坏 DIO）。
    /// </summary>
    private static int FrameHeaderFooterOverhead => LogPageFrameHeaderCodec.StructSize + PageFrameFooterSize;

    /// <summary>
    /// ★ 计算 frame 尾部补零字节数——把整个 frame（header+data+footer）撑到扇区对齐。
    /// <para>dataLen 为 frame 数据区长度（写入侧 = alignedLen（扇区对齐或 capped 到 PageSize），
    ///   读侧 = frame header.DataLength）。</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ComputeFramePadding(int dataLen)
    {
        var totalWithoutPadding = FrameHeaderFooterOverhead + dataLen;
        var mod = totalWithoutPadding % (int)SectorSize;
        return mod == 0 ? 0 : (int)SectorSize - mod;
    }

    /// <summary>★ 扇区对齐后的完整 frame 字节数（含 header + dataLen + footer + padding）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int PaddedFrameSize(int dataLen)
        => LogPageFrameHeaderCodec.StructSize + dataLen + PageFrameFooterSize + ComputeFramePadding(dataLen);

    /// <summary>
    /// ★ 提交当前页：组装 PageFrame（[header][data:实际用字节扇区对齐][crc]）→ engine.Write 到地址空间窗口当前位置。
    /// <para>★ 变长页提交（engine-migration §4.5 / perf §9.0）：数据区 = AlignUp(_pageUsed, SectorSize)，
    ///   不满页只写实际数据扇区对齐字节，不填零整页。</para>
    /// <para>★ frame 写到窗口内连续位置 _spaceStart + _spaceWriteOffset，推进 _spaceWriteOffset。
    ///   多页 frame 在窗口内连续排列 → cursor 顺序读跨 frame 无空洞。</para>
    /// <para>★ engine.Write（非 Append）：地址由 Allocate 预分配，Write 不推进 CommittedTail，只覆写预留区。</para>
    /// </summary>
    private void FlushPage()
    {
#pragma warning disable TCSG031 // 设计必需：同步写/截断 API 契约——返回前数据必须已落盘（_inFlightFlush 等待）
        if (_inFlightFlush is { } task) { task.GetAwaiter().GetResult(); _inFlightFlush = null; }
#pragma warning restore TCSG031

        if (_pageA is null || ActivePageUsed == 0) return;

        int used = ActivePageUsed;
        int alignedLen = used.AlignUp((int)SectorSize);
        if (alignedLen > PageSize) alignedLen = PageSize;

        int frameSize = PaddedFrameSize(alignedLen);
        int paddingSize = frameSize - (LogPageFrameHeaderCodec.StructSize + alignedLen + PageFrameFooterSize);

        // ★ 确保窗口剩余空间够放这个 frame（Flush/FlushOnDispose 直接调 FlushPage 不走 AppendCore 流水线）
        EnsureSpaceForNextPage(frameSize);

        var frameAddr = _engine.CalculationAddress(_spaceStart, _spaceWriteOffset);

        // ★ 对齐取 DIO 地板而非卷扇区——Windows DIO 缓冲地址须 max(扇区, 4096) 对齐（同 Ring 页池）
        var frameMem = _framePool!.RentAligned(frameSize, TC.Tier.Core.IO.DirectIo.BufferAlignmentFloor((int)SectorSize));
        try
        {
            Span<byte> frame = frameMem.GetSpan(0, frameSize);

            var hdr = new LogPageFrameHeader { MagicValue = RecordMagic.LogPageFrame, DataLength = alignedLen };
            LogPageFrameHeaderCodec.Write(frame[..LogPageFrameHeaderCodec.StructSize], in hdr, validate: true);
            ActivePage.GetSpan(0, alignedLen).CopyTo(frame.Slice(LogPageFrameHeaderCodec.StructSize, alignedLen));
            var crc = UnifiedCrc.ComputeCrc32C(frame[..(LogPageFrameHeaderCodec.StructSize + alignedLen)]);
            Crc32FooterCodec.Write(frame.Slice(LogPageFrameHeaderCodec.StructSize + alignedLen, PageFrameFooterSize), new Crc32Footer { Crc = crc });
            if (paddingSize > 0)
                frame.Slice(LogPageFrameHeaderCodec.StructSize + alignedLen + PageFrameFooterSize, paddingSize).Clear();

            _engine.Write(frameAddr, frame);
        }
        finally
        {
            _framePool.ReturnAligned(frameMem);
        }

        _spaceWriteOffset += frameSize;
        ActivePageUsed = 0;
        ActivePage.GetSpan(0, PageSize).Clear();

        _logicalTail = _engine.CalculationAddress(frameAddr, LogPageFrameHeaderCodec.StructSize + alignedLen + PageFrameFooterSize);

        OnPageFlushed(_logicalTail);
    }

    /// <summary>
    /// ★ 异步提交当前页：双页 ping-pong。
    /// <para>组帧当前页 → 先行推进地址（_spaceWriteOffset/_logicalTail，已知 frame 尺寸可提前算）
    /// → 启动 engine.WriteAsync（不 await）→ 立即切另一页继续写（IO 重叠）。</para>
    /// <para>背压：上一帧 WriteAsync 未完成时 await 等待（限在途 IO=1）。</para>
    /// </summary>
    private async ValueTask FlushPageAsync(CancellationToken ct = default)
    {
        if (_pageA is null || ActivePageUsed == 0) return;

        // Backpressure: await previous in-flight flush before starting new one
        if (_inFlightFlush is { } task)
        {
            await task.ConfigureAwait(false);
            _inFlightFlush = null;
        }

        int used = ActivePageUsed;
        int alignedLen = used.AlignUp((int)SectorSize);
        if (alignedLen > PageSize) alignedLen = PageSize;

        int frameSize = PaddedFrameSize(alignedLen);
        int paddingSize = frameSize - (LogPageFrameHeaderCodec.StructSize + alignedLen + PageFrameFooterSize);

        EnsureSpaceForNextPage(frameSize);

        var frameAddr = _engine.CalculationAddress(_spaceStart, _spaceWriteOffset);

        var frameMem = BuildPageFrame(alignedLen, frameSize, paddingSize);

        // ★ 先行推进地址（frame 尺寸已知，不影响 I/O 正确性）——确保后续 Append 计算地址不重叠
        var oldWriteOffset = _spaceWriteOffset;   // ★ 保存旧值用于写失败回滚（#224：连同 _logicalTail 一起回滚）
        _spaceWriteOffset += frameSize;
        // ★ 保存旧水位用于写失败回滚（#109：防止 WriteAsync 失败时 _logicalTail 越过持久化位置）
        var oldTail = _logicalTail;
        _logicalTail = _engine.CalculationAddress(frameAddr,
            LogPageFrameHeaderCodec.StructSize + alignedLen + PageFrameFooterSize);
        var committedTail = _logicalTail;

        int flushedPageIndex = _activePage;
        _inFlightFlush = CompleteFlushAsync(frameMem, frameAddr, frameSize, flushedPageIndex, committedTail, oldTail, oldWriteOffset, ct);

        // Switch to the other page immediately — IO overlap
        _activePage = 1 - _activePage;
        ActivePageUsed = 0;
    }

    /// <summary>
    /// ★ 在途 flush 完成回调：等待 WriteAsync 完成 → 归还 frame buffer → 清理已写页（已安全：该页已非 active）→ 通知提交。
    /// <para>地址状态（_spaceWriteOffset/_logicalTail）已由 FlushPageAsync 先行推进，回调不重复推进。</para>
    /// </summary>
    private async Task CompleteFlushAsync(
        AlignedMemoryManager frameMem,
        LogicalAddress frameAddr,
        int frameSize,
        int flushedPageIndex,
        LogicalAddress committedTail,
        LogicalAddress oldTail,
        long oldWriteOffset,
        CancellationToken ct)
    {
        try
        {
            await _engine.WriteAsync(frameAddr, frameMem.Memory[..frameSize], ct).ConfigureAwait(false);
        }
        catch
        {
            // ★ WriteAsync 失败时回滚先行推进的水位（#109/#224：两个都退回，防止地址错乱）
            //   - _logicalTail 退回 oldTail（#109：水位不越过持久化位置）
            //   - _spaceWriteOffset 退回 oldWriteOffset（#224：否则 EnsureSpaceForNextPage/
            //     GetCurrentWriteTail 算错地址，后续 Append 覆盖未持久化区域）
            _logicalTail = oldTail;
            _spaceWriteOffset = oldWriteOffset;
            throw;
        }
        finally
        {
            _framePool!.ReturnAligned(frameMem);
        }

        var flushedPage = flushedPageIndex == 0 ? _pageA : _pageB;
        flushedPage!.GetSpan(0, PageSize).Clear();

        await OnPageFlushedAsync(committedTail).ConfigureAwait(false);
    }

    /// <summary>★ 构建 PageFrame（扇区对齐的 AlignedMemoryManager，异步轨用）。变长数据区 + padding。</summary>
    /// <remarks>调用方负责通过 <c>_framePool.ReturnAligned</c> 归还返回的 buffer。</remarks>
    private AlignedMemoryManager BuildPageFrame(int alignedLen, int frameSize, int paddingSize)
    {
        // ★ 对齐取 DIO 地板而非卷扇区——Windows DIO 缓冲地址须 max(扇区, 4096) 对齐（同 Ring 页池）
        var frameMem = _framePool!.RentAligned(frameSize, TC.Tier.Core.IO.DirectIo.BufferAlignmentFloor((int)SectorSize));
        var frameSpan = frameMem.GetSpan(0, frameSize);

        var hdr = new LogPageFrameHeader { MagicValue = RecordMagic.LogPageFrame, DataLength = alignedLen };
        LogPageFrameHeaderCodec.Write(frameSpan[..LogPageFrameHeaderCodec.StructSize], in hdr, validate: true);
        ActivePage.GetSpan(0, alignedLen).CopyTo(frameSpan.Slice(LogPageFrameHeaderCodec.StructSize, alignedLen));
        uint crc = UnifiedCrc.ComputeCrc32C(frameSpan[..(LogPageFrameHeaderCodec.StructSize + alignedLen)]);
        Crc32FooterCodec.Write(frameSpan.Slice(LogPageFrameHeaderCodec.StructSize + alignedLen, PageFrameFooterSize), new Crc32Footer { Crc = crc });
        if (paddingSize > 0)
            frameSpan.Slice(LogPageFrameHeaderCodec.StructSize + alignedLen + PageFrameFooterSize, paddingSize).Clear();

        return frameMem;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureWriteInitialized()
    {
        if (_pageA is null) InitializeForWrites();
    }

    /// <summary>★ 懒分配地址空间窗口：首次 Append 时 Allocate。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureSpaceAllocated()
    {
        if (!_spaceAllocated)
        {
            _spaceStart = _engine.Allocate(SpaceAllocSize).Start;
            _spaceCapacity = SpaceAllocSize;
            _spaceWriteOffset = 0;
            _spaceAllocated = true;
        }
    }

    /// <summary>★ 窗口剩余放不下时退回旧窗口未用空间 + Allocate 新窗口。</summary>
    /// <remarks>★ 退回旧窗口 [_spaceWriteOffset, _spaceCapacity) 未用空间——引擎 ReclaimTail 退回全部水位线到
    ///   实际已写尾，使下一个 Allocate 紧接本窗口最后一个 frame（无窗口边界空洞）。
    ///   这是必须的：cursor 顺序读跨窗口时若窗口间有全零空洞会读不到 magic 而停。Log 自管 _logicalTail
    ///   是真实水位，与引擎水位（退回后也紧贴）一致。</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureSpaceForNextPage(long minRequired = 0)
    {
        long threshold = Math.Max(PageSize, minRequired);
        if (_spaceCapacity - _spaceWriteOffset < threshold)
        {
            if (_spaceWriteOffset < _spaceCapacity)
            {
                var usedEnd = _engine.CalculationAddress(_spaceStart, _spaceWriteOffset);
                _engine.ReclaimTail(usedEnd);
            }
            _spaceStart = _engine.Allocate(SpaceAllocSize).Start;
            _spaceCapacity = SpaceAllocSize;
            _spaceWriteOffset = 0;
        }
    }

    // === partial: Dispose 时刷末页 ===
    protected override void DisposeOverride(bool disposing)
    {
        if (_pageA is null) return;
#pragma warning disable TCSG031 // 设计必需：同步写/截断 API 契约——返回前数据必须已落盘（_inFlightFlush 等待）
        if (_inFlightFlush is { } task) { task.GetAwaiter().GetResult(); _inFlightFlush = null; }
#pragma warning restore TCSG031
        if (ActivePageUsed > 0) FlushPage();
        _pageA?.Dispose();
        _pageB?.Dispose();
        _framePool?.Dispose();
        base.DisposeOverride(disposing);
    }



    protected override async ValueTask DisposeOverrideAsync(bool disposing)
    {
        if (_pageA is null) return;
        if (_inFlightFlush is not null || ActivePageUsed > 0)
        {
            if (_inFlightFlush is { } task) { await task.ConfigureAwait(false); _inFlightFlush = null; }
            if (ActivePageUsed > 0) await FlushPageAsync(CancellationToken.None).ConfigureAwait(false);
            _pageA?.Dispose();
            _pageB?.Dispose();
            _framePool?.Dispose();
        }
        _pageA?.Dispose();
        _pageB?.Dispose();
        _framePool?.Dispose();
        await base.DisposeOverrideAsync(disposing).ConfigureAwait(false);
    }
}
