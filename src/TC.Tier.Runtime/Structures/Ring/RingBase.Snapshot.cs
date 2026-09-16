using System.Runtime.CompilerServices;

namespace TC.Tier.Runtime.Structures.Ring;

/// <summary>
/// RingBase 快照 partial——流式快照导出（Reader pull）/ 导入（Writer push）。
/// <para>★ 全 LogicalAddress（base.md §2.2）。</para>
/// <para>参见 base.md §2.10。</para>
/// </summary>
public abstract partial class RingBase<TKey>
{
    /// <summary>
    /// 创建快照读取器（上层 pull 数据从 Ring 导出）。
    /// </summary>
    /// <param name="begin">快照的起始逻辑地址。</param>
    /// <param name="end">快照的结束逻辑地址。</param>
    /// <returns>返回一个快照读取器实例。</returns>
    public IRingSnapshotReader OpenSnapshotReader(LogicalAddress begin = default, LogicalAddress end = default)
    {
        EnsureReady();
        var actualBegin = begin == default ? BeginAddress : begin;
        var actualEnd = end == default ? TailAddress : end;
        return _ringSnapshot.Reader(actualBegin, actualEnd);
    }

    /// <summary>
    /// 创建快照写入器（上层 push 数据填回 Ring 页池）。
    /// </summary>
    /// <param name="begin">快照的起始逻辑地址。</param>
    /// <param name="end">快照的结束逻辑地址。</param>
    /// <returns>返回一个快照写入器实例。</returns>
    public IRingSnapshotWriter OpenSnapshotWriter(LogicalAddress begin, LogicalAddress end)
    {
        EnsureReady();
       return _ringSnapshot.Writer(begin, end); // 仅用于触发 _ringSnapshot 的创建（若未创建）
    }

    /// <summary>
    /// Ring 快照实现类——上层导出/导入 Ring 页池数据的统一抽象。
    /// </summary>
    /// <param name="owner">拥有该快照的 Ring 实例。</param>
    /// <typeparam name="TRing">Ring 类型。</typeparam>
    private protected class RingSnapshot<TRing>(TRing owner) : IRingSnapshot
        where TRing : RingBase<TKey>
    {
        /// <summary>创建覆盖 [begin, end) 区间的快照读取器（上层 pull 导出 Ring 页池数据）。</summary>
        /// <param name="begin">导出区间的起始逻辑地址。</param>
        /// <param name="end">导出区间的结束逻辑地址（不含）。</param>
        /// <returns>快照读取器实例。</returns>
        public IRingSnapshotReader Reader(LogicalAddress begin, LogicalAddress end)
        {
            return new RingSnapshotReader(owner, begin, end);
        }

        /// <summary>创建覆盖 [begin, end) 区间的快照写入器（导入数据填回 Ring 页池）。</summary>
        /// <param name="begin">导入区间的起始逻辑地址。</param>
        /// <param name="end">导入区间的结束逻辑地址（超出当前 AllocatedTail 时先整段预留 Allocate）。</param>
        /// <returns>快照写入器实例。</returns>
        public IRingSnapshotWriter Writer(LogicalAddress begin, LogicalAddress end)
        {
            return new RingSnapshotWriter(owner, begin, end);
        }
    }

    private protected sealed class RingSnapshotReader : IRingSnapshotReader
    {
        private readonly RingBase<TKey> _owner;
        private readonly LogicalAddress _begin;
        private readonly LogicalAddress _end;
        private LogicalAddress _currentAddress;
        private readonly AlignedMemoryManager _frame;
        private bool _disposed;

        internal RingSnapshotReader(RingBase<TKey> owner, LogicalAddress begin, LogicalAddress end)
        {
            _owner = owner;
            _begin = begin;
            _end = end;
            _currentAddress = begin;
            _frame = new AlignedMemoryManager(owner.PageSize, (int)owner.SectorSize);
        }

        public long Length => _owner._engine.GetDistance(_begin, _end);

        /// <summary>同步读快照剩余数据到 buffer（热区页池直读 / 冷区设备回源，跨页自动分段拷贝）。</summary>
        /// <param name="buffer">接收缓冲区（长度 0 直接返回 0）。</param>
        /// <returns>实际读入的字节数（0 = 已到快照末尾 _end）。</returns>
        public int Read(Span<byte> buffer)
        {
            ThrowIfDisposed();
            if (_currentAddress >= _end) return 0;

            long toRead = Math.Min(buffer.Length, _owner._engine.GetDistance(_currentAddress, _end));
            LogicalAddress flushedUntil = _owner.FlushedUntilAddress;
            int written = 0;

            while (written < toRead)
            {
                LogicalAddress addr = _owner._engine.CalculationAddress(_currentAddress, written);
                int remaining = (int)(toRead - written);

                if (addr >= flushedUntil)
                    written += ReadHot(addr, buffer.Slice(written), remaining);
                else
                    written += ReadCold(addr, buffer.Slice(written), remaining);
            }

            _currentAddress = _owner._engine.CalculationAddress(_currentAddress, written);
            return written;
        }

