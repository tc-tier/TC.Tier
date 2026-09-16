using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TC.Tier.Core.IO.Shared;

namespace TC.Tier.Core.IO.Mem;

/// <summary>
/// 内存文件句柄——<see cref="MemoryFileSystem"/> 的槽视图（open 时 path→(槽, 代际) 解析一次，热路径零字典开销）。
/// <para>★ 与磁盘同构：open 时解析身份，之后 IO 只走 (slot, gen)，不再碰 path。</para>
/// <para>★ 无 syscall；DIO 形态模拟（NoBuffering → Supported——Disk 行为保真，见 <see cref="UnbufferedSupport"/>）。
/// 单值读写无对齐强制：内存 memcpy 天然任意粒度，与 Disk 句柄的 DIO 字节粒度契约（RMW）同像——
/// 四介质单一语义；向量 API 保留对齐 fail-fast（与 Disk 向量同款）。Flush/Advise=no-op，xattr=字典模拟。</para>
/// <para>★ 句柄 Dispose 自动释放其全部范围锁 + 注销共享注册 + 槽引用计数减一。</para>
/// </summary>
internal sealed class MemFileHandle : IFileHandle, IPoolAttachable
{
    private readonly MemoryFileSystem _fs;
    private readonly string _path;
    private readonly int _slotIdx;
    private readonly int _gen;
    private readonly FileOpenOptions _options;
    private readonly bool _writable;   // ★ IO-13：句柄打开语义（Access）——变更面统一守卫（原全裸，读句柄可静默改数据）
    private readonly long _preallocateSize;
    private long _position;      // 句柄书签（D7：会话状态——Position/Seek；追加预留另见 _appendCursor）
    private AppendCursor? _appendCursor;   // 文件级追加预留（fs.Open 注入——同 fs 同路径全部实例共享）
    private long _fallbackCursor;           // 未注入时的回退计数（正常路径不达）
    private int _inFlightOps;    // R4：在途异步计数（mem 异步实为同步——恒 0，契约占位）
    private int _disposed;
    private HandlePoolAttachment? _poolAttachment;   // 池挂载（null = 未挂载——Dispose 走真关闭）

    internal MemFileHandle(MemoryFileSystem fs, string path, int slotIdx, int generation,
        FileOpenOptions options, OpenRegistryEntry openRegistryEntry)
    {
        _fs = fs;
        _path = path;
        _slotIdx = slotIdx;
        _gen = generation;
        _options = options;
        _writable = options.Access is AccessMode.Write or AccessMode.ReadWrite;
        _preallocateSize = options.PreallocateSize;
        OpenRegistryEntry = openRegistryEntry;
        _position = options.Mode == FileOpenMode.Append ? Length : 0;   // Append：游标初始化于 EOF
        if (_preallocateSize > 0)
            Preallocate();   // open 即幂等预分配（与磁盘两步舞收拢语义一致）
    }

    internal OpenRegistryEntry? OpenRegistryEntry { get; }

    /// <summary>注入文件级追加预留盒（fs.Open 调用——同 fs 同路径的全部实例共享同盒）。</summary>
    internal void AttachAppendCursor(AppendCursor cursor) => _appendCursor = cursor;

    /// <inheritdoc/>
    public string Path => _path;

    /// <inheritdoc/>
    /// <remarks>★ DIO 形态模拟（行为保真补全）：带 NoBuffering 提示 = <see cref="UnbufferedIoSupport.Supported"/>
    /// + <see cref="RequiredAlignment"/> 对齐强制——与 Disk 的 DIO 句柄行为逐字对齐（三重对齐违规抛
    /// <see cref="IOError.AlignmentError"/>）。理由：Mem 是测试/模拟介质（Capacity 模拟 DiskFull 同款先例），
    /// 对齐纪律必须在开发期强制执行——否则消费方的对齐 bug 在 Mem 测试期静默通过，切 Disk 生产时大量爆炸。
    /// 不带提示 = <see cref="UnbufferedIoSupport.NotRequested"/>（恒 1 对齐，无约束）。</remarks>
    public UnbufferedIoSupport UnbufferedSupport => (_options.Hints & FileOpenHints.NoBuffering) != 0
        ? UnbufferedIoSupport.Supported
        : UnbufferedIoSupport.NotRequested;

