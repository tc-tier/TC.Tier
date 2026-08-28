using System.Collections.Concurrent;
using TC.Tier.Core.IO.Shared;

namespace TC.Tier.Core.IO.TierVolume;

/// <summary>
/// TierVolume 文件句柄——<see cref="IFileHandle"/> 的 TierVolume 实现（数据面经 fs 统一页管理，§3.4）。
/// <para>★ 共享设施全接线（§3.4 平权）：AppendCursor / SharingRegistry / IPoolAttachable / 在途计数。</para>
/// <para>★ 两档 IO（§3.4）：Hints.NoBuffering = 直达档（绕过自管页缓存）；否则缓冲档（命中 memcpy）。</para>
/// <para>★ v1 并发注记：数据面经 fs 元数据锁串行（正确性优先——锁外快照为演进项）。</para>
/// </summary>
internal sealed class TierVolumeFileHandle : IFileHandle, IPoolAttachable
{
    private readonly TierVolumeFs _fs;
    private readonly TierVolumeFs.Entry _entry;
    private readonly string _path;
    private readonly bool _writable;
    private readonly bool _direct;       // 直达档（NoBuffering 提示）
    private readonly bool _writeThrough; // 写透（WriteThrough 提示——RM-07：逐写提交，崩溃窗口归零）
    private readonly long _preallocateSize;
    private long _position;
    private AppendCursor? _appendCursor;
    private long _fallbackCursor;
    private int _inFlightOps;
    private int _disposed;
    private HandlePoolAttachment? _poolAttachment;
    private SharingRegistry? _sharingRegistry;
    private SharingRegistry.Entry? _sharingEntry;

    /// <summary>进程内字节范围锁表（§3.5 增强行——单实例下进程内即完备）。</summary>
    private static readonly ConcurrentDictionary<string, RangeLockTable> SRangeLocks = new();

    /// <summary>
    /// 初始化文件句柄（fs.Open 调用）。
    /// </summary>
    /// <param name="fs">TierVolume 文件系统实例</param>
    /// <param name="entry">文件条目</param>
    /// <param name="options">文件打开选项</param>
    internal TierVolumeFileHandle(TierVolumeFs fs, TierVolumeFs.Entry entry, FileOpenOptions options)
    {
        _fs = fs;
        _entry = entry;
        _path = entry.Path;
        _writable = options.Access is AccessMode.Write or AccessMode.ReadWrite;
        _direct = options.Hints.HasFlag(FileOpenHints.NoBuffering);
        _writeThrough = options.Hints.HasFlag(FileOpenHints.WriteThrough);
        _preallocateSize = options.PreallocateSize;
        _position = options.Mode == FileOpenMode.Append ? entry.LogicalLength : 0;
        if (_preallocateSize > 0)
            Preallocate();
    }

    /// <summary>注入文件级追加预留盒（fs.Open 调用）。</summary>
    /// <param name="cursor">追加预留盒</param>
    internal void AttachAppendCursor(AppendCursor cursor) => _appendCursor = cursor;

    /// <inheritdoc/>
    public string Path => _path;

    /// <inheritdoc/>
    /// <remarks>直达档恒 Supported（载体定位 IO 天然无缓存）；缓冲档 NotRequested。</remarks>
    public UnbufferedIoSupport UnbufferedSupport
        => _direct ? UnbufferedIoSupport.Supported : UnbufferedIoSupport.NotRequested;

    /// <inheritdoc/>
    /// <remarks>直达档按块对齐（v1 载体缓冲——对齐为语义承诺非硬约束）。</remarks>
    public long RequiredAlignment => _direct ? _fs.PageSize : 1;

    // ═══════════════ 位置读写 ═══════════════

