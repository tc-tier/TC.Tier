using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using TC.Tier.Core.IO.Shared;

namespace TC.Tier.Core.IO.Disk;

/// <summary>
/// 磁盘文件句柄——<see cref="SafeFileHandle"/> + <see cref="RandomAccess"/> 的 <see cref="IFileHandle"/> 实现。
/// <para>★ 打开语义显式化：Access×Mode×Sharing×Hints 四轴经 BCL 三要素无损映射（FileOpenOptions→FileMode/FileAccess/FileShare）。</para>
/// <para>★ DIO 语义链：请求（Hints.NoBuffering）→ 逐句柄探测（ProbeUnbuffered）→ 结果报告（<see cref="UnbufferedSupport"/>）；
///   <see cref="RequiredAlignment"/> = Win: max(扇区, 内存页) / Linux: 逻辑块 / 缓冲: 1——三重对齐单一事实源。</para>
/// <para>★ 句柄游标（D7）：Write/Read 不读不推进；Append 原子预留；Mode.Append 打开时游标置于 EOF。</para>
/// <para>★ 所有 syscall 失败统一 <see cref="IOExceptionMapper.Wrap"/> → <see cref="FileIOException"/>（家族 A）。</para>
/// </summary>
internal sealed class DiskFileHandle : IFileHandle, IPoolAttachable
{
    private readonly DiskFileSystem _fs;
    private readonly FileOpenOptions _options;
    private readonly SafeFileHandle _handle;
    private readonly string _path;
    private readonly UnbufferedIoSupport _unbufferedSupport;
    private readonly long _requiredAlignment;
    private readonly long _preallocateSize;
    private readonly ILogger? _logger;

    private long _position;        // 句柄书签（D7：会话状态——Position/Seek；追加预留另见 _appendCursor）
    private AppendCursor? _appendCursor;   // 文件级追加预留（fs.Open 注入——同 fs 同路径全部实例共享）
    private long _fallbackCursor;           // 未注入时的回退计数（直连构造场景——正常路径不达）
    private int _preallocated;     // 幂等标志：0=未预分配，1=已预分配
    private int _inFlightOps;      // R4：在途异步计数（Dispose 兜底等待基准）
    private int _disposed;
    private HandlePoolAttachment? _poolAttachment;   // 池挂载（null = 未挂载——Dispose 走真关闭）
    private SafeFileHandle? _lockHandle;      // Windows 专用非 OVERLAPPED 锁句柄（惰性）
    private readonly object _lockHandleGate = new();
    private SharingRegistry? _sharingRegistry;          // 进程内共享登记表（fs.Open 注入）
    private SharingRegistry.Entry? _sharingEntry;

