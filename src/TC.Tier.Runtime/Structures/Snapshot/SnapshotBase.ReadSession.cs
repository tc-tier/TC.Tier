using System.Runtime.CompilerServices;

namespace TC.Tier.Runtime.Structures.Snapshot;

/// <summary>ReadSession partial——双 buffer 异步预读读会话（移植自旧 BlobBase.ReadSession 已验证实现）。</summary>
public abstract partial class SnapshotBase
{
    /// <summary>
    /// 双 buffer 读取会话——扇区对齐 pinned 缓冲 + 异步预读流水线。
    /// <para>双 buffer 预读：填充 buffer A 时立即向 buffer B 发起异步预读；A 耗尽后切到 B（通常已完成），
    /// 同时向 A 发起下一次预读。消费与下一次 I/O 并行。</para>
    /// <para>物理/逻辑偏移分离：底层 DIO 要求 offset/length 扇区对齐，上层帧解析需要紧凑逻辑字节流——
    /// 对外暴露逻辑视图 [logicalStart, logicalEnd)，内部对齐预读并剔除 padding。</para>
    /// </summary>
    private sealed class ReadSession : ISnapshotReadSession
    {
        private readonly SnapshotBase _owner;
        private readonly LogicalAddress _logicalStart;
        private readonly LogicalAddress _logicalEnd;
        private readonly LogicalAddress _physicalStart;   // ★ 物理锚点（逻辑↔物理换算的基准，正向推算）
        private readonly LogicalAddress _physicalEnd;
        private readonly int _sectorSize;
        private LogicalAddress _physicalReadOffset;
        private long _logicalConsumed;

        private readonly AlignedMemoryManager _bufferA;
        private readonly AlignedMemoryManager _bufferB;
        private readonly int _fullBufferSize;

        // 活跃交付 buffer
        private AlignedMemoryManager _active;

        // 预读流水线
        private AlignedMemoryManager _prefetch;
        private ValueTask<int> _prefetchTask;
        private LogicalAddress _prefetchPhysStart;
        private bool _hasPrefetch;

        // 当前 buffer 交付状态
        private LogicalAddress _bufferPhysStart;
        private int _bufferPhysLen;
        private int _bufferConsumed;

        private bool _disposed;

        public ReadSession(SnapshotBase owner, LogicalAddress logicalStart, LogicalAddress logicalEnd,
            LogicalAddress physicalStart, LogicalAddress physicalEnd)
        {
            _owner = owner;
            _logicalStart = logicalStart;
            _logicalEnd = logicalEnd;
            _physicalStart = physicalStart;
            _sectorSize = owner._sectorSize;
            _physicalReadOffset = physicalStart;
            _physicalEnd = physicalEnd;
            _logicalConsumed = 0;

            _fullBufferSize = SectorAlignment.AlignDown(owner._sessionBufferSize, _sectorSize);

            int align = (int)Math.Max(owner._sectorSize, 4096);
            _bufferA = new AlignedMemoryManager(_fullBufferSize, align);
            _bufferB = new AlignedMemoryManager(_fullBufferSize, align);

            _active = _bufferA;
            _prefetch = _bufferB;
            _hasPrefetch = false;
            _bufferPhysStart = LogicalAddress.Empty;
            _bufferPhysLen = 0;
            _bufferConsumed = 0;
        }

        LogicalAddress ISnapshotReadSession.LogicalStart => _logicalStart;
        LogicalAddress ISnapshotReadSession.LogicalEnd => _logicalEnd;
        LogicalAddress ISnapshotReadSession.PhysicalEnd => _physicalEnd;

