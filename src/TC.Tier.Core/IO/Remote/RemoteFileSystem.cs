using System.Collections.Concurrent;
using System.Text.Json;
using TC.Tier.Core.IO.Disk;
using TC.Tier.Core.IO.Mem;
using TC.Tier.Core.IO.Shared;

namespace TC.Tier.Core.IO.Remote;

/// <summary>
/// 远程文件系统——<see cref="IFileSystem"/> 第三介质桥（B3.3/B3.4）：注入的 <see cref="IObjectStore"/>
/// 升格为文件语义（staging 写回层 / 延迟加载 / multipart 编排 / fencing 卷锁）。
/// <para>★ 构造：<see cref="New(IObjectStore, RemoteFileSystemOptions, ILogger)"/> /
///   <see cref="Open(IObjectStore, RemoteFileSystemOptions, ILogger)"/> /
///   <see cref="OpenOrCreate(IObjectStore, RemoteFileSystemOptions, ILogger)"/>——store 注入
///   （S3ObjectStore / MemoryObjectStore / 任意实现），桥对厂商零知识。</para>
/// <para>★ 键模型：对象键 = <c>{KeyPrefix}{path}</c>（多引擎共桶隔离）；path 经 <see cref="PathValidator"/>
///   （三介质同一实现——越出前缀即 <see cref="ArgumentException"/>，不可能访问同桶其他前缀对象）。</para>
/// <para>★ Dispose 契约（磁盘方向）："离开目录"——Dispose 后 Open 抛 <see cref="ObjectDisposedException"/>；
///   已开句柄继续可用（staging 自持——远程数据在云端不在 fs 内存）；store 归调用方所有不代释放。</para>
/// <para>★ AcquireExclusive = <b>尽力型 fencing</b>（lock 对象 + 条件 PUT；仅防意外双开，不构成正确性保证——
///   时钟漂移可致提前抢锁、崩溃后保护真空 = 心跳超时窗口；引擎正确性由段表 lease 单写者协议承担）。</para>
/// </summary>
public sealed class RemoteFileSystem : IFileSystem
{
    private const string LockFileName = ".tier-volume-lock";   // 与 DiskFileSystem 同名（平权）

    // === 桥选项（静态共享——record 不可变；docs/sync-async-bridge.md §9 P2）===
    private static readonly SyncBridgeOptions SFsIoOpts = new() { Name = "remote-fs" };
    private static readonly SyncBridgeOptions SFsCopyOpts = new() { Name = "remote-fs-copy", TimeoutMs = 60_000 };

    private readonly IObjectStore _store;
    private readonly RemoteFileSystemOptions _options;
    private readonly ILogger? _logger;
    private readonly string _keyPrefix;
    private readonly AccessMode _access;                     // G2 挂载访问三态（总上包络）
    private volatile bool _quotaBaselineReady;               // G3 惰性基线就绪哨兵（CORE-27：锁外枚举的双检）
    private readonly object _quotaBaselineInit = new();      // 基线初始化互斥（并发首写只枚举一次）
    private long _quotaProjectedTotal;                       // 基线 + 投影净增长（写路径写前拒）
    private readonly Dictionary<string, long> _quotaKnownSizes = new(StringComparer.Ordinal);   // path → 已知/投影大小

    /// <summary>挂载访问三态（G2——RemoteFileHandle 经此过包络校验）。</summary>
    internal AccessMode Access => _access;
    private readonly IDisposable? _mountExclusiveLease;                       // G5：构造期 fencing 租约（Dispose 释放）
    private readonly ConcurrentDictionary<string, AppendCursor> _appendCursors = new();
    private readonly SharingRegistry _sharing = new();
    private readonly object _lockGate = new();
    private DiskFileSystem? _spillFs;
    private MemoryFileSystem? _memSpillFs;
    private string? _spillDir;
    private RemoteLease? _heldLease;
    private int _disposed;
    private readonly MaintenanceGate _maintenance = new();   // 根空间维护门闩（设计 §8——三介质共享核心件）

    /// <summary>维护门闩（句柄写族的拒绝入口——RemoteFileHandle 经此访问）。</summary>
    internal MaintenanceGate Maintenance => _maintenance;

    private RemoteFileSystem(IObjectStore store, RemoteFileSystemOptions options, ILogger? logger)
    {
        _store = store;
        _options = options;
        _logger = logger;
        _keyPrefix = options.KeyPrefix;
        _access = options.Access;
        if (options.Exclusive)
            _mountExclusiveLease = AcquireExclusive(TimeSpan.FromSeconds(30));   // G5：fencing 尽力型（构造期抢建+心跳）
    }

    /// <summary>构造核——注入 store 与配置（options 校验非法抛 <see cref="ArgumentException"/>）。
    /// OrphanUploadCleanup 非空时执行孤儿 multipart 会话启动扫描（§4.4——崩溃残留碎片回收）。
    /// 旧公共入口 Create 已退役（P2 收尾）：动词面 = New / Open / OpenOrCreate。</summary>
    private static RemoteFileSystem ConstructCore(IObjectStore store, RemoteFileSystemOptions? options, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        options ??= new RemoteFileSystemOptions();
        options.Validate();
        // IS-04：network 无预分配概念（对象存储无块布局——厂商配额）——显式请求 Full 不静默
        if (options.Preallocation == PreallocationMode.Full)
            throw new FileIOException(IOError.Unsupported,
                "network 无预分配概念（对象存储无块布局——厂商配额，FreeSpace=-1）：Preallocation=Full 显式请求不支持（能力位诚实不置）。",
                null, "mount");
        var fs = new RemoteFileSystem(store, options, logger);
        if (options.OrphanUploadCleanup is { } threshold)
            fs.CleanupOrphanUploads(threshold);
        return fs;
    }

