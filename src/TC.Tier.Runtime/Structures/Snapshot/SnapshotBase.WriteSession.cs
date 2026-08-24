using System.Runtime.CompilerServices;

namespace TC.Tier.Runtime.Structures.Snapshot;

/// <summary>WriteSession partial——双 buffer flush 流水线写会话（移植自旧 BlobBase.WriteSession 已验证实现）。</summary>
public abstract partial class SnapshotBase
{
    /// <summary>
    /// 双 buffer 写入会话——扇区对齐 pinned 缓冲 + 流水线 flush。
    /// <para>双 buffer 流水线：buffer A 满时启动异步 flush(A)，立即切到 buffer B 继续填充；
    /// B 满时 await flush(A)（此时通常已完成）再启动 flush(B)，切回 A。
    /// CPU 序列化与磁盘 I/O 并行，消除单 buffer 的串行停顿。</para>
    /// </summary>
    private sealed class WriteSession : ISnapshotWriteSession
    {
        private readonly SnapshotBase _owner;
        private readonly AlignedMemoryManager _bufferA;
        private readonly AlignedMemoryManager _bufferB;
        private readonly int _fullBufferSize;
        private readonly int _sectorSize;
        // 活跃写状态
        private AlignedMemoryManager _active;
        private int _written;

        // flush 流水线状态
        private LogicalAddress _flushedAddress;
        private ValueTask _pendingFlush;
        private LogicalAddress _pendingFlushAddress;
        private int _pendingFlushLogical;
        private int _pendingFlushAligned;
        private bool _hasPendingFlush;

        private bool _disposed;

        /// <summary>每次 flush 完成触发：(flushedAddress, logicalBytes, alignedBytes)。</summary>
        public event Action<LogicalAddress, int, int>? OnFlushed;

        /// <summary>当前 buffer 剩余可用字节。</summary>
        public int FreeBytes => _fullBufferSize - _written;

        public WriteSession(SnapshotBase owner, LogicalAddress startAddress)
        {
            _owner = owner;
            _sectorSize = owner._sectorSize;
            _fullBufferSize = SectorAlignment.AlignDown(owner._sessionBufferSize, _sectorSize);

            int align = (int)Math.Max(owner._sectorSize, 4096);
            _bufferA = new AlignedMemoryManager(_fullBufferSize, align);
            _bufferB = new AlignedMemoryManager(_fullBufferSize, align);

            _active = _bufferA;
            _written = 0;
            _flushedAddress = startAddress;
            _pendingFlush = default;
            _hasPendingFlush = false;
        }

        /// <summary>
        /// 高性能异步写入：自动管理 buffer swap + pipeline flush。
        /// ★ 快速路径：数据能放进当前 buffer 时零分配同步返回，避免 async 状态机开销。
        /// </summary>
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            if (data.IsEmpty) return ValueTask.CompletedTask;

            int free = _fullBufferSize - _written;
            if (data.Length <= free)
            {
                CopyToBuffer(data.Span);
                _written += data.Length;
                return ValueTask.CompletedTask;
            }

            return WriteAsyncSlow(data, ct);
        }

        private async ValueTask WriteAsyncSlow(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            int offset = 0;
            while (offset < data.Length)
            {
                int free = _fullBufferSize - _written;
                if (free == 0)
                {
                    await SwapAndFlushAsync(ct).ConfigureAwait(false);
                    free = _fullBufferSize;
                }

                int chunk = Math.Min(data.Length - offset, free);
                CopyToBuffer(data.Span.Slice(offset, chunk));
                _written += chunk;
                offset += chunk;
            }
        }