        /// <summary>
        /// 唯一读 API：填充 caller buffer，零分配。
        /// 内部按物理对齐预读 + 双 buffer 流水线，剔除 padding，只交付 [logicalStart, logicalEnd) 内的逻辑字节。
        /// </summary>
        /// <returns>实际填充字节数；0 表示到达逻辑 EOF。</returns>
        public async ValueTask<int> ReadAsync(Memory<byte> dest, CancellationToken ct = default)
        {
            if (dest.IsEmpty) return 0;

            LogicalAddress logicalCursor = _owner._engine.CalculationAddress(_logicalStart, _logicalConsumed);
            long logicalRemaining = _owner._engine.GetDistance(logicalCursor, _logicalEnd);
            if (logicalRemaining <= 0) return 0;

            int filled = 0;
            while (filled < dest.Length && logicalRemaining > 0)
            {
                // ── 从活跃 buffer 交付 ──
                if (_bufferPhysLen > 0)
                {
                    LogicalAddress bufferPhysEnd = _owner._engine.CalculationAddress(_bufferPhysStart, _bufferPhysLen);
                    LogicalAddress physLo = _owner._engine.CalculationAddress(_bufferPhysStart, _bufferConsumed);
                    // ★ 逻辑↔物理换算从锚点（_logicalStart/_physicalStart）正向推算——会话锚点基准，语义最直
                    LogicalAddress logLo = _owner._engine.CalculationAddress(
                        _logicalStart, _owner._engine.GetDistance(_physicalStart, physLo));
                    LogicalAddress logHi = _owner._engine.CalculationAddress(
                        _logicalStart, _owner._engine.GetDistance(_physicalStart, bufferPhysEnd));
                    LogicalAddress deliverLo = logLo >= logicalCursor ? logLo : logicalCursor;
                    LogicalAddress deliverHi = logHi <= _logicalEnd ? logHi : _logicalEnd;
                    if (deliverHi > deliverLo)
                    {
                        int toCopy = (int)Math.Min(_owner._engine.GetDistance(deliverLo, deliverHi), dest.Length - filled);
                        LogicalAddress physCopyLo = _owner._engine.CalculationAddress(
                            _physicalStart, _owner._engine.GetDistance(_logicalStart, deliverLo));
                        int bufOff = (int)_owner._engine.GetDistance(_bufferPhysStart, physCopyLo);
                        _active.GetSpan(bufOff, toCopy)
                            .CopyTo(dest.Span.Slice(filled, toCopy));
                        filled += toCopy;
                        _logicalConsumed += toCopy;
                        logicalRemaining -= toCopy;
                        logicalCursor = _owner._engine.CalculationAddress(logicalCursor, toCopy);
                        LogicalAddress physCopyHi = _owner._engine.CalculationAddress(physCopyLo, toCopy);
                        _bufferConsumed = (int)_owner._engine.GetDistance(_bufferPhysStart, physCopyHi);
                        continue;
                    }

                    _bufferPhysLen = 0;
                    _bufferConsumed = 0;
                }

                // ── 活跃 buffer 耗尽，取下一块 ──
                if (_hasPrefetch)
                {
                    int got = await _prefetchTask.ConfigureAwait(false);
                    _hasPrefetch = false;
                    if (got == 0) break;

                    _bufferPhysStart = _prefetchPhysStart;
                    _bufferPhysLen = got;
                    _bufferConsumed = 0;
                    _physicalReadOffset = _owner._engine.CalculationAddress(_prefetchPhysStart, got);

                    SwapBuffers();
                }
                else
                {
                    // 无预读（首次读取）——读入活跃 buffer
                    if (_physicalReadOffset >= _physicalEnd) break;

                    int physToRead = (int)Math.Min(_owner._engine.GetDistance(_physicalReadOffset, _physicalEnd), _fullBufferSize);
                    int physAligned = SectorAlignment.AlignUp(physToRead, _sectorSize);
                    if (physAligned > _fullBufferSize) physAligned = _fullBufferSize;

                    int got = await _owner.ReadAtAsync(
                        _physicalReadOffset, _active.Memory.Slice(0, physAligned), ct).ConfigureAwait(false);
                    if (got == 0) break;

                    _bufferPhysStart = _physicalReadOffset;
                    _bufferPhysLen = got;
                    _bufferConsumed = 0;
                    _physicalReadOffset = _owner._engine.CalculationAddress(_physicalReadOffset, got);
                }

                StartPrefetch(ct);
            }

            return filled;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SwapBuffers()
        {
            (_active, _prefetch) = (_prefetch, _active);
        }

        private void StartPrefetch(CancellationToken ct)
        {
            if (_physicalReadOffset >= _physicalEnd)
            {
                _hasPrefetch = false;
                return;
            }

            int physToRead = (int)Math.Min(_owner._engine.GetDistance(_physicalReadOffset, _physicalEnd), _fullBufferSize);
            int physAligned = SectorAlignment.AlignUp(physToRead, _sectorSize);
            if (physAligned > _fullBufferSize) physAligned = _fullBufferSize;

            _prefetchPhysStart = _physicalReadOffset;
            // CA2012：pipeline 模式——存入字段在下次 buffer 耗尽时 await（单次消费），合法抑制。
#pragma warning disable CA2012
            _prefetchTask = _owner.ReadAtAsync(
                _physicalReadOffset, _prefetch.Memory.Slice(0, physAligned), ct);
#pragma warning restore CA2012
            _hasPrefetch = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            // 消费挂起的预读避免孤儿 ValueTask
            if (_hasPrefetch)
            {
                try
                {
                    await _prefetchTask.ConfigureAwait(false);
                }
                catch
                {
                    /* disposing */
                }

                _hasPrefetch = false;
            }

            _bufferA.Dispose();
            _bufferB.Dispose();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _bufferA.Dispose();
            _bufferB.Dispose();
        }
    }
}