    /// <summary>New = 创建空镜像（设计 §2.3）：前缀有内容即抛 <see cref="IOError.AlreadyExists"/>
    /// （枚举检查——防误覆盖既有命名空间）；label = 设置（标记对象写入一次，New 后不可变）。</summary>
    public static RemoteFileSystem New(IObjectStore store, RemoteFileSystemOptions? options = null, ILogger? logger = null)
    {
        var fs = ConstructCore(store, options, logger);
        try
        {
            if (SyncAsyncBridge.Run(ct => store.ListAsync(fs._keyPrefix, ct), SFsIoOpts).Count > 0)
                throw new FileIOException(IOError.AlreadyExists,
                    $"New 目标前缀已有内容：{fs._keyPrefix}（防误覆盖既有命名空间——打开既有请用 Open）。",
                    null, "remote-new");
            if (fs._options.Label is not null)
                fs.WriteLabelMarker(fs._options.Label);   // G1：New = 设置
            return fs;
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    /// <summary>Open = 打开既有命名空间视图（前缀存在性由内容推导——空前缀即空视图；零内容枚举）。
    /// label 非 null = 断言（与标记对象比对——不符即抛 fail-fast，§2.5）。</summary>
    public static RemoteFileSystem Open(IObjectStore store, RemoteFileSystemOptions? options = null, ILogger? logger = null)
    {
        var fs = ConstructCore(store, options, logger);
        try
        {
            if (fs._options.Label is not null)
            {
                var actual = fs.ReadLabelMarker();
                if (actual != fs._options.Label)
                    throw new FileIOException(IOError.NotFound,
                        $"label 校验不符：期望 '{fs._options.Label}'，前缀上实际 '{actual ?? "<无>"}'（spec label 在 Open = 断言）。",
                        null, "open-label-check");
            }
            return fs;
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    /// <summary>OpenOrCreate = 懒初始化糖（bind-any 终态——纯构造零探测）。
    /// label 语义：标记既有且不符即抛（校验）、缺省则写入（G1 建档）。</summary>
    public static RemoteFileSystem OpenOrCreate(IObjectStore store, RemoteFileSystemOptions? options = null, ILogger? logger = null)
    {
        var fs = ConstructCore(store, options, logger);
        try
        {
            if (fs._options.Label is not null)
            {
                var actual = fs.ReadLabelMarker();
                if (actual is null) fs.WriteLabelMarker(fs._options.Label);
                else if (actual != fs._options.Label)
                    throw new FileIOException(IOError.NotFound,
                        $"label 校验不符：期望 '{fs._options.Label}'，前缀上实际 '{actual}'（spec label 在挂载 = 断言）。",
                        null, "openorcreate-label-check");
            }
            return fs;
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 孤儿会话扫描：KeyPrefix 内进行中的 multipart 会话，发起早于阈值 → Abort（计数+告警日志）。
    /// InitiatedUtc 不可知的实现按"不误杀"跳过（在途会话零风险）。
    /// </summary>
    private void CleanupOrphanUploads(TimeSpan threshold)
    {
        try
        {
            // ★ CORE-26：走 SyncAsyncBridge（独立桥池 + 有界等待）——原裸 GetAwaiter().GetResult()
            //   无界阻塞公共线程（慢 store 卡死挂载线程；桥失败不阻断构造——调用方 try/catch 已收口）
            var sessions = SyncAsyncBridge.Run(
                ct => _store.ListMultipartUploadsAsync(ct), SFsIoOpts);
            var cutoff = DateTimeOffset.UtcNow - threshold;
            var cleaned = 0;
            foreach (var session in sessions)
            {
                if (!session.Key.StartsWith(_keyPrefix, StringComparison.Ordinal)) continue;
                if (session.InitiatedUtc is not { } initiated || initiated > cutoff) continue;   // 不误杀
                SyncAsyncBridge.Run(
                    ct => _store.AbortMultipartUploadAsync(session.Key, session.UploadId, ct), SFsIoOpts);
                cleaned++;
            }
            if (cleaned > 0)
                _logger?.LogWarning("孤儿 multipart 会话启动清理：{Cleaned} 个（阈值 {Threshold:F0}s，KeyPrefix={Prefix}）",
                    cleaned, threshold.TotalSeconds, _keyPrefix);
        }
        catch (Exception ex)
        {
            // 清理失败不阻断 fs 构造（服务可用优先——残留碎片仅计费，下次构造重试）
            _logger?.LogWarning(ex, "孤儿 multipart 会话扫描失败（不阻断构造）");
        }
    }

    // ═════════════════════════════ 内部访问器 ═════════════════════════════

    internal IObjectStore Store => _store;

    // ═══════════════ G3：惰性配额（opt-in——QuotaBytes > 0 才激活；不设零成本）═══════════════

    /// <summary>
    /// 写路径投影执法（写前拒）：path 的逻辑长度将变为 <paramref name="projectedLength"/>——
    /// 超出配额即抛 DiskFull。基线惰性：首个执法点枚举前缀对象一次（天然卷级——ListObjects 看得见全部写者）。
    /// </summary>
    internal void QuotaProject(string path, long projectedLength)
    {
        if (_options.QuotaBytes <= 0) return;   // -1 = 无上限：零成本不枚举
        EnsureQuotaBaseline();   // ★ CORE-27：锁外枚举（网络往返不持 _quotaKnownSizes——原锁内枚举
                                 //   阻塞全部配额路径；双检哨兵保证并发首写只枚举一次）
        lock (_quotaKnownSizes)
        {
            var known = _quotaKnownSizes.TryGetValue(path, out var size) ? size : 0;
            var delta = projectedLength - known;
            if (delta <= 0) return;
            if (_quotaProjectedTotal + delta > _options.QuotaBytes)
                throw new FileIOException(IOError.DiskFull,
                    $"网络配额收紧：投影总量 {_quotaProjectedTotal} + 增量 {delta} > 上限 {_options.QuotaBytes}" +
                    "（惰性基线 + 写前拒，medium-protocol §5.3——基线含全部写者对象）", path, "quota");
            _quotaKnownSizes[path] = projectedLength;
            _quotaProjectedTotal += delta;
        }
    }

    /// <summary>删除/移动后回收投影（尽力——配额口径随之调整）。</summary>
    internal void QuotaRelease(string path)
    {
        if (_options.QuotaBytes <= 0) return;
        lock (_quotaKnownSizes)
        {
            if (_quotaKnownSizes.Remove(path, out var size))
                _quotaProjectedTotal -= size;
        }
    }

    /// <summary>★ CORE-27：基线初始化（锁外枚举——网络往返不持锁；双检哨兵 + 互斥锁保证并发首写只枚举一次；
    /// 等待者阻塞在 _quotaBaselineInit（配额执法正确性必需——基线就绪才能投影）。</summary>
    private void EnsureQuotaBaseline()
    {
        if (_quotaBaselineReady) return;
        lock (_quotaBaselineInit)
        {
            if (_quotaBaselineReady) return;
            long sum = 0;
            var known = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var info in EnumerateFiles("*", recursive: true))
            {
                sum += info.Length;
                known[info.Name] = info.Length;
            }
            lock (_quotaKnownSizes)
            {
                foreach (var kv in known) _quotaKnownSizes[kv.Key] = kv.Value;
                _quotaProjectedTotal = sum;
                _quotaBaselineReady = true;
            }
            _logger?.LogInformation("网络配额基线建立：{Objects} 对象，{Bytes} 字节（KeyPrefix={Prefix}）",
                known.Count, sum, _keyPrefix);
        }
    }

    // ═══════════════ G1：label 标记对象（{prefix}.tier-volume——点前缀隐藏类）═══════════════

    /// <summary>写 label 标记（New/构造时——服务端小对象 PUT）。</summary>
    internal void WriteLabelMarker(string label)
        => SyncAsyncBridge.Run(ct => _store.PutAsync(LabelMarkerKey,
            System.Text.Encoding.UTF8.GetBytes(label), metadata: null, condition: null, ct), SFsIoOpts);

    /// <summary>读 label 标记（不存在 → null；网络错误上抛）。</summary>
    internal string? ReadLabelMarker()
    {
        // Head 探存在 + Range GET 取内容（GetAsync 读 256 字节缓冲——label ≤32B 绰绰）
        var head = SyncAsyncBridge.Run(ct => _store.HeadAsync(LabelMarkerKey, ct), SFsIoOpts);
        if (head is null) return null;
        var buf = new byte[Math.Min(256, (int)Math.Max(1, head.Size))];
        var n = SyncAsyncBridge.Run(ct => _store.GetAsync(LabelMarkerKey, 0, buf, ct), SFsIoOpts);
        return System.Text.Encoding.UTF8.GetString(buf.AsSpan(0, n));
    }

    private string LabelMarkerKey => _keyPrefix + ".tier-volume";
    private string? _labelMarkerCache;
    private bool _labelMarkerRead;

    private string? ReadLabelMarkerCached()
    {
        if (_labelMarkerRead) return _labelMarkerCache;
        _labelMarkerCache = ReadLabelMarker();
        _labelMarkerRead = true;
        return _labelMarkerCache;
    }

    internal RemoteFileSystemOptions Options => _options;

    /// <summary>
    /// spill 后端文件系统（lazy）：磁盘 = DiskFileSystem 自举（SpillDirectory 下 per-fs 子目录）；
    /// 无盘 = fs 级私有 MemoryFileSystem（SpillToMemory——配额 = StagingMemoryLimit × 8 上界放大）。
    /// </summary>
    internal IFileSystem? SpillFileSystem
    {
        get
        {
            if (_options.Spill is { IsMemory: true })
                return _memSpillFs ??= MemoryFileSystem.New(
                    new MemoryFileSystemOptions
                    {
                        QuotaBytes = _options.StagingMemoryLimit * 8,   // spill 侧上界（预算放大系数）
                    });
            if (_options.Spill is not { } spill) return null;
            if (_spillFs is not null) return _spillFs;
            _spillDir = Path.Combine(spill.Directory!, $"tier-remote-{Guid.NewGuid():N}");
            var diskFs = DiskFileSystem.OpenOrCreate(_spillDir);   // bind-any 自举（per-fs 子目录）
            diskFs.EnsureRoot();   // 目录幂等创建（磁盘根是部署路径——不预先存在）
            _spillFs = diskFs;
            return _spillFs;
        }
    }

    /// <summary>句柄真关闭回调——注销共享登记 + 释放该句柄全部范围锁（G8——泄漏防护）。</summary>
    internal void OnHandleClosed(RemoteFileHandle handle)
    {
        _sharing.Unregister(handle.Path, handle.SharingEntry!);
        lock (_rangeLockGate)
        {
            if (_rangeLocks.TryGetValue(handle.Path, out var table))
            {
                table.ReleaseAll(handle);
                if (table.IsEmpty) _rangeLocks.Remove(handle.Path);
            }
        }
    }

    // ═══════════════ G8：进程内 advisory 范围锁（medium-protocol §5.9 翻案——与 mem 同构）═══════════════

    private readonly object _rangeLockGate = new();
    private readonly Dictionary<string, RangeLockTable> _rangeLocks = new(StringComparer.Ordinal);

    /// <summary>获取范围锁（blocking=false 即 Try 语义）。仅约束同进程同 fs 实例句柄（advisory——差异声明）。</summary>
    internal bool LockRange(string path, long offset, long length, FileLockMode mode, bool blocking, object owner)
    {
        while (true)
        {
            object waitGate;
            lock (_rangeLockGate)
            {
                var table = _rangeLocks.TryGetValue(path, out var t) ? t : _rangeLocks[path] = new RangeLockTable();
                if (table.TryAcquire(offset, length, mode, owner)) return true;
                if (!blocking) return false;
                waitGate = table.ChangedGate;   // ★ CORE-20：锁内取信号引用（同 mem 修复——条件变量替代 15ms 轮询）
            }
            lock (waitGate)
                Monitor.Wait(waitGate, 50);   // 等待期间零 _rangeLockGate 抢占；50ms 分片兜底丢脉冲
        }
    }

    internal void UnlockRange(string path, long offset, long length, object owner)
    {
        lock (_rangeLockGate)
        {
            if (_rangeLocks.TryGetValue(path, out var table))
                table.Release(offset, length, owner);
        }
    }

    /// <summary>文件级追加预留的权威复位（SetLength 后——追加从新末端继续）。</summary>
    internal void OnFileLengthChanged(string path, long newLength)
    {
        if (_appendCursors.TryGetValue(path, out var cursor))
            Interlocked.Exchange(ref cursor.Value, newLength);
    }

    internal string KeyOf(string path)
    {
        PathValidator.ValidateRelative(path, _keyPrefix);   // 根空间层级相对路径（'/' 进对象键即前缀层级）
        var key = _keyPrefix + path;
        ObjectKeyValidator.Validate(key);
        return key;
    }

    // ═════════════════════════════ IFileSystem 平面 ═════════════════════════════

    /// <inheritdoc/>
    /// <remarks>置位：DurableRename（服务端 Copy+Delete）/ ExclusiveLock（尽力型 fencing）/
    /// Advise（桥级预取模拟）/ CopyRange（恒置位——快路径条件见 io.md）/
    /// RangeLock（G8：进程内 advisory 区间表——与 mem 同构，仅约束同进程同实例句柄）/
    /// Mmap（G11：物化映射——Read=Range GET 快照 / ReadWrite=staging 视图写回；物化成本悬崖见 io.md 差异表）。
    /// 不置：Sparse/DirectIO/WriteThrough/FlushDataOnly/RangeShift/VectorIO/RandomWrite。
    /// （ExtendedAttrs 位已退役——FileExtra 平面无条件可用，§3.6。）</remarks>
    public FileSystemCapabilities Capabilities =>
        FileSystemCapabilities.DurableRename
        | FileSystemCapabilities.ExclusiveLock
        | FileSystemCapabilities.Advise
        | FileSystemCapabilities.CopyRange
        | FileSystemCapabilities.RangeLock
        | FileSystemCapabilities.Mmap
        | FileSystemCapabilities.MaintenanceGate;

    /// <inheritdoc/>
    /// <remarks>SectorSize=1 / AllocationUnit=<b>1</b>（staging memset 无物理对齐约束——块粒度打洞平权）；
    /// FreeSpace/TotalSpace 不可知（厂商配额）→ -1。</remarks>
    public VolumeInfo Volume => new()
    {
        SectorSize = 1, AllocationUnit = 1, FreeSpace = -1, TotalSpace = -1,   // 对象存储几何不透明
        // §5.4 完整自描述——label 惰性读标记对象（首查一次 GET 后缓存）
        Label = _options.Label ?? ReadLabelMarkerCached(),
        Nature = StorageNature.Network,
        SubKind = _options.SubKind,
        Access = _access,
        QuotaBytes = _options.QuotaBytes,
        UsedBytes = _quotaBaselineReady ? _quotaProjectedTotal : -1,   // 惰性——配额激活后精确（CORE-27 哨兵）
    };

    /// <inheritdoc/>
    /// <remarks>★ 打开已有文件 = 延迟加载：Open 仅 Head 记长度（零下载）；首次读写未物化区间按需 Range GET。
    /// 读句柄长度 = Open 时快照（不追新——需要追新重新 Open）。</remarks>
    public IFileHandle Open(string path, FileOpenOptions options)
    {
        Shared.AccessGate.CheckHandleOpen(_access, options.Access, path);   // G2 包络：构造期 fail-fast
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        // 维护门闩：写意图打开按变异拒绝（All 档连读意图一并拒）——句柄打开本身是原子的，立即退出在途计数
        if (options.Access == AccessMode.Read)
            _maintenance.ThrowIfReadsRejected(nameof(Open), path);
        else
            using (_maintenance.BeginMutation(nameof(Open), path)) { }
        KeyOf(path);
        options.Validate();

        var key = _keyPrefix + path;
        var existing = SyncAsyncBridge.Run(ct => _store.HeadAsync(key, ct), SFsIoOpts);
        var needsWrite = options.Access is AccessMode.Write or AccessMode.ReadWrite;
        if (existing is null)
        {
            if (options.Mode == FileOpenMode.OpenExisting)
                throw new FileIOException(IOError.NotFound, $"文件不存在: {path}", path, "Open");
        }
        else if (options.Mode == FileOpenMode.CreateNew)
        {
            throw new FileIOException(IOError.AlreadyExists, $"文件已存在: {path}", path, "Open");
        }
        if (existing is null && !needsWrite && options.Mode == FileOpenMode.OpenOrCreate)
        {
            // 只读 OpenOrCreate × 缺失：建空对象（与 mem 的"建空文件后只读句柄"平权）
            SyncAsyncBridge.Run(ct => _store.PutAsync(key, ReadOnlyMemory<byte>.Empty, ct: ct), SFsIoOpts);
            existing = new ObjectInfo(key, 0, null, ObjectMetadata.Empty);
        }

        // 同实例共享登记（advisory——双向检查，同 Disk/Mem）
        var entry = _sharing.Register(path, options.Access, options.Sharing);
        var handle = new RemoteFileHandle(this, path, options, existing)
        {
            SharingEntry = entry,
        };
        var initialCursor = handle.Length;
        var cursor = _appendCursors.GetOrAdd(path, _ => new AppendCursor());
        // ★ 游标只升不降（抬升协议）：历史经 Write 增长的文件，游标若落后实际长度 → 抬到长度
        //   （防追加落点回卷覆写）；游标领先长度 = 在途预留（保留不动）。
        while (true)
        {
            var current = Volatile.Read(ref cursor.Value);
            if (current >= initialCursor) break;
            if (Interlocked.CompareExchange(ref cursor.Value, initialCursor, current) == current) break;
        }
        handle.AttachAppendCursor(cursor);
        return handle;
    }

    /// <inheritdoc/>
    /// <remarks>桶即根（存在性是部署决策——桶必须预先存在）；幂等 no-op（契约对齐）。</remarks>
    public void EnsureRoot()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        Shared.AccessGate.RejectWrite(_access, nameof(EnsureRoot));   // 对象存储无目录项——结构写族同拒
    }

    /// <inheritdoc/>
    /// <remarks>对象存储无目录项——no-op（PUT 即原子持久，无需父目录刷盘等价物）。</remarks>
    public void FlushRoot()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        Shared.AccessGate.RejectWrite(_access, nameof(FlushRoot));
    }

    /// <inheritdoc/>
    public bool Exists(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _maintenance.ThrowIfReadsRejected(nameof(Exists), path);
        return SyncAsyncBridge.Run(ct => _store.HeadAsync(KeyOf(path), ct), SFsIoOpts) is not null;
    }

    /// <inheritdoc/>
    /// <remarks>幂等（对不存在仍成功——POSIX unlink 对齐）；AppendCursor 盒摘除（重建按新 Length）。</remarks>
    public void Delete(string path)
    {
        Shared.AccessGate.RejectWrite(_access, nameof(Delete));
        QuotaRelease(path);
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var gate = _maintenance.BeginMutation(nameof(Delete), path);
        SyncAsyncBridge.Run(ct => _store.DeleteAsync(KeyOf(path), condition: null, ct), SFsIoOpts);
        _appendCursors.TryRemove(path, out _);
    }

    /// <inheritdoc/>
    /// <remarks>服务端 Copy（同区零流量）+ Delete 源——>5GB 走 CopyRange 的 multipart 编排。
    /// ★ 未 Flush 的源句柄 staging 不随 Move（Flush 仍写旧键——远程差异，io.md 声明）。</remarks>
    public void Move(string source, string dest, bool overwrite = false)
    {
        Shared.AccessGate.RejectWrite(_access, nameof(Move));
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var _gate = _maintenance.BeginMutation(nameof(Move), source);
        var srcKey = KeyOf(source);
        var dstKey = KeyOf(dest);
        var src = SyncAsyncBridge.Run(ct => _store.HeadAsync(srcKey, ct), SFsIoOpts)
            ?? throw new FileIOException(IOError.NotFound, $"源文件不存在: {source}", source, nameof(Move));
        if (!overwrite && SyncAsyncBridge.Run(ct => _store.HeadAsync(dstKey, ct), SFsIoOpts) is not null)
            throw new FileIOException(IOError.AlreadyExists, $"目标已存在: {dest}", dest, nameof(Move));

        // CopyRangeAsync 内建 multipart 编排（≤5GB 单 part / 超出循环切分）——服务端零流量
        SyncAsyncBridge.Run(ct => _store.CopyRangeAsync(srcKey, dstKey, 0, src.Size, metadata: null, ct), SFsCopyOpts);
        SyncAsyncBridge.Run(ct => _store.DeleteAsync(srcKey, condition: null, ct), SFsIoOpts);
        _appendCursors.TryRemove(source, out _);   // 源名盒摘除（目标名按需重建——同数据同长度）
    }

    // ═════════════════════════════ 根空间目录族（S3 前缀模拟——filesystem-root-space-design §6）═════════════════════════════

    /// <summary>路径 → 列举前缀键（path+"/"）。</summary>
    private string ListPrefixOf(string path) => KeyOf(path) + "/";

    /// <inheritdoc/>
    /// <remarks>S3 前缀模拟：<b>文档化 no-op</b>（目录因内容而存在——EmptyDirectories 不置位）。
    /// 预留名校验仍在 KeyOf 完成（路径合法性即时失败）——但维护门闩仍拒（契约统一：命名空间变异请求）。</remarks>
    public void CreateDirectory(string path)
    {
        AccessGate.RejectWrite(_access, nameof(CreateDirectory));
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var gate = _maintenance.BeginMutation(nameof(CreateDirectory), path);
        KeyOf(path);
    }

    /// <inheritdoc/>
    /// <remarks>前缀下有对象或子前缀 = 非空（NotEmpty 抛——rmdir 安全边界）；
    /// 空/不存在 = 成功 no-op（S3 无空目录——删完子项后的空目录无对象可删；正常流程
    /// "删子项→删目录"不再必然失败——CORE-17 死 API 修复；幂等删除对齐 Delete 文件倾向）。</remarks>
    public void DeleteDirectory(string path)
    {
        AccessGate.RejectWrite(_access, nameof(DeleteDirectory));
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _maintenance.ThrowIfReadsRejected(nameof(DeleteDirectory), path);   // 实为列举判定（读路径）+ 拒绝态联检
        using var gate = _maintenance.BeginMutation(nameof(DeleteDirectory), path);
        var prefix = ListPrefixOf(path);
        var listing = SyncAsyncBridge.Run(
            ct => _store.ListDelimitedAsync(prefix, "/", ct), SFsIoOpts);
        if (listing.Objects.Count == 0 && listing.CommonPrefixes.Count == 0)
            return;   // ★ CORE-17：空/不存在 = 成功（原 NotFound 抛——删完子项后的正常流程必失败 = 死 API）
        if (listing.Objects.Count > 0 || listing.CommonPrefixes.Count > 0)
            throw new FileIOException(IOError.DirectoryNotEmpty, $"目录非空: {path}", path, nameof(DeleteDirectory));
    }

    /// <inheritdoc/>
    /// <remarks>前缀下有对象或子前缀即存在（delimiter 单次列举判定）。</remarks>
    public bool DirectoryExists(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _maintenance.ThrowIfReadsRejected(nameof(DirectoryExists), path);
        var prefix = ListPrefixOf(path);
        var listing = SyncAsyncBridge.Run(
            ct => _store.ListDelimitedAsync(prefix, "/", ct), SFsIoOpts);
        return listing.Objects.Count > 0 || listing.CommonPrefixes.Count > 0;
    }

    /// <inheritdoc/>
    /// <remarks>★ 回退语义（AtomicDirectoryMove 不置位）：前缀全量 Copy+Delete——<b>非原子</b>，
    /// 部分失败有残留（已迁移对象在新前缀、余者在旧前缀——消费者按枚举幂等重放）。</remarks>
    public void MoveDirectory(string source, string dest)
    {
        AccessGate.RejectWrite(_access, nameof(MoveDirectory));
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var gate = _maintenance.BeginMutation(nameof(MoveDirectory), source);
        var srcPrefix = ListPrefixOf(source);
        var dstPrefix = ListPrefixOf(dest);
        if (!DirectoryExists(source))
            throw new FileIOException(IOError.NotFound, $"源目录不存在: {source}", source, nameof(MoveDirectory));
        if (SyncAsyncBridge.Run(ct => _store.HeadAsync(dstPrefix[..^1], ct), SFsIoOpts) is not null
            || DirectoryExists(dest))
            throw new FileIOException(IOError.AlreadyExists,
                $"MoveDirectory 目标已存在: {dest}（不提供 overwrite）。", dest, nameof(MoveDirectory));
        var entries = SyncAsyncBridge.Run(ct => _store.ListAsync(srcPrefix, ct), SFsIoOpts);
        foreach (var e in entries)
        {
            var destKey = dstPrefix + e.Key[srcPrefix.Length..];
            SyncAsyncBridge.Run(ct => _store.CopyRangeAsync(e.Key, destKey, 0, e.Size, metadata: null, ct), SFsCopyOpts);
            SyncAsyncBridge.Run(ct => _store.DeleteAsync(e.Key, condition: null, ct), SFsIoOpts);
        }
    }

    // ═════════════════════════════ 文件创建 + Stat ═════════════════════════════

    /// <inheritdoc/>
    /// <remarks>PUT 空对象单请求（元数据随 PUT 原子提交）；preallocateSize 服务端无稀疏长度概念——
    /// 记名接收、服务端 no-op（staging 级预分配走 Open(options.PreallocateSize) 既有协议）。</remarks>
    public void CreateFile(string path, long preallocateSize = 0, ReadOnlyMemory<byte> extra = default)
    {
        Shared.AccessGate.RejectWrite(_access, nameof(CreateFile));
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var _gate = _maintenance.BeginMutation(nameof(CreateFile), path);
        if (extra.Length > IFileSystem.MaxFileExtraBytes)
            throw new ArgumentException($"FileExtra 超限（{extra.Length} > {IFileSystem.MaxFileExtraBytes}）。", nameof(extra));
        var key = KeyOf(path);
        if (SyncAsyncBridge.Run(ct => _store.HeadAsync(key, ct), SFsIoOpts) is not null)
            throw new FileIOException(IOError.AlreadyExists, $"文件已存在: {path}", path, nameof(CreateFile));
        var objMeta = extra.IsEmpty
            ? null
            : ObjectMetadata.Create(new Dictionary<string, string>
              {
                  [MetadataKey] = Convert.ToBase64String(extra.Span),
              });
        SyncAsyncBridge.Run(ct => _store.PutAsync(key, ReadOnlyMemory<byte>.Empty, objMeta, condition: null, ct), SFsIoOpts);
    }

    /// <summary>FileExtra 在对象用户元数据中的传输键（base64 编码 ≤1.5K 原始字节；FileNative.XattrName 单一事实源——实现细节非契约）。</summary>
    private const string MetadataKey = FileNative.XattrName;

    /// <inheritdoc/>
    /// <remarks>文件 = HeadObject（Size/Metadata/LastModified）；目录 = 前缀有内容（时间不可得 MinValue/null）。</remarks>
    public FsEntryInfo Stat(string path)
    {
        Shared.AccessGate.RejectRead(_access, nameof(Stat));
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _maintenance.ThrowIfReadsRejected(nameof(Stat), path);
        var key = KeyOf(path);
        var info = SyncAsyncBridge.Run(ct => _store.HeadAsync(key, ct), SFsIoOpts);
        if (info is not null)
        {
            var meta = TryGetUserMetadata(info.Metadata.UserMetadata, MetadataKey) is { } b64
                ? Convert.FromBase64String(b64)
                : Array.Empty<byte>();
            return new FsEntryInfo(FsEntryType.File, path, info.Size,
                info.LastModified ?? DateTimeOffset.MinValue, null, meta);   // S3 LastModified（不可得诚实 MinValue）
        }
        if (DirectoryExists(path))
            return new FsEntryInfo(FsEntryType.Directory, path, 0,
                DateTimeOffset.MinValue, null, ReadOnlyMemory<byte>.Empty);
        throw new FileIOException(IOError.NotFound, $"条目不存在: {path}", path, nameof(Stat));
    }

    // ═════════════════════════════ 枚举族（ListDelimited 前缀模拟）═════════════════════════════

    /// <inheritdoc/>
    public IEnumerable<FsEntry> EnumerateFiles(string pattern = "*", bool recursive = false)
    {
        Shared.AccessGate.RejectRead(_access, nameof(EnumerateFiles));
        return EnumerateCore(null, pattern, recursive, EntryFilter.Files);
    }

    /// <inheritdoc/>
    public IEnumerable<FsEntry> EnumerateFiles(string path, string pattern, bool recursive = false)
    {
        Shared.AccessGate.RejectRead(_access, nameof(EnumerateFiles));
        return EnumerateCore(path, pattern, recursive, EntryFilter.Files);
    }

    /// <inheritdoc/>
    public IEnumerable<FsEntry> EnumerateDirectories(string pattern = "*", bool recursive = false)
    {
        Shared.AccessGate.RejectRead(_access, nameof(EnumerateDirectories));
        return EnumerateCore(null, pattern, recursive, EntryFilter.Directories);
    }

    /// <inheritdoc/>
    public IEnumerable<FsEntry> EnumerateDirectories(string path, string pattern, bool recursive = false)
    {
        Shared.AccessGate.RejectRead(_access, nameof(EnumerateDirectories));
        return EnumerateCore(path, pattern, recursive, EntryFilter.Directories);
    }

    /// <inheritdoc/>
    public IEnumerable<FsEntry> EnumerateEntries(string pattern = "*", bool recursive = false)
    {
        Shared.AccessGate.RejectRead(_access, nameof(EnumerateEntries));
        return EnumerateCore(null, pattern, recursive, EntryFilter.Both);
    }

    /// <inheritdoc/>
    public IEnumerable<FsEntry> EnumerateEntries(string path, string pattern, bool recursive = false)
    {
        Shared.AccessGate.RejectRead(_access, nameof(EnumerateEntries));
        return EnumerateCore(path, pattern, recursive, EntryFilter.Both);
    }

    private enum EntryFilter { Files, Directories, Both }

    /// <summary>
    /// 枚举核：非递归 = ListDelimited（delimiter='/' 一层）——混合族一次往返（Objects+CommonPrefixes）；
    /// 递归 = ListAsync 全前缀（文件）+ 逐层 delimiter 目录推导。模式匹配客户端过滤（最终组件名）。
    /// </summary>
    private List<FsEntry> EnumerateCore(string? path, string pattern, bool recursive, EntryFilter filter)   // 锁内物化 List——返回具体型避免接口多层枚举分配（CA1859）
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _maintenance.ThrowIfReadsRejected("Enumerate", path);
        Shared.PathPattern.Validate(pattern);
        var prefix = path is null ? _keyPrefix : ListPrefixOf(path);
        var showHidden = Shared.PathPattern.HiddenExempt(pattern);   // §3.5 隐藏类豁免（A 方案）
        var result = new List<FsEntry>();

        if (recursive)
        {
            // 全前缀文件（递归多组件名）+ 目录由键推导
            var entries = SyncAsyncBridge.Run(
                ct => _store.ListAsync(string.IsNullOrEmpty(prefix) ? null : prefix, ct), SFsIoOpts);
            var dirs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in entries)
            {
                var rest = e.Key[_keyPrefix.Length..];
                if (rest.Length == 0) continue;
                if (path is not null && !e.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var rel = e.Key[prefix.Length..];
                for (var i = rel.IndexOf('/'); i >= 0; i = rel.IndexOf('/', i + 1))
                    dirs.Add(prefix + rel[..i]);
                if (!showHidden && Shared.PathPattern.IsHiddenRelative(rel)) continue;   // 隐藏类（§3.5）
                if (filter != EntryFilter.Directories
                    && Shared.PathPattern.IsMatch(LastComponent(rel), pattern))
                    result.Add(new FsEntry(FsEntryType.File, rel, e.Size,
                        e.LastModified ?? DateTimeOffset.MinValue, null));   // ListObjectsV2 携带 LastModified
            }
            if (filter != EntryFilter.Files)
                foreach (var d in dirs)
                {
                    var rel = d[prefix.Length..];
                    if (!showHidden && Shared.PathPattern.IsHiddenRelative(rel)) continue;   // 隐藏类（§3.5）
                    if (Shared.PathPattern.IsMatch(LastComponent(rel), pattern))
                        result.Add(new FsEntry(FsEntryType.Directory, rel, 0, DateTimeOffset.MinValue, null));
                }
        }
        else
        {
            // 一层：单次 delimiter 列举（混合族一次往返）
            var listing = SyncAsyncBridge.Run(
                ct => _store.ListDelimitedAsync(
                    string.IsNullOrEmpty(prefix) ? null : prefix, "/", ct), SFsIoOpts);
            if (filter != EntryFilter.Directories)
                foreach (var e in listing.Objects)
                {
                    var rel = e.Key[prefix.Length..];
                    if (rel.Length == 0) continue;
                    if (!showHidden && Shared.PathPattern.IsHiddenRelative(rel)) continue;   // 隐藏类（§3.5）
                    if (!Shared.PathPattern.IsMatch(rel, pattern)) continue;
                    result.Add(new FsEntry(FsEntryType.File, rel, e.Size,
                        e.LastModified ?? DateTimeOffset.MinValue, null));
                }
            if (filter != EntryFilter.Files)
                foreach (var cp in listing.CommonPrefixes)
                {
                    var rel = cp[prefix.Length..].TrimEnd('/');   // 去尾分隔符
                    if (rel.Length == 0) continue;
                    if (!showHidden && Shared.PathPattern.IsHiddenRelative(rel)) continue;   // 隐藏类（§3.5）
                    if (!Shared.PathPattern.IsMatch(rel, pattern)) continue;
                    result.Add(new FsEntry(FsEntryType.Directory, rel, 0, DateTimeOffset.MinValue, null));
                }
        }

        // NotFound 平权：显式目录查询（非递归的空结果可能是目录不存在——delimiter 判定）
        if (result.Count == 0 && path is not null && !DirectoryExists(path))
            throw new FileIOException(IOError.NotFound, $"目录不存在: {path}", path, "Enumerate");
        result.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return result;
    }

    /// <summary>用户元数据键取值（精确优先 + OrdinalIgnoreCase 回退——AWS S3 服务端将键强制小写）。</summary>
    internal static string? TryGetUserMetadata(IReadOnlyDictionary<string, string> meta, string key)
    {
        if (meta.TryGetValue(key, out var exact)) return exact;
        foreach (var kv in meta)
        {
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        }
        return null;
    }

    private static ReadOnlySpan<char> LastComponent(string path)
    {
        var last = path.LastIndexOf('/');
        return last < 0 ? path.AsSpan() : path.AsSpan()[(last + 1)..];
    }

    /// <inheritdoc/>
    public IDisposable EnterMaintenance(string reason, MaintenanceScope scope, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        return _maintenance.Enter(reason, scope, ct);
    }

    /// <summary>尽力型 fencing 独占锁（lock 对象 + 条件 PUT 抢建 / 心跳超时接管 / 条件删除防误删）。</summary>
    /// <param name="timeout">抢锁等待上限（超时抛 <see cref="FileIOException"/>（SharingViolation））。</param>
    /// <returns>租约（RAII——Dispose 即释放停跳）。</returns>
    /// <remarks>★ <b>仅防意外双开</b>——无即死释放（崩溃后保护真空 = 心跳超时窗口）、
    /// 时钟漂移可提前接管；正确性由段表 lease 单写者协议承担（锁只是运营护栏）。</remarks>
    public IDisposable AcquireExclusive(TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (!_store.Capabilities.HasFlag(ObjectStoreCapabilities.ConditionalPut))
            throw new FileIOException(IOError.Unsupported,
                $"{nameof(AcquireExclusive)} 需要对象层条件 PUT（ConditionalPut）——当前 store 未置位（老端点可升级或换 store）。",
                null, nameof(AcquireExclusive));

        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        lock (_lockGate)
        {
            while (true)
            {
                var lease = TryAcquireOrTakeover();
                if (lease is not null)
                {
                    _heldLease = lease;
                    return lease;
                }
                if (Environment.TickCount64 >= deadline)
                    throw new FileIOException(IOError.SharingViolation,
                        $"AcquireExclusive timed out after {timeout.TotalMilliseconds:F0}ms（fencing 锁被其他持有者持有且心跳未超时）。",
                        null, nameof(AcquireExclusive));
                Thread.Sleep(15);
            }
        }
    }

    /// <inheritdoc/>
    /// <remarks>磁盘方向："离开目录"——卷锁违约释放（告警）+ spill 根清理；已开句柄不受影响（staging 自持）；
    /// store 归调用方（不代释放）。</remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _mountExclusiveLease?.Dispose();   // G5：挂载期 fencing 租约干净释放
        lock (_lockGate)
        {
            if (_heldLease is not null)
            {
                _logger?.LogWarning("fs Dispose 时 fencing 锁仍被持有（lease 未释放——违约，已强制释放）");
                _heldLease.ForceRelease();
                _heldLease = null;
            }
        }
        // spill 根清理（句柄级 spill 文件正常路径已自删——此处兜底残留）
        if (_spillFs is not null)
        {
            try
            {
                foreach (var f in _spillFs.EnumerateFiles())
                    _spillFs.Delete(f.Name);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "spill 根清理残留失败");
            }
            _spillFs.Dispose();
            _memSpillFs?.Dispose();
            if (_spillDir is not null)
            {
                try { Directory.Delete(_spillDir, false); } catch { /* 目录残留无害 */ }
            }
        }
    }

