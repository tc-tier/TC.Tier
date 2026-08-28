using System.Buffers.Binary;
using System.IO.Hashing;

namespace TC.Tier.Core.IO.TierVolume;

/// <summary>
/// 日志 partial（raw-journal-design）——物理循环日志 + 有效前缀提交（ordered 模式）。
/// <para>★ 记录粒度 = 逻辑操作（物理事实内嵌——分配器决策随记录持久，重放确定性）；
///   无 TxCommit 框架——原子性由 CRC 有效前缀规则承载（屏障后的记录可见、屏障前的不可见）。</para>
/// <para>★ 提交 = 排干（数据先于屏障）+ 批量记录落区 + 单屏 fsync；检查点衰减为周期动作（§5）。</para>
/// <para>★ 重放与写路径共用操作函数（_journalReplaying 闸——重放期发射器静默，防双重记录）。</para>
/// </summary>
public sealed partial class TierVolumeFs
{
    private enum JournalRecordType : byte
    {
        FileCreate = 1,
        FileDelete = 2,
        FileMove = 3,
        DirCreate = 4,
        DirDelete = 5,
        DirMove = 6,
        SetLength = 7,
        ExtentTailExtend = 8,
        ExtentAppend = 9,
        ExtentCover = 10,
        PunchHole = 11,
        ExtentWritten = 12,
        SetExtra = 13,
        Pad = 14,
        ExtentRelocate = 15,   // RM-04 v2a：迁移式缩容——旧区间段 → 新物理 run 列表（重放同构）
        SnapshotCreate = 16,   // V2 §1.1：快照创建（翻转后发射——重放幂等：表已含则跳）
        SnapshotDelete = 17,   // V2 §1.1：快照删除（翻转后发射——重放幂等：表已不含则跳）
    }

    // 运行态（_journalOn = false 时全部空转）
    internal bool _journalOn;
    private long _journalAreaStart;              // 绝对字节偏移
    private long _journalAreaLen;                // 字节长度
    private long _journalHead;                   // 区内下一记录字节偏移（块对齐）
    private ulong _journalGen;                   // 区代数（检查点复位 ++——防环绕陈旧块）
    private ulong _lsn;                          // 最后分配的 LSN
    private ulong _committedLsn;                 // 最后屏障提交的 LSN
    private readonly List<(JournalRecordType Type, byte[] Body, int BodyLen, ulong Lsn)> _pendingRecords = new();   // 待屏障记录（WAL：先于屏障——raw-journal §4 W1；LSn 发射时预分配；Body = 池化缓冲——RM-30）
    private readonly System.Collections.Concurrent.ConcurrentQueue<byte[]> _recordBufPool = new();   // RM-30：记录缓冲池（每记录 2-3 次堆分配 → 稳态零分配）
    private const int RecordBufInitialBytes = 128;
    private const int RecordBufPoolMaxBytes = 512;   // 超限缓冲不池化（防池膨胀——一次性 GC）
    private bool _journalReplaying;              // 重放闸（发射器静默——共用函数防双重记录）
    private readonly object _journalGate = new();   // 日志区写 + 屏障串行化（W2——与检查点互斥）
    private int _inFlightSnapshots;              // 在途两段式快照（0/1 闸——W2；检查点不等待：CkptLsn 取 _lsn 覆盖）

    private const int JournalHeaderSize = 32;    // magic(4) type(1) pad(1) rsvd(2) lsn(8) gen(8) bodyLen(4) bodyCrc(4)

    /// <summary>格式化/打开后初始化日志运行态（superblock 为权威）。</summary>
    private void JournalInitFromSuperblock()
    {
        _journalOn = (_sb.Flags & FlagJournaled) != 0 && _sb.JournalBlocks > 0;
        if (!_journalOn) return;
        _journalAreaStart = (long)(_sb.JournalStart * _sb.BlockSize);
        _journalAreaLen = (long)(_sb.JournalBlocks * _sb.BlockSize);
        _journalGen = _sb.JournalGeneration;
        _lsn = _sb.JournalHeadLsn;
        _committedLsn = _sb.JournalHeadLsn;
        _journalHead = 0;
        _deltaDirtyBlocks = _snapshotMount ? null : [];   // ★ V2 §1.2：增量窗口脏块跟踪（快照挂载无数据写）
    }

    // ═══════════════ 记录发射（在线路径——持 _metadataLock；重放期静默）═══════════════