    internal DiskFileHandle(DiskFileSystem fs, string path, FileOpenOptions options, ILogger? logger)
    {
        options.Validate();
        _fs = fs;
        _options = options;
        _path = path;
        _logger = logger;
        _preallocateSize = options.PreallocateSize;

        var access = options.Access switch
        {
            AccessMode.Read => FileAccess.Read,
            AccessMode.Write => FileAccess.Write,
            _ => FileAccess.ReadWrite,
        };
        var mode = options.Mode switch
        {
            FileOpenMode.OpenExisting => FileMode.Open,
            FileOpenMode.OpenOrCreate => FileMode.OpenOrCreate,
            FileOpenMode.CreateNew => FileMode.CreateNew,
            FileOpenMode.Truncate => FileMode.Truncate,
            _ => FileMode.OpenOrCreate,   // Append：仅游标初始化于 EOF（打开层面=OpenOrCreate）
        };
        var fileOptions = FileOptions.Asynchronous
                          | (options.Hints.HasFlag(FileOpenHints.WriteThrough) ? FileOptions.WriteThrough : 0)
                          | (options.Hints.HasFlag(FileOpenHints.SequentialScan) ? FileOptions.SequentialScan : 0)
                          | (options.Hints.HasFlag(FileOpenHints.RandomAccess) ? FileOptions.RandomAccess : 0);
        var share = (FileShare)options.Sharing;
        var directIo = options.Hints.HasFlag(FileOpenHints.NoBuffering);

        var fullPath = fs.GetFullPath(path);
        try
        {
            try
            {
                _handle = FileNative.OpenHandle(fullPath, mode, access, fileOptions, share, directIo, directIo, logger);
            }
            catch (IOException) when (mode == FileMode.OpenOrCreate && File.Exists(fullPath))
            {
                // 并发建文件抢先保护：OpenOrCreate 失败但文件已存在 → Open 回退
                _handle = FileNative.OpenHandle(fullPath, FileMode.Open, access, fileOptions, share, directIo, directIo, logger);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw ex.Wrap("Open", path);
        }

        // ★ DIO 探测 + 对齐基准（open 时一次，缓存只读）：
        //   Win= max(扇区, 系统页)——buffer 地址须页对齐，扇区仅约束 offset；
        //   Linux= 逻辑块（≈扇区）；BestEffort/Ignored/NotRequested → 1（对齐非强制）。
        _unbufferedSupport = directIo ? FileNative.ProbeUnbufferedIo(_handle, fullPath, directIo, directIo, logger) : UnbufferedIoSupport.NotRequested;

        var sector = (long)fs.Volume.SectorSize;
        if (_unbufferedSupport == UnbufferedIoSupport.Supported)
        {
            _requiredAlignment = DirectIo.BufferAlignmentFloor((int)sector);   // ★ 单真相（页池/帧池租用同式）
        }
        else
        {
            _requiredAlignment = 1;
        }

        // 游标初始化：Append ⇒ EOF；其余 ⇒ 0
        _position = options.Mode == FileOpenMode.Append ? RandomAccess.GetLength(_handle) : 0;

        if (_preallocateSize > 0)
            Preallocate();
    }

    // ═══════════════════════════════════════════════════════════════
    //  身份与 IO 模式
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public string Path => _path;

    /// <inheritdoc/>
    public UnbufferedIoSupport UnbufferedSupport => _unbufferedSupport;

    /// <inheritdoc/>
    public long RequiredAlignment => _requiredAlignment;

    /// <summary>注入文件级追加预留盒（fs.Open 调用——同 fs 同路径的全部实例共享同盒）。</summary>
    internal void AttachAppendCursor(AppendCursor cursor) => _appendCursor = cursor;

    // ═══════════════════════════════════════════════════════════════
    //  位置读写（pwrite/pread 铁律——不读不推进游标）
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public void Write(long offset, ReadOnlySpan<byte> source)
    {
        // CORE-09：Length（fstat syscall）只在配额启用时求值——默认关闭零成本（原无条件每写一次 fstat）
        if (_fs.QuotaEnabled)
            _fs.QuotaProject(_path, Math.Max(Length, offset + source.Length));   // G3 写前拒（惰性基线+投影）
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var gate = _fs.Maintenance.BeginMutation(nameof(Write), _path);
        if (source.IsEmpty) return;
        ThrowIfMisaligned(offset, source.Length, ref MemoryMarshal.GetReference(source));
        try { RandomAccess.Write(_handle, source, offset); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw ex.Wrap(nameof(Write), _path); }
    }

    /// <inheritdoc/>
    public async ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> source, CancellationToken ct)
    {
        // CORE-10：异步路径补配额执法（原完全绕过——同步/异步行为不一致）；配额关闭零成本
        if (_fs.QuotaEnabled)
            _fs.QuotaProject(_path, Math.Max(Length, offset + source.Length));
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var gate = _fs.Maintenance.BeginMutation(nameof(WriteAsync), _path);
        if (source.IsEmpty) return;
        ThrowIfMisaligned(offset, source.Length, ref MemoryMarshal.GetReference(source.Span));
        Interlocked.Increment(ref _inFlightOps);
        try
        {
            await RandomAccess.WriteAsync(_handle, source, offset, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw ex.Wrap(nameof(WriteAsync), _path); }
        finally
        { Interlocked.Decrement(ref _inFlightOps); }
    }

    /// <inheritdoc/>
    public int Read(long offset, Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _fs.Maintenance.ThrowIfReadsRejected(nameof(Read), _path);
        if (destination.IsEmpty) return 0;
        ThrowIfMisaligned(offset, destination.Length, ref MemoryMarshal.GetReference(destination));
        try { return RandomAccess.Read(_handle, destination, offset); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw ex.Wrap(nameof(Read), _path); }
    }

    /// <inheritdoc/>
    public async ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _fs.Maintenance.ThrowIfReadsRejected(nameof(ReadAsync), _path);
        if (destination.IsEmpty) return 0;
        ThrowIfMisaligned(offset, destination.Length, ref MemoryMarshal.GetReference(destination.Span));
        Interlocked.Increment(ref _inFlightOps);
        try
        {
            return await RandomAccess.ReadAsync(_handle, destination, offset, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw ex.Wrap(nameof(ReadAsync), _path); }
        finally
        { Interlocked.Decrement(ref _inFlightOps); }
    }

    // ═══════════════════════════════════════════════════════════════
    //  句柄游标（D7）
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public long Position => Volatile.Read(ref _position);

    /// <inheritdoc/>
    /// <remarks>★ 预留点为<b>文件级</b>（fs per-path 计数盒——同 fs 实例内跨句柄原子推进，多写者无需同实例）；
    ///   返回落点同时推进本句柄书签。失败语义（不回滚/洞读零/ReservedOffset）不变。</remarks>
    public long Append(ReadOnlySpan<byte> source)
    {
        // G3 写前拒在 Write 路径统一生效（Append 预留经 cursor 落点写入——投影由 Write 挂钩覆盖）
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var gate = _fs.Maintenance.BeginMutation(nameof(Append), _path);
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
            var w = ex.Wrap(nameof(Append), _path);
            throw new FileIOException(w.Error, w.Message, _path, nameof(Append), ex) { ReservedOffset = reserved };
        }
    }

    /// <inheritdoc/>
    public async ValueTask<long> AppendAsync(ReadOnlyMemory<byte> source, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var gate = _fs.Maintenance.BeginMutation(nameof(AppendAsync), _path);
        if (source.IsEmpty) return Position;
        var reserved = _appendCursor is { } cursor
            ? Interlocked.Add(ref cursor.Value, source.Length) - source.Length
            : Interlocked.Add(ref _fallbackCursor, source.Length) - source.Length;
        try
        {
            await WriteAsync(reserved, source, ct).ConfigureAwait(false);
            Volatile.Write(ref _position, reserved + source.Length);
            return reserved;
        }
        catch (FileIOException fioe)
        {
            if (fioe.ReservedOffset is not null) throw;
            throw new FileIOException(fioe.Error, $"{nameof(AppendAsync)} failed: {fioe.Message}", _path, nameof(AppendAsync), fioe)
            { ReservedOffset = reserved };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var w = ex.Wrap(nameof(AppendAsync), _path);
            throw new FileIOException(w.Error, w.Message, _path, nameof(AppendAsync), ex) { ReservedOffset = reserved };
        }
    }

    /// <inheritdoc/>
    public long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
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

    // ═══════════════════════════════════════════════════════════════
    //  空间管理
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public void Preallocate()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var gate = _fs.Maintenance.BeginMutation(nameof(Preallocate), _path);
        if (Interlocked.Exchange(ref _preallocated, 1) != 0) return;
        if (_preallocateSize <= 0) return;
        try
        {
            // 幂等：文件已存在且有大小（恢复场景）跳过——避免截断已有数据
            if (RandomAccess.GetLength(_handle) > 0) return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw ex.Wrap(nameof(Preallocate), _path); }

