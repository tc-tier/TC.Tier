using System.Buffers;
using TC.Tier.Runtime.Structures.Log.Contracts;

namespace TC.Tier.Runtime.Structures.Log;

/// <summary>
/// LogBase 扫描游标 partial——引擎 OpenSequentialReader + PageFrame 整页校验。
/// <para>★ PageFrame 让扫描器整页 CRC 跳逐条 entry 校验（~10-50× 加速）。</para>
/// <para>★ 同步/异步分轨——两种都支持真实 I/O。</para>
/// <para>★ 零地址算术违规：所有地址推进经 engine.CalculationAddress，比较用 LogicalAddress 运算符，
///   页内偏移是纯内存缓冲索引（int），不参与地址运算。</para>
/// </summary>
public abstract partial class LogBase
{
    /// <summary>打开扫描游标——引擎 OpenSequentialReader + PageFrame 校验。</summary>
    public ILogCursor OpenCursor(LogicalAddress startAddress = default, LogicalAddress endAddress = default, bool verifyCrc = false)
        => _cursorFactory?.Invoke(startAddress, endAddress, verifyCrc) ?? new PageFrameCursor(this, startAddress, endAddress, verifyCrc);

    private sealed class PageFrameCursor : ILogCursor
    {
        private readonly LogBase _owner;
        private readonly bool _verifyCrc;
        private readonly int _headerSize;

        private readonly ISequentialReader _reader;

        // 当前页
        private byte[]? _pageBuf;
        private LogicalAddress _pageStartAddr;   // data 区起点（frameStart + headerSize）
        private LogicalAddress _pageEndAddr;     // data 区尾（CalculationAddress 计算）
        private int _pageDataLen;                // 页内有效字节数（hdr.DataLength）

        // 扫描游标（总指向下一个待读 entry 起点）
        private LogicalAddress _currentAddress;
        private int _currentOffsetInPage;
        // 本次 MoveNext 返回 entry 的起点（供 CurrentAddress/CurrentPayload，避免被扫描游标推进覆盖）
        private LogicalAddress _currentEntryAddr;
        private int _currentEntryOff;
        private int _currentEntryLength;
        private int _currentIsMeta_int;
        private bool _disposed;
        // ★ 起始地址非页边界时，cursor 从段首读 frame、跳过 < _skipUntil 的 entry（断点续传重放用）。
        //   entry 地址落在 frame data 中间，不能直接当 reader 起点（会读到 entry 字节当 frame header）。
        private LogicalAddress _skipUntil;

        public PageFrameCursor(LogBase owner, LogicalAddress startAddress, LogicalAddress endAddress, bool verifyCrc)
        {
            _owner = owner;
            _verifyCrc = verifyCrc;
            _headerSize = owner.LogCodec.HeaderSize;

            // ★ 默认扫描终点 = 已落盘水位 FlushedTail（TailAddress 含内存页未 flush entry，不可读）
            var actualEnd = endAddress == default ? owner.FlushedTail : endAddress;
            // ★ startAddress 落在 frame data 中间（entry 地址）——reader 不能从这里起（会读 entry 字节当 frame header）。
            //   改为从 startAddress 所在段首起读 frame，cursor 内跳过 < startAddress 的 entry（断点续传重放）。
            LogicalAddress readerStart = startAddress;
            if (startAddress != default)
            {
                _skipUntil = startAddress;
                readerStart = new LogicalAddress(startAddress.SegId, 0);
            }
            _reader = owner._engine.OpenSequentialReader(readerStart, actualEnd,
                ReadDirection.Forward, usePageCache: true, SnapshotMode.Consistent);
        }

        public ReadDirection Direction => ReadDirection.Forward;
        public LogicalAddress CurrentAddress => _currentEntryAddr;
        public LogicalAddress EndAddress => _reader.End;
        public long CurrentPayloadStart => _owner._engine.CalculationAddress(_currentEntryAddr, _headerSize).Offset;
        public int CurrentEntryLength => _currentEntryLength;
        public bool CurrentIsMeta => _currentIsMeta_int != 0;