    private void Emit(JournalRecordType type, Action<BinaryWriter> write)
    {
        // RM-30：池化缓冲 + 增长式写入（此前 MemoryStream + ToArray = 每记录 2-3 次堆分配——20K ops/s = 6 万分配/s）
        if (!_recordBufPool.TryDequeue(out var buf)) buf = new byte[RecordBufInitialBytes];
        var stream = new PooledBufStream(buf);
        using (var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            write(w);
        _pendingRecords.Add((type, stream.Buffer, stream.LengthBytes, ++_lsn));   // LSN 发射时预分配（W2：等待者判据）
    }

    /// <summary>归还记录缓冲（消费成功后调用；池上限外的丢弃——防膨胀）。</summary>
    private void ReturnRecordBuffers(
        System.Collections.Generic.IEnumerable<(JournalRecordType Type, byte[] Body, int BodyLen, ulong Lsn)> records)
    {
        foreach (var (_, body, _, _) in records)
            if (body.Length <= RecordBufPoolMaxBytes)
                _recordBufPool.Enqueue(body);
    }

    /// <summary>池化增长缓冲的流包装（BinaryWriter 载体——Write 只追加；RM-30）。</summary>
    private sealed class PooledBufStream(byte[] buffer) : Stream
    {
        private byte[] _buf = buffer;
        private int _len;
        public byte[] Buffer => _buf;
        public int LengthBytes => _len;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _len;
        public override long Position { get => _len; set => throw new NotSupportedException(); }

        public override void Write(byte[] b, int offset, int count) => Write(b.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> b)
        {
            if (_len + b.Length > _buf.Length)
            {
                var grown = new byte[Math.Max(_buf.Length * 2, _len + b.Length)];
                _buf.AsSpan(0, _len).CopyTo(grown);
                _buf = grown;
            }
            b.CopyTo(_buf.AsSpan(_len));
            _len += b.Length;
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private void JnlFileCreate(string path, long createdTicks)
    {
        if (!_journalOn || _journalReplaying) return;
        Emit(JournalRecordType.FileCreate, w => { WriteStr(w, path); w.Write(createdTicks); });
    }

    private void JnlFileDelete(string path)
    {
        if (!_journalOn || _journalReplaying) return;
        Emit(JournalRecordType.FileDelete, w => WriteStr(w, path));
    }

    private void JnlFileMove(string src, string dst, bool overwrite)
    {
        if (!_journalOn || _journalReplaying) return;
        Emit(JournalRecordType.FileMove, w => { WriteStr(w, src); WriteStr(w, dst); w.Write(overwrite); });
    }

    private void JnlDirCreate(string path)
    {
        if (!_journalOn || _journalReplaying) return;
        Emit(JournalRecordType.DirCreate, w => WriteStr(w, path));
    }

    private void JnlDirDelete(string path)
    {
        if (!_journalOn || _journalReplaying) return;
        Emit(JournalRecordType.DirDelete, w => WriteStr(w, path));
    }

    private void JnlDirMove(string src, string dst)
    {
        if (!_journalOn || _journalReplaying) return;
        Emit(JournalRecordType.DirMove, w => { WriteStr(w, src); WriteStr(w, dst); });
    }

    private void JnlSetLength(string path, long newLen)
    {
        if (!_journalOn || _journalReplaying) return;
        Emit(JournalRecordType.SetLength, w => { WriteStr(w, path); w.Write(newLen); });
    }

    private void JnlExtentTailExtend(string path, long newTailEnd, long newLen)
    {
        if (!_journalOn || _journalReplaying) return;
        Emit(JournalRecordType.ExtentTailExtend, w => { WriteStr(w, path); w.Write(newTailEnd); w.Write(newLen); });
    }

    private void JnlExtentAppend(string path, long start, long len, ulong phys, ExtentState state, long newLen)
    {
        if (!_journalOn || _journalReplaying) return;
        Emit(JournalRecordType.ExtentAppend, w =>
        {
            WriteStr(w, path);
            w.Write(start);
            w.Write(len);
            w.Write(phys);
            w.Write((byte)state);
            w.Write(newLen);
        });
    }

    private void JnlExtentCover(string path, long rangeStart, long rangeLen, ulong newPhys, long newLen)
    {
        if (!_journalOn || _journalReplaying) return;
        Emit(JournalRecordType.ExtentCover, w =>
        {
            WriteStr(w, path);
            w.Write(rangeStart);
            w.Write(rangeLen);
            w.Write(newPhys);
            w.Write(newLen);
        });
    }

    private void JnlPunchHole(string path, long offset, long length)
    {
        if (!_journalOn || _journalReplaying) return;
        Emit(JournalRecordType.PunchHole, w => { WriteStr(w, path); w.Write(offset); w.Write(length); });
    }

    private void JnlExtentWritten(string path, long logicalStart, long convertStart, long convertEnd)
    {
        if (!_journalOn || _journalReplaying) return;
        Emit(JournalRecordType.ExtentWritten, w =>
        {
            WriteStr(w, path);
            w.Write(logicalStart);   // 区间身份（x.LogicalStart）
            w.Write(convertStart);
            w.Write(convertEnd);     // 块粒度转换范围（B1 族修复——未触及块保持 Unwritten）
        });
    }

    /// <summary>迁移记录发射（RM-04 v2a）。</summary>
    private void JnlExtentRelocate(string path, long oldStart, long oldLen, List<(ulong Phys, long Len)> newRuns)
    {
        if (!_journalOn || _journalReplaying) return;
        Emit(JournalRecordType.ExtentRelocate, w =>
        {
            WriteStr(w, path);
            w.Write(oldStart);
            w.Write(oldLen);
            w.Write((uint)newRuns.Count);
            foreach (var (phys, len) in newRuns)
            {
                w.Write(phys);
                w.Write(len);
            }
        });
    }

    internal void JnlSetExtra(string path, byte[] extra)
    {
        if (!_journalOn || _journalReplaying) return;
        Emit(JournalRecordType.SetExtra, w =>
        {
            WriteStr(w, path);
            w.Write((uint)extra.Length);
            w.Write(extra);
        });
    }

    /// <summary>快照创建记录（V2 §1.1——检查点翻转后发射；重放幂等：表已含同捕获 LSN 即跳）。</summary>
    private void JnlSnapshotCreate(string name, long ticks, ulong captureLsn)
    {
        if (!_journalOn || _journalReplaying) return;
        Emit(JournalRecordType.SnapshotCreate, w => { WriteStr(w, name); w.Write(ticks); w.Write(captureLsn); });
    }

    /// <summary>快照删除记录（V2 §1.1——检查点翻转后发射；重放幂等：表已不含即跳）。</summary>
    private void JnlSnapshotDelete(string name, ulong captureLsn)
    {
        if (!_journalOn || _journalReplaying) return;
        Emit(JournalRecordType.SnapshotDelete, w => { WriteStr(w, name); w.Write(captureLsn); });
    }

    private static void WriteStr(BinaryWriter w, string s)
    {
        var b = System.Text.Encoding.UTF8.GetBytes(s);
        w.Write((uint)b.Length);
        w.Write(b);
    }

    // ═══════════════ 提交（单屏障——raw-journal §4；W2 组提交两段式）═══════════════

    /// <summary>日志提交（W2 组提交）：<paramref name="holdLock"/>=true 全程持元数据锁（检查点/关卷路径——
    /// W3 切割点不变量直接成立）；=false（Flush/flusher 路径）两段式——锁内摘快照 → 释放元数据锁 →
    /// gate 内写区+屏障（fsync 期间数据面不停摆——组提交核心收益）→ 重拿锁收尾。
    /// 期间检查点完成（gen 翻转）→ 记录放回重试（罕见路径转硬模式）。</summary>
    internal void JournalCommit(bool holdLock = true)
    {
        if (!_journalOn) return;
        while (true)
        {
            if (holdLock)
            {
                lock (MetadataLock)
                {
                    CommitCore();
                    return;
                }
            }
            (JournalRecordType, byte[], int, ulong)[] snapshot = null!;
            ulong snapGen = 0;
            long snapHead = 0;
            var emptyRecords = false;
            var dataOnly = false;
            lock (MetadataLock)
            {
                if (_pendingRecords.Count == 0)
                {
                    emptyRecords = true;
                    // RM-40：数据-only 判据 = 脏页 ∪ 载体在途写（写绕/直达直落载体不计脏页——
                    // 曾致覆写+Flush 空转返回零 fsync：数据仅在内核页缓存，断电即丢）
                    dataOnly = Volatile.Read(ref _dirtyBytes) > 0 || Volatile.Read(ref _carrierWritePendingBytes) > 0;
                }
                else if (Volatile.Read(ref _inFlightSnapshots) > 0)
                {
                    holdLock = true;   // 已有在途快照（其屏障将覆盖本批可见性判断复杂化）——转硬模式排队（组提交：重叠屏障合并为一个的朴素形态）
                    continue;
                }
                else
                {
                    snapshot = _pendingRecords.ToArray();
                    _pendingRecords.Clear();
                    snapGen = _journalGen;
                    snapHead = _journalHead;   // 写区起点（原始值）
                    _inFlightSnapshots = 1;
                    try
                    {
                        ClearCleanBitLocked();
                        // 占位预推（本批为唯一在途——无接力竞态；写区后以真实终点校正）
                        var reserve = snapHead;
                        foreach (var (_, _, bodyLen, _) in snapshot)
                            reserve += FramedSize(bodyLen);
                        _journalHead = reserve % _journalAreaLen;
                    }
                    catch
                    {
                        _inFlightSnapshots = 0;
                        foreach (var t in snapshot) _pendingRecords.Add(t);   // 放回
                        throw;
                    }
                }
            }
            if (emptyRecords)
            {
                if (dataOnly)
                    FlushDirtyPages(sync: true);   // 数据-only 快道（锁外排干——O_DIRECT 写不可阻塞数据面）
                return;
            }
            try
            {
                // W2（ordered）：数据先于日志屏障——锁外排干（脏页集经页门拴并发安全；
                // 序保持：数据页触载体先于下方 gate 内记录写入与统一 fsync 屏障）
                FlushDirtyPages(sync: false);
            }
            catch
            {
                _inFlightSnapshots = 0;
                lock (MetadataLock)
                    foreach (var t in snapshot) _pendingRecords.Add(t);   // 放回（锁内——发射器并发）
                throw;
            }
            // ── 元数据锁外：gate 串行化写区+屏障（检查点互斥）═══
            lock (_journalGate)
            {
                var endHead = WriteSnapshotToArea(snapshot, snapHead, snapGen);
                JournalBarrier();   // 单屏障——数据页 + 日志记录同一覆盖（写穿档 = 写穿完成即屏障）
                if (snapGen == _journalGen)
                    _journalHead = endHead;   // 真实终点同步（含环绕 Pad——防漂移；检查点翻转后 head 由检查点权威）
                Volatile.Write(ref _inFlightSnapshots, 0);
            }
            lock (MetadataLock)
            {
                if (snapGen == _journalGen)
                {
                    _committedLsn = Math.Max(_committedLsn, snapshot[^1].Item4);
                    CheckDecayLocked();
                    return;
                }
                // 检查点在我们放锁期间完成（gen 翻转）——快照字节已是旧代数（重放器将忽略）：
                // 记录效果已在检查点镜像内（内存变更先于摘快照）且 CkptLsn 等待过在途计数 → 直接视为已提交
                _committedLsn = Math.Max(_committedLsn, snapshot[^1].Item4);
                return;
            }
        }
    }

    /// <summary>检查点/关卷路径的提交核心（全程持锁）。
    /// W3 切割点不变量（W2 形态）：在途两段式快照不等待——其记录的内存效果已在镜像、
    /// CkptLsn 取 _lsn（发射上界 ≥ 在途 lsn）→ 重放跳过；其在途字节用旧 gen 写 → 重放器忽略；
    /// 其数据随检查点屏障持久化（ordered——write 已完成）。无双放、无丢失。</summary>
    private void CommitCore()
    {
        if (_pendingRecords.Count > 0)
        {
            // ★ 区写者互斥（RM-03 修复）：在途两段式快照持有 head 预留——硬模式直接写预留处，
            // 两段式完成时 head 回退会覆盖硬模式记录（并发 Flush 下 2/200 文件丢失实证）。
            // 等待其 gate 段清 inFlight（gate 段不需要元数据锁——本线程持锁等待无死锁环）。
            while (Volatile.Read(ref _inFlightSnapshots) > 0)
                Thread.Yield();
            ClearCleanBitLocked();
            FlushDirtyPages(sync: false);   // W2（ordered）：数据先于日志屏障
            var snapshot = _pendingRecords.ToArray();
            long head;
            lock (_journalGate)
            {
                head = WriteSnapshotToArea(snapshot, _journalHead, _journalGen);
                JournalBarrier();   // 单屏障（写穿档 = 写穿完成即屏障）
            }
            _pendingRecords.Clear();
            _journalHead = head;
            _committedLsn = Math.Max(_committedLsn, _lsn);
            CheckDecayLocked();
        }
        else if (Volatile.Read(ref _dirtyBytes) > 0 || Volatile.Read(ref _carrierWritePendingBytes) > 0)
            FlushDirtyPages(sync: true);   // RM-40：脏页 ∪ 载体在途（写绕/直达）——空记录提交也必须屏障
    }

    /// <summary>崩溃检测位（§4.1 语义在日志形态的保持）：写意图活动首次提交前清 clean。</summary>
    private void ClearCleanBitLocked()
    {
        if ((_sb.Flags & FlagClean) != 0)
        {
            _sb.Flags = (ushort)(_sb.Flags & ~FlagClean);
            RotateSuperblocks();
        }
    }

    /// <summary>衰减策略 §5：占用 &gt; 75% → 强制检查点（head 复位由 CommitMetadata 承担）。fs 锁内。</summary>
    private void CheckDecayLocked()
    {
        if (_journalHead > _journalAreaLen * 3 / 4)
            CommitMetadata();
    }

    /// <summary>快照落区（gate 内）：逐记录块对齐成帧；区尾余量不足 → Pad 填满环绕；连续段合并单 write。
    /// 使用快照捕获的 gen/head（两段式下字段可能已被并发推进——快照值即本批事实）。
    /// 返回实际结束 head（含环绕 Pad——调用方据此同步 _journalHead，防漂移）。</summary>
    private long WriteSnapshotToArea((JournalRecordType Type, byte[] Body, int BodyLen, ulong Lsn)[] records, long head, ulong gen)
    {
        var buffer = new byte[_pageSize];
        long stretchStart = head;
        var stretch = new List<byte>();
        foreach (var (type, body, bodyLen, lsn) in records)
        {
            var framed = FramedSize(bodyLen);
            if (head + framed > _journalAreaLen)
            {
                FlushStretch(stretch, stretchStart);
                stretch.Clear();
                var padLen = _journalAreaLen - head;
                Array.Clear(buffer);
                EncodeRecordHeader(buffer, JournalRecordType.Pad, lsn, (int)(padLen - JournalHeaderSize), gen);
                WriteCarrier(_journalAreaStart + head, buffer.AsSpan(0, (int)padLen));
                head = 0;
                stretchStart = 0;
            }
            EncodeRecord(stretch, type, body, bodyLen, lsn, gen);
            head += framed;
        }
        FlushStretch(stretch, stretchStart);
        ReturnRecordBuffers(records);   // RM-30：消费成功——缓冲归还（异常路径不归还：两段式失败重试需记录原样在档）
        return head;

        void FlushStretch(List<byte> bytes, long start)
        {
            if (bytes.Count == 0) return;
            var chunk = new byte[bytes.Count];
            bytes.CopyTo(chunk);
            WriteCarrier(_journalAreaStart + start, chunk);
        }
    }

    private int FramedSize(int bodyLen)
        => (JournalHeaderSize + bodyLen + _pageSize - 1) / _pageSize * _pageSize;

    private void EncodeRecord(List<byte> into, JournalRecordType type, byte[] body, int bodyLen, ulong lsn, ulong gen)
    {
        var header = new byte[JournalHeaderSize];
        EncodeRecordHeader(header, type, lsn, bodyLen, gen);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), Crc32.HashToUInt32(body.AsSpan(0, bodyLen)));
        into.AddRange(header);
        into.AddRange(body.AsSpan(0, bodyLen));
        var pad = FramedSize(bodyLen) - JournalHeaderSize - bodyLen;
        if (pad > 0) into.AddRange(stackalloc byte[pad]);
    }

    private void EncodeRecordHeader(Span<byte> header, JournalRecordType type, ulong lsn, int bodyLen, ulong? gen = null)
    {
        "RJRN"u8.CopyTo(header);
        header[4] = (byte)type;
        BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(8), lsn);
        BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(16), gen ?? _journalGen);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(24), (uint)bodyLen);
    }

    // ═══════════════ 重放（dirty 打开——raw-journal §6）═══════════════

    /// <summary>日志区物理序帧（扫描产物——重放/增量导出共用）。</summary>
    private readonly record struct JournalFrame(ulong Lsn, JournalRecordType Type, long AreaOffset, int FramedLen, int BodyLen);

    /// <summary>日志区有效前缀扫描（重放/增量导出共用——raw-journal §6 规则同族）：
    /// magic/代数/帧完整/CRC 违约即止（有效前缀）；环绕续扫一次（Pad 之后）；
    /// 返回物理序帧 + 扫描终止偏移（追加起点 = 有效前缀末尾）。</summary>
    private List<JournalFrame> ScanJournalFrames(out long endOffset)
    {
        var frames = new List<JournalFrame>();
        var areaLen = _journalAreaLen;
        var offset = 0L;
        var wrapped = false;
        var headerBuf = new byte[JournalHeaderSize];   // 循环外单次分配（CA2014——stackalloc 移出循环）
        while (offset < areaLen)
        {
            var remaining = areaLen - offset;
            if (remaining < JournalHeaderSize) break;
            ReadJournalSpan(offset, headerBuf);
            if (!headerBuf.AsSpan(0, 4).SequenceEqual("RJRN"u8)) break;                 // 有效前缀终止
            var type = (JournalRecordType)headerBuf[4];
            var lsn = BinaryPrimitives.ReadUInt64LittleEndian(headerBuf.AsSpan(8));
            var gen = BinaryPrimitives.ReadUInt64LittleEndian(headerBuf.AsSpan(16));
            var bodyLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(headerBuf.AsSpan(24));
            var bodyCrc = BinaryPrimitives.ReadUInt32LittleEndian(headerBuf.AsSpan(28));
            var framed = FramedSize(bodyLen);
            if (bodyLen < 0 || framed > remaining) break;                                 // 撕裂尾
            if (gen != _journalGen) break;                                                // 陈旧代数（复位前旧块）
            var body = new byte[bodyLen];
            ReadJournalSpan(offset + JournalHeaderSize, body);
            if (Crc32.HashToUInt32(body) != bodyCrc) break;               // 撕裂/损毁
            frames.Add(new JournalFrame(lsn, type, offset, framed, bodyLen));
            offset += framed;
            if (offset >= areaLen && !wrapped) { wrapped = true; offset = 0; }    // 环绕续扫（Pad 之后）
        }
        endOffset = offset;
        return frames;
    }

    /// <summary>日志重放：扫全区 → 有效前缀（magic/gen/CRC/帧完整）→ LSN &gt; CkptLsn 的记录逐条重执行。
    /// 零载体写（位图/结构全内存；数据已 ordered 持久）。重放后首个 Flush/检查点落盘。
    /// D11：逐帧分块读（日志区可任意大——不再整区载入内存，也不再受 (int) 截断）。</summary>
    private void JournalReplay()
    {
        ulong maxLsn = _sb.JournalCkptLsn;
        var apply = new List<(ulong Lsn, JournalRecordType Type, byte[] Body)>();
        var frames = ScanJournalFrames(out var endOffset);
        foreach (var frame in frames)
        {
            if (frame.Lsn <= _sb.JournalCkptLsn || frame.Type == JournalRecordType.Pad)
                continue;
            var body = new byte[frame.BodyLen];
            ReadJournalSpan(frame.AreaOffset + JournalHeaderSize, body);
            apply.Add((frame.Lsn, frame.Type, body));
            maxLsn = Math.Max(maxLsn, frame.Lsn);
        }
        _journalReplaying = true;
        try
        {
            apply.Sort((a, b) => a.Lsn.CompareTo(b.Lsn));   // 环绕后物理序 ≠ LSN 序——按提交序应用
            foreach (var (_, type, body) in apply)
                ApplyJournalRecord(type, body);
        }
        finally
        {
            _journalReplaying = false;
        }
        _lsn = Math.Max(maxLsn, _sb.JournalHeadLsn);
        _committedLsn = _lsn;
        _journalHead = endOffset >= _journalAreaLen ? 0 : endOffset;   // 追加起点 = 有效前缀末尾
    }

    /// <summary>日志区内读（D11——分块路径：记录不跨区尾，区内任意段经载体读通道即可）。</summary>
    private void ReadJournalSpan(long journalOffset, Span<byte> dest)
    {
        var done = 0;
        while (done < dest.Length)
        {
            var local = journalOffset + done;
            var take = (int)Math.Min(dest.Length - done, _journalAreaLen - local);
            ReadCarrierExactly(_journalAreaStart + local, dest.Slice(done, take));
            done += take;
        }
    }

    /// <summary>重执行一条记录（与在线路径共用操作函数——物理事实来自记录，重放不重新决策分配）。</summary>
    private void ApplyJournalRecord(JournalRecordType type, byte[] body)
    {
        using var r = new BinaryReader(new System.IO.MemoryStream(body, writable: false));
        switch (type)
        {
            case JournalRecordType.FileCreate:
            {
                var path = ReadStr(r);
                var ticks = r.ReadInt64();
                _entries[path] = new Entry { Path = path, CreatedTicks = ticks };
                _sortedKeys.Add(path);   // RM-11 索引维护
                MetadataDirty = true;
                break;
            }
            case JournalRecordType.FileDelete:
            {
                var path = ReadStr(r);
                if (_entries.Remove(path, out var e))
                {
                    _sortedKeys.Remove(path);   // RM-11 索引维护
                    foreach (var x in e.Extents)
                    {
                        var blocks = (uint)((x.Length + _pageSize - 1) / _pageSize);
                        ReleaseBlocksFrozenAware(x.PhysicalBlock, blocks);   // ★ V2 §1.1：重放释放同过滤（快照冻结块保持 used）
                        InvalidateCacheBlocks(x.PhysicalBlock, blocks);   // RM-12：释放退出缓存
                    }
                    _appendCursors.TryRemove(path, out _);
                }
                MetadataDirty = true;
                break;
            }
            case JournalRecordType.FileMove:
            {
                var src = ReadStr(r);
                var dst = ReadStr(r);
                var overwrite = r.ReadBoolean();
                if (_entries.TryGetValue(dst, out var old))
                {
                    if (!overwrite) throw NewReplayError("FileMove 目标已存在但记录为非覆盖");
                    foreach (var x in old.Extents)
                    {
                        var blocks = (uint)((x.Length + _pageSize - 1) / _pageSize);
                        ReleaseBlocksFrozenAware(x.PhysicalBlock, blocks);   // ★ V2 §1.1：重放释放同过滤
                        InvalidateCacheBlocks(x.PhysicalBlock, blocks);   // RM-12：释放退出缓存
                    }
                    _entries.Remove(dst);
                    _sortedKeys.Remove(dst);
                }
                if (_entries.Remove(src, out var e))
                {
                    e.Path = dst;
                    _entries[dst] = e;
                    _sortedKeys.Remove(src);
                    _sortedKeys.Add(dst);   // RM-11 索引维护
                    _appendCursors.TryRemove(src, out _);
                }
                MetadataDirty = true;
                break;
            }
            case JournalRecordType.DirCreate:
            {
                var path = ReadStr(r);
                AddDirectoryRec(path);
                MetadataDirty = true;
                break;
            }
            case JournalRecordType.DirDelete:
            {
                _directories.Remove(ReadStr(r));
                MetadataDirty = true;
                break;
            }
            case JournalRecordType.DirMove:
            {
                ApplyDirMove(ReadStr(r), ReadStr(r));
                MetadataDirty = true;
                break;
            }
            case JournalRecordType.SetLength:
            {
                var path = ReadStr(r);
                var len = r.ReadInt64();
                TruncateEntry(_entries[path], len);   // 共用（收缩侧释放从在档区间推导——重放期发射器静默）
                break;
            }
            case JournalRecordType.ExtentTailExtend:
            {
                var path = ReadStr(r);
                var newTailEnd = r.ReadInt64();
                var newLen = r.ReadInt64();
                var e = _entries[path];
                var tail = e.Extents[^1];
                var bs = (long)_pageSize;
                var extBlocks = (uint)((RoundUp(newTailEnd, bs) - tail.LogicalEnd) / bs);
                MarkBlocks(tail.PhysicalBlock + (ulong)(tail.Length / bs), extBlocks, used: true);
                var grown = new List<Extent>(e.Extents);   // CoW（RM-12 一致性——重放也走不可变交换）
                grown[^1] = tail with { Length = tail.Length + (long)extBlocks * bs };
                e.Extents = grown;
                e.LogicalLength = Math.Max(e.LogicalLength, newLen);
                MetadataDirty = true;
                break;
            }
            case JournalRecordType.ExtentAppend:
            {
                var path = ReadStr(r);
                var start = r.ReadInt64();
                var len = r.ReadInt64();
                var phys = r.ReadUInt64();
                var state = (ExtentState)r.ReadByte();
                var newLen = r.ReadInt64();
                var e = _entries[path];
                MarkBlocks(phys, (uint)((len + _pageSize - 1) / _pageSize), used: true);
                e.Extents = new List<Extent>(e.Extents) { new Extent(start, len, phys, state) };   // CoW（RM-12）
                e.LogicalLength = Math.Max(e.LogicalLength, newLen);
                MetadataDirty = true;
                break;
            }
            case JournalRecordType.ExtentCover:
            {
                var path = ReadStr(r);
                var rangeStart = r.ReadInt64();
                var rangeLen = r.ReadInt64();
                var newPhys = r.ReadUInt64();
                var newLen = r.ReadInt64();
                var e = _entries[path];
                e.Extents = ApplyExtentCover(e, e.Extents, rangeStart, rangeLen, newPhys);   // 共用（相交释放从在档区间推导）
                e.LogicalLength = Math.Max(e.LogicalLength, newLen);
                break;
            }
            case JournalRecordType.PunchHole:
            {
                var path = ReadStr(r);
                var off = r.ReadInt64();
                var len = r.ReadInt64();
                PunchHoleEntry(_entries[path], off, len);   // 共用
                break;
            }
            case JournalRecordType.ExtentWritten:
            {
                var path = ReadStr(r);
                var logicalStart = r.ReadInt64();
                var convertStart = r.ReadInt64();
                var convertEnd = r.ReadInt64();
                var e = _entries[path];
                var x = e.Extents.FirstOrDefault(t => t.LogicalStart == logicalStart);
                if (x.LogicalStart != logicalStart || x.Length == 0)
                    throw NewReplayError($"ExtentWritten 区间缺失：{path} @{logicalStart}");
                e.Extents = ConvertExtentRange(e, e.Extents, x, convertStart, convertEnd);   // 共用（块粒度转换——未触及块保持 Unwritten）
                MetadataDirty = true;   // 重放后统一落盘（原 CoW 方法内置置位——签名改后由调用方承担）
                break;
            }
            case JournalRecordType.ExtentRelocate:
            {
                var path = ReadStr(r);
                var oldStart = r.ReadInt64();
                var oldLen = r.ReadInt64();
                var runCount = r.ReadInt32();
                var runs = new List<(ulong Phys, long Len)>(runCount);
                for (var i = 0; i < runCount; i++)
                    runs.Add((r.ReadUInt64(), r.ReadInt64()));
                ApplyExtentRelocate(_entries[path], oldStart, oldLen, runs);
                break;
            }
            case JournalRecordType.SetExtra:
            {
                var path = ReadStr(r);
                var len = r.ReadInt32();
                _entries[path].Extra = r.ReadBytes(len);
                MetadataDirty = true;
                break;
            }
            case JournalRecordType.SnapshotCreate:
            {
                // V2 §1.1：记录在检查点翻转后发射——记录持久 ⟹ 翻转持久 ⟹ 表已含条目 → 幂等跳。
                // 其余分支不可达（翻转先于记录、删除快照的检查点会覆盖本记录 LSN）——违约即数据不一致。
                var name = ReadStr(r);
                _ = r.ReadInt64();
                var lsn = r.ReadUInt64();
                if (!_sb.Snapshots.Any(s => s.CaptureLsn == lsn))
                    throw NewReplayError($"SnapshotCreate 记录无对应快照表条目：{name}——检查点翻转与记录序违约");
                break;
            }
            case JournalRecordType.SnapshotDelete:
            {
                // V2 §1.1：同上——翻转后发射；重放时表必已不含（删除检查点覆盖记录 LSN）→ 幂等跳。
                var name = ReadStr(r);
                var lsn = r.ReadUInt64();
                if (_sb.Snapshots.Any(s => s.CaptureLsn == lsn))
                    throw NewReplayError($"SnapshotDelete 记录对应快照仍在表：{name}——检查点翻转与记录序违约");
                break;
            }
            default:
                throw NewReplayError($"未知记录类型 {type}");
        }
    }

    private static string ReadStr(BinaryReader r)
    {
        var len = r.ReadInt32();
        return System.Text.Encoding.UTF8.GetString(r.ReadBytes(len));
    }

    private FileIOException NewReplayError(string detail)
        => new(IOError.IOFailure, $"日志重放失败：{detail}（有效前缀规则——检查点态可用，日志尾丢失）", _carrier.Path, "Replay");
}