        try { FileNative.PreallocateFile(_handle, _preallocateSize, _logger); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 预分配失败降级稀疏（best-effort，与旧实现一致）
            _logger?.LogWarning(ex, "Preallocate path={Path} size={Size} failed, will grow on demand", _path, _preallocateSize);
        }
    }

    /// <inheritdoc/>
    public long Length
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            try { return RandomAccess.GetLength(_handle); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { throw ex.Wrap(nameof(Length), _path); }
        }
    }

    /// <inheritdoc/>
    public long AllocatedSize
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            try { return FileNative.GetFileAllocatedDiskSize(_handle, _logger); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { throw ex.Wrap(nameof(AllocatedSize), _path); }
        }
    }

    /// <inheritdoc/>
    public void SetLength(long length)
    {
        // CORE-09：Length 只在配额启用时求值（原无条件 fstat）
        if (_fs.QuotaEnabled)
            _fs.QuotaProject(_path, Math.Max(Length, length));   // G3 写前拒
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var gate = _fs.Maintenance.BeginMutation(nameof(SetLength), _path);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        try { RandomAccess.SetLength(_handle, length); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw ex.Wrap(nameof(SetLength), _path); }
        _fs.OnFileLengthChanged(_path, length);   // 文件级追加预留权威复位（追加从新末端继续）
    }

    /// <inheritdoc/>
    public void PunchHole(long offset, long length)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var gate = _fs.Maintenance.BeginMutation(nameof(PunchHole), _path);
        if (length <= 0) return;
        // ★ 契约（引擎消费语义 A4——字节粒度归零）：接受任意区间。整块内部物理打洞（空间归还）；
        //   非对齐边缘写零（可观测等价读零；边缘块保持已分配——簇粒度是磁盘物理地板，与
        //   FSCTL_QUERY_ALLOCATED_RANGES 报告粒度一致）。须写权限句柄（FSCTL/写零同需）。
        try
        {
            var unit = _fs.Volume.AllocationUnit;
            var end = offset + length;
            var alignedStart = (offset + unit - 1) / unit * unit;
            var alignedEnd = end / unit * unit;

            var headEnd = Math.Min(alignedStart, end);
            if (headEnd > offset) WriteZeroes(offset, headEnd - offset);
            if (alignedEnd > alignedStart)
                FileNative.PunchHole(_handle, alignedStart, alignedEnd - alignedStart, _logger);
            var tailStart = Math.Max(alignedStart, alignedEnd);
            if (end > tailStart) WriteZeroes(tailStart, end - tailStart);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw ex.Wrap(nameof(PunchHole), _path); }
    }

    /// <summary>边缘零化（长度 &lt; AllocationUnit）——写零使可观测语义与打洞等价（读零）。</summary>
    private void WriteZeroes(long offset, long length)
    {
        var buf = ArrayPool<byte>.Shared.Rent((int)length);
        try
        {
            Array.Clear(buf, 0, (int)length);
            RandomAccess.Write(_handle, buf.AsSpan(0, (int)length), offset);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<(long Start, long End)> EnumerateAllocatedRanges()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        try { return FileNative.EnumerateAllocatedRanges(_handle, RandomAccess.GetLength(_handle), _logger); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw ex.Wrap(nameof(EnumerateAllocatedRanges), _path); }
    }

    /// <inheritdoc/>
    /// <remarks>Linux：fallocate(FALLOC_FL_COLLAPSE_RANGE/INSERT_RANGE)；Win/macOS：能力位未置位 → <see cref="IOError.Unsupported"/>。</remarks>
    public void CollapseRange(long offset, long length)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var gate = _fs.Maintenance.BeginMutation(nameof(CollapseRange), _path);
        if (length <= 0) return;
        if (!OperatingSystem.IsLinux())
            throw new FileIOException(IOError.Unsupported,
                $"{nameof(CollapseRange)} is not supported on this platform (capability RangeShift).", _path, nameof(CollapseRange));
        ThrowIfSpaceAligned(offset, length, nameof(CollapseRange));

        WithFd(fd =>
        {
            const int collapseRange = 0x08;   // FALLOC_FL_COLLAPSE_RANGE
            if (LibC.Fallocate(fd, collapseRange, offset, length) != 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                if (errno is 22 or 1) // EINVAL / EPERM：该文件系统不支持
                    throw new FileIOException(IOError.Unsupported,
                        $"{nameof(CollapseRange)} not supported by this file system (errno={errno}).", _path, nameof(CollapseRange));
                throw new FileIOException(IOExceptionMapper.ClassifyHResult(errno),
                    $"{nameof(CollapseRange)} failed, errno={errno}.", _path, nameof(CollapseRange));
            }
        });
    }

    /// <inheritdoc/>
    public void InsertRange(long offset, long length)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var gate = _fs.Maintenance.BeginMutation(nameof(InsertRange), _path);
        if (length <= 0) return;
        if (!OperatingSystem.IsLinux())
            throw new FileIOException(IOError.Unsupported,
                $"{nameof(InsertRange)} is not supported on this platform (capability RangeShift).", _path, nameof(InsertRange));
        ThrowIfSpaceAligned(offset, length, nameof(InsertRange));

        WithFd(fd =>
        {
            const int insertRange = 0x10;     // FALLOC_FL_INSERT_RANGE
            if (LibC.Fallocate(fd, insertRange, offset, length) != 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                if (errno is 22 or 1)
                    throw new FileIOException(IOError.Unsupported,
                        $"{nameof(InsertRange)} not supported by this file system (errno={errno}).", _path, nameof(InsertRange));
                throw new FileIOException(IOExceptionMapper.ClassifyHResult(errno),
                    $"{nameof(InsertRange)} failed, errno={errno}.", _path, nameof(InsertRange));
            }
        });
    }

    // ═══════════════════════════════════════════════════════════════
    //  文件间拷贝
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public long CopyRange(IFileHandle destination, long sourceOffset, long destinationOffset, long length)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _fs.Maintenance.ThrowIfReadsRejected(nameof(CopyRange), _path);
        if (destination is not DiskFileHandle dest)
            throw new ArgumentException($"CopyRange requires a {nameof(DiskFileHandle)} destination on the same medium.", nameof(destination));
        if (sourceOffset < 0 || destinationOffset < 0 || length < 0)
            throw new ArgumentOutOfRangeException(nameof(sourceOffset));

        var available = Math.Min(length, Math.Max(0, Length - sourceOffset));
        long done = 0;
        try
        {
            if (OperatingSystem.IsLinux() && length > 0)
                done = TryCopyFileRange(dest, sourceOffset, destinationOffset, available);
            if (done < available)
                done = CopyRangeUserLoop(dest, sourceOffset + done, destinationOffset + done, available - done, done);
            return done;
        }
        catch (FileIOException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var w = ex.Wrap(nameof(CopyRange), _path);
            throw new FileIOException(w.Error, w.Message, _path, nameof(CopyRange), ex) { CompletedLength = done };
        }
    }

    /// <summary>Linux copy_file_range——返回本次完成字节数（不含 fallback 已完成量）；不支持的错误码返回 -1 触发回退。</summary>
    private unsafe long TryCopyFileRange(DiskFileHandle dest, long sourceOffset, long destinationOffset, long length)
    {
        long done = 0;
        bool borrowedIn = false, borrowedOut = false;
        try
        {
            _handle.DangerousAddRef(ref borrowedIn);
            dest._handle.DangerousAddRef(ref borrowedOut);
            var fdIn = _handle.DangerousGetHandle().ToInt32();
            var fdOut = dest._handle.DangerousGetHandle().ToInt32();
            var offIn = sourceOffset;
            var offOut = destinationOffset;
            const long chunk = 1L << 30;
            while (done < length)
            {
                var n = LibC.CopyFileRange(fdIn, &offIn, fdOut, &offOut, (nuint)Math.Min(chunk, length - done), 0);
                if (n > 0) { done += n; continue; }
                if (n == 0) break;                                   // EOF
                var errno = Marshal.GetLastPInvokeError();
                if (errno is 18 or 22 or 95) return done;             // EXDEV/EINVAL/EOPNOTSUPP → 用户态回退
                throw new IOException($"copy_file_range failed, errno={errno}.");
            }
            return done;
        }
        finally
        {
            if (borrowedOut) dest._handle.DangerousRelease();
            if (borrowedIn) _handle.DangerousRelease();
        }
    }

    /// <summary>用户态拷贝回退（全平台共享）——目标 DIO 句柄用对齐缓冲满足其三重对齐。</summary>
    private long CopyRangeUserLoop(DiskFileHandle dest, long sourceOffset, long destinationOffset, long length, long alreadyDone)
    {
        if (length <= 0) return alreadyDone;
        var totalDone = alreadyDone;
        var alignment = (int)Math.Max(1, Math.Max(_requiredAlignment, dest._requiredAlignment));
        var bufferSize = Math.Min(1 << 16, Math.Max(alignment, 512) * 64);
        var buffer = _fs.RentIoBuffer((nuint)bufferSize, (nuint)alignment);
        try
        {
            var span = buffer.Span;
            while (totalDone - alreadyDone < length)
            {
                var want = (int)Math.Min(span.Length, length - (totalDone - alreadyDone));
                var read = Read(sourceOffset + (totalDone - alreadyDone), span[..want]);
                if (read <= 0) break;
                dest.Write(destinationOffset + (totalDone - alreadyDone), span[..read]);
                totalDone += read;
            }
        }
        finally
        {
            _fs.ReturnIoBuffer(buffer);
        }
        return totalDone;
    }

    /// <inheritdoc/>
    public long CloneRange(IFileHandle destination)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _fs.Maintenance.ThrowIfReadsRejected(nameof(CloneRange), _path);
        if (destination is not DiskFileHandle dest)
            throw new ArgumentException($"CloneRange requires a {nameof(DiskFileHandle)} destination on the same medium.", nameof(destination));

        if (OperatingSystem.IsMacOS())
        {
            var ok = WithFdResult(srcFd =>
            {
                var borrowedDest = false;
                try
                {
                    dest._handle.DangerousAddRef(ref borrowedDest);
                    var destFd = dest._handle.DangerousGetHandle().ToInt32();
                    return LibC.FcntlIntPtr(destFd, LibC.Ficlone, srcFd) == 0;
                }
                finally
                {
                    if (borrowedDest) dest._handle.DangerousRelease();
                }
            });
            if (ok) return Length;
        }
        return CopyRange(destination, 0, 0, Length);
    }

    // ═══════════════════════════════════════════════════════════════
    //  向量化 IO
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public unsafe void WriteVector(long offset, ReadOnlySpan<ReadOnlyMemory<byte>> sources)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var gate = _fs.Maintenance.BeginMutation(nameof(WriteVector), _path);
        if (sources.IsEmpty) return;
        long total = 0;
        foreach (var s in sources) total += s.Length;
        if (total == 0) return;
        ThrowIfVectorMisaligned(offset, total, sources);

        if (OperatingSystem.IsLinux())
        {
            var handles = new MemoryHandle[sources.Length];
            var iov = stackalloc LibC.IoVec[sources.Length];
            var borrowed = false;
            try
            {
                _handle.DangerousAddRef(ref borrowed);
                var fd = _handle.DangerousGetHandle().ToInt32();
                for (var i = 0; i < sources.Length; i++)
                {
                    handles[i] = sources[i].Pin();
                    iov[i] = new LibC.IoVec { Base = handles[i].Pointer, Len = (nuint)sources[i].Length };
                }
                long written = 0;
                while (written < total)
                {
                    // 从 written 推导起始片与片内偏移（每次从不可变的 handles 重算，防增量腐败）
                    var startIdx = 0;
                    var inSeg = written;
                    while (startIdx < sources.Length && inSeg >= sources[startIdx].Length)
                    {
                        inSeg -= sources[startIdx].Length;
                        startIdx++;
                    }
                    var vec = iov + startIdx;
                    vec[0].Base = (byte*)handles[startIdx].Pointer + inSeg;
                    vec[0].Len = (nuint)(sources[startIdx].Length - inSeg);
                    var n = LibC.Pwritev(fd, vec, sources.Length - startIdx, offset + written);
                    if (n < 0)
                    {
                        var errno = Marshal.GetLastPInvokeError();
                        if (errno == 22) goto Fallback;   // EINVAL → 用户态回退
                        throw new IOException($"pwritev failed, errno={errno}.");
                    }
                    written += n;
                }
                return;
                Fallback:
                WriteVectorUserLoop(offset, sources);
            }
            finally
            {
                if (borrowed) _handle.DangerousRelease();
                for (var i = 0; i < handles.Length; i++) handles[i].Dispose();
            }
        }
        WriteVectorUserLoop(offset, sources);
    }

    private void WriteVectorUserLoop(long offset, ReadOnlySpan<ReadOnlyMemory<byte>> sources)
    {
        long pos = offset;
        foreach (var s in sources)
        {
            if (!s.IsEmpty) Write(pos, s.Span);
            pos += s.Length;
        }
    }

    /// <inheritdoc/>
    public ValueTask WriteVectorAsync(long offset, ReadOnlyMemory<ReadOnlyMemory<byte>> sources, CancellationToken ct)
    {
        WriteVector(offset, sources.Span);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public unsafe int ReadVector(long offset, ReadOnlySpan<Memory<byte>> destinations)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _fs.Maintenance.ThrowIfReadsRejected(nameof(ReadVector), _path);
        if (destinations.IsEmpty) return 0;
        long total = 0;
        foreach (var d in destinations) total += d.Length;
        if (total == 0) return 0;

        if (OperatingSystem.IsLinux() && _unbufferedSupport != UnbufferedIoSupport.Supported)
        {
            var handles = new MemoryHandle[destinations.Length];
            var iov = stackalloc LibC.IoVec[destinations.Length];
            var borrowed = false;
            try
            {
                _handle.DangerousAddRef(ref borrowed);
                var fd = _handle.DangerousGetHandle().ToInt32();
                for (var i = 0; i < destinations.Length; i++)
                {
                    handles[i] = destinations[i].Pin();
                    iov[i] = new LibC.IoVec { Base = handles[i].Pointer, Len = (nuint)destinations[i].Length };
                }
                long read = 0;
                while (read < total)
                {
                    var startIdx = 0;
                    var inSeg = read;
                    while (startIdx < destinations.Length && inSeg >= destinations[startIdx].Length)
                    {
                        inSeg -= destinations[startIdx].Length;
                        startIdx++;
                    }
                    if (startIdx >= destinations.Length) break;
                    var vec = iov + startIdx;
                    vec[0].Base = (byte*)handles[startIdx].Pointer + inSeg;
                    vec[0].Len = (nuint)(destinations[startIdx].Length - inSeg);
                    var n = LibC.Preadv(fd, vec, destinations.Length - startIdx, offset + read);
                    if (n < 0)
                    {
                        var errno = Marshal.GetLastPInvokeError();
                        if (errno == 22) return ReadVectorUserLoop(offset, destinations);
                        throw new IOException($"preadv failed, errno={errno}.");
                    }
                    if (n == 0) break;   // EOF
                    read += n;
                }
                return (int)read;
            }
            finally
            {
                if (borrowed) _handle.DangerousRelease();
                for (var i = 0; i < handles.Length; i++) handles[i].Dispose();
            }
        }
        return ReadVectorUserLoop(offset, destinations);
    }

    private int ReadVectorUserLoop(long offset, ReadOnlySpan<Memory<byte>> destinations)
    {
        int got = 0;
        long pos = offset;
        foreach (var d in destinations)
        {
            if (d.IsEmpty) continue;
            var n = Read(pos, d.Span);
            if (n <= 0) break;
            got += n;
            pos += n;
            if (n < d.Length) break;
        }
        return got;
    }

    /// <inheritdoc/>
    public ValueTask<int> ReadVectorAsync(long offset, Memory<Memory<byte>> destinations, CancellationToken ct)
        => new(ReadVector(offset, destinations.Span));

    // ═══════════════════════════════════════════════════════════════
    //  持久化谱系
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        try { FileNative.FlushToDisk(_handle, _logger); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw ex.Wrap(nameof(Flush), _path); }
    }

    /// <inheritdoc/>
    /// <remarks>Linux=fdatasync（真数据刷）；Win/macOS ≡ <see cref="Flush"/> 全量回退不抛（能力位诚实表达）。</remarks>
    public void FlushData()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (OperatingSystem.IsLinux())
        {
            WithFd(fd =>
            {
                if (LibC.Fdatasync(fd) != 0)
                {
                    var errno = Marshal.GetLastPInvokeError();
                    throw new IOException($"fdatasync failed, errno={errno}.");
                }
            });
            return;
        }
        Flush();
    }

    /// <inheritdoc/>
    /// <remarks>Linux=posix_fadvise；Win/macOS no-op（能力位 Advise 未置位）。</remarks>
    public void Advise(FileAdvise advise)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (!OperatingSystem.IsLinux()) return;
        var advice = advise switch
        {
            FileAdvise.WillNeed => LibC.PosixFadvWillNeed,
            FileAdvise.DontNeed => LibC.PosixFadvDontNeed,
            FileAdvise.Sequential => LibC.PosixFadvSequential,
            FileAdvise.Random => LibC.PosixFadvRandom,
            _ => LibC.PosixFadvNormal,
        };
        // best-effort：fadvise 失败不影响正确性（仅预取优化），返回码有意忽略
        WithFd(fd => _ = LibC.PosixFadvise(fd, 0, 0, advice));
    }

    // ═══════════════════════════════════════════════════════════════
    //  字节范围锁（Win=LockFileEx 原生；Linux=F_OFD_SETLK，EINVAL 降级 flock 整文件）
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public void Lock(long offset, long length, FileLockMode mode)
        => LockCore(offset, length, mode, blocking: true);

    /// <inheritdoc/>
    public bool TryLock(long offset, long length, FileLockMode mode)
        => LockCore(offset, length, mode, blocking: false);

    private bool LockCore(long offset, long length, FileLockMode mode, bool blocking)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (offset < 0 || length <= 0) throw new ArgumentOutOfRangeException(nameof(length));

        if (OperatingSystem.IsWindows())
        {
            // ★ 专用非 OVERLAPPED 锁句柄：主句柄以 FILE_FLAG_OVERLAPPED 打开且（可能）已绑定运行时
            //   IOCP——在其上裸调 LockFileEx（OVERLAPPED 型 API）会把栈上伪造的 NativeOverlapped 投递
            //   到完成端口，运行时轮询器解引用即崩溃。专用句柄无任何异步关联，阻塞语义原生成立。
            var lockHandle = GetOrCreateLockHandle();
            var ov = new Kernel32.Overlapped
            {
                OffsetLow = (uint)(offset & 0xFFFFFFFF),
                OffsetHigh = (uint)((ulong)offset >> 32),
            };
            uint flags = mode == FileLockMode.Exclusive ? Kernel32.LockFileExclusiveLock : 0;
            if (!blocking) flags |= Kernel32.LockFileFailImmediately;
            try
            {
                if (!Kernel32.LockFileEx(lockHandle, flags, 0,
                        (uint)(length & 0xFFFFFFFF), (uint)((ulong)length >> 32), ref ov))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (!blocking && error is 33 or 122) return false;   // ERROR_LOCK_VIOLATION / ERROR_LOCK_FAILED
                    throw new IOException($"LockFileEx failed, error={error}.");
                }
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { throw ex.Wrap(blocking ? nameof(Lock) : nameof(TryLock), _path); }
        }

        // Unix：F_OFD_SETLK(W)（l_pid 必须 0）
        var fl = new LibC.FLock
        {
            LType = mode == FileLockMode.Exclusive ? LibC.FWrlck : LibC.FRdlck,
            LWhence = 0,
            LStart = offset,
            LLen = length,
            LPid = 0,
        };
        var cmd = blocking ? LibC.FOfdSetlkw : LibC.FOfdSetlk;
        var borrowed = false;
        try
        {
            _handle.DangerousAddRef(ref borrowed);
            var fd = _handle.DangerousGetHandle().ToInt32();
            var rc = LibC.FcntlFlock(fd, cmd, ref fl);
            if (rc == 0) return true;
            var errno = Marshal.GetLastPInvokeError();
            if (errno == 22)
            {
                // 内核 <3.15 无 OFD → flock 整文件降级（粒度降级，能力位应如实反映）
                return FlockFallback(fd, mode, blocking);
            }
            if (!blocking && errno is 11 or 13) return false;   // EAGAIN / EACCES
            throw new IOException($"{(blocking ? "F_OFD_SETLKW" : "F_OFD_SETLK")} failed, errno={errno}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw ex.Wrap(blocking ? nameof(Lock) : nameof(TryLock), _path); }
        finally
        {
            if (borrowed) _handle.DangerousRelease();
        }
    }

    /// <summary>
    /// 惰性打开专用锁句柄（无 FILE_FLAG_OVERLAPPED，FileOptions.None）——本句柄全部范围锁经它执行，
    /// Dispose 时随句柄释放（锁自动解除）。主句柄 Sharing 不允许再开（如 FileShare.None）时抛
    /// SharingViolation（文档化：范围锁消费者应以宽容共享打开）。
    /// </summary>
    private SafeFileHandle GetOrCreateLockHandle()
    {
        var existing = Volatile.Read(ref _lockHandle);
        if (existing is not null) return existing;
        lock (_lockHandleGate)
        {
            if (Volatile.Read(ref _lockHandle) is not null)
                return _lockHandle!;
            try
            {
                var handle = File.OpenHandle(_fs.GetFullPath(_path), FileMode.Open, FileAccess.ReadWrite,
                    FileShare.Read | FileShare.Write | FileShare.Delete);
                Volatile.Write(ref _lockHandle, handle);
                return handle;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw ex.Wrap("Lock(open dedicated handle)", _path);
            }
        }
    }

    private static bool FlockFallback(int fd, FileLockMode mode, bool blocking)
    {
        var op = (mode == FileLockMode.Exclusive ? LibC.LockEx : LibC.LockSh) | (blocking ? 0 : LibC.LockNb);
        return LibC.Flock(fd, op) == 0;
    }

    /// <inheritdoc/>
    public void Unlock(long offset, long length)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (offset < 0 || length <= 0) throw new ArgumentOutOfRangeException(nameof(length));

        if (OperatingSystem.IsWindows())
        {
            var lockHandle = GetOrCreateLockHandle();
            var ov = new Kernel32.Overlapped
            {
                OffsetLow = (uint)(offset & 0xFFFFFFFF),
                OffsetHigh = (uint)((ulong)offset >> 32),
            };
            try
            {
                if (!Kernel32.UnlockFileEx(lockHandle, 0,
                        (uint)(length & 0xFFFFFFFF), (uint)((ulong)length >> 32), ref ov))
                {
                    var error = Marshal.GetLastWin32Error();
                    throw new IOException($"UnlockFileEx failed, error={error}.");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { throw ex.Wrap(nameof(Unlock), _path); }
            return;
        }

        var fl = new LibC.FLock
        {
            LType = LibC.FUnlck,
            LWhence = 0,
            LStart = offset,
            LLen = length,
            LPid = 0,
        };
        var borrowed = false;
        try
        {
            _handle.DangerousAddRef(ref borrowed);
            var fd = _handle.DangerousGetHandle().ToInt32();
            if (LibC.FcntlFlock(fd, LibC.FOfdSetlk, ref fl) == 0) return;
            var errno = Marshal.GetLastPInvokeError();
            if (errno == 22) LibC.Flock(fd, LibC.LockUn);   // OFD 不支持 → flock 降级路径
            else throw new IOException($"F_OFD_SETLK unlock failed, errno={errno}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw ex.Wrap(nameof(Unlock), _path); }
        finally
        {
            if (borrowed) _handle.DangerousRelease();
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  内存映射（生命周期独立——dup/DuplicateHandle 复刻 OS 句柄）
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public IMappedSection Map(long offset, long length, AccessMode access)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _fs.Maintenance.ThrowIfReadsRejected(nameof(Map), _path);
        AccessGate.CheckMapOpen(_fs.Access, access, _path);   // G2 包络：映射无只写 + ⊑ 挂载
        if (offset < 0 || length <= 0) throw new ArgumentOutOfRangeException(nameof(length));
        if (length > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(length), "映射长度超过 2GB 上限。");
        if (offset + length > Length)
            throw new ArgumentException($"映射区间 [{offset}, {offset + length}) 超出文件长度 {Length}。");

        try
        {
            return new DiskMappedSection(DuplicateForMap(access), offset, length, access, _path, _logger);
        }
        catch (FileIOException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw ex.Wrap(nameof(Map), _path); }
    }

    private SafeFileHandle DuplicateForMap(AccessMode access)
    {
        if (OperatingSystem.IsWindows())
        {
            var proc = Kernel32.GetCurrentProcess();
            if (Kernel32.DuplicateHandle(proc, _handle, proc, out var target, 0, false, Kernel32.DuplicateSameAccess))
                return new SafeFileHandle(target, ownsHandle: true);
            var error = Marshal.GetLastWin32Error();
            throw new IOException($"DuplicateHandle failed, error={error}.");
        }

        var borrowed = false;
        try
        {
            _handle.DangerousAddRef(ref borrowed);
            var fd = _handle.DangerousGetHandle().ToInt32();
            var dup = LibC.Dup(fd);
            if (dup < 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                throw new IOException($"dup failed, errno={errno}.");
            }
            return LibC.WrapFileDescriptor(dup);
        }
        finally
        {
            if (borrowed) _handle.DangerousRelease();
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  扩展属性
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    /// <remarks>经 fs 模式路由（Sidecar 模式读伴生文件——与 fs 级同平面互见）。</remarks>
    public ReadOnlyMemory<byte> FileExtra
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            Span<byte> buf = stackalloc byte[IFileSystem.MaxFileExtraBytes];
            var n = _fs.ReadFileExtraRouted(_path, buf);
            return n > 0 ? buf[..n].ToArray() : ReadOnlyMemory<byte>.Empty;
        }
    }

    /// <inheritdoc/>
    /// <remarks>xattr 无偏移原语——RMW（读全量→切片→路由写回；与文件 pwrite 同竞态语义）。</remarks>
    public int ReadFileExtra(long offset, Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _fs.Maintenance.ThrowIfReadsRejected(nameof(ReadFileExtra), _path);
        if (destination.IsEmpty || offset < 0) return 0;
        Span<byte> buf = stackalloc byte[IFileSystem.MaxFileExtraBytes];
        var len = _fs.ReadFileExtraRouted(_path, buf);
        if (offset >= len) return 0;   // pread EOF 契约
        var n = (int)Math.Min(destination.Length, len - offset);
        buf[(int)offset..(int)(offset + n)].CopyTo(destination);
        return n;
    }

    /// <inheritdoc/>
    /// <remarks>RMW：读全量 → 原位 patch/零扩展 → 路由写回（预算封顶在写回前校验）。</remarks>
    public void WriteFileExtra(long offset, ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var gate = _fs.Maintenance.BeginMutation(nameof(WriteFileExtra), _path);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset + data.Length > IFileSystem.MaxFileExtraBytes)
            throw new ArgumentException($"FileExtra 超限（{offset + data.Length} > {IFileSystem.MaxFileExtraBytes}）。");
        Span<byte> buf = stackalloc byte[IFileSystem.MaxFileExtraBytes];
        var len = _fs.ReadFileExtraRouted(_path, buf);
        // pwrite 零扩展语义：gap 填零（stackalloc 已零——仅需保证 len..offset 段为零，buf 本就零初始化）
        var newLen = (int)Math.Max(len, offset + data.Length);
        data.CopyTo(buf[(int)offset..]);
        _fs.WriteFileExtraRouted(_path, buf[..newLen]);
    }

    /// <inheritdoc/>
    public void SetFileExtra(ReadOnlyMemory<byte> extra)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var gate = _fs.Maintenance.BeginMutation(nameof(SetFileExtra), _path);
        if (extra.Length > IFileSystem.MaxFileExtraBytes)
            throw new ArgumentException($"FileExtra 超限（{extra.Length} > {IFileSystem.MaxFileExtraBytes}）。", nameof(extra));
        _fs.WriteFileExtraRouted(_path, extra.Span);
    }

    // ═══════════════════════════════════════════════════════════════
    //  释放（在途异步兜底 R4）
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    /// <remarks>
    /// ★ 按挂载分叉（第九轮）：池内句柄 Dispose = 归还使用权（资源不动——using 安全）；池外 = 真关闭。
    /// ★ 真关闭契约：不等待不取消在途异步；实现侧兜底——计数非零时告警 + 阻塞等待归零（超时强制关闭再告警）。
    /// </remarks>
    public void Dispose()
    {
        if (_poolAttachment is { } attachment)
        {
            attachment.Pool!.OnUsageReleased(this, attachment);   // 归还——绝不关闭资源（释放权在池）
            return;
        }
        CloseUnderlying();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_poolAttachment is { } attachment)
        {
            attachment.Pool!.OnUsageReleased(this, attachment);
            return;
        }
        await Task.Run(CloseUnderlying).ConfigureAwait(false);
    }

    /// <summary>池挂载附件（null = 未挂载——测试/诊断经此观测使用权计数）。</summary>
    internal HandlePoolAttachment? PoolAttachmentOrNull => _poolAttachment;

    HandlePoolAttachment? IPoolAttachable.PoolAttachment => _poolAttachment;

    HandlePoolAttachment IPoolAttachable.AttachPool(FileHandlePool pool)
    {
        var attachment = _poolAttachment ??= new HandlePoolAttachment();
        attachment.Pool = pool;
        return attachment;
    }

    void IPoolAttachable.CloseUnderlying() => CloseUnderlying();

    /// <summary>真关闭（池内三出口 / 池外 Dispose 调用）。</summary>
    internal void CloseUnderlying()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        WaitForInFlightOps();
        // 进程内共享登记注销（先于句柄关闭——关闭后注册表立即允许同 path 再开）
        _sharingRegistry?.Unregister(_path, _sharingEntry!);
        try { _handle.Dispose(); }   // OS 自动释放本句柄全部范围锁
        catch (Exception ex) { _logger?.LogWarning(ex, "CloseUnderlying handle path={Path} failed", _path); }
        try { Volatile.Read(ref _lockHandle)?.Dispose(); }   // 释放全部经专用句柄获取的范围锁
        catch (Exception ex) { _logger?.LogWarning(ex, "CloseUnderlying lock handle path={Path} failed", _path); }
    }

    /// <summary>进程内共享登记注入（fs.Open 调用——Dispose 经 CloseUnderlying 注销）。</summary>
    internal void AttachSharing(SharingRegistry registry, SharingRegistry.Entry entry)
    {
        _sharingRegistry = registry;
        _sharingEntry = entry;
    }

    private void WaitForInFlightOps()
    {
        if (Volatile.Read(ref _inFlightOps) == 0) return;
        _logger?.LogWarning("Dispose 时仍有 {Count} 个在途异步操作（契约违规：调用方须先收敛），等待归零",
            Volatile.Read(ref _inFlightOps));
        var deadline = Environment.TickCount64 + 5000;
        while (Volatile.Read(ref _inFlightOps) != 0)
        {
            if (Environment.TickCount64 > deadline)
            {
                _logger?.LogWarning("在途异步等待超时（5s），强制关闭句柄 path={Path}", _path);
                return;
            }
            Thread.Sleep(1);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  对齐校验（DIO 三重对齐 / 空间操作 AllocationUnit——两个基准并存）
    // ═══════════════════════════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe void ThrowIfMisaligned(long fileOffset, int length, ref byte bufferRef)
    {
        if (_unbufferedSupport != UnbufferedIoSupport.Supported || _requiredAlignment <= 1) return;
        var mask = (ulong)(_requiredAlignment - 1);
        if (((ulong)fileOffset & mask) != 0)
            throw new FileIOException(IOError.AlignmentError,
                $"DIO requires fileOffset aligned to {_requiredAlignment}, got {fileOffset}.", _path, "alignment");
        if (length > 0 && ((ulong)length & mask) != 0)
            throw new FileIOException(IOError.AlignmentError,
                $"DIO requires length aligned to {_requiredAlignment}, got {length}.", _path, "alignment");
        if (length > 0)
        {
            var bufAddr = (ulong)Unsafe.AsPointer(ref bufferRef);
            if ((bufAddr & mask) != 0)
                throw new FileIOException(IOError.AlignmentError,
                    $"DIO requires buffer address aligned to {_requiredAlignment}.", _path, "alignment");
        }
    }

    /// <summary>向量 IO 的 DIO 对齐校验——offset/总长/各片缓冲地址（Pin 即时 Dispose 配对）。</summary>
    private unsafe void ThrowIfVectorMisaligned(long fileOffset, long total, ReadOnlySpan<ReadOnlyMemory<byte>> sources)
    {
        if (_unbufferedSupport != UnbufferedIoSupport.Supported || _requiredAlignment <= 1) return;
        var mask = (ulong)(_requiredAlignment - 1);
        if (((ulong)fileOffset & mask) != 0 || ((ulong)total & mask) != 0)
            throw new FileIOException(IOError.AlignmentError,
                $"DIO vector requires offset and total length aligned to {_requiredAlignment} (offset={fileOffset}, total={total}).",
                _path, "alignment");
        foreach (var s in sources)
        {
            if (s.IsEmpty) continue;
            var h = s.Pin();
            try
            {
                if (((ulong)h.Pointer & mask) != 0)
                    throw new FileIOException(IOError.AlignmentError,
                        $"DIO vector item buffer address must be aligned to {_requiredAlignment}.", _path, "alignment");
            }
            finally
            {
                h.Dispose();
            }
        }
    }

    /// <summary>空间操作对齐（PunchHole/Collapse/Insert）——AllocationUnit 基准（与 DIO 的 RequiredAlignment 互不混用）。</summary>
    private void ThrowIfSpaceAligned(long offset, long length, string operation)
    {
        var unit = _fs.Volume.AllocationUnit;
        if (unit <= 1) return;
        var mask = (ulong)(unit - 1);
        if (((ulong)offset & mask) != 0 || ((ulong)length & mask) != 0)
            throw new FileIOException(IOError.AlignmentError,
                $"{operation} requires offset and length aligned to AllocationUnit {unit} (got offset={offset}, length={length}).",
                _path, operation);
    }

    // ═══════════════════════════════════════════════════════════════
    //  fd 便捷借用（Unix syscall 用）
    // ═══════════════════════════════════════════════════════════════

    private void WithFd(Action<int> action)
    {
        var borrowed = false;
        try
        {
            _handle.DangerousAddRef(ref borrowed);
            action(_handle.DangerousGetHandle().ToInt32());
        }
        finally
        {
            if (borrowed) _handle.DangerousRelease();
        }
    }

    private T WithFdResult<T>(Func<int, T> action)
    {
        var borrowed = false;
        try
        {
            _handle.DangerousAddRef(ref borrowed);
            return action(_handle.DangerousGetHandle().ToInt32());
        }
        finally
        {
            if (borrowed) _handle.DangerousRelease();
        }
    }
}
