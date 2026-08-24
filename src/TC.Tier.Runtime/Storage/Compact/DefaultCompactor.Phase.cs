using System.Buffers;
using TC.Tier.Core.Primitives;
namespace TC.Tier.Runtime.Storage.Compact;

internal sealed partial class DefaultCompactor
{
    // ═══════════════════════════════════════════════════════════════
    //  主流程——Phase 0/1/2
    // ═══════════════════════════════════════════════════════════════

    private CompactResult RunCompactLifecycle(CompactTask task)
    {
        var op = task.Op;
        var leases = task.Leases;
        var ct = op.CancellationToken;

        // 批量模型：所有 lease 一个批次处理，要么全部成功要么全部回滚
        // 每个 lease 独立计算新段布局、独立拷贝；Phase 2 统一 Promote + 全部 lease.Commit
        var perLeasePlans = new List<LeasePlan>(leases.Length);
        List<IFileHandle> allTempHandles = new();
        bool markerWritten = false;   // ★ 现场保留契约（2026-08-24）：Phase 2（marker 已写）失败保留临时文件

        try
        {
            Volatile.Write(ref _status, (int)CompactStatus.Copying);

            // ── Phase 0 + Phase 1：逐 lease 拍快照 + 拷贝 ──
            var migrationMap = new Dictionary<LogicalAddress, LogicalAddress?>();
            foreach (var lease in leases)
            {
                ct.ThrowIfCancellationRequested();

                var snapshots = TakePhysicalSnapshots(lease, ct);
                long leaseTotalLen = snapshots.Values.SelectMany(s => s).Sum(r => r.End - r.Start);

                // 无数据 lease → 空操作（仍记 plan 用于 Phase 2 Commit/Dispose）
                if (leaseTotalLen <= 0)
                {
                    perLeasePlans.Add(new LeasePlan
                    {
                        Lease = lease,
                        Snapshots = snapshots,
                        TotalLen = 0,
                        FirstNewSegId = lease.Start.SegId,
                        NewSegCount = 0,
                        SegLimit = 1,
                    });
                    continue;
                }

                long segLimit = ComputeSegLimit(lease);
                int firstNewSegId = lease.Start.SegId;
                int newSegCount = (int)((leaseTotalLen + segLimit - 1) / segLimit);
                if (newSegCount < 1) newSegCount = 1;

                // 创建临时段
                var tempHandles = CreateTempSegments(firstNewSegId, newSegCount, segLimit, ct);
                allTempHandles.AddRange(tempHandles);

                // 拷贝本 lease 数据
                CopyData(snapshots, firstNewSegId, newSegCount, segLimit, leaseTotalLen,
                    tempHandles, op, ct, migrationMap);

                // flush 本 lease 临时段
                foreach (var h in tempHandles) h.Flush();

                // ★ 新段自写元数据（2026-08-24 用户裁定：元数据随新段走，fs 替换同步就位）——
                //   段元组写临时段 FileExtra（xattr 随 inode / sidecar 随 TryMoveSidecar），
                //   promote（rename）时随文件就位——不再经引擎 tupleWriter 委托事后补写。
                for (int i = 0; i < tempHandles.Count; i++)
                {
                    long realSize = (i == tempHandles.Count - 1)
                        ? leaseTotalLen - segLimit * (tempHandles.Count - 1)
                        : segLimit;
                    WriteTempSegmentMeta(tempHandles[i], realSize, segLimit, realSize);
                }

                perLeasePlans.Add(new LeasePlan
                {
                    Lease = lease,
                    Snapshots = snapshots,
                    TotalLen = leaseTotalLen,
                    FirstNewSegId = firstNewSegId,
                    NewSegCount = newSegCount,
                    SegLimit = segLimit,
                    TempHandles = tempHandles,
                });
            }

            // ── Phase 2: 提交（不可取消）── 文件循环替换在前、段表原子提交殿后 ──
            // ★ 顺序重排（2026-08-24 用户裁定）：拷贝（耗时全部）→ 循环{ rename 临时段 + SetReplacement }
            //   → 最后 lease.Commit（瞬间原子）。提交阶段无长耗时、失败断点续传（PromoteTemp 幂等：
            //   TempExists 判断跳过已替换段，与 RecoverCompactMarker 恢复循环同构）——rename 失败
            //   抛类型化异常（FileIOException.SharingViolation）由使用方（引擎）决策：关句柄后续传/回滚。
            // ★ 崩溃安全（STORAGE-001 注释声明的理想顺序，现实现真正满足）：Commit 让段表指向新段时，
            //   新段必已就位（Promote 全部完成）；Commit 前崩溃 → 段表指旧段 + 临时文件在 + marker 在 →
            //   恢复补执行；Commit 后崩溃 → 段表指新段 + 新段已就位，安全。
            Volatile.Write(ref _status, (int)CompactStatus.Committing);

            // 先填所有 lease 的 Chunks（SetReplacement/MarkInvalid）+ 写 marker
            // 注意：这些步骤都不改段表（lease.Commit 才改），失败可安全回滚；marker 写后即"现场可恢复"
            //   （RecoverCompactMarker 对坏 marker 有清理兜底）。
            foreach (var plan in perLeasePlans)
            {
                if (plan.NewSegCount == 0)
                {
                    // ★ L19 配套（2026-08-22）：lease 尾段扩到 GrowthLimit 后"零数据 lease"也有 chunk
                    //   （旧钳制下空区间零 chunk、Commit 无绊线可触）——显式填充：零数据整理 = 空段槽
                    //   （SetReplacement(旧上限, 0)：MaxOffset 归零、区间表清空，读零语义不变）。
                    //   空设备/全打洞 lease 的正常形态。
                    foreach (var chunk in plan.Lease.Chunks)
                        chunk.SetReplacement(chunk.OldGrowthLimit, 0);
                    continue;
                }
                FillLeaseChunks(plan);
                WriteCommitMarkerForPlan(plan);
            }
            markerWritten = true;

            // ★ 循环替换段文件（rename .compact → 正式段，幂等断点续传）——唯一碰文件占用的步骤：
            //   失败即抛类型化异常（重试预算见 PromoteTemp：仅兜短命句柄内核释放窗口），不在此消化
            //   引擎缓存句柄问题（那是使用方资源——op.Failed 后引擎按异常类型决策）。
            foreach (var plan in perLeasePlans)
            {
                for (int i = 0; i < plan.NewSegCount; i++)
                {
                    PromoteTemp(plan.FirstNewSegId + i);
                }
            }

            // 最后 lease.Commit（原子替换段表——纯内存瞬间操作，验证完备后不应失败）
            // 任一失败回滚已 commit 的（best-effort Rollback——但 Commit 已改段表，无法真回滚，
            // 这是已知风险，正常情况 Commit 不应失败）
            for (int i = 0; i < perLeasePlans.Count; i++)
            {
                try
                {
                    perLeasePlans[i].Lease.Commit();
                }
                catch (Exception)
                {
                    for (int j = 0; j < i; j++)
                        try
                        {
                            perLeasePlans[j].Lease.Rollback();
                        }
                        catch
                        {
                            /* ignored */
                        }

                    throw;
                }
            }

            // ★ STORAGE-001 (#221)：删旧段移到 Commit + PromoteTemp 之后。
            //   此时段表已指向新段 + 新段已就位，删旧段中崩溃 → 恢复时新段可用、旧段可重删，安全。
            //   （旧行为：删旧段在 Commit 前，Commit 前崩溃 → 段表指已删旧段，必然丢数据。）
            foreach (var plan in perLeasePlans)
            {
                if (plan.NewSegCount == 0) continue;
                ProcessOldSegDispositions(AnalyzeSegDispositions(plan));
            }

            // 末段尾 PunchHole 释放预分配块（每个 lease 的末段）
            foreach (var plan in perLeasePlans)
            {
                if (plan.NewSegCount == 0) continue;
                long lastNewOff = plan.TotalLen - plan.SegLimit * (plan.NewSegCount - 1);
                if (lastNewOff < plan.SegLimit)
                {
                    int lastNewSegId = plan.FirstNewSegId + plan.NewSegCount - 1;
                    PunchHoleSegment(lastNewSegId, lastNewOff, plan.SegLimit - lastNewOff);
                }
            }

            // 删 commit marker
            DeleteCommitMarkerRequired();

            // ★ 新段元数据已随临时段就位（Phase 1 拷贝后自写 FileExtra，rename 同步迁移）——此处无需补写。

            // Dispose 所有 lease
            foreach (var plan in perLeasePlans)
                try
                {
                    plan.Lease.Dispose();
                }
                catch
                {
                    /* ignored */
                }

            // 构造总 CompactResult（汇总所有 lease 的边界）
            var validPlans = perLeasePlans.Where(p => p.NewSegCount > 0).ToList();
            var lowMark = validPlans.Count > 0
                ? new LogicalAddress(validPlans.Min(p => p.FirstNewSegId), 0)
                : LogicalAddress.Empty;
            var highMark = validPlans.Count > 0
                ? new LogicalAddress(
                    validPlans.Max(p => p.FirstNewSegId + p.NewSegCount - 1),
                    validPlans.Max(p => p.TotalLen - p.SegLimit * (p.NewSegCount - 1)))
                : LogicalAddress.Empty;

            Volatile.Write(ref _status, (int)CompactStatus.Completed);
            // ★ 只构造返回——完成通知由 RunCompactLifecycleSafe 在 _compacting 复位后统一发（L2）。
            return new CompactResult
            {
                NewLowWaterMark = lowMark,
                NewHighWaterMark = highMark,
                MigrationMap = migrationMap,
            };
        }
        catch (Exception ex)
        {
            // 子系统自身资源卫生：Dispose 临时段句柄（保留文件——见下方现场保留契约）、回滚未提交的 lease
            foreach (var h in allTempHandles)
            {
                try
                {
                    h.Dispose();
                }
                catch
                {
                    /* ignored */
                }
            }

            // ★ 现场保留契约（2026-08-24 用户裁定——失败决策权归使用方）：
            //   - Phase 2 失败（marker 已写）：【保留】临时文件 + marker——续传（引擎关句柄后
            //     Retry/Recover 补执行或下次启动 marker 恢复的前提；删除 = 摧毁搬迁成果（重拷贝）。
            //   - Phase 1 失败（marker 未写）：【清理】半成品临时文件——无 marker 无恢复路径，
            //     保留 = 泄漏；清理是子系统自身半成品的资源卫生，非使用方决策。
            if (!markerWritten)
                DeleteAllTemps();
            foreach (var plan in perLeasePlans)
            {
                try
                {
                    plan.Lease.Dispose();
                }
                catch
                {
                    /* ignored */
                }
            }

            // ★ L4 取证修复（2026-08-21）：搬迁中途失败的 lease 不在 perLeasePlans（Add 在 CopyData
            //   成功之后）——只 Dispose plans 会泄漏其在途区间（CompactLeased 残留 → 读门永自旋，
            //   满负载下全量挂死）。全部 leases 统一 Dispose：幂等（已终态 no-op），plans 内与
            //   中途失败的一并兜底回滚。
            foreach (var lease in leases)
            {
                try
                {
                    lease.Dispose();
                }
                catch
                {
                    /* ignored */
                }
            }

            bool isCancel = ex is OperationCanceledException;
            Volatile.Write(ref _status, (int)(isCancel ? CompactStatus.Cancelling : CompactStatus.Faulted));
            // ★ 不在此通知失败——wrapper 在 _compacting 复位后统一 NotifyFailed（L2 happens-before）。
            throw;
        }
    }

