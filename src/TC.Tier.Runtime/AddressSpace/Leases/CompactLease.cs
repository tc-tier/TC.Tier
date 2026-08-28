using System.Buffers;

namespace TC.Tier.Runtime.AddressSpace.Leases;

/// <summary>
/// Compact 操作 lease——不支持 chunk 分阶段提交，必须整体原子提交。
/// <para>★ _extents + _chunks 数组走 ArrayPool（独立池化）。</para>
/// <para>★ 支持 Reset（池化复用）。</para>
/// </summary>
public sealed class CompactLease : IDisposable
{
    private ILeaseSource _source = null!;
    private ILogger? _logger;
    private CompactChunk[] _chunks = Array.Empty<CompactChunk>();
    private ExtentLease[] _extents = Array.Empty<ExtentLease>();
    private bool _extentsRented;
    private bool _chunksRented;
    private int _chunkCount;   // ★ _chunks 实际有效长度（ArrayPool 租的数组可能 > chunkCount）
    private int _state = (int)LeaseState.Active;

    /// <summary>Compact 区间起始逻辑地址（包含）。</summary>
    public LogicalAddress Start { get; private set; }

    /// <summary>Compact 区间结束逻辑地址（不包含）。</summary>
    public LogicalAddress End { get; private set; }

    /// <summary>lease 状态（Active / Committed / RolledBack——原子读）。</summary>
    public LeaseState State => (LeaseState)Volatile.Read(ref _state);

    /// <summary>
    /// ★ L19（）：整理<b>数据窗</b>终点（默认 = End）。
    /// <para>lease 上界扩到尾段 GrowthLimit 是为了阻断追加（占区间），数据打包仍需钳在
    /// CommittedTail——引擎全量 Compact 造 lease 后设置本值；compactor 拍快照/切分按
    /// DataEnd 裁剪，防 PreallocateFile 预分配幻影区（未提交占位）被当活数据打包。</para>
    /// </summary>
    public LogicalAddress DataEnd { get; internal set; }

    /// <summary>Compact 区间块列表——只返回前 <see cref="_chunkCount"/> 个（ArrayPool 数组尾部可能有 null）。</summary>
    public IReadOnlyList<CompactChunk> Chunks => new ArraySegment<CompactChunk>(_chunks, 0, _chunkCount);

    internal CompactLease(
        ILeaseSource source,
        LogicalAddress start,
        LogicalAddress end,
        ILogger? logger = null)
    {
        Reset(source, start, end, logger);
    }

    /// <summary>重置并重新占住——池化复用时调。</summary>
    internal void Reset(
        ILeaseSource source,
        LogicalAddress start,
        LogicalAddress end,
        ILogger? logger = null)
    {
        _source = source;
        Start = start;
        End = end;
        DataEnd = end;   // ★ L19：默认数据窗 = lease 区间（RangeCompact 不改；全量 Compact 由引擎设置）
        _logger = logger;
        _state = (int)LeaseState.Active;

        // ★ CompactLease 冷路径——需要 CompactChunk（含 OldGrowthLimit），用 List 版本可接受。
        //   热路径（LeaseBase）用 AcquireExtentsForLease 零中间分配。
        var ranges = source.GetExtentRanges(start, end);
        var chunkCount = ranges.Count;

        ReleaseChunks();
        _chunksRented = false;
        if (chunkCount > 0)
        {
            _chunks = ArrayPool<CompactChunk>.Shared.Rent(chunkCount);
            _chunksRented = true;
        }
        else
            _chunks = Array.Empty<CompactChunk>();

        ReleaseExtents();
        _extentsRented = false;
        if (chunkCount > 0)
        {
            _extents = ArrayPool<ExtentLease>.Shared.Rent(chunkCount);
            _extentsRented = true;
        }
        else
            _extents = Array.Empty<ExtentLease>();

        if (chunkCount == 0) { _chunkCount = 0; return; }
        _chunkCount = chunkCount;   // ReleaseChunks 之后赋值

        var acquired = 0;
        try
        {
            for (var i = 0; i < chunkCount; i++)
            {
                var chunk = ranges[i];
                _chunks[i] = chunk;
                _extents[i] = source.AcquireExtent(chunk.SegId, chunk.SegOff, chunk.SegEnd, ExtentStateCode.CompactLeased);
                acquired++;
            }
        }
        catch
        {
            for (var i = 0; i < acquired; i++) _extents[i].Rollback();
            throw;
        }
    }

