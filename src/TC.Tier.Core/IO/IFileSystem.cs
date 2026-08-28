namespace TC.Tier.Core.IO;

/// <summary>
/// <b>根空间管理器</b>——命名空间平面（路径/目录/枚举/元数据）与数据平面 <see cref="IFileHandle"/> 拆分
/// （OS 模型：pread 属 fd，rename/readdir 属命名空间）。
/// <para>★ 模型（filesystem-root-space-design）：一个 IFileSystem = 整个存储系统的<b>根空间</b>——
///   内部全相对路径的<b>层级命名空间</b>（'/' 唯一分隔符，组件化校验见 PathValidator.ValidateRelative）。
///   目标布局 <c>{root}/{数据结构}/{引擎}/{段文件、整理目录}</c>；上层经本表面完成全部命名空间管理，
///   零 BCL File/Directory 依赖（三介质平权：Disk/Mem/Remote 实现同一接口，能力位表达差异）。</para>
/// <para>★ 创建/句柄解耦：<see cref="CreateFile"/> 提前创建（预分配+元数据，建段协议移出热路径）；
///   <see cref="Open"/> 保留即建即用模式族（FileMode 语义）。</para>
/// <para>★ FileExtra 平面（§3.6）：一份不透明附加数据 ≤<see cref="MaxFileExtraBytes"/>（创建即写/句柄四成员
///   2KB 上限）；<see cref="Stat"/> 全量读；枚举条目 <see cref="FsEntry"/> 轻量不携带（S3 列举不返回
///   用户元数据——携带 = 逐键 Head，不可接受）。</para>
/// <para>★ 枚举契约（对齐 POSIX readdir）：不保证原子快照（枚举期间增删可见性未定义）、不保证顺序
///   （消费者自行排序）；模式匹配 = <c>*</c>/<c>?</c> 通配（BCL MatchType.Simple，Ordinal），
///   匹配目标为条目最终组件名；单参重载的 string 实参是 <b>pattern</b>（根范围），子目录枚举须双参。</para>
/// <para>★ Dispose 契约（方向差异必须显式认知）：磁盘 = 仅释放 fs 自持资源（卷锁违约释放、探测缓存），
///   <b>不关闭</b>消费者持有的句柄（OS 句柄归消费者），已开句柄继续有效；mem 特例 = 销毁卷（"拔盘"）——
///   全部槽归还池（含 Detached），此后其任何句柄操作抛 <see cref="ObjectDisposedException"/>。</para>
/// </summary>
public interface IFileSystem : IDisposable
{
    /// <summary>
    /// FileExtra 统一上限（字节）——全介质同限，超限抛 <see cref="ArgumentException"/>（全量/偏移写同一封顶）。
    /// <para>★ 数学：S3 头部 2048 字节总预算 − 键前缀开销，按 base64 3:4 膨胀折算的可证明安全值（§3.6）。</para>
    /// </summary>
    const int MaxFileExtraBytes = 1536;


    /// <summary>能力协商位——构造时一次性探测（消费者主动避免依赖回退）。</summary>
    FileSystemCapabilities Capabilities { get; }

    /// <summary>卷几何——SectorSize（DIO 读写对齐基准）与 AllocationUnit（空间操作对齐基准）独立探测。</summary>
    VolumeInfo Volume { get; }

    /// <summary>★ 句柄入口——按显式打开语义打开/创建文件（PreallocateSize&gt;0 时 open 即幂等预分配）。</summary>
    /// <param name="path">根下相对路径（层级命名空间，经 PathValidator.ValidateRelative 校验）。</param>
    /// <param name="options">打开语义四要素 + 预分配。</param>
    /// <returns>文件句柄</returns>
    IFileHandle Open(string path, FileOpenOptions options);

    /// <summary>根目录 mkdir -p（幂等）——引擎 Initialize 必经。</summary>
    void EnsureRoot();

    /// <summary>目录级持久化（父目录 fsync）——新建文件的目录项落盘。</summary>
    void FlushRoot();

    // ═══════════════════════════════════════════════════════════════
    //  目录族
    // ═══════════════════════════════════════════════════════════════

    /// <summary>mkdir -p（幂等，登记全部祖先组件）+ 耐久（新建目录与父目录 fsync）。</summary>
    /// <param name="path">相对目录路径。</param>
    void CreateDirectory(string path);

