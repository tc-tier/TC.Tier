namespace TC.Tier.Runtime.Storage.Checkpoint;

internal sealed partial class ScanCheckpoint
{
    /// <summary>
    /// 空 Writer——WriteHeader/WriteSegment/WriteFooter 静默空操作。
    /// <para>扫盘是只读切面，不存在"写回"语义。保留 NoopWriter 而非 null 是为了接口完备。</para>
    /// </summary>
    private sealed class NoopWriter : IAddressTableWriter
    {
        public void WriteHeader(int minSegId, int segCount, long growthLimit)
        {
        }

        public void WriteSegment(in int segId ,in SegmentSpec spec)
        {
        }

        public void WriteFooter(LogicalAddress committedTail, LogicalAddress allocatedTail)
        {
        }
    }
}