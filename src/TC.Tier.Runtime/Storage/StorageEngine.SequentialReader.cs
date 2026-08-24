namespace TC.Tier.Runtime.Storage;

/// <summary>
/// SequentialReader partial——基类统一实现顺序读句柄（介质无关）。
/// <para>★ 游标 + 读/跳分离，自动跨段，Forward/Backward 双向，Consistent/DirtyRead 双模式。</para>
/// <para>★ 物理读走 <see>
///         <cref>IO.IFileHandle.Read/ReadAsync</cref>
///     </see>
///     ——磁盘 pread，内存 MemoryCopy。</para>
/// <para>★ DirtyRead 同步路径使用段共享锁 + epoch；异步路径不能跨 await 持有 thread-static epoch，
///   因此使用段共享锁，drain worker 的段独占锁提供同等物理互斥。</para>
/// </summary>
internal sealed partial class StorageEngine
{
    /// <inheritdoc/>
    public ISequentialReader OpenSequentialReader(LogicalAddress start, LogicalAddress end,
        ReadDirection direction = ReadDirection.Forward, bool usePageCache = true,
        SnapshotMode snapshotMode = SnapshotMode.Consistent)
    {
        ThrowIfDisposed();
        EnsureReady();
        return new SequentialReader(this, start, end, direction, usePageCache, snapshotMode);
    }

    /// <summary>
    /// 顺序读句柄——游标 + 读/跳分离，自动跨段，双模式快照。
    /// </summary>
    private sealed class SequentialReader : ISequentialReader
    {
        private readonly StorageEngine _owner;
        private readonly LogicalAddress _start;
        private readonly LogicalAddress _end;
        private readonly ReadDirection _direction;
        private readonly SnapshotMode _snapshotMode;
        private readonly bool _usePageCache;
        private LogicalAddress _position;
        private readonly List<SpinRWLock>? _lockedSegments;   // ★ 真正获取到的锁实例（解析一次持有；Dispose 只释放它们，不二次解析）
        private bool _disposed;

        public SequentialReader(StorageEngine device,
            LogicalAddress start, LogicalAddress end,
            ReadDirection direction, bool usePageCache,
            SnapshotMode snapshotMode)
        {
            _owner = device;
            _start = start;
            _end = end;
            _direction = direction;
            _snapshotMode = snapshotMode;
            _usePageCache = usePageCache;

            _position = direction == ReadDirection.Forward ? start : end;

            // Consistent 模式：构造时一次性锁住 [start, end] 所有段（共享锁），读期间不被 Compact/Reclaim 改
            if (snapshotMode == SnapshotMode.Consistent)
            {
                // ★ L20（）双相门：与 Compact 互斥——compact 入闸后等一致读者清零；
                //   构造期见 compacting 让位重试。锁实例对换内脏无效（L12 原位更新锁不失效），
                //   布局切换前的清场由本门保证（锁仍保留——挡 ReclaimTail/ReclaimHead 物理变更）。
                var spinner = new SpinWait();
                while (true)
                {
                    while (Volatile.Read(ref _owner._compacting) != 0)
                        spinner.SpinOnce();
                    Interlocked.Increment(ref _owner._consistentReaders);
                    if (Volatile.Read(ref _owner._compacting) != 0)
                    {
                        Interlocked.Decrement(ref _owner._consistentReaders);
                        continue;
                    }
                    break;
                }
                _lockedSegments = LockRange(start, end);
            }
        }

        public LogicalAddress Position => _position;
        public LogicalAddress Start => _start;
        public LogicalAddress End => _end;
        public ReadDirection Direction => _direction;
        public SnapshotMode SnapshotMode => _snapshotMode;

        public int Read(Span<byte> destination)
        {
            ThrowIfDisposed();
            if (destination.Length == 0) return 0;
            if (IsAtEnd()) return 0;

            return _direction == ReadDirection.Forward
                ? ReadForward(destination)
                : ReadBackward(destination);
        }

        public ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct)
        {
            ThrowIfDisposed();
            if (destination.Length == 0) return ValueTask.FromResult(0);
            if (IsAtEnd()) return ValueTask.FromResult(0);

            return _direction == ReadDirection.Forward
                ? ReadForwardAsync(destination, ct)
                : ReadBackwardAsync(destination, ct);
        }

        public void Skip(long length)
        {
            ThrowIfDisposed();
            if (length <= 0) return;

            if (_direction == ReadDirection.Forward)
                SkipForward(length);
            else
                SkipBackward(length);
        }

