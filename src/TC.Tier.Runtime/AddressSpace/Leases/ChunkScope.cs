using System.Runtime.CompilerServices;

namespace TC.Tier.Runtime.AddressSpace.Leases;

/// <summary>
/// chunk 视图 + 分段提交控制——完整迭代器模式（foreach / for / while 三态，）。
/// <para>★ 普通struct（非 ref）——foreach/for 循环体内可 await（CopyChunksAsync 兼容）。</para>
/// <para>★ 几何属性沿用 RangeChunk 命名（SegId/SegOff/SegEnd/Length）；
///   <see cref="Commit"/>/<see cref="Rollback"/> 显式分段终态（doneMask 保 exactly-once，重复调用安全）。</para>
/// <para>★ 消费方协议（三种等价写法）：</para>
/// <list type="bullet">
/// <item><c>foreach (var chunk in lease) { IO(chunk); chunk.Commit(); }</c>——pattern-based GetEnumerator，零分配</item>
/// <item><c>for (var i = 0; i &lt; lease.ChunkCount; i++) { var chunk = lease[i]; IO(chunk); chunk.Commit(); }</c>——索引器含物理门</item>
/// <item><c>while (iter.MoveNext()) { IO(iter.Current); iter.CommitCurrent(); }</c>——原 while 模式保留</item>
/// </list>
/// </summary>
public readonly struct ChunkScope
{
    private readonly LeaseBase _lease;
    private readonly ExtentLease _ext;
    private readonly int _index;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ChunkScope(LeaseBase lease, ExtentLease ext, int index)
    {
        _lease = lease;
        _ext = ext;
        _index = index;
    }

    /// <summary>该 chunk 所属段 ID。</summary>
    public int SegId => _ext.OwnerSegId;

    /// <summary>该 chunk 的段内起始偏移（包含）。</summary>
    public long SegOff => _ext.Start;

    /// <summary>该 chunk 的段内结束偏移（不包含）。</summary>
    public long SegEnd => _ext.End;

    /// <summary>该 chunk 的长度（字节）。</summary>
    public long Length => _ext.End - _ext.Start;

    /// <summary>
    /// 分段提交——chunk 终态迁移（Leased→Committed，区间当场归还/渐进可见）。
    /// <para>★ exactly-once：doneMask 仲裁，重复调用/对端路径已终态时 no-op。</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Commit() => _lease.OnChunkCommit(_index);

    /// <summary>
    /// 分段回滚——chunk 终态迁移（Leased→Wasted 可覆写空洞）。
    /// <para>★ exactly-once：doneMask 仲裁。</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Rollback() => _lease.OnChunkRollback(_index);
}
