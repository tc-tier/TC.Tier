using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using TC.Tier.Core.IO.Shared;

namespace TC.Tier.Core.IO.Disk;

/// <summary>
/// 磁盘文件系统——<see cref="FileNative"/> 的面向对象封口 + 根空间命名空间平面（层级相对路径，filesystem-root-space-design）。
/// <para>★ 构造（动词面 §2.3）：<see cref="New(string, DiskFileSystemOptions, ILogger?)"/> /
///   <see cref="Open(string, DiskFileSystemOptions, ILogger?)"/> /
///   <see cref="OpenOrCreate(string, DiskFileSystemOptions, ILogger?)"/>（懒初始化糖——bind-any 终态；
///   磁盘根目录是部署决策，无全局单例）。</para>
/// <para>★ 卷锁：lock file + FileShare.None（Win）/ flock（Unix）——跨进程互斥、崩溃自愈
///   （进程死亡后句柄关闭，后来者可重新获取）。</para>
/// <para>★ Dispose 契约（磁盘方向）："离开目录"——仅释放 fs 自持资源（卷锁违约释放并告警）；
///   不关闭消费者持有的句柄；Dispose 后 Open/命名空间操作抛 <see cref="ObjectDisposedException"/>。</para>
/// </summary>
public sealed class DiskFileSystem : IFileSystem
{
    private const string LockFileName = ".tier-volume-lock";

    private readonly string _root;
    private readonly ILogger? _logger;
    private readonly DiskMetadataMode _metaMode;
    private readonly PreallocationMode _preallocation;
    private int _metaChannelProbe;   // ExtendedAttr 模式惰性探测结果：0=未探，1=可用，-1=不可用
    private readonly PinnedBufferPool _ioBufferPool = new();
    private readonly object _lockGate = new();
    private const string LabelMarkerName = ".tier-volume";   // G1：空间根卷记录（点前缀隐藏类）
    private SafeFileHandle? _lockHandle;
    private readonly ConcurrentDictionary<string, AppendCursor> _appendCursors = new();
    private readonly SharingRegistry _sharing = new();   // 进程内 FileSharing 双向检查（Unix advisory 兑现；Win 双保险）
    private readonly MaintenanceGate _maintenance = new();   // 根空间维护门闩（设计 §8——三介质共享核心件）
    private int _disposed;
    private AccessMode _access = AccessMode.ReadWrite;       // 挂载访问三态（G2 总上包络——New/Open 应用）
    private long _quotaBytes;                                // G3：空间根上限（0 = 不设；写前拒·惰性基线）
    private long? _quotaBaselineTotal;                       // 惰性基线（首个执法点递归求和一次）
    private readonly Dictionary<string, long> _quotaKnownSizes = new(StringComparer.Ordinal);   // path → 已知/投影逻辑长度
    private readonly object _quotaLock = new();

    /// <summary>维护门闩（句柄写族的拒绝入口——DiskFileHandle 经此访问）。</summary>
    internal MaintenanceGate Maintenance => _maintenance;

    /// <summary>挂载访问三态（G2——DiskFileHandle.Map 经此过络校验）。</summary>
    internal AccessMode Access => _access;

    // ═══════════════ G3：磁盘配额（opt-in——写前拒·惰性基线·按实例记账）═══════════════

    /// <summary>配额是否启用（G3：0/-1 = 不设）——调用方据此避免白付 Length syscall（CORE-09）。</summary>
    internal bool QuotaEnabled => _quotaBytes > 0;

    /// <summary>写路径投影执法（写前拒）：path 逻辑长度将变为 projectedLength——配额内放行，超限 DiskFull。
    /// 协同写者模型下卷级准确（多实例打开各自对账基线）；记账单调（截断不回收——保守执法方向）。</summary>
    internal void QuotaProject(string path, long projectedLength)
    {
        if (_quotaBytes <= 0) return;   // 0/-1 = 不设：零成本不求基线
        lock (_quotaLock)
        {
            EnsureQuotaBaselineLocked();
            var known = _quotaKnownSizes.TryGetValue(path, out var size) ? size : 0;
            var delta = projectedLength - known;
            if (delta <= 0) return;
            if (_quotaProjectedTotal + delta > _quotaBytes)
                throw new FileIOException(IOError.DiskFull,
                    $"磁盘配额收紧：投影总量 {_quotaProjectedTotal} + 增量 {delta} > 上限 {_quotaBytes}" +
                    "（枚举基线 + 写前拒，medium-protocol §5.3——按实例记账）", path, "quota");
            _quotaKnownSizes[path] = projectedLength;
            _quotaProjectedTotal += delta;
        }
    }

    /// <summary>删除回收投影（Move 目标同回收源——移动不双计由调用方保证）。</summary>
    private void QuotaRelease(string path)
    {
        if (_quotaBytes <= 0) return;
        lock (_quotaLock)
        {
            if (_quotaKnownSizes.Remove(path, out var size))
                _quotaProjectedTotal -= size;
        }
    }

    private long _quotaProjectedTotal;

    private void EnsureQuotaBaselineLocked()
    {
        if (_quotaBaselineTotal is { } total)
        {
            return;
        }
        long sum = 0;
        foreach (var e in EnumerateFiles("*", recursive: true))
        {
            sum += e.Length;
            _quotaKnownSizes[e.Name] = e.Length;
        }
        _quotaBaselineTotal = sum;
        _quotaProjectedTotal = sum;
        _logger?.LogInformation("磁盘配额基线建立：{Files} 文件，{Bytes} 字节（root={Root}）",
            _quotaKnownSizes.Count, sum, _root);
    }

