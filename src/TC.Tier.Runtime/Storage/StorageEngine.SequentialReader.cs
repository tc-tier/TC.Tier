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
        _faults?.OnOpEnter("OpenSequentialReader");
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

        /// <summary>从当前游标读取 destination.Length 字节，读后游标自动推进（跨段自动，方向随 <see cref="Direction"/>）。</summary>
        /// <param name="destination">目标缓冲区（长度可为 0——直接返回 0）。</param>
        /// <returns>实际读取的字节数（可能 0 = 已到 <see cref="End"/> 边界）。</returns>
        public int Read(Span<byte> destination)
        {
            ThrowIfDisposed();
            if (destination.Length == 0) return 0;
            if (IsAtEnd()) return 0;

            return _direction == ReadDirection.Forward
                ? ReadForward(destination)
                : ReadBackward(destination);
        }

        /// <summary>异步从当前游标读取（语义同 <see cref="Read"/>，物理读走 <see cref="IFileHandle.ReadAsync"/>）。</summary>
        /// <param name="destination">目标缓冲区（长度可为 0——直接完成并返回 0）。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>完成后得到实际读取的字节数（可能 0 = 已到 <see cref="End"/> 边界）。</returns>
        public ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct)
        {
            ThrowIfDisposed();
            if (destination.Length == 0) return ValueTask.FromResult(0);
            if (IsAtEnd()) return ValueTask.FromResult(0);

            return _direction == ReadDirection.Forward
                ? ReadForwardAsync(destination, ct)
                : ReadBackwardAsync(destination, ct);
        }

        /// <summary>相对移动游标 length 字节，不读数据（正序前进 / 倒序后退，跨段自动）。</summary>
        /// <param name="length">要跳过的字节数（非正数时静默忽略）。</param>
        public void Skip(long length)
        {
            ThrowIfDisposed();
            if (length <= 0) return;

            if (_direction == ReadDirection.Forward)
                SkipForward(length);
            else
                SkipBackward(length);
        }

        /// <summary>绝对地址跳转——把游标定位到任意地址（方向不变）。</summary>
        /// <param name="target">目标地址（必须在 [Start, End] 范围内且段有效）。</param>
        /// <exception cref="ArgumentOutOfRangeException">target 越界（不在读范围内）。</exception>
        /// <exception cref="PartitionInvalidException">target 指向的段已不存在（Invalid）。</exception>
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

        /// <summary>释放顺序读句柄——Consistent 模式下退出 Compact 双相门并释放构造时持有的全部段共享锁（幂等）。</summary>
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
                {
                    // ★ Hollow 段（已回收，占位连续）段首跨到下段继续读——cursor 起点锚点在
                    //   回收段时仍能读到其后实数据（skip 同语义）；带界：越过读窗口尾即 EOF
                    //   （截断后 meta 未夹的物理尾 < end 场景）。段内越界（offset > 0）= 真错误。
                    if (_position.Offset == 0)
                    {
                        if (DistanceToEnd(segId, 0) <= 0) break;
                        _position = new LogicalAddress(segId + 1, 0);
                        continue;
                    }
                    throw new PartitionInvalidException("Segment not found.", _position);
                }

                // ★ EOF 先于跳段检查：Position 恰好 = End（截断后 committed 夹到边界/帧尾=end）
                //   时 segRemaining 可能 = 0——先 break（EOF）而非跳段（跳段会撞不存在段 throw）。
                long toEnd = DistanceToEnd(segId, _position.Offset);
                if (toEnd <= 0) break;

                // ★ 段内数据剩余 = View.MaxOffset - Offset（★语义层次：位置计算用布局游标
                //   MaxOffset——cursor 只需知道数据在哪；VisibleOffset 是可见性门语义
                //   （读句柄层职责）——两字段语义不同层，位置计算误用可见门 = 提前停丢读）
                long segRemaining = seg.MaxOffset - _position.Offset;
                if (segRemaining <= 0)
                {
                    segId++;
                    _position = new LogicalAddress(segId, 0);
                    continue;
                }

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
                    // ★ 游标推进走段表算术（教义：地址 ± 走引擎/段表计算方法——Hollow 段占位
                    //   语义 + 恰好填满停驻 (seg, limit) 规范形；n 已被 chunkLen≤segRemaining
                    //   夹逼在段内，与段表算术等价且不再依赖隐式夹逼链）
                    _position = _owner._segmentTable.AdvanceAddress(_position, n);
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
                {
                    // ★ Hollow 段段首跨段继续读（同 ReadForward——占位连续 + 带界 EOF）
                    if (_position.Offset == 0)
                    {
                        if (DistanceToEnd(segId, 0) <= 0) break;
                        _position = new LogicalAddress(segId + 1, 0);
                        continue;
                    }
                    throw new PartitionInvalidException("Segment not found.", _position);
                }

                // ★ EOF 先于跳段检查：Position 恰好 = End（截断后 committed 夹到边界/帧尾=end）
                //   时 segRemaining 可能 = 0——先 break（EOF）而非跳段（跳段会撞不存在段 throw）。
                long toEnd = DistanceToEnd(segId, _position.Offset);
                if (toEnd <= 0) break;

                // ★ 段内数据剩余 = View.MaxOffset - Offset（★语义层次：位置计算用布局游标
                //   MaxOffset——cursor 只需知道数据在哪；VisibleOffset 是可见性门语义
                //   （读句柄层职责）——两字段语义不同层，位置计算误用可见门 = 提前停丢读）
                long segRemaining = seg.MaxOffset - _position.Offset;
                if (segRemaining <= 0)
                {
                    segId++;
                    _position = new LogicalAddress(segId, 0);
                    continue;
                }

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
                    await using var handle = _owner.GetReadHandle(segId, _usePageCache);
                    var n = await handle.ReadAsync(_position.Offset, destination.Slice(dstOffset, chunkLen), ct)
                        .ConfigureAwait(false);
                    dstOffset += n;
                    // ★ 游标推进走段表算术（同 ReadForward——Hollow 占位 + 停驻规范形）
                    _position = _owner._segmentTable.AdvanceAddress(_position, n);
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
                    // ★ 跨段回退：TryGetSegment 一步到位——实段=MaxOffset（数据末——布局语义）/
                    //   段不存在=SegmentGrowthLimit（占位末——地址空间连续）
                    _position = new LogicalAddress(segId,
                        _owner._segmentTable.TryGetSegment(segId, out var prevSeg) && prevSeg is { IsValid: true }
                            ? prevSeg.Value.MaxOffset
                            : _owner._segmentTable.SegmentGrowthLimit(segId));
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
                    var buf = destination.Slice(totalLen - dstOffset - chunkLen, chunkLen);
                    var n = handle.Read(readOffset, buf);
                    dstOffset += n;
                    // ★ 游标推进走段表算术：RetreatAddress(_position, chunkLen) == (segId, readOffset)
                    //   （chunkLen ≤ segAvailable 夹逼段内，借位语义 Hollow 占位——等价替换）
                    _position = _owner._segmentTable.RetreatAddress(_position, chunkLen);
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
                    // ★ 段缺失 = 数据边界到达（ReclaimHead 已回收）：安静停止——与 SkipForward
                    //   同款语义。禁"退到前段段首继续"式防御推进：下轮 segAvailable==0 走跳段
                    //   分支会跳过前段全部数据（漏读）；原实现即此病灶（L23 残留）。
                    break;
                }


                long segAvailable = _position.Offset;
                if (segAvailable <= 0)
                {
                    segId--;
                    // ★ 跨段回退：TryGetSegment 一步到位（同 ReadBackward）
                    _position = new LogicalAddress(segId,
                        _owner._segmentTable.TryGetSegment(segId, out var prevSeg) && prevSeg is { IsValid: true }
                            ? prevSeg.Value.MaxOffset
                            : _owner._segmentTable.SegmentGrowthLimit(segId));
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
                    await using var handle = _owner.GetReadHandle(segId, _usePageCache);
                    var buf = destination.Slice(totalLen - dstOffset - chunkLen, chunkLen);
                    var n = await handle.ReadAsync(readOffset, buf, ct).ConfigureAwait(false);
                    dstOffset += n;
                    // ★ 游标推进走段表算术（同 ReadBackward——RetreatAddress == (segId, readOffset)）
                    _position = _owner._segmentTable.RetreatAddress(_position, chunkLen);
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
                    // ★ 段内数据剩余（同 ReadForward——MaxOffset 布局游标）
                    long segRemaining = seg.Value.MaxOffset - _position.Offset;
                    long toEnd = DistanceToEnd(segId, _position.Offset);

                    if (toEnd <= 0) break;

                    if (segRemaining <= 0)
                    {
                        // ★ 段末停驻（AdvanceAddress 恰好填满规范形）→ 跨到下段原点继续——
                        //   (N,0) = 段首原点身份（读游标合法形态；区间端点才要求停驻形）。
                        //   不能 break：remaining 可能未耗尽（跨段 skip 未完成——提前终止=游标错位）
                        _position = new LogicalAddress(segId + 1, 0);
                        continue;
                    }

                    long step = Math.Min(remaining, Math.Min(segRemaining, toEnd));
                    remaining -= step;
                    // ★ 游标推进走段表算术：step ≤ segRemaining 夹逼段内；恰好填满停驻
                    //   (segId, MaxOffset) 规范形——由上方跳段分支跨段收敛
                    _position = _owner._segmentTable.AdvanceAddress(_position, step);
                }
                else
                {
                    // ★ 段缺失 = Hollow 段（Reclaim 回收——地址空间占位连续，SegmentGrowthLimit
                    //   占位）：skip 用段表占位跨过它推进到下一实段继续——不能 break（跨 Hollow
                    //   的 skip 会提前终止——cursor 读不到其后实数据，2026-08-27 soak 实锤：
                    //   ReadLogTermAsync 空 → commit 判定失效/applied 卡）；也不能只推位置不减
                    //   remaining（原始死循环——GrowthLimit 占位推进两者都做，天然不循环）
                    var hollowLimit = _owner._segmentTable.SegmentGrowthLimit(segId);
                    var hollowRemaining = hollowLimit - _position.Offset;
                    if (hollowRemaining <= 0)
                    {
                        // 占位段末（含 GrowthLimit 未知段退化为段末）：跨到下段原点
                        _position = new LogicalAddress(segId + 1, 0);
                        continue;
                    }
                    var hollowStep = Math.Min(remaining, hollowRemaining);
                    remaining -= hollowStep;
                    _position = _owner._segmentTable.AdvanceAddress(_position, hollowStep);
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
                    // ★ 跨段回退：TryGetSegment 一步到位——实段=MaxOffset（数据末——布局语义）/
                    //   段不存在=SegmentGrowthLimit（占位末，对称 SkipForward 占位推进）
                    _position = new LogicalAddress(segId,
                        _owner._segmentTable.TryGetSegment(segId, out var seg) && seg is { IsValid: true }
                            ? seg.Value.MaxOffset
                            : _owner._segmentTable.SegmentGrowthLimit(segId));
                }
                else
                {
                    // ★ 游标推进走段表算术：step ≤ segAvailable 夹逼段内，借位语义 Hollow 占位
                    _position = _owner._segmentTable.RetreatAddress(_position, step);
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