        public ReadOnlySpan<byte> CurrentPayload
        {
            get
            {
                int payloadOff = _currentEntryOff + _headerSize;
                return _pageBuf.AsSpan(payloadOff, _currentEntryLength);
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // 同步轨
        // ═══════════════════════════════════════════════════════════════

        public bool MoveNext()
        {
            while (true)
            {
                // ★ 先消费当前页缓冲内的剩余 entry——reader.Position 可能在上一页 LoadNextPage 后已达 End
                //   （整页 frame 一次读完），但页内仍有未解析 entry；故 EOF 判定推迟到"需要加载新页"时。
                if (_pageBuf is not null && _currentAddress < _pageEndAddr)
                {
                    int remaining = _pageDataLen - _currentOffsetInPage;
                    if (remaining >= _headerSize)
                    {
                        var headerSpan = _pageBuf.AsSpan(_currentOffsetInPage, remaining);
                        if (_owner.LogCodec.TryReadHeader(headerSpan, out int entryLength, out int paddingLength,
                                out bool isMeta, _verifyCrc))
                        {
                            _currentEntryLength = entryLength;
                            _currentIsMeta_int = isMeta ? 1 : 0;
                            // ★ step 必须含 padding——下一个 entry 起点在 padding 之后（对齐到 codec.Alignment），
                            //   漏掉 padding 会让扫描游标落在 padding 中间，TryReadHeader 读到错位 magic 而误判页结束。
                            int step = _headerSize + entryLength + paddingLength;
                            // ★ 锁定本次返回 entry 的起点（供 CurrentAddress/CurrentPayload），再推进扫描游标
                            _currentEntryAddr = _currentAddress;
                            _currentEntryOff = _currentOffsetInPage;
                            _currentAddress = _owner._engine.CalculationAddress(_currentAddress, step);
                            _currentOffsetInPage += step;
                            // ★ 断点续传：跳过 < _skipUntil 的 entry（startAddress 落在 frame data 中间）
                            if (_currentEntryAddr < _skipUntil) continue;
                            return true;
                        }
                    }
                    // 当前页无更多有效 entry——换页
                    _currentAddress = _pageEndAddr;
                    _pageBuf = null;
                }

                // 需要加载新页：先看 reader 是否还有数据
                if (_reader.Position >= _reader.End) return false;
                if (!LoadNextPage()) return false;
            }
        }

        private bool LoadNextPage()
        {
            LogicalAddress frameStart = _reader.Position;

            Span<byte> frameHeader = stackalloc byte[LogPageFrameHeaderCodec.StructSize];
            if (_reader.Read(frameHeader) < LogPageFrameHeaderCodec.StructSize) return false;

            var hdr = LogPageFrameHeaderCodec.Read(frameHeader);
            if (hdr.MagicValue != RecordMagic.LogPageFrame) return false;
            int dataLen = hdr.DataLength;
            if (dataLen <= 0 || dataLen > _owner.PageSize) return false;

            EnsurePageBuf(dataLen);
            if (_reader.Read(_pageBuf.AsSpan(0, dataLen)) < dataLen) return false;

            Span<byte> crcBuf = stackalloc byte[Crc32FooterCodec.StructSize];
            if (_reader.Read(crcBuf) < Crc32FooterCodec.StructSize) return false;

            // ★ 跳过 frame 尾部 padding（写侧补零撑到扇区对齐），否则下一帧 header 落在 padding 上。
            int padding = _owner.ComputeFramePadding(dataLen);
            if (padding > 0) _reader.Skip(padding);

            if (_verifyCrc)
            {
                var footer = Crc32FooterCodec.Read(crcBuf);
                // ★ cover 可达 PageSize+8（默认 4MB+）——绝不能 stackalloc（栈溢出），用 ArrayPool。
                int coverLen = LogPageFrameHeaderCodec.StructSize + dataLen;
                byte[] coverBuf = ArrayPool<byte>.Shared.Rent(coverLen);
                try
                {
                    Span<byte> cover = coverBuf.AsSpan(0, coverLen);
                    frameHeader.CopyTo(cover);
                    _pageBuf.AsSpan(0, dataLen).CopyTo(cover.Slice(LogPageFrameHeaderCodec.StructSize));
                    if (UnifiedCrc.ComputeCrc32C(cover) != footer.Crc) return false;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(coverBuf);
                }
            }

            // ★ _pageStartAddr 指 data 区起点（frameStart + headerSize）——与写入侧 entry 地址
            //   = windowStart + headerSize + 页内偏移 严格对齐。cursor 推进的 entry 地址即 entry 真实 LogicalAddress。
            _pageStartAddr = _owner._engine.CalculationAddress(frameStart, LogPageFrameHeaderCodec.StructSize);
            _pageEndAddr = _owner._engine.CalculationAddress(_pageStartAddr, dataLen);
            _pageDataLen = dataLen;
            _currentAddress = _pageStartAddr;
            _currentOffsetInPage = 0;
            return true;
        }

        // ═══════════════════════════════════════════════════════════════
        // 异步轨：真异步 I/O + Span 抽同步 helper
        // ═══════════════════════════════════════════════════════════════

        public async ValueTask<bool> MoveNextAsync(CancellationToken ct = default)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                // ★ 先消费当前页缓冲内的剩余 entry（同同步轨：EOF 判定推迟到需加载新页时）
                if (_pageBuf is not null && _currentAddress < _pageEndAddr)
                {
                    int remaining = _pageDataLen - _currentOffsetInPage;
                    if (remaining >= _headerSize && TryAdvanceEntry(_currentOffsetInPage, remaining)
                        && _currentEntryAddr >= _skipUntil)   // ★ 断点续传：跳过 < _skipUntil 的 entry
                        return true;
                    // entry 被 skip（< _skipUntil）或 TryAdvance 失败——若页内还有 entry 继续，否则换页
                    if (_pageBuf is null || _currentAddress >= _pageEndAddr)
                    {
                        _currentAddress = _pageEndAddr;
                        _pageBuf = null;
                    }
                }

                // 需要加载新页：先看 reader 是否还有数据
                if (_reader.Position >= _reader.End) return false;
                if (!await LoadNextPageAsync(ct).ConfigureAwait(false)) return false;
            }
        }

