namespace TC.Tier.Runtime.AddressSpace.Leases;

/// <summary>
/// 区间级操作租约——封装占住区间的三阶段协议（Commit/Rollback）。
/// <para>★ 持 IExtentLeaseSource（段表接口）引用，不持 Segment 引用。</para>
/// <para>★ kind 隐藏在 _extentState byte 高 4 bit——Commit/Rollback 按 Source 分发。</para>
/// </summary>
public readonly struct ExtentLease : IDisposable
{
    private readonly IExtentLeaseSource _source;
    private readonly long _start;
    private readonly long _end;
    private readonly byte _extentState;  // ExtentStateCode 编码（高 4 bit = Src）
    private readonly int _segId;
    /// <summary>★ L12（）：获取时刻的段 CompactVersion 快照——Commit/Rollback 时校验，
    /// 段被 Compact 原位重整（版本变）= 本 lease 的区间记录已随旧表消失，快速失败让上层重试。</summary>
    private readonly int _compactVersion;

    internal ExtentLease(IExtentLeaseSource source, int segId, long start, long end, byte extentState,
        int compactVersion = 0)
    {
        _source = source;
        _start = start;
        _end = end;
        _extentState = extentState;
        _segId = segId;
        _compactVersion = compactVersion;
    }

    internal readonly long Start => _start;
    internal readonly long End => _end;
    internal readonly int OwnerSegId => _segId;

    /// <summary>
    /// 提交——按 extentState 的 Source 分发到 {Kind}Commit。
    /// </summary>
    internal void Commit()
    {
        switch (ExtentStateCode.SourceOf(_extentState))
        {
            case ExtentStateCode.SrcAppend:
                _source.AppendCommit(_segId, _start, _end, _compactVersion);
                break;
            case ExtentStateCode.SrcWrite:
                _source.WriteCommit(_segId, _start, _end, _compactVersion);
                break;
            case ExtentStateCode.SrcReclaim:
                _source.ReclaimCommit(_segId, _start, _end, _compactVersion);
                break;
            case ExtentStateCode.SrcCompact:
                _source.CompactCommit(_segId, _start, _end, _compactVersion);
                break;
        }
    }

    /// <summary>
    /// 回滚——按 extentState 的 Source 分发到 {Kind}Rollback。
    /// </summary>
    internal void Rollback()
    {
        switch (ExtentStateCode.SourceOf(_extentState))
        {
            case ExtentStateCode.SrcAppend:
                _source.AppendRollback(_segId, _start, _end, _compactVersion);
                break;
            case ExtentStateCode.SrcWrite:
                _source.WriteRollback(_segId, _start, _end, _compactVersion);
                break;
            case ExtentStateCode.SrcReclaim:
                _source.ReclaimRollback(_segId, _start, _end, _compactVersion);
                break;
            case ExtentStateCode.SrcCompact:
                _source.CompactRollback(_segId, _start, _end, _compactVersion);
                break;
        }
    }

    public void Dispose() => Rollback();
}