        public void Seek(LogicalAddress target)
        {
            ThrowIfDisposed();
            if (!InRange(target))
                throw new ArgumentOutOfRangeException(nameof(target));

            var seg = _owner._segmentTable.GetSegment(target.SegId);
            if (seg.StableState == StableState.Invalid)
                throw new PartitionInvalidException("Segment not found.", target);

            _position = target;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_snapshotMode == SnapshotMode.Consistent)
                Interlocked.Decrement(ref _owner._consistentReaders);   // L20 双相门配对
            if (_lockedSegments == null) return;
            // ★ 只释放构造时真正获取到的实例——不重新 TryGetLock（两阶段解析翻转 = 无配对释放 → 读计数下溢楔死）
            foreach (var segLock in _lockedSegments)
                segLock.ReleaseShared();
        }

        // ═══════════════════════════════════════════════════════════
        //  Forward sync read
        // ═══════════════════════════════════════════════════════════

        private int ReadForward(Span<byte> destination)
        {
            int totalLen = destination.Length;
            int dstOffset = 0;

            while (dstOffset < totalLen)
            {
                int segId = _position.SegId;
                var seg = _owner._segmentTable.GetSegment(segId);
                if (seg.StableState == StableState.Invalid)
                    throw new PartitionInvalidException("Segment not found.", _position);

                long segRemaining = seg.RealSize - _position.Offset;
                if (segRemaining <= 0)
                {
                    segId++;
                    _position = new LogicalAddress(segId, 0);
                    continue;
                }

                long toEnd = DistanceToEnd(segId, _position.Offset);
                if (toEnd <= 0) break;

                int chunkLen = (int)Math.Min(totalLen - dstOffset, Math.Min(segRemaining, toEnd));

                // DirtyRead：每段读前 AcquireShared（防 Compact 排他）+ Epoch.Resume（防 PunchHole 物理销毁）
                // Consistent：构造时已一次性锁全程，这里不再加锁
                bool ownLock = _snapshotMode == SnapshotMode.DirtyRead;
                SpinRWLock? heldLock = null;   // ★ 解析一次持有引用——释放只认它，不二次 TryGetLock（防两阶段翻转无配对释放）
                if (ownLock)
                {
                    if (_owner._segmentTable.TryGetLock(segId, out var segLock) && segLock is not null)
                    {
                        segLock.AcquireShared();
                        heldLock = segLock;
                    }
                    _owner._epoch.Resume();
                }
                try
                {
                    using var handle = _owner.GetReadHandle(segId, _usePageCache);
                    int n = handle.Read(_position.Offset, destination.Slice(dstOffset, chunkLen));
                    dstOffset += n;
                    _position = new LogicalAddress(segId, _position.Offset + n);
                    if (n < chunkLen) break;
                }
                finally
                {
                    if (ownLock)
                    {
                        heldLock?.ReleaseShared();
                        _owner._epoch.Suspend();
                    }
                }
            }

            return dstOffset;
        }

        // ═══════════════════════════════════════════════════════════
        //  Forward async read
        // ═══════════════════════════════════════════════════════════

        private async ValueTask<int> ReadForwardAsync(Memory<byte> destination, CancellationToken ct)
        {
            int totalLen = destination.Length;
            int dstOffset = 0;

            while (dstOffset < totalLen)
            {
                ct.ThrowIfCancellationRequested();
                int segId = _position.SegId;
                var seg = _owner._segmentTable.GetSegment(segId);
                if (seg.StableState == StableState.Invalid)
                    throw new PartitionInvalidException("Segment not found.", _position);

                long segRemaining = seg.RealSize - _position.Offset;
                if (segRemaining <= 0)
                {
                    segId++;
                    _position = new LogicalAddress(segId, 0);
                    continue;
                }

                long toEnd = DistanceToEnd(segId, _position.Offset);
                if (toEnd <= 0) break;

                int chunkLen = (int)Math.Min(totalLen - dstOffset, Math.Min(segRemaining, toEnd));

                bool ownLock = _snapshotMode == SnapshotMode.DirtyRead;
                SpinRWLock? heldLock = null;   // ★ 解析一次持有引用——释放只认它（防两阶段翻转无配对释放）
                if (ownLock)
                {
                    if (_owner._segmentTable.TryGetLock(segId, out var segLock) && segLock is not null)
                    {
                        segLock.AcquireShared();
                        heldLock = segLock;
                    }
                }
                try
                {
                    using var handle = _owner.GetReadHandle(segId, _usePageCache);
                    int n = await handle.ReadAsync(_position.Offset, destination.Slice(dstOffset, chunkLen), ct)
                        .ConfigureAwait(false);
                    dstOffset += n;
                    _position = new LogicalAddress(segId, _position.Offset + n);
                    if (n < chunkLen) break;
                }
                finally
                {
                    if (ownLock)
                        heldLock?.ReleaseShared();
                }
            }

            return dstOffset;
        }

        // ═══════════════════════════════════════════════════════════
        //  Backward sync read
        // ═══════════════════════════════════════════════════════════

        private int ReadBackward(Span<byte> destination)
        {
            int totalLen = destination.Length;
            int dstOffset = 0;

            while (dstOffset < totalLen)
            {
                int segId = _position.SegId;
                var seg = _owner._segmentTable.GetSegment(segId);
                if (seg.StableState == StableState.Invalid)
                    throw new PartitionInvalidException("Segment not found.", _position);

                long segAvailable = _position.Offset;
                if (segAvailable <= 0)
                {
                    segId--;
                    seg = _owner._segmentTable.GetSegment(segId);   // 前一段，读 RealSize（SegmentView）
                    _position = new LogicalAddress(segId, seg.RealSize);
                    continue;
                }

                long fromStart = DistanceFromStart(segId, _position.Offset);
                if (fromStart <= 0) break;

                int chunkLen = (int)Math.Min(totalLen - dstOffset, Math.Min(segAvailable, fromStart));
                long readOffset = _position.Offset - chunkLen;

                bool ownLock = _snapshotMode == SnapshotMode.DirtyRead;
                SpinRWLock? heldLock = null;   // ★ 解析一次持有引用——释放只认它（防两阶段翻转无配对释放）
                if (ownLock)
                {
                    if (_owner._segmentTable.TryGetLock(segId, out var segLock) && segLock is not null)
                    {
                        segLock.AcquireShared();
                        heldLock = segLock;
                    }
                    _owner._epoch.Resume();
                }
                try
                {
                    using var handle = _owner.GetReadHandle(segId, _usePageCache);
                    Span<byte> buf = destination.Slice(totalLen - dstOffset - chunkLen, chunkLen);
                    int n = handle.Read(readOffset, buf);
                    dstOffset += n;
                    _position = new LogicalAddress(segId, readOffset);
                    if (n < chunkLen) break;
                }
                finally
                {
                    if (ownLock)
                    {
                        heldLock?.ReleaseShared();
                        _owner._epoch.Suspend();
                    }
                }
            }

            return dstOffset;
        }

        // ═══════════════════════════════════════════════════════════
        //  Backward async read
        // ═══════════════════════════════════════════════════════════

        private async ValueTask<int> ReadBackwardAsync(Memory<byte> destination, CancellationToken ct)
        {
            int totalLen = destination.Length;
            int dstOffset = 0;

            while (dstOffset < totalLen)
            {
                ct.ThrowIfCancellationRequested();
                var segId = _position.SegId;
                if (_owner._segmentTable.TryGetSegment(segId, out var seg) && seg is { IsValid: true })
                {
                    if (seg.Value.StableState == StableState.Invalid)
                        throw new PartitionInvalidException("Segment not found.", _position);
                }
                else
                {
                    // ★ L23 防御推进（）：缺失段退到前段段首——旧实现空转死循环
                    //   （dstOffset/位置都不变；当前运行期无摘索引路径，潜伏缺陷）。
                    _position = new LogicalAddress(segId - 1, 0);
                    continue;
                }


                long segAvailable = _position.Offset;
                if (segAvailable <= 0)
                {
                    segId--;
                    seg = _owner._segmentTable.GetSegment(segId); // 前一段，读 RealSize
                    _position = new LogicalAddress(segId, seg.Value.RealSize);
                    continue;
                }

                long fromStart = DistanceFromStart(segId, _position.Offset);
                if (fromStart <= 0) break;

                int chunkLen = (int)Math.Min(totalLen - dstOffset, Math.Min(segAvailable, fromStart));
                long readOffset = _position.Offset - chunkLen;

                bool ownLock = _snapshotMode == SnapshotMode.DirtyRead;
                SpinRWLock? heldLock = null;   // ★ 解析一次持有引用——释放只认它（防两阶段翻转无配对释放）
                if (ownLock)
                {
                    if (_owner._segmentTable.TryGetLock(segId, out var segLock) && segLock is not null)
                    {
                        segLock.AcquireShared();
                        heldLock = segLock;
                    }
                }
                try
                {
                    using var handle = _owner.GetReadHandle(segId, _usePageCache);
                    Memory<byte> buf = destination.Slice(totalLen - dstOffset - chunkLen, chunkLen);
                    int n = await handle.ReadAsync(readOffset, buf, ct).ConfigureAwait(false);
                    dstOffset += n;
                    _position = new LogicalAddress(segId, readOffset);
                    if (n < chunkLen) break;
                }
                finally
                {
                    if (ownLock)
                        heldLock?.ReleaseShared();
                }
            }

            return dstOffset;
        }

        // ═══════════════════════════════════════════════════════════
        //  Skip
        // ═══════════════════════════════════════════════════════════

        private void SkipForward(long length)
        {
            long remaining = length;
            while (remaining > 0)
            {
                int segId = _position.SegId;
                if (_owner._segmentTable.TryGetSegment(segId, out var seg) && seg is { IsValid: true })
                {
                    long segRemaining = seg.Value.RealSize - _position.Offset;
                    long toEnd = DistanceToEnd(segId, _position.Offset);

                    if (segRemaining <= 0 || toEnd <= 0) break;

                    long step = Math.Min(remaining, Math.Min(segRemaining, toEnd));
                    remaining -= step;
                    long newOff = _position.Offset + step;

                    if (newOff >= seg.Value.RealSize)
                    {
                        segId++;
                        _position = new LogicalAddress(segId, 0);
                    }
                    else
                    {
                        _position = new LogicalAddress(segId, newOff);
                    }
                }
                else
                {
                    // ★ L23 防御推进（）：段缺失时游标前进——旧实现空转死循环
                    //   （当前运行期无摘索引路径，潜伏缺陷；索引考古曾实锤空洞段窗口）。
                    _position = new LogicalAddress(segId + 1, 0);
                }
            }
        }

        private void SkipBackward(long length)
        {
            long remaining = length;
            while (remaining > 0)
            {
                int segId = _position.SegId;
                long segAvailable = _position.Offset;
                long fromStart = DistanceFromStart(segId, _position.Offset);

                if (segAvailable <= 0 || fromStart <= 0) break;

                long step = Math.Min(remaining, Math.Min(segAvailable, fromStart));
                remaining -= step;
                long newOff = _position.Offset - step;

                if (newOff <= 0)
                {
                    segId--;
                    if (!_owner._segmentTable.TryGetSegment(segId, out var seg) || seg is not { IsValid: true })
                    {
                        // ★ L23 防御推进：缺失段退到段首（RealSize 未知）——下一轮 segAvailable==0 自然 break
                        _position = new LogicalAddress(segId, 0);
                        continue;
                    }
                    var prevSeg = seg.Value;
                    _position = new LogicalAddress(segId, prevSeg.RealSize);
                }
                else
                {
                    _position = new LogicalAddress(segId, newOff);
                }
            }
        }

        // ── Helpers ──

        private bool IsAtEnd()
        {
            return _direction == ReadDirection.Forward
                ? _position >= _end
                : _position <= _start;
        }

        private bool InRange(LogicalAddress addr)
        {
            return _direction == ReadDirection.Forward
                ? addr >= _start && addr < _end
                : addr > _start && addr <= _end;
        }

        private long DistanceToEnd(int segId, long segOff)
        {
            if (segId == _end.SegId)
                return _end.Offset - segOff;
            if (segId < _end.SegId)
                return long.MaxValue;
            return 0;
        }

        private long DistanceFromStart(int segId, long segOff)
        {
            if (segId == _start.SegId)
                return segOff - _start.Offset;
            if (segId > _start.SegId)
                return long.MaxValue;
            return 0;
        }

        private List<SpinRWLock> LockRange(LogicalAddress start, LogicalAddress end)
        {
            var locked = new List<SpinRWLock>();
            for (int segId = start.SegId; segId <= end.SegId; segId++)
            {
                // ★ 只记录真正获取到的实例（TryGetLock 失败不加）——Dispose 只释放它们，配对由此保证
                if (_owner._segmentTable.TryGetLock(segId, out var segLock) && segLock is not null)
                {
                    segLock.AcquireShared();
                    locked.Add(segLock);
                }
            }
            return locked;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                ObjectDisposedException.ThrowIf(true, nameof(SequentialReader));
        }
    }
}