
namespace TC.Tier.Runtime.Benchmarks.Storage;

/// <summary>
/// 精简版 MemoryAddressTableReader（照 tests 的 MemoryAddressTableReader 抄）。
/// Benchmarks 项目访问不到 Tests 的 internal 类，故复制一份。
/// <para>★ 2026-08-14 协议硬化：改用 <see cref="SegmentEntry"/> 命名构造（旧 5 元组把
///   growthLimit 喂进 minOffset 槽——第四处位序错位潜伏实例，被强校验当场抓获）。</para>
/// </summary>
internal sealed class BenchMemoryReader : IAddressTableReader
{
    private readonly List<(int SegId,SegmentSpec spec)> _segments;
    private int _index;
    private readonly long _growthLimit;
    internal BenchMemoryReader(long growthLimit, List<(int SegId,SegmentSpec spec)> segments)
    {
        _growthLimit = growthLimit;
        _segments = segments; _index = 0;
    }

    public bool ReadHeader(out long growthLimit)
    {
        growthLimit = _growthLimit;
        return true;
    }

    public bool ReadSegment(out int segId, out SegmentSpec entry)
    {
        if (_index >= _segments.Count)
        {
            segId = 0; entry = default!; return false;
        }
        (segId, entry) = _segments[_index++];
        return true;
    }

    public bool ReadFooter(out LogicalAddress? committedTail, out LogicalAddress? allocatedTail)
    {
        committedTail = null; allocatedTail = null;
        return true;
    }

    /// <summary>便捷构建：创建段表并恢复（末段 committedOffset=0，有空间可 Append）。</summary>
    internal static SegmentTable Build(int segCount, long growthLimit = 1000, long committedOffset = 0)
        => BuildWithLifecycle(segCount, growthLimit, committedOffset, null);

    /// <summary>带 lifecycle 构造——worker 启动（LoadAddressTable 内部自动 StartLifecycle）。</summary>
    private static SegmentTable BuildWithLifecycle(
        int segCount, long growthLimit, long committedOffset, ISegmentLifecycle? lifecycle, int? maxInFlight = null)
    {
        var reader = BuildReader(segCount, growthLimit, committedOffset);
        var registry = new SegmentTable(SegmentTableSettings.Default);
        registry.LoadAddressTable(reader);
        return registry;
    }

    /// <summary>构建 reader（不加载）——调用方自己 LoadAddressTable。</summary>
    private static BenchMemoryReader BuildReader(int segCount, long growthLimit = 1000, long committedOffset = 0)
    {
        var segments = new List<(int SegId,SegmentSpec spec)>();
        for (int i = 0; i < segCount; i++)
        {
            var off = i == segCount - 1 ? committedOffset : growthLimit;   // 末段留空间，其余满段
            segments.Add((i, new SegmentSpec( minOffset: 0, growthLimit, maxOffset: off, StableState.Ready)));
        }
        return new BenchMemoryReader(growthLimit,segments);
    }
}