    /// <inheritdoc/>
    /// <remarks>DIO 句柄 = <see cref="MemoryFileSystem.SimulatedSectorSize"/>（512 逻辑扇区——几何基准，
    /// 与 Linux 块设备最广泛形态一致；页/AllocationUnit 是分配粒度不是对齐基）；缓冲句柄恒 1。</remarks>
    public long RequiredAlignment => (_options.Hints & FileOpenHints.NoBuffering) != 0
        ? MemoryFileSystem.SimulatedSectorSize
        : 1;

    /// <summary>向量 IO 的 DIO 对齐校验——offset/总长/各片缓冲地址（Disk 同款；单值 IO 无对齐强制——字节粒度契约）。</summary>
    private unsafe void ThrowIfVectorMisaligned(long fileOffset, long total, ReadOnlySpan<ReadOnlyMemory<byte>> sources)
    {
        if (UnbufferedSupport != UnbufferedIoSupport.Supported || RequiredAlignment <= 1) return;
        var mask = (ulong)(RequiredAlignment - 1);
        if (((ulong)fileOffset & mask) != 0 || ((ulong)total & mask) != 0)
            throw new FileIOException(IOError.AlignmentError,
                $"DIO vector requires offset and total length aligned to {RequiredAlignment} (offset={fileOffset}, total={total}).",
                _path, "alignment");
        foreach (var s in sources)
        {
            if (s.IsEmpty) continue;
            var h = s.Pin();
            try
            {
                if (((ulong)h.Pointer & mask) != 0)
                    throw new FileIOException(IOError.AlignmentError,
                        $"DIO vector item buffer address must be aligned to {RequiredAlignment}.", _path, "alignment");
            }
            finally
            {
                h.Dispose();
            }
        }
    }

    /// <summary>读向量形态（Memory 重载——免 ToArray 分配）。</summary>
    private unsafe void ThrowIfVectorMisaligned(long fileOffset, long total, ReadOnlySpan<Memory<byte>> destinations)
    {
        if (UnbufferedSupport != UnbufferedIoSupport.Supported || RequiredAlignment <= 1) return;
        var mask = (ulong)(RequiredAlignment - 1);
        if (((ulong)fileOffset & mask) != 0 || ((ulong)total & mask) != 0)
            throw new FileIOException(IOError.AlignmentError,
                $"DIO vector requires offset and total length aligned to {RequiredAlignment} (offset={fileOffset}, total={total}).",
                _path, "alignment");
        foreach (var d in destinations)
        {
            if (d.IsEmpty) continue;
            var h = d.Pin();
            try
            {
                if (((ulong)h.Pointer & mask) != 0)
                    throw new FileIOException(IOError.AlignmentError,
                        $"DIO vector item buffer address must be aligned to {RequiredAlignment}.", _path, "alignment");
            }
            finally
            {
                h.Dispose();
            }
        }
    }

    // ══════════════════ 位置读写（pwrite/pread 铁律）══════════════════

