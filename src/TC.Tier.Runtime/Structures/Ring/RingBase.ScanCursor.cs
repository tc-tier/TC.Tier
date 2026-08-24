using System.Buffers;
using System.Runtime.CompilerServices;
using TC.Tier.Core.Primitives;
using TC.Tier.Core.Primitives;

namespace TC.Tier.Runtime.Structures.Ring;

/// <summary>
/// RingBase 扫描游标 partial——嵌套 RingScanCursor&lt;TRingBase&gt; + 默认 SequentialRingScanCursor。
/// <para>★ 新模型（照 LogBase.Cursor.cs）：全程 LogicalAddress，引擎 OpenSequentialReader 整页读 + record 解帧。</para>
/// <para>★ 冷热统一：热区从内存页池（GetPhysicalAddress），冷区从引擎 reader 读帧。</para>
/// <para>★ epoch 保护：扫描整段持 epoch（防热区页被驱逐回收）。</para>
/// </summary>
public abstract partial class RingBase<TKey>
{
    /// <summary>★ 打开扫描游标（工厂注入或默认 SequentialRingScanCursor）。</summary>
    public IRingScanCursor OpenScanCursor(LogicalAddress begin = default, LogicalAddress end = default)
    {
        EnsureReady();
        return _cursorFactory?.Invoke(begin, end) ?? new SequentialRingScanCursor(this, begin, end);
    }

