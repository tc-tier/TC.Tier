using System.Collections.Concurrent;
using System.IO.MemoryMappedFiles;
using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using TC.Tier.Core.IO.Image;
using TC.Tier.Core.IO.Shared;

namespace TC.Tier.Core.IO.TierVolume;

public sealed partial class TierVolumeFs
{
    // ═══════════════ IFileSystem 平面 ═══════════════

    /// <inheritdoc/>
    /// <remarks>置位（§3.5 对齐矩阵 v1 实达）：Sparse / RangeLock / RandomWrite / EmptyDirectories / DurableRename /
    /// AtomicDirectoryMove（元数据事务）/ ExclusiveLock（内建）/ MaintenanceGate / ContiguousCapture / CopyRange /
    /// VectorIO / RangeShift（全平台——增强行）/ DirectIO（两档模型：NoBuffering=绕过自管页缓存）/
    /// Advise（Sequential=页缓存预取）/ Mmap（文件载体——单区间 MMF 直映射）/
    /// WriteThrough（RM-07 接线）/ FlushDataOnly（RM-09 接线）。</remarks>
    public FileSystemCapabilities Capabilities
    {
        get
        {
            var caps = FileSystemCapabilities.Sparse
                       | FileSystemCapabilities.RangeLock
                       | FileSystemCapabilities.RandomWrite
                       | FileSystemCapabilities.EmptyDirectories
                       | FileSystemCapabilities.DurableRename
                       | FileSystemCapabilities.AtomicDirectoryMove
                       | FileSystemCapabilities.ExclusiveLock
                       | FileSystemCapabilities.MaintenanceGate
                       | FileSystemCapabilities.ContiguousCapture
                       | FileSystemCapabilities.CopyRange
                       | FileSystemCapabilities.VectorIO
                       | FileSystemCapabilities.RangeShift    // Collapse/Insert 全平台（§3.5 增强）
                       | FileSystemCapabilities.DirectIO      // 两档模型：NoBuffering=绕过自管页缓存（直达档）
                       | FileSystemCapabilities.Advise        // Advise(Sequential)=页缓存预取真行为
                       | FileSystemCapabilities.WriteThrough   // RM-07：Hints.WriteThrough=逐写日志提交（崩溃窗口归零）
                       | FileSystemCapabilities.FlushDataOnly; // RM-09：FlushData=排干+屏（数据面）≠ Flush（含日志提交）——真可区分
            if (!_carrier.IsDevice && !_snapshotMount)
                caps |= FileSystemCapabilities.Mmap;   // 文件载体：单区间 MMF 直映射（设备/快照挂载诚实不置位）
            if (_snapshotMount)
                caps &= ~FileSystemCapabilities.ContiguousCapture;   // ★ V2 §1.1：快照挂载不支持载体直视（冻结态非载体真象）
            return caps;
        }
    }

    /// <inheritdoc/>
    /// <remarks>几何自 superblock+位图推导——FreeSpace/TotalSpace 精确（§3.5 增强行；D11 溢出安全）。</remarks>
    public VolumeInfo Volume => new()
    {
        SectorSize = (int)_sb.BlockSize,
        AllocationUnit = _sb.BlockSize,
        FreeSpace = (long)Math.Min(_freeBlocks * _sb.BlockSize, long.MaxValue),
        TotalSpace = (long)Math.Min(_sb.CapacityBlocks * _sb.BlockSize, long.MaxValue),
        // §5.4 完整自描述——与 spec 协议头逐字同源
        Label = string.IsNullOrEmpty(_sb.Label) ? null : _sb.Label,
        Nature = StorageNature.Virtual,
        SubKind = _carrier.IsDevice ? "dev" : null,
        Access = _readOnly ? AccessMode.Read : _mountAccess,
        // 自动扩容卷无界（-1 与 spec quota= 同名往返）；收紧配额时 = 界本身
        QuotaBytes = _quotaCapBlocks is { } cap
            ? (long)Math.Min(cap * _sb.BlockSize, long.MaxValue)
            : _autoExpand ? -1 : (long)Math.Min(_sb.CapacityBlocks * _sb.BlockSize, long.MaxValue),
        UsedBytes = (long)Math.Min((_sb.CapacityBlocks - _freeBlocks) * _sb.BlockSize, long.MaxValue),   // 位图推导——精确
    };

