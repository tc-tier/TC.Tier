using System.Text;
using TC.Tier.Core.IO.Shared;

namespace TC.Tier.Core.IO.Remote;

/// <summary>
/// 远程文件句柄——<see cref="RemoteFileSystem"/> 的桥视图（B3.3）。
/// <para>★ 写句柄 = staging 写回层（页缓存的远端同构物）：Open 仅 Head 记长度（零下载）；
///   首次读写未物化区间按需 Range GET 补集（纯追加句柄零历史拉取）；<see cref="Flush"/> =
///   <b>唯一持久化点</b>（multipart 编排 + 未触区间回填）。任何 Dispose 不触发上传。</para>
/// <para>★ 读句柄 = Open 时 Head 一次缓存长度与元数据（<b>不追新</b>——其他句柄的 Flush 追加不可见，
///   需要追新重新 Open）+ 可选预取页缓存。</para>
/// <para>★ 能力位矩阵（§4.8）：Unsupported 族 = RangeShift（调用抛）；RangeLock = 进程内 advisory
///   区间表（G8）；Mmap = 物化映射（G11——Read 快照 / ReadWrite staging 视图写回）；
///   AllocationUnit=1（空间操作无对齐约束——staging memset 语义）。</para>
/// </summary>
internal sealed class RemoteFileHandle : IFileHandle, IPoolAttachable
{
    // === 桥选项（静态共享——record 不可变；docs/sync-async-bridge.md §9 P2）===
    private static readonly SyncBridgeOptions s_materializeOpts = new() { Name = "remote-materialize" };
    private static readonly SyncBridgeOptions s_readOpts = new() { Name = "remote-read" };
    private static readonly SyncBridgeOptions s_copyOpts = new() { Name = "remote-copyrange", TimeoutMs = 60_000 };
    // Flush = multipart 整对象上传（大对象慢介质）——预算放宽到 10 分钟（此前为无限期阻塞）
    private static readonly SyncBridgeOptions s_flushOpts = new() { Name = "remote-flush", TimeoutMs = 600_000 };

    private readonly RemoteFileSystem _fs;
    private readonly string _path;
    private readonly FileOpenOptions _options;
    private readonly bool _writable;
    private readonly string _key;
    private readonly int _pageSize;

    // 写句柄状态
    private StagingBuffer? _staging;
    private long _baseLength;          // Open 时旧对象长度（Flush 后 = 新长度）
    private long _backfillLimit;       // 旧对象数据有效上界（SetLength 收缩降级——截断后扩展读零）
    private bool _contentDirty;
    private bool _metaDirty;
    private readonly Dictionary<string, string> _xattrs = new();   // 名 → Base64 值（PUT 原子快照语义）

    // 读句柄状态
    private long _cachedLength = -1;
    private ObjectMetadata? _cachedMetadata;
    private List<(long Start, long End)> _readHoles = [];   // Open 时解析（读路径加速——内容即真相）
    private readonly Dictionary<long, byte[]>? _readCache;   // 读句柄页缓存（LRU 近似——容量淘汰）
    private readonly LinkedList<long>? _readLru;             // 访问序（显式 LRU——容量满时不自逐新页）
    private readonly object _readCacheLock = new();          // ★ CORE-15：读缓存同步（并发共享读句柄——
                                                              //   Dictionary/链表并发增删 = 结构破坏；锁内纯内存无 await，命中开销 ~50ns/页）
    private long _readCacheBytes;
    private bool _sequentialAdvise;                          // ★ CORE-15：跨线程写读——Volatile 访问

    // 共通
    /// <summary>洞元数据保留键（读路径加速专用——消费者 xattr 禁用此名；仅 Flush 时写入）。</summary>
    internal const string HoleMetadataKey = "tier-holes";
    private readonly List<(long Start, long End)> _holes = [];   // 合并排序的洞区间（写句柄簿记）
    private long _position;
    private AppendCursor? _appendCursor;
    private long _fallbackCursor;
    /// <summary>Flush 串行化门（P2 改造：lock → 异步信号量——FlushAsync 全 await 后 lock 不能跨 await）。</summary>
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private HandlePoolAttachment? _poolAttachment;
    private int _disposed;

    internal RemoteFileHandle(RemoteFileSystem fs, string path, FileOpenOptions options, ObjectInfo? existing)
    {
        _fs = fs;
        _path = path;
        _options = options;
        _writable = options.Access is AccessMode.Write or AccessMode.ReadWrite;
        _key = fs.KeyOf(path);
        _pageSize = fs.Options.StagingPageSize;
        _baseLength = existing?.Size ?? 0;
        _backfillLimit = _baseLength;

        if (_writable)
        {
            _staging = new StagingBuffer(_pageSize, fs.Options.StagingMemoryLimit,
                fs.Options.Spill?.Directory, fs.SpillFileSystem);
            if (existing?.Metadata is { UserMetadata.Count: > 0 } meta)
            {
                foreach (var (k, v) in meta.UserMetadata)
                {
                    if (k == HoleMetadataKey) continue;   // 保留键走洞簿记（_holes），不进用户 xattr
                    _xattrs[k] = v;   // staging 初始 = 旧元数据（Flush 前读 = staging 值）
                }
                _holes.AddRange(ParseHoles(meta));   // 继承洞视图（写填洞/截断时正确增删，Flush 重编码）
            }
            // ★ 逻辑长度 = 旧对象长度（未物化区间 = 旧数据语义——Read/Flush 按需物化）
            _staging.SetLength(_baseLength);
            if (options.Mode == FileOpenMode.Truncate)
            {
                _baseLength = 0;
                _backfillLimit = 0;
                _staging.SetLength(0);
                _contentDirty = true;
            }
            if (options.PreallocateSize > _staging.Length)
            {
                _staging.SetLength(options.PreallocateSize);
                _contentDirty = true;
            }
        }
        else
        {
            _cachedLength = _baseLength;
            _cachedMetadata = existing?.Metadata;
            _readHoles = ParseHoles(existing?.Metadata);
            if (fs.Options.ReadCacheBytes > 0)
            {
                _readCache = new Dictionary<long, byte[]>();
                _readLru = new LinkedList<long>();
            }
        }
        _position = options.Mode == FileOpenMode.Append ? LengthInternal() : 0;
    }