    /// <summary>单个 lease 的执行计划（批量批次中的一项）。</summary>
    private sealed class LeasePlan
    {
        internal CompactLease Lease;
        internal Dictionary<int, List<(long Start, long End)>> Snapshots = new();
        internal long TotalLen;
        internal int FirstNewSegId;
        internal int NewSegCount;
        internal long SegLimit;
        internal List<IFileHandle> TempHandles = new();
    }

    /// <summary>填 lease.Chunks——新段 SetReplacement、超出范围的旧段 MarkInvalid。</summary>
    private void FillLeaseChunks(LeasePlan plan)
    {
        int newSegEndExclusive = plan.FirstNewSegId + plan.NewSegCount;
        long lastNewOff = plan.TotalLen - plan.SegLimit * (plan.NewSegCount - 1);
        var chunks = plan.Lease.Chunks.ToList();

        for (int i = 0; i < plan.NewSegCount; i++)
        {
            int segId = plan.FirstNewSegId + i;
            long segOff = (i == plan.NewSegCount - 1) ? lastNewOff : plan.SegLimit;
            var chunk = chunks.FirstOrDefault(c => c.SegId == segId);
            // ★ 全量 Compact 不设 preserveFrom（重打包模型：数据物理搬移到新偏移，原位保留会指向
            //   未拷贝的洞 = 假 committed）。写者数据进快照的保障 = lease 获取前的提交必在
            //   CommittedTail 之内（AppendFinalize 即时推尾），窗口外无已提交残留。
            chunk?.SetReplacement(plan.SegLimit, segOff);
        }

        foreach (var chunk in chunks)
        {
            if (chunk.SegId >= newSegEndExclusive)
                chunk.MarkInvalid();
        }
    }