        /// <summary>同步写入：buffer 满时同步 swap+flush。单线程契约。</summary>
        public void Write(ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty) return;
            int offset = 0;
            while (offset < data.Length)
            {
                int free = _fullBufferSize - _written;
                if (free == 0)
                {
                    SwapAndFlush();
                    free = _fullBufferSize;
                }
                int chunk = Math.Min(data.Length - offset, free);
                CopyToBuffer(data.Slice(offset, chunk));
                _written += chunk;
                offset += chunk;
            }
        }

        /// <summary>微写入同步 API（header/footer 等固定小数据）。不触发 flush；先 FlushIfFull 确保空间。</summary>
        public void WriteSmall(ReadOnlySpan<byte> data)
        {
            if (data.Length > _fullBufferSize - _written)
                throw new InvalidOperationException(
                    $"WriteSmall overflow: requested {data.Length}, free {_fullBufferSize - _written}. Flush first.");

            CopyToBuffer(data);
            _written += data.Length;
        }

        /// <summary>空间不足 needed 时 swap + flush（异步）。</summary>
        public async ValueTask FlushIfFullAsync(int needed, CancellationToken ct = default)
        {
            if (_fullBufferSize - _written < needed)
                await SwapAndFlushAsync(ct).ConfigureAwait(false);
        }

        /// <summary>空间不足 needed 时同步 swap + flush。</summary>
        public void FlushIfFull(int needed)
        {
            if (_fullBufferSize - _written < needed)
                SwapAndFlush();
        }

        /// <summary>最终 flush：await pipeline 上一次 + flush 剩余。幂等。</summary>
        public async ValueTask FlushAsync(CancellationToken ct = default)
        {
            await AwaitPendingFlushAsync().ConfigureAwait(false);
            if (_written > 0)
            {
                int aligned = _written.AlignUp(_sectorSize);
                ClearPadding(aligned);
                await _owner.WriteAtAsync(
                    _flushedAddress, _active.Memory[..aligned], ct).ConfigureAwait(false);
                OnFlushed?.Invoke(_flushedAddress, _written, aligned);
                _flushedAddress = _owner._engine.CalculationAddress(_flushedAddress, aligned);
                _written = 0;
            }
        }

        /// <summary>同步最终 flush。幂等。</summary>
        public void Flush()
        {
            AwaitPendingFlush();
            if (_written > 0)
            {
                int aligned = _written.AlignUp(_sectorSize);
                ClearPadding(aligned);
                _owner.WriteAt(_flushedAddress, _active.GetSpan(0, aligned));
                OnFlushed?.Invoke(_flushedAddress, _written, aligned);
                _flushedAddress = _owner._engine.CalculationAddress(_flushedAddress, aligned);
                _written = 0;
            }
        }

        // ══ 双 buffer swap + pipeline flush 核心（异步 + 同步对等）══

        private async ValueTask SwapAndFlushAsync(CancellationToken ct)
        {
            await AwaitPendingFlushAsync().ConfigureAwait(false);

            int aligned = SectorAlignment.AlignUp(_written, _sectorSize);
            ClearPadding(aligned);
#pragma warning disable CA2012
            _pendingFlush = _owner.WriteAtAsync(
                _flushedAddress, _active.Memory.Slice(0, aligned), ct);
#pragma warning restore CA2012
            _pendingFlushAddress = _flushedAddress;
            _pendingFlushLogical = _written;
            _pendingFlushAligned = aligned;
            _hasPendingFlush = true;
            _flushedAddress = _owner._engine.CalculationAddress(_flushedAddress, aligned);

            _active = (_active == _bufferA) ? _bufferB : _bufferA;
            _written = 0;
        }

        private void SwapAndFlush()
        {
            AwaitPendingFlush();
            int aligned = SectorAlignment.AlignUp(_written, _sectorSize);
            ClearPadding(aligned);
            _owner.WriteAt(_flushedAddress, _active.GetSpan(0, aligned));
            OnFlushed?.Invoke(_flushedAddress, _written, aligned);
            _flushedAddress = _owner._engine.CalculationAddress(_flushedAddress, aligned);
            _active = (_active == _bufferA) ? _bufferB : _bufferA;
            _written = 0;
        }

        private async ValueTask AwaitPendingFlushAsync()
        {
            if (!_hasPendingFlush) return;
            await _pendingFlush.ConfigureAwait(false);
            OnFlushed?.Invoke(_pendingFlushAddress, _pendingFlushLogical, _pendingFlushAligned);
            _hasPendingFlush = false;
        }

        private void AwaitPendingFlush()
        {
            if (!_hasPendingFlush) return;
#pragma warning disable TCSG031 // 设计必需：同步写路径等 pending flush 完成
            _pendingFlush.GetAwaiter().GetResult();
#pragma warning restore TCSG031
            OnFlushed?.Invoke(_pendingFlushAddress, _pendingFlushLogical, _pendingFlushAligned);
            _hasPendingFlush = false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ClearPadding(int aligned)
        {
            if (aligned > _written)
                _active.GetSpan(_written, aligned - _written).Clear();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void CopyToBuffer(ReadOnlySpan<byte> src)
            => src.CopyTo(_active.GetSpan(_written, src.Length));

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            await FlushAsync().ConfigureAwait(false);
            _bufferA.Dispose();
            _bufferB.Dispose();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Flush();
            _bufferA.Dispose();
            _bufferB.Dispose();
        }
    }
}