    /// <summary>
    /// 整体原子提交（不支持 chunk 分阶段提交）——先校验全部 chunk 已填终态（半填拒绝提交），
    /// CAS 进 Committed 后先 CompactCommit（原位换内脏，锁内清场）再释放区间所有权。
    /// </summary>
    /// <exception cref="InvalidOperationException">任一 chunk 仍 Pending（漏填 SetReplacement/MarkInvalid）。</exception>
    public void Commit()
    {
        // ★ 完整性绊线（设计文档 §5.2）：任何 chunk 仍 Pending（漏填 SetReplacement/MarkInvalid）→
        //   拒绝提交。校验在 CAS 之前——失败时 lease 仍 Active，调用方走既有 Rollback/Dispose 收尾，现场零副作用。
        for (var i = 0; i < _chunkCount; i++)
            if (_chunks[i].State == CompactChunkState.Pending)
                throw new InvalidOperationException(
                    $"Compact lease chunk {i} (seg {_chunks[i].SegId}) 未填 SetReplacement/MarkInvalid——拒绝提交半填 lease");

        if (Interlocked.CompareExchange(ref _state, (int)LeaseState.Committed, (int)LeaseState.Active) !=
            (int)LeaseState.Active) return;

        // ★ L12 修复（）序列重排：先 CompactCommit（原位换内脏，锁内清场等待）
        //   再释放区间——旧序（先释放后替换）在两步之间开窗，写者钻入已释放区间与新布局冲突。
        //   新序下写者整个被 extent 互斥挡在提交之外，窗口不存在。
        var toInvalidate = new List<int>();
        var toReplace = new List<(int SegId, SegmentSpec Spec)>();
        for (var i = 0; i < _chunkCount; i++)
        {
            var chunk = _chunks[i];
            if (chunk.State == CompactChunkState.Invalid)
                toInvalidate.Add(chunk.SegId);
            else if (chunk.State == CompactChunkState.Replacement)
                toReplace.Add((chunk.SegId,
                    new SegmentSpec(chunk.NewMinOffset, chunk.NewGrowthLimit, chunk.NewMaxOffset,
                        preserveFrom: chunk.NewPreserveFrom)));
        }
        _source.CompactCommit(toInvalidate, toReplace);

        // 释放所有段占住的所有权（只遍历有效 chunk，ArrayPool 数组尾部可能是 null）
        // ★ 1.3：Compact 的 extent 级释放用 Rollback——因 CompactLeased 区间的 Commit 与 Rollback
        //   在段表实现里都映射到 ReleaseCompact（见 SegmentTable.ExtentLease.cs），行为等价。
        //   内脏已换，CompactLeased 记录随换表消失——此处 Rollback 走幂等路径（找不到即 no-op）。
        for (var i = 0; i < _chunkCount; i++)
        {
            try { _extents[i].Rollback(); }
            catch (Exception ex) { _logger?.LogWarning(ex, "Compact ExtentLease.Rollback 失败 segId={segId}", _chunks[i].SegId); }
        }

        ReleaseExtents();
        ReleaseChunks();
    }

    /// <summary>整体回滚——CAS 进 RolledBack 后逐一回滚 extent、通知源 CompactRollback、释放数组。</summary>
    public void Rollback()
    {
        if (Interlocked.CompareExchange(ref _state, (int)LeaseState.RolledBack, (int)LeaseState.Active) !=
            (int)LeaseState.Active) return;

        for (var i = 0; i < _chunkCount; i++)
        {
            try { _extents[i].Rollback(); }
            catch (Exception ex) { _logger?.LogWarning(ex, "Compact 回滚 ExtentLease.Rollback 失败 idx={idx}", i); }
        }
        _source.CompactRollback();

        ReleaseExtents();
        ReleaseChunks();
    }

    private void ReleaseExtents()
    {
        if (_extentsRented && _extents.Length > 0)
        {
            ArrayPool<ExtentLease>.Shared.Return(_extents, clearArray: true);
            _extentsRented = false;
        }
        _extents = Array.Empty<ExtentLease>();
    }

    private void ReleaseChunks()
    {
        if (_chunksRented && _chunks.Length > 0)
        {
            ArrayPool<CompactChunk>.Shared.Return(_chunks, clearArray: true);
            _chunksRented = false;
        }
        _chunks = Array.Empty<CompactChunk>();
        _chunkCount = 0;   // ★ 重置有效长度
    }

    /// <summary>释放——仍 Active 的 lease 自动走 Rollback（未提交的占住区间回滚）。</summary>
    public void Dispose()
    {
        if (Volatile.Read(ref _state) != (int)LeaseState.Active) return;
        Rollback();
    }
}
