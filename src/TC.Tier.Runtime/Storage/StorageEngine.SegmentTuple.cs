using System.Collections.Concurrent;

namespace TC.Tier.Runtime.Storage;

/// <summary>
/// 段元组 partial——per-segment 状态经段文件 FileExtra 平面写（D-11/D-15）。
/// <para>★ 通道 = Core IO（磁盘 = xattr/ADS 或 per-file sidecar 原子换名，由 DiskMetadataMode 路由；
///   mem = 槽 blob 锁内原子）——引擎零通道感知。全介质强制写，不设开关。</para>
/// <para>★ 耐久化两形态（<see cref="StorageEngineOptions.MetaTupleFlushInterval"/>）：
///   生命周期高频写（建段初始/段满）= <b>缓存写</b>（写入即读——缓存管理器读己写恒成立；零 fsync
///   零等待，churn 卷上 fsync 延迟无界）+ 耐久化泵按周期 <see cref="FlushPendingSegmentTuples"/> 回扫
///   刷盘（崩溃窗口 ≤ 周期）；耐久锚点写（RefreshSegmentMetaExtents 刷新/Dispose 补写）= 同步持久写
///   （写即 fsync）。interval = Zero 退化为逐写持久（每生命周期点 fsync 旧形态）；Infinite 仅 Dispose 锚定。</para>
/// </summary>
internal sealed partial class StorageEngine
{
    /// <summary>元组写串行锁（原 EngineMeta._writeLock 语义降为引擎字段级——多线程写时点互斥）。</summary>
    private readonly object _tupleWriteLock = new();

    /// <summary>待刷盘段集合（生命周期缓存写后挂入——泵/Dispose 回扫 fsync）。锁 = <see cref="_metaFlushLock"/>。</summary>
    private readonly HashSet<int> _metaFlushPending = new();

    /// <summary>回扫泵启动闸（首个生命周期缓存写启动一次）。</summary>
    private int _metaPumpStarted;

    /// <summary>回扫泵/元组写并发保护（快照-清空-失败回挂的原子性）。</summary>
    private readonly object _metaFlushLock = new();

    /// <summary>元组写打开语义（缓冲读写、无提示——元组不在数据热路径）。</summary>
    private static readonly FileOpenOptions TupleWriteOptions = new()
    {
        Access = AccessMode.ReadWrite,
        Mode = FileOpenMode.OpenExisting,
        Sharing = FileSharing.ReadWrite | FileSharing.Delete,
    };

    /// <summary>
    /// 写段元组（同步持久写——耐久锚点专用：RefreshSegmentMetaExtents 刷新/Dispose 补写；
    /// 失败抛出，调用方按各自语义处理，保真刷新路径自行吞异常）。
    /// </summary>
    internal void WriteSegmentTuple(int segId, StableState state, long maxOffset, long growthLimit,
        long realSize, ReadOnlySpan<byte> summary)
    {
        var payload = SegmentTupleCodec.Encode(state, maxOffset, growthLimit, realSize, summary);
        WriteTuplePayloadNow(segId, payload, durable: true);
    }

    /// <summary>
    /// 生命周期元组缓存写（建段初始/段满——高频生命周期写，写入即读、零 fsync 零等待零句柄跨越）：
    /// 耐久化由回扫泵按 <see cref="StorageEngineOptions.MetaTupleFlushInterval"/> 周期刷盘
    /// （崩溃窗口 ≤ 周期，恢复 fileSize 权威回退）；interval = Zero 退化为逐写持久（旧形态）；
    /// Infinite 仅 Dispose 锚定。段排他锁 + 墓碑复核在回刷盘时点执行（<see cref="FlushPendingSegmentTuples"/>）
    /// ——生命周期调用点彻底与删除/读路径解耦。
    /// </summary>
    internal void MarkSegmentTupleDirty(int segId, StableState state, long maxOffset, long growthLimit,
        long realSize, ReadOnlySpan<byte> summary)
    {
        var payload = SegmentTupleCodec.Encode(state, maxOffset, growthLimit, realSize, summary);
        WriteTuplePayloadNow(segId, payload, durable: false);   // 缓存写——立即读

        var interval = _options.MetaTupleFlushInterval;
        if (interval == TimeSpan.Zero)
        {
            FlushSegmentTupleNow(segId);   // 逐写持久（旧形态）
            return;
        }
        if (interval == Timeout.InfiniteTimeSpan) return;   // 仅 Dispose 锚定——不回扫

        lock (_metaFlushLock) _metaFlushPending.Add(segId);
        if (Interlocked.Exchange(ref _metaPumpStarted, 1) == 0)
            StartMetaTuplePump();
    }

    /// <summary>丢弃某段的待刷盘登记（删除段时调用——回扫不触碰已死段）。</summary>
    private void DropPendingSegmentTuple(int segId)
    {
        lock (_metaFlushLock) _metaFlushPending.Remove(segId);
    }