    private DiskFileSystem(string root, DiskMetadataMode metadataMode, PreallocationMode preallocation, ILogger? logger)
    {
        _root = root;
        _logger = logger;
        _metaMode = metadataMode;
        _preallocation = preallocation;
        _baseVolume = ProbeVolume(root);
        Capabilities = ProbeCapabilities(root);
    }

    /// <summary>构造核（绑定根目录——绝对路径，路径分隔符归一；不立即创建，<see cref="EnsureRoot"/> 幂等建）。
    /// 旧公共入口 Create 已退役（P2 收尾）：动词面 = New / Open / OpenOrCreate。</summary>
    private static DiskFileSystem BindCore(string root, DiskMetadataMode metadataMode, PreallocationMode preallocation, ILogger? logger)
    {
        PathValidator.ValidateRoot(root);
        var full = Path.GetFullPath(root);
        return new DiskFileSystem(full, metadataMode, preallocation, logger);
    }

    /// <summary>OpenOrCreate = 懒初始化糖（设计 §2.3 可选形态——显式表达"我接受两种状态"）：
    /// 根不存在则建（New 语义）、存在则开（Open 语义，不校验空否）——bind-any 场景的终态入口。
    /// label 语义随形态：既有根 = 校验（不符抛）、新根 = 写入。</summary>
    public static DiskFileSystem OpenOrCreate(string root, DiskFileSystemOptions? options = null, ILogger? logger = null)
    {
        options ??= new DiskFileSystemOptions();
        var fs = BindCore(root, options.MetadataMode, options.Preallocation, logger);
        var existed = Directory.Exists(fs._root);
        fs.EnsureRoot();   // 幂等建（ApplyOptions 前——默认访问不触 ro 拒写，与 New 同序）
        fs.ApplyOptions(options, existed ? TierFsVerbDummy.Open : TierFsVerbDummy.New);
        return fs;
    }

    /// <summary>New = 创建空镜像并打开（设计 §2.3：根不存在则建、已存在且非空抛 AlreadyExists、空根幂等成功）。</summary>
    public static DiskFileSystem New(string root, DiskFileSystemOptions? options = null, ILogger? logger = null)
    {
        options ??= new DiskFileSystemOptions();
        var fs = BindCore(root, options.MetadataMode, options.Preallocation, logger);
        fs.EnsureRoot();   // New+ro = 建完即封存——创建动作本身不算写（封络在建后生效）
        if (fs.EnumerateEntries("*").Any())
            throw new FileIOException(IOError.AlreadyExists,
                $"New 目标根空间非空：{fs._root}（已存在且非空即抛；打开既有请用 Open）。", fs._root, "disk-new");
        fs.ApplyOptions(options, TierFsVerbDummy.New);
        return fs;
    }

    /// <summary>Open = 打开既有（设计 §2.3：根不存在即抛 NotFound——本地文件系统不代建根）。</summary>
    public static DiskFileSystem Open(string root, DiskFileSystemOptions? options = null, ILogger? logger = null)
    {
        options ??= new DiskFileSystemOptions();
        var fs = BindCore(root, options.MetadataMode, options.Preallocation, logger);
        if (!Directory.Exists(fs._root))
            throw new FileIOException(IOError.NotFound,
                $"Open 目标根不存在：{fs._root}（创建请用 New）。", fs._root, "disk-open");
        fs.ApplyOptions(options);
        return fs;
    }

    /// <summary>构造后应用基类挂载属性（access/label/quota——Create 旧入口缺省值维持既有行为）。</summary>
    private void ApplyOptions(DiskFileSystemOptions options, TierFsVerbDummy verb = TierFsVerbDummy.Open)
    {
        _access = options.Access;
        _quotaBytes = options.QuotaBytes;
        if (options.Exclusive)
            _ = AcquireExclusive(MountExclusiveTimeout);   // G5：构造期获取（Dispose 违约释放路径既有兜底）
        if (options.Label is null) return;
        var marker = Path.Combine(_root, LabelMarkerName);
        if (verb == TierFsVerbDummy.New)
        {
            EnsureRoot();
            File.WriteAllText(marker, options.Label);   // G1：New = 写入空间根卷记录
        }
        else
        {
            var actual = File.Exists(marker) ? File.ReadAllText(marker) : null;
            if (actual != options.Label)
                throw new FileIOException(IOError.NotFound,
                    $"label 校验不符：期望 '{options.Label}'，根上实际 '{actual ?? "<无>"}'（spec label 在 Open = 断言）。",
                    _root, "open-label-check");
        }
    }

    /// <summary>挂载期排他获取超时（G5——fail-fast 语义：等 30s 拿不到即 SharingViolation）。</summary>
    private static readonly TimeSpan MountExclusiveTimeout = TimeSpan.FromSeconds(30);

    internal enum TierFsVerbDummy { New, Open }


    /// <summary>
    /// ExtendedAttr 模式通道探测（惰性：构造时根可能尚不存在——首用探测并缓存；
    /// 不可用抛 Unsupported——部署错误 fail-fast）。
    /// </summary>
    private void EnsureMetaChannelAvailable()
    {
        if (_metaMode != DiskMetadataMode.ExtendedAttr) return;
        var probed = Volatile.Read(ref _metaChannelProbe);
        if (probed == -1)
            throw new FileIOException(IOError.Unsupported,
                "MetadataMode=ExtendedAttr 但文件系统不支持 xattr/ADS（ProbeFileMetaSupport=Unsupported）——改用 Fallback/Sidecar 模式。",
                null, "metadata-channel");
        if (probed == 1) return;
        // 首用探测：根下临时探针文件（试写试读校验）
        EnsureRoot();
        var probePath = Path.Combine(_root, ".meta-channel-probe");
        try
        {
            File.WriteAllBytes(probePath, [1]);
            var support = FileNative.ProbeFileMetaSupport(probePath, logger: _logger);
            Volatile.Write(ref _metaChannelProbe, support == FileMetaSupport.Supported ? 1 : -1);
        }
        finally
        {
            try { File.Delete(probePath); } catch { /* 探针残留无害 */ }
        }
        if (Volatile.Read(ref _metaChannelProbe) == -1)
            throw new FileIOException(IOError.Unsupported,
                "MetadataMode=ExtendedAttr 但文件系统不支持 xattr/ADS——改用 Fallback/Sidecar 模式。", null, "metadata-channel");
    }

