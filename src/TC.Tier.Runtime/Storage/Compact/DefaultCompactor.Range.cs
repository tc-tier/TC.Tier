using System.Buffers;
using System.Runtime.ExceptionServices;
using TC.Tier.Core.Primitives;

namespace TC.Tier.Runtime.Storage.Compact;

internal sealed partial class DefaultCompactor
{
    /// <inheritdoc/>
    public IAsyncOperation<CompactResult> RangeCompact(
        CompactLease lease,
        LogicalAddress from,
        LogicalAddress to,
        IReadOnlyList<LogicalAddress> addresses)
        => RangeCompact(lease, from, to, addresses, livePlan: null);

    /// <summary>申报活区间版（§XVIII）——livePlan 覆盖 [from,to) 内的搬迁规划（记录粒度洞可见）。
    /// ★ 统一异步形态（同 <see cref="Compact(CompactLease[])"/>）——后台执行 + 句柄驱动，
    ///   取消经 op.CancellationToken（链接 _cts + Cancel()），lease 由后台任务收尾 Dispose。</summary>
    /// <param name="lease">已获取的 Compact 租约（覆盖整理范围），非空；生命周期移交后台任务收尾。</param>
    /// <param name="from">整理范围起点（含）。</param>
    /// <param name="to">整理范围终点（不含）。</param>
    /// <param name="addresses">参与搬迁的记录地址列表（申报的活数据），非空。</param>
    /// <param name="livePlan">按段分组的活区间规划（记录粒度）；null = 不做记录粒度规划（按地址搬迁）。</param>
    /// <returns>后台区间 Compact 句柄（0 等待返回）；完成后得到 <see cref="CompactResult"/>，失败/取消经句柄异常重抛。</returns>
    public IAsyncOperation<CompactResult> RangeCompact(
        CompactLease lease,
        LogicalAddress from,
        LogicalAddress to,
        IReadOnlyList<LogicalAddress> addresses,
        IReadOnlyDictionary<int, List<(long Start, long End)>>? livePlan)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(addresses);
        if (_epoch is null)
            throw new NotSupportedException("RangeCompact requires engine epoch integration.");
        if (!SupportsRangeCompact)
            throw new PlatformNotSupportedException(
                "RangeCompact requires reliable allocated-range and hole support.");
        EnsureNoPendingCommitMarker();

