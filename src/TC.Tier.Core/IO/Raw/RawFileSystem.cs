using System.Collections.Concurrent;
using System.IO.MemoryMappedFiles;
using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using TC.Tier.Core.IO.Image;
using TC.Tier.Core.IO.Shared;

namespace TC.Tier.Core.IO.Raw;

/// <summary>
/// Raw 根空间——第四介质（raw-medium-and-conversion-design 全篇）。
/// <para>★ 自维护布局的连续后端根空间：既是活卷又是存档；本地持久化推荐位（§1.4）。</para>
/// <para>★ 统一块空间（§3.1）：无元数据台阶——条目/区间/FileExtra 唯一上限 = 块数。</para>
/// <para>★ 区间三态（§3.2）：洞读零 / unwritten 预分配 / written——逻辑物理分离。</para>
/// <para>★ 一卷一实例（§2.4）：进程内登记（载体身份 + 卷 UUID 双键）+ 跨进程锁（文件载体伴生锁文件 / 设备 flock）。</para>
/// <para>★ 断电恢复底线（§4.1）：全量镜像检查点 + superblock 原子翻转 + 可达性对账——完整读写。</para>
/// <para>★ v1 实现注记：元数据/数据面共用一把元数据锁（正确性优先——读路径锁外快照为演进项）；
///   载体走缓冲 IO + 自管读缓存 + 数据写直通（DIO 纪律为后续优化，性能契约经 §12.4 探针验证可达）。</para>
/// </summary>
public sealed partial class RawFileSystem : IFileSystem, IContiguousVolume
{
    /// <summary>进程内实例登记表（一卷一实例——载体身份键 + UUID 双查）。</summary>
    private static readonly ConcurrentDictionary<string, RawFileSystem> SInstances = new();

    private readonly RawCarrier _carrier;                   // 主载体（成员 0）
    private readonly ILogger? _logger;
    private readonly bool _readOnly;
    private readonly AccessMode _mountAccess;                  // G2：挂载访问（Write 在入口即拒——虚拟卷无只写）
    private ulong? _quotaCapBlocks;                            // Open 收紧：min(quota, 供给) 折块（null = 不收紧）
    /// <summary>
    /// 元数据 + 数据面 v1 全局锁（RawFileHandle 经此串行）
    /// </summary>
    internal readonly object MetadataLock = new();       // 元数据 + 数据面 v1 全局锁（RawFileHandle 经此串行）
    private readonly LightEpoch _readEpoch = new();       // D1b：锁外快照读者保护域——块回收延迟至全部在途读者退出
    private readonly ConcurrentDictionary<string, AppendCursor> _appendCursors = new();
    private readonly SharingRegistry _sharing = new();
    private readonly MaintenanceGate _maintenance = new();
    private readonly List<ulong> _journalReserveBlocks = [];   // 日志物理保留（§3.9——格式化标记占用）

    /// <summary>成员载体运行态（RM-04 §3.8——线性拼接；成员 0 = 主载体）。</summary>
    internal sealed class CarrierMember
    {
        public required RawCarrier Carrier;
        public required MemberEntry Info;
        public SafeFileHandle Handle = null!;
        public FileStream? CrossProcLock;
        public ulong BaseBlock;             // 全局基块（成员表序推导）
        public bool Direct;                 // O_DIRECT 生效（本成员）
        public int IoAlign = 512;           // 载体 IO 对齐基（设备 = 扇区 512；文件 O_DIRECT = 内部块 4096）
        public bool IsMissing;              // 降级运行（v2b）：成员缺失——数据面路由拒读（诚实）
        public SafeFileHandle? DioReadHandle;   // RM-28：文件载体直达读专用 O_DIRECT 句柄（懒开——见 GetDioReadHandle）
        public int DioReadState;                // 0 未试 / 1 可用 / 2 失败记忆（回退缓冲读 + DONTNEED 纪律）
    }

    private CarrierMember[] _members = [];
    private bool _carrierDio;   // 全成员 O_DIRECT（写绕条件化判据——O_DIRECT 载体小写 = 设备 flush/次，须写回吸收）
    private bool _degraded;   // 降级运行（v2b）：成员缺失——只读 + 缺失数据拒读
    private bool _autoExpand;   // 自动扩容卷（medium-protocol §5.3：quota=-1 New 的文件载体——按需增长到磁盘物理满）
    private bool _expanding;   // 扩容进行中（重入护栏——触发点在 AllocateBlocks 自身）
    private SuperblockData _sb = null!;
    private int _pageSize;                               // 块大小（Open 后自 superblock 探知——构造期未知）
    private bool _timestampsDirty;                       // lazytime（RM-17）：mtime 待持久化——随结构提交/clean 关闭顺带，不单独付检查点
    private int _disposed;

    /// <summary>
    /// 元数据脏标记（命名空间/区间/长度/Extra——恢复可见性判据）——随结构提交/clean 关闭顺带，不单独付检查点。
    /// </summary>
    internal bool MetadataDirty { get; set; }

    /// <summary>
    /// 块大小（Open 后自 superblock 探知——构造期未知）。
    /// </summary>
    internal int PageSize => _pageSize;
    private RawFileSystem(RawCarrier carrier, RawOpenOptions options, ILogger? logger)
    {
        _carrier = carrier;
        _logger = logger;
        _readOnly = options.Access == AccessMode.Read;
        _mountAccess = options.Access;
        _pageBudget = options.PageCacheBytes;
        _backgroundDirtyThreshold = Math.Max(1L << 20, _pageBudget / 8);   // flusher 唤醒阈值（RM-02）
    }

    // ═══════════════ 施工入口（§3.6）═══════════════

    /// <summary>New（原 Format 终态改名）——在载体上创建空虚拟卷根空间（显式语义：已格式化载体抛 AlreadyExists）。</summary>
    public static RawFileSystem New(RawCarrier carrier, RawFormatOptions? options = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(carrier);
        options ??= new RawFormatOptions();
        options.Validate();

        ClaimInstance(carrier, null);
        var fs = new RawFileSystem(carrier, new RawOpenOptions(), logger)
        {
            _pageSize = options.BlockSize,
        };
        try
        {
            fs.OpenCarrierHandle(writable: true, createIfMissing: !carrier.IsDevice);
            fs.ThrowIfAlreadyFormatted();   // 显式语义：已格式化载体拒（幂等由调用方组合）
            fs.FormatCore(options);
            SInstances[carrier.IdentityKey] = fs;
            SInstances[$"uuid:{fs._sb.Uuid}"] = fs;
            return fs;
        }
        catch
        {
            fs.ReleaseResources();
            throw;
        }
    }

    /// <summary>打开已格式化载体为根空间（唯一性检查在此——§2.4）。</summary>
    public static RawFileSystem Open(RawCarrier carrier, RawOpenOptions? options = null, ILogger? logger = null)
        => Open([carrier], options, logger);

    /// <summary>多载体卷打开（RM-04 §3.8）：全量成员清单（成员 0 = 主载体），UUID/索引装配匹配。
    /// 降级打开（v2b）：options.AllowDegraded 时缺失成员以 null 占位（只读形态）。</summary>
    public static RawFileSystem Open(RawCarrier?[] carriers, RawOpenOptions? options = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(carriers);
        if (carriers.Length == 0) throw new ArgumentException("成员清单为空。", nameof(carriers));
        options ??= new RawOpenOptions();

        if (options.Access == AccessMode.Write)
            throw new ArgumentException("虚拟卷无只写形态（AccessMode.Write）——G2：映射/虚拟介质值域受限（ro|rw）");
        var carrier = carriers[0] ?? throw new ArgumentException("主载体（成员 0）不可缺失。", nameof(carriers));
        ClaimInstance(carrier, null);
        var fs = new RawFileSystem(carrier, options, logger);
        try
        {
            fs.OpenCarrierHandle(writable: options.Access != AccessMode.Read, createIfMissing: false);
            fs._pageSize = 0;   // DecodeWinner 自 superblock 探知
            var (winner, side) = fs.DecodeWinner();
            fs.AdoptWinner(winner);
            if (winner.Members.Count > 1 || carriers.Length > 1)
                fs.AssembleMembers(carriers, writable: options.Access != AccessMode.Read && !options.AllowDegraded,
                    allowDegraded: options.AllowDegraded);   // 多载体装配（身份校验 + 基块；降级 = 只读）
            fs.ContinueLoad(winner, side);

            // G1：Open label 校验（不符即抛 fail-fast——挂错卷的配置错误）
            if (options.Label is not null && options.Label != fs._sb.Label)
                throw new FileIOException(IOError.NotFound,
                    $"label 校验不符：期望 '{options.Label}'，卷上实际 '{fs._sb.Label}'（spec label 在 Open = 断言）。",
                    carrier.Path, "open-label-check");
            // G3：Open 收紧——有效上限 = min(quota, 供给)（§5.3；分配咽喉 AllocateBlocks 执法）。
            // 自动扩容卷例外：供给动态增长，quota 即界（min 规则随增长自然成立——quota ≤ 供给恒真）
            if (options.QuotaBytes > 0)
                fs._quotaCapBlocks = fs._autoExpand
                    ? (ulong)options.QuotaBytes / (ulong)fs._pageSize
                    : Math.Min((ulong)options.QuotaBytes / (ulong)fs._pageSize, fs._sb.CapacityBlocks);

            // UUID 双查（同 UUID 异载体 = 复制卷——一卷一实例同样拒绝）+ 正式登记
            var uuidKey = $"uuid:{fs._sb.Uuid}";
            if (SInstances.TryGetValue(uuidKey, out var existing) && !ReferenceEquals(existing, fs))
                throw new FileIOException(IOError.SharingViolation,
                    $"卷 UUID {fs._sb.Uuid} 已有活跃实例（复制卷？）——一卷一实例（§2.4）", null, "Open");
            foreach (var c in carriers)
                if (c is not null) SInstances[c.IdentityKey] = fs;
            SInstances[uuidKey] = fs;

            // 写意图打开 clean 卷：置 dirty（此后崩溃 → 恢复路径，§4.1；降级形态零写）
            if (options.Access != AccessMode.Read && !options.AllowDegraded && (fs._sb.Flags & FlagClean) != 0)
            {
                lock (fs.MetadataLock)
                {
                    fs._sb.Flags = (ushort)(fs._sb.Flags & ~FlagClean);
                    fs.RotateSuperblocks();
                }
            }
            return fs;
        }
        catch
        {
            fs.ReleaseResources();
            throw;
        }
    }