    /// <summary>回扫泵——按配置周期把缓存写的段元组 fsync 持久化（崩溃窗口 ≤ 周期）。
    /// 段排他锁内墓碑复核（已删除/出表 → 丢弃）+ Compact 范围跳过（新段 meta 归 Compact）。
    /// fsync 在 churn 卷上延迟无界——泵线程独立承担，生命周期操作零等待。</summary>
    private void StartMetaTuplePump()
    {
        RunBackgroundTask(async ct =>
        {
            // ★ 时钟缝 件一 P1：泵周期经时钟供给源（假钟下由快进确定性触发；System 直通）
            try
            {
                while (true)
                {
                    await _clock.Delay(_options.MetaTupleFlushInterval, ct).ConfigureAwait(false);
                    FlushPendingSegmentTuples();
                }
            }
            catch (OperationCanceledException)
            {
                /* Dispose 取消——同步排水接管（DisposeOverride → FlushPendingSegmentTuples） */
            }
        });
    }

    /// <summary>回扫/排水一轮：待刷快照 → 逐段（廉价复核前置 → 段排他锁内终验 → fsync → 移除）。
    /// ★ Compact/删除复核必须在取锁之前——Compact 拷贝持段锁秒级，锁内才发现 under-compact 会让
    /// 泵在自旋锁上空转整个拷贝期（59s 测试实测）；under-compact 直接丢弃（Compact 完成后的
    /// RefreshSegmentMetaExtents 持久写最终 meta，回扫本就是 best-effort 兜底）。</summary>
    private void FlushPendingSegmentTuples()
    {
        int[] snapshot;
        lock (_metaFlushLock)
        {
            if (_metaFlushPending.Count == 0) return;
            snapshot = new int[_metaFlushPending.Count];
            _metaFlushPending.CopyTo(snapshot);
            _metaFlushPending.Clear();
        }
        foreach (var segId in snapshot)
        {
            // ★ 无锁廉价复核前置（自旋锁等待 = 烧 CPU）
            if (IsSegmentUnderCompact(segId))
                continue;   // Compact 换段中——新段 meta 归 Compact（Refresh 持久写），兜底弃权
            if (!_segmentTable.TryGetLock(segId, out var segLock) || segLock is null)
                continue;   // 段已出表（Hollow）——已删段
            if (segId < _segmentTable.MinSegId
                || !_segmentTable.TryGetSegment(segId, out var seg)
                || seg is not { IsValid: true })
                continue;   // 已被 ReclaimHead 删除——不触碰已死段
            segLock.AcquireExclusive();
            try
            {
                // 锁内终验（TOCTOU：检查到获取之间状态可能变化）
                if (segId < _segmentTable.MinSegId
                    || !_segmentTable.TryGetSegment(segId, out var seg2)
                    || seg2 is not { IsValid: true }
                    || IsSegmentUnderCompact(segId))
                    continue;
                FlushSegmentTupleNow(segId);
            }
            catch (Exception ex)
            {
                lock (_metaFlushLock) _metaFlushPending.Add(segId);   // 失败回挂——下轮重试（fsync 幂等）
                Logger?.LogWarning($"MetaTuple backscan: flush seg#{segId} failed, kept pending: {ex.Message}");
            }
            finally { segLock.ReleaseExclusive(); }
        }
    }

    /// <summary>fsync 段文件的缓存元组数据（不重写内容）。</summary>
    private void FlushSegmentTupleNow(int segId)
    {
        lock (_tupleWriteLock)
        {
            using var handle = _fs.Open(SegmentFileName(segId), TupleWriteOptions);
            if (handle is IFileHandleMetaDurability d) d.FlushFileExtra();
            else handle.Flush();
        }
    }

    /// <summary>元组物理写（开句柄 + SetFileExtra[+fsync（durable）]）——句柄存活期 = 本调用（不跨任何等待）。
    /// 非 durable 走缓存通道（<see cref="IFileHandleMetaDurability.SetFileExtraCached"/>；未实现介质回落持久写）。</summary>
    private void WriteTuplePayloadNow(int segId, byte[] payload, bool durable)
    {
        lock (_tupleWriteLock)
        {
            using var handle = _fs.Open(SegmentFileName(segId), TupleWriteOptions);
            if (!durable && handle is IFileHandleMetaDurability d) d.SetFileExtraCached(payload);
            else handle.SetFileExtra(payload);
        }
    }

    /// <summary>
    /// 读段元组（fs 级同平面——Stat 全量读，无需开句柄）；无/损坏 → null（恢复回退 fileSize 权威）。
    /// </summary>
    internal (StableState State, long MaxOffset, long GrowthLimit, long RealSize, byte[] Summary)? ReadSegmentTuple(
        int segId)
    {
        try
        {
            var extra = _fs.Stat(SegmentFileName(segId)).FileExtra;
            return extra.IsEmpty ? null : SegmentTupleCodec.Decode(extra.Span);
        }
        catch (FileIOException)
        {
            return null;   // NotFound 等——非致命
        }
    }
}
