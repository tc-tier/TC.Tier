namespace TC.Tier.Runtime.AddressSpace;

/// <summary>
/// 地址表写入委托——对称 <see cref="IAddressTableReader"/>，底层调此接口将段表持久化到存储设备。
/// </summary>
public interface IAddressTableWriter
{
    /// <summary>写入地址表头——段表规模信息（快照/远程场景存全，供后续免扫盘重建）。</summary>
    /// <param name="minSegId">最小段号。</param>
    /// <param name="segCount">段数。</param>
    /// <param name="growthLimit">段生长上限。</param>
    void WriteHeader(int minSegId, int segCount, long growthLimit);

    /// <summary>
    /// 写入段信息——上层在此写入段号、最小偏移、增长上限、最大偏移和稳定状态。
    /// </summary>
    /// <param name="segId">段号。</param>
    /// <param name="entry">段扫描条目（已通过不变量校验）。</param>
    void WriteSegment(in int segId,in SegmentSpec entry);

    /// <summary>写入地址表尾——两水位最终值 + 校验和等尾数据。</summary>
    /// <param name="committedTail">已提交水位（真实数据边界）。</param>
    /// <param name="allocatedTail">分配水位（新写入起点）。</param>
    void WriteFooter(LogicalAddress committedTail, LogicalAddress allocatedTail);
}