        private bool TryAdvanceEntry(int offInPage, int remaining)
        {
            var headerSpan = _pageBuf.AsSpan(offInPage, remaining);
            if (!_owner.LogCodec.TryReadHeader(headerSpan, out int entryLength, out int paddingLength,
                    out bool isMeta, _verifyCrc))
                return false;

            _currentEntryLength = entryLength;
            _currentIsMeta_int = isMeta ? 1 : 0;
            // ★ step 必须含 padding（同步轨同因）
            int step = _headerSize + entryLength + paddingLength;
            // ★ 锁定本次返回 entry 的起点（供 CurrentAddress/CurrentPayload），再推进扫描游标
            _currentEntryAddr = _currentAddress;
            _currentEntryOff = offInPage;
            _currentAddress = _owner._engine.CalculationAddress(_currentAddress, step);
            _currentOffsetInPage += step;
            return true;
        }

        private async ValueTask<bool> LoadNextPageAsync(CancellationToken ct)
        {
            LogicalAddress frameStart = _reader.Position;

            using var headerOwner = RentTemp(LogPageFrameHeaderCodec.StructSize);
            var headerMem = headerOwner.Memory;
            if (await _reader.ReadAsync(headerMem, ct).ConfigureAwait(false) < LogPageFrameHeaderCodec.StructSize)
                return false;

            var hdr = LogPageFrameHeaderCodec.Read(headerMem.Span);
            if (hdr.MagicValue != RecordMagic.LogPageFrame) return false;
            int dataLen = hdr.DataLength;
            if (dataLen <= 0 || dataLen > _owner.PageSize) return false;

            EnsurePageBuf(dataLen);
            if (await _reader.ReadAsync(_pageBuf.AsMemory(0, dataLen), ct).ConfigureAwait(false) < dataLen)
                return false;

            using var crcOwner = RentTemp(Crc32FooterCodec.StructSize);
            var crcMem = crcOwner.Memory;
            if (await _reader.ReadAsync(crcMem, ct).ConfigureAwait(false) < Crc32FooterCodec.StructSize)
                return false;

            // ★ 跳过 frame 尾部 padding（与同步轨 LoadNextPage 对等）
            int padding = _owner.ComputeFramePadding(dataLen);
            if (padding > 0) _reader.Skip(padding);

            if (_verifyCrc)
            {
                var footer = Crc32FooterCodec.Read(crcMem.Span);
                // ★ 统一使用原始磁盘字节计算 CRC（对齐同步路径），避免 codec 规范化导致 CRC 不一致
                if (!VerifyPageCrc(dataLen, headerMem.Span, _pageBuf.AsSpan(0, dataLen), footer)) return false;
            }

            _pageStartAddr = _owner._engine.CalculationAddress(frameStart, LogPageFrameHeaderCodec.StructSize);
            _pageEndAddr = _owner._engine.CalculationAddress(_pageStartAddr, dataLen);
            _pageDataLen = dataLen;
            _currentAddress = _pageStartAddr;
            _currentOffsetInPage = 0;
            return true;
        }

        private static bool VerifyPageCrc(int dataLen, ReadOnlySpan<byte> rawHeader, ReadOnlySpan<byte> pageData, Crc32Footer footer)
        {
            // ★ 统一使用原始磁盘字节（对齐同步路径），避免 codec 序列化规范化产生不同 CRC
            int coverLen = LogPageFrameHeaderCodec.StructSize + dataLen;
            byte[] coverBuf = ArrayPool<byte>.Shared.Rent(coverLen);
            try
            {
                Span<byte> cover = coverBuf.AsSpan(0, coverLen);
                rawHeader.CopyTo(cover);
                pageData.CopyTo(cover.Slice(LogPageFrameHeaderCodec.StructSize));
                return UnifiedCrc.ComputeCrc32C(cover) == footer.Crc;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(coverBuf);
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // 共享 helper
        // ═══════════════════════════════════════════════════════════════

        private void EnsurePageBuf(int len)
        {
            if (_pageBuf is null || _pageBuf.Length < len)
                _pageBuf = new byte[len];
        }

        private static TempBuffer RentTemp(int size) => new(size);

        private readonly struct TempBuffer(int size) : IDisposable
        {
            private readonly byte[] _buf = ArrayPool<byte>.Shared.Rent(size);

            // ★ Memory 必须按请求大小切片——ArrayPool.Rent 返回的数组可能更大，
            //   直接返回 _buf.Length 会让 ReadAsync 多读字节，吞掉后续 frame 数据。
            public Memory<byte> Memory => _buf.AsMemory(0, size);
            public void Dispose() { ArrayPool<byte>.Shared.Return(_buf); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _reader.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            _reader.Dispose();
            await ValueTask.CompletedTask;
        }
    }
}