    /// <inheritdoc/>
    public string Path => _path;

    /// <inheritdoc/>
    /// <remarks>远程无 DIO 概念——恒 <see cref="UnbufferedIoSupport.NotRequested"/>（本就绕过一切本地缓存）。</remarks>
    public UnbufferedIoSupport UnbufferedSupport => UnbufferedIoSupport.NotRequested;

    /// <inheritdoc/>
    public long RequiredAlignment => 1;

    // ═════════════════════════════ 位置读写（pread/pread 铁律）═════════════════════════════

    /// <inheritdoc/>
    public void Write(long offset, ReadOnlySpan<byte> source)
    {
        ThrowIfDisposed();
        using var _gate = _fs.Maintenance.BeginMutation(nameof(Write), _path);
        if (!_writable) throw new InvalidOperationException("只读句柄不接受 Write。");
        if (source.IsEmpty) return;
        // ★ CORE-06：写与 Flush 互斥（Flush 屏障语义——分类/上传/Complete 全程一致；否则 Flush 期间
        //   的并发写：①落在被分类为 server-side-copy 的 part = 上传旧内容；②MarkAllClean 擦除新脏标
        //   = 写返回成功却永久丢失）。写-写并发不受影响（多写者共句柄仍并行——_flushGate 只串行化 vs Flush）
        _flushGate.Wait();
        try
        {
            var len = source.Length;   // ref-like Span 不可进 lambda——长度先取出
            _fs.QuotaProject(_path, Math.Max(_staging!.Length, offset + len));   // G3 写前拒（惰性基线 + 投影）
            SyncAsyncBridge.Run(ct => MaterializeComplementAsync(offset, len, punch: false, ct), s_materializeOpts);
            _staging.Write(offset, source);
            _contentDirty = true;
            RemoveHole(offset, offset + source.Length);   // 写填洞
        }
        finally
        {
            _flushGate.Release();
        }
    }

