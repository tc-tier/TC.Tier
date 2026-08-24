using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TC.Tier.Core.IO.Shared;

namespace TC.Tier.Core.IO.Mem;

/// <summary>
/// 内存文件系统——path-keyed 中立内存介质（IFileSystem 平权实现）。
/// <para>★ 构造：<see cref="Default"/> 全局单例（稀疏、不限容量、Dispose=no-op）/
///   <see cref="New(MemoryFileSystemOptions?, ILogger?)"/> 私有卷（Capacity 配额 + Sparse/Reserved 分配模式）。
///   ★ 测试隔离纪律：需要隔离的测试一律 New() 私有卷——Default 的路径空间全局共享。</para>
/// <para>★ 身份：path 唯一（无数字 id）。open 时 path→(槽, 代际) 解析一次，热路径零字典开销。</para>
/// <para>★ 内部并发模型（三窗口防护，D5）：
///   在途读者（微秒级）= per-fs <see cref="LightEpoch"/>（Reserved 模式数据面锁外拷贝）；
///   长活观察者（句柄/映射）= 槽引用计数 + Detached 延迟回收 + SlotData 引用计数（直址映射钉住旧 buffer）；
///   槽身份复用 = 代际 bump（ABA 防护，双检 + 空检）；
///   写者×替换（Grow 换租）= freeze 两步替换屏障（epoch 的盲区——写者不进 epoch）。
///   Sparse 模式数据面 = per-file Gate（<see cref="SparseLayout"/> 自带锁——不同文件并行、同文件互斥）：
///   ① 锁序不变式：唯一合法嵌套 = fs._lock → Gate（单向），持 Gate 禁取 fs._lock（推长度先出 Gate）；
///   ② 可见性顺序：写者"页先就位（Gate 内）→ 长度后发布（fs 锁）"——读者见新长度 ⇒ 页必已可读；
///   ③ 在途 IO × 回收：数据面持 Gate 后代际+layout 引用双检；回收方 fs 锁内摘身份后空获取 Gate
///     （DrainLayoutGate）等在途退出才还页（页级进一步无锁化/CAS 页分配位仍为后续方向）。</para>
/// <para>★ Dispose = 拔盘（与磁盘"离开目录"方向相反）：全部槽归还池（含 Detached），此后句柄操作抛
///   <see cref="ObjectDisposedException"/>。</para>
/// </summary>
public sealed unsafe class MemoryFileSystem : IFileSystem
{
    internal enum SlotState : byte
    {
        Empty = 0,
        Live,
        /// <summary>路径项已摘（Delete/Move 覆盖/Detach），仍有观察者（句柄/映射）——数据延迟到最后观察者关闭。</summary>
        Detached,
    }

    /// <summary>
    /// 槽数据快照（不可变）——(Ptr, Size) 打包单引用原子换——根治读者成员撕裂（旧 Ptr 配新 Size = 越界读）。
    /// 引用计数守直址映射（视图钉住旧 buffer，Grow 换租后旧 buffer 延迟到最后 unmap 才归还）。
    /// </summary>
    internal sealed class SlotData(byte[]? buffer, byte* ptr, long size)
    {
        public readonly byte[]? Buffer = buffer;     // Reserved：）池租借单块（强引用防 GC
        public readonly byte* Ptr = ptr;          // Reserved：Buffer 固定指针
        public readonly long Size = size;          // 逻辑长度（两模式共用——Sparse 下 Buffer/Ptr 为 null）
        public int Refs;                    // 活跃直址映射数
        public bool Retired;                // 已从槽摘除（归还条件：epoch drain 过 + Refs==0）
    }

    /// <summary>Sparse 页表（页号 → 池租借页）。</summary>
    internal sealed class SparseLayout(int pageSize)
    {
        public readonly Dictionary<long, byte[]> Pages = [];
        public readonly int PageSize = pageSize;

        /// <summary>数据面读写锁（per-file）——页表路由/页内容访问的唯一锁（不同文件并行、同文件读共享写独占）。
        /// ★ SpinRWLock 写偏向 CAS RW 原语（自裸 monitor 升级，自 LockWord 换型）：
        ///   读共享——并发读者互不阻塞、高频写者下读者不再饥饿（裸 monitor 无公平性，17GB/s 写者轮转
        ///   lock/release 每次压过排队读者——混合负载读者饿死实测 0.2M ops/s，升级后同形态 40M+）；
        ///   写/回收独占——页表结构与页归属变更互斥；写偏向（pending 位挡新读者）保证写者不被持续读者流饿死。
        /// ★ 锁序不变式：唯一合法嵌套 = fs._lock → Gate（单向）；持 Gate 期间禁止获取 fs._lock（推长度先出 Gate）。
        /// ★ 回收屏障：空获取排他（AcquireExclusive→ReleaseExclusive）= 等全部在途读写退出（原 DrainLayoutGate 语义）。</summary>
        internal readonly SpinRWLock Gate = new();
    }