    private static void ClaimInstance(RawCarrier carrier, Guid? uuid)
    {
        if (SInstances.ContainsKey(carrier.IdentityKey))
            throw new FileIOException(IOError.SharingViolation,
                $"载体已有活跃实例：{carrier.Path}——一卷一实例（§2.4）", null, "Open");
        if (uuid is { } u && SInstances.ContainsKey($"uuid:{u}"))
            throw new FileIOException(IOError.SharingViolation,
                $"卷 UUID {u} 已有活跃实例——一卷一实例（§2.4）", null, "Open");
    }

    private void FormatCore(RawFormatOptions options)
    {
        long capacity;
        if (_carrier.IsDevice)
        {
            // 设备容量：BLKGETSIZE64 ioctl（块设备 fstat.st_size 恒 0——RM-05 loop 实测抓到的坑）
            capacity = QueryDeviceCapacityBytes(_members[0].Handle);
            if (capacity <= 0)
                throw new FileIOException(IOError.IOFailure, $"设备容量非法：{capacity}", _carrier.Path, "Format");
            var sector = QueryDeviceSectorSize(_members[0].Handle);
            if (sector > options.BlockSize)
                throw new ArgumentException(
                    $"块大小 {options.BlockSize} 小于设备逻辑扇区 {sector}（4Kn 设备须 ≥ 扇区——DIO 对齐基准）。");
            if (options.QuotaBytes > 0 && capacity > options.QuotaBytes)
                capacity = options.QuotaBytes;   // 供给 = min(设备, quota)（New = 供给时刻——物化进卷记录）
            capacity -= capacity % (options.BlockSize * BitmapAlignBlocks);   // 64 块对齐（位字不跨成员——RM-04）
        }
        else
        {
            if (options.QuotaBytes == -1)
            {
                // 自动扩容卷（medium-protocol §5.3）：初始小界 + 按需倍增——直到磁盘物理满（与 disk 的 -1 同形）
                capacity = AutoExpandInitialBytes;
                _autoExpand = true;
            }
            else
            {
                capacity = options.QuotaBytes;
                if (capacity <= 0)
                    throw new ArgumentException(
                        "QuotaBytes 非法：正数 = 供给；-1 = 自动扩容（文件载体——按需增长；设备载体 = 设备大小）。");
            }
            capacity -= capacity % (options.BlockSize * BitmapAlignBlocks);   // 64 块对齐（位字不跨成员——RM-04 §3.8）
            RandomAccess.SetLength(_members[0].Handle, capacity);    // 声明上限一次成形（NTFS 稀疏）
        }

        var bs = (long)options.BlockSize;
        var capacityBlocks = (ulong)(capacity / bs);
        var bitmapBytes = (capacityBlocks + 7) / 8;
        var bitmapBlocks = (bitmapBytes + (ulong)bs - 1) / (ulong)bs;
        var bitmapStart = (ulong)((HeaderBytes + bs - 1) / bs);

        _sb = new SuperblockData
        {
            Flags = (ushort)(_autoExpand ? FlagAutoExpand : 0),
            BlockSize = (uint)options.BlockSize,
            CapacityBlocks = capacityBlocks,
            BitmapStart = bitmapStart,
            BitmapBlocks = bitmapBlocks,
            Generation = 1,
            Uuid = Guid.NewGuid(),
            Label = options.Label ?? "",   // 基类 Label 缺省 null——superblock 空串即无标签
        };
        _sb.Members = [new MemberEntry(_sb.Uuid, capacityBlocks, bitmapStart, bitmapBlocks)];   // RM-04：成员 0 自登记
        _members[0].Info = _sb.Members[0];   // 路由就位（格式化路径无 AdoptWinner）
        _members[0].BaseBlock = 0;
        _bitmapWords = new ulong[(bitmapBytes + 7) / 8];
        _freeBlocks = capacityBlocks;
        for (var w = 0UL; w < (ulong)_bitmapWords.LongLength; w++) _dirtyBitmapWords.Add(w);   // 首次提交全量写（增量基线：设备可能有残留字节）

        // 保留区：头部 + 位图 + 日志物理保留（§3.9——对数据不可见）
        for (var b = 0UL; b < bitmapStart; b++) MarkBlocks(b, 1, true);
        MarkBlocks(bitmapStart, (uint)bitmapBlocks, true);
        var journalBytes = Math.Min(options.JournalReserveBytes, capacity / 8);   // 保留封顶容量 1/8（小卷自适）
        if (journalBytes > 0)
        {
            var jb = (uint)(journalBytes / bs);
            var jstart = AllocateBlocks(jb, "JournalReserve");
            for (var i = 0UL; i < jb; i++) _journalReserveBlocks.Add(jstart + i);
            // 日志启用（raw-journal §3.1）：字段随首次 superblock 轮写持久
            _sb.Flags |= FlagJournaled;
            _sb.JournalStart = jstart;
            _sb.JournalBlocks = jb;
            _sb.JournalGeneration = 1;
            _sb.JournalState = 1;
        }

        MetadataDirty = true;
        lock (MetadataLock)
        {
            CommitMetadata();       // 初始空镜像（代数 1）
            _sb.Flags |= FlagClean; // 格式化完成即 clean
            RotateSuperblocks();
        }
        MetadataDirty = false;
        JournalInitFromSuperblock();   // 日志运行态就位（格式化即 Journaled——默认启用）
    }

    // ═══════════════ 载体访问（实例内唯一通道——§2.4 无侧门）═══════════════