    /// <inheritdoc/>
    /// <remarks>异步族直通对象层异步（真异步 IO——非 Task.Run 假异步）。</remarks>
    public async ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> source, CancellationToken ct)
    {
        ThrowIfDisposed();
        using var _gate = _fs.Maintenance.BeginMutation(nameof(WriteAsync), _path);
        if (!_writable) throw new InvalidOperationException("只读句柄不接受 WriteAsync。");
        if (source.IsEmpty) return;
        // ★ CORE-06：同 Write——与 Flush 互斥（Flush 屏障一致性；写-写并行不受影响）
        await _flushGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await MaterializeComplementAsync(offset, source.Length, punch: false, ct).ConfigureAwait(false);
            _staging!.Write(offset, source.Span);
            _contentDirty = true;
            RemoveHole(offset, offset + source.Length);   // 写填洞
        }
        finally
        {
            _flushGate.Release();
        }
    }

    /// <inheritdoc/>
    public int Read(long offset, Span<byte> destination)
    {
        ThrowIfDisposed();
        var buf = destination.ToArray();
        var n = SyncAsyncBridge.Run(ct => ReadCoreAsync(offset, buf, ct), s_readOpts);
        buf.AsSpan(0, n).CopyTo(destination);
        return n;
    }

    /// <inheritdoc/>
    public ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken ct)
        => ReadCoreAsync(offset, destination, ct);

    private async ValueTask<int> ReadCoreAsync(long offset, Memory<byte> destination, CancellationToken ct)
    {
        ThrowIfDisposed();
        _fs.Maintenance.ThrowIfReadsRejected(nameof(Read), _path);
        if (destination.Length == 0) return 0;
        if (_writable)
        {
            await MaterializeComplementAsync(offset, destination.Length, punch: false, ct).ConfigureAwait(false);
            return _staging!.Read(offset, destination.Span);
        }

        // 读句柄：长度缓存（不追新）——EOF 语义按 Open 时快照
        var length = _cachedLength;
        if (offset >= length) return 0;
        var want = (int)Math.Min(destination.Length, length - offset);

        // 洞命中零填充（读路径加速）：洞区间本地返零不发 GET；其余分片走常规取数
        if (_readHoles.Count > 0)
        {
            // 分片（真实取数）与洞（零填充）无缝铺满请求区间——洞也是有效数据，
            // 返回连续前缀长度：全洞读 = want；分片短读（EOF 类）则截至该点
            destination.Span[..want].Clear();
            foreach (var (s, e) in SubtractRangeList(offset, offset + want, _readHoles))
            {
                var pieceLen = (int)(e - s);
                var n = await ReadHandleSpanAsync(s, pieceLen, destination[(int)(s - offset)..], ct).ConfigureAwait(false);
                if (n < pieceLen)
                    return (int)(s - offset) + Math.Max(0, n);
            }
            return want;
        }

        return await ReadHandleSpanAsync(offset, want, destination, ct).ConfigureAwait(false);
    }

    private async ValueTask<int> ReadHandleSpanAsync(long offset, int want, Memory<byte> destination, CancellationToken ct)
    {
        var length = _cachedLength;
        // 页缓存命中路径（显式 LRU——容量满时驱逐访问序最旧页；刚插入页永不自逐）
        // ★ CORE-15：全程 `_readCacheLock` 保护（Dictionary/链表并发增删 = 悬挂指针/丢页）——
        // 锁内纯内存（命中拷贝 ~4KB memcpy）无 await；缺页的 Range GET 在锁外（网络 IO 不进锁）
        if (_readCache is { } cache && _readLru is { } lru)   // 共同可空对——模式捕获后流分析成立（CS8602×4 消）
        {
            var got = 0;
            var pos = 0;
            var guard = 0;
            while (pos < want)
            {
                if (++guard > 100_000)
                    throw new InvalidOperationException(
                        $"[readCache] 迭代超限 guard={guard} pos={pos} want={want} offset={offset} pageSize={_pageSize} cacheCount={cache.Count} seq={Volatile.Read(ref _sequentialAdvise)}");
                var pageIdx = (offset + pos) / _pageSize;
                var inPage = (int)((offset + pos) % _pageSize);
                var chunk = (int)Math.Min(_pageSize - inPage, want - pos);
                lock (_readCacheLock)
                {
                    if (cache.TryGetValue(pageIdx, out var page))
                    {
                        lru.Remove(pageIdx);
                        lru.AddLast(pageIdx);   // 访问序刷新
                        page.AsSpan(inPage, chunk).CopyTo(destination.Span[pos..(pos + chunk)]);
                        pos += chunk;
                        got += chunk;
                        continue;
                    }
                }
                // 缺页：Range GET 预取窗口（顺序提示放大 4×）——锁外网络 IO
                var windowPages = Math.Max(1, _fs.Options.PrefetchPages * (Volatile.Read(ref _sequentialAdvise) ? 4 : 1));
                var fetchStart = pageIdx * _pageSize;
                var fetchLen = (int)Math.Min((long)_pageSize * windowPages, length - fetchStart);
                var fetch = new byte[fetchLen];
                var n = await _fs.Store.GetAsync(_key, fetchStart, fetch, ct).ConfigureAwait(false);
                if (n <= 0) return got;
                // 装缓存（容量淘汰——驱逐访问序最旧；新装页在队尾必存活）——锁内
                lock (_readCacheLock)
                {
                    for (var p = 0; p < n; p += _pageSize)
                    {
                        var idx = (fetchStart + p) / _pageSize;
                        var slice = new byte[_pageSize];
                        var take = Math.Min(_pageSize, n - p);
                        fetch.AsSpan(p, take).CopyTo(slice);
                        while ((long)cache.Count * _pageSize >= _fs.Options.ReadCacheBytes
                               && lru.Count > 0
                               && lru.First!.Value != idx)
                        {
                            var oldest = lru.First.Value;
                            lru.RemoveFirst();
                            cache.Remove(oldest);
                        }
                        cache[idx] = slice;
                        lru.Remove(idx);
                        lru.AddLast(idx);
                    }
                }
                // 重试该页（已入缓存且必存活）
            }
            return got;
        }

        // 无缓存直通
        return await _fs.Store.GetAsync(_key, offset, destination[..want], ct).ConfigureAwait(false);
    }

    // ═════════════════════════════ 句柄游标（D7）═════════════════════════════

    /// <inheritdoc/>
    public long Position => Volatile.Read(ref _position);

    /// <inheritdoc/>
    public long Append(ReadOnlySpan<byte> source)
    {
        ThrowIfDisposed();
        using var _gate = _fs.Maintenance.BeginMutation(nameof(Append), _path);
        if (!_writable) throw new InvalidOperationException("只读句柄不接受 Append。");
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
    /// <remarks>★ CORE-18：async 方法体持维护门闩到 await 结束（原非 async 返回 + 内部 async——
    /// using 在返回即释放，网络写仍在前行 = 维护方可与在途写并发；状态机分配原在
    /// AppendReservedAsync——挪至此处零额外分配）。</remarks>
    public async ValueTask<long> AppendAsync(ReadOnlyMemory<byte> source, CancellationToken ct)
    {
        // 预留后走异步写——门闩覆盖预留 + await 全程
        ThrowIfDisposed();
        using var _gate = _fs.Maintenance.BeginMutation(nameof(AppendAsync), _path);
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
            throw new FileIOException(IOError.IOFailure, $"{nameof(AppendAsync)} failed: {ex.Message}",
                _path, nameof(AppendAsync), ex) { ReservedOffset = reserved };
        }
    }

    /// <inheritdoc/>
    public long Seek(long offset, SeekOrigin origin)
    {
        ThrowIfDisposed();
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            SeekOrigin.End => LengthInternal() + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        Volatile.Write(ref _position, target);
        return target;
    }

    // ═════════════════════════════ 空间管理 ═════════════════════════════

    /// <inheritdoc/>
    public void Preallocate()
    {
        ThrowIfDisposed();
        if (_options.PreallocateSize <= 0) return;
        if (_options.PreallocateSize > LengthInternal())
        {
            if (_writable)
            {
                _staging!.SetLength(_options.PreallocateSize);
                _contentDirty = true;
            }
        }
    }

    /// <inheritdoc/>
    /// <remarks>写句柄 = staging 长度（实时）；读句柄 = Open 时缓存长度（不追新）。</remarks>
    public long Length => LengthInternal();

    private long LengthInternal()
        => _writable ? (_staging?.Length ?? 0) : _cachedLength;

    /// <inheritdoc/>
    /// <remarks>对象不透明整块——<c>AllocatedSize ≡ Length</c>（消费者无从得知实际存储成本，差异表声明）。</remarks>
    public long AllocatedSize => LengthInternal();

    /// <inheritdoc/>
    /// <remarks>★ AllocationUnit=1——未对齐 offset/length <b>不抛</b> AlignmentError（staging memset 无物理对齐约束）。</remarks>
    public void SetLength(long length)
    {
        ThrowIfDisposed();
        using var _gate = _fs.Maintenance.BeginMutation(nameof(SetLength), _path);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (!_writable) throw new InvalidOperationException("只读句柄不接受 SetLength。");
        _fs.QuotaProject(_path, Math.Max(_staging!.Length, length));   // G3 写前拒（截断不增——max 兜）
        _staging.SetLength(length);
        if (length < _backfillLimit)
            _backfillLimit = length;   // 截断降界——扩展回来读零（POSIX truncate-extend），不复活旧数据
        for (var i = _holes.Count - 1; i >= 0; i--)
        {
            var (hs, he) = _holes[i];
            if (hs >= length) { _holes.RemoveAt(i); continue; }           // 洞整体被截掉
            if (he > length) _holes[i] = (hs, length);                     // 洞被截尾
        }
        _contentDirty = true;
        _fs.OnFileLengthChanged(_path, length);   // AppendCursor 权威复位
    }

    /// <inheritdoc/>
    /// <remarks>仅 staging 内 memset 模拟（读零语义由 staging/对象内容保证；文件长度不变）。
    /// Flush 全量上传所有 part——★ 跳 part = 对象缩短 + 偏移错位（正确性 bug，禁止）。</remarks>
    public void PunchHole(long offset, long length)
    {
        ThrowIfDisposed();
        using var _gate = _fs.Maintenance.BeginMutation(nameof(PunchHole), _path);
        if (length <= 0) return;
        if (offset + length > LengthInternal())
            throw new FileIOException(IOError.IOFailure, "PunchHole 区间超出文件长度。", _path, "PunchHole");
        if (!_writable) throw new InvalidOperationException("只读句柄不接受 PunchHole。");
        SyncAsyncBridge.Run(ct => MaterializeComplementAsync(offset, length, punch: true, ct), s_materializeOpts);
        _staging!.Write(offset, new byte[length]);   // 全零覆写（页已物化——补集数据已在）
        _contentDirty = true;
        AddHole(offset, offset + length);   // 读路径加速簿记（内容即真相——元数据仅加速）
    }

    /// <inheritdoc/>
    /// <remarks>对象不透明——整文件单一区间（块粒度报告 = 全量）。</remarks>
    public IReadOnlyCollection<(long Start, long End)> EnumerateAllocatedRanges()
    {
        ThrowIfDisposed();
        var len = LengthInternal();
        return len <= 0 ? [] : [(0L, len)];
    }

    /// <inheritdoc/>
    public void CollapseRange(long offset, long length)
        => throw Unsupported("CollapseRange");

    /// <inheritdoc/>
    public void InsertRange(long offset, long length)
        => throw Unsupported("InsertRange");

    // ═════════════════════════════ 文件间拷贝 ═════════════════════════════

    /// <inheritdoc/>
    /// <remarks>能力位恒置位（静态实例属性）；快路径 = 目标全新 + 同 store + ≤5GB → 服务端零流量；
    /// 其余回退本地读写循环（性能≈本地拷贝）。</remarks>
    public long CopyRange(IFileHandle destination, long sourceOffset, long destinationOffset, long length)
    {
        ThrowIfDisposed();
        _fs.Maintenance.ThrowIfReadsRejected(nameof(CopyRange), _path);
        if (destination is not RemoteFileHandle dest || !ReferenceEquals(dest._fs, _fs))
            throw new ArgumentException($"CopyRange requires a remote handle destination on the same fs.", nameof(destination));
        if (sourceOffset < 0 || destinationOffset < 0 || length < 0)
            throw new ArgumentOutOfRangeException(nameof(sourceOffset));

        var available = Math.Min(length, Math.Max(0, LengthInternal() - sourceOffset));
        // 快路径：目标全新（零长度/零脏/零基线）+ 源头起对齐 → 服务端 CopyRange（真零流量）
        // ★ CORE-16：源句柄必须已 Flush（无脏页/无 staging 变更）——否则服务端拷的是已提交对象：
        //   全新对象 NotFound / 既有对象旧字节且 copied < available（静默截断）
        if (destinationOffset == 0 && dest.LengthInternal() == 0 && !dest._contentDirty && dest._baseLength == 0
            && !_contentDirty && (_staging is null || !_staging.HasDirtyPage(0, (int)((available - 1) / _pageSize)))
            && available > 0 && available <= 5L * 1024 * 1024 * 1024)
        {
            var copied = SyncAsyncBridge.Run(
                ct => _fs.Store.CopyRangeAsync(_key, dest._key, sourceOffset, available, metadata: null, ct),
                s_copyOpts);
            dest.AdoptServerSideLength(copied);
            return copied;
        }

        // 回退：本地读写循环
        long done = 0;
        try
        {
            var chunk = (int)Math.Min(available, 1 << 16);
            var buf = new byte[Math.Max(1, chunk)];
            while (done < available)
            {
                var want = (int)Math.Min(buf.Length, available - done);
                var n = Read(sourceOffset + done, buf.AsSpan(0, want));
                if (n <= 0) break;
                dest.Write(destinationOffset + done, buf.AsSpan(0, n));
                done += n;
            }
            return done;
        }
        catch (FileIOException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new FileIOException(IOError.IOFailure, $"CopyRange failed: {ex.Message}", _path, nameof(CopyRange), ex)
            { CompletedLength = done };
        }
    }

    /// <inheritdoc/>
    public long CloneRange(IFileHandle destination) => CopyRange(destination, 0, 0, LengthInternal());

    /// <summary>服务端拷贝后接管长度（目标句柄基线 = 已存在的对象）。</summary>
    internal void AdoptServerSideLength(long length)
    {
        _baseLength = length;
        _backfillLimit = length;
        _staging!.SetLength(length);
        _contentDirty = false;
    }

    // ═════════════════════════════ 向量化 IO（回退逐段——能力位不置）═════════════════════════════

    /// <inheritdoc/>
    public void WriteVector(long offset, ReadOnlySpan<ReadOnlyMemory<byte>> sources)
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
        long pos = offset;
        foreach (var s in sources.Span)
        {
            if (!s.IsEmpty) Write(pos, s.Span);
            pos += s.Length;
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public ValueTask<int> ReadVectorAsync(long offset, Memory<Memory<byte>> destinations, CancellationToken ct)
        => new(ReadVector(offset, destinations.Span));

    // ═════════════════════════════ 持久化谱系 ═════════════════════════════

    /// <summary>
    /// ★ 唯一持久化点：staging → multipart（或单 PUT）→ complete = 原子替换旧对象版本。
    /// 未触区间回填（H1）：旧对象中从未加载/写入的区间优先 UploadPartCopy（服务端零流量），
    /// 边界错位页 Range GET 补集。崩溃在 complete 之前 → 旧对象完全不受影响。
    /// </summary>
    public void Flush()
    {
        ThrowIfDisposed();
        SyncAsyncBridge.Run(ct => FlushAsync(ct), s_flushOpts);
    }

    /// <inheritdoc/>
    /// <remarks>FlushDataOnly 未置位——调用 ≡ Flush 全量回退不抛。</remarks>
    public void FlushData() => Flush();

    /// <inheritdoc/>
    /// <remarks>读句柄 Flush = no-op（无持久化义务——与 mem 平权）。
    /// ★ 真异步实现（P2 改造）：内部全 await（异步门 + 物化 + 上传），异步调用方不再被伪异步阻塞；
    ///   同步 <see cref="Flush"/> 外壳经 <see cref="SyncAsyncBridge"/> 桥接（独立池 + 有界等待）。</remarks>
    public async ValueTask FlushAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        if (!_writable) return;
        await _flushGate.WaitAsync(ct).ConfigureAwait(false);   // CA2016：转发令牌
        try
        {
            if (!_contentDirty && !_metaDirty) return;   // 无变更——no-op（幂等）

            var effectiveBase = Math.Min(_backfillLimit, _baseLength);
            var finalLength = _staging!.Length;
            var metadata = BuildMetadata();

            if (finalLength < _fs.Options.MultipartThreshold)
            {
                // 单 PUT：物化全部旧数据区间后整块上传
                await MaterializeComplementAsync(0, finalLength, punch: false, CancellationToken.None)
                    .ConfigureAwait(false);
                var body = _staging.ReadToArray(0, (int)finalLength);
                await _fs.Store.PutAsync(_key, body, metadata, ct: CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await FlushMultipartAsync(finalLength, effectiveBase, metadata).ConfigureAwait(false);
            }

            // 上传后：staging 保留为读缓存（clean——增量 Flush 判定基准）；基线前移
            _staging!.MarkAllClean();
            _baseLength = finalLength;
            _backfillLimit = finalLength;
            _contentDirty = false;
            _metaDirty = false;
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private async Task FlushMultipartAsync(long finalLength, long effectiveBase, ObjectMetadata metadata)
    {
        // part 尺寸：目标 PartSize，必要时上调满足 MaxParts 上限
        var partSize = _fs.Options.PartSize;
        var minPartSize = (finalLength + _fs.Options.MaxParts - 1) / _fs.Options.MaxParts;
        if (minPartSize > partSize)
        {
            partSize = ((minPartSize + _pageSize - 1) / _pageSize) * _pageSize;   // 页对齐
            partSize = Math.Min(partSize, 5L * 1024 * 1024 * 1024);
        }

        var session = _fs.Store.CreateMultipartUpload(_key, metadata);
        try
        {
            var partCount = (int)((finalLength + partSize - 1) / partSize);

            // 分类 pass（串行——增量 Flush 判定：part 无脏页且整体在语义有效界内 → 内容 == 已持久
            // 对象 → 服务端自拷贝（UploadPartCopy 零出口流量）；脏页/越有效界 → 物化补集后上传。
            // ★ CORE-05：判据用 effectiveBase（语义有效界——截断后扩展场景 [effectiveBase, _baseLength)
            //   的旧对象数据已按 POSIX 语义丢弃，pEnd <= _baseLength 会自拷贝"复活"已丢弃数据——
            //   该区必须走本地上传（物化补集为空 → 零页上传 → 零语义））
            var plan = new (long Start, int Len, bool ServerSideCopy)[partCount];
            for (var i = 0; i < partCount; i++)
            {
                var pStart = (long)i * partSize;
                var pLen = (int)Math.Min(partSize, finalLength - pStart);
                var pEnd = pStart + pLen;
                var firstPage = pStart / _pageSize;
                var lastPage = (pEnd - 1) / _pageSize;
                plan[i] = (pStart, pLen, !_staging!.HasDirtyPage(firstPage, lastPage) && pEnd <= effectiveBase);
            }

            // ★ 并发上传（§4.4：MaxConcurrency 节流，默认 4）——物化/staging 读取/会话上传全线程安全；
            //   Task.WhenAll 数组序 = PartNumber 升序（complete 拼接契约）
            using var throttle = new SemaphoreSlim(Math.Max(1, _fs.Options.MaxConcurrency));
            var tasks = new Task<UploadPartResult>[partCount];
            for (var i = 0; i < partCount; i++)
            {
                var idx = i;
                await throttle.WaitAsync().ConfigureAwait(false);
                tasks[idx] = Task.Run(async () =>
                {
                    try
                    {
                        var (pStart, pLen, serverSideCopy) = plan[idx];
                        if (serverSideCopy)
                            return await session.UploadPartCopyAsync(idx + 1, _key, pStart, pLen)
                                .ConfigureAwait(false);
                        // 物化 part 内缺失补集（旧数据区间），再从 staging 上传（全零 part 照常——禁止跳）
                        await MaterializeComplementAsync(pStart, pLen, punch: false, CancellationToken.None)
                            .ConfigureAwait(false);
                        var body = _staging!.ReadToArray(pStart, pLen);
                        return await session.UploadPartAsync(idx + 1, body).ConfigureAwait(false);
                    }
                    finally
                    {
                        throttle.Release();
                    }
                });
            }
            var parts = await Task.WhenAll(tasks).ConfigureAwait(false);
            await session.CompleteAsync(parts).ConfigureAwait(false);
        }
        catch
        {
            try { await session.AbortAsync().ConfigureAwait(false); } catch { /* 碎片回收失败不掩盖主异常 */ }
            throw;
        }
    }

    /// <inheritdoc/>
    /// <remarks>桥级预取模拟：Sequential → 预取窗口放大（能力位 Advise 置位）；其余 no-op。</remarks>
    public void Advise(FileAdvise advise)
    {
        ThrowIfDisposed();
        if (advise == FileAdvise.Sequential) Volatile.Write(ref _sequentialAdvise, true);   // CORE-15：跨线程可见
    }

    // ═════════════════════════════ 范围锁（G8：进程内 advisory——与 mem 同构）+ 物化映射（G11）═════════════════════════════

    /// <inheritdoc/>
    /// <remarks>G8：fs 级进程内区间表（同 owner 重叠允许 / 他 owner 排他冲突）——
    /// 仅约束同进程同 fs 实例句柄（advisory，与 FileSharing 同一诚实等级——差异声明管辖）。</remarks>
    public void Lock(long offset, long length, FileLockMode mode)
    {
        ThrowIfDisposed();
        ValidateRange(offset, length);
        _ = _fs.LockRange(_path, offset, length, mode, blocking: true, owner: this);
    }

    /// <inheritdoc/>
    public bool TryLock(long offset, long length, FileLockMode mode)
    {
        ThrowIfDisposed();
        ValidateRange(offset, length);
        return _fs.LockRange(_path, offset, length, mode, blocking: false, owner: this);
    }

    /// <inheritdoc/>
    public void Unlock(long offset, long length)
    {
        ThrowIfDisposed();
        ValidateRange(offset, length);
        _fs.UnlockRange(_path, offset, length, owner: this);
    }

    private static void ValidateRange(long offset, long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
    }

    private static FileIOException Unsupported(string op) => new(
        IOError.Unsupported, $"{op} 不受远程介质支持（能力位未置位——见 io.md 远程差异表）。", null, op);

    /// <inheritdoc/>
    /// <remarks>G11 物化映射（medium-protocol §5.12）：Read = Range GET 整段快照（纯读零写回）；
    /// ReadWrite = staging 视图（含未 Flush 写）——视图写在 Flush/Dispose 无条件写回 staging，
    /// 持久化仍由句柄 <see cref="Flush"/> 上传承担（写穿透契约与 mem Sparse 一致）。映射无只写。
    /// ★ 悬崖声明：物化成本 = 区间全量下载（GB 级 = 秒级 + 下行流量计费）——大对象随机小改经 Map 是最差姿势。</remarks>
    public IMappedSection Map(long offset, long length, AccessMode access)
    {
        ThrowIfDisposed();
        _fs.Maintenance.ThrowIfReadsRejected(nameof(Map), _path);
        AccessGate.CheckMapOpen(_fs.Access, access, _path);   // G2 包络：映射无只写 + ⊑ 挂载
        if (offset < 0 || length <= 0) throw new ArgumentOutOfRangeException(nameof(length));
        if (length > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(length), "映射长度超过 2GB 上限。");
        if (offset + length > LengthInternal())
            throw new ArgumentException($"映射区间 [{offset}, {offset + length}) 超出文件长度 {LengthInternal()}。", nameof(offset));
        if (access == AccessMode.ReadWrite && !_writable)
            throw new FileIOException(IOError.AccessDenied,
                $"ReadWrite 映射须写句柄（当前只读打开）：{_path}", _path, nameof(Map));
        var snapshot = MaterializeMapSnapshot(offset, (int)length);
        return new RemoteMappedSection(this, offset, snapshot, access == AccessMode.Read);
    }

    /// <summary>映射快照物化（G11）：写句柄 = staging 视图（补集物化后整段读出——含未 Flush 写）；
    /// 读句柄 = 单次 Range GET 整段（物化快照——纯读）。短读零兜底（外部干扰场景与 LoadRangeAsync 同族）。</summary>
    private byte[] MaterializeMapSnapshot(long offset, int length)
    {
        if (_writable)
        {
            SyncAsyncBridge.Run(ct => MaterializeComplementAsync(offset, length, punch: false, ct), s_materializeOpts);
            return _staging!.ReadToArray(offset, length);
        }
        var buf = new byte[length];
        var n = SyncAsyncBridge.Run(ct => _fs.Store.GetAsync(_key, offset, buf, ct), s_readOpts);
        if (n < length) buf.AsSpan(Math.Max(0, n)).Clear();
        return buf;
    }

    /// <summary>映射写回（G11 ReadWrite 视图的 Flush/Dispose 通道）：无条件全量写回 staging + 置脏——
    /// Memory&lt;byte&gt; 无法拦截写，脏标记不可靠（与 mem Sparse 同判）。持久化由句柄 Flush 上传承担。</summary>
    internal void WriteBackFromMap(long offset, ReadOnlySpan<byte> data)
    {
        ThrowIfDisposed();
        using var _gate = _fs.Maintenance.BeginMutation(nameof(Map), _path);
        if (!_writable) throw new InvalidOperationException("只读映射无写回。");
        _fs.QuotaProject(_path, Math.Max(_staging!.Length, offset + data.Length));
        _staging.Write(offset, data);
        _contentDirty = true;
        RemoveHole(offset, offset + data.Length);
    }

    // ═════════════════════════════ 扩展属性（PUT 原子快照语义）═════════════════════════════

    /// <inheritdoc/>
    /// <remarks>写句柄：staging 值（Flush 随 PUT 原子提交；Flush 前读 = staging 值）；
    /// 读句柄：Open 时 Head 缓存（句柄生命周期内不再重拉）。值经 Base64 往返（对象元数据为字符串域）。</remarks>
    // ═══════════════ FileExtra 平面（§3.6：staging 生命周期——写即时可见，随 Flush/PUT 原子提交）═══════════════

    /// <summary>取 FileExtra 当前值（staging 优先；缓存回退带大小写归一——AWS S3 服务端小写化键）。</summary>
    private byte[]? GetExtraBytes()
    {
        if (_writable && _xattrs.TryGetValue(FileNative.XattrName, out var staged))
            return Convert.FromBase64String(staged);
        if (_writable && _xattrs.Count > 0
            && RemoteFileSystem.TryGetUserMetadata(_xattrs, FileNative.XattrName) is { } stagedCi)
            return Convert.FromBase64String(stagedCi);
        if (!_writable)
        {
            var cached = RemoteFileSystem.TryGetUserMetadata(
                (_cachedMetadata ?? ObjectMetadata.Empty).UserMetadata, FileNative.XattrName);
            return cached is { } b64 ? Convert.FromBase64String(b64) : null;
        }
        return null;
    }

    /// <summary>FileExtra 入 staging（整替换）。</summary>
    private void StageExtra(ReadOnlySpan<byte> extra)
        => _xattrs[FileNative.XattrName] = Convert.ToBase64String(extra);

    /// <inheritdoc/>
    /// <remarks>对象用户元数据（与 fs 级 CreateFile/Stat 同平面互见）；读=staging 优先/Head 缓存。</remarks>
    public ReadOnlyMemory<byte> FileExtra
    {
        get
        {
            ThrowIfDisposed();
            return GetExtraBytes() is { } v ? v : ReadOnlyMemory<byte>.Empty;
        }
    }

    /// <inheritdoc/>
    public int ReadFileExtra(long offset, Span<byte> destination)
    {
        ThrowIfDisposed();
        _fs.Maintenance.ThrowIfReadsRejected(nameof(ReadFileExtra), _path);
        if (destination.IsEmpty || offset < 0) return 0;
        if (GetExtraBytes() is not { } blob) return 0;
        if (offset >= blob.Length) return 0;   // pread EOF 契约
        var n = (int)Math.Min(destination.Length, blob.Length - offset);
        blob.AsSpan((int)offset, n).CopyTo(destination);
        return n;
    }

    /// <inheritdoc/>
    /// <remarks>staging RMW：读当前 → patch/零扩展 → 重入 staging（即时对句柄可见，随 Flush 提交）。</remarks>
    public void WriteFileExtra(long offset, ReadOnlySpan<byte> data)
    {
        ThrowIfDisposed();
        using var _gate = _fs.Maintenance.BeginMutation(nameof(WriteFileExtra), _path);
        if (!_writable) throw new InvalidOperationException("只读句柄不接受 WriteFileExtra。");
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset + data.Length > IFileSystem.MaxFileExtraBytes)
            throw new ArgumentException($"FileExtra 超限（{offset + data.Length} > {IFileSystem.MaxFileExtraBytes}）。");
        var cur = GetExtraBytes();
        var newLen = (int)Math.Max(cur?.Length ?? 0, offset + data.Length);
        var blob = new byte[newLen];
        cur?.AsSpan().CopyTo(blob);
        data.CopyTo(blob.AsSpan((int)offset));
        StageExtra(blob);
        _metaDirty = true;
    }

    /// <inheritdoc/>
    public void SetFileExtra(ReadOnlyMemory<byte> extra)
    {
        ThrowIfDisposed();
        using var _gate = _fs.Maintenance.BeginMutation(nameof(SetFileExtra), _path);
        if (!_writable) throw new InvalidOperationException("只读句柄不接受 SetFileExtra。");
        if (extra.Length > IFileSystem.MaxFileExtraBytes)
            throw new ArgumentException($"FileExtra 超限（{extra.Length} > {IFileSystem.MaxFileExtraBytes}）。", nameof(extra));
        StageExtra(extra.Span);
        _metaDirty = true;
    }

    /// <inheritdoc/>
    /// <remarks>键字符集/2KB 超限在写入时即抛 ArgumentException（早失败——不在 Flush 的 PUT 才失败）。</remarks>
    private ObjectMetadata BuildMetadata()
    {
        var dict = new Dictionary<string, string>(_xattrs);
        // 洞元数据（读路径加速——可选；编码超 512B 丢弃该优化只留语义；内容即真相，缺失/过时无害）
        if (_holes.Count > 0)
        {
            var encoded = string.Join(";", _holes.Select(h => $"{h.Start}-{h.End}"));
            if (encoded.Length <= 512)
                dict[HoleMetadataKey] = encoded;
        }
        return dict.Count == 0 ? null! : ObjectMetadata.Create(dict);   // 总量超限在此抛——写入路径已先验键
    }

    /// <summary>洞区间记账（合并排序）。</summary>
    private void AddHole(long start, long end)
    {
        var i = 0;
        while (i < _holes.Count && _holes[i].End < start) i++;
        var s = start;
        var e = end;
        while (i < _holes.Count && _holes[i].Start <= e)
        {
            s = Math.Min(s, _holes[i].Start);
            e = Math.Max(e, _holes[i].End);
            _holes.RemoveAt(i);
        }
        _holes.Insert(i, (s, e));
    }

    /// <summary>写填洞——移除被覆盖区间。</summary>
    private void RemoveHole(long start, long end)
    {
        if (_holes.Count == 0) return;
        for (var i = _holes.Count - 1; i >= 0; i--)
        {
            var (hs, he) = _holes[i];
            if (he <= start || hs >= end) continue;
            _holes.RemoveAt(i);
            if (hs < start) _holes.Insert(i, (hs, start));
            if (he > end)
            {
                var idx = hs < start ? i + 1 : i;
                _holes.Insert(idx, (end, he));
            }
        }
    }

    /// <summary>读句柄解析洞元数据（Open 时缓存）。</summary>
    internal static List<(long Start, long End)> ParseHoles(ObjectMetadata? metadata)
    {
        if (metadata?.UserMetadata.TryGetValue(HoleMetadataKey, out var encoded) != true
            || string.IsNullOrEmpty(encoded))
            return [];
        var holes = new List<(long, long)>();
        foreach (var piece in encoded.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var dash = piece.IndexOf('-');
            if (dash <= 0) continue;
            if (long.TryParse(piece[..dash], out var s) && long.TryParse(piece[(dash + 1)..], out var e) && e > s)
                holes.Add((s, e));
        }
        return holes;
    }

    // ═════════════════════════════ 补集物化（延迟加载核心）═════════════════════════════

    /// <summary>
    /// 物化 [offset, offset+len) 所触页的<b>补集</b>（页内未触部分从旧对象按需 Range GET——限
    /// effectiveBase 内；越界部分 = 零语义）。punch=true 时排除区间本身（打洞不拉旧数据）。
    /// 纯追加句柄（写入全部 ≥ effectiveBase）零网络——快路径前置判断。
    /// </summary>
    private async ValueTask MaterializeComplementAsync(long offset, long len, bool punch, CancellationToken ct)
    {
        var effectiveBase = Math.Min(_backfillLimit, _baseLength);
        // ★ CORE-04：早退须按页取整——仅当 offset 所在整页都 ≥ effectiveBase 才免补集加载；
        //   旧判据（offset >= effectiveBase）在"旧长度非页对齐 + 追加落在尾页内"时跳过加载 →
        //   staging 缺页按全零新建 → 页内 [pageStart, effectiveBase) 的旧数据被零覆盖并上传 = 静默损坏。
        if (!punch && (offset / _pageSize) * _pageSize >= effectiveBase)
        {
            // 区间整体在旧数据有效区外（整页界）——无需加载（页分配交给 staging.Write 零填充）
            return;
        }

        var pageSize = _pageSize;
        var firstPage = offset / pageSize;
        var lastPage = (offset + len - 1) / pageSize;
        for (var p = firstPage; p <= lastPage; p++)
        {
            if (_staging!.IsPageMaterialized(p)) continue;
            var pageStart = p * pageSize;
            var pageEnd = pageStart + pageSize;

            // 该页需加载的子区间 = [pageStart, min(pageEnd, effectiveBase)) −（punch ? [offset, offset+len) : ∅）
            var loadStart = pageStart;
            var loadEnd = Math.Min(pageEnd, effectiveBase);
            if (punch)
            {
                // 排除打洞区间（可能把页加载区间切成两段）
                foreach (var (s, e) in SubtractRange(loadStart, loadEnd, offset, offset + len))
                    await LoadRangeAsync(s, e, ct).ConfigureAwait(false);
                _staging.EnsurePage(p);   // 全覆页也物化（零页——Flush 上传零而非旧数据）
            }
            else
            {
                if (loadEnd > loadStart)
                    await LoadRangeAsync(loadStart, loadEnd, ct).ConfigureAwait(false);
                _staging.EnsurePage(p);   // 页内 [effectiveBase, pageEnd) 部分保持零（截断/越界语义）
            }
        }
    }

    private async ValueTask LoadRangeAsync(long start, long end, CancellationToken ct)
    {
        var len = (int)(end - start);
        var buf = new byte[len];
        var n = await _fs.Store.GetAsync(_key, start, buf, ct).ConfigureAwait(false);
        if (n <= 0)
        {
            // 旧对象区间读空（对象被外部替换/删除——单写者协议外的干扰）：零兜底 + 计入 staging
            _staging!.EnsurePage(start / _pageSize);
            return;
        }
        _staging!.WriteClean(start, buf.AsSpan(0, n));   // 加载镜像 = 干净（增量 Flush 不因加载重传）
    }

    /// <summary>多洞扣除：[start,end) − 洞列表 → 需真实取数的分片（有序）。</summary>
    private static IEnumerable<(long Start, long End)> SubtractRangeList(long start, long end, List<(long Start, long End)> holes)
    {
        var pos = start;
        foreach (var (hs, he) in holes)
        {
            if (he <= pos || hs >= end) continue;
            if (hs > pos) yield return (pos, Math.Min(hs, end));
            pos = Math.Max(pos, he);
            if (pos >= end) yield break;
        }
        if (pos < end) yield return (pos, end);
    }

    private static IEnumerable<(long Start, long End)> SubtractRange(long start, long end, long cutStart, long cutEnd)
    {
        if (cutEnd <= start || cutStart >= end)
        {
            if (end > start) yield return (start, end);
            yield break;
        }
        if (cutStart > start) yield return (start, Math.Min(cutStart, end));
        if (cutEnd < end) yield return (Math.Max(cutEnd, start), end);
    }

    // ═════════════════════════════ 释放（池归还协议）═════════════════════════════

    /// <summary>注入文件级追加预留盒（fs.Open 调用）。</summary>
    internal void AttachAppendCursor(AppendCursor cursor) => _appendCursor = cursor;

    /// <summary>共享登记项（fs.Open 注册——关闭时注销）。</summary>
    internal SharingRegistry.Entry? SharingEntry { get; set; }

    /// <summary>句柄是否已真关闭（RemoteMappedSection 生命周期判据——池内归还不算关闭）。</summary>
    internal bool IsClosed => _disposed != 0;

    /// <inheritdoc/>
    /// <remarks>★ 按挂载分叉（第九轮）：池内 Dispose = 归还使用权（staging 随句柄留池——read-your-writes 连续性）；
    /// 池外 = 关闭（未 Flush 的 staging <b>丢弃</b>——"未 fsync 即丢"）。任何形态 Dispose 不触发上传。</remarks>
    public void Dispose()
    {
        if (_poolAttachment is { } attachment)
        {
            attachment.Pool!.OnUsageReleased(this, attachment);
            return;
        }
        CloseUnderlying();
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    internal HandlePoolAttachment? PoolAttachmentOrNull => _poolAttachment;

    HandlePoolAttachment? IPoolAttachable.PoolAttachment => _poolAttachment;

    HandlePoolAttachment IPoolAttachable.AttachPool(FileHandlePool pool)
    {
        var attachment = _poolAttachment ??= new HandlePoolAttachment();
        attachment.Pool = pool;
        return attachment;
    }

    void IPoolAttachable.CloseUnderlying() => CloseUnderlying();

    /// <summary>真关闭（池内三出口 / 池外 Dispose）——staging 丢弃 + spill 文件回收。绝不上传。</summary>
    internal void CloseUnderlying()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _fs.OnHandleClosed(this);
        _staging?.Dispose();
        _staging = null;
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed != 0, this);
}
