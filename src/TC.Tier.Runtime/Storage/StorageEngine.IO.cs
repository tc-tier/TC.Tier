namespace TC.Tier.Runtime.Storage;

/// <summary>
/// IO 实现 partial——Append/Write/Read/Allocate/Flush/Reclaim 全在基类，
/// 通过 <see cref="IFileHandle"/> 调用（介质无关）。
/// <para>★ 实现类只决定注入哪个文件句柄工厂委托（造 DiskFileHandle/MemFileHandle）
///   和哪个 <see cref="Compact.ICompact"/>——其余差异为零。</para>
/// </summary>
internal sealed partial class StorageEngine
{

    // ═══════════════════════════════════════════════════════════════
    //  Append（追加写，推进游标）
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public LogicalAddress Append(ReadOnlySpan<byte> source)
    {
        ThrowIfDisposed();
        EnsureReady();
        EnsureCpuCapacity(CancellationToken.None);   // CPU 限流（同步路径——仅超时，无外部 ct）
        using var lease = _segmentTable.AppendLease(source.Length);
        // ★ 单段模式校验（IO 层）：分配结果超出 seg0 → 地址空间用尽
        if (!EnableSegmentation && lease.Start.SegId > 0)
            throw new InvalidOperationException(
                $"Single-segment mode: address space exhausted (seg0 capacity {SegmentGrowthLimit} bytes).");
        CopyChunks(lease, source);
        lease.Commit();
        return lease.Start;
    }