    /// <summary>
    /// 扫描游标基类（嵌套泛型抽象，持 Owner）。
    /// </summary>
    protected internal abstract class RingScanCursor<TRingBase> : StructureScanCursorBase, IRingScanCursor
        where TRingBase : RingBase<TKey>
    {
        protected readonly TRingBase Owner;
        private readonly AlignedMemoryManager _frame;
        private readonly int _pageSize;
        private readonly int _pageSizeMask;
        private readonly int _pageSizeBits;
        private LogicalAddress _currentAddress;
        private LogicalAddress _nextAddress;
        // ★ 读帧当前装载的冷页起始。_frameLoaded=false 表示未装载（不可用 Empty 作哨兵：
        //   Empty == seg#0@0x0 恰是数据区第一页地址，会让第一页的冷加载被错误跳过，帧内存全零导致扫描返回 0 条）。
        private LogicalAddress _framePageStart = LogicalAddress.Empty;
        private bool _frameLoaded;
        private int _currentRecordSize;

        protected RingScanCursor(TRingBase owner, LogicalAddress beginAddress, LogicalAddress endAddress)
            : base(ReadDirection.Forward)
        {
            Owner = owner;
            _pageSize = owner.PageSize;
            _pageSizeMask = owner.PageSizeMask;
            _pageSizeBits = owner.PageSizeBits;
            LogicalAddress begin = beginAddress < owner.BeginAddress ? owner.BeginAddress : beginAddress;
            BeginAddress = begin;
            EndAddress = endAddress == default ? owner.TailAddress : endAddress;
            _currentAddress = begin;
            _nextAddress = begin;
            _frame = new AlignedMemoryManager(_pageSize, (int)owner.SectorSize);
        }

        public LogicalAddress CurrentAddress => _currentAddress;
        public LogicalAddress NextAddress => _nextAddress;
        public LogicalAddress BeginAddress { get; }
        public LogicalAddress EndAddress { get; }
        public int CurrentRecordSize => _currentRecordSize;

        public RingRecordFields GetFields()
        {
            int headerSize = Owner.RingCodec.HeaderSize;
            long phys = GetRecordPhys(_currentAddress, Owner.FlushedUntilAddress);
            unsafe
            {
                var span = new ReadOnlySpan<byte>((void*)phys, headerSize);
                Owner.RingCodec.TryReadHeader(span, out var fields);
                return fields;
            }
        }

        public override bool MoveNext()
        {
            Owner._epoch.Resume();
            try
            {
                return MoveNextCore();
            }
            finally
            {
                Owner._epoch.Suspend();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private unsafe bool MoveNextCore()
        {
            while (true)
            {
                if (_nextAddress >= EndAddress) return false;

                LogicalAddress flushedUntil = Owner.FlushedUntilAddress;
                long nextIntra = _nextAddress.Offset & _pageSizeMask;
                LogicalAddress currentPage = nextIntra == 0
                    ? _nextAddress
                    : Owner._engine.CalculationAddress(_nextAddress, -nextIntra);

                if (_nextAddress < flushedUntil && (!_frameLoaded || _framePageStart != currentPage))
                {
                    if (!LoadColdPage(_nextAddress)) return false;
                }

                long phys = GetRecordPhys(_nextAddress, flushedUntil);
                int offsetInPage = (int)(_nextAddress.Offset & _pageSizeMask);
                int headerSize = Owner.RingCodec.HeaderSize;

                if (offsetInPage + headerSize > _pageSize)
                {
                    // 跳到下一页（CalculationAddress 推进，禁止位运算）
                    LogicalAddress next = Owner._engine.CalculationAddress(currentPage, _pageSize);
                    if (next <= _nextAddress) return false;
                    _nextAddress = next;
                    continue;
                }

                var headerSpan = new ReadOnlySpan<byte>((void*)phys, headerSize);
                if (!Owner.RingCodec.TryReadHeader(headerSpan, out var fields))
                {
                    _nextAddress = Owner._engine.CalculationAddress(_nextAddress, Owner.RingCodec.Alignment);
                    continue;
                }

                int filled = headerSize + (int)fields.PayloadLength + fields.PaddingLength;
                int aligned = (filled + Owner.RingCodec.Alignment - 1) & ~(Owner.RingCodec.Alignment - 1);
                if (aligned <= 0) aligned = Owner.RingCodec.Alignment;

                if ((fields.Flags & RecordFlags.FLAG_ENTRY_IS_META) != 0)
                {
                    _nextAddress = Owner._engine.CalculationAddress(_nextAddress, aligned);
                    continue;
                }

                _currentAddress = _nextAddress;
                _currentRecordSize = aligned;
                _nextAddress = Owner._engine.CalculationAddress(_currentAddress, aligned);
                return true;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe long GetRecordPhys(LogicalAddress addr, LogicalAddress flushedUntil)
        {
            if (addr >= flushedUntil)
                return Owner.GetPhysicalAddress(addr);   // 热区
            int offset = (int)(addr.Offset & _pageSizeMask);
            return (long)(_frame.BytePtr + offset);   // 冷区：读帧
        }

        private bool LoadColdPage(LogicalAddress address)
        {
            long intra = address.Offset & _pageSizeMask;
            LogicalAddress pageStart = intra == 0 ? address : Owner._engine.CalculationAddress(address, -intra);
            int got = Owner.ReadDevicePage(pageStart, _frame.GetSpan(0, _pageSize));
            if (got <= 0) return false;
            _framePageStart = pageStart;
            _frameLoaded = true;
            return true;
        }

        public override ValueTask<bool> MoveNextAsync(CancellationToken cancellationToken = default)
        {
            if (_nextAddress >= Owner.FlushedUntilAddress)
                return new ValueTask<bool>(MoveNext());
            return MoveNextSlowAsync(cancellationToken);
        }

        private async ValueTask<bool> MoveNextSlowAsync(CancellationToken ct)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                LogicalAddress flushedUntil = Owner.FlushedUntilAddress;

                if (_nextAddress < flushedUntil)
                {
                    long intra = _nextAddress.Offset & _pageSizeMask;
                    LogicalAddress currentPage = intra == 0 ? _nextAddress : Owner._engine.CalculationAddress(_nextAddress, -intra);
                    if (!_frameLoaded || _framePageStart != currentPage)
                    {
                        if (!await LoadColdPageAsync(_nextAddress, ct).ConfigureAwait(false))
                            return false;
                    }
                }

                bool moved = MoveNext();
                if (moved) return true;
                if (_nextAddress >= EndAddress) return false;
                if (_nextAddress >= Owner.FlushedUntilAddress) return false;
            }
        }

        private async ValueTask<bool> LoadColdPageAsync(LogicalAddress address, CancellationToken ct)
        {
            long intra = address.Offset & _pageSizeMask;
            LogicalAddress pageStart = intra == 0 ? address : Owner._engine.CalculationAddress(address, -intra);
            int got = await Owner.ReadDevicePageAsync(pageStart, _frame.Memory, ct).ConfigureAwait(false);
            if (got <= 0) return false;
            _framePageStart = pageStart;
            _frameLoaded = true;
            return true;
        }

        public override void Dispose() => _frame.Dispose();
        public override ValueTask DisposeAsync() { _frame.Dispose(); return default; }
    }

    internal sealed class SequentialRingScanCursor : RingScanCursor<RingBase<TKey>>
    {
        internal SequentialRingScanCursor(RingBase<TKey> owner, LogicalAddress begin, LogicalAddress end) : base(owner, begin, end) { }
    }
}