    /// <summary>删除目录（POSIX rmdir：仅限空——非空抛 <see cref="IOError.DirectoryNotEmpty"/>）+ 父目录 fsync。
    /// <para>★ 不提供递归删除（危险操作不藏糖：EnumerateFiles+Delete+DeleteDirectory 显式组合）。</para></summary>
    /// <param name="path">相对目录路径。</param>
    void DeleteDirectory(string path);

    /// <summary>目录存在性（Remote：前缀下有对象或子前缀——能力位 EmptyDirectories 未置位时创建后未必可见）。</summary>
    /// <param name="path">相对目录路径。</param>
    bool DirectoryExists(string path);

    /// <summary>
    /// 目录整树移动。dest 已存在抛 <see cref="IOError.AlreadyExists"/>（不提供 overwrite——平台语义分歧大）。
    /// <para>★ 原子性由能力位 <see cref="FileSystemCapabilities.AtomicDirectoryMove"/> 表达：
    ///   Disk/Mem 原子；Remote 回退 = 逐对象 Copy+Delete（非原子，部分失败有残留）。</para></summary>
    /// <param name="source">相对源目录路径。</param>
    /// <param name="dest">相对目标目录路径。</param>
    void MoveDirectory(string source, string dest);

    // ═══════════════════════════════════════════════════════════════
    //  文件创建（与句柄解耦）/ 元数据
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 显式提前创建文件（与 <see cref="Open"/> 解耦）——空文件 + 可选预分配（毫秒级真预留）+
    /// 可选 FileExtra（≤<see cref="MaxFileExtraBytes"/>）+ 目录项 fsync。已存在抛 <see cref="IOError.AlreadyExists"/>
    /// （幂等由消费者 Exists 前置组合——显式创建语义，重复初始化 bug 不静默吞）。
    /// </summary>
    /// <param name="path">相对路径（父目录须存在——对齐 Open 的 ENOENT 语义）。</param>
    /// <param name="preallocateSize">预分配字节数（0 = 不预分配）。</param>
    /// <param name="extra">FileExtra 附加数据（default = 无）。</param>
    void CreateFile(string path, long preallocateSize = 0, ReadOnlyMemory<byte> extra = default);

    /// <summary>文件存在性（目录存在性见 <see cref="DirectoryExists"/>）。</summary>
    /// <param name="path">相对路径。</param>
    bool Exists(string path);

    /// <summary>耐久删除文件（=FileNative.DeleteFileDurably 语义）。</summary>
    /// <param name="path">相对路径。</param>
    void Delete(string path);

    /// <summary>
    /// 文件移动/换名（=MoveFileDurably 语义，内建父目录刷盘——能力位 DurableRename）。
    /// <para>★ overwrite 必须显式：true=POSIX rename 原子覆盖；false=目标已存在抛 <see cref="IOError.AlreadyExists"/>。</para>
    /// </summary>
    /// <param name="source">相对源路径。</param>
    /// <param name="dest">相对目标路径。</param>
    /// <param name="overwrite">是否覆盖已存在的目标文件。</param>
    void Move(string source, string dest, bool overwrite = false);

    /// <summary>单条目完整信息（基本字段 + 元数据；Name 回显输入路径；文件或目录自动判别）。</summary>
    /// <exception cref="FileIOException">IOError.NotFound——条目不存在。</exception>
    /// <param name="path">相对路径。</param>
    /// <returns>条目信息</returns>
    FsEntryInfo Stat(string path);

    // ═══════════════════════════════════════════════════════════════
    //  枚举族（三族同形态：单参=根+pattern；双参=path+pattern；recursive 默认 false）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>枚举根层文件（pattern 缺省 "*" 全匹配；recursive=true 全部后代）。</summary>
    /// <exception cref="FileIOException">IOError.NotFound——目录不存在（仅双参形态的子目录路径）。</exception>
    /// <param name="pattern">通配模式（仅匹配最终组件名，BCL MatchType.Simple，Ordinal）。</param>
    /// <param name="recursive">是否递归枚举全部后代目录。</param>
    /// <returns>枚举条目集合</returns>
    IEnumerable<FsEntry> EnumerateFiles(string pattern = "*", bool recursive = false);

    /// <summary>枚举子目录层文件。Name = 相对所枚举目录的路径（recursive 时多组件）。</summary>
    /// <exception cref="FileIOException">IOError.NotFound——目录不存在（仅双参形态的子目录路径）。</exception>
    /// <param name="path">相对目录路径。</param>
    /// <param name="pattern">通配模式（仅匹配最终组件名， BCL MatchType.Simple，Ordinal）。</param>
    /// <param name="recursive">是否递归枚举全部后代目录。</param>
    /// <returns>枚举条目集合</returns>
    IEnumerable<FsEntry> EnumerateFiles(string path, string pattern, bool recursive = false);