    /// <summary>写 commit marker（每个 lease 一份标记——实现简化为单 marker，多 lease 时合并到最后一个）。</summary>
    private void WriteCommitMarkerForPlan(LeasePlan plan)
    {
        // 单 marker 设计：只记最后一份（多 lease 共享一个 marker 文件）
        // 简化：用 lease 的 compactType 判断（全量 vs 区间）
        bool isFull = plan.Lease.Start.SegId == plan.Lease.End.SegId && plan.Lease.Start.Offset == 0;
        int newSegEndExclusive = plan.FirstNewSegId + plan.NewSegCount;
        var dispositions = AnalyzeSegDispositions(plan)
            .Where(d => d.SegId >= newSegEndExclusive).ToList();
        WriteCommitMarker(isFull ? CompactType.Full : CompactType.Keep,
            plan.FirstNewSegId, plan.NewSegCount, dispositions);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Phase 0: 物理快照
    // ═══════════════════════════════════════════════════════════════

    /// <summary>对 lease 区间内每个段物理扫描 allocated ranges。</summary>
    private Dictionary<int, List<(long Start, long End)>> TakePhysicalSnapshots(
        CompactLease lease, CancellationToken ct)
    {
        var snapshots = new Dictionary<int, List<(long Start, long End)>>();
        foreach (var chunk in lease.Chunks)
        {
            ct.ThrowIfCancellationRequested();
            if (!SegmentExists(chunk.SegId))
            {
                snapshots[chunk.SegId] = new List<(long, long)>();
                continue;
            }

            // 造只读 IFileHandle 扫物理 allocated ranges，扫完立即 Dispose
            using var handle = OpenSourceHandle(chunk.SegId);
            var ranges = handle.EnumerateAllocatedRanges();
            var list = new List<(long Start, long End)>();
            // ★ L19（2026-08-22）：数据窗按 lease.DataEnd 裁剪（默认=End；全量 Compact 由引擎
            //   钳到 CommittedTail）——lease 上界扩到 GrowthLimit 只用于阻断贴边追加，
            //   PreallocateFile 预分配幻影区（未提交占位）不得进打包窗。
            var dataEnd = lease.DataEnd;
            var rangeStart = chunk.SegId == lease.Start.SegId ? lease.Start.Offset : 0;
            var rangeEnd = chunk.SegId == dataEnd.SegId
                ? dataEnd.Offset
                : handle.Length;
            rangeEnd = Math.Min(rangeEnd, handle.Length);
            foreach (var r in ranges)
            {
                var start = Math.Max(rangeStart, r.Start);
                var end = Math.Min(rangeEnd, r.End);
                if (end > start)
                    list.Add((start, end));
            }

            snapshots[chunk.SegId] = list;
        }

        return snapshots;
    }

    /// <summary>计算新段 segLimit——取 lease.Chunks 里最大的 OldGrowthLimit。</summary>
    private long ComputeSegLimit(CompactLease lease)
    {
        long segLimit = 0;
        foreach (var chunk in lease.Chunks)
        {
            if (chunk.OldGrowthLimit > segLimit) segLimit = chunk.OldGrowthLimit;
        }

        return segLimit <= 0 ? 1 : segLimit;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Phase 1: 拷贝
    // ═══════════════════════════════════════════════════════════════

    /// <summary>创建 newSegCount 个临时段 IFileHandle。</summary>
    private List<IFileHandle> CreateTempSegments(int firstNewSegId, int newSegCount, long segLimit,
        CancellationToken ct)
    {
        var handles = new List<IFileHandle>(newSegCount);
        for (int i = 0; i < newSegCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            int segId = firstNewSegId + i;
            handles.Add(CreateTempHandle(segId, segLimit));
        }

        return handles;
    }

    /// <summary>拷贝循环——基于物理快照把源段 allocated 区间搬到新段（累积到全局 migrationMap）。</summary>
    private void CopyData(
        Dictionary<int, List<(long Start, long End)>> snapshots,
        int firstNewSegId, int newSegCount, long segLimit, long totalLen,
        List<IFileHandle> tempHandles, AsyncOperation<CompactResult> op, CancellationToken ct,
        Dictionary<LogicalAddress, LogicalAddress?> migrationMap)
    {
        using var chunkBuf = new AlignedMemoryManager(CopyChunkSize, AlignmentConst.Alignment4K);
        Memory<byte> bufMem = chunkBuf.Memory;

        long copied = 0;
        foreach (var segId in snapshots.Keys.OrderBy(k => k))
        {
            ct.ThrowIfCancellationRequested();
            var ranges = snapshots[segId];
            if (ranges.Count == 0) continue;

            // 造只读源段 IFileHandle（整个段读一次）
            using var srcHandle = OpenSourceHandle(segId);

            foreach (var range in ranges)
            {
                long off = range.Start;
                long end = range.End;
                while (off < end)
                {
                    ct.ThrowIfCancellationRequested();
                    int chunkLen = (int)Math.Min(end - off, CopyChunkSize);
                    int n = srcHandle.Read(off, bufMem.Span.Slice(0, chunkLen));
                    if (n <= 0) break;

                    // 写入可能跨段边界——拆分多次写入，每次不超当前段剩余空间
                    int written = 0;
                    while (written < n)
                    {
                        int dstSegIdx = (int)((copied + written) / segLimit);
                        long dstOff = (copied + written) % segLimit;
                        long spaceInSeg = segLimit - dstOff;
                        int toWrite = (int)Math.Min(n - written, spaceInSeg);

                        tempHandles[dstSegIdx].Write(dstOff, bufMem.Span.Slice(written, toWrite));

                        int dstSegId = firstNewSegId + dstSegIdx;
                        migrationMap[new LogicalAddress(segId, off + written)] =
                            new LogicalAddress(dstSegId, dstOff);

                        written += toWrite;
                    }

                    copied += n;
                    off += n;
                    op.ReportProgress((double)copied / totalLen);
                }
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Phase 2: 旧段处置
    // ═══════════════════════════════════════════════════════════════

    /// <summary>分析 lease 区间内每段的处置方式（Delete / PunchHole）。</summary>
    private List<OldSegmentDisposition> AnalyzeSegDispositions(LeasePlan plan)
    {
        var result = new List<OldSegmentDisposition>();
        var lease = plan.Lease;
        int startSeg = lease.Start.SegId;
        int endSeg = lease.End.SegId;
        int newSegEndExclusive = plan.FirstNewSegId + plan.NewSegCount;

        foreach (var chunk in lease.Chunks)
        {
            // 只处理新段范围之外的旧段（被替换的旧段物理处置）
            if (chunk.SegId < newSegEndExclusive) continue;

            int segId = chunk.SegId;
            bool isFirst = segId == startSeg;
            bool isLast = segId == endSeg;
            long rangeStartInSeg = isFirst ? lease.Start.Offset : 0;
            // long rangeEndInSeg = isLast ? lease.End.Offset : chunk.OldRealSize;
            //
            // if (rangeStartInSeg == 0 && rangeEndInSeg == chunk.OldRealSize)
            // {
            //     result.Add(new OldSegmentDisposition(segId, OldSegmentDisposition.ModeDelete, 0, 0));
            // }
            // else
            // {
            //     result.Add(new OldSegmentDisposition(segId, OldSegmentDisposition.ModePunchHole,
            //         rangeStartInSeg, rangeEndInSeg));
            // }
        }

        return result;
    }

    /// <summary>处理旧段物理处置（Delete=删段, PunchHole=部分抹除）。</summary>
    private void ProcessOldSegDispositions(List<OldSegmentDisposition> dispositions)
    {
        foreach (var d in dispositions)
        {
            if (d.IsDelete)
            {
                // ★ 先 Remove（墓碑）+ Flush（排空在途 flusher 句柄）再删文件——顺序反了会撞
                //   ADS 写句柄的 Windows 共享违例（与引擎 DeleteSegment 入口同型，VII-1 家族）。
                try
                {


                }
                catch
                {
                    /* best effort */
                }
                DeleteSegment(d.SegId);
            }
            else
            {
                // PunchHole 抹除已搬走的区间
                long len = d.PunchEnd - d.PunchStart;
                if (len > 0)
                    PunchHoleSegment(d.SegId, d.PunchStart, len);
            }
        }
    }

    /// <summary>对指定段区间 PunchHole（读写短命句柄现造现弃——打洞含边零化 Write，须写权限）。</summary>
    private void PunchHoleSegment(int segId, long offset, long length)
    {
        if (!SegmentExists(segId)) return;
        using var handle = _fileSystem.Open(GetSegmentPath(segId), new FileOpenOptions
        {
            Access = AccessMode.ReadWrite,
            Mode = FileOpenMode.OpenExisting,
            Sharing = FileSharing.ReadWrite | FileSharing.Delete,
        });
        handle.PunchHole(offset, length);
    }
}
