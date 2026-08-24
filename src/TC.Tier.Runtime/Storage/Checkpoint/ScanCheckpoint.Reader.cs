namespace TC.Tier.Runtime.Storage.Checkpoint;

internal sealed partial class ScanCheckpoint
{
    /// <summary>
    /// 段扫描 Reader——枚举拿 min/max 边界 + 游标遍历吐段，O(1) 内存（IO 全经引擎 fs 面）。
    /// <para>★ 枚举 ScanSegments 时只记 minSegId/maxSegId 两个标量（不记录集合）。
    ///   枚举天然正确（遍历真实文件，含孤岛/稀疏——不假设单调性）。</para>
    /// <para>★ Reader 按 [minSegId, maxSegId] 游标遍历，SegmentFileExists 跳空洞，存在即读元组吐段。
    ///   不持有段集合——仅 _cursor 游标 + _minSegId/_maxSegId 边界。</para>
    /// <para>★ 元组比较取值（min(tuple, fileSize)）——OS 文件大小是物理权威，元组只是参考。</para>
    /// <para>★ 进度条：枚举 + 吐段经 owner.RaiseProgress 上报。</para>
    /// <para>★ 不计算水位——ReadFooter 返回 null，两水位由 LoadAddressTable 算。</para>
    /// </summary>
    private sealed class StreamingSegmentReader : IAddressTableReader, IExtentSummaryProvider
    {
        private readonly ScanCheckpoint _owner;
        private readonly int _minSegId;
        private readonly int _maxSegId;
        private int _cursor;
        private bool _headerRead;
        private Dictionary<int, byte[]>? _extentSummaries;

        internal StreamingSegmentReader(ScanCheckpoint owner)
        {
            _owner = owner;
            (_minSegId, _maxSegId) = FindMinMax(owner);
            _cursor = _minSegId;
        }

        /// <summary>段区间摘要旁路（VII-3）——ReadSegment 期间从元组 FileExtra 捕获，LoadAddressTable 探测安装。null = 无。</summary>
        public IReadOnlyDictionary<int, byte[]>? ExtentSummaries => _extentSummaries;

        /// <summary>枚举段（引擎 fs 扫描面），只记 minSegId/maxSegId（不记录集合，O(1) 内存）。</summary>
        private static (int min, int max) FindMinMax(ScanCheckpoint owner)
        {
            owner._logger?.LogDebug("StreamingSegmentReader: 枚举段文件找边界");
            var engine = owner._storage;

            // 单段模式：只有一个 {engine} 文件（seg0），无 segId 后缀
            if (!engine.EnableSegmentation)
                return engine.SegmentFileExists(0) ? (0, 0) : (-1, -1);

            var min = -1;
            var max = -1;
            foreach (var (segId, _) in engine.ScanSegments())
            {
                if (min < 0 || segId < min) min = segId;
                if (segId > max) max = segId;
            }
            return (min, max);
        }

        /// <inheritdoc/>
        /// <remarks>★ <paramref name="growthLimit"/> 是<strong>本次生命周期的段大小上限</strong>
        ///（= 设备 SegmentGrowthLimit，IO 引擎 Initialize 必传）——<b>不是</b>扫描出来的历史段大小。
        /// 历史段的实际大小由 <see cref="ReadSegment"/> 逐段给出。</remarks>
        public bool ReadHeader(out long growthLimit)
        {
            _headerRead = true;
            // ★ 开始信号恒为设备本次生命周期的 SegmentGrowthLimit（IO 引擎 Initialize 必传，恢复 task 设
            //   SegmentGrowthLimit 后才首次访问 Reader，此时已就绪）。空设备/有历史段都一样——这是上限，不是扫描值。
            growthLimit = _owner._storage.SegmentGrowthLimit;

            if (_maxSegId < 0)
            {
                _owner.RaiseProgress(35, "空设备");
                return true;
            }

            // 仅定位首段存在性（ReadSegment 从 _minSegId 全程扫，不在此跳过首段）。
            //   旧行为 _cursor = segId+1 让 ReadSegment 跳过首段 → 多段恢复丢首段数据。
            for (var segId = _minSegId; segId <= _maxSegId; segId++)
            {
                if (!_owner._storage.SegmentFileExists(segId)) continue;
                long fileSize;
                try { fileSize = _owner._storage.SegmentFileLength(segId); }
                catch { continue; }
                if (fileSize == 0) continue;

                _owner.RaiseProgress(35, "首段定位");
                return true;
            }

            _owner.RaiseProgress(35, "空设备（无有效段）");
            return true;
        }

        /// <inheritdoc/>
        public bool ReadSegment(out int segId, out SegmentSpec spec)
        {
            spec = default!;
            segId = 0;

            if (!_headerRead) return false;

            var span = _maxSegId - _minSegId + 1;
            while (_cursor <= _maxSegId)
            {
                var sid = _cursor++;
                if (!_owner._storage.SegmentFileExists(sid)) continue;

                long fileSize;
                try { fileSize = _owner._storage.SegmentFileLength(sid); }
                catch { continue; }
                if (fileSize == 0) continue;

                var isLast = sid == _maxSegId;
                var (gl, mo, st) = ReadSegmentTupleValues(sid, fileSize, isLast);

                // ★ 命名构造（扫盘无头部回收信息，minOffset=0）——SegmentScanEntry 构造强校验
                spec = new SegmentSpec(minOffset: 0, growthLimit: gl, maxOffset: mo, stableState: st);
                segId = sid;

                _owner.RaiseProgress(40 + (int)((long)(sid - _minSegId + 1) * 20 / span), null);
                return true;
            }

            _owner.RaiseProgress(60, "扫盘完成");
            return false;
        }

        /// <inheritdoc/>
        public bool ReadFooter(out LogicalAddress? committedTail, out LogicalAddress? allocatedTail)
        {
            committedTail = null;
            allocatedTail = null;
            return _headerRead;
        }

        /// <summary>
        /// 读段元组比较取值——OS 文件大小是物理权威，元组只是信息记录（不能超过 fileSize）。
        /// <para>★ 元组来源 = 段文件 FileExtra（fs.Stat 全量读）；无/损坏 → 回退启发式（fileSize / isLast）。</para>
        /// </summary>
        private (long growthLimit, long maxOffset, StableState state) ReadSegmentTupleValues(int segId, long fileSize, bool isLast)
        {
            var tuple = _owner._storage.ReadSegmentTuple(segId);

            // ★ VII-3 extent 级保真：捕获段区间摘要（元组内联）——LoadAddressTable 探测安装精确洞布局
            if (tuple is { } t && t.Summary is { Length: > 0 })
                (_extentSummaries ??= new Dictionary<int, byte[]>())[segId] = t.Summary;

            var hasMeta = tuple is not null;
            var metaGrowth = tuple?.GrowthLimit ?? 0;
            var metaMaxOffset = tuple?.MaxOffset ?? 0;
            var metaState = tuple?.State ?? default;

            var growthLimit = hasMeta && metaGrowth > 0 ? Math.Min(metaGrowth, fileSize) : fileSize;

            long maxOffset;
            if (hasMeta && metaMaxOffset > 0)
                maxOffset = Math.Min(metaMaxOffset, fileSize);
            else if (!_owner._storage.PreallocateFile)
                maxOffset = fileSize;
            else
                maxOffset = isLast ? 0 : fileSize;

            var state = hasMeta && metaState != default ? metaState : (isLast ? StableState.Ready : StableState.Full);
            return (growthLimit, maxOffset, state);
        }
    }
}
