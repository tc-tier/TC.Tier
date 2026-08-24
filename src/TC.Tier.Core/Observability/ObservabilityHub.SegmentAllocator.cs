using System.Runtime.CompilerServices;

namespace TC.Tier.Core.Observability;

public sealed partial class ObservabilityHub
{
    /// <summary>
    /// Segment 分配器维度视图——段表的 Segment 分配/释放/FreeList 深度（段表专用，非通用 Allocator 原语；
    /// 命名如实标明作用域，勿再泛化）。
    /// </summary>
    public sealed partial class SegmentAllocatorView
    {
        private readonly IMetricsSink _sink;
        private readonly int _rate;
        private readonly bool _enabled;
        private int _allocCtr;

        internal SegmentAllocatorView(IMetricsSink sink, int rate, bool enabled)
        { _sink = sink; _rate = rate; _enabled = enabled; }

        public bool IsEnabled => _enabled;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ShouldSampleAlloc() => _enabled && ShouldSample(ref _allocCtr, _rate);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnSegmentAllocate(int segId, long size)
        {
            if (!_enabled) return;
            _sink.Counter("segment_allocator.alloc",
                [Kv("seg_id", segId.ToString()), Kv("size", size.ToString())]);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnSegmentFree(int segId)
        {
            if (!_enabled) return;
            _sink.Counter("segment_allocator.free", [Kv("seg_id", segId.ToString())]);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnFreeListDepth(int depth)
        {
            if (!_enabled) return;
            _sink.Gauge("segment_allocator.free_list_depth", depth, []);
        }
    }
}