    /// <summary>层级路径的父目录存在性（缺失 → NotFound；单组件 = 根，恒过）。</summary>
    private static void EnsureParentExists(string full, string path, string op)
    {
        var parent = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(parent) || Directory.Exists(parent)) return;
        throw new FileIOException(IOError.NotFound, $"父目录不存在: {parent}", path, op);
    }

    /// <summary>根下相对路径 → 绝对路径（核内共用；层级命名空间校验 + 分隔符平台归一）。</summary>
    internal string GetFullPath(string path)
    {
        PathValidator.ValidateRelative(path, _root);
        return Path.Combine(_root,
            OperatingSystem.IsWindows() ? path.Replace('/', Path.DirectorySeparatorChar) : path);
    }

    /// <inheritdoc/>
    public FileSystemCapabilities Capabilities { get; }

    /// <inheritdoc/>
    /// <remarks>§5.4 完整自描述：几何自探测基值 + 挂载属性运行时可见；label 惰性读标记一次缓存；
    /// used = 配额激活后投影精确（未设 = -1 诚实）。</remarks>
    public VolumeInfo Volume => _baseVolume with
    {
        Label = ReadLabelMarkerCached(),
        Nature = StorageNature.Local,
        Access = _access,
        QuotaBytes = _quotaBytes,
        UsedBytes = _quotaBytes > 0 ? _quotaProjectedTotal : -1,
    };

    private readonly VolumeInfo _baseVolume;
    private string? _labelMarkerCache;

    private string? ReadLabelMarkerCached()
    {
        if (_labelMarkerCache is null)
        {
            var marker = Path.Combine(_root, LabelMarkerName);
            _labelMarkerCache = File.Exists(marker) ? "" + File.ReadAllText(marker) : "";
        }
        return _labelMarkerCache.Length == 0 ? null : _labelMarkerCache[1..];
    }

    /// <inheritdoc/>
    public IFileHandle Open(string path, FileOpenOptions options)
    {
        AccessGate.CheckHandleOpen(_access, options.Access, path);   // G2 包络：构造期 fail-fast
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        // 维护门闩：写意图打开按变异拒绝（All 档连读意图一并拒）——句柄打开本身是原子的，立即退出在途计数
        if (options.Access == AccessMode.Read)
            _maintenance.ThrowIfReadsRejected(nameof(Open), path);
        else
            using (_maintenance.BeginMutation(nameof(Open), path)) { }
        // 路径校验在 DiskFileHandle 构造内经 GetFullPath 完成；此处先验（快失败）+ 父目录存在性
        // （层级路径：目录缺失映射 NotFound——DirectoryNotFoundException 的 Wrap 否则归 Unknown）
        var full = GetFullPath(path);
        EnsureParentExists(full, path, nameof(Open));
        var handle = new DiskFileHandle(this, path, options, _logger);
        // 进程内共享登记（ctor 成功后注册——与任何已开句柄双向不兼容时抛 SharingViolation；
        // Windows 上 OS 原生 FileShare 为超集保护，本表两平台统一断言口径）
        handle.AttachSharing(_sharing, _sharing.Register(path, options.Access, options.Sharing));
        // 文件级追加预留：open 时解析盒引用（初值 = 打开时文件长度——预分配已在 ctor 内生效）
        handle.AttachAppendCursor(_appendCursors.GetOrAdd(path, _ => new AppendCursor { Value = handle.Length }));
        return handle;
    }

    /// <inheritdoc/>
    public void EnsureRoot()
    {
        AccessGate.RejectWrite(_access, nameof(EnsureRoot));
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var gate = _maintenance.BeginMutation(nameof(EnsureRoot), null);
        try { Directory.CreateDirectory(_root); }   // 幂等：已存在 no-op
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw ex.Wrap(nameof(EnsureRoot), _root); }
    }

    /// <inheritdoc/>
    public void FlushRoot()
    {
        AccessGate.RejectWrite(_access, nameof(FlushRoot));
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        try
        {
            // FlushParentDirectory 取参数的父目录 fsync——传 root 下的哨兵路径使父目录 = root（Windows 侧 no-op）
            FileNative.FlushParentDirectory(Path.Combine(_root, "."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw ex.Wrap(nameof(FlushRoot), _root); }
    }

    /// <inheritdoc/>
    public bool Exists(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _maintenance.ThrowIfReadsRejected(nameof(Exists), path);
        return File.Exists(GetFullPath(path));
    }

    /// <inheritdoc/>
    public void Delete(string path)
    {
        AccessGate.RejectWrite(_access, nameof(Delete));
        QuotaRelease(path);   // G3：删除回收投影
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var gate = _maintenance.BeginMutation(nameof(Delete), path);
        var full = GetFullPath(path);
        try
        {
            FileNative.DeleteFileDurably(full);
            // sidecar 伴生生命周期绑定（§3.6）——best-effort：主文件已删，残留伴生只占目录项
            var sidecarFull = GetFullPath(PathPattern.SidecarOf(path));
            if (File.Exists(sidecarFull))
                FileNative.DeleteFileDurably(sidecarFull);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw ex.Wrap(nameof(Delete), path); }
        finally { _appendCursors.TryRemove(path, out _); }   // 追加预留盒摘除（下次重建按新 Length）
    }

    /// <inheritdoc/>
    public void Move(string source, string dest, bool overwrite = false)
    {
        AccessGate.RejectWrite(_access, nameof(Move));
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var gate = _maintenance.BeginMutation(nameof(Move), source);
        var srcFull = GetFullPath(source);
        var dstFull = GetFullPath(dest);
        try
        {
            if (!overwrite && File.Exists(dstFull))
                throw new FileIOException(IOError.AlreadyExists,
                    $"Move target already exists: {dest} (overwrite=false).", dest, nameof(Move));
            FileNative.MoveFileDurably(srcFull, dstFull, overwrite);
            TryMoveSidecar(source, dest);   // sidecar 伴生随主文件迁移（§3.6）
        }
        catch (FileIOException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw ex.Wrap(nameof(Move), source); }
        finally { _appendCursors.TryRemove(source, out _); }   // 源名盒摘除（目标名按需重建——同数据同长度）
    }

    /// <summary>文件级追加预留的权威复位（SetLength 收缩/增长后，追加从新末端继续）。</summary>
    internal void OnFileLengthChanged(string path, long newLength)
    {
        if (_appendCursors.TryGetValue(path, out var cursor))
            Interlocked.Exchange(ref cursor.Value, newLength);
    }

    // ═══════════════════════════════════════════════════════════════
    //  目录族（filesystem-root-space-design §3）
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public void CreateDirectory(string path)
    {
        AccessGate.RejectWrite(_access, nameof(CreateDirectory));
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var gate = _maintenance.BeginMutation(nameof(CreateDirectory), path);
        var full = GetFullPath(path);
        try
        {
            var existed = Directory.Exists(full);
            Directory.CreateDirectory(full);   // mkdir -p（幂等）
            if (!existed)
            {
                // 耐久：新建目录自身 + 父目录的目录项 fsync（FlushRoot 同款哨兵技巧）
                FileNative.FlushParentDirectory(Path.Combine(full, "."));
                FileNative.FlushParentDirectory(full);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw ex.Wrap(nameof(CreateDirectory), path); }
    }

    /// <inheritdoc/>
    public void DeleteDirectory(string path)
    {
        AccessGate.RejectWrite(_access, nameof(DeleteDirectory));
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var gate = _maintenance.BeginMutation(nameof(DeleteDirectory), path);
        var full = GetFullPath(path);
        try
        {
            if (!Directory.Exists(full))
                throw new FileIOException(IOError.NotFound, $"目录不存在: {path}", path, nameof(DeleteDirectory));
            try { Directory.Delete(full, recursive: false); }   // POSIX rmdir——仅限空
            catch (IOException ex) when ((ex.HResult & 0xFFFF) is 145 or 39)   // ERROR_DIR_NOT_EMPTY / ENOTEMPTY
            {
                throw new FileIOException(IOError.DirectoryNotEmpty, $"目录非空: {path}",
                    path, nameof(DeleteDirectory), ex);
            }
            FileNative.FlushParentDirectory(full);
        }
        catch (FileIOException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw ex.Wrap(nameof(DeleteDirectory), path); }
    }

    /// <inheritdoc/>
    public bool DirectoryExists(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        return Directory.Exists(GetFullPath(path));
    }

    /// <inheritdoc/>
    /// <remarks>同根内必同卷 → <see cref="Directory.Move"/> 原子（能力位 AtomicDirectoryMove 置位）。</remarks>
    public void MoveDirectory(string source, string dest)
    {
        AccessGate.RejectWrite(_access, nameof(MoveDirectory));
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var gate = _maintenance.BeginMutation(nameof(MoveDirectory), source);
        var srcFull = GetFullPath(source);
        var dstFull = GetFullPath(dest);
        try
        {
            if (!Directory.Exists(srcFull))
                throw new FileIOException(IOError.NotFound, $"源目录不存在: {source}", source, nameof(MoveDirectory));
            if (Directory.Exists(dstFull) || File.Exists(dstFull))
                throw new FileIOException(IOError.AlreadyExists,
                    $"MoveDirectory 目标已存在: {dest}（不提供 overwrite——平台语义分歧，§3.4）。", dest, nameof(MoveDirectory));
            Directory.Move(srcFull, dstFull);
            // 两侧父目录的目录项 fsync（源移除 + 目标落盘）
            FileNative.FlushParentDirectory(dstFull);
            FileNative.FlushParentDirectory(srcFull);
        }
        catch (FileIOException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw ex.Wrap(nameof(MoveDirectory), source); }
    }

    // ═══════════════════════════════════════════════════════════════
    //  文件创建（与句柄解耦）+ 元数据通道（§3.6）
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public void CreateFile(string path, long preallocateSize = 0, ReadOnlyMemory<byte> extra = default)
    {
        AccessGate.RejectWrite(_access, nameof(CreateFile));
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var gate = _maintenance.BeginMutation(nameof(CreateFile), path);
        if (extra.Length > IFileSystem.MaxFileExtraBytes)
            throw new ArgumentException($"FileExtra 超限（{extra.Length} > {IFileSystem.MaxFileExtraBytes}）。", nameof(extra));
        var full = GetFullPath(path);
        EnsureParentExists(full, path, nameof(CreateFile));
        try
        {
            SafeFileHandle h;
            try
            {
                h = File.OpenHandle(full, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) is 80 or 183)   // ERROR_FILE_EXISTS / ERROR_ALREADY_EXISTS
            {
                throw new FileIOException(IOError.AlreadyExists, $"文件已存在: {path}", path, nameof(CreateFile), ex);
            }
            using (h)
            {
                if (preallocateSize > 0)
                    PreallocateHandle(h, preallocateSize, path);
            }
            if (!extra.IsEmpty)
                WriteFileExtraCore(full, path, extra.Span);   // 空 = 不写（新建文件无需清除）
            FileNative.FlushParentDirectory(full);   // 目录项 fsync（创建成本在此付清——运行时 Open 免；sidecar 换名的父 fsync 已由 MoveFileDurably 内建）
        }
        catch (FileIOException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw ex.Wrap(nameof(CreateFile), path); }
    }

    /// <summary>预分配方式轴（IS-04——DiskFileHandle.Preallocate 读取挂载档）。</summary>
    internal PreallocationMode PreallocationMode => _preallocation;

    /// <summary>按挂载档预分配：Full = 物理占位强制（失败显式报错——不允许静默降级为稀疏）；
    /// Metadata（缺省）= PreallocateFile 链（best-effort——IS-01 修复后降级 = 稀疏标记 + SetLength）。</summary>
    private void PreallocateHandle(SafeFileHandle h, long size, string path)
    {
        if (_preallocation == PreallocationMode.Full)
        {
            if (!FileNative.EnsurePhysicalAllocation(h, size, _logger))
                throw new FileIOException(IOError.IOFailure,
                    $"预分配失败（Preallocation=Full）：{path}——full 档不允许静默降级为稀疏",
                    path, nameof(CreateFile));
            return;
        }
        FileNative.PreallocateFile(h, size, _logger);   // 毫秒级真预留（失败降级稀疏——PreallocateFile 内建）
    }

    /// <summary>
    /// FileExtra 写入（模式路由 §3.6）：ExtendedAttr=仅 xattr（通道缺失抛 Unsupported）/
    /// Sidecar=仅伴生文件/Fallback=xattr 优先失败回退 sidecar。
    /// sidecar 强一致写：tmp 同目录 + WriteThrough + Flush(true) + MoveFileDurably 原子换名。
    /// ★ 空 = 清除语义（SetFileExtra(空)）：xattr 通道 best-effort 删键 + sidecar 伴生删除。
    /// </summary>
    private void WriteFileExtraCore(string full, string relPath, ReadOnlySpan<byte> metadata, bool consistent = true)
    {
        if (metadata.IsEmpty)
        {
            if (_metaMode != DiskMetadataMode.Sidecar)
                FileNative.DeleteFileMeta(full, logger: _logger);
            var sidecarFull = GetFullPath(PathPattern.SidecarOf(relPath));
            if (File.Exists(sidecarFull))
                TryDeleteNoThrow(sidecarFull);
            return;
        }
        if (_metaMode != DiskMetadataMode.Sidecar)
        {
            EnsureMetaChannelAvailable();
            if (FileNative.WriteFileMeta(full, metadata, logger: _logger))
                return;
            if (_metaMode == DiskMetadataMode.ExtendedAttr)
                throw new FileIOException(IOError.IOFailure, $"xattr/ADS FileExtra 写入失败: {relPath}", relPath, "metadata-write");
        }
        WriteSidecarMetadata(relPath, metadata, consistent);
    }

    /// <summary>句柄侧路由入口（FileExtra 平面——DiskFileHandle 四成员复用模式路由通道）。</summary>
    internal void WriteFileExtraRouted(string relPath, ReadOnlySpan<byte> extra)
        => WriteFileExtraCore(GetFullPath(relPath), relPath, extra);

    /// <summary>句柄侧路由读取（FileExtra——返回有效长度；0 = 无）。</summary>
    internal int ReadFileExtraRouted(string relPath, Span<byte> buffer)
        => ReadFileExtraCore(GetFullPath(relPath), relPath, buffer);

    /// <summary>sidecar 伴生写（consistent = tmp+WriteThrough+Flush(true)+MoveFileDurably 原子换名；否则直写页缓存）。</summary>
    private void WriteSidecarMetadata(string relPath, ReadOnlySpan<byte> metadata, bool consistent)
    {
        var sidecarFull = GetFullPath(PathPattern.SidecarOf(relPath));
        if (!consistent)
        {
            using var fs = new FileStream(sidecarFull, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 4096, FileOptions.None);
            fs.Write(metadata);
            return;
        }
        var tmpPath = sidecarFull + ".tmp";
        try
        {
            using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 4096, FileOptions.WriteThrough))
            {
                fs.Write(metadata);
                fs.Flush(flushToDisk: true);
            }
            FileNative.MoveFileDurably(tmpPath, sidecarFull, overwrite: true);   // 原子换名 + 父目录 fsync 内建
        }
        catch
        {
            TryDeleteNoThrow(tmpPath);
            throw;
        }
    }

    /// <summary>
    /// FileExtra 读取（span 形态——零分配：调用方 stackalloc 直配，1.5K 上限是钥匙）。
    /// 模式路由：ExtendedAttr=仅 xattr / Sidecar=仅伴生 / Fallback=双通道（xattr 先、sidecar 兜底）。
    /// </summary>
    /// <returns>有效长度；0 = 无。</returns>
    private int ReadFileExtraCore(string full, string relPath, Span<byte> buffer)
    {
        if (_metaMode != DiskMetadataMode.Sidecar)
        {
            var n = FileNative.ReadFileMeta(full, buffer, FileNative.XattrName, _logger);
            if (n > 0) return n;
        }
        var sidecarFull = GetFullPath(PathPattern.SidecarOf(relPath));
        if (!File.Exists(sidecarFull)) return 0;
        using var fs = new FileStream(sidecarFull, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096);
        if (fs.Length <= 0 || fs.Length > buffer.Length) return 0;
        var size = (int)fs.Length;
        return fs.Read(buffer[..size]) == size ? size : 0;
    }

    /// <summary>sidecar 伴生生命周期绑定（Delete/Move 同步处理——§3.6；无伴生 no-op）。</summary>
    private void TryMoveSidecar(string source, string dest)
    {
        var srcSidecar = GetFullPath(PathPattern.SidecarOf(source));
        if (!File.Exists(srcSidecar)) return;
        FileNative.MoveFileDurably(srcSidecar, GetFullPath(PathPattern.SidecarOf(dest)), overwrite: true);
    }

    /// <inheritdoc/>
    public FsEntryInfo Stat(string path)
    {
        AccessGate.RejectRead(_access, nameof(Stat));
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _maintenance.ThrowIfReadsRejected(nameof(Stat), path);
        var full = GetFullPath(path);
        try
        {
            if (File.Exists(full))
            {
                var fi = new FileInfo(full);
                // 内部零分配（stackalloc 1.5K）→ 公共边界精确尺寸拷贝（FsEntryInfo.FileExtra 消费者持有——契约固有成本）
                Span<byte> buf = stackalloc byte[IFileSystem.MaxFileExtraBytes];
                var extraLen = ReadFileExtraCore(full, path, buf);
                var extra = extraLen > 0 ? buf[..extraLen].ToArray() : Array.Empty<byte>();
                return new FsEntryInfo(FsEntryType.File, path, fi.Length,
                    new DateTimeOffset(fi.LastWriteTimeUtc), NullIfEpoch(fi.CreationTimeUtc), extra);
            }
            if (Directory.Exists(full))
            {
                var di = new DirectoryInfo(full);
                return new FsEntryInfo(FsEntryType.Directory, path, 0,
                    new DateTimeOffset(di.LastWriteTimeUtc), NullIfEpoch(di.CreationTimeUtc),
                    ReadOnlyMemory<byte>.Empty);
            }
            throw new FileIOException(IOError.NotFound, $"条目不存在: {path}", path, nameof(Stat));
        }
        catch (FileIOException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw ex.Wrap(nameof(Stat), path); }
    }

    /// <summary>epoch 起点时间（部分 FS 无创建时间语义）→ null（不可得诚实表达）。</summary>
    private static DateTimeOffset? NullIfEpoch(DateTime time)
        => time.Year <= 1601 ? null : new DateTimeOffset(time);

    // ═══════════════════════════════════════════════════════════════
    //  枚举族（§3.5：BCL EnumerationOptions.MatchType.Simple——与 Mem/Remote 客户端过滤同语义）
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public IEnumerable<FsEntry> EnumerateFiles(string pattern = "*", bool recursive = false)
    {
        AccessGate.RejectRead(_access, "Enumerate");
        ValidateEnumeration(null, pattern);
        return EnumerateCore(null, pattern, recursive, EntryFilter.Files);
    }

    /// <inheritdoc/>
    public IEnumerable<FsEntry> EnumerateFiles(string path, string pattern, bool recursive = false)
    {
        AccessGate.RejectRead(_access, "Enumerate");
        ValidateEnumeration(path, pattern);
        return EnumerateCore(path, pattern, recursive, EntryFilter.Files);
    }

    /// <inheritdoc/>
    public IEnumerable<FsEntry> EnumerateDirectories(string pattern = "*", bool recursive = false)
    {
        AccessGate.RejectRead(_access, "Enumerate");
        ValidateEnumeration(null, pattern);
        return EnumerateCore(null, pattern, recursive, EntryFilter.Directories);
    }

    /// <inheritdoc/>
    public IEnumerable<FsEntry> EnumerateDirectories(string path, string pattern, bool recursive = false)
    {
        AccessGate.RejectRead(_access, "Enumerate");
        ValidateEnumeration(path, pattern);
        return EnumerateCore(path, pattern, recursive, EntryFilter.Directories);
    }

    /// <inheritdoc/>
    public IEnumerable<FsEntry> EnumerateEntries(string pattern = "*", bool recursive = false)
    {
        AccessGate.RejectRead(_access, "Enumerate");
        ValidateEnumeration(null, pattern);
        return EnumerateCore(null, pattern, recursive, EntryFilter.Both);
    }

    /// <inheritdoc/>
    public IEnumerable<FsEntry> EnumerateEntries(string path, string pattern, bool recursive = false)
    {
        AccessGate.RejectRead(_access, "Enumerate");
        ValidateEnumeration(path, pattern);
        return EnumerateCore(path, pattern, recursive, EntryFilter.Both);
    }

    private enum EntryFilter { Files, Directories, Both }

    /// <summary>枚举入口校验（★ 调用即快失败——迭代器体内的校验会延迟到首次 MoveNext）。</summary>
    private void ValidateEnumeration(string? path, string pattern)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _maintenance.ThrowIfReadsRejected("Enumerate", path);
        PathPattern.Validate(pattern);
        if (path is null) return;
        var dirFull = GetFullPath(path);
        if (!Directory.Exists(dirFull))
            throw new FileIOException(IOError.NotFound, $"目录不存在: {path}", path, nameof(EnumerateFiles));
    }

    /// <summary>
    /// 枚举核（惰性迭代器）：BCL Simple 匹配 + 类型过滤 + sidecar 伴生隐藏 + 相对名归一（'/'）。
    /// <para>sidecar 隐藏规则（§3.6）：".X" 且同目录存在 "X" → 元数据伴生，不产出；无配对点文件保持可见。</para>
    /// </summary>
    private IEnumerable<FsEntry> EnumerateCore(string? path, string pattern, bool recursive, EntryFilter filter)
    {
        // 前置校验已在公共入口（ValidateEnumeration）完成——此处纯遍历
        var showHidden = PathPattern.HiddenExempt(pattern);   // §3.5 隐藏类豁免（A 方案：pattern 首字符 '.'）
        var dirFull = path is null ? _root : GetFullPath(path);
        var baseLen = dirFull.EndsWith(Path.DirectorySeparatorChar) ? dirFull.Length : dirFull.Length + 1;
        var options = new EnumerationOptions
        {
            MatchType = MatchType.Simple,          // 契约语义：与 Mem/Remote 客户端过滤同集
            MatchCasing = MatchCasing.CaseSensitive,   // Ordinal 契约（默认 PlatformDefault 在 NTFS 不敏感）
            RecurseSubdirectories = recursive,
            AttributesToSkip = 0,                  // 默认跳 Hidden/System——sidecar/锁文件必须可见（隐藏由配对规则处理）
        };
        IEnumerable<FileSystemInfo> source = path is null
            ? new DirectoryInfo(_root).EnumerateFileSystemInfos(pattern, options)
            : new DirectoryInfo(dirFull).EnumerateFileSystemInfos(pattern, options);
        foreach (var f in source)
        {
            var isDir = (f.Attributes & FileAttributes.Directory) != 0;
            if (filter == EntryFilter.Files && isDir) continue;
            if (filter == EntryFilter.Directories && !isDir) continue;
            var name = f.FullName[baseLen..].Replace(Path.DirectorySeparatorChar, '/');
            if (!showHidden && PathPattern.IsHiddenRelative(name))
                continue;   // 隐藏类（§3.5：点前缀组件，含隐藏子树）——sidecar 伴生特判已被吸收
            if (isDir)
            {
                var d = (DirectoryInfo)f;
                yield return new FsEntry(FsEntryType.Directory, name, 0,
                    new DateTimeOffset(d.LastWriteTimeUtc), NullIfEpoch(d.CreationTimeUtc));
            }
            else
            {
                var fi = (FileInfo)f;
                yield return new FsEntry(FsEntryType.File, name, fi.Length,
                    new DateTimeOffset(fi.LastWriteTimeUtc), NullIfEpoch(fi.CreationTimeUtc));
            }
        }
    }


    /// <inheritdoc/>
    public IDisposable EnterMaintenance(string reason, MaintenanceScope scope, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        return _maintenance.Enter(reason, scope, ct);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// ★ 非重入：持有期间的二次 Acquire（任何线程——重入与跨线程争用不可区分且重入语义易误用）
    ///   按争用处理——轮询至超时抛 <see cref="IOError.SharingViolation"/>。
    /// </remarks>
    public IDisposable AcquireExclusive(TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (!Capabilities.HasFlag(FileSystemCapabilities.ExclusiveLock))
            throw new FileIOException(IOError.Unsupported,
                $"{nameof(AcquireExclusive)} is not supported by this file system.", null, nameof(AcquireExclusive));

        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        lock (_lockGate)
        {
            // 本实例已持有（同实例二次 Acquire）：与跨线程争用同路——出锁轮询至超时
            // （线程 id 不能当持有者身份：池线程复用会误判跨线程获取为重入——已实测 flaky）

            var lockPath = Path.Combine(_root, LockFileName);
            Directory.CreateDirectory(_root);
            while (true)
            {
                SafeFileHandle? candidate = null;
                try
                {
                    if (OperatingSystem.IsWindows())
                    {
                        // FileShare.None 的打开是跨进程原子互斥；进程崩溃后句柄由 OS 关闭 → 后来者可重获（自愈）
                        candidate = File.OpenHandle(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                            FileShare.None);
                    }
                    else
                    {
                        // ★ 原生 open(2) 而非 File.OpenHandle：.NET 8 的 Unix 进程内共享表感知 flock——
                        //   已持锁实例的 fd 会让后来者的 OpenHandle 在到达我们的 flock 之前就被拒
                        //   （InUse/EAGAIN，FileShare 参数兼容也拦）——轮询逻辑失效。裸 fd + flock
                        //   令互斥语义 100% 由内核 flock 提供，不经 BCL 表。
                        var fd = LibC.Open(lockPath, NativeConstants.ORdwr | NativeConstants.OCreat, NativeConstants.FileMode0644);
                        candidate = fd < 0
                            ? throw new IOException($"open(lock file) failed, errno={Marshal.GetLastPInvokeError()}.")
                            : new SafeFileHandle(fd, ownsHandle: true);
                        var borrowed = false;
                        try
                        {
                            candidate.DangerousAddRef(ref borrowed);
                            var lockFd = candidate.DangerousGetHandle().ToInt32();
                            if (LibC.Flock(lockFd, LibC.LockEx | LibC.LockNb) != 0)
                            {
                                var errno = Marshal.GetLastPInvokeError();
                                if (errno != 11)   // EAGAIN=被占；其他错误真实失败
                                    throw new IOException($"flock failed, errno={errno}.");
                                candidate.Dispose();
                                candidate = null;
                            }
                        }
                        finally
                        {
                            if (borrowed) candidate?.DangerousRelease();
                        }
                    }

                    if (candidate is not null)
                    {
                        _lockHandle = candidate;
                        return new ExclusiveLease(this);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    candidate?.Dispose();
                    throw ex.Wrap(nameof(AcquireExclusive), _root);
                }

                if (Environment.TickCount64 >= deadline)
                    throw new FileIOException(IOError.SharingViolation,
                        $"AcquireExclusive timed out after {timeout.TotalMilliseconds:F0}ms (volume lock held by another holder).",
                        _root, nameof(AcquireExclusive));
                Thread.Sleep(15);
            }
        }
    }

    /// <inheritdoc/>
    /// <remarks>磁盘方向："离开目录"——卷锁违约释放（告警）+ IO 缓冲池释放；已开句柄不受影响（OS 句柄归消费者）。</remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_lockGate)
        {
            if (_lockHandle is not null)
            {
                // 违约释放：lease 未 Dispose 而 fs 先 Dispose——告警但强制释放（防死锁残留）
                _logger?.LogWarning("fs Dispose 时卷锁仍被持有（lease 未释放——违约，已强制释放）");
                ReleaseVolumeLockNoLock();
            }
        }
        _ioBufferPool.Dispose();
    }

    private void ReleaseVolumeLockNoLock()
    {
        var handle = _lockHandle;
        _lockHandle = null;
        if (handle is null) return;
        try
        {
            handle.Dispose();
            TryDeleteNoThrow(Path.Combine(_root, LockFileName));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "释放卷锁失败 root={Root}", _root);
        }
    }

    private void TryDeleteNoThrow(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort：锁文件残留不影响互斥语义（下次仍可获取） */ }
    }

    /// <summary>卷锁 lease——RAII（Dispose 即释放）。</summary>
    private sealed class ExclusiveLease(DiskFileSystem owner) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            lock (owner._lockGate)
            {
                owner.ReleaseVolumeLockNoLock();
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  IO 缓冲租借（CopyRange 回退路径用——DIO 对齐缓冲）
    // ═══════════════════════════════════════════════════════════════

    internal Memory<byte> RentIoBuffer(nuint size, nuint alignment)
        => _ioBufferPool.RentAligned((int)size, (int)alignment).Memory;

    internal void ReturnIoBuffer(Memory<byte> buffer)
    {
        if (MemoryMarshal.TryGetMemoryManager<byte, AlignedMemoryManager>(buffer, out var mgr))
            _ioBufferPool.ReturnAligned(mgr);
    }

    // ═══════════════════════════════════════════════════════════════
    //  构造期探测（Volume / Capabilities 一次完成，句柄生命周期内不变）
    // ═══════════════════════════════════════════════════════════════

    private static VolumeInfo ProbeVolume(string root)
    {
        try
        {
            if (OperatingSystem.IsWindows()
                && Kernel32.GetDiskFreeSpace(root, out var spc, out var bps, out var freeClusters, out var totalClusters))
            {
                var allocationUnit = (long)spc * bps;
                return new VolumeInfo
                {
                    SectorSize = (int)bps,
                    AllocationUnit = Math.Max(1, allocationUnit),
                    FreeSpace = freeClusters * allocationUnit,
                    TotalSpace = totalClusters * allocationUnit,
                };
            }

            if ((!OperatingSystem.IsWindows()) && LibC.Statvfs(root, out var sv) == 0)
            {
                return new VolumeInfo
                {
                    SectorSize = (int)Math.Max(1, (long)sv.FrSize),
                    AllocationUnit = Math.Max(1, (long)sv.FBsize),
                    FreeSpace = (long)sv.FBavail * (long)sv.FrSize,
                    TotalSpace = (long)sv.FBlocks * (long)sv.FrSize,
                };
            }
        }
        catch
        {
            // 探测失败退保守默认（根不存在等构造期场景）
        }
        return new VolumeInfo { SectorSize = 512, AllocationUnit = 4096, FreeSpace = -1, TotalSpace = -1 };
    }

    private static FileSystemCapabilities ProbeCapabilities(string root)
    {
        // 共通位：磁盘介质均有
        var caps = FileSystemCapabilities.Sparse
                   | FileSystemCapabilities.DurableRename
                   | FileSystemCapabilities.WriteThrough
                   | FileSystemCapabilities.ExclusiveLock
                   | FileSystemCapabilities.RangeLock
                   | FileSystemCapabilities.Mmap
                   | FileSystemCapabilities.RandomWrite
                   | FileSystemCapabilities.EmptyDirectories        // 真目录（根空间层级）
                   | FileSystemCapabilities.AtomicDirectoryMove    // 同根内必同卷 rename 原子
                   | FileSystemCapabilities.MaintenanceGate;       // 维护门闩（设计 §8——三介质统一）

        if (OperatingSystem.IsLinux())
        {
            caps |= FileSystemCapabilities.DirectIO
                    | FileSystemCapabilities.FlushDataOnly
                    | FileSystemCapabilities.CopyRange
                    | FileSystemCapabilities.VectorIO
                    | FileSystemCapabilities.RangeShift
                    | FileSystemCapabilities.Advise;
            return caps;
        }

        // Windows：DirectIO 为卷级静态能力（NTFS/ReFS 非压缩卷；网络/压缩卷不置——句柄级另有逐个探测）
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var volumeRoot = Path.GetPathRoot(root) ?? root;
                if (!volumeRoot.EndsWith(Path.DirectorySeparatorChar) && volumeRoot.Length >= 2 && volumeRoot[1] == ':')
                    volumeRoot += Path.DirectorySeparatorChar;
                const int fsNameBufSize = 256;
                var fsNameBuf = Marshal.AllocHGlobal(fsNameBufSize * 2);
                try
                {
                    if (Kernel32.GetVolumeInformation(volumeRoot,
                            IntPtr.Zero, 0, out _, out _, out var fsFlags, fsNameBuf, fsNameBufSize))
                    {
                        const uint volumeIsCompressed = 0x00008000; // FILE_VOLUME_IS_COMPRESSED
                        var compressed = (fsFlags & volumeIsCompressed) != 0;
                        var fsName = Marshal.PtrToStringUni(fsNameBuf) ?? string.Empty;
                        if (!compressed && (fsName.Equals("NTFS", StringComparison.OrdinalIgnoreCase)
                                            || fsName.Equals("ReFS", StringComparison.OrdinalIgnoreCase)))
                            caps |= FileSystemCapabilities.DirectIO;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(fsNameBuf);
                }
            }
            catch
            {
                // 探测失败不置位（句柄级探测仍会真实报告）
            }
        }
        return caps;
    }
}
