namespace TC.Tier.Runtime.Storage.Compact;

internal sealed partial class DefaultCompactor
{
    // ═══════════════════════════════════════════════════════════════
    //  段元组持久化（新段自写——设计决策：元数据随新段走，fs 替换同步就位）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>元组写打开语义（对齐引擎 WriteSegmentTuple——缓冲读写、无提示，元组不在数据热路径）。</summary>
    private static readonly FileOpenOptions SegmentTupleWriteOptions = new()
    {
        Access = AccessMode.ReadWrite,
        Mode = FileOpenMode.OpenExisting,
        Sharing = FileSharing.ReadWrite | FileSharing.Delete,
    };

    /// <summary>
    /// 新段自写段元组——写临时段 FileExtra（xattr 随 inode / sidecar 随 TryMoveSidecar），
    /// promote（rename）时随文件同步就位——不再经引擎 tupleWriter 委托事后补写。
    /// <para>★ 时序：拷贝完成 + Flush 后调用（maxOffset/realSize 依赖拷贝结果）。</para>
    /// </summary>
    private void WriteTempSegmentMeta(IFileHandle tempHandle, long maxOffset, long growthLimit, long realSize)
    {
        var payload = SegmentTupleCodec.Encode(StableState.Ready, maxOffset, growthLimit, realSize,
            ReadOnlySpan<byte>.Empty);
        tempHandle.SetFileExtra(payload);
    }

    /// <summary>
    /// 恢复路径补写正式段元组（崩溃现场临时段可能无元数据——兼容窗口，直接 fs 写；无段元组时
    /// 引擎恢复回退 fileSize 权威，补写是保真增强非正确性必需）。
    /// </summary>
    private void WriteSegmentMetaDirect(int segId, long fileSize)
    {
        if (!SegmentExists(segId)) return;
        var payload = SegmentTupleCodec.Encode(StableState.Ready, maxOffset: fileSize, growthLimit: fileSize,
            realSize: fileSize, ReadOnlySpan<byte>.Empty);
        using var handle = _fileSystem.Open(GetSegmentPath(segId), SegmentTupleWriteOptions);
        handle.SetFileExtra(payload);
    }

    private void WriteSegmentMetaForRecoveredRangeSegments(IReadOnlyList<int> newSegIds)
    {
        foreach (var segId in newSegIds)
            WriteSegmentMetaDirect(segId, GetSegmentLength(segId));
    }

    private void WriteSegmentMetaForRecoveredFullSegments(IReadOnlyList<int> newSegIds)
    {
        foreach (var segId in newSegIds)
            WriteSegmentMetaDirect(segId, GetSegmentLength(segId));
    }

    // 元组同步强一致直写（D-11）——无后台 flusher 可排空，FlushMeta 退役。
}