    /// <summary>枚举根层子目录。</summary>
    /// <param name="pattern">通配模式（仅匹配最终组件名，BCL MatchType.Simple，Ordinal）。</param>
    /// <param name="recursive">是否递归枚举全部后代目录。</param>
    /// <returns>枚举条目集合</returns>
    IEnumerable<FsEntry> EnumerateDirectories(string pattern = "*", bool recursive = false);

    /// <summary>枚举子目录的一层子目录（recursive=true = 全部后代目录）。</summary>
    /// <param name="path">相对目录路径。</param>
    /// <param name="pattern">通配模式（仅匹配最终组件名，BCL MatchType.Simple，Ordinal）。</param>
    /// <param name="recursive">是否递归枚举全部后代目录。</param>
    /// <returns>枚举条目集合</returns>
    IEnumerable<FsEntry> EnumerateDirectories(string path, string pattern, bool recursive = false);

    /// <summary>
    /// 混合枚举（文件+目录一次产出——引擎扫盘形态；Remote 单次列举同时出 Objects+CommonPrefixes）。
    /// 契约：同参下结果 = EnumerateFiles ∪ EnumerateDirectories。
    /// </summary>
    /// <param name="pattern">通配模式（仅匹配最终组件名，BCL MatchType.Simple，Ordinal）。</param>
    /// <param name="recursive">是否递归枚举全部后代目录。</param>
    /// <returns>枚举条目集合</returns>
    IEnumerable<FsEntry> EnumerateEntries(string pattern = "*", bool recursive = false);

    /// <summary>混合枚举（子目录范围）。</summary>
    /// <param name="path">相对目录路径。</param>
    /// <param name="pattern">通配模式（仅匹配最终组件名，BCL MatchType.Simple，Ordinal）。</param>
    /// <param name="recursive">是否递归枚举全部后代目录。</param>
    /// <returns>枚举条目集合</returns>
    IEnumerable<FsEntry> EnumerateEntries(string path, string pattern, bool recursive = false);

    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 获取卷级排他锁（能力位 ExclusiveLock；未置位实现抛 <see cref="IOError.Unsupported"/>——含 MemoryFileSystem）。
    /// <para>★ 语义：阻塞获取（可带超时）；卷级互斥（同进程多 fs 实例 + 跨进程 lock file）；
    ///   非重入（同持有者二次 Acquire 抛异常）；持锁中 fs.Dispose 视为违约释放并告警。</para>
    /// <para>★ 返回 IDisposable lease——RAII + 异常安全（Dispose 即释放）。</para>
    /// </summary>
    /// <param name="timeout">获取锁的超时（默认无限等待）。</param>
    /// <returns>排他锁租约</returns>
    IDisposable AcquireExclusive(TimeSpan timeout);

    /// <summary>
    /// 进入维护态（能力位 MaintenanceGate 门控；未置位实现抛 <see cref="IOError.Unsupported"/>）——
    /// 采集/还原静默快照的通用前置（raw-medium-and-conversion-design §8）。
    /// <para>★ 语义：闭门 → 阻塞等待在途<b>变异</b>归零（ct 可取消；消费者业务在途收敛是消费者契约，§8.2）
    ///   → 返回 IDisposable 租约（Dispose 即解除）。非重入：维护中二次 Enter 抛
    ///   <see cref="FileIOException"/>(<see cref="IOError.UnderMaintenance"/>)。</para>
    /// <para>★ scope=<see cref="MaintenanceScope.WriteOperations"/>：命名空间变更与句柄写族拒绝（读放行——备份档）；
    ///   scope=<see cref="MaintenanceScope.AllOperations"/>：读写全拒（完全隔离档）。
    ///   被拒操作抛 <see cref="FileIOException"/>(<see cref="IOError.UnderMaintenance"/>)——与能力位语义
    ///   <see cref="IOError.Unsupported"/> 分离，调用方可映射"维护中"提示。</para>
    /// <para>★ Flush/FlushRoot 不拒（关闭协议的组成：进维护 → 收敛 → Flush 置 clean）。</para>
    /// </summary>
    /// <param name="reason">维护理由（诊断/可观测）。</param>
    /// <param name="scope">拒绝范围。</param>
    /// <param name="ct">在途收敛等待的取消令牌。</param>
    /// <returns>维护租约</returns>
    IDisposable EnterMaintenance(string reason, MaintenanceScope scope, CancellationToken ct = default);
}