    // ═════════════════════════════ fencing 内部 ═════════════════════════════
    // ★ P2 桥接不变量：本区三个方法可能在 _lockGate 内 / Timer 线程上同步等桥操作——
    //   安全前提是「桥完成者（对象层 IO）绝不触碰 _lockGate」。此处无环成立；日后往 store
    //   路径加任何需要 _lockGate 的逻辑前，必须先重构锁结构（否则持锁等桥 = 死锁）。

    private string LockKey => _keyPrefix + LockFileName;

    private sealed record LockPayload(string Token, long HeartbeatUtcMs);

    private RemoteLease? TryAcquireOrTakeover()
    {
        var token = Guid.NewGuid().ToString("N");
        try
        {
            // 抢建
            SyncAsyncBridge.Run(ct => _store.PutAsync(LockKey,
                JsonSerializer.SerializeToUtf8Bytes(new LockPayload(token, CurrentUnixMs())),
                condition: new PutCondition(IfMatch: null, IfNoneMatch: "*"), ct: ct), SFsIoOpts);
            return new RemoteLease(this, token);
        }
        catch (FileIOException ex) when (ex.Error == IOError.PreconditionFailed)
        {
            // 已被持有——心跳超时检测
        }

        var head = SyncAsyncBridge.Run(ct => _store.HeadAsync(LockKey, ct), SFsIoOpts);
        if (head is null) return null;   // 刚被释放——下轮抢建

        var buf = new byte[Math.Max(1, (int)head.Size)];
        SyncAsyncBridge.Run(ct => _store.GetAsync(LockKey, 0, buf, ct), SFsIoOpts);
        LockPayload? payload = null;
        try { payload = JsonSerializer.Deserialize<LockPayload>(buf); } catch { /* 畸形锁——按超时处理 */ }
        if (payload is not null && CurrentUnixMs() - payload.HeartbeatUtcMs < (long)_options.LeaseTimeout.TotalMilliseconds)
            return null;   // 持有者存活——等待

        // 接管：CAS（IfMatch = 当前 ETag——防与并发接管者双吃）+ 二次内容校验
        try
        {
            SyncAsyncBridge.Run(ct => _store.PutAsync(LockKey,
                JsonSerializer.SerializeToUtf8Bytes(new LockPayload(token, CurrentUnixMs())),
                condition: new PutCondition(head.ETag, null), ct: ct), SFsIoOpts);
            _logger?.LogWarning("fencing 锁接管（原持有者心跳超时 {Timeout:F0}s）", _options.LeaseTimeout.TotalSeconds);
            return new RemoteLease(this, token);
        }
        catch (FileIOException ex) when (ex.Error is IOError.PreconditionFailed or IOError.NotFound)
        {
            return null;   // 接管竞态失败——下轮
        }
    }