    /// <summary>
    /// 槽（★ CORE-21：显式布局——热字段（State/Gen/RefCount/Data/Layout）独占首缓存行（0-63）；
    /// ModifiedTicks 独立第二行（80——写者每写污染的是自己的冷字段行，不与热字段/相邻槽热区同线）。
    /// Size=128 = 每槽两整缓存行——相邻槽热区（128-191）与本品 ModifiedTicks 行（64-127）分离。
    /// 热路径：读者只碰 0-31（Data/Layout/Gen）——多文件并发写读零乒乓。
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 128)]
    internal struct FileSlot
    {
        [FieldOffset(0)] public SlotState State;
        [FieldOffset(4)] public int Generation;                          // 槽复用代际（回收时 +1——ABA 防护）
        [FieldOffset(8)] public int RefCount;                            // 打开句柄数 + 活跃映射数（Detached→回收门槛）
        [FieldOffset(16)] public SlotData? Data;                         // 原子快照（Reserved 直址 / Sparse 仅 Size）
        [FieldOffset(24)] public SparseLayout? Layout;                   // Sparse 页表（与 Data.Buffer 互斥）
        [FieldOffset(32)] public byte[]? FileExtra;                      // FileExtra 平面单 blob（≤1.5K）
        [FieldOffset(40)] public List<OpenRegistryEntry>? OpenRegistry;  // 打开注册表（advisory）
        [FieldOffset(48)] public RangeLockTable? Locks;
        [FieldOffset(56)] public List<WeakReference>? MaterializedMaps;  // Sparse 物化映射
        [FieldOffset(64)] public List<(long Start, long End)>? Ranges;   // Reserved 记账区间
        [FieldOffset(72)] public long CreatedTicks;                      // 创建时间（UtcNow ticks——Stat/枚举）
        [FieldOffset(80)] public long ModifiedTicks;                     // 修改时间（写/截断/整理路径推进）——独立行（CORE-21）
    }

    /// <summary>已摘除的文件负载（DetachFile/InstallFile 转移——compactor promote 原语）。</summary>
    public sealed class DetachedFile
    {
        internal readonly byte[]? Buffer;
        internal readonly long Size;
        internal readonly List<(long Start, long End)>? Ranges;
        internal readonly Dictionary<long, byte[]>? Pages;
        internal readonly int PageSize;

        internal DetachedFile(byte[]? buffer, long size, List<(long Start, long End)>? ranges,
            Dictionary<long, byte[]>? pages, int pageSize)
        {
            Buffer = buffer;
            Size = size;
            Ranges = ranges;
            Pages = pages;
            PageSize = pageSize;
        }
    }

    // ═══════════════ 实例状态 ═══════════════

    /// <summary>全局唯一共享实例——"永远在的那个盘"（稀疏、不限容量、Dispose=no-op）。★ 路径空间全局共享——测试隔离用 <see cref="New(MemoryFileSystemOptions?, ILogger?)"/>。</summary>
    public static MemoryFileSystem Default { get; } = new(new MemoryFileSystemOptions(), isDefault: true);

    private readonly MemoryFileSystemOptions _options;
    private readonly bool _isDefault;
    private readonly ILogger? _logger;
    private readonly object _lock = new();                          // 元数据面锁（结构操作 + Sparse 数据面）
    private readonly Dictionary<string, int> _paths = new(StringComparer.Ordinal);   // mem 区分大小写对齐 Linux
    private readonly HashSet<string> _directories = new(StringComparer.Ordinal);     // 显式目录集合（根空间层级；mkdir -p 登记，含祖先）
    private readonly ConcurrentDictionary<string, AppendCursor> _appendCursors = new();   // 文件级追加预留
    private readonly Stack<int> _freeSlots = new();
    private FileSlot[] _slots = new FileSlot[16];
    private readonly PinnedBufferPool _pool = new();
    private readonly LightEpoch _epoch = new();                     // ★ per-fs 实例——A 卷 drain 不等 B 卷读者
    private int _freeze;                                             // 替换屏障（per-fs）
    private int _writersInFlight;                                    // 屏障内写者计数
    private long _physicalUsage;                                     // 配额计费（物理占用口径）
    private int _disposed;
    private readonly AccessMode _access;                             // 挂载访问三态（G2 总上包络）
    private readonly string? _label;                                 // G1：卷标签（易失介质——记录即卷对象字段）
    private IDisposable? _mountExclusiveLease;                       // G5：构造期排他租约（Dispose 释放）
    private readonly MaintenanceGate _maintenance = new();   // 根空间维护门闩（设计 §8——三介质共享核心件）

    /// <summary>维护门闩（句柄写族的拒绝入口——MemFileHandle 经此访问）。</summary>
    internal MaintenanceGate Maintenance => _maintenance;

    /// <summary>挂载访问三态（G2——MemFileHandle.Map 经此过包络校验）。</summary>
    internal AccessMode Access => _access;

    /// <summary>路径校验（层级相对路径——与三介质同一规则）。</summary>
    private static void ValidatePath(string path) => PathValidator.ValidateRelative(path, "mem");

    private MemoryFileSystem(MemoryFileSystemOptions options, bool isDefault, ILogger? logger = null)
    {
        _options = options;
        _isDefault = isDefault;
        _logger = logger;
        _access = options.Access;
        _label = options.Label;
        if (options.Exclusive)
            _mountExclusiveLease = AcquireExclusive(TimeSpan.FromSeconds(30));   // G5：进程内真锁
        if (_options.PageSize is < 512 or > (1 << 24) || (_options.PageSize & (_options.PageSize - 1)) != 0)
            throw new ArgumentException($"PageSize 必须为 2 的幂且在 [512, 16M]：{options.PageSize}");
    }

    /// <summary>New——创建新卷（独立"卷"，私有路径空间——测试隔离的正确姿势）。New/Open 同形（内存无存在性概念，§2.3）。</summary>
    public static MemoryFileSystem New(MemoryFileSystemOptions? options = null, ILogger? logger = null)
        => new(options ?? new MemoryFileSystemOptions(), isDefault: false, logger);

    /// <summary>Open——New 同形（既非动词差异；保留双名仅为调用点语义可读）。</summary>
    public static MemoryFileSystem Open(MemoryFileSystemOptions? options = null, ILogger? logger = null)
        => New(options, logger);

    // ═══════════════ IFileSystem 平面 ═══════════════

    /// <summary>磁盘模拟逻辑扇区（512）——DIO 三重对齐的几何基准（Volume.SectorSize 同源）。</summary>
    internal const int SimulatedSectorSize = 512;

    /// <inheritdoc/>
    public FileSystemCapabilities Capabilities
    {
        get
        {
            var caps = FileSystemCapabilities.RangeLock           // 进程内区间表（真实生效）
                       | FileSystemCapabilities.Mmap
                       | FileSystemCapabilities.RangeShift          // memmove 全支持
                       | FileSystemCapabilities.RandomWrite         // 槽直址/页路由随机覆写无代价
                       | FileSystemCapabilities.DurableRename       // 锁内原子
                       | FileSystemCapabilities.EmptyDirectories    // 显式目录集合（根空间）
                       | FileSystemCapabilities.AtomicDirectoryMove   // 锁内批量 re-key 原子
                       | FileSystemCapabilities.MaintenanceGate        // 维护门闩（设计 §8——三介质统一）
                       | FileSystemCapabilities.ExclusiveLock;         // 进程内真卷锁（补全）
            if (_options.Allocation == MemoryAllocationMode.Sparse)
                caps |= FileSystemCapabilities.Sparse;               // 真稀疏：PunchHole 真物理回收
            return caps;
        }
    }

    /// <inheritdoc/>
    /// <remarks>mem 磁盘模拟几何（）：SectorSize=512（逻辑扇区——DIO 对齐基准，与 Linux 块设备
    /// 最广泛形态一致）；AllocationUnit=PageSize（空间操作对齐基准——页是分配粒度不是对齐基，两码事）。</remarks>
    public VolumeInfo Volume => new()
    {
        SectorSize = SimulatedSectorSize,
        AllocationUnit = _options.PageSize,
        FreeSpace = _options.QuotaBytes > 0 ? Math.Max(0, _options.QuotaBytes - Volatile.Read(ref _physicalUsage)) : -1,
        TotalSpace = _options.QuotaBytes > 0 ? _options.QuotaBytes : -1,
        // §5.4 完整自描述——易失介质的记录即卷对象字段
        Label = _label,
        Nature = StorageNature.Memory,
        Access = _access,
        QuotaBytes = _options.QuotaBytes,
        UsedBytes = Volatile.Read(ref _physicalUsage),   // 物理占用口径（与配额执法同源）
    };

    /// <inheritdoc/>
    public IFileHandle Open(string path, FileOpenOptions options)
    {
        ThrowIfDisposed();
        // 维护门闩：写意图打开按变异拒绝（All 档连读意图一并拒）——句柄打开本身是原子的，立即退出在途计数
        if (options.Access == AccessMode.Read)
            _maintenance.ThrowIfReadsRejected(nameof(Open), path);
        else
            using (_maintenance.BeginMutation(nameof(Open), path)) { }
        ValidatePath(path);
        options.Validate();
        AccessGate.CheckHandleOpen(_access, options.Access, path);   // G2 包络：构造期 fail-fast

        lock (_lock)
        {
            // ★ 路径被目录占用——对齐磁盘（EISDIR/AccessDenied）：打开目录当文件必失败。
            if (_directories.Contains(path) || AnyEntryUnderNoLock(path))
                throw new FileIOException(IOError.AccessDenied, $"路径是目录，无法打开为文件: {path}", path, "Open");
            if (!_paths.TryGetValue(path, out var slotIdx))
            {
                if (options.Mode == FileOpenMode.OpenExisting)
                    throw new FileIOException(IOError.NotFound, $"文件不存在: {path}", path, "Open");
                // 层级路径：父目录须存在（对齐 disk ENOENT 语义——防止静默隐式建目录）
                var parent = ParentOf(path);
                if (parent.Length > 0 && !_directories.Contains(parent) && !AnyEntryUnderNoLock(parent))
                    throw new FileIOException(IOError.NotFound, $"父目录不存在: {parent}", path, "Open");
                slotIdx = CreateFileNoLock(path, 0);   // OpenOrCreate/CreateNew(空目标)/Append/Truncate 建空文件
            }
            else if (options.Mode == FileOpenMode.CreateNew)
            {
                throw new FileIOException(IOError.AlreadyExists, $"文件已存在: {path}", path, "Open");
            }

            ref var slot = ref _slots[slotIdx];
            if (options.Mode == FileOpenMode.Truncate)
                TruncateNoLock(ref slot, 0);

            CheckSharingNoLock(ref slot, options);

            slot.RefCount++;
            var entry = new OpenRegistryEntry(
                Sharing: options.Sharing,
                NeedsRead: options.Access is AccessMode.Read or AccessMode.ReadWrite,
                NeedsWrite: options.Access is AccessMode.Write or AccessMode.ReadWrite);
            var handle = new MemFileHandle(this, path, slotIdx, slot.Generation, options, entry);
            (slot.OpenRegistry ??= []).Add(entry);
            // 文件级追加预留：open 时解析盒引用（初值 = 打开时逻辑长度——预分配已在 ctor 内生效）
            var initialCursor = slot.Data?.Size ?? 0;
            handle.AttachAppendCursor(_appendCursors.GetOrAdd(path,
                _ => new AppendCursor { Value = initialCursor }));
            return handle;
        }
    }

    /// <summary>双向共享兼容检查（BCL 语义）：新开的 access 须被所有已有 sharing 允许；已有 access 须被新开 sharing 允许。仅同 fs 实例（advisory 本质）。</summary>
    private static void CheckSharingNoLock(ref FileSlot slot, FileOpenOptions options)
    {
        if (slot.OpenRegistry is not { Count: > 0 } registry) return;
        var needsRead = options.Access is AccessMode.Read or AccessMode.ReadWrite;
        var needsWrite = options.Access is AccessMode.Write or AccessMode.ReadWrite;
        var mine = options.Sharing;
        foreach (var open in registry)
        {
            if (needsWrite && (open.Sharing & FileSharing.Write) == 0)
                throw SharingViolation(open.Sharing, "写");
            if (needsRead && (open.Sharing & FileSharing.Read) == 0)
                throw SharingViolation(open.Sharing, "读");
            if (open.NeedsWrite && (mine & FileSharing.Write) == 0)
                throw SharingViolation(mine, "写（反向：已有句柄需要写权限）");
            if (open.NeedsRead && (mine & FileSharing.Read) == 0)
                throw SharingViolation(mine, "读（反向：已有句柄需要读权限）");
        }

        return;

        static FileIOException SharingViolation(FileSharing sharing, string what) => new(
            IOError.SharingViolation,
            $"共享冲突：Sharing={sharing} 不允许{what}打开（advisory：仅同 fs 实例内生效，跨进程互斥用卷锁）。",
            null, "Open");
    }

    /// <inheritdoc/>
    public void EnsureRoot()
    {
        AccessGate.RejectWrite(_access, nameof(EnsureRoot));
        ThrowIfDisposed();   // mem 无根目录概念——幂等 no-op（契约对齐）
    }

    /// <inheritdoc/>
    public void FlushRoot()
    {
        AccessGate.RejectWrite(_access, nameof(FlushRoot));
        ThrowIfDisposed();   // mem 无持久化——no-op
    }

    /// <inheritdoc/>
    public bool Exists(string path)
    {
        ThrowIfDisposed();
        _maintenance.ThrowIfReadsRejected(nameof(Exists), path);
        ValidatePath(path);
        lock (_lock)
            return _paths.ContainsKey(path);
    }

    /// <inheritdoc/>
    public void Delete(string path)
    {
        ThrowIfDisposed();
        AccessGate.RejectWrite(_access, nameof(Delete));
        using var gate = _maintenance.BeginMutation(nameof(Delete), path);
        ValidatePath(path);
        int slotIdx;
        lock (_lock)
        {
            if (!_paths.Remove(path, out slotIdx)) return;   // 幂等（POSIX unlink 对不存在路径仍成功——本层对齐 File.Delete）
            _slots[slotIdx].State = SlotState.Detached;       // 名字即摘（Exists=false）；数据延迟到最后观察者关闭
            _appendCursors.TryRemove(path, out _);             // 追加预留盒摘除（重建同路径时按新 Length 重解析）
        }
        TryRetireSlot(slotIdx);
        FileDeleted?.Invoke(path);
    }

    /// <inheritdoc/>
    public void Move(string source, string dest, bool overwrite = false)
    {
        ThrowIfDisposed();
        AccessGate.RejectWrite(_access, nameof(Move));
        using var gate = _maintenance.BeginMutation(nameof(Move), source);
        ValidatePath(source);
        ValidatePath(dest);
        int retiredIdx = -1;
        lock (_lock)
        {
            if (!_paths.TryGetValue(source, out var srcIdx))
                throw new FileIOException(IOError.NotFound, $"源文件不存在: {source}", source, nameof(Move));
            if (_paths.TryGetValue(dest, out var dstIdx))
            {
                if (!overwrite)
                    throw new FileIOException(IOError.AlreadyExists, $"目标已存在: {dest}", dest, nameof(Move));
                // 覆盖：旧 dst → Detached（旧句柄/视图继续读旧数据——POSIX rename 覆盖：旧 inode 延迟到 close）
                _paths.Remove(dest);
                _slots[dstIdx].State = SlotState.Detached;
                retiredIdx = dstIdx;
            }
            // 字典项原子交换：槽跟数据走——已打开句柄的 (slot, gen) 不受扰（POSIX fd 语义）
            _paths[dest] = srcIdx;
            if (!string.Equals(source, dest, StringComparison.Ordinal))
            {
                _paths.Remove(source);   // ★ 源路径项摘除（名字跟槽走——遗漏=Move 后源仍可见）
                // ★ CORE-13：覆盖目标盒必须摘除（旧盒 Value = 旧文件长度——覆盖后新句柄 Append
                //   落陈旧偏移 → 覆写新数据或留零洞）。不迁移源盒：mem 游标不随 Write 推进
                //   （仅截断/扩展复位）——迁移 = 陈旧值覆写；新句柄 GetOrAdd 按新长度初始化（重建）
                _appendCursors.TryRemove(dest, out _);
                _appendCursors.TryRemove(source, out _);
            }
        }
        if (retiredIdx >= 0) TryRetireSlot(retiredIdx);
        FileReplaced?.Invoke(source, dest);
    }

    // ═══════════════ 目录族（根空间层级——filesystem-root-space-design §3/§6）═══════════════

    /// <inheritdoc/>
    public void CreateDirectory(string path)
    {
        ThrowIfDisposed();
        AccessGate.RejectWrite(_access, nameof(CreateDirectory));
        using var gate = _maintenance.BeginMutation(nameof(CreateDirectory), path);
        ValidatePath(path);
        lock (_lock)
        {
            // mkdir -p：登记全部祖先组件（幂等）
            for (var i = path.IndexOf('/'); ; i = path.IndexOf('/', i + 1))
            {
                var dir = i < 0 ? path : path[..i];
                _directories.Add(dir);
                if (i < 0) break;
            }
        }
    }

    /// <inheritdoc/>
    public void DeleteDirectory(string path)
    {
        AccessGate.RejectWrite(_access, nameof(DeleteDirectory));
        ThrowIfDisposed();
        using var gate = _maintenance.BeginMutation(nameof(DeleteDirectory), path);
        ValidatePath(path);
        lock (_lock)
        {
            if (!DirectoryExistsNoLock(path))
                throw new FileIOException(IOError.NotFound, $"目录不存在: {path}", path, nameof(DeleteDirectory));
            if (AnyEntryUnderNoLock(path))
                throw new FileIOException(IOError.DirectoryNotEmpty, $"目录非空: {path}", path, nameof(DeleteDirectory));
            _directories.Remove(path);   // 空：显式登记摘除（derived-only 目录"存在"依赖内容——非空已被拦）
        }
    }

    /// <inheritdoc/>
    /// <remarks>显式集合 ∨ 前缀下有文件/子目录（derived——与枚举口径一致）。</remarks>
    public bool DirectoryExists(string path)
    {
        ThrowIfDisposed();
        _maintenance.ThrowIfReadsRejected(nameof(DirectoryExists), path);
        ValidatePath(path);
        lock (_lock)
            return DirectoryExistsNoLock(path);
    }

    private bool DirectoryExistsNoLock(string path)
        => _directories.Contains(path) || AnyEntryUnderNoLock(path);

    /// <summary>前缀下是否有任何文件或子目录（锁内）。</summary>
    private bool AnyEntryUnderNoLock(string dir)
    {
        var prefix = dir + "/";
        foreach (var k in _paths.Keys)
            if (k.StartsWith(prefix, StringComparison.Ordinal)) return true;
        foreach (var d in _directories)
            if (d.StartsWith(prefix, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <inheritdoc/>
    /// <remarks>mem：fs 锁内批量 re-key（路径项 + 目录集合）——原子（能力位 AtomicDirectoryMove 置位）。</remarks>
    public void MoveDirectory(string source, string dest)
    {
        AccessGate.RejectWrite(_access, nameof(MoveDirectory));
        ThrowIfDisposed();
        using var gate = _maintenance.BeginMutation(nameof(MoveDirectory), source);
        ValidatePath(source);
        ValidatePath(dest);
        lock (_lock)
        {
            if (!DirectoryExistsNoLock(source))
                throw new FileIOException(IOError.NotFound, $"源目录不存在: {source}", source, nameof(MoveDirectory));
            if (_paths.ContainsKey(dest) || DirectoryExistsNoLock(dest))
                throw new FileIOException(IOError.AlreadyExists,
                    $"MoveDirectory 目标已存在: {dest}（不提供 overwrite）。", dest, nameof(MoveDirectory));
            var srcPrefix = source + "/";
            var dstPrefix = dest + "/";
            // 文件批量 re-key（槽跟数据走——已打开句柄的 (slot, gen) 不受扰）
            foreach (var k in _paths.Keys.Where(k => k.StartsWith(srcPrefix, StringComparison.Ordinal)).ToArray())
            {
                _paths[dstPrefix + k[srcPrefix.Length..]] = _paths[k];
                _paths.Remove(k);
                _appendCursors.TryRemove(k, out _);
            }
            // 目录集合 re-key（含 source 本身）
            foreach (var d in _directories.Where(d => d == source || d.StartsWith(srcPrefix, StringComparison.Ordinal)).ToArray())
            {
                _directories.Remove(d);
                _directories.Add(d == source ? dest : dstPrefix + d[srcPrefix.Length..]);
            }
        }
    }

    // ═══════════════ 文件创建（与句柄解耦）+ Stat ═══════════════

    /// <inheritdoc/>
    /// <remarks>★ 与 <see cref="CreateOrReplaceFile"/>（mem 特有覆盖语义）区分：接口语义 = 显式非幂等（已存在抛 AlreadyExists）。
    /// 预分配：Reserved 真租物理块 / Sparse 逻辑长度；FileExtra 入槽字段（§3.6）。</remarks>
    public void CreateFile(string path, long preallocateSize = 0, ReadOnlyMemory<byte> extra = default)
    {
        ThrowIfDisposed();
        AccessGate.RejectWrite(_access, nameof(CreateFile));
        using var gate = _maintenance.BeginMutation(nameof(CreateFile), path);
        ValidatePath(path);
        if (extra.Length > IFileSystem.MaxFileExtraBytes)
            throw new ArgumentException($"FileExtra 超限（{extra.Length} > {IFileSystem.MaxFileExtraBytes}）。", nameof(extra));
        ArgumentOutOfRangeException.ThrowIfNegative(preallocateSize);
        lock (_lock)
        {
            if (_paths.ContainsKey(path))
                throw new FileIOException(IOError.AlreadyExists, $"文件已存在: {path}", path, nameof(CreateFile));
            // ★ 路径被目录占用（显式 mkdir 或含子条目的派生目录）——对齐磁盘行为：
            //   Win=ERROR_ACCESS_DENIED / Linux=EISDIR，文件创建必失败（介质平权：目录占位注入失败场景同构）。
            if (_directories.Contains(path) || AnyEntryUnderNoLock(path))
                throw new FileIOException(IOError.AccessDenied, $"路径是目录，无法创建文件: {path}", path, nameof(CreateFile));
            // 父目录须存在（对齐 disk ENOENT 语义——根 "" 恒存在）
            var parent = ParentOf(path);
            if (parent.Length > 0 && !_directories.Contains(parent) && !AnyEntryUnderNoLock(parent))
                throw new FileIOException(IOError.NotFound, $"父目录不存在: {parent}", path, nameof(CreateFile));
            var slotIdx = CreateFileNoLock(path, preallocateSize, preallocatePhysical: true);
            if (!extra.IsEmpty)
                _slots[slotIdx].FileExtra = extra.ToArray();
        }
    }

    /// <summary>父目录路径（"" = 根）。</summary>
    private static string ParentOf(string path)
    {
        var last = path.LastIndexOf('/');
        return last < 0 ? "" : path[..last];
    }

    /// <inheritdoc/>
    public FsEntryInfo Stat(string path)
    {
        ThrowIfDisposed();
        AccessGate.RejectRead(_access, nameof(Stat));
        _maintenance.ThrowIfReadsRejected(nameof(Stat), path);
        ValidatePath(path);
        lock (_lock)
        {
            if (_paths.TryGetValue(path, out var idx))
            {
                ref var slot = ref _slots[idx];
                return new FsEntryInfo(FsEntryType.File, path, slot.Data?.Size ?? 0,
                    new DateTimeOffset(slot.ModifiedTicks, TimeSpan.Zero),
                    new DateTimeOffset(slot.CreatedTicks, TimeSpan.Zero),
                    slot.FileExtra ?? Array.Empty<byte>());
            }
            if (DirectoryExistsNoLock(path))
                return new FsEntryInfo(FsEntryType.Directory, path, 0,
                    DateTimeOffset.MinValue, null, ReadOnlyMemory<byte>.Empty);   // mem 目录不追踪时间（诚实 MinValue/null）
            throw new FileIOException(IOError.NotFound, $"条目不存在: {path}", path, nameof(Stat));
        }
    }

    // ═══════════════ 枚举族（模式匹配 = PathPattern 客户端过滤——与 BCL Simple 同语义）═══════════════

    /// <inheritdoc/>
    public IEnumerable<FsEntry> EnumerateFiles(string pattern = "*", bool recursive = false)
    {
        AccessGate.RejectRead(_access, nameof(EnumerateFiles));
        return EnumerateCore(null, pattern, recursive, EntryFilter.Files);
    }

    /// <inheritdoc/>
    public IEnumerable<FsEntry> EnumerateFiles(string path, string pattern, bool recursive = false)
    {
        AccessGate.RejectRead(_access, nameof(EnumerateFiles));
        return EnumerateCore(path, pattern, recursive, EntryFilter.Files);
    }

    /// <inheritdoc/>
    public IEnumerable<FsEntry> EnumerateDirectories(string pattern = "*", bool recursive = false)
    {
        AccessGate.RejectRead(_access, nameof(EnumerateDirectories));
        return EnumerateCore(null, pattern, recursive, EntryFilter.Directories);
    }

    /// <inheritdoc/>
    public IEnumerable<FsEntry> EnumerateDirectories(string path, string pattern, bool recursive = false)
    {
        AccessGate.RejectRead(_access, nameof(EnumerateDirectories));
        return EnumerateCore(path, pattern, recursive, EntryFilter.Directories);
    }

    /// <inheritdoc/>
    public IEnumerable<FsEntry> EnumerateEntries(string pattern = "*", bool recursive = false)
    {
        AccessGate.RejectRead(_access, nameof(EnumerateEntries));
        return EnumerateCore(null, pattern, recursive, EntryFilter.Both);
    }

    /// <inheritdoc/>
    public IEnumerable<FsEntry> EnumerateEntries(string path, string pattern, bool recursive = false)
    {
        AccessGate.RejectRead(_access, nameof(EnumerateEntries));
        return EnumerateCore(path, pattern, recursive, EntryFilter.Both);
    }

    private enum EntryFilter { Files, Directories, Both }

    /// <summary>
    /// 枚举核（锁内快照物化后返回——readdir 契约本就不承诺原子快照）。
    /// 文件：路径前缀过滤（非递归 = 余段无 '/'）；目录：显式集合 ∪ 文件路径推导。
    /// 模式匹配目标 = 条目最终组件名；输出按 Name Ordinal 排序（测试确定性）。
    /// </summary>
    private List<FsEntry> EnumerateCore(string? path, string pattern, bool recursive, EntryFilter filter)   // 锁内物化 List——返回具体型避免接口多层枚举分配（CA1859）
    {
        ThrowIfDisposed();
        _maintenance.ThrowIfReadsRejected("Enumerate", path);
        PathPattern.Validate(pattern);
        lock (_lock)
        {
            if (path is not null && !DirectoryExistsNoLock(path))
                throw new FileIOException(IOError.NotFound, $"目录不存在: {path}", path, "Enumerate");
            var prefix = path is null ? "" : path + "/";
            var showHidden = PathPattern.HiddenExempt(pattern);   // §3.5 隐藏类豁免（A 方案）
            var result = new List<FsEntry>();

            if (filter != EntryFilter.Directories)
            {
                foreach (var kv in _paths)
                {
                    if (!kv.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    var rest = kv.Key[prefix.Length..];
                    if (rest.Length == 0) continue;
                    if (!recursive && rest.Contains('/')) continue;   // 非递归 = 仅一层
                    if (!showHidden && PathPattern.IsHiddenRelative(rest)) continue;   // 隐藏类（§3.5）
                    if (!PathPattern.IsMatch(LastComponent(rest), pattern)) continue;
                    ref var slot = ref _slots[kv.Value];
                    result.Add(new FsEntry(FsEntryType.File, rest, slot.Data?.Size ?? 0,
                        new DateTimeOffset(slot.ModifiedTicks, TimeSpan.Zero),
                        new DateTimeOffset(slot.CreatedTicks, TimeSpan.Zero)));
                }
            }

            if (filter != EntryFilter.Files)
            {
                var dirs = new HashSet<string>(StringComparer.Ordinal);
                // 显式登记目录
                foreach (var d in _directories)
                {
                    if (d.Length == 0) continue;
                    if (!d.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    var rest = d[prefix.Length..];
                    if (rest.Length == 0) continue;
                    if (!recursive && rest.Contains('/')) continue;
                    dirs.Add(d);
                }
                // 文件路径推导
                foreach (var k in _paths.Keys)
                {
                    if (!k.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    var rest = k[prefix.Length..];
                    var slash = rest.IndexOf('/');
                    if (slash <= 0) continue;   // 文件直接位于所枚举目录——不推导目录
                    if (recursive)
                    {
                        // 全部中间祖先
                        for (var i = rest.IndexOf('/'); i >= 0; i = rest.IndexOf('/', i + 1))
                            dirs.Add(prefix + rest[..i]);
                    }
                    else
                    {
                        dirs.Add(prefix + rest[..slash]);   // 一层：首段即子目录
                    }
                }
                foreach (var d in dirs)
                {
                    var name = d[prefix.Length..];
                    if (!showHidden && PathPattern.IsHiddenRelative(name)) continue;   // 隐藏类（§3.5）
                    if (!PathPattern.IsMatch(LastComponent(name), pattern)) continue;
                    result.Add(new FsEntry(FsEntryType.Directory, name, 0,
                        DateTimeOffset.MinValue, null));   // mem 目录不追踪时间
                }
            }

            result.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
            return result;
        }
    }

    private static ReadOnlySpan<char> LastComponent(string path)
    {
        var last = path.LastIndexOf('/');
        return last < 0 ? path.AsSpan() : path.AsSpan()[(last + 1)..];
    }

    /// <inheritdoc/>
    public IDisposable EnterMaintenance(string reason, MaintenanceScope scope, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        return _maintenance.Enter(reason, scope, ct);
    }

    /// <inheritdoc/>
    /// <remarks>★ 进程内真锁（补全）：LockWord CAS 互斥 + 自旋等待超时——与 Disk 卷锁行为保真
    /// （防同实例并发采集/维护编排错误；Default 全局盘的多组件共享是真实场景）。RAII lease；
    /// 超时 <see cref="IOError.SharingViolation"/>；非重入（持锁再获取立即失败——与 Disk 一致）。</remarks>
    public IDisposable AcquireExclusive(TimeSpan timeout)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout.TotalMilliseconds, 0);
        if (!_exclusiveLock.TryEnterExclusive((int)Math.Min(int.MaxValue, timeout.TotalMilliseconds)))
            throw new FileIOException(IOError.SharingViolation,
                $"AcquireExclusive timed out after {timeout.TotalMilliseconds:F0}ms（mem 卷锁被其他持有者持有）。",
                null, nameof(AcquireExclusive));
        return new ExclusiveLease(_exclusiveLock);
    }

    /// <summary>mem 卷锁原语（自旋互斥 + 有界超时——进程内单实例语义下即完备）。
    /// <para>★ 删除未使用的 LockWord 字段（作者草稿残留——互斥由 _held CAS 自实现，
    ///   LockWord 已被 SpinRWLock 终态替代删除，rebase 后编译断链在此收口）。</para></summary>
    private sealed class MemExclusiveLock
    {
        private int _held;

        public bool TryEnterExclusive(int timeoutMs)
        {
            var spinner = new SpinWait();
            var deadline = Environment.TickCount64 + timeoutMs;
            while (Interlocked.CompareExchange(ref _held, 1, 0) != 0)
            {
                if (Environment.TickCount64 >= deadline) return false;
                spinner.SpinOnce();
                if (spinner.NextSpinWillYield) Thread.Yield();
            }
            return true;
        }

        public void Release() => Volatile.Write(ref _held, 0);
    }

    private readonly MemExclusiveLock _exclusiveLock = new();

    private sealed class ExclusiveLease(MemExclusiveLock @lock) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            @lock.Release();
        }
    }

    /// <inheritdoc/>
    /// <remarks>★ mem 特例（方向与磁盘相反）：Dispose = 销毁卷（"拔盘"）——全部槽归还池（含 Detached），此后句柄操作抛 <see cref="ObjectDisposedException"/>。Default 单例 Dispose=no-op。</remarks>
    public void Dispose()
    {
        if (_isDefault) return;   // 全局单例不可释放
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _mountExclusiveLease?.Dispose();   // G5：挂载期排他租约释放
        lock (_lock)
        {
            _paths.Clear();
            foreach (ref var slot in _slots.AsSpan())
            {
                RecycleSlotAccounting(ref slot);
                slot = default;
            }
            _freeSlots.Clear();
            Volatile.Write(ref _physicalUsage, 0);
        }
    }

    /// <summary>文件创建事件（可观察）。参数：(路径, 初始大小)。</summary>
    public event Action<string, long>? FileCreated;

    /// <summary>文件删除事件。参数：路径。</summary>
    public event Action<string>? FileDeleted;

    /// <summary>文件移动/覆盖事件。参数：(源路径, 目标路径)。</summary>
    public event Action<string, string>? FileReplaced;

    // ═══════════════ 文件级公开操作（D5 API 重构——path-keyed）═══════════════

    /// <summary>显式创建文件（覆盖语义——已存在则旧文件 Detached 替换）。</summary>
    public void CreateOrReplaceFile(string path, long size)
    {
        AccessGate.RejectWrite(_access, nameof(CreateOrReplaceFile));
        ThrowIfDisposed();
        ValidatePath(path);
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        lock (_lock)
        {
            if (_paths.Remove(path, out var old))
            {
                _slots[old].State = SlotState.Detached;
                TryRetireSlot(old);
            }
            _appendCursors.TryRemove(path, out _);   // ★ CORE-13：覆盖摘除旧盒——新文件 Append 从新长度起（旧盒 = 旧长度 → 覆写/零洞）
            CreateFileNoLock(path, size);
        }
    }

    /// <summary>删除文件（<see cref="Delete"/> 的 path-keyed 别名）。</summary>
    public void DeleteFile(string path)
    {
        AccessGate.RejectWrite(_access, nameof(DeleteFile));
        Delete(path);
    }

    /// <summary>移动文件（<see cref="Move"/> 的 path-keyed 别名）。</summary>
    public void MoveFile(string source, string dest, bool overwrite = false)
    {
        AccessGate.RejectWrite(_access, nameof(MoveFile));
        Move(source, dest, overwrite);
    }

    /// <summary>扩展文件逻辑长度（保数据；Sparse 仅逻辑扩展，Reserved 换租拷贝）。</summary>
    public void GrowFile(string path, long newSize)
    {
        AccessGate.RejectWrite(_access, nameof(GrowFile));
        ThrowIfDisposed();
        ValidatePath(path);
        ArgumentOutOfRangeException.ThrowIfNegative(newSize);
        lock (_lock)
        {
            if (!_paths.TryGetValue(path, out var idx))
                throw new FileIOException(IOError.NotFound, $"文件不存在: {path}", path, nameof(GrowFile));
            GrowSlotNoLock(ref _slots[idx], newSize);
            RaiseAppendCursorNoLock(path, newSize);   // 只升不降（游标领先长度=有在途预留）
        }
    }

    /// <summary>截断文件（扩展方向读零；Reserved 收缩记账不还物理）。</summary>
    public void TruncateFile(string path, long newLength)
    {
        AccessGate.RejectWrite(_access, nameof(TruncateFile));
        ThrowIfDisposed();
        ValidatePath(path);
        ArgumentOutOfRangeException.ThrowIfNegative(newLength);
        lock (_lock)
        {
            if (!_paths.TryGetValue(path, out var idx))
                throw new FileIOException(IOError.NotFound, $"文件不存在: {path}", path, nameof(TruncateFile));
            TruncateNoLock(ref _slots[idx], newLength);
            ResetAppendCursorNoLock(path, newLength);   // 截断为权威复位（追加从新末端继续）
        }
    }

    /// <summary>追加预留权威复位（截断后）。锁内调用。</summary>
    private void ResetAppendCursorNoLock(string path, long newLength)
    {
        if (_appendCursors.TryGetValue(path, out var cursor))
            Interlocked.Exchange(ref cursor.Value, newLength);
    }

    /// <summary>追加预留下限抬升（显式扩展后防覆写——只升不降，游标领先长度=有在途预留）。锁内调用。</summary>
    private void RaiseAppendCursorNoLock(string path, long newLength)
    {
        if (!_appendCursors.TryGetValue(path, out var cursor)) return;
        while (true)
        {
            var current = Volatile.Read(ref cursor.Value);
            if (current >= newLength || Interlocked.CompareExchange(ref cursor.Value, newLength, current) == current)
                return;
        }
    }

    /// <summary>枚举全部文件路径（恢复扫描——消费者自行 parse 与排序；重命名避免与接口枚举族混淆）。</summary>
    public IEnumerable<string> EnumerateFilePaths()
    {
        ThrowIfDisposed();
        AccessGate.RejectRead(_access, nameof(EnumerateFilePaths));
        lock (_lock)
            return _paths.Keys.ToArray();
    }

    /// <summary>摘除文件取走负载（compactor 原语）：路径项摘除 + 负载所有权转移给调用方。</summary>
    public DetachedFile DetachFile(string path)
    {
        AccessGate.RejectWrite(_access, nameof(DetachFile));
        ThrowIfDisposed();
        ValidatePath(path);
        DetachedFile detached;
        SparseLayout? detachedLayout;
        lock (_lock)
        {
            if (!_paths.Remove(path, out var idx))
                throw new FileIOException(IOError.NotFound, $"文件不存在: {path}", path, nameof(DetachFile));
            ref var slot = ref _slots[idx];
            detachedLayout = slot.Layout;
            detached = new DetachedFile(slot.Data?.Buffer, slot.Data?.Size ?? 0, slot.Ranges,
                slot.Layout?.Pages, slot.Layout?.PageSize ?? _options.PageSize);
            // 负载所有权转移：槽内引用清空（缓冲归调用方管理），代际 bump 旧句柄失效
            slot.Data = null;
            slot.Layout = null;
            slot.Ranges = null;
            slot.State = SlotState.Detached;
            TryRetireSlot(idx);
        }
        // 负载移交屏障：在途数据面 IO（持旧 layout 引用）退出后，页数组才移交调用方
        DrainLayoutGate(detachedLayout);
        return detached;
    }

    /// <summary>安装外部负载为文件（compactor promote 原语——所有权转入 fs）。</summary>
    public void InstallFile(string path, DetachedFile file)
    {
        Shared.AccessGate.RejectWrite(_access, nameof(InstallFile));
        ThrowIfDisposed();
        ValidatePath(path);
        ArgumentNullException.ThrowIfNull(file);
        lock (_lock)
        {
            if (_paths.Remove(path, out var old))
            {
                _slots[old].State = SlotState.Detached;
                TryRetireSlot(old);
            }
            _appendCursors.TryRemove(path, out _);   // ★ CORE-13：覆盖摘除旧盒（同 CreateOrReplaceFile 律）
            var slotIdx = AllocateSlotNoLock();
            ref var slot = ref _slots[slotIdx];
            if (file.Buffer is not null)
            {
                var ptr = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(file.Buffer));
                AccountPhysical(file.Buffer.Length);
                slot.Data = new SlotData(file.Buffer, ptr, file.Size);
                slot.Ranges = file.Ranges ?? [(0, file.Size)];
            }
            else if (file.Pages is not null)
            {
                var layout = new SparseLayout(file.PageSize);
                foreach (var kv in file.Pages)
                    layout.Pages[kv.Key] = kv.Value;
                slot.Layout = layout;
                slot.Data = new SlotData(null, null, file.Size);
                AccountPhysical((long)file.Pages.Count * file.PageSize);
            }
            else
            {
                slot.Data = new SlotData(null, null, file.Size);
            }
            slot.State = SlotState.Live;
            var now = DateTime.UtcNow.Ticks;
            slot.CreatedTicks = now;
            slot.ModifiedTicks = now;
            _paths[path] = slotIdx;
        }
        FileReplaced?.Invoke("<detached>", path);
    }

    /// <summary>
    /// ★ internal 快路径：直接拿文件数据指针（Reserved 模式）。裸指针无生命周期保护——仅限 fs 内部
    /// 带引用语义的调用方使用；外部消费者一律 <see cref="IFileHandle.Map"/>。
    /// </summary>
    internal byte* GetDataPointer(string path)
    {
        ThrowIfDisposed();
        ValidatePath(path);
        lock (_lock)
        {
            if (!_paths.TryGetValue(path, out var idx))
                throw new FileIOException(IOError.NotFound, $"文件不存在: {path}", path, nameof(GetDataPointer));
            var data = _slots[idx].Data;
            if (data is null || (nint)data.Ptr == 0)
                throw new FileIOException(IOError.Unsupported, "Sparse 模式无直址指针（用 Map）。", path, nameof(GetDataPointer));
            return data.Ptr;
        }
    }

    // ═══════════════ 槽管理 ═══════════════

    /// <summary>槽分配（free list 优先，回退线性扫描，必要时倍容）。</summary>
    private int AllocateSlotNoLock()
    {
        if (_freeSlots.TryPop(out var idx))
        {
            _slots[idx].Generation++;
            return idx;
        }
        for (var i = 0; i < _slots.Length; i++)
        {
            if (_slots[i].State != SlotState.Empty) continue;
            _slots[i].Generation++;
            return i;
        }
        var oldLen = _slots.Length;
        Array.Resize(ref _slots, oldLen * 2);
        for (var i = oldLen + 1; i < _slots.Length; i++) _freeSlots.Push(i);
        _slots[oldLen].Generation++;
        return oldLen;
    }

    /// <summary>创建文件（锁内）——返回槽索引。</summary>
    private int CreateFileNoLock(string path, long initialSize, bool preallocatePhysical = false)
    {
        var slotIdx = AllocateSlotNoLock();
        ref var slot = ref _slots[slotIdx];
        if (_options.Allocation == MemoryAllocationMode.Reserved)
        {
            if (initialSize > 0)
            {
                var buffer = RentPhysical((int)initialSize);
                var ptr = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(buffer));
                slot.Data = new SlotData(buffer, ptr, initialSize);
                slot.Ranges = [(0, initialSize)];
            }
            else
            {
                // 空文件零物理（文件级追加游标初值=0；首次写入经 EnsureCapacity 自然换租）
                slot.Data = new SlotData(null, null, 0);
                slot.Ranges = [];
            }
        }
        else
        {
            slot.Layout = new SparseLayout(_options.PageSize);
            if (initialSize > 0 && preallocatePhysical)
            {
                // ★ 物理预租（——家族契约对齐）：Disk fallocate / Raw unwritten 区间都是物理
                // 预留，Sparse 此前只记逻辑长度是真缺口（预分配语义对账：账面对了物理没给）。
                // 预分配 = 分配/清零税前移到显式预留点，写路径变纯 memcpy（追加热道零分配）。
                // 仅 preallocate 语义物理化——CreateOrReplaceFile/GrowFile 的逻辑扩展语义不变（稀疏零物理）。
                PrewarmPagesNoLock(ref slot, 0, initialSize);
            }
            slot.Data = new SlotData(null, null, initialSize);   // Data 仅承载逻辑长度
        }
        slot.State = SlotState.Live;
        var now = DateTime.UtcNow.Ticks;
        slot.CreatedTicks = now;
        slot.ModifiedTicks = now;
        _paths[path] = slotIdx;
        FileCreated?.Invoke(path, initialSize);
        return slotIdx;
    }

    /// <summary>槽退役检查：Detached 且 RefCount==0 → 回收（缓冲 epoch 延迟归还 + 代际 bump）。</summary>
    private void TryRetireSlot(int slotIdx)
    {
        byte[]? retiredBuffer;
        SparseLayout? retiredLayout;
        List<byte[]> retiredPages = [];
        lock (_lock)
        {
            ref var slot = ref _slots[slotIdx];
            if (slot.State != SlotState.Detached || slot.RefCount != 0) return;
            retiredBuffer = slot.Data?.Buffer;
            retiredLayout = slot.Layout;
            if (slot.Layout is { } layout)
            {
                retiredPages.AddRange(layout.Pages.Values);
                UnaccountPhysical((long)layout.Pages.Count * layout.PageSize);
            }
            if (retiredBuffer is not null)
                UnaccountPhysical(retiredBuffer.Length);
            slot.Data = null;
            slot.Layout = null;
            slot.Ranges = null;
            slot.FileExtra = null;
            slot.OpenRegistry = null;
            slot.Locks = null;
            slot.MaterializedMaps = null;
            slot.State = SlotState.Empty;
            slot.Generation++;
            _freeSlots.Push(slotIdx);
        }
        // 在途数据面 IO 屏障：fs 锁内已摘 Layout/bump 代际——其后新 IO 双检失败不触页；
        // 已持 Gate 的在途 IO 退出后归还页才安全（DEBUG 池归还 0xCC 毒化兜底暴露违例）
        DrainLayoutGate(retiredLayout);
        if (retiredBuffer is not null) RetireBuffer(retiredBuffer);
        foreach (var page in retiredPages) RetireBuffer(page);
    }

    /// <summary>空获取 Gate 排他 = 在途数据面 IO 屏障（等持门者退出；其后页归还/负载移交安全）。</summary>
    private static void DrainLayoutGate(SparseLayout? layout)
    {
        if (layout is null) return;
        layout.Gate.AcquireExclusive();
        layout.Gate.ReleaseExclusive();
    }

    private void RecycleSlotAccounting(ref FileSlot slot)
    {
        if (slot.Data?.Buffer is { } buffer)
            UnaccountPhysical(buffer.Length);
        if (slot.Layout is { } layout)
            UnaccountPhysical((long)layout.Pages.Count * layout.PageSize);
        // 拔盘路径：缓冲不归还池（池将随之消亡）；仅清计费
    }

    /// <summary>
    /// Reserved 扩容（★ 唯一 memcpy 换 buffer 点——freeze 屏障 + epoch/引用双门槛延迟归还）。
    /// 调用约定：持 _lock。屏障内写者永不等待 _lock（EnsureCapacityForWrite 在出屏障后调锁）——freeze 自旋无死锁。
    /// </summary>
    private void GrowSlotNoLock(ref FileSlot slot, long newSize)
    {
        if (slot.Layout is not null)
        {
            // Sparse：逻辑长度扩展（原子换 Data 引用，无物理动作）
            var snap = slot.Data!;
            if (newSize <= snap.Size) return;
            slot.Data = new SlotData(null, null, newSize);
            return;
        }

        var old = slot.Data!;
        if (newSize <= old.Size) return;

        // ★ freeze 先行：等在途写者退出（其数据完整落在旧 buffer——随后 memcpy 带走），再拷贝。
        //   ★ try/finally 包夹——租借/记账可在中途抛（DiskFull），freeze 必须在异常路径同样解除
        //   （否则屏障永久锁死全部写者——Append 失败场景实测死锁根因）。
        Interlocked.Exchange(ref _freeze, 1);
        try
        {
            while (Volatile.Read(ref _writersInFlight) != 0) Thread.Yield();

            var buffer = RentPhysical((int)newSize);
            var ptr = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(buffer));
            if (old.Size > 0 && old.Ptr != null)
                Buffer.MemoryCopy(old.Ptr, ptr, old.Size, old.Size);
            if (old.Buffer is not null)
                UnaccountPhysical(old.Buffer.Length);
            var newData = new SlotData(buffer, ptr, newSize);
            slot.Data = newData;                       // 原子换快照（读者无撕裂）

            if (slot.Ranges is not null)
                AddRangeNoLock(slot.Ranges, old.Size, newSize);
        }
        finally
        {
            Interlocked.Exchange(ref _freeze, 0);
        }

        // 旧 buffer 延迟归还：epoch drain（在途读者退出）+ 直址映射 Refs（视图钉住）双门槛
        // （空文件首写——旧 Data 无 buffer，无归还）
        if (old.Buffer is not null)
            RetireSlotData(old);
    }

    /// <summary>Sparse 物理预热（预分配快道）：[from, to) 区间整页批量预租 + 清零。
    /// ★ 须持 fs._lock 调用（内部再取 Gate 排他——锁序 _lock→Gate 合法）；
    /// 分配/清零税前移到显式预分配点——此后写路径 = 页表命中纯 memcpy（零分配零清零）。
    /// 配额不足抛 DiskFull（已租页保留——零页无害，逻辑长度未推进，状态一致）。</summary>
    private void PrewarmPagesNoLock(ref FileSlot slot, long from, long to)
    {
        var layout = slot.Layout;
        if (layout is null || to <= from) return;
        var firstPage = from / layout.PageSize;
        var lastPage = (to + layout.PageSize - 1) / layout.PageSize;
        using (layout.Gate.EnterExclusive())
        {
            for (var p = firstPage; p < lastPage; p++)
            {
                if (!layout.Pages.ContainsKey(p))
                    layout.Pages[p] = RentPhysical(layout.PageSize);   // 预分配读零语义：清零租借
            }
        }
    }

    /// <summary>Sparse 预分配物理化（MemFileHandle.Preallocate / TruncateFile 扩展段共用）。</summary>
    internal void PrewarmSparse(string path, long newLength)
    {
        lock (_lock)
        {
            if (!_paths.TryGetValue(path, out var slotIdx)) return;
            ref var slot = ref _slots[slotIdx];
            if (slot.State != SlotState.Live) return;
            var current = slot.Data?.Size ?? 0;
            if (newLength <= current) return;
            PrewarmPagesNoLock(ref slot, current, newLength);
            slot.Data = new SlotData(null, null, newLength);
            slot.ModifiedTicks = DateTime.UtcNow.Ticks;
        }
    }

    /// <summary>截断：扩展=读零（Sparse 逻辑 / Reserved 换租）；收缩=Sparse 释放整页+部分页清尾 / Reserved 记账收缩。</summary>
    private void TruncateNoLock(ref FileSlot slot, long newLength)    {
        if (slot.Layout is not null)
        {
            var snap = slot.Data!;
            if (newLength > snap.Size)
            {
                slot.Data = new SlotData(null, null, newLength);   // 扩展读零（页未分配/被清）
                slot.ModifiedTicks = DateTime.UtcNow.Ticks;
                return;
            }
            var layout = slot.Layout;
            using var gate = layout.Gate.EnterExclusive();   // 页释放/清尾——数据面互斥（锁序 _lock→Gate 合法；RetireBuffer 不取 fs._lock）

            {
                var pageSize = layout.PageSize;
                var keepPages = newLength / pageSize;                  // 完整保留页数
                // 部分保留页：清尾（truncate-extend 再读必须为零——POSIX ftruncate 语义）
                if (newLength % pageSize != 0 && layout.Pages.TryGetValue(keepPages, out var partial))
                {
                    var tailStart = (int)(newLength % pageSize);
                    fixed (byte* p = partial)
                        Unsafe.InitBlockUnaligned(p + tailStart, 0, (uint)(pageSize - tailStart));
                }
                var retiredKeys = new List<long>();
                foreach (var kv in layout.Pages)
                {
                    if (kv.Key > keepPages || (kv.Key == keepPages && newLength % pageSize == 0))
                        retiredKeys.Add(kv.Key);   // ★ CORE-11：收集 key 直接 Remove——旧实现每页一次全表线性扫描 = O(P²)（持全局锁拖死整卷）
                }
                foreach (var key in retiredKeys)
                {
                    if (layout.Pages.Remove(key, out var page))
                    {
                        UnaccountPhysical(pageSize);
                        _pool.Return(page);   // 页已出字典——新 IO 不可达，归还安全
                    }
                }
                slot.Data = new SlotData(null, null, newLength);
                slot.ModifiedTicks = DateTime.UtcNow.Ticks;
                TruncateMaterializedMaps(slot, newLength);
            }
            return;
        }

        var data = slot.Data!;
        if (newLength > data.Size)
        {
            GrowSlotNoLock(ref slot, newLength);
            return;
        }
        if (newLength == data.Size) return;

        // Reserved 收缩：不换主 buffer（容量保留）——记账收缩 + 尾部清零（防 truncate-extend 复视）
        Interlocked.Exchange(ref _freeze, 1);
        try
        {
            while (Volatile.Read(ref _writersInFlight) != 0) Thread.Yield();
            if (data.Ptr != null)
                Unsafe.InitBlockUnaligned(data.Ptr + newLength, 0, (uint)(data.Size - newLength));
            slot.Data = new SlotData(data.Buffer, data.Ptr, newLength);
            slot.ModifiedTicks = DateTime.UtcNow.Ticks;
        }
        finally
        {
            Interlocked.Exchange(ref _freeze, 0);
        }
        RemoveRangeNoLock(slot.Ranges!, newLength, data.Size);
        TruncateMaterializedMaps(slot, newLength);
    }

    /// <summary>页 → key 反向查找（CORE-11 后仅诊断/断言用——截断路径已改收集 key 直接 Remove，O(P²) 消除）。</summary>
    private static long RemoveKeyOf(SparseLayout layout, byte[] page)
    {
        foreach (var kv in layout.Pages)
        {
            if (ReferenceEquals(kv.Value, page)) return kv.Key;
        }
        return -1;
    }

    /// <summary>写路径零扩展（Write 越过 EOF——pwrite 平权；出屏障/Gate 后调锁，防屏障×锁死锁）。
    /// 代际失效抛 stale（原为静默 return 由调用方循环重检——提前到此处，语义等价）。</summary>
    internal void EnsureCapacityForWrite(int slotIdx, int gen, long endOffset)
    {
        lock (_lock)
        {
            ref var slot = ref _slots[slotIdx];
            var data = slot.Data;
            if (data is null || slot.Generation != gen) ThrowStale(slotIdx, gen);
            if (endOffset <= data.Size) return;
            if (slot.Layout is not null)
                slot.Data = new SlotData(null, null, endOffset);   // Sparse 逻辑扩展
            else
                GrowSlotNoLock(ref slot, endOffset);               // Reserved 换租
        }
    }

    // ★ CORE-14：slot-keyed 空间操作（MemFileHandle.SetLength/Preallocate 用）——代际校验
    //   防"按 path 重解析命中同名新文件"的跨代越权；游标操作由句柄经注入盒自理（盒按 path 共享）。

    /// <summary>句柄绑定槽的截断（双向：收缩释放/清尾；扩展 = 读零）——stale 抛（不碰新主）。</summary>
    internal void TruncateSlot(int slotIdx, int gen, long newLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(newLength);
        lock (_lock)
        {
            if (ReadSlotChecked(slotIdx, gen) is null) ThrowStale(slotIdx, gen);
            TruncateNoLock(ref _slots[slotIdx], newLength);
        }
    }

    /// <summary>句柄绑定槽的扩展（Reserved 换租拷贝）——stale 抛。</summary>
    internal void GrowSlot(int slotIdx, int gen, long newSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(newSize);
        lock (_lock)
        {
            if (ReadSlotChecked(slotIdx, gen) is null) ThrowStale(slotIdx, gen);
            GrowSlotNoLock(ref _slots[slotIdx], newSize);
        }
    }

    /// <summary>句柄绑定槽的 Sparse 预分配物理化（preallocate 语义 = 物理预留 + 读零）——stale 抛。</summary>
    internal void PrewarmSlot(int slotIdx, int gen, long newLength)
    {
        lock (_lock)
        {
            ref var slot = ref _slots[slotIdx];
            if (ReadSlotChecked(slotIdx, gen) is null) ThrowStale(slotIdx, gen);
            if (slot.State != SlotState.Live) return;
            var current = slot.Data?.Size ?? 0;
            if (newLength <= current) return;
            PrewarmPagesNoLock(ref slot, current, newLength);
            slot.Data = new SlotData(null, null, newLength);
            slot.ModifiedTicks = DateTime.UtcNow.Ticks;
        }
    }

    // ═══════════════ 数据面原语（MemFileHandle 热路径）═══════════════

    /// <summary>带代际双检的快照读（gen→Data→gen + 空检——ABA 三重关闭）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private SlotData? ReadSlotChecked(int slotIdx, int gen)
    {
        ref var slot = ref _slots[slotIdx];
        var gen1 = Volatile.Read(ref slot.Generation);
        var data = Volatile.Read(ref slot.Data);
        var gen2 = Volatile.Read(ref slot.Generation);
        if (gen1 != gen || gen2 != gen || data is null) return null;
        return data;
    }

    /// <summary>Reserved 读——epoch 保护锁外拷贝（在途读者窗口；临界区零分配/零锁/零 await）。</summary>
    internal int ReadDirect(int slotIdx, int gen, long offset, Span<byte> destination)
    {
        ThrowIfDisposed();   // 拔盘后句柄操作失效（⑬ 方向差异：与磁盘"离开目录"相反）
        _epoch.Resume();
        try
        {
            var data = ReadSlotChecked(slotIdx, gen);
            if (data is null) ThrowStale(slotIdx, gen);
            var canRead = data.Size - offset;
            if (canRead <= 0) return 0;
            var n = (int)Math.Min(destination.Length, canRead);
            fixed (byte* pDst = destination)
                Buffer.MemoryCopy(data.Ptr + offset, pDst, n, n);
            return n;
        }
        finally
        {
            _epoch.Suspend();
        }
    }

    /// <summary>
    /// Reserved 写——freeze 屏障保护锁外写（写者不进 epoch：epoch 只保读者，写者生命周期由屏障完整覆盖）。
    /// 越界扩展时先出屏障再进锁（屏障内等锁 × 持锁等屏障 = 死锁），扩展后重入屏障重读快照。
    /// </summary>
    internal void WriteDirect(int slotIdx, int gen, long offset, ReadOnlySpan<byte> source)
    {
        ThrowIfDisposed();
        if (source.IsEmpty) return;
        while (true)
        {
            EnterWriterBarrier();
            var data = ReadSlotChecked(slotIdx, gen);
            if (data is null)
            {
                ExitWriterBarrier();
                ThrowStale(slotIdx, gen);
            }
            if (offset + source.Length > data.Size)
            {
                ExitWriterBarrier();
                EnsureCapacityForWrite(slotIdx, gen, offset + source.Length);
                continue;   // 扩展后重读快照
            }
            try
            {
                fixed (byte* pSrc = source)
                    Buffer.MemoryCopy(pSrc, data.Ptr + offset, data.Size - offset, source.Length);
                Volatile.Write(ref _slots[slotIdx].ModifiedTicks, DateTime.UtcNow.Ticks);
                return;
            }
            finally
            {
                ExitWriterBarrier();
            }
        }
    }

    /// <summary>写者屏障进入：freeze 检查 + 计数 + double-check（进屏障竞态重试）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnterWriterBarrier()
    {
        while (true)
        {
            if (Volatile.Read(ref _freeze) == 1)
            {
                Thread.Yield();
                continue;
            }
            Interlocked.Increment(ref _writersInFlight);
            if (Volatile.Read(ref _freeze) == 1)
            {
                Interlocked.Decrement(ref _writersInFlight);
                continue;
            }
            return;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ExitWriterBarrier() => Interlocked.Decrement(ref _writersInFlight);

    /// <summary>旧 SlotData 延迟归还：epoch drain（在途读者退出）后，Refs==0 立即归还，否则标记 retired 由最后 unpin 归还。</summary>
    private void RetireSlotData(SlotData old)
    {
        var buffer = old.Buffer!;
        _epoch.Resume();
        try
        {
            _epoch.BumpCurrentEpoch(() =>
            {
                // onDrain：全部 Enter 于 bump 之前的读者已 Exit——只剩直址映射引用
                if (Interlocked.CompareExchange(ref old.Refs, 0, 0) == 0)
                    _pool.Return(buffer);
                else
                    Volatile.Write(ref old.Retired, true);
            });
        }
        finally
        {
            _epoch.Suspend();
        }
    }

    /// <summary>直址映射 Pin/Unpin（Refs 守护——retired 且归零时归还池）。</summary>
    internal static void PinSlotData(SlotData data) => Interlocked.Increment(ref data.Refs);

    internal void UnpinSlotData(SlotData data)
    {
        if (Interlocked.Decrement(ref data.Refs) != 0) return;
        if (Volatile.Read(ref data.Retired))
            _pool.Return(data.Buffer!);
    }

    /// <summary>普通缓冲延迟归还（Delete/截断/打洞释放——仅需 epoch 门槛）。★ 出锁后调用；onDrain 三禁合规（Return 纯内存）。</summary>
    private void RetireBuffer(byte[] buffer)
    {
        _epoch.Resume();
        try
        {
            _epoch.BumpCurrentEpoch(() => _pool.Return(buffer));
        }
        finally
        {
            _epoch.Suspend();
        }
    }

    // ★ CORE-12：对齐 IO 缓冲租借（CopyRange 等 DIO 句柄场景——托管数组仅 8B 对齐必触发 AlignmentError；
    // 与 DiskFileSystem.RentIoBuffer 同构）。页池对齐桶 + 归还校验（PoolId 防误还）。

    /// <summary>租对齐 IO 缓冲（NoBuffering 句柄三重对齐的缓冲地址约束——对齐基准 RequiredAlignment）。</summary>
    internal Memory<byte> RentIoBuffer(int size, int alignment)
        => _pool.RentAligned(size, alignment).Memory;

    /// <summary>归还对齐 IO 缓冲（TryGetMemoryManager 校验归属——非本池缓冲静默忽略）。</summary>
    internal void ReturnIoBuffer(Memory<byte> buffer)
    {
        if (MemoryMarshal.TryGetMemoryManager<byte, AlignedMemoryManager>(buffer, out var mgr))
            _pool.ReturnAligned(mgr);
    }

    [DoesNotReturn]
    internal static void ThrowStale(int slotIdx, int gen) =>
        throw new FileIOException(IOError.NotFound,
            $"句柄指向的槽已失效（slot={slotIdx}, gen={gen}）——文件已被删除/替换/拔盘。", null, "stale-handle");

    // ═══════════════ Sparse 数据面（RW Gate——读共享写独占；页路由一致性优先）═══════════════

    internal int ReadSparse(int slotIdx, int gen, long offset, Span<byte> destination)
    {
        ThrowIfDisposed();
        var layout = Volatile.Read(ref _slots[slotIdx].Layout);
        if (layout is null) ThrowStale(slotIdx, gen);
        using var gate = layout.Gate.EnterShared();   // 读共享（RW Gate）——并发读者互不阻塞

        {
            if (!LayoutCurrent(slotIdx, gen, layout)) ThrowStale(slotIdx, gen);
            var canRead = (Volatile.Read(ref _slots[slotIdx].Data)?.Size ?? 0) - offset;
            if (canRead <= 0) return 0;
            var n = (int)Math.Min(destination.Length, canRead);
            ReadSparseNoLock(layout, offset, destination[..n]);
            return n;
        }
    }

    /// <summary>持 Gate 后的槽身份重验（代际 + layout 引用双检）——回收互斥的另一半：
    /// 回收路径在 fs 锁内 bump 代际/摘 Layout 后，还须空获取 Gate 等在途 IO 退出才归还页（见 DrainLayoutGate）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool LayoutCurrent(int slotIdx, int gen, SparseLayout layout)
    {
        ref var slot = ref _slots[slotIdx];
        return slot.Generation == gen && ReferenceEquals(Volatile.Read(ref slot.Layout), layout);
    }

    private static void ReadSparseNoLock(SparseLayout layout, long offset, Span<byte> destination)
    {
        var pageSize = layout.PageSize;
        var pos = 0L;
        while (pos < destination.Length)
        {
            var page = (offset + pos) / pageSize;
            var inPage = (int)((offset + pos) % pageSize);
            var chunk = (int)Math.Min(pageSize - inPage, destination.Length - pos);
            if (layout.Pages.TryGetValue(page, out var buf))
            {
                fixed (byte* pDst = destination[(int)pos..])
                fixed (byte* pSrc = buf)
                    Buffer.MemoryCopy(pSrc + inPage, pDst, chunk, chunk);
            }
            else
            {
                destination[(int)pos..(int)(pos + chunk)].Clear();   // 未分配页读零
            }
            pos += chunk;
        }
    }

    internal void WriteSparse(int slotIdx, int gen, long offset, ReadOnlySpan<byte> source)
    {
        ThrowIfDisposed();
        if (source.IsEmpty) return;
        var layout = Volatile.Read(ref _slots[slotIdx].Layout);
        if (layout is null) ThrowStale(slotIdx, gen);
        var end = offset + source.Length;
        // ★ 排他必须显式块 scope：EnsureCapacityForWrite 要拿 fs._lock——持 Gate 拿 _lock = 锁序反转死锁
        //   （锁序不变式：_lock 只能在 Gate 外获取）。using var 形态会持有到方法尾——禁止。
        using (layout.Gate.EnterExclusive())
        {
            if (!LayoutCurrent(slotIdx, gen, layout)) ThrowStale(slotIdx, gen);
            // 可见性顺序：页先就位（Gate 内）→ 长度后发布（下方 fs 锁）——读者见新长度 ⇒ 页必已可读
            WriteSparseNoLock(layout, offset, source);
            Volatile.Write(ref _slots[slotIdx].ModifiedTicks, DateTime.UtcNow.Ticks);
        }
        // 越过 EOF 才进 fs 锁推长度（锁序不变式：_lock 只能在 Gate 外获取；覆写/预分配场景零 fs 锁）。
        // 写页后被摘槽的极端窗口：Ensure 内代检验 stale——页已落在 Detached 布局随回收消亡，无害。
        if (end > (Volatile.Read(ref _slots[slotIdx].Data)?.Size ?? 0))
            EnsureCapacityForWrite(slotIdx, gen, end);
    }

    private void WriteSparseNoLock(SparseLayout layout, long offset, ReadOnlySpan<byte> source)
    {
        var pageSize = layout.PageSize;
        var pos = 0L;
        while (pos < source.Length)
        {
            var page = (offset + pos) / pageSize;
            var inPage = (int)((offset + pos) % pageSize);
            var chunk = (int)Math.Min(pageSize - inPage, source.Length - pos);
            if (!layout.Pages.TryGetValue(page, out var buf))
            {
                // ★ 免清零租借 + 未覆盖段按需清（）：池复租不残留旧数据的读零安全
                //   由"新页插入前未覆盖段清零"保证——整页覆写路径零清零成本（此前无条件全页
                //   Array.Clear 是纯浪费：数据马上整页覆写，清零只为部分写语义服务）。
                buf = RentPhysical(pageSize, zeroMemory: false);
                layout.Pages[page] = buf;
                if (inPage > 0 || inPage + chunk < pageSize)
                {
                    fixed (byte* pDst = buf)
                    {
                        if (inPage > 0)
                            Unsafe.InitBlockUnaligned(pDst, 0, (uint)inPage);
                        var tail = pageSize - inPage - chunk;
                        if (tail > 0)
                            Unsafe.InitBlockUnaligned(pDst + inPage + chunk, 0, (uint)tail);
                    }
                }
            }
            fixed (byte* pDst = buf)
            fixed (byte* pSrc = source[(int)pos..])
                Buffer.MemoryCopy(pSrc, pDst + inPage, chunk, chunk);
            pos += chunk;
        }
    }

    /// <summary>Sparse PunchHole：洞内字节物理 memset(0)（读路径零特判——洞就是物理零）+ 整页释放（epoch 延迟归还）。</summary>
    internal void PunchHoleSparse(int slotIdx, int gen, long offset, long length)
    {
        List<byte[]>? retired = null;
        lock (_lock)
        {
            var data = ReadSlotChecked(slotIdx, gen);
            if (data is null) ThrowStale(slotIdx, gen);
            var layout = _slots[slotIdx].Layout!;
            using var gate = layout.Gate.EnterExclusive();   // 数据面互斥（页在字典移除前禁止他方访问；锁序 _lock→Gate 合法）

            {
                var pageSize = layout.PageSize;
                var firstPage = offset / pageSize;
                var lastPage = (offset + length - 1) / pageSize;

                var toRemove = new List<long>();
                foreach (var (page, bytes) in layout.Pages)
                {
                    if (page < firstPage || page > lastPage) continue;
                    var pageStart = page * pageSize;
                    var zeroStart = Math.Max(offset, pageStart);
                    var zeroEnd = Math.Min(offset + length, pageStart + pageSize);
                    if (zeroEnd <= zeroStart) continue;
                    fixed (byte* p = bytes)
                        Unsafe.InitBlockUnaligned(p + (zeroStart - pageStart), 0, (uint)(zeroEnd - zeroStart));
                    if (zeroStart == pageStart && zeroEnd == pageStart + pageSize)
                    {
                        toRemove.Add(page);   // 整页被洞完全覆盖 → 物理释放
                        (retired ??= []).Add(bytes);
                        UnaccountPhysical(pageSize);
                    }
                    else
                    {
                        // 部分页：数据页降级为"全零页"——保留占位（AllocatedSize 记账含此页，契约允许块粒度）
                    }
                }
                foreach (var page in toRemove)
                    layout.Pages.Remove(page);

                ZeroMaterializedMaps(_slots[slotIdx], offset, length);
            }
        }
        if (retired is not null)
            foreach (var page in retired) RetireBuffer(page);   // 页已出字典——新 IO 不可达，归还安全
    }

    /// <summary>Reserved PunchHole：memset + 记账收缩（物理不还——容量本就预留；等价磁盘预分配文件打洞的中间态）。</summary>
    internal void PunchHoleReserved(int slotIdx, int gen, long offset, long length)
    {
        lock (_lock)
        {
            var data = ReadSlotChecked(slotIdx, gen);
            if (data is null) ThrowStale(slotIdx, gen);
            if (offset + length > data.Size)
                throw new FileIOException(IOError.IOFailure, "PunchHole 区间超出文件长度。", null, "PunchHole");
            if (data.Ptr != null)
                Unsafe.InitBlockUnaligned(data.Ptr + offset, 0, (uint)length);
            RemoveRangeNoLock(_slots[slotIdx].Ranges!, offset, offset + length);
            ZeroMaterializedMaps(_slots[slotIdx], offset, length);
        }
    }

    /// <summary>Sparse 区间整理：memmove 页数据 + 长度变更（页号重映射）。</summary>
    internal void ShiftRangeSparse(int slotIdx, int gen, long offset, long length, bool insert)
    {
        lock (_lock)
        {
            var data = ReadSlotChecked(slotIdx, gen);
            if (data is null) ThrowStale(slotIdx, gen);
            var slot = _slots[slotIdx];
            var layout = slot.Layout!;
            using var gate = layout.Gate.EnterExclusive();   // 全量重排页表——数据面互斥（锁序 _lock→Gate 合法）

            {
                var pageSize = layout.PageSize;
                var size = data.Size;

                // 物化快照重排（页表重映射的正确性载体——冷路径，简洁优先）
                var buffer = new byte[size];
                ReadSparseNoLock(layout, 0, buffer);
                // ★ insert 的尾部 = size - offset（无移除量）；collapse 的尾部 = size - offset - length
                var tailLen = insert ? size - offset : size - offset - length;
                var newBuffer = insert
                    ? Concat(buffer.AsSpan(0, (int)offset), new byte[length], buffer.AsSpan((int)offset, (int)Math.Max(0, tailLen)))
                    : Concat(buffer.AsSpan(0, (int)offset), buffer.AsSpan((int)(offset + length), (int)Math.Max(0, tailLen)));

                // 重建页表
                var oldPages = layout.Pages.Values.ToArray();
                layout.Pages.Clear();
                UnaccountPhysical((long)oldPages.Length * pageSize);
                foreach (var old in oldPages) RetireBuffer(old);
                WriteSparseNoLock(layout, 0, newBuffer);
                _slots[slotIdx].Data = new SlotData(null, null, newBuffer.Length);

                RebuildMaterializedMaps(slot, newBuffer);
            }
        }
    }

    /// <summary>Reserved 区间整理：memmove 主 buffer + 记账/长度变更。</summary>
    internal void ShiftRangeReserved(int slotIdx, int gen, long offset, long length, bool insert)
    {
        lock (_lock)
        {
            var data = ReadSlotChecked(slotIdx, gen);
            if (data is null) ThrowStale(slotIdx, gen);
            var size = data.Size;
            var tailLen = Math.Max(0, size - offset - length);
            var newLength = insert ? size + length : size - length;

            Interlocked.Exchange(ref _freeze, 1);
            try
            {
                while (Volatile.Read(ref _writersInFlight) != 0) Thread.Yield();
                var buffer = RentPhysical((int)Math.Max(newLength, 1));
                var ptr = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(buffer));
                if (data.Ptr != null && size > 0)
                {
                    Buffer.MemoryCopy(data.Ptr, ptr, offset, offset);
                    if (tailLen > 0)
                        Buffer.MemoryCopy(data.Ptr + offset + length, ptr + offset + (insert ? length : 0), tailLen, tailLen);
                    if (insert)
                        Unsafe.InitBlockUnaligned(ptr + offset, 0, (uint)length);   // 插入洞读零
                }
                UnaccountPhysical(data.Buffer!.Length);
                _slots[slotIdx].Data = new SlotData(buffer, ptr, newLength);
            }
            finally
            {
                Interlocked.Exchange(ref _freeze, 0);
            }
            _slots[slotIdx].Ranges = [(0, newLength)];
            TruncateMaterializedMaps(_slots[slotIdx], newLength);
            RetireSlotData(data);
        }
    }

    private static byte[] Concat(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, ReadOnlySpan<byte> c = default)
    {
        var result = new byte[a.Length + b.Length + c.Length];
        a.CopyTo(result);
        b.CopyTo(result.AsSpan(a.Length));
        c.CopyTo(result.AsSpan(a.Length + b.Length));
        return result;
    }

    // ═══════════════ 物化映射协同（空间操作 × 映射契约——副本同步应用）═══════════════

    internal void RegisterMaterializedMap(int slotIdx, MemSparseMappedSection section)
    {
        lock (_lock)
        {
            (_slots[slotIdx].MaterializedMaps ??= []).Add(new WeakReference(section));
        }
    }

    private static void ZeroMaterializedMaps(FileSlot slot, long offset, long length)
    {
        if (slot.MaterializedMaps is not { Count: > 0 } maps) return;
        foreach (var wr in maps)
        {
            if (wr.Target is MemSparseMappedSection map)
                map.ZeroRange(offset, length);
        }
    }

    private static void TruncateMaterializedMaps(FileSlot slot, long newLength)
    {
        if (slot.MaterializedMaps is not { Count: > 0 } maps) return;
        foreach (var wr in maps)
        {
            if (wr.Target is MemSparseMappedSection map)
                map.TruncateTo(newLength);
        }
    }

    private static void RebuildMaterializedMaps(FileSlot slot, byte[] newContent)
    {
        if (slot.MaterializedMaps is not { Count: > 0 } maps) return;
        foreach (var wr in maps)
        {
            if (wr.Target is MemSparseMappedSection map)
                map.RebuildFrom(newContent);
        }
    }

    // ═══════════════ 范围锁 / xattr / 句柄生命周期（MemFileHandle 复用）═══════════════

    internal bool LockRange(int slotIdx, int gen, long offset, long length, FileLockMode mode, bool blocking, object owner)
    {
        while (true)
        {
            object waitGate;
            lock (_lock)
            {
                if (_slots[slotIdx].Generation != gen) ThrowStale(slotIdx, gen);
                var table = _slots[slotIdx].Locks ??= new RangeLockTable();
                if (table.TryAcquire(offset, length, mode, owner)) return true;
                if (!blocking) return false;
                waitGate = table.ChangedGate;   // ★ CORE-20：锁内取信号引用（表引用稳定——惰性创建后不换）
            }
            // 阻塞等待：条件变量（释放方 PulseAll）——等待期间零全局锁抢占；50ms 有界分片兜底丢脉冲
            lock (waitGate)
                Monitor.Wait(waitGate, 50);
        }
    }

    internal void UnlockRange(int slotIdx, long offset, long length, object owner)
    {
        lock (_lock)
        {
            _slots[slotIdx].Locks?.Release(offset, length, owner);
        }
    }

    internal void ReleaseAllLocks(int slotIdx, object owner)
    {
        lock (_lock)
        {
            _slots[slotIdx].Locks?.ReleaseAll(owner);
        }
    }

    // ═══════════════ FileExtra 槽访问（§3.6——锁内原子；写=整替换数组，读=快照引用）═══════════════

    /// <summary>槽 FileExtra 快照（null = 无）。</summary>
    internal byte[]? ReadSlotExtra(int slotIdx)
    {
        lock (_lock)
            return _slots[slotIdx].FileExtra;
    }

    /// <summary>槽 FileExtra 整体替换（SetFileExtra 通道）。</summary>
    internal void WriteSlotExtra(int slotIdx, ReadOnlySpan<byte> value)
    {
        lock (_lock)
            _slots[slotIdx].FileExtra = value.ToArray();
    }

    /// <summary>
    /// 槽 FileExtra 偏移写（pwrite 契约：原位覆写/越尾零扩展；锁内 RMW = 真原子——§3.6 介质矩阵）。
    /// 返回新长度。预算由调用方前置校验（offset+len ≤ MaxFileExtraBytes）。
    /// </summary>
    internal int WriteSlotExtraRange(int slotIdx, long offset, ReadOnlySpan<byte> data)
    {
        lock (_lock)
        {
            ref var slot = ref _slots[slotIdx];
            var cur = slot.FileExtra;
            var newLen = (int)Math.Max(cur?.Length ?? 0, offset + data.Length);
            var blob = new byte[newLen];
            cur?.AsSpan().CopyTo(blob);
            data.CopyTo(blob.AsSpan((int)offset));
            slot.FileExtra = blob;
            return newLen;
        }
    }

    /// <summary>映射注册（RefCount++ 钉住槽）。</summary>
    internal void RegisterMap(int slotIdx)
    {
        lock (_lock)
        {
            _slots[slotIdx].RefCount++;
        }
    }

    /// <summary>映射释放（RefCount--；Detached 归零回收）。</summary>
    internal void ReleaseMap(int slotIdx)
    {
        lock (_lock)
        {
            _slots[slotIdx].RefCount--;
        }
        TryRetireSlot(slotIdx);
    }

    /// <summary>句柄关闭：注销共享注册 + 释放其范围锁 + RefCount--（Detached 归零回收）。</summary>
    internal void ReleaseHandle(MemFileHandle handle, int slotIdx)
    {
        lock (_lock)
        {
            ref var slot = ref _slots[slotIdx];
            slot.RefCount--;
            _slots[slotIdx].Locks?.ReleaseAll(handle);
            if (slot.OpenRegistry is not null && handle.OpenRegistryEntry is { } entry)
                slot.OpenRegistry.Remove(entry);
        }
        TryRetireSlot(slotIdx);
    }

    /// <summary>句柄 xattr/枚举用——槽当前活跃（Live/Detached 均可）。</summary>
    internal bool SlotAlive(int slotIdx) => _slots[slotIdx].State != SlotState.Empty;

    // ═══════════════ 记账与配额 ═══════════════

    private byte[] RentPhysical(int size, bool zeroMemory = true)
    {
        var buffer = _pool.Rent(size, zeroMemory);   // zeroMemory=false：调用方保证全页定义（见 WriteSparseNoLock）
        try
        {
            AccountPhysical(buffer.Length);
        }
        catch
        {
            _pool.Return(buffer);   // 配额拒绝——已租 buffer 归还，不留泄漏
            throw;
        }
        return buffer;
    }

    private void AccountPhysical(long bytes)
    {
        var cap = _options.QuotaBytes;
        if (cap <= 0) return;   // -1 = 无上限
        var usage = Interlocked.Add(ref _physicalUsage, bytes);
        if (usage > cap)
        {
            Interlocked.Add(ref _physicalUsage, -bytes);
            throw new FileIOException(IOError.DiskFull,
                $"内存卷配额耗尽（capacity={cap}, usage={usage - bytes}, request={bytes}）。", null, "quota");
        }
    }

    private void UnaccountPhysical(long bytes) => Interlocked.Add(ref _physicalUsage, -bytes);

    internal long AllocatedSizeOf(int slotIdx)
    {
        lock (_lock)
        {
            var slot = _slots[slotIdx];
            if (slot.Layout is { } layout)
            {
                using var gate = layout.Gate.EnterShared();   // 页数与并发插页一致（锁序 _lock→Gate 合法）
                return (long)layout.Pages.Count * layout.PageSize;   // 物理真值
            }
            return slot.Ranges is { } ranges ? ranges.Sum(static r => r.End - r.Start) : 0;
        }
    }

    internal IReadOnlyCollection<(long Start, long End)> EnumerateAllocatedOf(int slotIdx)
    {
        lock (_lock)
        {
            var slot = _slots[slotIdx];
            if (slot.Layout is { } layout)
            {
                using var gate = layout.Gate.EnterShared();   // 页表派生视图与并发插页一致（锁序 _lock→Gate 合法）

                {
                    // 页表派生视图（块粒度对齐 PageSize）
                    var pages = layout.Pages.Keys.OrderBy(static k => k).ToArray();
                    var result = new List<(long Start, long End)>();
                    foreach (var page in pages)
                    {
                        var start = page * layout.PageSize;
                        if (result.Count > 0 && result[^1].End == start)
                            result[^1] = (result[^1].Start, start + layout.PageSize);
                        else
                            result.Add((start, start + layout.PageSize));
                    }
                    return result;
                }
            }
            return slot.Ranges?.ToArray() ?? [];
        }
    }

    private static void AddRangeNoLock(List<(long Start, long End)> ranges, long start, long end)
    {
        if (end <= start) return;
        var index = 0;
        while (index < ranges.Count && ranges[index].End < start) index++;
        while (index < ranges.Count && ranges[index].Start <= end)
        {
            start = Math.Min(start, ranges[index].Start);
            end = Math.Max(end, ranges[index].End);
            ranges.RemoveAt(index);
        }
        ranges.Insert(index, (start, end));
    }

    private static void RemoveRangeNoLock(List<(long Start, long End)> ranges, long start, long end)
    {
        if (end <= start) return;
        for (var i = ranges.Count - 1; i >= 0; i--)
        {
            var range = ranges[i];
            if (range.End <= start || range.Start >= end) continue;
            ranges.RemoveAt(i);
            if (range.Start < start) ranges.Insert(i++, (range.Start, start));
            if (range.End > end) ranges.Insert(i, (end, range.End));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(MemoryFileSystem), "内存卷已 Dispose（拔盘）——所有句柄操作失效。");
    }

    /// <summary>卷已拔盘（映射视图的悬垂检查用）。</summary>
    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    // ═══════════════ MemFileHandle 内部访问器 ═══════════════

    internal bool IsReserved => _options.Allocation == MemoryAllocationMode.Reserved;
    internal int PageSize => _options.PageSize;
    internal bool IsSparse => _options.Allocation == MemoryAllocationMode.Sparse;

    /// <summary>fs 内部锁（句柄结构操作复用）。</summary>
    internal object SyncRoot => _lock;

    /// <summary>槽访问（MemFileHandle 长度读取/映射创建——须持 SyncRoot）。</summary>
    internal ref FileSlot GetSlot(int slotIdx) => ref _slots[slotIdx];

    /// <summary>Sparse 物化快照读（Map 创建——读出 [offset, offset+length) 全量）。</summary>
    internal byte[] MaterializeSparse(int slotIdx, int gen, long offset, long length)
    {
        var result = new byte[length];
        lock (_lock)
        {
            var data = ReadSlotChecked(slotIdx, gen);
            if (data is null) ThrowStale(slotIdx, gen);
            var layout = _slots[slotIdx].Layout!;
            using var _gate = layout.Gate.EnterShared();   // 页路由读（锁序 _lock→Gate 合法）
            {
                var canRead = (int)Math.Min(length, Math.Max(0, data.Size - offset));
                if (canRead > 0)
                    ReadSparseNoLock(layout, offset, result.AsSpan(0, canRead));
            }
        }
        return result;
    }

    /// <summary>Sparse 物化写回（Map Flush/Dispose——fs 锁+Gate 双持临界区内原子完成：
    /// 长度推进与页写入同临界区——无中间态暴露，可见性顺序不变式由 Gate 互斥保证）。</summary>
    internal void WriteBackSparse(int slotIdx, int gen, long offset, ReadOnlySpan<byte> data)
    {
        lock (_lock)
        {
            var snap = ReadSlotChecked(slotIdx, gen);
            if (snap is null) ThrowStale(slotIdx, gen);
            var layout = _slots[slotIdx].Layout!;
            using var gate = layout.Gate.EnterExclusive();

            {
                if (offset + data.Length > _slots[slotIdx].Data!.Size)
                    _slots[slotIdx].Data = new SlotData(null, null, offset + data.Length);
                WriteSparseNoLock(layout, offset, data);
            }
        }
    }
}