        /// <summary>异步读快照剩余数据到 buffer（冷区真异步 IO，跨页自动分段拷贝）。</summary>
        /// <param name="buffer">接收缓冲区（长度 0 直接返回 0）。</param>
        /// <param name="ct">取消令牌，可用于取消异步操作。默认 <c>default</c>。</param>
        /// <returns>完成后结果为实际读入的字节数（0 = 已到快照末尾 _end）。</returns>
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            if (_currentAddress >= _end) return 0;

            long toRead = Math.Min(buffer.Length, _owner._engine.GetDistance(_currentAddress, _end));
            LogicalAddress flushedUntil = _owner.FlushedUntilAddress;
            int written = 0;

            while (written < toRead)
            {
                ct.ThrowIfCancellationRequested();
                LogicalAddress addr = _owner._engine.CalculationAddress(_currentAddress, written);
                int remaining = (int)(toRead - written);

                if (addr >= flushedUntil)
                    written += ReadHot(addr, buffer.Span.Slice(written), remaining);
                else
                    written += await ReadColdAsync(addr, buffer.Slice(written), remaining, ct).ConfigureAwait(false);
            }

            _currentAddress = _owner._engine.CalculationAddress(_currentAddress, written);
            return written;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe int ReadHot(LogicalAddress addr, Span<byte> dest, int maxBytes)
        {
            _owner._epoch.Resume();
            try
            {
                long phys = _owner.GetPhysicalAddress(addr);
                // ★ STORAGE-036：按页边界钳制——拷贝区间可跨页，页与页是独立 native 分配，
                //   单次拷贝不得越出当前页槽（原 min(maxBytes, dest.Length) 会跨页越读）。
                //   外层 while 按返回的 written 推进 addr，下一页由下一次迭代处理。
                int pageRemain = _owner.PageSize - (int)(addr.Offset & _owner.PageSizeMask);
                int copyLen = Math.Min(Math.Min(maxBytes, dest.Length), pageRemain);
                new ReadOnlySpan<byte>((void*)phys, copyLen).CopyTo(dest);
                return copyLen;
            }
            finally
            {
                _owner._epoch.Suspend();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int ReadCold(LogicalAddress addr, Span<byte> dest, int maxBytes)
        {
            long intra = addr.Offset & _owner.PageSizeMask;
            LogicalAddress pageStart = intra == 0 ? addr : _owner._engine.CalculationAddress(addr, -intra);
            int got = _owner.ReadDevicePage(pageStart, _frame.GetSpan(0, _owner.PageSize));
            if (got <= 0) return 0;
            int offsetInPage = (int)(addr.Offset & _owner.PageSizeMask);
            int available = Math.Min(got - offsetInPage, maxBytes);
            available = Math.Min(available, dest.Length);
            _frame.GetSpan(offsetInPage, available).CopyTo(dest);
            return available;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private async ValueTask<int> ReadColdAsync(LogicalAddress addr, Memory<byte> dest, int maxBytes,
            CancellationToken ct)
        {
            long intra = addr.Offset & _owner.PageSizeMask;
            LogicalAddress pageStart = intra == 0 ? addr : _owner._engine.CalculationAddress(addr, -intra);
            int got = await _owner.ReadDevicePageAsync(pageStart, _frame.Memory, ct).ConfigureAwait(false);
            if (got <= 0) return 0;
            int offsetInPage = (int)(addr.Offset & _owner.PageSizeMask);
            int available = Math.Min(got - offsetInPage, maxBytes);
            available = Math.Min(available, dest.Length);
            _frame.GetSpan(offsetInPage, available).CopyTo(dest.Span);
            return available;
        }

        /// <summary>释放读取器（释放页框缓冲；幂等）。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _frame.Dispose();
        }

        /// <summary>异步释放读取器（同步完成——仅释放 native 页框；幂等）。</summary>
        /// <returns>完成后读取器已释放。</returns>
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            _frame.Dispose();
            await ValueTask.CompletedTask.ConfigureAwait(false);
        }

        private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    }
    private protected sealed class RingSnapshotWriter : IRingSnapshotWriter
    {
        private readonly RingBase<TKey> _owner;
        private readonly LogicalAddress _end;
        private LogicalAddress _currentAddress;
        private bool _completed;
        private bool _disposed;