    /// <summary>卷 UUID（诊断）。</summary>
    public Guid VolumeUuid => _sb.Uuid;

    /// <summary>最后检查点 LSN（V2 §1.2——增量导出基点的下界：此前的日志记录已截断，不可再导出）。</summary>
    public ulong JournalCheckpointLsn => _sb.JournalCkptLsn;

    /// <summary>最后已提交 LSN（V2 §1.2——增量导出的头上界；基点 = 此值时目标卷头部须一致）。</summary>
    public ulong JournalCommittedLsn => Volatile.Read(ref _committedLsn);

    /// <inheritdoc/>
    public IFileHandle Open(string path, FileOpenOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (options.Access == AccessMode.Read)
            _maintenance.ThrowIfReadsRejected(nameof(Open), path);
        else
        {
            if (_degraded)
                throw new FileIOException(IOError.ReadOnlyVolume,
                    "降级卷不接受写意图打开（成员缺失只读形态——RM-04 v2b；修复 = 全量成员重开）", path, nameof(Open));
            if (_readOnly)
                throw new FileIOException(IOError.ReadOnlyVolume,
                    "只读卷不接受写意图打开（ReadOnlyVolume 语义——dirty 降级形态或显式只读打开，§4.1）",
                    path, nameof(Open));
            using (_maintenance.BeginMutation(nameof(Open), path)) { }
        }
        PathValidator.ValidateRelative(path, "raw");
        options.Validate();

        lock (MetadataLock)
        {
            var exists = _entries.ContainsKey(path);
            if (!exists)
            {
                if (options.Mode == FileOpenMode.OpenExisting)
                    throw new FileIOException(IOError.NotFound, $"文件不存在: {path}", path, "Open");
                var parent = ParentOf(path);
                if (parent.Length > 0 && !_directories.Contains(parent) && !AnyEntryUnder(parent))
                    throw new FileIOException(IOError.NotFound, $"父目录不存在: {parent}", path, "Open");
                var e = new Entry { Path = path, CreatedTicks = DateTimeOffset.UtcNow.UtcTicks };
                _entries[path] = e;
                _sortedKeys.Add(path);   // RM-11 索引维护
                JnlFileCreate(path, e.CreatedTicks);
                MetadataDirty = true;
            }
            else if (options.Mode == FileOpenMode.CreateNew)
            {
                throw new FileIOException(IOError.AlreadyExists, $"文件已存在: {path}", path, "Open");
            }

            var entry = _entries[path];
            if (options.Mode == FileOpenMode.Truncate)
                TruncateEntry(entry, 0);

            var handle = new TierVolumeFileHandle(this, entry, options);
            handle.AttachSharing(_sharing, _sharing.Register(path, options.Access, options.Sharing));
            handle.AttachAppendCursor(_appendCursors.GetOrAdd(path,
                _ => new AppendCursor { Value = entry.LogicalLength }));
            return handle;
        }
    }