    /// <inheritdoc/>
    /// <param name="offset">写入起始偏移（字节，≥0）。</param>
    /// <param name="source">源数据（空则 no-op）。</param>
    public void Write(long offset, ReadOnlySpan<byte> source)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var _gate = _fs.Maintenance.BeginMutation(nameof(Write), _path);
        if (!_writable) throw new InvalidOperationException("只读句柄不接受 Write。");   // ★ IO-13（对齐 Remote/TierVolume）
        WriteCore(offset, source);
    }

    /// <summary>无校验内部写（vector 各片复用——vector 语义按总长校验，Disk 同款，片级不重复）。</summary>
    private void WriteCore(long offset, ReadOnlySpan<byte> source)
    {
        using var _gate = _fs.Maintenance.BeginMutation(nameof(Write), _path);
        if (_fs.IsReserved) _fs.WriteDirect(_slotIdx, _gen, offset, source);
        else _fs.WriteSparse(_slotIdx, _gen, offset, source);
    }

    /// <inheritdoc/>
    /// <param name="offset">写入起始偏移（字节，≥0）。</param>
    /// <param name="source">源数据（空则 no-op）。</param>
    /// <param name="ct">取消令牌（本实现同步完成，不响应取消）。</param>
    /// <returns>写入完成后即完成的 <see cref="ValueTask"/>。</returns>
    public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> source, CancellationToken ct)
    {
        Write(offset, source.Span);   // 内存模式无异步原语——同步拷贝
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    /// <param name="offset">读取起始偏移（字节，≥0）。</param>
    /// <param name="destination">接收缓冲（长度即单次最多读取字节数）。</param>
    /// <returns>实际读取的字节数（0 = offset 已到文件末尾，pread 语义）。</returns>
    public int Read(long offset, Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _fs.Maintenance.ThrowIfReadsRejected(nameof(Read), _path);
        return _fs.IsReserved
            ? _fs.ReadDirect(_slotIdx, _gen, offset, destination)
            : _fs.ReadSparse(_slotIdx, _gen, offset, destination);
    }

    /// <summary>无校验内部读（vector 各片复用）。</summary>
    private int ReadCore(long offset, Span<byte> destination)
        => _fs.IsReserved
            ? _fs.ReadDirect(_slotIdx, _gen, offset, destination)
            : _fs.ReadSparse(_slotIdx, _gen, offset, destination);

    /// <inheritdoc/>
    /// <param name="offset">读取起始偏移（字节，≥0）。</param>
    /// <param name="destination">接收缓冲（长度即单次最多读取字节数）。</param>
    /// <param name="ct">取消令牌（本实现同步完成，不响应取消）。</param>
    /// <returns>完成后得到实际读取的字节数（0 = offset 已到文件末尾）。</returns>
    public ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken ct)
        => new(Read(offset, destination.Span));

    // ══════════════════ 句柄游标（D7）══════════════════

    /// <inheritdoc/>
    public long Position => Volatile.Read(ref _position);

    /// <inheritdoc/>
    /// <param name="source">要追加的数据（空则原样返回当前游标位置）。</param>
    /// <returns>本次数据被预留到的起始偏移（字节）。</returns>
    /// <exception cref="FileIOException">配额超限等写入失败——<see cref="FileIOException.ReservedOffset"/> 携带已预留区间起点（D7 失败语义）。</exception>
    public long Append(ReadOnlySpan<byte> source)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var _gate = _fs.Maintenance.BeginMutation(nameof(Append), _path);
        if (!_writable) throw new InvalidOperationException("只读句柄不接受 Append。");   // ★ IO-13：预留前拒（防游标留洞）
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
    /// <param name="source">要追加的数据。</param>
    /// <param name="ct">取消令牌（本实现同步完成，不响应取消）。</param>
    /// <returns>完成后得到本次数据被预留到的起始偏移（字节）。</returns>
    public ValueTask<long> AppendAsync(ReadOnlyMemory<byte> source, CancellationToken ct)
        => new(Append(source.Span));

    /// <inheritdoc/>
    /// <param name="offset">相对 <paramref name="origin"/> 的偏移（字节，可为负）。</param>
    /// <param name="origin">基准位置（Begin/Current/End）。</param>
    /// <returns>移动后的绝对位置（字节）。</returns>
    /// <exception cref="ArgumentOutOfRangeException">origin 非 Begin/Current/End。</exception>
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

    // ══════════════════ 空间管理 ══════════════════

    /// <inheritdoc/>
    public void Preallocate()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var _gate = _fs.Maintenance.BeginMutation(nameof(Preallocate), _path);
        if (_preallocateSize <= 0) return;
        if (!_writable) return;   // ★ IO-13：只读句柄不扩容（对齐 Remote 静默跳过——open 即预分配不自炸）
        var current = Length;
        if (_preallocateSize > current)
        {
            // ★ CORE-14：slot-keyed（代际校验——旧句柄不扩展同名新文件）；游标经本句柄注入盒
            // 只升不降（盒按 path 共享——同 path 全部句柄生效；path 已换主 = 旧盒无害）
            if (_fs.IsReserved) _fs.GrowSlot(_slotIdx, _gen, _preallocateSize);
            else _fs.PrewarmSlot(_slotIdx, _gen, _preallocateSize);
            if (_appendCursor is { } cursor)
            {
                while (true)
                {
                    var cur = Volatile.Read(ref cursor.Value);
                    if (cur >= _preallocateSize || Interlocked.CompareExchange(ref cursor.Value, _preallocateSize, cur) == cur) break;
                }
            }
        }
    }

    /// <inheritdoc/>
    public long Length
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            lock (_fs.SyncRoot)
            {
                var slot = _fs.GetSlot(_slotIdx);
                if (slot.Generation != _gen) MemoryFileSystem.ThrowStale(_slotIdx, _gen);   // ★ IO-24：槽已换主（CORE-14 同纪律——防身份混淆）
                return slot.Data?.Size ?? 0;
            }
        }
    }

    /// <inheritdoc/>
    public long AllocatedSize
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            return _fs.AllocatedSizeOf(_slotIdx);
        }
    }

    /// <summary>设置槽逻辑长度（截断/扩展——扩展零填充；追加预留盒权威复位）。</summary>
    /// <param name="length">目标逻辑长度（字节，≥0）。</param>
    public void SetLength(long length)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var _gate = _fs.Maintenance.BeginMutation(nameof(SetLength), _path);
        if (!_writable) throw new InvalidOperationException("只读句柄不接受 SetLength。");   // ★ IO-13
        // ★ CORE-14：slot-keyed（代际校验——旧句柄不再截断/扩展同名新文件；path 重解析 = 跨代越权）；
        // 游标经本句柄注入盒复位（盒按 path 共享——同 path 全部句柄生效；path 已换主 = 旧盒复位无害）
        _fs.TruncateSlot(_slotIdx, _gen, length);
        if (_appendCursor is { } cursor)
            Interlocked.Exchange(ref cursor.Value, length);
    }

    /// <summary>打洞——字节粒度精确归零（读零等价；整页空间归还，物理地板 = 页）。</summary>
    /// <param name="offset">洞起始偏移（字节，≥0）。</param>
    /// <param name="length">洞长度（字节，≤0 时 no-op）。</param>
    public void PunchHole(long offset, long length)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var _gate = _fs.Maintenance.BeginMutation(nameof(PunchHole), _path);
        if (!_writable) throw new InvalidOperationException("只读句柄不接受 PunchHole。");   // ★ IO-13
        if (length <= 0) return;
        // ★ 字节精确（extent 代数本就字节级——Reserved memset / Sparse 页边界零化）；
        //   空间归还仅整页生效（物理地板=页），可观测语义（读零 + 区间洞）任意粒度等价——
        //   引擎 Reclaim 是字节粒度（A4），mem 无硬件对齐约束。
        if (_fs.IsReserved) _fs.PunchHoleReserved(_slotIdx, _gen, offset, length);
        else _fs.PunchHoleSparse(_slotIdx, _gen, offset, length);
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<(long Start, long End)> EnumerateAllocatedRanges()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        return _fs.EnumerateAllocatedOf(_slotIdx);
    }

    /// <inheritdoc/>
    /// <remarks>mem 全支持（能力位置位）——memmove + 长度变更 + 页表重映射。</remarks>
    /// <param name="offset">待删除区间起始偏移（字节；须按 AllocationUnit(PageSize) 对齐）。</param>
    /// <param name="length">待删除区间长度（字节，≤0 时 no-op；须按 AllocationUnit 对齐）。</param>
    /// <exception cref="FileIOException">offset/length 未按 AllocationUnit 对齐。</exception>
    public void CollapseRange(long offset, long length)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var _gate = _fs.Maintenance.BeginMutation(nameof(CollapseRange), _path);
        if (length <= 0) return;
        ThrowIfSpaceAligned(offset, length);
        if (_fs.IsReserved) _fs.ShiftRangeReserved(_slotIdx, _gen, offset, length, insert: false);
        else _fs.ShiftRangeSparse(_slotIdx, _gen, offset, length, insert: false);
    }

    /// <summary>插入区间——在 offset 处开 length 长度的洞，其后数据整体后移（memmove + 页表重映射）。</summary>
    /// <param name="offset">插入点偏移（字节；须按 AllocationUnit(PageSize) 对齐）。</param>
    /// <param name="length">插入长度（字节，≤0 时 no-op；须按 AllocationUnit 对齐）。</param>
    /// <exception cref="FileIOException">offset/length 未按 AllocationUnit 对齐。</exception>
    public void InsertRange(long offset, long length)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var _gate = _fs.Maintenance.BeginMutation(nameof(InsertRange), _path);
        if (length <= 0) return;
        ThrowIfSpaceAligned(offset, length);
        if (_fs.IsReserved) _fs.ShiftRangeReserved(_slotIdx, _gen, offset, length, insert: true);
        else _fs.ShiftRangeSparse(_slotIdx, _gen, offset, length, insert: true);
    }

    // ══════════════════ 文件间拷贝 ══════════════════

    /// <summary>文件间拷贝——同卷 mem 句柄间对齐缓冲搬运（64KB 分块）。</summary>
    /// <param name="destination">目标句柄（须为同卷 mem 句柄，否则抛 <see cref="ArgumentException"/>）。</param>
    /// <param name="sourceOffset">源起始偏移（字节，≥0）。</param>
    /// <param name="destinationOffset">目标起始偏移（字节，≥0）。</param>
    /// <param name="length">计划拷贝字节数（≥0；超出源剩余部分按实际可得截取）。</param>
    /// <returns>实际拷贝的字节数（字节）。</returns>
    /// <exception cref="ArgumentException">目标不是同卷 mem 句柄。</exception>
    /// <exception cref="ArgumentOutOfRangeException">偏移/长度为负。</exception>
    /// <exception cref="FileIOException">失败——<see cref="FileIOException.CompletedLength"/> 携带已完成字节数。</exception>
    public long CopyRange(IFileHandle destination, long sourceOffset, long destinationOffset, long length)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (destination is not MemFileHandle dest || !ReferenceEquals(dest._fs, _fs))
            throw new ArgumentException($"CopyRange requires a {nameof(MemFileHandle)} destination on the same volume.", nameof(destination));
        if (sourceOffset < 0 || destinationOffset < 0 || length < 0)
            throw new ArgumentOutOfRangeException(nameof(sourceOffset));

        var available = Math.Min(length, Math.Max(0, Length - sourceOffset));
        long done = 0;
        try
        {
            // ★ CORE-12：对齐 IO 缓冲（NoBuffering 句柄的缓冲地址对齐约束——托管数组仅 8B 对齐，
            //   旧实现 DIO 句柄间 CopyRange 确定性 AlignmentError；对齐 Disk 的 RentIoBuffer 形态）
            var alignment = (int)Math.Max(1, Math.Max(RequiredAlignment, dest.RequiredAlignment));
            var chunk = (int)Math.Min(available, 1 << 16);
            var buf = _fs.RentIoBuffer(Math.Max(alignment, chunk), alignment);
            try
            {
                while (done < available)
                {
                    var want = (int)Math.Min(buf.Length, available - done);
                    var n = Read(sourceOffset + done, buf.Span[..want]);
                    if (n <= 0) break;
                    dest.Write(destinationOffset + done, buf.Span[..n]);
                    done += n;
                }
            }
            finally
            {
                _fs.ReturnIoBuffer(buf);
            }
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

    /// <inheritdoc/>
    /// <remarks>mem 无引用克隆原语——回退 CopyRange 全量（能力位 CopyRange 未置位，诚实表达）。</remarks>
    /// <returns>实际拷贝的字节数（字节，通常等于本文件逻辑长度）。</returns>
    public long CloneRange(IFileHandle destination) => CopyRange(destination, 0, 0, Length);

    // ══════════════════ 向量化 IO（回退逐片——语义等价）══════════════════

    /// <summary>向量写——把 sources 各片按序连续写到 offset 起始位置（DIO 句柄对齐 fail-fast；逐片回退）。</summary>
    /// <param name="offset">写入起始偏移（字节，≥0）。</param>
    /// <param name="sources">写入片段序列（逻辑上首尾相接）。</param>
    /// <exception cref="FileIOException">DIO 句柄上 offset/总长/缓冲地址未对齐。</exception>
    public void WriteVector(long offset, ReadOnlySpan<ReadOnlyMemory<byte>> sources)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (UnbufferedSupport == UnbufferedIoSupport.Supported && !sources.IsEmpty)
        {
            var total = 0L;
            foreach (var s in sources) total += s.Length;
            ThrowIfVectorMisaligned(offset, total, sources);
        }
        long pos = offset;
        foreach (var s in sources)
        {
            if (!s.IsEmpty) WriteCore(pos, s.Span);
            pos += s.Length;
        }
    }

    /// <summary>向量写（异步形态——同步完成后即完成）。</summary>
    /// <param name="offset">写入起始偏移（字节，≥0）。</param>
    /// <param name="sources">写入片段序列（逻辑上首尾相接）。</param>
    /// <param name="ct">取消令牌（本实现同步完成，不响应取消）。</param>
    /// <returns>全部片段写完后即完成的 <see cref="ValueTask"/>。</returns>
    public ValueTask WriteVectorAsync(long offset, ReadOnlyMemory<ReadOnlyMemory<byte>> sources, CancellationToken ct)
    {
        WriteVector(offset, sources.Span);
        return ValueTask.CompletedTask;
    }

    /// <summary>向量读——从 offset 起按序填入 destinations 各片，读到文件末尾或某片未读满即停。</summary>
    /// <param name="offset">读取起始偏移（字节，≥0）。</param>
    /// <param name="destinations">接收片段序列（逻辑上首尾相接）。</param>
    /// <returns>实际读取的总字节数（字节）。</returns>
    public int ReadVector(long offset, ReadOnlySpan<Memory<byte>> destinations)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (UnbufferedSupport == UnbufferedIoSupport.Supported && !destinations.IsEmpty)
        {
            var total = 0L;
            foreach (var d in destinations) total += d.Length;
            ThrowIfVectorMisaligned(offset, total, destinations);
        }
        int got = 0;
        long pos = offset;
        foreach (var d in destinations)
        {
            if (d.IsEmpty) continue;
            var n = ReadCore(pos, d.Span);
            if (n <= 0) break;
            got += n;
            pos += n;
            if (n < d.Length) break;
        }
        return got;
    }

    /// <summary>向量读（异步形态——同步完成后即完成）。</summary>
    /// <param name="offset">读取起始偏移（字节，≥0）。</param>
    /// <param name="destinations">接收片段序列（逻辑上首尾相接）。</param>
    /// <param name="ct">取消令牌（本实现同步完成，不响应取消）。</param>
    /// <returns>完成后得到实际读取的总字节数（字节）。</returns>
    public ValueTask<int> ReadVectorAsync(long offset, Memory<Memory<byte>> destinations, CancellationToken ct)
        => new(ReadVector(offset, destinations.Span));

    // ══════════════════ 持久化与提示（mem no-op）══════════════════

    /// <inheritdoc/>
    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);   // no-op（mem 无持久化）
    }

    /// <inheritdoc/>
    public void FlushData()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);   // ≡ Flush（no-op）
    }

    /// <summary>访问提示——mem 介质 no-op（能力位 Advise 未置位）。</summary>
    /// <param name="advise">访问提示（被忽略）。</param>
    public void Advise(FileAdvise advise)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);   // no-op（能力位 Advise 未置位）
    }

    // ══════════════════ 字节范围锁（进程内区间表——多句柄协调真实生效）══════════════════

    /// <summary>获取进程内字节范围锁（冲突抛异常阻塞等待）。</summary>
    /// <param name="offset">锁区间起始偏移（字节，≥0）。</param>
    /// <param name="length">锁区间长度（字节，&gt;0）。</param>
    /// <param name="mode">锁模式（Shared/Exclusive）。</param>
    public void Lock(long offset, long length, FileLockMode mode)
        => _fs.LockRange(_slotIdx, _gen, offset, length, mode, blocking: true, owner: this);

    /// <summary>尝试获取进程内字节范围锁（冲突立即返回失败，不阻塞）。</summary>
    /// <param name="offset">锁区间起始偏移（字节，≥0）。</param>
    /// <param name="length">锁区间长度（字节，&gt;0）。</param>
    /// <param name="mode">锁模式（Shared/Exclusive）。</param>
    /// <returns>true = 获取成功；false = 与其他 owner 的重叠排他冲突。</returns>
    public bool TryLock(long offset, long length, FileLockMode mode)
        => _fs.LockRange(_slotIdx, _gen, offset, length, mode, blocking: false, owner: this);

    /// <summary>释放本句柄此前获取的字节范围锁。</summary>
    /// <param name="offset">锁区间起始偏移（字节，须与获锁时一致）。</param>
    /// <param name="length">锁区间长度（字节，须与获锁时一致）。</param>
    public void Unlock(long offset, long length) => _fs.UnlockRange(_slotIdx, offset, length, owner: this);

    // ══════════════════ 内存映射 ══════════════════

    /// <inheritdoc/>
    /// <remarks>
    /// Reserved = 槽直址零拷贝（视图即文件内存——写穿透天然成立）；
    /// Sparse = 物化拷贝（ReadWrite 带脏标记，Flush/Dispose 写回——可见时点=Flush/Dispose 非实时）。
    /// 映射生命周期独立于父句柄（RefCount 钉住槽/旧 buffer）。
    /// </remarks>
    /// <summary>内存映射文件区间——Reserved 直址零拷贝 / Sparse 物化拷贝（见 remarks）。</summary>
    /// <param name="offset">映射起始偏移（字节，≥0）。</param>
    /// <param name="length">映射长度（字节，&gt;0 且 ≤2GB）。</param>
    /// <param name="access">映射访问模式（无只写映射；不得超出挂载访问权）。</param>
    /// <returns>映射区段（<see cref="IMappedSection"/>，生命周期独立于父句柄）。</returns>
    /// <exception cref="ArgumentOutOfRangeException">offset 为负、length ≤0 或 &gt;2GB。</exception>
    /// <exception cref="ArgumentException">映射区间超出文件长度。</exception>
    public IMappedSection Map(long offset, long length, AccessMode access)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _fs.Maintenance.ThrowIfReadsRejected(nameof(Map), _path);
        AccessGate.CheckMapOpen(_fs.Access, access, _path);   // G2 包络：映射无只写 + ⊑ 挂载
        if (offset < 0 || length <= 0) throw new ArgumentOutOfRangeException(nameof(length));
        if (length > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(length), "映射长度超过 2GB 上限。");
        if (offset + length > Length)
            throw new ArgumentException($"映射区间 [{offset}, {offset + length}) 超出文件长度 {Length}。");

        return _fs.IsReserved
            ? MemDirectMappedSection.Create(_fs, _slotIdx, _gen, offset, length, access, _path)   // ★ IO-24：Reserved 直址同带代际（Sparse 原有）
            : MemSparseMappedSection.Create(_fs, _slotIdx, _gen, offset, length, access, _path);
    }

    // ══════════════════ 扩展属性（字典模拟——能力位置位）══════════════════

    /// <inheritdoc/>
    /// <remarks>槽 blob（与 fs 级 CreateFile/Stat 同平面互见）。</remarks>
    public ReadOnlyMemory<byte> FileExtra
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            return _fs.ReadSlotExtra(_slotIdx) is { } v ? v : ReadOnlyMemory<byte>.Empty;
        }
    }

    /// <summary>读取 FileExtra（槽 blob 承载）片段。</summary>
    /// <param name="offset">FileExtra 内起始偏移（字节，≥0）。</param>
    /// <param name="destination">接收缓冲。</param>
    /// <returns>实际读取的字节数（0 = 缓冲为空、FileExtra 未设或 offset 已达末尾，pread EOF 契约）。</returns>
    public int ReadFileExtra(long offset, Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _fs.Maintenance.ThrowIfReadsRejected(nameof(ReadFileExtra), _path);
        if (destination.IsEmpty || offset < 0) return 0;
        if (_fs.ReadSlotExtra(_slotIdx) is not { } blob) return 0;
        if (offset >= blob.Length) return 0;   // pread EOF 契约
        var n = (int)Math.Min(destination.Length, blob.Length - offset);
        blob.AsSpan((int)offset, n).CopyTo(destination);
        return n;
    }

    /// <summary>按偏移写入 FileExtra——锁内 RMW（越当前长度零扩展），原子生效。</summary>
    /// <param name="offset">FileExtra 内起始偏移（字节，≥0）。</param>
    /// <param name="data">写入数据。</param>
    /// <exception cref="ArgumentException">offset + data.Length 超出 MaxFileExtraBytes 上限。</exception>
    public void WriteFileExtra(long offset, ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var _gate = _fs.Maintenance.BeginMutation(nameof(WriteFileExtra), _path);
        if (!_writable) throw new InvalidOperationException("只读句柄不接受 WriteFileExtra。");   // ★ IO-14（对齐 Remote）
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset + data.Length > IFileSystem.MaxFileExtraBytes)
            throw new ArgumentException($"FileExtra 超限（{offset + data.Length} > {IFileSystem.MaxFileExtraBytes}）。");
        _fs.WriteSlotExtraRange(_slotIdx, offset, data);   // 锁内 RMW——真原子
    }

    /// <summary>整体替换 FileExtra 内容（槽 blob 整写；上限 MaxFileExtraBytes）。</summary>
    /// <param name="extra">新 FileExtra 内容。</param>
    /// <exception cref="ArgumentException">extra.Length 超出 MaxFileExtraBytes 上限。</exception>
    public void SetFileExtra(ReadOnlyMemory<byte> extra)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var _gate = _fs.Maintenance.BeginMutation(nameof(SetFileExtra), _path);
        if (!_writable) throw new InvalidOperationException("只读句柄不接受 SetFileExtra。");   // ★ IO-14（对齐 Remote）
        if (extra.Length > IFileSystem.MaxFileExtraBytes)
            throw new ArgumentException($"FileExtra 超限（{extra.Length} > {IFileSystem.MaxFileExtraBytes}）。", nameof(extra));
        _fs.WriteSlotExtra(_slotIdx, extra.Span);
    }

    // ══════════════════ 释放 ══════════════════

    /// <inheritdoc/>
    /// <remarks>★ 按挂载分叉（第九轮）：池内句柄 Dispose = 归还使用权（槽引用计数不动——using 安全）；池外 = 真关闭。</remarks>
    public void Dispose()
    {
        if (_poolAttachment is { } attachment)
        {
            attachment.Pool!.OnUsageReleased(this, attachment);   // 归还——绝不触达槽引用计数（释放权在池）
            return;
        }
        CloseUnderlying();
    }

    /// <inheritdoc/>
    /// <returns>释放完成后即完成的 <see cref="ValueTask"/>（与 <see cref="Dispose"/> 同步等价）。</returns>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
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

    /// <summary>真关闭（池内三出口 / 池外 Dispose 调用）——注销共享注册 + 释放范围锁 + 槽引用计数减一。</summary>
    internal void CloseUnderlying()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _fs.ReleaseHandle(this, _slotIdx);
    }

    /// <summary>空间操作对齐（PunchHole/Collapse/Insert）——AllocationUnit=PageSize 基准（与 DIO 的 RequiredAlignment 互不混用）。</summary>
    private void ThrowIfSpaceAligned(long offset, long length)
    {
        var unit = _fs.PageSize;
        var mask = (ulong)(unit - 1);
        if (((ulong)offset & mask) != 0 || ((ulong)length & mask) != 0)
            throw new FileIOException(IOError.AlignmentError,
                $"空间操作要求 offset/length 对齐到 AllocationUnit(PageSize) {unit}（got offset={offset}, length={length}）。",
                _path, "alignment");
    }
}
