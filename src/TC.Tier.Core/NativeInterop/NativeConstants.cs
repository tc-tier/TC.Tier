using System.Runtime.InteropServices;

namespace TC.Tier.Core.NativeInterop;

/// <summary>
/// 原生互操作常量集中定义。消除散落在 Kernel32 / LibC 中的常量声明，
/// 统一管理 open flags / fcntl commands / IO 错误码 / 文件标志 / 权限码。
/// <para>按平台/用途 region 分类，所有 P/Invoke 相关常量归此一处。</para>
/// </summary>
internal static class NativeConstants
{
    // ════════════════════════════════════════════════════════════
    // 区域：Windows 错误码与文件标志（kernel32 / file IO）
    // ════════════════════════════════════════════════════════════

    /// <summary>IO 重叠操作进行中（非错误，IOCP 路径判定用）。</summary>
    public const int ErrorIoPending = 997;

    /// <summary>路径未找到错误码。</summary>
    public const int ErrorPathNotFound = 3;

    /// <summary>Windows 最大路径长度。</summary>
    public const int Win32MaxPath = 260;

    /// <summary>GENERIC_READ 访问标志。</summary>
    public const uint GenericRead = 0x80000000;

    /// <summary>GENERIC_WRITE 访问标志。</summary>
    public const uint GenericWrite = 0x40000000;

    /// <summary>FILE_SHARE_READ 共享标志（快照挂载只读开口——与活卷并发由冻结纪律保证）。</summary>
    public const uint FileShareRead = 0x00000001;

    /// <summary>FILE_SHARE_WRITE 共享标志。</summary>
    public const uint FileShareWrite = 0x00000002;

    /// <summary>OPEN_EXISTING 创建方式（设备/卷必须已存在——裸设备无创建语义）。</summary>
    public const uint OpenExisting = 3;

    /// <summary>FILE_FLAG_WRITE_THROUGH（写完成即达设备——载体写穿档 IS-03 的 Windows 化身）。</summary>
    public const uint FileFlagWriteThrough = 0x80000000;

    /// <summary>FSCTL_LOCK_VOLUME（卷锁定——独占排他，防挂载层写入；用户拍板：独占 + 锁卷）。</summary>
    public const uint FsctlLockVolume = 0x00090018;

    /// <summary>FSCTL_DISMOUNT_VOLUME（卸载卷——本实现不用：危险操作，锁定失败由调用方手动卸载）。</summary>
    public const uint FsctlDismountVolume = 0x00090020;

    /// <summary>关闭时删除文件标志。</summary>
    public const uint FileFlagDeleteOnClose = 0x04000000;

    /// <summary>无缓冲标志（DirectIO 用）。</summary>
    public const uint FileFlagNoBuffering = 0x20000000;

    /// <summary>重叠 IO 标志（IOCP 用）。</summary>
    public const uint FileFlagOverlapped = 0x40000000;

    /// <summary>共享删除标志。</summary>
    public const uint FileShareDelete = 0x00000004;

    /// <summary>所有处理器组（NUMA 查询用）。</summary>
    public const uint AllProcessorGroups = 0xffff;

    // ════════════════════════════════════════════════════════════
    // 区域：Unix open(2) flags（libc）
    // ════════════════════════════════════════════════════════════

    /// <summary>O_RDWR：读写模式打开。</summary>
    public const int ORdwr = 0x0002;

    /// <summary>O_CREAT：不存在则创建。</summary>
    public const int OCreat = 0x0040;

    /// <summary>O_DIRECT：Direct IO（绕过页缓存，Linux 专用）。</summary>
    public const int ODirect = 0x4000;

    /// <summary>★ Spec 26：O_DSYNC 同步数据写入（刷数据 + 必要元数据，跳过 mtime/cmtime —— WAL 追加写最优）。</summary>
    public const int ODsync = 0x1000;

    /// <summary>★ Spec 26：O_SYNC 同步写入（刷数据 + 全部元数据）—— O_DSYNC 不可用时的 fallback。</summary>
    public const int OSync = 0x101000;

    // ════════════════════════════════════════════════════════════
    // 区域：Unix lseek(2) whence 值（libc）
    // ════════════════════════════════════════════════════════════

    /// <summary>SEEK_END = 2：相对文件末尾偏移（lseek(fd, 0, SEEK_END) 获取文件大小）。</summary>
    public const int SeekEnd = 2;

    // ════════════════════════════════════════════════════════════
    // 区域：macOS fcntl(2) commands（libc）
    // ════════════════════════════════════════════════════════════

    /// <summary>F_NOCACHE = 48：不缓存文件数据（绕过页缓存的 hint，等价 Linux O_DIRECT 的弱化版）。</summary>
    public const int FNocache = 48;

    /// <summary>★ Spec 26：F_FULLFSYNC = 51：fsync + 强制设备刷缓存到永久存储（macOS 唯一的真落盘保证）。</summary>
    public const int FFullfsync = 51;

    // ════════════════════════════════════════════════════════════
    // 区域：macOS F_PREALLOCATE（文件预分配，fcntl 命令）
    // ════════════════════════════════════════════════════════════

    /// <summary>F_PREALLOCATE = 42：macOS 预分配磁盘存储（fcntl 命令，配合 fstore_t 结构体）。</summary>
    public const int FPreallocate = 42;

    /// <summary>F_ALLOCATECONTIG = 0x02：预分配连续磁盘空间（失败则降级 F_ALLOCATEALL 非连续）。</summary>
    public const int FAllocateContig = 0x02;