    /// <inheritdoc/>
    public void EnsureRoot()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var gate = _maintenance.BeginMutation(nameof(EnsureRoot), null);
        // 根恒存在（superblock 即根）——幂等 no-op（契约对齐）
    }

    /// <inheritdoc/>
    public void FlushRoot()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        lock (MetadataLock)
        {
            if (_journalOn)
            {
                JournalCommit();   // 记录屏障（sync() 语义：一切在途提交）
                // 检查点仅在有结构/时间戳变更时（每记录发射必伴随脏标记——干净卷 = 无新记录，
                // 纯屏障快道；避免 1783μs/op 的空转检查点成为引擎高频调用陷阱，RM-17 同族）
                if (MetadataDirty || _timestampsDirty)
                    CommitMetadata();   // 检查点（时间戳收口 + CkptLsn 前进）
            }
            else if (MetadataDirty || _timestampsDirty) CommitMetadata();   // sync() 语义：时间戳一并收口（数据随提交序屏障落盘）
            else FlushDirtyPages(sync: true);   // RM-40：数据-only 脏（脏页 ∪ 写绕/直达在途载体写）也须屏障——sync() 语义（旧实现仅数据脏时静默跳过）
        }
    }

    /// <inheritdoc/>
    public void CreateDirectory(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var gate = _maintenance.BeginMutation(nameof(CreateDirectory), path);
        PathValidator.ValidateRelative(path, "raw");
        lock (MetadataLock)
        {
            ThrowIfReadOnly(nameof(CreateDirectory));
            AddDirectoryRec(path);
            JnlDirCreate(path);   // 重放侧 AddDirectoryRec 补祖先——一记录一族
        }
    }

    /// <summary>目录登记（含祖先——mkdir -p 语义；在线/重放共用）。</summary>
    private void AddDirectoryRec(string path)
    {
        for (var i = path.IndexOf('/'); ; i = path.IndexOf('/', i + 1))
        {
            var dir = i < 0 ? path : path[..i];
            if (_directories.Add(dir)) MetadataDirty = true;
            if (i < 0) break;
        }
    }

    /// <inheritdoc/>
    public void DeleteDirectory(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _maintenance.ThrowIfReadsRejected(nameof(DeleteDirectory), path);
        using var gate = _maintenance.BeginMutation(nameof(DeleteDirectory), path);
        PathValidator.ValidateRelative(path, "raw");
        lock (MetadataLock)
        {
            ThrowIfReadOnly(nameof(DeleteDirectory));
            if (!DirectoryExistsInternal(path))
                throw new FileIOException(IOError.NotFound, $"目录不存在: {path}", path, nameof(DeleteDirectory));
            if (AnyEntryUnder(path))
                throw new FileIOException(IOError.DirectoryNotEmpty, $"目录非空: {path}", path, nameof(DeleteDirectory));
            if (_directories.Remove(path)) MetadataDirty = true;
            JnlDirDelete(path);
        }
    }

    /// <inheritdoc/>
    public bool DirectoryExists(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _maintenance.ThrowIfReadsRejected(nameof(DirectoryExists), path);
        PathValidator.ValidateRelative(path, "raw");
        lock (MetadataLock)
            return DirectoryExistsInternal(path);
    }

    private bool DirectoryExistsInternal(string path)
        => _directories.Contains(path) || AnyEntryUnder(path);

    private bool AnyEntryUnder(string dir)
    {
        var prefix = dir + "/";
        return KeysUnder(prefix).Any() || _directories.Any(d => d.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>前缀键视图（RM-11）：有序集合 [prefix, prefix+\uFFFF) 区间裁剪 + 精确过滤。</summary>
    private IEnumerable<string> KeysUnder(string prefix)
    {
        var upper = prefix + '\uFFFF';
        foreach (var k in _sortedKeys.GetViewBetween(prefix, upper))
        {
            if (!k.StartsWith(prefix, StringComparison.Ordinal)) continue;
            yield return k;
        }
    }

    /// <inheritdoc/>
    /// <remarks>实例内元数据事务原子（§3.5 增强行——不依赖 OS rename）。</remarks>
    public void MoveDirectory(string source, string dest)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var gate = _maintenance.BeginMutation(nameof(MoveDirectory), source);
        PathValidator.ValidateRelative(source, "raw");
        PathValidator.ValidateRelative(dest, "raw");
        lock (MetadataLock)
        {
            ThrowIfReadOnly(nameof(MoveDirectory));
            if (!DirectoryExistsInternal(source))
                throw new FileIOException(IOError.NotFound, $"源目录不存在: {source}", source, nameof(MoveDirectory));
            if (_entries.ContainsKey(dest) || DirectoryExistsInternal(dest))
                throw new FileIOException(IOError.AlreadyExists,
                    $"目标已存在: {dest}（不提供 overwrite）", dest, nameof(MoveDirectory));
            ApplyDirMove(source, dest);
            JnlDirMove(source, dest);
        }
    }

    /// <summary>目录前缀改写手术（在线/重放共用——条目 + 目录 + 追加游标）。</summary>
    private void ApplyDirMove(string source, string dest)
    {
        var srcPrefix = source + "/";
        var dstPrefix = dest + "/";
        foreach (var k in KeysUnder(srcPrefix).ToArray())
        {
            var e = _entries[k];
            _entries.Remove(k);
            e.Path = dstPrefix + k[srcPrefix.Length..];
            _entries[e.Path] = e;
            _sortedKeys.Remove(k);
            _sortedKeys.Add(e.Path);   // RM-11 索引维护
            // ★ 追加游标实例迁移（D10 修复——与 Move 同语义：跨句柄 Append 原子性不断代）
            if (_appendCursors.TryRemove(k, out var cursor))
                _appendCursors[e.Path] = cursor;
        }
        foreach (var d in _directories.Where(d => d == source || d.StartsWith(srcPrefix, StringComparison.Ordinal)).ToArray())
        {
            _directories.Remove(d);
            _directories.Add(d == source ? dest : dstPrefix + d[srcPrefix.Length..]);
        }
        MetadataDirty = true;
    }

    /// <inheritdoc/>
    public void CreateFile(string path, long preallocateSize = 0, ReadOnlyMemory<byte> extra = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var gate = _maintenance.BeginMutation(nameof(CreateFile), path);
        PathValidator.ValidateRelative(path, "raw");
        if (extra.Length > IFileSystem.MaxFileExtraBytes)
            throw new ArgumentException($"FileExtra 超限（{extra.Length} > {IFileSystem.MaxFileExtraBytes}）。", nameof(extra));
        lock (MetadataLock)
        {
            ThrowIfReadOnly(nameof(CreateFile));
            if (_entries.ContainsKey(path))
                throw new FileIOException(IOError.AlreadyExists, $"文件已存在: {path}", path, nameof(CreateFile));
            var parent = ParentOf(path);
            if (parent.Length > 0 && !_directories.Contains(parent) && !AnyEntryUnder(parent))
                throw new FileIOException(IOError.NotFound, $"父目录不存在: {parent}", path, nameof(CreateFile));
            var e = new Entry
            {
                Path = path,
                CreatedTicks = DateTimeOffset.UtcNow.UtcTicks,
                Extra = extra.ToArray(),
            };
            _entries[path] = e;
            _sortedKeys.Add(path);   // RM-11 索引维护
            JnlFileCreate(path, e.CreatedTicks);
            if (extra.Length > 0) JnlSetExtra(path, e.Extra);
            if (preallocateSize > 0) PreallocateEntry(e, preallocateSize);
            MetadataDirty = true;
        }
    }

    /// <inheritdoc/>
    public bool Exists(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _maintenance.ThrowIfReadsRejected(nameof(Exists), path);
        PathValidator.ValidateRelative(path, "raw");
        lock (MetadataLock)
            return _entries.ContainsKey(path);
    }

    /// <inheritdoc/>
    public void Delete(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var gate = _maintenance.BeginMutation(nameof(Delete), path);
        PathValidator.ValidateRelative(path, "raw");
        lock (MetadataLock)
        {
            ThrowIfReadOnly(nameof(Delete));
            // ★ 打开句柄在档拒删（RM-12 读锁外快照 + 块即时回收的组合下，唯一安全语义——
            //   否则锁外读者会读到已回收/已重分配的块）
            if (_sharing.HasOpenHandles(path))
                throw new FileIOException(IOError.SharingViolation,
                    $"文件有打开句柄在档，拒绝删除：{path}（关闭全部句柄后重试——§4.1 块回收安全语义）",
                    path, nameof(Delete));
            if (!_entries.Remove(path, out var e))
                return;   // 幂等（POSIX unlink 对齐）
            _sortedKeys.Remove(path);   // RM-11 索引维护
            JnlFileDelete(path);
            WaitWritersIdle(e);   // ★ CORE-02：等本文件在途写者出数据段（写者计数钉块——数据段锁外不碰锁，自旋有界）
            foreach (var x in e.Extents)
            {
                var blocks = (uint)((x.Length + _pageSize - 1) / _pageSize);
                ReleaseBlocksFrozenAware(x.PhysicalBlock, blocks);   // ★ V2 §1.1：快照冻结块保持 used（钉块）
                TrimCarrierBlocks(x.PhysicalBlock, blocks);   // RM-05 + V2 §1.3：无句柄在档=无读者——即时回收点（冻结段内跳过）
                InvalidateCacheBlocks(x.PhysicalBlock, blocks);   // RM-12：删除释放退出缓存（缓存失效不伤快照——数据在载体）
            }
            _appendCursors.TryRemove(path, out _);
            MetadataDirty = true;
        }
    }

    /// <inheritdoc/>
    public void Move(string source, string dest, bool overwrite = false)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var gate = _maintenance.BeginMutation(nameof(Move), source);
        PathValidator.ValidateRelative(source, "raw");
        PathValidator.ValidateRelative(dest, "raw");
        lock (MetadataLock)
        {
            ThrowIfReadOnly(nameof(Move));
            if (!_entries.TryGetValue(source, out var e))
                throw new FileIOException(IOError.NotFound, $"源文件不存在: {source}", source, nameof(Move));
            if (_entries.TryGetValue(dest, out var old))
            {
                if (!overwrite)
                    throw new FileIOException(IOError.AlreadyExists, $"目标已存在: {dest}", dest, nameof(Move));
                // ★ 覆盖目标有打开句柄在档 → 拒绝（与 Delete 同语义——被覆盖条目即时回收物理块）
                if (_sharing.HasOpenHandles(dest))
                    throw new FileIOException(IOError.SharingViolation,
                        $"覆盖目标有打开句柄在档，拒绝移动覆盖：{dest}（关闭全部句柄后重试）",
                        dest, nameof(Move));
                WaitWritersIdle(old);   // ★ CORE-02：等目标文件在途写者出数据段（写者计数钉块）
                foreach (var x in old.Extents)
                {
                    var blocks = (uint)((x.Length + _pageSize - 1) / _pageSize);
                    ReleaseBlocksFrozenAware(x.PhysicalBlock, blocks);   // ★ V2 §1.1：快照冻结块保持 used（钉块）
                    TrimCarrierBlocks(x.PhysicalBlock, blocks);   // RM-05 + V2 §1.3：覆盖释放（句柄已查在档——即时回收；冻结段内跳过）
                    InvalidateCacheBlocks(x.PhysicalBlock, blocks);   // RM-12：覆盖释放退出缓存
                }
                _entries.Remove(dest);
                _sortedKeys.Remove(dest);   // RM-11 索引维护
            }
            _entries.Remove(source);
            e.Path = dest;
            _entries[dest] = e;
            _sortedKeys.Remove(source);
            _sortedKeys.Add(dest);   // RM-11 索引维护
            // ★ 追加游标实例迁移（D10 修复）：源句柄与新开目标句柄共享同一游标——移动前后跨句柄 Append 原子性不断代
            _appendCursors.TryRemove(dest, out _);
            if (_appendCursors.TryRemove(source, out var cursor))
                _appendCursors[dest] = cursor;
            JnlFileMove(source, dest, overwrite);
            MetadataDirty = true;
        }
    }

    /// <inheritdoc/>
    public FsEntryInfo Stat(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _maintenance.ThrowIfReadsRejected(nameof(Stat), path);
        PathValidator.ValidateRelative(path, "raw");
        lock (MetadataLock)
        {
            if (_entries.TryGetValue(path, out var e))
                return new FsEntryInfo(FsEntryType.File, path, e.LogicalLength,
                    new DateTimeOffset(e.ModifiedTicks, TimeSpan.Zero),
                    new DateTimeOffset(e.CreatedTicks, TimeSpan.Zero), e.Extra);
            if (DirectoryExistsInternal(path))
                return new FsEntryInfo(FsEntryType.Directory, path, 0,
                    DateTimeOffset.MinValue, null, ReadOnlyMemory<byte>.Empty);
            throw new FileIOException(IOError.NotFound, $"条目不存在: {path}", path, nameof(Stat));
        }
    }

    /// <inheritdoc/>
    public IEnumerable<FsEntry> EnumerateFiles(string pattern = "*", bool recursive = false)
        => EnumerateCore(null, pattern, recursive, EntryFilter.Files);

    /// <inheritdoc/>
    public IEnumerable<FsEntry> EnumerateFiles(string path, string pattern, bool recursive = false)
        => EnumerateCore(path, pattern, recursive, EntryFilter.Files);

    /// <inheritdoc/>
    public IEnumerable<FsEntry> EnumerateDirectories(string pattern = "*", bool recursive = false)
        => EnumerateCore(null, pattern, recursive, EntryFilter.Directories);

    /// <inheritdoc/>
    public IEnumerable<FsEntry> EnumerateDirectories(string path, string pattern, bool recursive = false)
        => EnumerateCore(path, pattern, recursive, EntryFilter.Directories);

    /// <inheritdoc/>
    public IEnumerable<FsEntry> EnumerateEntries(string pattern = "*", bool recursive = false)
        => EnumerateCore(null, pattern, recursive, EntryFilter.Both);

    /// <inheritdoc/>
    public IEnumerable<FsEntry> EnumerateEntries(string path, string pattern, bool recursive = false)
        => EnumerateCore(path, pattern, recursive, EntryFilter.Both);

    private enum EntryFilter { Files, Directories, Both }

    private List<FsEntry> EnumerateCore(string? path, string pattern, bool recursive, EntryFilter filter)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _maintenance.ThrowIfReadsRejected("Enumerate", path);
        PathPattern.Validate(pattern);
        lock (MetadataLock)
        {
            var prefix = path is null ? "" : path + "/";
            if (path is not null && !DirectoryExistsInternal(path))
                throw new FileIOException(IOError.NotFound, $"目录不存在: {path}", path, "Enumerate");
            var showHidden = PathPattern.HiddenExempt(pattern);

            // RM-11 遗留收口：_sortedKeys 有序不变量（契约锁定）→ Files 族按键序直出免排序；
            // Both 族 = 文件序 + 目录序双有序线性归并（替代全量 O(n log n) 排序——消费方有序契约不变）
            var filesSorted = new List<FsEntry>();
            if (filter != EntryFilter.Directories)
                foreach (var k in KeysUnder(prefix.Length == 0 ? "" : prefix))
                {
                    var e = _entries[k];
                    var rest = k[prefix.Length..];
                    if (rest.Length == 0) continue;
                    if (!recursive && rest.Contains('/')) continue;
                    if (!showHidden && PathPattern.IsHiddenRelative(rest)) continue;
                    if (!PathPattern.IsMatch(LastComponent(rest), pattern)) continue;
                    filesSorted.Add(new FsEntry(FsEntryType.File, rest, e.LogicalLength,
                        new DateTimeOffset(e.ModifiedTicks, TimeSpan.Zero),
                        new DateTimeOffset(e.CreatedTicks, TimeSpan.Zero)));
                }
            if (filter == EntryFilter.Files)
                return filesSorted;

            var dirsSorted = new List<FsEntry>();
            {
                var dirs = new HashSet<string>(StringComparer.Ordinal);
                foreach (var d in _directories)
                {
                    if (d.Length == 0 || !d.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    var rest = d[prefix.Length..];
                    if (rest.Length == 0) continue;
                    if (!recursive && rest.Contains('/')) continue;
                    dirs.Add(d);
                }
                foreach (var k in _entries.Keys)
                {
                    if (!k.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    var rest = k[prefix.Length..];
                    var slash = rest.IndexOf('/');
                    if (slash <= 0) continue;
                    if (recursive)
                        for (var i = rest.IndexOf('/'); i >= 0; i = rest.IndexOf('/', i + 1))
                            dirs.Add(prefix + rest[..i]);
                    else
                        dirs.Add(prefix + rest[..slash]);
                }
                foreach (var d in dirs)
                {
                    var name = d[prefix.Length..];
                    if (!showHidden && PathPattern.IsHiddenRelative(name)) continue;
                    if (!PathPattern.IsMatch(LastComponent(name), pattern)) continue;
                    dirsSorted.Add(new FsEntry(FsEntryType.Directory, name, 0, DateTimeOffset.MinValue, null));
                }
                dirsSorted.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
            }
            if (filter == EntryFilter.Directories)
                return dirsSorted;

            // 双有序线性归并（files/dirs 各自按 Name 有序）
            var merged = new List<FsEntry>(filesSorted.Count + dirsSorted.Count);
            var fi = 0;
            var di = 0;
            while (fi < filesSorted.Count && di < dirsSorted.Count)
                merged.Add(string.CompareOrdinal(filesSorted[fi].Name, dirsSorted[di].Name) <= 0
                    ? filesSorted[fi++] : dirsSorted[di++]);
            while (fi < filesSorted.Count) merged.Add(filesSorted[fi++]);
            while (di < dirsSorted.Count) merged.Add(dirsSorted[di++]);
            return merged;
        }
    }

    private static string ParentOf(string path)
    {
        var last = path.LastIndexOf('/');
        return last < 0 ? "" : path[..last];
    }

    private static ReadOnlySpan<char> LastComponent(string path)
    {
        var last = path.LastIndexOf('/');
        return last < 0 ? path.AsSpan() : path.AsSpan()[(last + 1)..];
    }

    /// <inheritdoc/>
    /// <remarks>内建（§3.5 增强行）：实例打开即排他（一卷一实例）——恒成功。</remarks>
    public IDisposable AcquireExclusive(TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        return NoOpLease.Instance;
    }

    /// <inheritdoc/>
    public IDisposable EnterMaintenance(string reason, MaintenanceScope scope, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        return _maintenance.Enter(reason, scope, ct);
    }

    /// <summary>挂载访问三态（G2——TierVolumeFileHandle.Map 经此过包络校验）。</summary>
    internal AccessMode Access => _readOnly ? AccessMode.Read : _mountAccess;

    /// <summary>维护门闩（句柄写族的拒绝入口——TierVolumeFileHandle 经此访问）。</summary>
    internal MaintenanceGate Maintenance => _maintenance;

    /// <summary>是否已 Dispose（TierVolumeFileHandle 生命周期语义——fs 关闭后句柄操作统一抛 ObjectDisposedException，
    /// 与 Mem"拔盘"契约对齐：卷状态随实例销毁，句柄静默内存成功 = 永不持久化的假象，必须显式失败）。</summary>
    internal bool IsDisposed => _disposed != 0;

    /// <summary>驻留页计数（测试观测——预取/逐出行为断言用，CrashSimulate 同款后门）。</summary>
    internal int ResidentPageCount => _pages.Count;

    /// <summary>载体在途写字节（测试观测——RM-40 Flush 屏障语义断言：覆写/写绕直落后 &gt; 0，Flush/FlushData 后归零）。</summary>
    internal long CarrierWritePendingBytes => Volatile.Read(ref _carrierWritePendingBytes);

    /// <summary>句柄变异入口（维护门闩 + 生命周期双门）。</summary>
    internal MaintenanceGate.MutationScope BeginHandleMutation(string operation, string path)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        return _maintenance.BeginMutation(operation, path);
    }

    /// <summary>句柄读入口（维护 scope=All 拒绝 + 生命周期双门）。</summary>
    internal void ThrowIfHandleReadsRejected(string operation, string path)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _maintenance.ThrowIfReadsRejected(operation, path);
    }

    /// <summary>读者进入保护域（D1b——TierVolumeFileHandle.Read 包夹快照捕获与读取；回收延迟至本读者退出）。</summary>
    internal void EnterReadEpoch() => _readEpoch.Resume();

    /// <summary>读者退出保护域（与 EnterReadEpoch 同线程严格配对）。</summary>
    internal void ExitReadEpoch() => _readEpoch.Suspend();

    /// <summary>是否设备载体（Map 能力判别——设备形态诚实不支持）。</summary>
    internal bool IsDeviceCarrier => _carrier.IsDevice;

}
