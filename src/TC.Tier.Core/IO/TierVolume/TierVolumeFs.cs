using System.Collections.Concurrent;
using System.IO.MemoryMappedFiles;
using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using TC.Tier.Core.IO.Image;
using TC.Tier.Core.IO.Shared;

namespace TC.Tier.Core.IO.TierVolume;

/// <summary>
/// TierVolume 根空间——第四介质（自维护布局的连续后端；既是活卷又是存档，本地持久化推荐位 §1.4）。
/// <para>★ 统一块空间（§3.1）：无元数据台阶——条目/区间/FileExtra 唯一上限 = 块数；
///   区间三态（§3.2）：洞读零 / unwritten 预分配 / written——逻辑物理分离。</para>
/// <para>★ 一卷一实例（§2.4）：进程内登记（载体身份 + 卷 UUID 双键）+ 跨进程排他——
///   文件载体伴生锁文件；设备载体 = Linux flock(LOCK_EX|LOCK_NB) / Windows CreateFile share=0 独占
///   + FSCTL_LOCK_VOLUME（卷锁定；物理盘独占句柄）。</para>
/// <para>★ 断电恢复底线（§4.1 + raw-journal）：journal 物理循环日志（有效前缀提交——重放与写路径
///   共用操作函数）+ CoW 元数据提交 + superblock 原子翻转 + 可达性对账（孤儿回收——位图 = 可达集）。</para>
/// <para>★ 数据面：O_DIRECT（设备载体强制 RM-05 / 文件载体 O_DIRECT 句柄 RM-28——未对齐访问经
///   DioAlignment 弹跳窗口适配）；锁外快照读者保护（_readEpoch——块回收延迟至在途读者退出）；
///   载体写穿档（IS-03：FILE_FLAG_WRITE_THROUGH/O_SYNC——journal 免独立 fsync）；预分配轴
///   （IS-02/IS-04：Metadata 稀疏 / Full 物理占位）；同文件写并发档（V2 §2.1：Parallel 数据段锁外并发）。</para>
/// <para>★ 产品能力（V2 §1）：卷级快照（冻结检查点 + 命中冻结块强制 CoW——快照表 superblock 内联、
///   删除 = 位图差集对账）；增量导出（journal delta 帧——脏块跟踪集，检查点复位）；
///   多载体成员装配（RM-04 §3.8——线性拼接 + 身份校验；成员缺失降级只读）；自动扩容
///   （quota=-1 文件载体按需增长到磁盘物理满）；快照挂载（只读冻结态视图——与活卷并发由冻结纪律保证）。</para>
/// </summary>
public sealed partial class TierVolumeFs : IFileSystem, IContiguousVolume
{
    /// <summary>进程内实例登记表（一卷一实例——载体身份键 + UUID 双查）。</summary>
    private static readonly ConcurrentDictionary<string, TierVolumeFs> SInstances = new();

    private readonly TierVolumeCarrier _carrier;                   // 主载体（成员 0）
    private readonly ILogger? _logger;
    private readonly bool _readOnly;
    private readonly AccessMode _mountAccess;                  // G2：挂载访问（Write 在入口即拒——虚拟卷无只写）
    private ulong? _quotaCapBlocks;                            // Open 收紧：min(quota, 供给) 折块（null = 不收紧）
    /// <summary>
    /// 元数据 + 数据面 v1 全局锁（TierVolumeFileHandle 经此串行）
    /// </summary>
    internal readonly object MetadataLock = new();       // 元数据 + 数据面 v1 全局锁（TierVolumeFileHandle 经此串行）
    private readonly LightEpoch _readEpoch = new();       // D1b：锁外快照读者保护域——块回收延迟至全部在途读者退出
    private readonly ConcurrentDictionary<string, AppendCursor> _appendCursors = new();
    private readonly SharingRegistry _sharing = new();
    private readonly MaintenanceGate _maintenance = new();
    private readonly List<ulong> _journalReserveBlocks = [];   // 日志物理保留（§3.9——格式化标记占用）

    /// <summary>成员载体运行态（RM-04 §3.8——线性拼接；成员 0 = 主载体）。</summary>
    internal sealed class CarrierMember
    {
        public required TierVolumeCarrier Carrier;
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
    private PreallocationMode _preallocation;   // 载体预分配方式（IS-02：Full = 不标记稀疏 + 创建时物理物化）
    private bool _carrierWriteThrough;   // 载体句柄写穿档（IS-03：FILE_FLAG_WRITE_THROUGH/O_SYNC——journal 免独立 fsync）
    private bool _parallelWrites;   // 同文件写并发档（V2 §2.1：Parallel = 数据段锁外并发 + 合并提交）
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
    private TierVolumeFs(TierVolumeCarrier carrier, TierVolumeOpenOptions options, ILogger? logger)
    {
        _carrier = carrier;
        _logger = logger;
        _readOnly = options.Access == AccessMode.Read;
        _mountAccess = options.Access;
        _pageBudget = options.PageCacheBytes;
        _preallocation = options.Preallocation;
        _carrierWriteThrough = options.CarrierWriteThrough;
        _parallelWrites = options.WriteConcurrency == WriteConcurrencyMode.Parallel;
        _backgroundDirtyThreshold = Math.Max(1L << 20, _pageBudget / 8);   // flusher 唤醒阈值（RM-02）
    }
}