    /// <inheritdoc/>
    /// <remarks>真异步：走 <see cref="IFileHandle.WriteAsync"/>，不阻塞调用线程。</remarks>
    public async ValueTask<LogicalAddress> AppendAsync(ReadOnlyMemory<byte> source, CancellationToken ct)
    {
        ThrowIfDisposed();
        EnsureReady();
        EnsureCpuCapacity(ct);   // CPU 限流（异步路径——传调用方 ct，外部可取消）
        using var lease = _segmentTable.AppendLease(source.Length, ct);
        await CopyChunksAsync(lease, source, ct).ConfigureAwait(false);
        lease.Commit();
        return lease.Start;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Allocate（预留空间，推进游标，不写数据）
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    /// <remarks>Allocate ≡ Append − pwrite：lease 占区间 + 推水位，传空 Span 跳过拷贝。</remarks>
    public (LogicalAddress Start, LogicalAddress End) Allocate(long length)
    {
        if (length <= 0)
            throw new ArgumentOutOfRangeException(nameof(length), length, "Allocate length must be positive.");
        ThrowIfDisposed();
        EnsureReady();
        EnsureCpuCapacity(CancellationToken.None);   // CPU 限流（同步路径——仅超时，无外部 ct）
       var lease = _segmentTable.AllocateLease(length);
        // ★ 单段模式校验（IO 层）：分配结果超出 seg0 → 地址空间用尽。
        //   区间统一（）后分配 end 不再产出 (seg+1,0)（exact-fill 停驻段末 (seg,limit)），
        //   下方豁免为存量防御恒不命中——End 跨段即真超限（表级守卫 ② 已拦，此处双保险）。
        if (!EnableSegmentation && lease.End.SegId > lease.Start.SegId
            && !(lease.End.SegId == lease.Start.SegId + 1 && lease.End.Offset == 0))
            throw new InvalidOperationException(
                $"Single-segment mode: address space exhausted (seg0 capacity {SegmentGrowthLimit} bytes).");
        return lease;
    }

    // ═══════════════════════════════════════════════════════════════
    //  CalculationAddress（地址逻辑推算，不分配不 IO）
    // ═══════════════════════════════════════════════════════════════

   /// <summary>
   /// 计算给定地址 ± 长度的逻辑地址（纯计算，不分配不 IO，无空间管理）。
   /// <para>★ length ≥ 0 前进（跨段进位）；length &lt; 0 回退（跨段借位，等价 <see cref="RetreatAddress"/>(address, -length)）；
   ///   回退越过 MinAddress 返回 <see cref="LogicalAddress.Invalid"/>。</para>
   /// </summary>
   /// <param name="address">起始逻辑地址。</param>
   /// <param name="length">推进（正）/回退（负）的长度。</param>
   /// <returns>计算后的逻辑地址。</returns>
    public LogicalAddress CalculationAddress(LogicalAddress address, long length)
        => length >= 0
            ? _segmentTable.AdvanceAddress(address, length)
            : _segmentTable.RetreatAddress(address, -length);

   /// <summary>
   /// 计算给定地址 - 长度的逻辑地址（跨段退位正确），不分配、不 IO。
   /// </summary>
   /// <param name="address">起始逻辑地址。</param>
   /// <param name="length">要退回的长度。</param>
   /// <returns>计算后的逻辑地址。</returns>
   public LogicalAddress RetreatAddress(LogicalAddress address, long length)
        => _segmentTable.RetreatAddress(address, length);
   /// <summary>
   /// 计算两个逻辑地址之间的距离（跨段正确），不分配、不 IO。
   /// </summary>
   /// <param name="from">起始逻辑地址。</param>
   /// <param name="to">结束逻辑地址。</param>
   /// <returns>两个逻辑地址之间的距离。</returns>
    public long GetDistance(LogicalAddress from, LogicalAddress to)
        => _segmentTable.GetDistance(from, to);

    // ═══════════════════════════════════════════════════════════════
    //  Write（随机覆写，给定地址）
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public LogicalAddress Write(LogicalAddress destination, ReadOnlySpan<byte> source)
    {
        ThrowIfDisposed();
        EnsureReady();
        using var lease = _segmentTable.WriteLease(destination, source.Length);
        CopyChunks(lease, source);
        lease.Commit();
        return destination;
    }

    /// <inheritdoc/>
    /// <remarks>真异步：走 <see cref="IFileHandle.WriteAsync"/>，不阻塞调用线程。</remarks>
    public async ValueTask<LogicalAddress> WriteAsync(LogicalAddress destination, ReadOnlyMemory<byte> source,
        CancellationToken ct)
    {
        ThrowIfDisposed();
        EnsureReady();
        using var lease = _segmentTable.WriteLease(destination, source.Length, ct);
        await CopyChunksAsync(lease, source, ct).ConfigureAwait(false);
        lease.Commit();
        return destination;
    }


    // ═══════════════════════════════════════════════════════════════
    //  Flush（仅同步）
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public void Flush()
    {
        ThrowIfDisposed();
        EnsureReady();
        // ★ WriteThrough + Win/Linux：每次写已落盘（内核同步写），Flush 是 no-op。
        //   macOS 无原生写透，仍需 F_FULLFSYNC 兜底（平台判断收口到 FileNative）。
        if (Hints.HasFlag(FileOpenHints.WriteThrough) && FileNative.WriteThroughImpliesFlushed) return;
        for (var segId = _segmentTable.MinSegId; segId <= _segmentTable.MaxSegId; segId++)
            FlushSegment(segId);
    }

    /// <inheritdoc/>
    public void Flush(LogicalAddress upTo)
    {
        ThrowIfDisposed();
        EnsureReady();
        // ★ 同 Flush()：WriteThrough + Win/Linux 时 no-op
        if (Hints.HasFlag(FileOpenHints.WriteThrough) && FileNative.WriteThroughImpliesFlushed) return;
        for (var segId = _segmentTable.MinSegId; segId <= upTo.SegId; segId++)
            FlushSegment(segId);
    }

    /// <summary>刷单段（借池内写句柄——命中零开销，未命中按需打开，刷完即还）。</summary>
    private void FlushSegment(int segId)
    {
        if (!_segmentTable.TryGetSegment(segId, out var view) || view is not { IsValid: true }) return;
        using var handle = GetWriteHandle(segId);
        handle.Flush();
    }

    // ═══════════════════════════════════════════════════════════════
    //  内部辅助
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 第二阶段纯物理 IO——遍历 lease.Chunks 延迟集合，用 chunk 副本定位物理资源做拷贝。
    /// <para>★ lease.Chunks 是延迟对象：每次 yield 前 lease 内部先 WaitSegmentReady（等段物理就绪），
    ///   把建段压力分摊到多段 IO 之间（前一段 IO 时后一段并行建）——消费方无需自己等段、无需传段号。</para>
    /// <para>★ chunk 副本含 SegId/SegOff/Length 一切（lease 第一阶段算好的地址占位区间），
    ///   通过 IFileHandle.Write 落地。段号是 lease 给的，消费方只是"使用"，不能自己造。</para>
    /// <para>★ 每段 IO 后 chunk.Commit 分段提交（chunk 持委托指向 lease 内部方法，
    ///   逐段释放所有权 + 推 MaxOffset，读路径渐进可见）。</para>
    /// </summary>
    private void CopyChunks(LeaseBase lease, ReadOnlySpan<byte> source)
    {
        bool hasData = !source.IsEmpty;
        int srcOffset = 0;
        // ★ foreach 完整迭代器模式（ChunkScope：几何 + 分段 Commit）——MoveNext 内嵌类型化物理门
        //   （延迟模型 + 建段压力分摊），Commit 由 doneMask 保 exactly-once
        foreach (var chunk in lease)
        {
            int chunkLen = (int)chunk.Length;
            if (hasData && chunkLen > 0)
            {
                WriteChunkWithHandleReacquire(chunk.SegId, chunk.SegOff, source.Slice(srcOffset, chunkLen));
            }

            srcOffset += chunkLen;
            chunk.Commit();
        }
    }

    /// <summary>
    /// 第二阶段纯物理 IO（异步版）——遍历 lease，用 <see cref="IFileHandle.WriteAsync"/> 落地。
    /// <para>★ 与 <see cref="CopyChunks"/> 协议一致，区别仅物理调用走 async——磁盘真异步 pwrite，内存同步快路径。</para>
    /// </summary>
    private async ValueTask CopyChunksAsync(LeaseBase lease, ReadOnlyMemory<byte> source, CancellationToken ct)
    {
        bool hasData = !source.IsEmpty;
        int srcOffset = 0;
        // ★ foreach 迭代器模式（ChunkScope 为普通 struct，循环体内 await 合法）
        foreach (var chunk in lease)
        {
            ct.ThrowIfCancellationRequested();
            int chunkLen = (int)chunk.Length;
            if (hasData && chunkLen > 0)
            {
                await WriteChunkAsyncWithHandleReacquire(
                    chunk.SegId, chunk.SegOff, source.Slice(srcOffset, chunkLen), ct).ConfigureAwait(false);
            }

            srcOffset += chunkLen;
            chunk.Commit();
        }
    }

    /// <summary>
    /// ★ 带句柄重取的 chunk 写（A8：整理不挡追加的容错面）——句柄缓存被并发整理逐出
    /// （<see cref="ReleaseCompactRangeHandles"/> 释放尾段句柄时，正在写尾段未提交区的写者）
    /// 抛 <see cref="ObjectDisposedException"/> 时重取句柄重写一次（pwrite 同偏移同数据幂等）。
    /// <para>★ 取证：ChaseCompactionSimulationTests——RangeCompact 全量清句柄致范围外写者 ODE
    ///   （已由范围释放根治）；本重试兜尾段（部分在范围内、必须释放）的残余窗口。</para>
    /// </summary>
    private void WriteChunkWithHandleReacquire(int segId, long offset, ReadOnlySpan<byte> data)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var handle = GetWriteHandleForChunk(segId, offset, data.Length);
            try
            {
                handle.Write(offset, data);
                return;
            }
            catch (ObjectDisposedException) when (attempt == 0)
            {
                // 句柄被整理释放——重取（缓存已清，必是新句柄）重写一次
            }
        }
    }

    /// <summary>异步版 <see cref="WriteChunkWithHandleReacquire"/>（同语义）。</summary>
    private async ValueTask WriteChunkAsyncWithHandleReacquire(
        int segId, long offset, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            await using var handle = GetWriteHandleForChunk(segId, offset, data.Length);
            try
            {
                await handle.WriteAsync(offset, data, ct).ConfigureAwait(false);
                return;
            }
            catch (ObjectDisposedException) when (attempt == 0)
            {
                // 句柄被整理释放——重取（缓存已清，必是新句柄）重写一次
            }
        }
    }
}