    private static long CurrentUnixMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>心跳刷新（lease 周期驱动——失锁即告警停跳）。</summary>
    private void Heartbeat(string token)
    {
        try
        {
            var head = SyncAsyncBridge.Run(ct => _store.HeadAsync(LockKey, ct), SFsIoOpts);
            if (head is null) return;
            var buf = new byte[Math.Max(1, (int)head.Size)];
            var n = SyncAsyncBridge.Run(ct => _store.GetAsync(LockKey, 0, buf, ct), SFsIoOpts);
            var payload = n > 0 ? JsonSerializer.Deserialize<LockPayload>(buf.AsSpan(0, n).ToArray()) : null;
            if (payload?.Token != token)
            {
                _logger?.LogWarning("fencing 锁心跳发现失锁（token 已变更——被接管？），停止心跳");
                return;
            }
            SyncAsyncBridge.Run(ct => _store.PutAsync(LockKey,
                JsonSerializer.SerializeToUtf8Bytes(new LockPayload(token, CurrentUnixMs())),
                condition: new PutCondition(head.ETag, null), ct: ct), SFsIoOpts);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "fencing 锁心跳失败（下轮重试）");
        }
    }

    private void ReleaseLease(string token, bool force)
    {
        lock (_lockGate)
        {
            if (_heldLease is null) return;
            try
            {
                var head = SyncAsyncBridge.Run(ct => _store.HeadAsync(LockKey, ct), SFsIoOpts);
                if (head is null) return;
                var buf = new byte[Math.Max(1, (int)head.Size)];
                var n = SyncAsyncBridge.Run(ct => _store.GetAsync(LockKey, 0, buf, ct), SFsIoOpts);
                var payload = n > 0 ? JsonSerializer.Deserialize<LockPayload>(buf.AsSpan(0, n).ToArray()) : null;
                if (!force && payload?.Token != token)
                {
                    _logger?.LogWarning("fencing 锁释放跳过（token 已变更——不误删他人锁）");
                    return;
                }
                SyncAsyncBridge.Run(ct => _store.DeleteAsync(LockKey, new DeleteCondition(head.ETag), ct), SFsIoOpts);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "fencing 锁释放失败（超时自愈：心跳停止后可被接管）");
            }
            finally
            {
                _heldLease = null;
            }
        }
    }

    /// <summary>fencing lease——RAII（周期心跳 + Dispose 即释放停跳）。</summary>
    private sealed class RemoteLease : IDisposable
    {
        private readonly RemoteFileSystem _owner;
        private readonly string _token;
        private readonly Timer _heartbeat;
        private int _released;

        public RemoteLease(RemoteFileSystem owner, string token)
        {
            _owner = owner;
            _token = token;
            var interval = owner._options.HeartbeatInterval
                           ?? TimeSpan.FromTicks(owner._options.LeaseTimeout.Ticks / 3);
            _heartbeat = new Timer(_ =>
            {
                if (Volatile.Read(ref _released) == 0) owner.Heartbeat(token);
            }, null, interval, interval);
        }

        public void ForceRelease()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            _heartbeat.Dispose();
            _owner.ReleaseLease(_token, force: true);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            _heartbeat.Dispose();
            _owner.ReleaseLease(_token, force: false);
        }
    }
}