        internal RingSnapshotWriter(RingBase<TKey> owner, LogicalAddress begin, LogicalAddress end)
        {
            _owner = owner;
            _end = end;
            _currentAddress = begin;
            // ★ 导入区间 [begin, end) 可能超出目标 ring 的当前 AllocatedTail（跨实例导入：
            //   源 ring 的地址区间在新 ring 是未分配空间）。预先 Allocate 预留整段，使
            //   GetDistance(_currentAddress, _end) / GetDistance(_dataStart, addr) 合法（地址 ≤ AllocatedTail）。
            //   ★ 不能用 GetDistance 算 need（end 本身就 > AllocatedTail 会抛异常）——
            //     按段几何直接算：segId × SegmentGrowthLimit + 段内 Offset（与 LogicalAddress 布局一致）。
            if (end > owner._engine.AllocatedTail)
            {
                long need = (long)end.SegId * owner._engine.SegmentGrowthLimit + end.Offset;
                if (need > 0) owner._engine.Allocate(need);
            }
        }

        /// <summary>同步写入数据到导入区间（超出 _end 的尾部截断；Complete 后再写抛异常）。</summary>
        /// <param name="buffer">待写入的字节数据。</param>
        public void Write(ReadOnlySpan<byte> buffer)
        {
            ThrowIfDisposed();
            ThrowIfCompleted();
            int written = 0;
            while (written < buffer.Length && _currentAddress < _end)
            {
                int remaining = buffer.Length - written;
                long remainingToEnd = _owner._engine.GetDistance(_currentAddress, _end);
                int toWrite = (int)Math.Min(remaining, remainingToEnd);
                WriteChunk(buffer.Slice(written), toWrite);
                written += toWrite;
            }
        }

        /// <summary>异步写入数据到导入区间（当前实现同步落页池后即完成；Complete 后再写抛异常）。</summary>
        /// <param name="buffer">待写入的字节数据。</param>
        /// <param name="ct">取消令牌（当前实现写前检查）。默认 <c>default</c>。</param>
        /// <returns>完成后 buffer 已写入页池（截断规则同 <see cref="Write"/>）。</returns>
        public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            ThrowIfCompleted();
            Write(buffer.Span);
            await ValueTask.CompletedTask.ConfigureAwait(false);
        }

        /// <summary>完成导入——把 Ring 写尾 _tailAddress 推进到 _end（幂等；Dispose 后再调抛异常）。</summary>
        public void Complete()
        {
            ThrowIfDisposed();
            if (_completed) return;
            _completed = true;
            // ★ #166：导入区水位收口（原实现裸设 tail，ReadOnly/SafeReadOnly 滞留旧位——
            //   导入数据被当 mutable、永不进入 flush/驱逐管线）
            lock (_owner._tailLock)
            {
                if (_end > _owner._tailAddress) _owner._tailAddress = _end;
            }
            _owner.CompleteSnapshotRegion(_end);
        }

        /// <summary>异步完成导入（语义同 <see cref="Complete"/>）。</summary>
        /// <param name="ct">取消令牌（当前实现写前检查）。默认 <c>default</c>。</param>
        /// <returns>完成后写尾已推进到 _end。</returns>
        public async ValueTask CompleteAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Complete();
            await ValueTask.CompletedTask.ConfigureAwait(false);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe void WriteChunk(ReadOnlySpan<byte> src, int len)
        {
            _owner._epoch.Resume();
            try
            {
                LogicalAddress addr = _currentAddress;
                int written = 0;
                while (written < len)
                {
                    long pageSeq = _owner._engine.GetDistance(_owner._dataStart, addr) >> _owner.PageSizeBits;
                    _owner.EnsurePageAllocated(pageSeq);
                    long
                        phys = _owner
                            .GetPhysicalAddress(
                                addr); // ★ phys 已含 pageIntra（RingBase.Addressing.cs:160 = _nativePagePointers[slot] + pageIntra）
                    int pageStart = (int)(addr.Offset & _owner.PageSizeMask); // 页内偏移（仅用于算页内剩余空间）
                    int pageEnd = _owner.PageSize;
                    int chunk = Math.Min(len - written, pageEnd - pageStart);
                    fixed (byte* pSrc = src)
                        // ★ STORAGE-006 (#226)：写指针用 phys（已含 intra），不再 +pageStart（旧代码 2×intra 越界，非页对齐快照导入 SIGSEGV）。
                        Buffer.MemoryCopy(pSrc + written, (void*)phys, chunk, chunk);
                    written += chunk;
                    addr = _owner._engine.CalculationAddress(addr, chunk);
                }

                _currentAddress = _owner._engine.CalculationAddress(_currentAddress, written);
            }
            finally
            {
                _owner._epoch.Suspend();
            }
        }

        /// <summary>释放写入器（仅置 disposed 标记——页池内存归 Ring 所有，无需另行回收；幂等）。</summary>
        public void Dispose() => _disposed = true;

        /// <summary>异步释放写入器（同步完成，语义同 <see cref="Dispose"/>）。</summary>
        /// <returns>完成后写入器已释放。</returns>
        public async ValueTask DisposeAsync()
        {
            _disposed = true;
            await ValueTask.CompletedTask.ConfigureAwait(false);
        }

        private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

        private void ThrowIfCompleted()
        {
            if (_completed) throw new InvalidOperationException("SnapshotWriter 已 Complete，不能再 Write");
        }
    }
}