    /// <summary>写入指定偏移的字节数据。</summary>
    /// <param name="offset">写入偏移</param>
    /// <param name="source">源数据</param>
    public void Write(long offset, ReadOnlySpan<byte> source)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var gate = _fs.BeginHandleMutation(nameof(Write), _path);
        ThrowIfNotWritable(nameof(Write));
        if (source.IsEmpty) return;
        _fs.WriteDataPlanned(_entry, offset, source, _direct);   // CORE-02 写计划协议：规划/数据/提交三段——数据段锁外
        if (_writeThrough)
            lock (_fs.MetadataLock)
                _fs.JournalCommit();   // RM-07 写透：逐写提交（O_SYNC 语义——崩溃窗口归零）
    }
    /// <inheritdoc/>
    public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> source, CancellationToken ct)
    {
        Write(offset, source.Span);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public int Read(long offset, Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _fs.ThrowIfHandleReadsRejected(nameof(Read), _path);
        var streaming = _readahead && !_direct;
        if (streaming)
            _fs.FlushDirtyPages(sync: false);   // 自管脏页排干——流式读经载体须见最新数据（页门拴并发安全）
        _fs.EnterReadEpoch();
        try
        {
            TierVolumeFs.DataSnapshot snap;
            lock (_fs.MetadataLock)
                snap = _fs.CaptureSnapshot(_entry);
            var n = _fs.ReadData(snap, offset, destination, _direct, streaming);
            // 自动顺序检测（内核 readahead 同款哲学）：连续读（offset == 上次读终点）触发预取——
            // 无需 Advise 即服务缓冲档顺序访问；Advise(Sequential) = 显式纯流式档（不走缓存）
            var sequential = offset == _lastReadEnd && n > 0;
            _lastReadEnd = offset + n;
            // 小粒度连续读 → 预取（大跨度读由读绕整段直读服务——预取页与其交替会互踩吞吐）
            if (sequential && !_direct && !streaming && destination.Length < _fs.PageSize * 16)
                _fs.PrefetchFollowing(ref _prefetchCursor, snap, offset + n, PrefetchWindowBlocks);
            return n;
        }
        finally
        {
            _fs.ExitReadEpoch();
        }
    }
    /// <inheritdoc/>
    public ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken ct)
        => new(Read(offset, destination.Span));

    // ═══════════════ 游标与追加 ═══════════════

    public long Position => Volatile.Read(ref _position);
    /// <inheritdoc/>
    public long Append(ReadOnlySpan<byte> source)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var gate = _fs.BeginHandleMutation(nameof(Append), _path);
        ThrowIfNotWritable(nameof(Append));
        if (source.IsEmpty) return Position;
        var reserved = _appendCursor is { } cursor
            ? Interlocked.Add(ref cursor.Value, source.Length) - source.Length
            : Interlocked.Add(ref _fallbackCursor, source.Length) - source.Length;
        try
        {
            Write(reserved, source);
            Volatile.Write(ref _position, reserved + source.Length);
            return reserved;
        }
        catch (FileIOException fioe)
        {
            if (fioe.ReservedOffset is not null) throw;
            throw new FileIOException(fioe.Error, $"{nameof(Append)} failed: {fioe.Message}", _path, nameof(Append), fioe)
            { ReservedOffset = reserved };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new FileIOException(IOError.IOFailure, $"{nameof(Append)} failed: {ex.Message}",
                _path, nameof(Append), ex) { ReservedOffset = reserved };
        }
    }
    /// <inheritdoc/>
    public ValueTask<long> AppendAsync(ReadOnlyMemory<byte> source, CancellationToken ct)
        => new(Append(source.Span));
    /// <inheritdoc/>
    public long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ThrowIfFsDisposed();
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        Volatile.Write(ref _position, target);
        return target;
    }

    // ═══════════════ 空间管理 ═══════════════
    /// <inheritdoc/>
    public void Preallocate()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var gate = _fs.BeginHandleMutation(nameof(Preallocate), _path);
        if (_preallocateSize <= 0) return;
        lock (_fs.MetadataLock)
            _fs.PreallocateEntry(_entry, _preallocateSize);
    }

    public long Length
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            ThrowIfFsDisposed();
            lock (_fs.MetadataLock)
                return _entry.LogicalLength;
        }
    }

    public long AllocatedSize
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            ThrowIfFsDisposed();
            lock (_fs.MetadataLock)
                return _fs.AllocatedSizeOf(_entry);
        }
    }

    public void SetLength(long length)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var gate = _fs.BeginHandleMutation(nameof(SetLength), _path);
        ThrowIfNotWritable(nameof(SetLength));
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        lock (_fs.MetadataLock)
            _fs.TruncateEntry(_entry, length);
    }

    public void PunchHole(long offset, long length)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var gate = _fs.BeginHandleMutation(nameof(PunchHole), _path);
        ThrowIfNotWritable(nameof(PunchHole));
        if (length <= 0) return;
        ThrowIfSpaceAligned(offset, length, nameof(PunchHole));
        lock (_fs.MetadataLock)
            _fs.PunchHoleEntry(_entry, offset, length);
    }

    public IReadOnlyCollection<(long Start, long End)> EnumerateAllocatedRanges()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _fs.ThrowIfHandleReadsRejected(nameof(EnumerateAllocatedRanges), _path);
        lock (_fs.MetadataLock)
            return _fs.AllocatedRangesOf(_entry);
    }

    /// <inheritdoc/>
    /// <remarks>TierVolume 区间三态的真实投射（§3.2）——unwritten 标注保真（采集管线 D4 依赖）。</remarks>
    public IReadOnlyCollection<(long Start, long End, bool Unwritten)> EnumerateAllocatedRangesDetailed()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _fs.ThrowIfHandleReadsRejected(nameof(EnumerateAllocatedRangesDetailed), _path);
        lock (_fs.MetadataLock)
            return _entry.Extents
                .Select(x => (x.LogicalStart, x.LogicalEnd, x.State == TierVolumeFs.ExtentState.Unwritten))
                .ToList();
    }

    public void CollapseRange(long offset, long length)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var gate = _fs.BeginHandleMutation(nameof(CollapseRange), _path);
        ThrowIfNotWritable(nameof(CollapseRange));
        if (length <= 0) return;
        ThrowIfSpaceAligned(offset, length, nameof(CollapseRange));
        lock (_fs.MetadataLock)
            _fs.CollapseEntry(_entry, offset, length);
    }

    public void InsertRange(long offset, long length)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var gate = _fs.BeginHandleMutation(nameof(InsertRange), _path);
        ThrowIfNotWritable(nameof(InsertRange));
        if (length <= 0) return;
        ThrowIfSpaceAligned(offset, length, nameof(InsertRange));
        lock (_fs.MetadataLock)
            _fs.InsertEntryRange(_entry, offset, length);
    }

    private void ThrowIfSpaceAligned(long offset, long length, string op)
    {
        var unit = _fs.Volume.AllocationUnit;
        if (unit <= 1) return;
        var mask = (ulong)(unit - 1);
        if (((ulong)offset & mask) != 0 || ((ulong)length & mask) != 0)
            throw new FileIOException(IOError.AlignmentError,
                $"{op} requires offset and length aligned to AllocationUnit {unit} (got offset={offset}, length={length}).",
                _path, op);
    }

    // ═══════════════ 文件间拷贝（介质内 memcpy——无平台依赖）═══════════════

    public long CopyRange(IFileHandle destination, long sourceOffset, long destinationOffset, long length)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _fs.ThrowIfHandleReadsRejected(nameof(CopyRange), _path);
        if (destination is not TierVolumeFileHandle dest || !ReferenceEquals(dest._fs, _fs))
            throw new ArgumentException($"CopyRange requires a TierVolume handle destination on the same volume.", nameof(destination));
        if (sourceOffset < 0 || destinationOffset < 0 || length < 0)
            throw new ArgumentOutOfRangeException(nameof(sourceOffset));

        var available = Math.Min(length, Math.Max(0, Length - sourceOffset));
        // RM-32 块级快道（判据不符内部回退 -1）：单锁 extent 对齐搬运——compact/migrate 形态
        if (available > 0)
        {
            var fast = _fs.TryCopyRangeBlockLevel(_entry, dest._entry, sourceOffset, destinationOffset, available);
            if (fast >= 0) return fast;
        }
        var buf = new byte[1 << 16];
        long done = 0;
        while (done < available)
        {
            var take = (int)Math.Min(buf.Length, available - done);
            var got = Read(sourceOffset + done, buf.AsSpan(0, take));
            if (got <= 0) break;
            dest.Write(destinationOffset + done, buf.AsSpan(0, got));
            done += got;
        }
        return done;
    }

    public long CloneRange(IFileHandle destination) => CopyRange(destination, 0, 0, Length);

    // ═══════════════ 向量 IO（逐片回退——语义等价）═══════════════

    public void WriteVector(long offset, ReadOnlySpan<ReadOnlyMemory<byte>> sources)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var gate = _fs.BeginHandleMutation(nameof(WriteVector), _path);
        long pos = offset;
        foreach (var s in sources)
        {
            if (!s.IsEmpty) Write(pos, s.Span);
            pos += s.Length;
        }
    }

    public ValueTask WriteVectorAsync(long offset, ReadOnlyMemory<ReadOnlyMemory<byte>> sources, CancellationToken ct)
    {
        WriteVector(offset, sources.Span);
        return ValueTask.CompletedTask;
    }

    public int ReadVector(long offset, ReadOnlySpan<Memory<byte>> destinations)
    {
        int got = 0;
        long pos = offset;
        foreach (var d in destinations)
        {
            if (d.IsEmpty) continue;
            var n = Read(pos, d.Span);
            got += n;
            pos += n;
            if (n < d.Length) break;
        }
        return got;
    }

    public ValueTask<int> ReadVectorAsync(long offset, Memory<Memory<byte>> destinations, CancellationToken ct)
        => new(ReadVector(offset, destinations.Span));

    // ═══════════════ 持久化 ═══════════════

    /// <summary>提交（raw-journal §4 + W2 组提交）：日志模式 = 记录屏障（两段式——fsync 期间数据面不停摆）；
    /// 检查点模式 = 结构脏 → 检查点，结构净 → 数据-only 快道（fdatasync 形态）。</summary>
    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ThrowIfFsDisposed();
        if (_fs._journalOn)
        {
            _fs.JournalCommit(holdLock: false);   // W2：屏障期释放元数据锁
            return;
        }
        lock (_fs.MetadataLock)
        {
            if (_fs.MetadataDirty) _fs.CommitMetadata();
            else _fs.FlushDirtyPages();
        }
    }

    public void FlushData()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ThrowIfFsDisposed();
        lock (_fs.MetadataLock)
            _fs.FlushDirtyPages();
    }

    public void Advise(FileAdvise advise)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ThrowIfFsDisposed();
        _readahead = advise == FileAdvise.Sequential;   // 页缓存预取开关（能力位 Advise 置位——真行为）
    }

    private bool _readahead;
    private ulong _prefetchCursor;   // 预取前沿（物理块号——per-handle 去重游标，尽力而为）
    private long _lastReadEnd = -1;  // 自动顺序检测：上次读的逻辑终点（连续读 = 预取触发）
    private const int PrefetchWindowBlocks = 32;

    // ═══════════════ 字节范围锁（进程内）═══════════════

    public void Lock(long offset, long length, FileLockMode mode)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ThrowIfFsDisposed();
        var table = SRangeLocks.GetOrAdd(_fs.VolumeUuid.ToString(), _ => new RangeLockTable());
        lock (table)
        {
            if (!table.TryAcquire(offset, length, mode, this))
                throw new FileIOException(IOError.SharingViolation,
                    $"范围锁冲突：[{offset}, {offset + length}) mode={mode}", _path, "Lock");
        }
    }

    public bool TryLock(long offset, long length, FileLockMode mode)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ThrowIfFsDisposed();
        var table = SRangeLocks.GetOrAdd(_fs.VolumeUuid.ToString(), _ => new RangeLockTable());
        lock (table)
            return table.TryAcquire(offset, length, mode, this);
    }

    public void Unlock(long offset, long length)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ThrowIfFsDisposed();
        if (SRangeLocks.TryGetValue(_fs.VolumeUuid.ToString(), out var table))
            lock (table)
                table.Release(offset, length, this);
    }

    // ═══════════════ 内存映射（单连续区间——文件载体 BCL MMF 直映射物理区间）═══════════════

    public IMappedSection Map(long offset, long length, AccessMode access)
    {
        AccessGate.CheckMapOpen(_fs.Access, access, Path);
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _fs.ThrowIfHandleReadsRejected(nameof(Map), _path);
        if (offset < 0 || length <= 0) throw new ArgumentOutOfRangeException(nameof(length));
        if (length > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(length), "映射长度超过 2GB 上限。");
        if (offset + length > Length)
            throw new ArgumentException($"映射区间 [{offset}, {offset + length}) 超出文件长度 {Length}。");

        // 单连续 Written 区间才可直映射；碎片/跨成员（RM-08/D8）→ 物化整理（重写为单成员单连续区间后映射）
        lock (_fs.MetadataLock)
        {
            var covering = _entry.Extents is [{ State: TierVolumeFs.ExtentState.Written, LogicalStart: 0 } x]
                           && x.LogicalEnd >= _entry.LogicalLength
                ? _entry.Extents[0]
                : default(TierVolumeFs.Extent?);
            // D8：单连续但跨成员同样不可 MMF——整理须落单成员（成员内分配由 DefragmentEntry 保证）
            var needsDefrag = covering is not { } ext || !_fs.ExtentWithinSingleMember(ext);
            if (needsDefrag)
            {
                if (!_writable)
                    throw new FileIOException(IOError.Unsupported,
                        "碎片/跨成员文件映射须先物化整理（写操作）——只读句柄不触发（RM-08 诚实语义）", _path, nameof(Map));
                _fs.DefragmentEntry(_entry);   // RM-08/D8：物化（洞归零重写——语义等价，AllocatedSize 增长为代价）
                covering = _entry.Extents[0];
            }
            var target = covering!.Value;
            if (_fs.IsDeviceCarrier)
                throw new FileIOException(IOError.Unsupported,
                    "设备载体不支持 Map（Mmap 能力位设备形态不置位——诚实，§3.5）", _path, nameof(Map));
            return _fs.CreateBackingMap(target, offset, length, access, _path);
        }
    }

    // ═══════════════ FileExtra（条目内联——随元数据提交原子）═══════════════

    public ReadOnlyMemory<byte> FileExtra
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            _fs.ThrowIfHandleReadsRejected(nameof(FileExtra), _path);
            lock (_fs.MetadataLock)
                return _entry.Extra;
        }
    }

    public int ReadFileExtra(long offset, Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _fs.ThrowIfHandleReadsRejected(nameof(ReadFileExtra), _path);
        if (destination.IsEmpty || offset < 0) return 0;
        lock (_fs.MetadataLock)
        {
            if (offset >= _entry.Extra.Length) return 0;   // pread EOF 契约
            var n = (int)Math.Min(destination.Length, _entry.Extra.Length - offset);
            _entry.Extra.AsSpan((int)offset, n).CopyTo(destination);
            return n;
        }
    }

    public void WriteFileExtra(long offset, ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var gate = _fs.BeginHandleMutation(nameof(WriteFileExtra), _path);
        ThrowIfNotWritable(nameof(WriteFileExtra));
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset + data.Length > IFileSystem.MaxFileExtraBytes)
            throw new ArgumentException($"FileExtra 超限（{offset + data.Length} > {IFileSystem.MaxFileExtraBytes}）。");
        lock (_fs.MetadataLock)
        {
            var cur = _entry.Extra;
            var newLen = (int)Math.Max(cur.Length, offset + data.Length);
            var blob = new byte[newLen];
            cur.AsSpan().CopyTo(blob);
            data.CopyTo(blob.AsSpan((int)offset));
            _entry.Extra = blob;
            _fs.MetadataDirty = true;   // Extra 入镜像 = 结构变更（lazytime 分流后自证）
            _fs.JnlSetExtra(_path, blob);
            _fs.TouchModified(_entry);
        }
    }

    public void SetFileExtra(ReadOnlyMemory<byte> extra)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var gate = _fs.BeginHandleMutation(nameof(SetFileExtra), _path);
        ThrowIfNotWritable(nameof(SetFileExtra));
        if (extra.Length > IFileSystem.MaxFileExtraBytes)
            throw new ArgumentException($"FileExtra 超限（{extra.Length} > {IFileSystem.MaxFileExtraBytes}）。", nameof(extra));
        lock (_fs.MetadataLock)
        {
            _entry.Extra = extra.ToArray();
            _fs.MetadataDirty = true;   // Extra 入镜像 = 结构变更（lazytime 分流后自证）
            _fs.JnlSetExtra(_path, _entry.Extra);
            _fs.TouchModified(_entry);
        }
    }

    // ═══════════════ 释放（池挂载分叉 + 在途兜底）═══════════════

    public void Dispose()
    {
        if (_poolAttachment is { } attachment)
        {
            attachment.Pool!.OnUsageReleased(this, attachment);
            return;
        }
        CloseUnderlying();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    HandlePoolAttachment? IPoolAttachable.PoolAttachment => _poolAttachment;

    HandlePoolAttachment IPoolAttachable.AttachPool(FileHandlePool pool)
    {
        var attachment = _poolAttachment ??= new HandlePoolAttachment();
        attachment.Pool = pool;
        return attachment;
    }

    void IPoolAttachable.CloseUnderlying() => CloseUnderlying();

    internal void CloseUnderlying()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _sharingRegistry?.Unregister(_path, _sharingEntry!);
        if (SRangeLocks.TryGetValue(_fs.VolumeUuid.ToString(), out var table))
            lock (table)
                table.ReleaseAll(this);
    }

    internal void AttachSharing(SharingRegistry registry, SharingRegistry.Entry entry)
    {
        _sharingRegistry = registry;
        _sharingEntry = entry;
    }

    private void ThrowIfNotWritable(string op)
    {
        if (!_writable)
            throw new InvalidOperationException($"只读句柄不接受 {op}。");
    }

    /// <summary>fs 生命周期双门（D2）：卷实例已 Dispose → 句柄操作统一抛 ObjectDisposedException
    /// （与 Mem"拔盘"契约对齐——静默内存成功 = 永不持久化的假象）。</summary>
    private void ThrowIfFsDisposed() => ObjectDisposedException.ThrowIf(_fs.IsDisposed, _fs);

    /// <summary>进程内范围锁表（同 Mem 语义：同 owner 重叠允许、他 owner 排他冲突）。</summary>
    private sealed class RangeLockTable
    {
        private readonly List<Entry4> _entries = [];

        private readonly record struct Entry4(long Start, long Length, FileLockMode Mode, object Owner);

        public bool TryAcquire(long offset, long length, FileLockMode mode, object owner)
        {
            foreach (var e in _entries)
            {
                if (e.Start < offset + length && offset < e.Start + e.Length
                    && !ReferenceEquals(e.Owner, owner)
                    && (e.Mode == FileLockMode.Exclusive || mode == FileLockMode.Exclusive))
                    return false;
            }
            _entries.Add(new Entry4(offset, length, mode, owner));
            return true;
        }

        public void Release(long offset, long length, object owner)
            => _entries.RemoveAll(e => ReferenceEquals(e.Owner, owner) && e.Start == offset && e.Length == length);

        public void ReleaseAll(object owner)
            => _entries.RemoveAll(e => ReferenceEquals(e.Owner, owner));
    }
}