        var op = new AsyncOperation<CompactResult>("range-compact", _logger, _cts.Token);
        var ct = op.CancellationToken;
        RunCompactInBackground(() => RangeCompactCore(lease, from, to, addresses, livePlan, op, ct));
        return op;
    }

    /// <summary>区间整理执行体（同步——后台任务内跑）：快照/拷贝/原子 promote/提交。失败抛给 op。</summary>
    private void RangeCompactCore(
        CompactLease lease,
        LogicalAddress from,
        LogicalAddress to,
        IReadOnlyList<LogicalAddress> addresses,
        IReadOnlyDictionary<int, List<(long Start, long End)>>? livePlan,
        AsyncOperation<CompactResult> op,
        CancellationToken cancellationToken)
    {
        var migrationMap = new Dictionary<LogicalAddress, LogicalAddress?>();
        foreach (var address in addresses)
            migrationMap[address] = null;

        var chunks = lease.Chunks.OrderBy(static chunk => chunk.SegId).ToArray();
        if (chunks.Length == 0)
        {
            lease.Commit();
            op.ReportSucceeded(new CompactResult
            {
                NewLowWaterMark = from,
                NewHighWaterMark = from,
                MigrationMap = migrationMap,
            });
            return;
        }

        DeleteAllTemps();
        var images = new List<RangeSegmentImage>(chunks.Length);
        var markerWritten = false;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateRangeImages(chunks, images, to, cancellationToken);

            using var buffer = new AlignedMemoryManager(CopyChunkSize, AlignmentConst.Alignment4K);
            CopyOutsideRange(images, from, to, buffer, cancellationToken);
            var (moves, compactedEnd) = CopyCompactedRange(
                images, from, to, buffer, livePlan, cancellationToken);
            PopulateMigrationMap(addresses, moves, images, migrationMap);
            // ★ 活跃尾段：搬移完成后源区打洞（打包尾到水位线之间的窗口内数据已全部搬走）
            PunchTailSource(images, compactedEnd, to);

            foreach (var image in images)
            {
                if (image.Temp is not null) image.Temp.Flush();
                else if (image.IsTail) image.InPlace.Flush();   // ★ 尾段原地：原段文件 flush
            }

            // ★ 新段自写元数据（设计决策）：段元组写临时段 FileExtra——promote（rename）
            //   随文件同步就位，不再经引擎 tupleWriter 委托事后补写。活跃尾段原地搬移——
            //   元组写原段 FileExtra（文件不替换，元组就地更新）。
            foreach (var image in images)
            {
                var metaHandle = image.Temp ?? (image.IsTail ? image.InPlace : null);
                if (metaHandle is not null)
                    WriteTempSegmentMeta(metaHandle, image.Length, image.GrowthLimit, image.Length);
            }

            cancellationToken.ThrowIfCancellationRequested();
            foreach (var image in images)
                image.DisposeSource();

            WriteCommitMarker(
                CompactType.Range,
                chunks[0].SegId,
                chunks.Length,
                new List<OldSegmentDisposition>());
            markerWritten = SupportsMarker;

            var segmentIds = chunks.Select(static chunk => chunk.SegId).ToArray();
            // ★ 协作 drain（DrainThen，FASTER mutator 模型）——无并发 reader 时本线程同步触发；
            //   有 reader 时延迟到其退出 epoch 后触发。gate：promotion 完成前【不返回】。
            // ★ 原子协议（重排）：drain 回调内【先填 replacement 标量（min/max/growthLimit/state）、
            //   再物理 promote（rename 就位）、最后一次 lease.Commit() 原子换表+区间布局】——
            //   全部准备完毕、一次提交全部成功；段/区间重排全在 lease 内部，外部只报标量。
            // ★ L16 修复（）：promote 异常在 drain 线程【捕获】（不再炸无辜触发线程——
            //   reader 的 Suspend 栈），Wait 返回后 worker 侧校验并重抛——旧 finally{Set()} 吞错
            //   + 无成功校验 = marker 已删、物理已 rename、lease 却 Rollback、上层拿到成功结果
            //   按 MigrationMap 改址 → 读零/错数据（误报成功复合事故）。失败路径 marker 保留
            //   （DeleteCommitMarker 只在确认成功后执行）——重开/重试按 marker 恢复。
            Exception? promoteError = null;
            using var promoted = new ManualResetEventSlim(false);
            _epoch!.DrainThen(() =>
            {
                try { PromoteRangeImages(segmentIds, lease, compactedEnd, to); }
                catch (Exception ex) { promoteError = ex; }
                finally { promoted.Set(); }
            });
            if (!promoted.Wait(TimeSpan.FromSeconds(60), CancellationToken.None))
                throw new TimeoutException("RangeCompact promotion 未在 60s 内完成（epoch drain 阻塞？）");
            if (promoteError is not null)
                throw new InvalidOperationException("RangeCompact promotion 失败——段表未提交，marker 保留供恢复", promoteError);

            // Once the durable marker exists, promotion is a non-cancellable commit phase.

            DeleteLastWrittenCommitMarker();
            markerWritten = false;
            // ★ lease 释放先于完成通知（同全量契约）：等待者苏醒时 lease 必已释放（可重新入闸）
            try
            {
                lease.Dispose();
            }
            catch
            {
                /* ignored */
            }
            op.ReportSucceeded(new CompactResult
            {
                NewLowWaterMark = from,
                NewHighWaterMark = compactedEnd,
                MigrationMap = migrationMap,
            });
        }
        catch (Exception ex)
        {
            // ★ 现场保留契约（同全量）：marker 已写 → 保留临时文件+marker（续传/恢复前提）；
            //   marker 未写 → 清理半成品（无恢复路径防泄漏）。失败决策权归使用方——只报异常。
            if (!markerWritten)
            {
                DeleteLastWrittenCommitMarker();
                DeleteAllTemps();
                lease.Rollback();
            }
            try
            {
                lease.Dispose();
            }
            catch
            {
                /* ignored */
            }
            op.ReportFailed(ex);
        }
        finally
        {
            foreach (var image in images)
                image.Dispose();
            try
            {
                lease.Dispose();
            }
            catch
            {
                /* ignored */
            }
        }
    }

    private void CreateRangeImages(
        CompactChunk[] chunks,
        ICollection<RangeSegmentImage> images,
        LogicalAddress to,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < chunks.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = chunks[i];
            if (i > 0 && chunk.SegId != chunks[i - 1].SegId + 1)
                throw new InvalidOperationException("RangeCompact requires contiguous segment ids.");
            if (!SegmentExists(chunk.SegId))
                throw new FileNotFoundException($"RangeCompact source segment {chunk.SegId} is missing.");

            IFileHandle? source = null;
            IFileHandle? temp = null;
            try
            {
                source = OpenRangeSourceHandle(chunk.SegId);
                var length = source.Length;
                var allocated = source.EnumerateAllocatedRanges()
                    .Select(range => new PhysicalRange(
                        Math.Max(0, range.Start),
                        Math.Min(length, range.End)))
                    .Where(static range => range.End > range.Start)
                    .OrderBy(static range => range.Start)
                    .ToArray();

                // ★ 活跃尾段（水位线 to 落在段中间）：不建 temp、不 rename——窗口内数据原地
                //   前移打包 + 源区 PunchHole 打洞（写者句柄恒有效——并行整理语义）。
                var isTail = chunk.SegId == to.SegId && to.Offset < chunk.OldGrowthLimit;
                if (!isTail)
                {
                    temp = CreateTempHandle(chunk.SegId, 0);
                    if (length > 0)
                    {
                        temp.SetLength(length);
                        temp.PunchHole(0, length);
                    }
                }

                images.Add(new RangeSegmentImage(
                    this,
                    chunk.SegId,
                    length,
                    chunk.OldGrowthLimit,
                    allocated,
                    source,
                    temp));
                source = null;
                temp = null;
            }
            finally
            {
                source?.Dispose();
                temp?.Dispose();
            }
        }
    }

    private static void CopyOutsideRange(
        IReadOnlyList<RangeSegmentImage> images,
        LogicalAddress from,
        LogicalAddress to,
        AlignedMemoryManager buffer,
        CancellationToken cancellationToken)
    {
        foreach (var image in images)
        {
            // ★ 活跃尾段：窗口外数据原地就在（不建 temp 不搬）——跳过
            if (image.IsTail) continue;

            var rangeStart = image.SegmentId == from.SegId ? from.Offset : 0;
            var rangeEnd = image.SegmentId == to.SegId ? to.Offset : image.GrowthLimit;

            foreach (var allocated in image.AllocatedRanges)
            {
                if (allocated.Start < rangeStart)
                {
                    var end = Math.Min(allocated.End, rangeStart);
                    CopyRange(image.Source, allocated.Start, image.Temp!, allocated.Start,
                        end - allocated.Start, buffer, cancellationToken);
                }

                if (allocated.End > rangeEnd)
                {
                    var start = Math.Max(allocated.Start, rangeEnd);
                    CopyRange(image.Source, start, image.Temp!, start,
                        allocated.End - start, buffer, cancellationToken);
                }
            }
        }
    }

    private static (List<RangeMove> Moves, LogicalAddress End) CopyCompactedRange(
        IReadOnlyList<RangeSegmentImage> images,
        LogicalAddress from,
        LogicalAddress to,
        AlignedMemoryManager buffer,
        IReadOnlyDictionary<int, List<(long Start, long End)>>? livePlan,
        CancellationToken cancellationToken)
    {
        var bySegment = images.ToDictionary(static image => image.SegmentId);
        var moves = new List<RangeMove>();
        var destination = from;

        foreach (var image in images)
        {
            var rangeStart = image.SegmentId == from.SegId ? from.Offset : 0;
            var rangeEnd = image.SegmentId == to.SegId ? to.Offset : image.GrowthLimit;
            rangeEnd = Math.Min(rangeEnd, image.Length);

            // ★ §XVIII 申报活区间优先（记录粒度洞可见——物理 allocated 是簇粒度，小记录场景全量拷贝零回收）；
            //   未申报段回退物理枚举。统一裁剪到 [rangeStart, rangeEnd) 并按起点排序。
            IEnumerable<(long Start, long End)> ranges;
            if (livePlan is not null && livePlan.TryGetValue(image.SegmentId, out var declared))
            {
                ranges = declared
                    .Select(r => (Start: Math.Max(r.Start, rangeStart), End: Math.Min(r.End, rangeEnd)))
                    .Where(r => r.End > r.Start)
                    .OrderBy(r => r.Start);
            }
            else
            {
                ranges = image.AllocatedRanges
                    .Select(a => (Start: Math.Max(a.Start, rangeStart), End: Math.Min(a.End, rangeEnd)))
                    .Where(a => a.End > a.Start)
                    .OrderBy(a => a.Start);
            }

            foreach (var allocated in ranges)
            {
                var start = allocated.Start;
                var end = allocated.End;

                moves.Add(new RangeMove(image.SegmentId, start, end, destination));
                CopyToLogicalDestination(
                    image.Source,
                    image.SegmentId,
                    start,
                    end - start,
                    destination,
                    bySegment,
                    buffer,
                    cancellationToken);
                destination = AdvanceAddress(destination, end - start, bySegment);
            }
        }

        return (moves, destination);
    }

    private static void PopulateMigrationMap(
        IReadOnlyList<LogicalAddress> addresses,
        IReadOnlyList<RangeMove> moves,
        IReadOnlyList<RangeSegmentImage> images,
        Dictionary<LogicalAddress, LogicalAddress?> migrationMap)
    {
        // ★ A8 待打磨②收口（）：moves 按构建序 (SourceSegmentId, SourceStart) 升序
        //   （CopyCompactedRange 逐段逐区间顺序产出）——二分定位包含区间，O(地址数 × log 搬移数)
        //   替代旧 O(地址数 × 搬移数) 嵌套全扫（大地址集追赶整理的规划侧退化）。
        var bySegment = images.ToDictionary(static image => image.SegmentId);
        foreach (var address in addresses)
        {
            // ★ 区间统一：记录起点可以是段末边界 (seg, GrowthLimit)（首字节在下一段）——
            //   匹配 move 时归位到首字节所在段（与 CopyToLogicalDestination 的边界跳过同规），
            //   否则 move 记录在下一段、元组匹配落空 → 活记录被误判为未迁移。
            var probeSegId = address.SegId;
            var probeOffset = address.Offset;
            if (bySegment.TryGetValue(probeSegId, out var probeImage)
                && probeOffset == probeImage.GrowthLimit)
            {
                probeSegId++;
                probeOffset = 0;
            }

            // 二分：SourceSegmentId == probeSegId 且 SourceStart ≤ probeOffset 的最大 move
            var lo = 0;
            var hi = moves.Count - 1;
            var found = -1;
            while (lo <= hi)
            {
                var mid = (lo + hi) >> 1;
                var m = moves[mid];
                if (m.SourceSegmentId < probeSegId
                    || (m.SourceSegmentId == probeSegId && m.SourceStart <= probeOffset))
                {
                    found = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            if (found >= 0)
            {
                var move = moves[found];
                if (move.SourceSegmentId == probeSegId && probeOffset < move.SourceEnd)
                    migrationMap[address] = AdvanceAddress(
                        move.Destination, probeOffset - move.SourceStart, bySegment);
            }
        }
    }

    private static void CopyToLogicalDestination(
        IFileHandle source,
        int sourceSegmentId,
        long sourceOffset,
        long length,
        LogicalAddress destination,
        IReadOnlyDictionary<int, RangeSegmentImage> images,
        AlignedMemoryManager buffer,
        CancellationToken cancellationToken)
    {
        var segId = destination.SegId;
        var segOffset = destination.Offset;
        var remaining = length;
        var readOffset = sourceOffset;

        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!images.TryGetValue(segId, out var image))
                throw new InvalidOperationException("Compacted data exceeds the RangeCompact segment set.");
            if (segOffset == image.GrowthLimit)
            {
                segId++;
                segOffset = 0;
                continue;
            }

            var available = image.GrowthLimit - segOffset;
            var count = Math.Min(remaining, available);
            var requiredLength = segOffset + count;
            // ★ 活跃尾段目标 = 原地写（同段句柄）；非尾段 = temp 文件
            var target = image.Temp;
            if (target is null)
            {
                target = image.InPlace;
                if (target.Length < requiredLength)
                    target.SetLength(requiredLength);
            }
            else if (requiredLength > target.Length)
            {
                target.SetLength(requiredLength);
            }

            // ★ 尾段同段前移搬移（源、目标同文件且目标 < 源）：必须反向拷贝（memmove 反向分支——
            //   正向会先覆盖源区未读部分）；同址/跨段（异文件）正向安全。
            if (image.IsTail && sourceSegmentId == segId && segOffset < readOffset)
                CopyRangeBackward(source, readOffset, target, segOffset, count, buffer, cancellationToken);
            else
                CopyRange(source, readOffset, target, segOffset, count, buffer, cancellationToken);
            readOffset += count;
            remaining -= count;
            segOffset += count;
            if (segOffset == image.GrowthLimit)
            {
                segId++;
                segOffset = 0;
            }
        }
    }

    /// <summary>★ 尾段源区打洞（搬移完成后）：[尾段内打包尾, to.Offset) 的窗口内数据已全部搬走
    /// （目标恒在 [from, compactedEnd)）——源区 PunchHole 归还物理空间。同址未搬区间不在此列
    /// （compactedEnd 之前）。幂等（Reclaim 同款语义）。</summary>
    private static void PunchTailSource(
        IReadOnlyList<RangeSegmentImage> images,
        LogicalAddress compactedEnd,
        LogicalAddress to)
    {
        foreach (var image in images)
        {
            if (!image.IsTail) continue;
            var punchStart = compactedEnd.SegId == image.SegmentId ? compactedEnd.Offset : 0;
            var punchEnd = image.SegmentId == to.SegId ? to.Offset : image.Length;
            punchEnd = Math.Min(punchEnd, image.Length);
            if (punchStart >= punchEnd) continue;
            image.InPlace.PunchHole(punchStart, punchEnd - punchStart);
        }
    }

    private static void CopyRange(
        IFileHandle source,
        long sourceOffset,
        IFileHandle destination,
        long destinationOffset,
        long length,
        AlignedMemoryManager buffer,
        CancellationToken cancellationToken)
    {
        var copied = 0L;
        while (copied < length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(buffer.Size, length - copied);
            var span = buffer.GetSpan().Slice(0, count);
            var read = source.Read(sourceOffset + copied, span);
            if (read != count)
                throw new EndOfStreamException(
                    $"RangeCompact short read: expected {count} bytes, got {read}.");
            destination.Write(destinationOffset + copied, span);
            copied += count;
        }
    }

    /// <summary>★ 反向分块拷贝（同文件前移搬移——从区间尾向头，memmove 反向分支）。</summary>
    private static void CopyRangeBackward(
        IFileHandle source,
        long sourceOffset,
        IFileHandle destination,
        long destinationOffset,
        long length,
        AlignedMemoryManager buffer,
        CancellationToken cancellationToken)
    {
        var remaining = length;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(buffer.Size, remaining);
            var span = buffer.GetSpan().Slice(0, count);
            var read = source.Read(sourceOffset + remaining - count, span);
            if (read != count)
                throw new EndOfStreamException(
                    $"RangeCompact short read (backward): expected {count} bytes, got {read}.");
            destination.Write(destinationOffset + remaining - count, span);
            remaining -= count;
        }
    }

    private static LogicalAddress AdvanceAddress(
        LogicalAddress address,
        long length,
        IReadOnlyDictionary<int, RangeSegmentImage> images)
    {
        var segId = address.SegId;
        var offset = address.Offset;
        var remaining = length;

        while (remaining > 0)
        {
            if (!images.TryGetValue(segId, out var image))
                throw new InvalidOperationException("Logical address exceeds the RangeCompact segment set.");
            // ★ 区间统一：起点为段末边界 (seg, GrowthLimit) 时首字节在下一段——跳过边界进位
            if (offset == image.GrowthLimit)
            {
                segId++;
                offset = 0;
                continue;
            }

            var step = Math.Min(remaining, image.GrowthLimit - offset);
            offset += step;
            remaining -= step;
            // ★ 区间统一：恰好填满停驻 (seg, GrowthLimit)——不再归一成 (seg+1, 0)
            //   （compactedEnd 停在真实数据末尾，与段表 AdvanceAddress 同形）
        }

        return new LogicalAddress(segId, offset);
    }

    private void PromoteRangeImages(
        IReadOnlyList<int> segmentIds,
        CompactLease lease,
        LogicalAddress compactedEnd,
        LogicalAddress to)
    {
        // ① 填 replacement【标量】（外部契约只有 min/max/growthLimit/state——段/区间重排是 lease 内部的事）：
        //   打包末端 compactedEnd 之前的段 = 整段打包（max=旧上限）；末端所在段 = max=其偏移；之后 = 空段（max=0）。
        //   ★ L19 收口：窗口尾段（SegId == to.SegId）设 preserveFrom = to.Offset——[to.Offset, 旧 MaxOffset)
        //   的窗口外已提交区间原样保留（写者恰在 lease 获取前提交的数据不洗零）。
        foreach (var chunk in lease.Chunks)
        {
            long segMax;
            if (chunk.SegId < compactedEnd.SegId)
                segMax = chunk.OldGrowthLimit;
            else if (chunk.SegId == compactedEnd.SegId)
                segMax = compactedEnd.Offset;
            else
                segMax = 0;
            chunk.SetReplacement(chunk.OldGrowthLimit, segMax,
                preserveFrom: chunk.SegId == to.SegId ? to.Offset : long.MaxValue);
        }

        // ② 物理 promote（rename 就位）——新段文件必须先在，lease.Commit 换表后才指得到
        foreach (var segId in segmentIds)
        {
            // ★ STORAGE-002 (#222)：加最大重试上限，避免持续 IO 故障下 drain 线程死锁。
            const int maxPromoteAttempts = 50;   // 50 × 100ms = 最长 5s
            int attempts = 0;
            while (TempExists(segId))
            {
                try
                {
                    PromoteTemp(segId);
                    attempts = 0;   // 成功（或 TempExists 翻 false）则重置
                }
                catch (Exception ex) when (ex is IOException)   // FileIOException : IOException——Core catch-all Wrap 后原生异常不逃逸
                {
                    if (++attempts >= maxPromoteAttempts)
                    {
                        throw new FileIOException(IOError.SharingViolation,
                            $"RangeCompact promote segId={segId} 失败：{maxPromoteAttempts} 次重试（共约 5s）仍占用，"
                            + "drain 线程放弃以避免死锁（commit marker 已持久，下次启动会重试）", inner: ex);
                    }
                    _logger?.LogWarning(
                        ex,
                        "RangeCompact promotion will retry segId={segId} (attempt {attempt}/{max}); commit marker is durable.",
                        segId, attempts, maxPromoteAttempts);
                    Thread.Sleep(100);
                }
            }
        }

        // ③ 原子点：一次 lease.Commit 换表（区间布局由 lease 内部按 标量+旧段 锁内推导）
        lease.Commit();
    }

    private readonly record struct PhysicalRange(long Start, long End);

    private readonly record struct RangeMove(
        int SourceSegmentId,
        long SourceStart,
        long SourceEnd,
        LogicalAddress Destination);

    private sealed class RangeSegmentImage : IDisposable
    {
        private readonly DefaultCompactor _owner;

        internal RangeSegmentImage(
            DefaultCompactor owner,
            int segmentId,
            long length,
            long growthLimit,
            PhysicalRange[] allocatedRanges,
            IFileHandle source,
            IFileHandle? temp)
        {
            _owner = owner;
            SegmentId = segmentId;
            Length = length;
            GrowthLimit = growthLimit;
            AllocatedRanges = allocatedRanges;
            Source = source;
            Temp = temp;
            IsTail = temp is null;   // ★ 活跃尾段（水位线在段中间）：不建 temp——原地搬移 + 打洞
        }

        internal int SegmentId { get; }
        internal long Length { get; }
        internal long GrowthLimit { get; }
        internal PhysicalRange[] AllocatedRanges { get; }
        internal IFileHandle Source { get; }
        internal IFileHandle? Temp { get; }
        /// <summary>★ 活跃尾段标志（水位线落在段中间——该段不 rename：写者句柄恒有效，并行不丢）。</summary>
        internal bool IsTail { get; }

        private IFileHandle? _inPlace;
        /// <summary>★ 尾段原地写句柄（懒打开——同段 pwrite 搬移 + PunchHole 源区；非尾段恒 null）。</summary>
        internal IFileHandle InPlace => _inPlace ??= _owner._fileSystem.Open(_owner.GetSegmentPath(SegmentId),
            new FileOpenOptions
            {
                Access = AccessMode.ReadWrite,
                Mode = FileOpenMode.OpenExisting,
                Sharing = FileSharing.ReadWrite | FileSharing.Delete,
            });

        private int _sourceDisposed;
        private int _disposed;

        internal void DisposeSource()
        {
            if (Interlocked.Exchange(ref _sourceDisposed, 1) == 0)
                Source.Dispose();
        }

        /// <summary>释放整理会话资源——幂等：释放源句柄、临时目标与原位搬移上下文（各内部自防双释放）。</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            DisposeSource();
            Temp?.Dispose();
            _inPlace?.Dispose();
        }
    }
}
