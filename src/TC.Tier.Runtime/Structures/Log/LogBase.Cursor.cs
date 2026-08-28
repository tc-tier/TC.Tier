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
        // ★ mid-frame 帧边界回退位（尾截断原地重写）：上一帧 header 声明的 dataLen 仍覆盖新帧字节——
        //   在期望 entry 头处读到新帧的 LPGF 头 → 记录新帧起点，换页前 Seek 回去按帧解析。
        private LogicalAddress? _rewindTo;
        // ★ 起点重定位（重放定位 O(n)→O(1024)）：startAddress 处读 StructSize 探测——是页帧头则直接
        //   起读（帧头对齐，零 skip）；是 entry 头则无帧头模式（_frameless）从 entry 地址直接解析，
        //   页尾靠 TryDetectFrameBoundary 探测下一页帧头换页（数据巧合 magic 风险同既有机制）。
        private bool _initialized;
        private bool _frameless;

        public PageFrameCursor(LogBase owner, LogicalAddress startAddress, LogicalAddress endAddress, bool verifyCrc)
        {
            _owner = owner;
            _verifyCrc = verifyCrc;
            _headerSize = owner.LogCodec.HeaderSize;

            // ★ 默认扫描终点 = 已落盘水位 FlushedTail（TailAddress 含内存页未 flush entry，不可读）
            var actualEnd = endAddress == default ? owner.FlushedTail : endAddress;
            // ★ startAddress 直接当 reader 起点（不再段首重扫）——首次加载分流：
            //   帧头 → 正常页解析；entry 头 → 无帧头模式（cursor 内跳过 < startAddress 的 entry）。
            _skipUntil = startAddress;
            _reader = owner._engine.OpenSequentialReader(startAddress, actualEnd,
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
                        // ★ mid-frame 帧边界探测（尾截断原地重写）：上一帧被 ReclaimTail 截到 entry 起点后，
                        //   新帧从截断点原地覆写——旧帧 header 声明的 dataLen 仍覆盖新帧字节。
                        //   在"期望 entry 头却读到新帧 LPGF 头"处判定旧帧数据区真实终点，退位解析新帧。
                        if (TryDetectFrameBoundary(headerSpan))
                        {
                            _rewindTo = _currentAddress;
                            _currentAddress = _pageEndAddr;
                            _pageBuf = null;
                            continue;
                        }
                        // ★ 无帧头模式：解析失败处 = 页数据尾（CRC+padding 区）或坏 entry——扇区对齐
                        //   步进探测下一页帧头（padding 契约：帧头扇区对齐，缓冲 cap 保证帧头在缓冲内）。
                        if (_frameless)
                        {
                            if (!TryFindNextFrameHeader()) return false;
                            continue;
                        }
                    }
                    else if (_frameless)
                    {
                        // ★ 无帧头模式缓冲耗尽（页尾剩余不足一条 entry 头）——同扇区探测
                        if (!TryFindNextFrameHeader()) return false;
                        continue;
                    }
                    // 当前页无更多有效 entry——换页
                    _currentAddress = _pageEndAddr;
                    _pageBuf = null;
                }

                // 需要加载新页：先看 reader 是否还有数据
                // ★ mid-frame 回退位：换页读之前 Seek 回新帧起点（reader 已越过它——旧帧 data 区一次读完）
                if (_rewindTo is { } rewindTo)
                {
                    _rewindTo = null;
                    if (rewindTo < _reader.End) _reader.Seek(rewindTo);
                }
                if (_reader.Position >= _reader.End) return false;
                if (!LoadNextPageSync()) return false;
            }
        }

        /// <summary>同步轨页加载分流：首次 = 帧头/entry 探测分流；无帧头模式 = 页尾探测续读。</summary>
        private bool LoadNextPageSync()
        {
            if (!_initialized)
            {
                _initialized = true;
                if (_skipUntil != default)
                    return TryLoadFromEntryStart();
            }
            else if (_frameless)
            {
                return TryResumeFrameless();
            }
            return LoadNextPage();
        }

        /// <summary>
        /// ★ 起点探测分流：读 startAddress 处 StructSize 验页帧头 magic——
        ///   帧头 → 回退后走正常 LoadNextPage（帧头对齐直接起读，零段首重扫）；
        ///   entry 头 → 回退后无帧头模式（LoadFramelessPage——从 entry 地址直接解析）。
        /// </summary>
        private bool TryLoadFromEntryStart()
        {
            var pos = _reader.Position;
            Span<byte> probe = stackalloc byte[LogPageFrameHeaderCodec.StructSize];
            if (_reader.Read(probe) < LogPageFrameHeaderCodec.StructSize) return false;
            var fh = LogPageFrameHeaderCodec.Read(probe);
            _reader.Seek(pos);
            if (fh.MagicValue == RecordMagic.LogPageFrame && fh.DataLength > 0 && fh.DataLength <= _owner.PageSize)
            {
                _skipUntil = default;   // 帧头对齐——首条 entry 地址 > 帧头，skip 语义解除
                return LoadNextPage();
            }
            return LoadFramelessPage();
        }

        /// <summary>
        /// 无帧头模式载入：从当前 reader 位置（entry 起点）读满缓冲上限，
        /// 页尾/坏 entry 处由 TryDetectFrameBoundary 探测下一页帧头（MoveNext 内）。
        /// </summary>
        private bool LoadFramelessPage()
        {
            // 缓冲上限 = PageSize（页 data 上限）+ 帧头 + CRC + 扇区余量——单 entry 不跨页，
            // 一次读入足以覆盖"页剩余 + 下页帧头"，页尾帧头探测无需补读。
            // ★ 扇区余量必须按 SectorSize（最大 padding = SectorSize−1 + 下帧头 8B）——
            //   硬编码 512 在 SectorSize=4096（raw 卷块大小）时缓冲罩不住下帧头：
            //   满页帧 padding 4,084B > 512，帧头落在缓冲外，页界探测断链（契约③ virtual 增量重放 0 条根因）。
            int cap = _owner.PageSize + LogPageFrameHeaderCodec.StructSize + Crc32FooterCodec.StructSize
                      + (int)_owner.SectorSize;
            EnsurePageBuf(cap);
            var pos = _reader.Position;
            int got = _reader.Read(_pageBuf.AsSpan(0, cap));
            if (got <= 0) return false;
            _pageStartAddr = pos;
            _currentAddress = pos;
            _currentOffsetInPage = 0;
            _pageDataLen = got;
            _pageEndAddr = _owner._engine.CalculationAddress(pos, got);
            _frameless = true;
            return true;
        }

        /// <summary>
        /// 无帧头模式续读：缓冲耗尽/解析失败后的下一个候选点——探测是否页帧头
        /// （是 → 转正常页模式；否 → 仍是 entry 数据，无帧头续读）。
        /// </summary>
        private bool TryResumeFrameless()
        {
            var pos = _reader.Position;
            if (pos >= _reader.End) return false;
            Span<byte> probe = stackalloc byte[LogPageFrameHeaderCodec.StructSize];
            if (_reader.Read(probe) < LogPageFrameHeaderCodec.StructSize) return false;
            if (LogPageFrameHeaderCodec.Read(probe).MagicValue == RecordMagic.LogPageFrame)
            {
                _reader.Seek(pos);
                _frameless = false;
                _pageBuf = null;
                return LoadNextPage();
            }
            _reader.Seek(pos);
            return LoadFramelessPage();
        }

        /// <summary>
        /// ★ 无帧头模式页尾定位：解析失败点 = 页数据尾（CRC+padding 区，&lt; 519B）——下一帧头
        /// 扇区对齐且在缓冲内。对齐到下一扇区边界逐扇区探测帧头 magic+dataLen，命中则转正常页模式。
        /// </summary>
        private bool TryFindNextFrameHeader()
        {
            int segId = _pageStartAddr.SegId;
            long baseOff = _pageStartAddr.Offset;
            long off = _currentAddress.Offset;
            long aligned = (off + _owner.SectorSize - 1) / _owner.SectorSize * _owner.SectorSize;
            long buffEnd = baseOff + _pageDataLen;
            for (long probeOff = aligned; probeOff + LogPageFrameHeaderCodec.StructSize <= buffEnd; probeOff += _owner.SectorSize)
            {
                int idx = (int)(probeOff - baseOff);
                var fh = LogPageFrameHeaderCodec.Read(_pageBuf.AsSpan(idx, LogPageFrameHeaderCodec.StructSize));
                if (fh.MagicValue != RecordMagic.LogPageFrame) continue;
                if (fh.DataLength <= 0 || fh.DataLength > _owner.PageSize) continue;
                _reader.Seek(new LogicalAddress(segId, probeOff));
                _frameless = false;
                _pageBuf = null;
                return LoadNextPage();
            }
            return false;   // 缓冲内无帧头（真 EOF/截断点）——安静停止
        }

        /// <summary>
        /// ★ 探测当前游标处是否为新帧起点（尾截断原地重写场景）：字节以 LPGF magic 开头且
        /// dataLen 合法（>0 且 ≤ PageSize）——在 entry 解析失败处（正常只会撞零/坏字节，
        /// 撞 LPGF 帧头 = 上一帧数据区被新帧覆盖的真实边界）。
        /// </summary>
        private bool TryDetectFrameBoundary(ReadOnlySpan<byte> entryHeaderArea)
        {
            if (entryHeaderArea.Length < LogPageFrameHeaderCodec.StructSize) return false;
            var fh = LogPageFrameHeaderCodec.Read(entryHeaderArea[..LogPageFrameHeaderCodec.StructSize]);
            if (fh.MagicValue != RecordMagic.LogPageFrame) return false;
            if (fh.DataLength <= 0 || fh.DataLength > _owner.PageSize) return false;
            return true;
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
            int gotData = _reader.Read(_pageBuf.AsSpan(0, dataLen));
            if (gotData < dataLen)
            {
                // ★ 物理截断帧容忍（ReclaimTail 打洞后 committed 边界落在帧中——读不满是 EOF 语义）：
                //   按实际读到的数据解析（截断点前的 entry 完整保留）；无数据 = 真 EOF。
                //   ★ 截断帧无 CRC/padding——直接按实际解析（Position=end 读 CRC 会失败——
                //   旧实现截断后仍读 CRC，帧被整页丢弃，截断点前的 entry 全部丢失）。
                if (gotData <= 0) return false;
                dataLen = gotData;
            }
            else
            {
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
                    if (remaining >= _headerSize && TryAdvanceEntry(_currentOffsetInPage, remaining))
                    {
                        if (_currentEntryAddr >= _skipUntil)   // ★ 断点续传：跳过 < _skipUntil 的 entry
                            return true;
                        // ★ skip——页内继续（continue 回 while 顶，不落入循环末尾的 reader EOF 检查：
                        //   LoadNextPage 读数据时 Position 可能已到 End（帧尾=end 的场景），
                        //   页内仍有未消费 entry——提前 EOF 会把 skip 中的 entry 全丢。同步轨同构）。
                        continue;
                    }
                    // entry 无效/页尾不足——先探测 mid-frame 帧边界（尾截断原地重写，同步轨同构），再换页
                    if (remaining >= _headerSize
                        && TryDetectFrameBoundary(_pageBuf.AsSpan(_currentOffsetInPage, remaining)))
                    {
                        _rewindTo = _currentAddress;
                        _currentAddress = _pageEndAddr;
                        _pageBuf = null;
                    }
                    else if (_frameless)
                    {
                        // ★ 无帧头模式：解析失败处扇区探测下一帧头（同步轨同构）
                        if (!await TryFindNextFrameHeaderAsync(ct).ConfigureAwait(false)) return false;
                        continue;
                    }
                    else
                    {
                        _currentAddress = _pageEndAddr;
                        _pageBuf = null;
                    }
                }

                // 需要加载新页：先看 reader 是否还有数据
                // ★ mid-frame 回退位：换页读之前 Seek 回新帧起点（reader 已越过它——旧帧 data 区一次读完）
                if (_rewindTo is { } rewindTo)
                {
                    _rewindTo = null;
                    if (rewindTo < _reader.End) _reader.Seek(rewindTo);
                }
                if (_reader.Position >= _reader.End) return false;
                if (!await LoadNextPageAsyncCore(ct).ConfigureAwait(false)) return false;
            }
        }

        /// <summary>异步轨页加载分流（同同步轨 LoadNextPageSync）。</summary>
        private ValueTask<bool> LoadNextPageAsyncCore(CancellationToken ct)
        {
            if (!_initialized)
            {
                _initialized = true;
                if (_skipUntil != default) return TryLoadFromEntryStartAsync(ct);
            }
            else if (_frameless)
            {
                return TryResumeFramelessAsync(ct);
            }
            return LoadNextPageAsync(ct);
        }

        /// <summary>★ 起点探测分流（异步轨，同同步轨 TryLoadFromEntryStart）。</summary>
        private async ValueTask<bool> TryLoadFromEntryStartAsync(CancellationToken ct)
        {
            var pos = _reader.Position;
            using var probeOwner = RentTemp(LogPageFrameHeaderCodec.StructSize);
            if (await _reader.ReadAsync(probeOwner.Memory, ct).ConfigureAwait(false) < LogPageFrameHeaderCodec.StructSize)
                return false;
            var fh = LogPageFrameHeaderCodec.Read(probeOwner.Memory.Span);
            _reader.Seek(pos);
            if (fh.MagicValue == RecordMagic.LogPageFrame && fh.DataLength > 0 && fh.DataLength <= _owner.PageSize)
            {
                _skipUntil = default;   // 帧头对齐——skip 语义解除
                return await LoadNextPageAsync(ct).ConfigureAwait(false);
            }
            return await LoadFramelessPageAsync(ct).ConfigureAwait(false);
        }

        /// <summary>无帧头模式载入（异步轨，同同步轨 LoadFramelessPage——扇区余量按 SectorSize，见同步轨注释）。</summary>
        private async ValueTask<bool> LoadFramelessPageAsync(CancellationToken ct)
        {
            int cap = _owner.PageSize + LogPageFrameHeaderCodec.StructSize + Crc32FooterCodec.StructSize
                      + (int)_owner.SectorSize;
            EnsurePageBuf(cap);
            var pos = _reader.Position;
            int got = await _reader.ReadAsync(_pageBuf.AsMemory(0, cap), ct).ConfigureAwait(false);
            if (got <= 0) return false;
            _pageStartAddr = pos;
            _currentAddress = pos;
            _currentOffsetInPage = 0;
            _pageDataLen = got;
            _pageEndAddr = _owner._engine.CalculationAddress(pos, got);
            _frameless = true;
            return true;
        }

        /// <summary>无帧头模式续读（异步轨，同同步轨 TryResumeFrameless）。</summary>
        private async ValueTask<bool> TryResumeFramelessAsync(CancellationToken ct)
        {
            var pos = _reader.Position;
            if (pos >= _reader.End) return false;
            using var probeOwner = RentTemp(LogPageFrameHeaderCodec.StructSize);
            if (await _reader.ReadAsync(probeOwner.Memory, ct).ConfigureAwait(false) < LogPageFrameHeaderCodec.StructSize)
                return false;
            if (LogPageFrameHeaderCodec.Read(probeOwner.Memory.Span).MagicValue == RecordMagic.LogPageFrame)
            {
                _reader.Seek(pos);
                _frameless = false;
                _pageBuf = null;
                return await LoadNextPageAsync(ct).ConfigureAwait(false);
            }
            _reader.Seek(pos);
            return await LoadFramelessPageAsync(ct).ConfigureAwait(false);
        }

        /// <summary>★ 无帧头模式页尾定位（异步轨，同同步轨 TryFindNextFrameHeader）。</summary>
        private async ValueTask<bool> TryFindNextFrameHeaderAsync(CancellationToken ct)
        {
            int segId = _pageStartAddr.SegId;
            long baseOff = _pageStartAddr.Offset;
            long off = _currentAddress.Offset;
            long aligned = (off + _owner.SectorSize - 1) / _owner.SectorSize * _owner.SectorSize;
            long buffEnd = baseOff + _pageDataLen;
            for (long probeOff = aligned; probeOff + LogPageFrameHeaderCodec.StructSize <= buffEnd; probeOff += _owner.SectorSize)
            {
                int idx = (int)(probeOff - baseOff);
                var fh = LogPageFrameHeaderCodec.Read(_pageBuf.AsSpan(idx, LogPageFrameHeaderCodec.StructSize));
                if (fh.MagicValue != RecordMagic.LogPageFrame) continue;
                if (fh.DataLength <= 0 || fh.DataLength > _owner.PageSize) continue;
                _reader.Seek(new LogicalAddress(segId, probeOff));
                _frameless = false;
                _pageBuf = null;
                return await LoadNextPageAsync(ct).ConfigureAwait(false);
            }
            return false;
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
            int hdrGot = await _reader.ReadAsync(headerMem, ct).ConfigureAwait(false);
            if (hdrGot < LogPageFrameHeaderCodec.StructSize)
            {
                    return false;
            }

            var hdr = LogPageFrameHeaderCodec.Read(headerMem.Span);
            if (hdr.MagicValue != RecordMagic.LogPageFrame)
            {
                    return false;
            }
            int dataLen = hdr.DataLength;
            if (dataLen <= 0 || dataLen > _owner.PageSize)
            {
                return false;
            }

            EnsurePageBuf(dataLen);
            int gotData = await _reader.ReadAsync(_pageBuf.AsMemory(0, dataLen), ct).ConfigureAwait(false);
            if (gotData < dataLen)
            {
                // ★ 物理截断帧容忍（同同步轨——ReclaimTail 打洞后按实际数据解析）
                //   ★ 截断帧无 CRC/padding——直接按实际解析（Position=end 读 CRC 会失败）
                if (gotData <= 0)
                {
                    return false;
                }
                dataLen = gotData;
            }
            else
            {
                using var crcOwner = RentTemp(Crc32FooterCodec.StructSize);
                var crcMem = crcOwner.Memory;
                if (await _reader.ReadAsync(crcMem, ct).ConfigureAwait(false) < Crc32FooterCodec.StructSize)
                {
                    return false;
                }

                // ★ 跳过 frame 尾部 padding（与同步轨 LoadNextPage 对等）
                int padding = _owner.ComputeFramePadding(dataLen);
                if (padding > 0) _reader.Skip(padding);

                if (_verifyCrc)
                {
                    var footer = Crc32FooterCodec.Read(crcMem.Span);
                    // ★ 统一使用原始磁盘字节计算 CRC（对齐同步路径），避免 codec 规范化导致 CRC 不一致
                    if (!VerifyPageCrc(dataLen, headerMem.Span, _pageBuf.AsSpan(0, dataLen), footer)) return false;
                }
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
