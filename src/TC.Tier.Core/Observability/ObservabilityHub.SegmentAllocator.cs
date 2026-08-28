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

        /// <summary>SegmentAllocator 维度指标是否启用（Options.Metrics.Enabled &amp;&amp; EnableSegmentAllocatorMetrics 短路后的终值）。</summary>
        public bool IsEnabled => _enabled;

        /// <summary>Alloc 本次是否应采样（确定性百分比采样；维度关闭恒 false）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ShouldSampleAlloc() => _enabled && ShouldSample(ref _allocCtr, _rate);

        /// <summary>上报段分配计数（<c>segment_allocator.alloc</c>，含段号与尺寸标签）。</summary>
        /// <param name="segId">分配的段号。</param>
        /// <param name="size">段的尺寸（字节）。</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnSegmentAllocate(int segId, long size)
        {
            if (!_enabled) return;
            _sink.Counter("segment_allocator.alloc",
                [Kv("seg_id", segId.ToString()), Kv("size", size.ToString())]);
        }

        /// <summary>上报段释放计数（<c>segment_allocator.free</c>，含段号标签）。</summary>
        /// <param name="segId">释放的段号。</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnSegmentFree(int segId)
        {
            if (!_enabled) return;
            _sink.Counter("segment_allocator.free", [Kv("seg_id", segId.ToString())]);
        }

        /// <summary>上报空闲段列表深度（<c>segment_allocator.free_list_depth</c>，瞬时 Gauge）。</summary>
        /// <param name="depth">当前空闲段列表深度。</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnFreeListDepth(int depth)
        {
            if (!_enabled) return;
            _sink.Gauge("segment_allocator.free_list_depth", depth, []);
        }
    }
}
