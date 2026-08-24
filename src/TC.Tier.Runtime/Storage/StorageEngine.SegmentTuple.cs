namespace TC.Tier.Runtime.Storage;

/// <summary>
/// 段元组 partial——per-segment 状态经段文件 FileExtra 平面同步强一致直写（D-11/D-15）。
/// <para>★ 通道 = Core IO（磁盘 = xattr/ADS 或 per-file sidecar 原子换名，由 DiskMetadataMode 路由；
///   mem = 槽 blob 锁内原子）——引擎零通道感知。全介质强制写，不设开关。</para>
/// <para>★ 时机 = 段生命周期点（建段/段满/Reclaim 刷新/Compact 换段/Dispose 补写）——数据 chunk
///   路径零 FileExtra 调用。每写即持久（sidecar=tmp+Flush+原子换名），无脏窗。</para>
/// </summary>
internal sealed partial class StorageEngine
{
    /// <summary>元组写串行锁（原 EngineMeta._writeLock 语义降为引擎字段级——多线程写时点互斥）。</summary>
    private readonly object _tupleWriteLock = new();

    /// <summary>元组写打开语义（缓冲读写、无提示——元组不在数据热路径）。</summary>
    private static readonly FileOpenOptions TupleWriteOptions = new()
    {
        Access = AccessMode.ReadWrite,
        Mode = FileOpenMode.OpenExisting,
        Sharing = FileSharing.ReadWrite | FileSharing.Delete,
    };

    /// <summary>
    /// 写段元组（同步强一致——失败抛出，调用方按各自语义处理；保真刷新路径自行吞异常）。
    /// </summary>
    internal void WriteSegmentTuple(int segId, StableState state, long maxOffset, long growthLimit,
        long realSize, ReadOnlySpan<byte> summary)
    {
        var payload = SegmentTupleCodec.Encode(state, maxOffset, growthLimit, realSize, summary);
        lock (_tupleWriteLock)
        {
            using var handle = _fs.Open(SegmentFileName(segId), TupleWriteOptions);
            handle.SetFileExtra(payload);
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
