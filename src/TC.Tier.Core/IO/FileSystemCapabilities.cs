namespace TC.Tier.Core.IO;

/// <summary>
/// 文件系统能力协商位——介质异构的显式表达（一次性探测，消费者主动避免依赖回退）。
/// <para>★ 契约：每个能力位对应的操作在<b>未置位</b>的实现上有文档化回退
///   （xattr→no-op、PunchHole→memset）或抛 <see cref="IOError.Unsupported"/>（无回退族，如 CollapseRange）。</para>
/// <para>★ <see cref="DirectIO"/> 是 fs/卷级<b>静态</b>能力（构造时探测）；句柄实际探测结果由
///   <see cref="IFileHandle.UnbufferedSupport"/> 报告——两层互补，不可互相取代。</para>
/// </summary>
[Flags]
public enum FileSystemCapabilities
{
    /// <summary>无能力。</summary>
    None = 0,

    /// <summary>PunchHole 真实物理回收（否则 memset 模拟——mem Reserved 模式即如此）。</summary>
    Sparse = 1 << 0,

    /// <summary>Move 内建父目录刷盘（否则仅 rename）。</summary>
    DurableRename = 1 << 2,

    /// <summary>无缓冲 IO——fs/卷级静态能力（构造时探测；tmpfs 等例外在此反映）。句柄级结果见 <see cref="IFileHandle.UnbufferedSupport"/>。</summary>
    DirectIO = 1 << 3,

    /// <summary>写透（FILE_FLAG_WRITE_THROUGH / O_SYNC）。</summary>
    WriteThrough = 1 << 4,

    /// <summary>FlushData ≠ Flush（真 fdatasync）——仅 Linux 置位；Win/macOS 调用 FlushData ≡ Flush 全量回退不抛。</summary>
    FlushDataOnly = 1 << 5,

    /// <summary>文件间高效拷贝（copy_file_range/FICLONE；否则回退用户态循环——能力位诚实告知无零拷贝加速）。</summary>
    CopyRange = 1 << 6,

    /// <summary>readv/writev 向量 IO（否则回退逐片循环）。</summary>
    VectorIO = 1 << 7,

    /// <summary>CollapseRange/InsertRange（不支持平台抛 <see cref="IOError.Unsupported"/>，无回退）。</summary>
    RangeShift = 1 << 8,

    /// <summary>访问提示 posix_fadvise（否则 no-op）。</summary>
    Advise = 1 << 9,

    /// <summary>跨进程卷锁（AcquireExclusive；未置位实现调用抛 Unsupported——含 MemoryFileSystem）。</summary>
    ExclusiveLock = 1 << 10,

    /// <summary>字节范围锁（否则抛 Unsupported 或降级整文件锁——能力位表达实际粒度）。</summary>
    RangeLock = 1 << 11,

    /// <summary>内存映射（mem 介质天然直址；否则 BCL MemoryMappedFile 封装）。</summary>
    Mmap = 1 << 12,

    /// <summary>
    /// 已有文件高效随机覆写（无延迟加载悬崖）——Disk/Mem 置位（pwrite/槽直址天然成立）；
    /// Remote 不置位（打开已有文件随机覆写触发按需拉取全量——远端同构"页缺失"代价，io.md 差异表）。
    /// 消费者据此决策访问模式（如 Compact 源段读、随机更新负载）。
    /// </summary>
    RandomWrite = 1 << 13,

    /// <summary>
    /// 空目录真实存在（CreateDirectory 后 DirectoryExists=true）——Disk ✓（真目录）/ Mem ✓（显式集合）；
    /// Remote 不置位（S3 前缀模拟——目录因内容而存在，CreateDirectory 为文档化 no-op）。
    /// </summary>
    EmptyDirectories = 1 << 14,

    /// <summary>
    /// 目录移动原子（MoveDirectory 介质语义判别）——Disk ✓（同根内必同卷 rename）/ Mem ✓（锁内批量交换）；
    /// Remote 不置位（回退 = 逐对象 Copy+Delete，非原子，部分失败有残留）。
    /// </summary>
    AtomicDirectoryMove = 1 << 15,

    /// <summary>
    /// 根空间数据面 = 单一连续后端（Raw 介质两形态——.raw 文件与块设备；单一后端是 Raw 的定义性质）——
    /// 整体采集可走单句柄 [0, Length) 顺序读，零命名空间参与；多载体卷语义为"每载体连续"（逐载体段拷贝）。
    /// 结构化根空间（Disk 目录树 / Mem——Reserved 为每文件连续而非整卷连续 / Remote 对象空间）不置位。
    /// <para>★ 消费者（采集/还原管线）据此自动路由 dd 快道，不按介质硬编码（raw-medium-and-conversion-design §6）。</para>
    /// </summary>
    ContiguousCapture = 1 << 16,

    /// <summary>
    /// 根空间维护门闩（EnterMaintenance——进入维护态后该根空间全部句柄操作按 scope 拒绝，RAII lease 释放即解除）。
    /// 未置位实现调用抛 <see cref="IOError.Unsupported"/>（与 <see cref="ExclusiveLock"/> 同风格）。
    /// <para>★ 采集静默快照的通用前置（raw-medium-and-conversion-design §8）；消费者业务在途收敛是消费者契约。</para>
    /// </summary>
    MaintenanceGate = 1 << 17,
}
