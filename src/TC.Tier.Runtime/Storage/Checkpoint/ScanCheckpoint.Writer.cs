namespace TC.Tier.Runtime.Storage.Checkpoint;

internal sealed partial class ScanCheckpoint
{
    /// <summary>
    /// 空 Writer——WriteHeader/WriteSegment/WriteFooter 静默空操作。
    /// <para>扫盘是只读切面，不存在"写回"语义。保留 NoopWriter 而非 null 是为了接口完备。</para>
    /// </summary>
    private sealed class NoopWriter : IAddressTableWriter
    {
        /// <summary>空操作——不写入任何头部数据，静默忽略 <paramref name="minSegId"/>/<paramref name="segCount"/>/<paramref name="growthLimit"/>。</summary>
        /// <param name="minSegId">最小段号（忽略）。</param>
        /// <param name="segCount">有效段数（忽略）。</param>
        /// <param name="growthLimit">本次生命周期段大小上限（字节，忽略）。</param>
        public void WriteHeader(int minSegId, int segCount, long growthLimit)
        {
        }

        /// <summary>空操作——不写入任何段记录，静默忽略 <paramref name="segId"/>/<paramref name="spec"/>。</summary>
        /// <param name="segId">段号（忽略）。</param>
        /// <param name="spec">段规格（忽略）。</param>
        public void WriteSegment(in int segId ,in SegmentSpec spec)
        {
        }

        /// <summary>空操作——不写入任何尾部水位，静默忽略 <paramref name="committedTail"/>/<paramref name="allocatedTail"/>。</summary>
        /// <param name="committedTail">提交尾水位（忽略）。</param>
        /// <param name="allocatedTail">分配尾水位（忽略）。</param>
        public void WriteFooter(LogicalAddress committedTail, LogicalAddress allocatedTail)
        {
        }
    }
}