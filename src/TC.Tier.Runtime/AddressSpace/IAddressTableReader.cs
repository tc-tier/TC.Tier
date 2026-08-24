namespace TC.Tier.Runtime.AddressSpace;

/// <summary>
/// 地址表读入委托——对称 <see cref="IAddressTableWriter"/>，底层调此接口从持久化数据恢复段表。
/// </summary>
public interface IAddressTableReader
{
    /// <summary>
    /// 读地址表头——返回 false = 无头/损坏。
    /// <para>★ 只返 growthLimit（建段表唯一硬需求）。tail/minSegId/segCount 由 ReadSegment 循环现算、ReadFooter 修正。</para>
    /// </summary>
    /// <param name="growthLimit">段生长上限。</param>
    /// <returns>是否成功读取地址表头。</returns>
    bool ReadHeader(out long growthLimit);

    /// <summary>
    /// 读段信息——返回 false = 无更多段/损坏。
    /// </summary>
    /// <param name="segId">段 ID。</param>
    /// <param name="spec">段扫描条目（已通过不变量校验）。</param>
    /// <returns>是否成功读取段信息。</returns>
    bool ReadSegment(out int segId, out SegmentSpec spec);

    /// <summary>
    /// 读地址表尾——返回 false = 无尾/损坏。
    /// <para>★ 两水位修正（可不给 null = 用段表默认值）：committedTail 修正真实水位、allocatedTail 修正分配起点。</para>
    /// </summary>
    /// <param name="committedTail">已提交水位修正值（null = 用段表默认）。</param>
    /// <param name="allocatedTail">分配水位修正值（null = 用段表默认）。</param>
    /// <returns>是否成功读取地址表尾。</returns>
    bool ReadFooter(out LogicalAddress? committedTail, out LogicalAddress? allocatedTail);
}