    /// <summary>打开单个成员载体（锁 + 句柄 + DIO + 身份）——Format/Open/AddCarrier 共用。
    /// 文件载体：缓冲主句柄（内核 writeback 吸收 + 电梯调度——实测本机 O_DIRECT 同步写地板仅 ~500MB/s、
    /// 缓冲档 900MB/s+，硬切 O_DIRECT 缓冲档跌至 80MB/s；OS 缓存驻留由 DONTNEED 流式纪律控制，
    /// 见 <see cref="DropCarrierCache"/>）。设备载体：O_DIRECT 强制（外部写者一致性——RM-05）。</summary>
    private CarrierMember OpenMemberCarrier(RawCarrier carrier, MemberEntry info, bool writable, bool createIfMissing)
    {
        FileStream? crossProcLock = null;
        SafeFileHandle handle;
        var direct = false;
        var ioAlign = 512;
        if (!carrier.IsDevice)
        {
            // 跨进程锁：伴生锁文件 FileShare.None（进程崩溃 OS 关闭自愈——与 DiskFileSystem 同机制）
            var lockPath = carrier.Path + ".lock";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(carrier.Path)!);
                crossProcLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex)
            {
                throw new FileIOException(IOError.SharingViolation,
                    $"跨进程卷锁获取失败（另一实例持有？）：{lockPath}——{ex.Message}", null, "Open");
            }
            // FileShare.ReadWrite：MMF 直映射需第二次写打开——跨进程互斥由 .lock 锁文件承担（§2.4）
            // 锁文件已持有——主句柄失败必须释放锁（局部变量无人接手 = 泄漏到 GC；OpenOrCreate 的
            // Open→New 回退路径首次踩中：New 再取锁 FileShare.None 冲突）
            try
            {
                handle = File.OpenHandle(carrier.Path,
                    createIfMissing ? FileMode.OpenOrCreate : FileMode.Open,
                    writable ? FileAccess.ReadWrite : FileAccess.Read,
                    FileShare.ReadWrite, FileOptions.Asynchronous);
            }
            catch
            {
                crossProcLock.Dispose();
                throw;
            }
        }
        else
        {
            // 设备载体：原生 open(2)（绕过 .NET 8 Unix 共享层的 flock 感知）。排他判定归 flock(LOCK_EX|LOCK_NB)。
            // O_DIRECT（RM-05 DIO 纪律）：设备强制——内核缓存外部写者一致性；未对齐访问经对齐窗口弹跳。
            var flags = (writable ? NativeConstants.ORdwr : 0)
                       | (OperatingSystem.IsLinux() ? NativeConstants.ODirect : 0);
            var fd = LibC.Open(carrier.Path, flags, 0);
            if (fd < 0)
                throw new FileIOException(IOError.NotFound,
                    $"设备打开失败：{carrier.Path}（errno={Marshal.GetLastPInvokeError()}）",
                    carrier.Path, "Open");
            handle = LibC.WrapFileDescriptor(fd);
            if (OperatingSystem.IsLinux())
            {
                var borrowed = false;
                try
                {
                    handle.DangerousAddRef(ref borrowed);
                    // flock 排他（★返回值必须检查——RM-05 实测：丢弃 = 跨进程第二实例静默通过）
                    if (LibC.Flock(handle.DangerousGetHandle().ToInt32(), LibC.LockEx | LibC.LockNb) != 0)
                        throw new FileIOException(IOError.SharingViolation,
                            $"设备已被另一实例持有（flock）：{carrier.Path}——一卷一实例（§2.4）",
                            carrier.Path, "Open");
                    direct = true;
                }
                finally
                {
                    if (borrowed) handle.DangerousRelease();
                }
            }
        }
        return new CarrierMember { Carrier = carrier, Info = info, Handle = handle, CrossProcLock = crossProcLock, Direct = direct, IoAlign = ioAlign };
    }

    private void OpenCarrierHandle(bool writable, bool createIfMissing)
    {
        // 单载体主成员（多载体成员经 Open(carriers[]) 装配路径补开）
        var placeholder = new MemberEntry(Guid.Empty, 0, 0, 0);   // 占位（sb 未立——AdoptWinner/FormatCore 即补全；cc9bb2e0 曾误引 _sb.Uuid 使 New/Open 全 NRE）
        var m = OpenMemberCarrier(_carrier, placeholder, writable, createIfMissing);
        _members = [m];
        RefreshCarrierDio();
    }

    /// <summary>全成员 O_DIRECT 判据刷新（写绕条件化——成员装配/加卸载后调用）。</summary>
    private void RefreshCarrierDio()
        => _carrierDio = _members.Length > 0 && _members.All(m => m.Direct || m.IsMissing);

    /// <summary>多载体装配（RM-04 §3.8）：按成员表顺序补开其余载体 + RAWC 身份校验 + 基块推导。
    /// 载体清单由调用方供给（LVM 同构——路径不入盘上格式）；身份以 UUID/索引匹配，不匹配拒开。
    /// 降级运行（v2b）：AllowDegraded 且清单含 null 占位 → 幽灵成员（只读 + 数据面拒读）。</summary>
    private void AssembleMembers(RawCarrier?[] carriers, bool writable, bool allowDegraded)
    {
        if (_sb.Members.Count != carriers.Length)
            throw new FileIOException(IOError.NotFound,
                $"成员载体数不符：卷声明 {_sb.Members.Count}，供给 {carriers.Length}（含主载体须全量提交；降级打开用 null 占缺失成员）",
                _carrier.Path, "Open");
        var list = new List<CarrierMember>(_sb.Members.Count) { _members[0] };
        list[0].Info = _sb.Members[0];
        ulong total = 0;
        var missing = 0;
        for (var i = 0; i < _sb.Members.Count; i++)
        {
            var info = _sb.Members[i];
            if (i > 0)
            {
                if (carriers[i] is null)
                {
                    if (!allowDegraded)
                        throw new FileIOException(IOError.NotFound,
                            $"成员 {i} 缺失（null 占位须 AllowDegraded）——§3.8 成员缺失即拒开", _carrier.Path, "Open");
                    missing++;
                    list.Add(new CarrierMember
                    {
                        Carrier = $"<missing:{i}>",
                        Info = info,
                        BaseBlock = 0,
                        IsMissing = true,
                    });
                }
                else
                {
                    ClaimInstance(carriers[i]!, null);
                    var m = OpenMemberCarrier(carriers[i]!, info, writable, createIfMissing: false);
                    VerifyMemberHeader(m, i);
                    list.Add(m);
                }
            }
            list[i].BaseBlock = total;
            total += info.CapacityBlocks;
        }
        _members = list.ToArray();
        RefreshCarrierDio();
        if (missing <= 0) return;
        _degraded = true;   // 数据面路由拒读缺失成员 + 全部变异拒绝（v2b 降级形态）
        _logger?.LogWarning("降级打开：{Count} 个成员缺失（只读形态——缺失成员数据读将失败）", missing);
    }

    /// <summary>成员载体级身份头（"RAWC" 512B：magic|ver|卷UUID|成员索引|bitmapStart|bitmapBlocks|capacity|CRC）。
    /// 写于 AddCarrier（不可变）；Open 装配时校验——UUID/索引不匹配拒开（§3.8）。</summary>
    private static void EncodeMemberHeader(Span<byte> buffer, MemberEntry info, Guid volumeUuid, int carrierIndex, int pageSize)
    {
        buffer.Clear();
        "RAWC"u8.CopyTo(buffer);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(4), RawLayoutVersion);
        volumeUuid.TryWriteBytes(buffer.Slice(8));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(24), (uint)carrierIndex);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(28), info.BitmapStartLocal);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(36), info.BitmapBlocksLocal);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(44), info.CapacityBlocks);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(508),
            System.IO.Hashing.Crc32.HashToUInt32(buffer.Slice(0, 508)));
    }

    private void VerifyMemberHeader(CarrierMember m, int expectedIndex)
    {
        var header = new byte[512];
        ReadMemberLocal(m, 0, header);
        if (!header.AsSpan(0, 4).SequenceEqual("RAWC"u8)
            || System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24)) != (uint)expectedIndex
            || new Guid(header.AsSpan(8, 16).ToArray()) != _sb.Uuid
            || System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(508))
               != System.IO.Hashing.Crc32.HashToUInt32(header.AsSpan(0, 508)))
            throw new FileIOException(IOError.IOFailure,
                $"成员载体身份不符（UUID/索引/CRC）：{m.Carrier.Path}——期望成员 {expectedIndex}", m.Carrier.Path, "Open");
    }

    /// <summary>成员本地读（绕过全局路由——成员装配/头写入用；512 对齐经成员自己的 DIO 纪律）。</summary>
    private unsafe void ReadMemberLocal(CarrierMember m, long localOffset, Span<byte> dest)
    {
        if (!m.Direct)
        {
            var got = RandomAccess.Read(m.Handle, dest, localOffset);
            if (got != dest.Length)
                throw new FileIOException(IOError.IOFailure, $"成员短读：{m.Carrier.Path} @{localOffset}", m.Carrier.Path, "Open");
            return;
        }
        var align = m.IoAlign;
        var buf = (byte*)NativeMemory.AlignedAlloc(4096, 4096);
        try
        {
            var done = 0;
            while (done < dest.Length)
            {
                var take = Math.Min(align, dest.Length - done);
                // 读满对齐窗（4Kn 逻辑块要求整窗长度——512B 头读也按整窗取）
                var got = RandomAccess.Read(m.Handle, new Span<byte>(buf, align), localOffset + done);
                if (got < take)
                    throw new FileIOException(IOError.IOFailure, $"成员短读：{m.Carrier.Path} @{localOffset + done}", m.Carrier.Path, "Open");
                new ReadOnlySpan<byte>(buf, take).CopyTo(dest.Slice(done, take));
                done += take;
            }
        }
        finally
        {
            NativeMemory.AlignedFree(buf);
        }
    }

    /// <summary>成员本地写（512 对齐窗口——AddCarrier 头/位图初始化用）。</summary>
    private unsafe void WriteMemberLocal(CarrierMember m, long localOffset, ReadOnlySpan<byte> src)
    {
        var align = m.IoAlign;
        var buf = (byte*)NativeMemory.AlignedAlloc(4096, 4096);
        try
        {
            var done = 0;
            while (done < src.Length)
            {
                var take = Math.Min(align, src.Length - done);
                new Span<byte>(buf, align).Clear();
                src.Slice(done, take).CopyTo(new Span<byte>(buf, take));
                RandomAccess.Write(m.Handle, new Span<byte>(buf, align), localOffset + done);
                done += take;
            }
        }
        finally
        {
            NativeMemory.AlignedFree(buf);
        }
    }

    private unsafe byte[] ReadCarrier(long offset, int length)
    {
        var buf = new byte[length];
        ReadCarrierExactly(offset, buf);
        return buf;
    }

    /// <summary>设备容量（字节）——Linux BLKGETSIZE64 ioctl；非 Linux/失败回退 fstat 长度。</summary>
    private unsafe long QueryDeviceCapacityBytes(SafeFileHandle handle)
    {
        if (OperatingSystem.IsLinux())
        {
            var borrowed = false;
            try
            {
                handle.DangerousAddRef(ref borrowed);
                ulong size = 0;
                if (TC.Tier.Core.NativeInterop.LibC.Ioctl(handle.DangerousGetHandle().ToInt32(),
                        TC.Tier.Core.NativeInterop.LibC.BlkGetSize64, &size) == 0 && size > 0)
                    return (long)size;
            }
            finally
            {
                if (borrowed) handle.DangerousRelease();
            }
        }
        return RandomAccess.GetLength(handle);
    }

    /// <summary>设备逻辑扇区大小（字节）——BLKSSZGET；失败回退 512（DIO 对齐安全侧）。</summary>
    private unsafe int QueryDeviceSectorSize(SafeFileHandle handle)
    {
        if (OperatingSystem.IsLinux())
        {
            var borrowed = false;
            try
            {
                handle.DangerousAddRef(ref borrowed);
                int sector = 0;
                if (LibC.Ioctl(handle.DangerousGetHandle().ToInt32(),
                        LibC.BlkSszGet, &sector) == 0 && sector > 0)
                    return sector;
            }
            finally
            {
                if (borrowed) handle.DangerousRelease();
            }
        }
        return 512;
    }


    /// <summary>全局字节偏移 → 成员路由（线性拼接：基块 = Σ前序容量）。返回 (成员, 成员内本地偏移, 本段可用字节)。
    /// 成员信息未采纳前（sb 解码阶段——Info 为占位）直通主成员（offset 即本地偏移）。
    /// 降级运行（v2b）：缺失成员数据面访问诚实拒绝（洞数据不可伪造）。</summary>
    private (CarrierMember Member, long LocalOffset, int Segment) Route(long globalOffset, int remaining)
    {
        if (_members.Length == 1 && _members[0].Info.CapacityBlocks == 0)
            return (_members[0], globalOffset, remaining);
        foreach (var m in _members)
        {
            var memberBytes = (long)m.Info.CapacityBlocks * _pageSize;
            if (globalOffset < memberBytes)
            {
                if (m.IsMissing)
                    throw new FileIOException(IOError.IOFailure,
                        $"数据块位于缺失成员（降级运行）：全局偏移 {globalOffset} / 成员 {m.Carrier.Path}——数据不可用（诚实拒绝，v2b）",
                        _carrier.Path, "Read");
                return (m, globalOffset, (int)Math.Min(remaining, memberBytes - globalOffset));
            }
            globalOffset -= memberBytes;
        }
        throw new FileIOException(IOError.IOFailure,
            $"载体访问越界（超出卷容量）：offset={globalOffset}", _carrier.Path, "IO");
    }

    /// <summary>成员 O_DIRECT 读句柄（RM-28）：设备 = 主句柄（本就 O_DIRECT）；文件 = 懒开专用
    /// O_RDONLY|O_DIRECT 只读句柄 + 失败记忆（文件系统不支持时回退缓冲读 + DONTNEED 纪律）。
    /// 读侧专用不破坏一卷一实例（跨进程互斥由锁文件/flock 承担——此句柄只读不出实例）。
    /// 读侧无 RM-34 写侧灾难（O_DIRECT 同步小写每写一次设备往返——那是写路径否决依据）；
    /// 大粒度顺序读恰是 DIO 甜点（台账读粒度曲线：64KB+ 均 9.6GB/s+）。
    /// 与缓冲写在同范围交错由内核 DIO 读纪律保障（先回写重叠脏区再读盘——generic_file_direct_read 同款）。</summary>
    private static SafeFileHandle? GetDioReadHandle(CarrierMember m)
    {
        if (m.IsMissing) return null;
        if (m.Direct) return m.Handle;
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return null;   // RM-36：Windows = NO_BUFFERING 通道
        lock (m)
        {
            if (m.DioReadState == 1) return m.DioReadHandle;
            if (m.DioReadState == 2) return null;
            if (OperatingSystem.IsWindows())
            {
                // RM-36：FILE_FLAG_NO_BUFFERING（0x20000000——FileOptions 位域直传 CreateFile）
                // 只读句柄；扇区对齐纪律与 Linux 同道（弹跳窗 4096 对齐 ≥ 512e/4Kn）
                try
                {
                    m.DioReadHandle = File.OpenHandle(m.Carrier.Path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite, (FileOptions)0x20000000 | FileOptions.Asynchronous);
                    m.DioReadState = 1;
                }
                catch (IOException)
                {
                    m.DioReadState = 2;   // 失败记忆——回退缓冲读
                }
                return m.DioReadHandle;
            }
            const int oRdOnly = 0x0;   // O_RDONLY（FileNative 同款本地常量先例）
            var fd = LibC.Open(m.Carrier.Path,
                oRdOnly | NativeConstants.ODirect, 0);
            if (fd < 0)
            {
                m.DioReadState = 2;   // EINVAL/ENOTSUP 等——失败记忆，此后走缓冲 + DONTNEED 回退
                return null;
            }
            m.DioReadHandle = LibC.WrapFileDescriptor(fd);
            m.DioReadState = 1;
            return m.DioReadHandle;
        }
    }

    /// <summary>直达档载体读（RM-28 + RM-36）：优先 DIO 读通道——Linux = O_RDONLY|O_DIRECT 专用句柄 /
    /// Windows = FILE_FLAG_NO_BUFFERING 只读句柄 / 设备 = 主句柄（本就 O_DIRECT）。
    /// 弹跳窗三重对齐（偏移/长度/缓冲地址）；单段覆盖全剩余且三重对齐时零拷贝直读。
    /// 任一成员 DIO 不可用返回 false——调用方回退缓冲读（Linux 附 DONTNEED 纪律；重读幂等无害）。</summary>
    private unsafe bool TryReadCarrierDio(long offset, Span<byte> destination)
    {
        if (destination.Length == 0) return true;
        var done = 0;
        while (done < destination.Length)
        {
            var (m, localBase, segLen) = Route(offset + done, destination.Length - done);
            var h = GetDioReadHandle(m);
            if (h is null) return false;
            var align = m.Direct ? m.IoAlign : 4096;   // 文件 O_DIRECT：文件系统逻辑块对齐（4K 安全侧）
            var slice = destination.Slice(done, segLen);
            // 零拷贝快道：段覆盖全剩余 + 缓冲地址/偏移/长度全对齐
            var ptr = (nint)System.Runtime.CompilerServices.Unsafe.AsPointer(
                ref MemoryMarshal.GetReference(slice));
            if (segLen == destination.Length - done
                && ptr % align == 0 && localBase % align == 0 && slice.Length % align == 0)
            {
                var got = RandomAccess.Read(h, slice, localBase);
                if (got != slice.Length)
                    throw new FileIOException(IOError.IOFailure,
                        $"载体短读（DIO 直读）：offset={offset}+{done}, len={slice.Length} 实得 {got}", m.Carrier.Path, "Read");
                return true;
            }
            // 弹跳窗（对齐窗整读 + 局部拷贝——窗口上限 1MB 同 ReadCarrierExactly 纪律）
            const int chunkAlign = 1 << 20;
            var windowStart = localBase / align * align;
            var windowEnd = Math.Min(
                (localBase + slice.Length + align - 1) / align * align,
                Math.Min(windowStart + chunkAlign, (localBase / align + segLen / align + 1) * align));
            var windowLen = (int)(windowEnd - windowStart);
            var window = (byte*)NativeMemory.AlignedAlloc((nuint)windowLen, 4096);
            try
            {
                var got = RandomAccess.Read(h, new Span<byte>(window, windowLen), windowStart);
                var inOffset = (int)(localBase - windowStart);
                var need = Math.Min(slice.Length, windowLen - inOffset);
                if (got < inOffset + need)
                    throw new FileIOException(IOError.IOFailure,
                        $"载体短读（DIO 窗口）：offset={offset}+{done}（窗口 {windowStart}+{windowLen} 实得 {got}）",
                        m.Carrier.Path, "Read");
                new ReadOnlySpan<byte>(window + inOffset, need).CopyTo(slice.Slice(0, need));
                done += need;
            }
            finally
            {
                NativeMemory.AlignedFree(window);
            }
        }
        return true;
    }

    private unsafe void ReadCarrierExactly(long offset, Span<byte> destination)
    {
        // 成员分段 + 各自 DIO 纪律（三重对齐：偏移/长度/缓冲地址——RM-05）
        const int chunkAlign = 1 << 20;   // 窗口上限 1MB（O_DIRECT 大段读：4KB 窗口 = 每 4KB 一次 alloc+syscall）
        var done = 0;
        while (done < destination.Length)
        {
            var logicalStart = offset + done;
            var (m, localBase, segLen) = Route(logicalStart, destination.Length - done);
            if (!m.Direct)
            {
                var got = RandomAccess.Read(m.Handle, destination.Slice(done, segLen), localBase);
                if (got != segLen)
                    throw new FileIOException(IOError.IOFailure,
                        $"载体短读：offset={offset}+{done}, len={segLen} 实得 {got}", m.Carrier.Path, "Read");
                done += segLen;
                continue;
            }
            var align = m.IoAlign;
            var windowStart = localBase / align * align;
            var windowEnd = Math.Min(
                (localBase + (destination.Length - done) + align - 1) / align * align,
                Math.Min(windowStart + chunkAlign, (localBase / align + segLen / align + 1) * align));
            var windowLen = (int)(windowEnd - windowStart);
            var window = (byte*)System.Runtime.InteropServices.NativeMemory.AlignedAlloc((nuint)windowLen, 4096);
            try
            {
                var got = RandomAccess.Read(m.Handle, new Span<byte>(window, windowLen), windowStart);
                var inOffset = (int)(localBase - windowStart);
                var need = Math.Min(destination.Length - done, windowLen - inOffset);
                if (got < inOffset + need)
                    throw new FileIOException(IOError.IOFailure,
                        $"载体短读：offset={offset}+{done}（窗口 {windowStart}+{windowLen} 实得 {got}）", m.Carrier.Path, "Read");
                new ReadOnlySpan<byte>(window + inOffset, need).CopyTo(destination.Slice(done, need));
                done += need;
            }
            finally
            {
                NativeMemory.AlignedFree(window);
            }
        }
    }

    private unsafe void WriteCarrier(long offset, ReadOnlySpan<byte> source)
    {
        // 成员分段 + 各自 DIO 纪律（对齐窗口 RMW——数据面已按块对齐，免 RMW 热路径）。
        // 写窗口 64KB（2026-08-19 实测 O_DIRECT 写成本曲线：64KB=189μs 甜点，256KB=3.3ms/1MB=12ms 灾难段）
        const int chunkAlign = 64 << 10;
        var done = 0;
        while (done < source.Length)
        {
            var logicalStart = offset + done;
            var (m, localBase, segLen) = Route(logicalStart, source.Length - done);
            if (!m.Direct)
            {
                RandomAccess.Write(m.Handle, source.Slice(done, segLen), localBase);
                done += segLen;
                continue;
            }
            var align = m.IoAlign;
            var windowStart = localBase / align * align;
            var windowEnd = Math.Min(
                (localBase + (source.Length - done) + align - 1) / align * align,
                Math.Min(windowStart + chunkAlign, windowStart / align * align + segLen + align));
            var windowLen = (int)(windowEnd - windowStart);
            var window = (byte*)NativeMemory.AlignedAlloc((nuint)windowLen, 4096);
            try
            {
                var wspan = new Span<byte>(window, windowLen);
                if (windowStart != localBase)
                {
                    var got = RandomAccess.Read(m.Handle, wspan, windowStart);
                    if (got < windowLen) wspan.Slice(got).Clear();
                }
                var inOffset = (int)(localBase - windowStart);
                var patch = Math.Min(source.Length - done, windowLen - inOffset);
                source.Slice(done, patch).CopyTo(wspan.Slice(inOffset, patch));
                RandomAccess.Write(m.Handle, wspan, windowStart);
                done += patch;
            }
            finally
            {
                NativeMemory.AlignedFree(window);
            }
        }
    }

    internal void FlushCarrier()
    {
        foreach (var m in _members)
            RandomAccess.FlushToDisk(m.Handle);
    }

    /// <summary>
    /// DONTNEED 扫描纪律（2026-08-19 性能轮结论）：文件载体走缓冲 IO（内核 writeback 吸收是缓冲档
    /// 平权 Disk 的存在条件——实测本机 O_DIRECT 同步写地板仅 ~500MB/s，且 fadvise(DONTNEED) 对脏页
    /// 触发立即写回，写路径任何"弃页"手段都等价于把 O_DIRECT 的每写一次设备往返请回来）。
    /// 因此只对<b>直达档读</b>（干净页——弃之无写回代价）施加：扫描不驻留 OS 缓存。
    /// 设备载体（O_DIRECT）无 OS 缓存，no-op。尽力而为：失败静默（fadvise 是 advisory）。</summary>
    internal void DropCarrierCache(long offset, int length)
    {
        if (!OperatingSystem.IsLinux() || length <= 0) return;
        var done = 0;
        while (done < length)
        {
            var (m, localBase, segLen) = Route(offset + done, length - done);
            if (!m.Direct && !m.IsMissing)
            {
                try
                {
                    var borrowed = false;
                    m.Handle.DangerousAddRef(ref borrowed);
                    try
                    {
                        _ = LibC.PosixFadvise(m.Handle.DangerousGetHandle().ToInt32(),
                            localBase, segLen, LibC.PosixFadvDontNeed);   // CA1806：advisory 尽力——返回值即错误码，无处置动作
                    }
                    finally
                    {
                        if (borrowed) m.Handle.DangerousRelease();
                    }
                }
                catch { /* advisory 尽力 */ }
            }
            done += segLen;
        }
    }

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
            if (!_carrier.IsDevice)
                caps |= FileSystemCapabilities.Mmap;   // 文件载体：单区间 MMF 直映射（设备诚实不置位）
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

            var handle = new RawFileHandle(this, entry, options);
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
            else if (MetadataDirty || _timestampsDirty) CommitMetadata();   // sync() 语义：时间戳一并收口
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
                MarkBlocks(x.PhysicalBlock, blocks, used: false);
                TrimDeviceBlocks(x.PhysicalBlock, blocks);   // RM-05：无句柄在档=无读者——即时回收点
                InvalidateCacheBlocks(x.PhysicalBlock, blocks);   // RM-12：删除释放退出缓存
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
                    MarkBlocks(x.PhysicalBlock, blocks, used: false);
                    TrimDeviceBlocks(x.PhysicalBlock, blocks);   // RM-05：覆盖释放（句柄已查在档——即时回收）
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

    /// <summary>挂载访问三态（G2——RawFileHandle.Map 经此过包络校验）。</summary>
    internal AccessMode Access => _readOnly ? AccessMode.Read : _mountAccess;

    /// <summary>维护门闩（句柄写族的拒绝入口——RawFileHandle 经此访问）。</summary>
    internal MaintenanceGate Maintenance => _maintenance;

    /// <summary>是否已 Dispose（RawFileHandle 生命周期语义——fs 关闭后句柄操作统一抛 ObjectDisposedException，
    /// 与 Mem"拔盘"契约对齐：卷状态随实例销毁，句柄静默内存成功 = 永不持久化的假象，必须显式失败）。</summary>
    internal bool IsDisposed => _disposed != 0;

    /// <summary>驻留页计数（测试观测——预取/逐出行为断言用，CrashSimulate 同款后门）。</summary>
    internal int ResidentPageCount => _pages.Count;

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

    /// <summary>读者进入保护域（D1b——RawFileHandle.Read 包夹快照捕获与读取；回收延迟至本读者退出）。</summary>
    internal void EnterReadEpoch() => _readEpoch.Resume();

    /// <summary>读者退出保护域（与 EnterReadEpoch 同线程严格配对）。</summary>
    internal void ExitReadEpoch() => _readEpoch.Suspend();

    /// <summary>是否设备载体（Map 能力判别——设备形态诚实不支持）。</summary>
    internal bool IsDeviceCarrier => _carrier.IsDevice;

    // ═══════════════ 多载体操作族（RM-04 §3.8——扩容/缩容）═══════════════

    /// <summary>在线扩容 = 加载体（§3.8）：成员表事务（检查点原子持久）→ 新块立即可用。
    /// 新成员容量须 64 块对齐（位字不跨成员）；设备载体容量自几何，文件载体必填 capacityBytes。</summary>
    public void AddCarrier(RawCarrier carrier, long capacityBytes = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentNullException.ThrowIfNull(carrier);
        if (_readOnly) ThrowIfReadOnly(nameof(AddCarrier));
        if (_sb.Members.Count >= Sb.MemberTableMax)
            throw new FileIOException(IOError.IOFailure,
                $"成员表满（{Sb.MemberTableMax}）——超上限留布局版本（§3.8）", null, nameof(AddCarrier));
        using var gate = _maintenance.BeginMutation(nameof(AddCarrier), carrier.Path);
        lock (MetadataLock)
        {
            ClaimInstance(carrier, null);
            // 容量解析（设备 = 几何 ioctl；文件 = 必填）+ 64 块对齐 + 扇区校验
            long capacity;
            var probeHandle = OpenProbeHandle(carrier, writable: true);
            try
            {
                if (carrier.IsDevice)
                {
                    capacity = QueryDeviceCapacityBytes(probeHandle);
                    var sector = QueryDeviceSectorSize(probeHandle);
                    if (sector > _pageSize)
                        throw new FileIOException(IOError.IOFailure,
                            $"新成员逻辑扇区 {sector} > 卷块大小 {_pageSize}——几何不兼容", carrier.Path, nameof(AddCarrier));
                }
                else
                {
                    capacity = capacityBytes;
                    if (capacity <= 0)
                        throw new ArgumentException("文件成员须声明 capacityBytes。", nameof(capacityBytes));
                }
                capacity -= capacity % (_pageSize * BitmapAlignBlocks);
                if (capacity <= 0)
                    throw new FileIOException(IOError.IOFailure,
                        $"成员容量不足（64 块对齐后 = {capacity}）", carrier.Path, nameof(AddCarrier));
                // 载体须非 TC 卷（防误并入已格式化载体）
                var probe = new byte[4];
                var got = RandomAccess.Read(probeHandle, probe, 0);
                if (got >= 4 && (probe.AsSpan().SequenceEqual("RAW1"u8) || probe.AsSpan().SequenceEqual("RAWC"u8)))
                    throw new FileIOException(IOError.AlreadyExists,
                        $"载体已是 TC 卷成员：{carrier.Path}", carrier.Path, nameof(AddCarrier));
            }
            finally
            {
                probeHandle.Dispose();
            }

            var bs = (long)_pageSize;
            var capacityBlocks = (ulong)(capacity / bs);
            var bitmapBytes = (capacityBlocks + 7) / 8;
            var bitmapBlocks = (bitmapBytes + (ulong)bs - 1) / (ulong)bs;
            var bitmapStart = (ulong)((HeaderBytes + bs - 1) / bs);
            var info = new MemberEntry(Guid.NewGuid(), capacityBlocks, bitmapStart, bitmapBlocks);

            // 打开成员（锁 + DIO）+ RAWC 身份头 + 位图区清零
            var m = OpenMemberCarrier(carrier, info, writable: true, createIfMissing: !carrier.IsDevice);
            var oldBitmap = _bitmapWords;   // D11：回滚基准（失败时位图/空闲计数/脏字索引复原）
            try
            {
                if (!carrier.IsDevice)
                    RandomAccess.SetLength(m.Handle, capacity);
                m.BaseBlock = _sb.CapacityBlocks;
                var header = new byte[512];
                EncodeMemberHeader(header, info, _sb.Uuid, _sb.Members.Count, _pageSize);
                WriteMemberLocal(m, 0, header);
                var zeros = new byte[_pageSize];
                for (var b = 0UL; b < bitmapBlocks; b++)
                    WriteMemberLocalAligned(m, (long)((bitmapStart + b) * (ulong)_pageSize), zeros);

                // 成员表事务（检查点原子——日志记录含全局块号，追加式扩容下旧记录语义稳定）
                var newWords = new ulong[oldBitmap.LongLength + (long)(capacityBlocks / 64)];
                Array.Copy(oldBitmap, newWords, oldBitmap.LongLength);
                _bitmapWords = newWords;
                _freeBlocks += capacityBlocks;   // 新成员块全部空闲（保留区随后标记扣减）
                for (var w = oldBitmap.LongLength; w < newWords.LongLength; w++) _dirtyBitmapWords.Add((ulong)w);   // 新区全脏（落位图区）
                MarkBlocks(m.BaseBlock, (uint)((long)bitmapStart + (long)bitmapBlocks), used: true);   // 新成员头部+位图保留

                _sb.Members.Add(info);
                _sb.CapacityBlocks += capacityBlocks;
                _sb.Flags |= FlagMultiCarrier;
                // 多载体卷退出自动扩容：成员 0 容量变更会使后续成员基块漂移——容量管理转显式（AddCarrier/RemoveCarrier）
                if (_autoExpand)
                {
                    _sb.Flags = (ushort)(_sb.Flags & ~FlagAutoExpand);
                    _autoExpand = false;
                    _logger?.LogInformation("卷转入多载体管理：自动扩容关闭（容量管理 = AddCarrier/RemoveCarrier）");
                }
                _members = _members.Append(m).ToArray();
                RefreshCarrierDio();
                CommitMetadata();   // 成员表 + 位图 + superblock 原子持久（§3.8 成员表事务）
            }
            catch
            {
                // 失败回滚：新成员登记撤销（载体上残留 RAWC 头——下次 AddCarrier 探测拒并入）
                _sb.Members.Remove(info);
                _sb.CapacityBlocks -= capacityBlocks;
                if (_sb.Members.Count == 1) _sb.Flags = (ushort)(_sb.Flags & ~FlagMultiCarrier);
                _members = _members.Where(x => !ReferenceEquals(x, m)).ToArray();
                RefreshCarrierDio();
                // D11：位图/空闲计数/脏字索引一并回滚（失败后卷内存态一致——半提交成员不得污染分配面）
                _bitmapWords = oldBitmap;
                _freeBlocks = 0;
                foreach (var w in oldBitmap) _freeBlocks += (ulong)(64 - System.Numerics.BitOperations.PopCount(w));
                var totalBits = _sb.CapacityBlocks;
                var usedBeyond = (ulong)oldBitmap.LongLength * 64 - totalBits;
                if (usedBeyond > 0) _freeBlocks -= usedBeyond;
                _dirtyBitmapWords.Clear();
                for (var w = 0UL; w < (ulong)oldBitmap.LongLength; w++) _dirtyBitmapWords.Add(w);   // 全量重写（回滚后基线复位）
                try
                {
                    m.Handle.Dispose();
                }
                catch
                {
                    // ignored
                }

                try { m.CrossProcLock?.Dispose(); }
                catch
                {
                    // ignored
                }

                throw;
            }
        }
    }

    /// <summary>迁移式缩容数据面（RM-04 v2a——btrfs device remove 同构）：
    /// ① 源成员数据区位全部标记占用（分配器自然绕开——迁移目标必落其他成员）；
    /// ② 逐文件逐 extent：数据搬运（读旧写新）+ ApplyExtentRelocate 重定向 + 日志记录；
    /// ③ 页缓存失效旧块。完成即成员全空 → 走摘除路径。fs 锁内（RemoveCarrier 调用）。</summary>
    private void MigrateMemberData(CarrierMember m)
    {
        var memberEnd = m.BaseBlock + m.Info.CapacityBlocks;
        // ① 屏蔽源成员数据区（位图标占——分配绕开；摘除时整体丢弃不计泄漏）
        var firstData = m.BaseBlock + m.Info.BitmapStartLocal + m.Info.BitmapBlocksLocal;
        MarkBlocks(firstData, (uint)(memberEnd - firstData), used: true);

        const int chunkBlocks = 64;   // 256KB 搬运粒度
        var buf = ArrayPool<byte>.Shared.Rent(chunkBlocks * _pageSize);
        try
        {
            foreach (var e in _entries.Values.ToList())
            {
                foreach (var x in e.Extents.Where(x => x.PhysicalBlock < memberEnd
                             && x.PhysicalBlock + (ulong)(x.Length / _pageSize) > m.BaseBlock).ToList())
                {
                    // 逐段搬迁：ExtentRelocate 粒度 = 单个旧 extent（新 run 可拆多段——分配器自由）
                    var blocks = (uint)((x.Length + _pageSize - 1) / _pageSize);
                    var newRuns = new List<(ulong Phys, long Len)>();
                    var remaining = blocks;
                    while (remaining > 0)
                    {
                        var take = Math.Min(remaining, chunkBlocks);
                        var phys = AllocateBlocks(take, "Migrate");
                        newRuns.Add((phys, take * _pageSize));
                        remaining -= take;
                    }
                    // 数据搬运（旧读新写——块粒度对齐，源/目标各自 DIO 纪律经全局路由）
                    long done = 0;
                    foreach (var (phys, len) in newRuns)
                    {
                        for (var off = 0L; off < len; off += chunkBlocks * _pageSize)
                        {
                            var take = (int)Math.Min(len - off, (long)chunkBlocks * _pageSize);
                            ReadCarrierExactly((long)(x.PhysicalBlock * (ulong)_pageSize) + done + off, buf.AsSpan(0, take));
                            WriteCarrier((long)(phys * (ulong)_pageSize) + off, buf.AsSpan(0, take));
                        }
                        done += len;
                        InvalidateCacheBlocks(phys, (uint)(len / _pageSize));   // 新块直落——缓存不驻留旧载体状态
                    }
                    ApplyExtentRelocate(e, x.LogicalStart, x.Length, newRuns);
                    JnlExtentRelocate(e.Path, x.LogicalStart, x.Length, newRuns);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    /// <summary>缩容 = 减载体（§3.8 v1：仅允许移除全空成员——位图全零校验）。
    /// 成员表事务（检查点原子）；被移成员后续载体作废（含 RAWC 头）。</summary>
    public void RemoveCarrier(int memberIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_readOnly) ThrowIfReadOnly(nameof(RemoveCarrier));
        using var gate = _maintenance.BeginMutation(nameof(RemoveCarrier), null);
        lock (MetadataLock)
        {
            if (memberIndex <= 0)
                throw new ArgumentException("主载体（成员 0）不可移除。", nameof(memberIndex));
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(memberIndex, _members.Length, "成员索引超限。");
            var m = _members[memberIndex];
            // 全空校验：数据区位全零（头部+位图保留位在数据区之前——共字时掩码排除）
            var firstData = m.BaseBlock + m.Info.BitmapStartLocal + m.Info.BitmapBlocksLocal;
            var firstDataWord = firstData / 64;
            var nonEmpty = false;
            for (var w = m.BaseBlock / 64; w < (m.BaseBlock + m.Info.CapacityBlocks) / 64; w++)
            {
                var word = _bitmapWords[w];
                if (w == firstDataWord)
                    word &= ulong.MaxValue << (int)(firstData % 64);   // 保留侧位掩除
                if (w >= firstDataWord && word != 0) { nonEmpty = true; break; }
            }
            if (nonEmpty)
                MigrateMemberData(m);   // RM-04 v2a：迁移式缩容（非空成员——块搬迁后摘除）
            JournalCommit();   // 在途记录先落（成员表变更后全局块号语义变化——日志尾必须清空）
            // 成员表事务：摘除（先记旧基块——位图重排拷贝用）
            var oldWords = _bitmapWords;
            var oldMembers = _members;
            _sb.Members.RemoveAt(memberIndex);
            _sb.CapacityBlocks -= m.Info.CapacityBlocks;
            if (_sb.Members.Count == 1) _sb.Flags = (ushort)(_sb.Flags & ~FlagMultiCarrier);
            _members = _members.Where((_, i) => i != memberIndex).ToArray();
            RefreshCarrierDio();
            // 位图重排：按新基块把各成员字段从旧数组搬到新数组（被移成员字全零已验证——丢弃无损）
            var newLen = (_sb.CapacityBlocks + 63) / 64;
            var newWords = new ulong[newLen];
            ulong total = 0;
            foreach (var mm in _members)
            {
                var oldBase = oldMembers[Array.IndexOf(oldMembers, mm)].BaseBlock;
                Array.Copy(oldWords, (long)(oldBase / 64), newWords, (long)(total / 64), (long)(mm.Info.CapacityBlocks / 64));
                mm.BaseBlock = total;
                total += mm.Info.CapacityBlocks;
            }
            _bitmapWords = newWords;
            _freeBlocks = 0;
            foreach (var w in newWords) _freeBlocks += (ulong)(64 - System.Numerics.BitOperations.PopCount(w));
            var totalBits = _sb.CapacityBlocks;
            var usedBeyond = newLen * 64 - totalBits;
            if (usedBeyond > 0) _freeBlocks -= usedBeyond;
            _dirtyBitmapWords.Clear();
            for (var w = 0UL; w < newLen; w++) _dirtyBitmapWords.Add(w);   // 全量重写（布局重排）
            CommitMetadata();   // 摘除原子持久
            SInstances.TryRemove(m.Carrier.IdentityKey, out _);
            try { m.Handle.Dispose(); }
            catch
            {
                // ignored
            }

            try { m.DioReadHandle?.Dispose(); }
            catch
            {
                // ignored
            }

            try { m.CrossProcLock?.Dispose(); }
            catch
            {
                // ignored
            }
        }
    }

    /// <summary>探测句柄（AddCarrier 容量/残留检查用——不复用成员锁路径）。
    /// 文件成员允许创建（新建载体探测语义）；设备成员仅打开。</summary>
    private SafeFileHandle OpenProbeHandle(RawCarrier carrier, bool writable)
        => File.OpenHandle(carrier.Path, carrier.IsDevice ? FileMode.Open : FileMode.OpenOrCreate,
            writable ? FileAccess.ReadWrite : FileAccess.Read,
            FileShare.ReadWrite, FileOptions.Asynchronous);

    private void ThrowIfReadOnly(string op)
    {
        if (_degraded)
            throw new FileIOException(IOError.ReadOnlyVolume,
                $"降级卷不接受 {op}（成员缺失只读形态——RM-04 v2b；修复 = 全量成员重开）", null, op);
        if (_readOnly)
            throw new FileIOException(IOError.ReadOnlyVolume,
                $"只读卷不接受 {op}（ReadOnlyVolume 语义——dirty 降级形态或显式只读打开，§4.1）", null, op);
    }

    private sealed class NoOpLease : IDisposable
    {
        public static readonly NoOpLease Instance = new();
        public void Dispose() { }
    }

    // ═══════════════ IContiguousVolume（dd 快道——§6.2）═══════════════

    /// <summary>整卷原始字节视图（维护租约内由管线调用——载体访问不出实例）。</summary>
    Stream IContiguousVolume.OpenRawBacking(bool writable)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (writable && _readOnly)
            throw new FileIOException(IOError.ReadOnlyVolume, "只读卷不接受可写载体视图", null, "OpenRawBacking");
        return new RawBackingStream(this, (long)(_sb.CapacityBlocks * _sb.BlockSize));
    }

    /// <summary>
    /// 单连续 Written 区间的载体 MMF 直映射（文件载体）——缓存一致性：Map 前排干脏页 + 该文件全部块退出页缓存
    /// （视图 IO 经 OS 页缓存直达载体——视图写入后我们再读经载体可见）。
    /// </summary>
    internal IMappedSection CreateBackingMap(Extent ext, long offset, long length, AccessMode access, string path)
    {
        FlushDirtyPages();
        InvalidateEntryCacheBlocks(ext);

        // RM-04：extent 所属成员路由（跨成员 extent 不能 MMF——映射须单成员单文件）
        var m = MemberForBlock(ext.PhysicalBlock);
        if (ext.PhysicalBlock + (ulong)(ext.Length / _pageSize) > m.BaseBlock + m.Info.CapacityBlocks)
            throw new FileIOException(IOError.Unsupported,
                "跨成员区间不支持 Map（MMF 单文件语义）——整理后重试", path, "Map");
        var fileAccess = access == AccessMode.Read ? FileAccess.Read : FileAccess.ReadWrite;
        var stream = new FileStream(m.Carrier.Path, FileMode.Open, fileAccess, FileShare.ReadWrite);
        // ★ MMF 节访问须与句柄访问一致：只读句柄（GENERIC_READ）建 PAGE_READWRITE 节在 Windows 上
        //   必得 ERROR_ACCESS_DENIED（CreateFileMapping 权限要求）——ReadOnly Map 曾在此确定性抛
        //   UnauthorizedAccessException（满套验证时暴露，RawIntegrity D8/RawWriteBack 同根因）。
        var mmfAccess = access == AccessMode.Read ? MemoryMappedFileAccess.Read : MemoryMappedFileAccess.ReadWrite;
        var mmf = MemoryMappedFile.CreateFromFile(fileStream: stream, mapName: null, capacity: 0L, access: mmfAccess, inheritability: HandleInheritability.None, leaveOpen: false);
        // 视图偏移对齐（Win=64K 分配粒度 / Unix=页）——CreateViewAccessor 会向下取整，指针差值补偿
        var viewOffset = (long)((ext.PhysicalBlock - m.BaseBlock) * (ulong)_pageSize) + (offset - ext.LogicalStart);
        var granularity = OperatingSystem.IsWindows() ? 65536L : 4096L;
        var aligned = viewOffset / granularity * granularity;
        var delta = (int)(viewOffset - aligned);
        var accessor = mmf.CreateViewAccessor(aligned, length + delta, access == AccessMode.Read
            ? MemoryMappedFileAccess.Read
            : MemoryMappedFileAccess.ReadWrite);
        return new RawMappedSection(accessor, stream, mmf, this, ext, (int)length, delta);
    }

    /// <summary>该区间覆盖的全部物理块退出页缓存（Map 一致性——先排干后失效，脏页已清空）。</summary>
    private void InvalidateEntryCacheBlocks(Extent ext)
        => InvalidateCacheBlocks(ext.PhysicalBlock, (uint)((ext.Length + _pageSize - 1) / _pageSize));

    /// <summary>区间是否完整落单成员（MMF 单文件语义前提——D8）。</summary>
    internal bool ExtentWithinSingleMember(Extent ext)
    {
        var m = MemberForBlock(ext.PhysicalBlock);
        return ext.PhysicalBlock + (ulong)(ext.Length / _pageSize) <= m.BaseBlock + m.Info.CapacityBlocks;
    }

    /// <summary>Map 视图关闭回调——区间块再失效（末次写入可见性收口；DIO 载体 fsync 后主句柄读可见 MMF 写入）。</summary>
    internal void OnMapClosed(Extent ext)
    {
        InvalidateEntryCacheBlocks(ext);
        FlushCarrier();   // msync 后 fsync——O_DIRECT 主句柄读经设备（MMF 写入必须已达设备）
    }

    /// <summary>Raw 映射区——MemoryMappedViewAccessor 包装（View=指针 MemoryManager 零拷贝）。</summary>
    private sealed class RawMappedSection : IMappedSection
    {
        private readonly MemoryMappedViewAccessor _accessor;
        private readonly FileStream _stream;
        private readonly MemoryMappedFile _mmf;
        private readonly RawFileSystem _fs;
        private readonly Extent _ext;
        private readonly PointerMemoryManager _manager;
        private readonly int _viewLength;   // 请求长度（视图物理窗口含对齐差值——View 按请求切片）
        private readonly int _delta;         // 对齐差值（指针基址到请求起点的偏移）
        private int _disposed;

        internal RawMappedSection(MemoryMappedViewAccessor accessor, FileStream stream, MemoryMappedFile mmf,
            RawFileSystem fs, Extent ext, int viewLength, int delta)
        {
            _delta = delta;
            _accessor = accessor;
            _stream = stream;
            _mmf = mmf;
            _fs = fs;
            _ext = ext;
            _viewLength = viewLength;
            _manager = CreateManager();
        }

        private unsafe PointerMemoryManager CreateManager()
        {
            byte* ptr = null;
            _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
            return new PointerMemoryManager(ptr, checked((int)_accessor.SafeMemoryMappedViewHandle.ByteLength));
        }

        public Memory<byte> View
        {
            get
            {
                ObjectDisposedException.ThrowIf(_disposed != 0, this);
                return _manager.Memory.Slice(_delta, _viewLength);   // 差值补偿 + 按请求长度暴露
            }
        }

        public void Advise(FileAdvise advise) { /* no-op（映射级提示——v1 未接） */ }

        public void Flush()
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            _accessor.Flush();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            // DIO 载体一致性：MMF 脏页经 OS 页缓存，O_DIRECT 主句柄读绕过——关闭前排干（msync）+ fsync
            // 后 O_DIRECT 读可见视图写入（缓冲载体无害、语义同构）
            try { _accessor.Flush(); } catch { /* 尽力 */ }
            _manager.Dispose();
            _accessor.Dispose();
            _mmf.Dispose();
            _stream.Dispose();
            _fs.OnMapClosed(_ext);
        }
    }

    /// <summary>原生指针 MemoryManager（MMF 视图零拷贝 View）。</summary>
    private sealed unsafe class PointerMemoryManager(byte* ptr, int length) : MemoryManager<byte>
    {
        private readonly byte* _ptr = ptr;
        private readonly int _length = length;
        private int _disposed;

        public override Span<byte> GetSpan()
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            return new Span<byte>(_ptr, _length);
        }

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            if (elementIndex < 0 || elementIndex >= _length) throw new ArgumentOutOfRangeException(nameof(elementIndex));
            return new MemoryHandle(_ptr + elementIndex);
        }

        public override void Unpin() { }

        public void Dispose() => _disposed = 1;   // MemoryManager 的 Dispose 是显式接口实现——自持 public 出口

        protected override void Dispose(bool disposing) { _disposed = 1; }
    }

    /// <summary>镜像后重载：内存元数据/页缓存清空并从盘重建（管线维护租约内调用——§6.2）。</summary>
    void IContiguousVolume.OnMirrorCompleted()
    {
        lock (MetadataLock)
        {
            _entries.Clear();
            _sortedKeys.Clear();   // RM-11 索引维护
            _directories.Clear();
            _journalReserveBlocks.Clear();
            _pages.Clear();
            _dirtyPages.Clear();   // 脏页索引同步清（漏清 = 陈旧 Page 引用滞留——后续排干会写已重分配块）
            ReturnRecordBuffers(_pendingRecords);   // RM-30：作废记录缓冲归还
            _pendingRecords.Clear();   // 镜像前在途记录作废（盘上日志随 LoadAndRecover 重放重建）
            // D1b：镜像重载后回收队列清空（盘上状态重建——旧批次无意义）。
            // _retireSeq/_safeBatch/_bumpPending 保持单调不复位：在途 bump 回调按旧批次推进无害，
            // 后续新回收取更高批次号走常规协议（过早复位会使陈旧回调把安全批次推高到未保护批次之上）。
            _retiredBlocks.Clear();
            while (_prefetchQueue.TryDequeue(out _)) { }   // 性能债 6：陈旧物理块预取作废（镜像重载后布局全变）
            Interlocked.Exchange(ref _dirtyBytes, 0);
            while (_lru.TryDequeue(out _)) { }
            Interlocked.Exchange(ref _pageBytes, 0);
            LoadAndRecover();
        }
    }

    /// <summary>载体直视流（Position 驱动的定位读写——维护租约内独占使用）。
    /// 经 fs 对齐通道（O_DIRECT 载体下未对齐访问自动弹跳——RM-05 DIO 纪律）。</summary>
    private sealed class RawBackingStream(RawFileSystem fs, long length) : Stream
    {
        private readonly RawFileSystem _fs = fs;
        private readonly long _length = length;
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _position; set => _position = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(count, (int)Math.Max(0, _length - _position));
            if (n <= 0) return 0;
            _fs.ReadCarrierExactly(_position, buffer.AsSpan(offset, n));
            _position += n;
            return n;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _fs.WriteCarrier(_position, buffer.AsSpan(offset, count));
            _position += count;
        }

        public override long Seek(long offset, SeekOrigin origin)
            => _position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };

        public override void Flush() => _fs.FlushCarrier();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    // ═══════════════ 释放（clean 关闭协议）═══════════════

    /// <summary>
    /// 关闭：提交 + 置 clean + 双侧轮写（§4.1 clean 关闭协议）→ 释放跨进程锁与登记。
    /// 未调 Dispose 的进程退出 = 崩溃语义（dirty 残留 → 下次打开走恢复）。
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            CleanShutdown();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "clean 关闭协议失败（残留 dirty——下次打开走恢复路径）");
        }
        finally
        {
            ReleaseResources();
        }
    }

    /// <summary>测试后门：模拟崩溃——跳过 clean 关闭协议，仅释放资源与登记（dirty 残留 → 下次打开走恢复）。</summary>
    internal void CrashSimulate()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        ReleaseResources();
    }

    private void ReleaseResources()
    {
        StopFlusher();   // flusher 先于载体句柄停止（RM-02——后台线程不碰已释放句柄）
        StopPrefetcher();   // 预读线程同序（性能债 6）
        // ★ 退全部成员载体键（RM-04 修复：此前只退主载体——成员键滞留使已 Dispose 卷的成员载体永久不可开）
        foreach (var m in _members)
            SInstances.TryRemove(m.Carrier.IdentityKey, out _);
        SInstances.TryRemove(_carrier.IdentityKey, out _);
        if (_sb is not null) SInstances.TryRemove($"uuid:{_sb.Uuid}", out _);   // New 早期失败（格式化前）sb 未立——NRE 掩蔽修复
        foreach (var m in _members)
        {
            try { m.Handle.Dispose(); } catch { /* 尽力 */ }
            try { m.DioReadHandle?.Dispose(); } catch { /* 尽力 */ }   // RM-28：直达读专用句柄同批释放
            try { m.CrossProcLock?.Dispose(); } catch { /* 尽力 */ }
        }
    }
}