    /// <summary>F_ALLOCATEALL = 0x04：预分配全部请求空间（非连续，允许碎片）。</summary>
    public const int FAllocateAll = 0x04;

    /// <summary>F_PEOFPOSMODE = 1：fst_offset 相对文件物理 EOF（fstore_t.fst_posmode）。</summary>
    public const int FPeofposmode = 1;

    /// <summary>当前运行平台名称（日志/诊断用，避免业务层重复平台判断）。</summary>
    public static string PlatformName =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows"
        : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "Linux"
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macOS" : "Other";

    // ════════════════════════════════════════════════════════════
    // 区域：Unix 文件权限（open mode 参数）
    // ════════════════════════════════════════════════════════════

    /// <summary>Unix 文件权限 0644（owner 读写 / group 读 / other 读）。open(2) 的 mode 参数。</summary>
    public const int FileMode0644 = 0x1A4;

    // ════════════════════════════════════════════════════════════
    // 区域：Windows 令牌权限（advapi32）
    // ════════════════════════════════════════════════════════════

    /// <summary>TOKEN_QUERY = 0x0008。OpenProcessToken 的访问标志（查询令牌信息）。</summary>
    public const uint TokenQuery = 0x0008;

    /// <summary>TOKEN_ADJUST_PRIVILEGES = 0x0020。OpenProcessToken 的访问标志（调整权限）。</summary>
    public const uint TokenAdjustPrivileges = 0x0020;

    /// <summary>SE_PRIVILEGE_ENABLED = 0x00000002。TokenPrivileges 的 Attributes（启用权限）。</summary>
    public const uint SePrivilegeEnabled = 0x00000002;

    // ════════════════════════════════════════════════════════════
    // 区域：Windows 文件属性（CreateFile flagsAndAttributes）
    // ════════════════════════════════════════════════════════════

    /// <summary>FILE_ATTRIBUTE_NORMAL = 0x80。CreateFile 的默认文件属性（无特殊属性）。</summary>
    public const uint FileAttributeNormal = 0x80;

    // ════════════════════════════════════════════════════════════
    // 区域：Windows DeviceIoControl（USN 卷标记）
    // ════════════════════════════════════════════════════════════

    /// <summary>FSCTL_MARK_HANDLE_INFO 的设备类型参数（FILE_DEVICE_FILE_SYSTEM = 9）。</summary>
    public const uint FileDeviceFileSystem = 9;

    /// <summary>FSCTL_MARK_HANDLE_INFO 的功能码参数。</summary>
    public const uint FsctlMarkHandleInfoFunction = 63;

    /// <summary>FSCTL_SET_SPARSE 的功能码参数（function=49）。</summary>
    public const uint FsctlSetSparseFunction = 49;

    /// <summary>FSCTL_SET_ZERO_DATA 的功能码参数（function=50）。</summary>
    public const uint FsctlSetZeroDataFunction = 50;

    /// <summary>FSCTL_QUERY_ALLOCATED_RANGES 的功能码参数（function=51）。</summary>
    /// <para>★ 查询稀疏文件的物理 allocated 区间列表（Compact 用——查字节级空洞位置）。</para>
    public const uint FsctlQueryAllocatedRangesFunction = 51;

    // ════════════════════════════════════════════════════════════
    // 区域：Windows 卷信息标志（GetVolumeInformationW 的 lpFileSystemFlags）
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// FILE_FILE_COMPRESSION = 0x00000010。卷支持文件级压缩。
    /// <para>★ DirectIO 判定：压缩卷与 FILE_FLAG_NO_BUFFERING 不兼容（CreateFile 失败），
    ///   GetVolumeInformation 返回此标志 → 降级 DirectIoMode.Buffered。</para>
    /// </summary>
    public const uint FileFileCompression = 0x00000010;

    /// <summary>FILE_VOLUME_IS_COMPRESSED = 0x00008000。卷本身是压缩的（同上语义，降级 Buffered）。</summary>
    public const uint FileVolumeIsCompressed = 0x00008000;

    // ════════════════════════════════════════════════════════════
    // 区域：Linux 文件系统 magic（fstatfs.f_type，DirectIO 能力判定）
    // ════════════════════════════════════════════════════════════
    // 来源：include/uapi/linux/magic.h。判定 DirectIO 是否被文件系统静默吞。

    /// <summary>OVERLAYFS_SUPER_MAGIC = 0x794c7630。overlayfs（Docker/K8s 容器默认挂载）—— 静默吞 O_DIRECT。</summary>
    public const long OverlayfsSuperMagic = 0x794c7630;

    /// <summary>TMPFS_MAGIC = 0x01021994。tmpfs（内存文件系统）—— 现代内核 FS_RAM_BASED 静默吞 O_DIRECT。</summary>
    public const long TmpfsMagic = 0x01021994;

    /// <summary>RAMFS_MAGIC = 0x858458f6。ramfs（内存文件系统）—— 同 tmpfs 静默吞 O_DIRECT。</summary>
    public const long RamfsMagic = 0x858458f6;

    /// <summary>EXT4_SUPER_MAGIC = 0xef53。ext4 —— 真正支持 O_DIRECT（强制对齐）。</summary>
    public const long Ext4SuperMagic = 0xef53;

    /// <summary>XFS_SUPER_MAGIC = 0x58465342。xfs —— 真正支持 O_DIRECT（强制对齐）。</summary>
    public const long XfsSuperMagic = 0x58465342;

    /// <summary>BTRFS_SUPER_MAGIC = 0x9123683e。btrfs —— 自 2.6.31 起支持 O_DIRECT（强制对齐）。</summary>
    public const long BtrfsSuperMagic = 0x9123683e;
}

