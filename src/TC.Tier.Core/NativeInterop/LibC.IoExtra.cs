using System.Runtime.InteropServices;

namespace TC.Tier.Core.NativeInterop;

/// <summary>
/// LibC IO 扩展（Core/IO 专用新增 syscall：fdatasync / posix_fadvise / copy_file_range /
/// preadv / pwritev / flock / dup / F_OFD_SETLK）。
/// <para>★ Linux 专用符号仅在 OperatingSystem.IsLinux() 守卫下调用（Windows 构建懒解析不触发）。</para>
/// <para>★ 全部 [LibraryImport] 源生成器编译期 marshalling（NativeAOT 友好）。</para>
/// </summary>
internal static unsafe partial class LibC
{
    // ══ fdatasync / posix_fadvise ══

    /// <summary>
    /// fdatasync(2) - 仅刷数据不刷元数据（Linux 专用）。失败返回 -1（errno）。
    /// </summary>
    [LibraryImport(NativeLibraries.Libc, EntryPoint = "fdatasync", SetLastError = true)]
    internal static partial int Fdatasync(int fd);

    // ══ 块设备 ioctl（Raw 介质设备载体——BLKGETSIZE64 容量 / BLKSSZGET 逻辑扇区）══

    /// <summary>BLKGETSIZE64 — 设备容量（字节，u64 出参）。块设备 fstat.st_size 恒 0，容量唯一可靠来源。</summary>
    internal const ulong BlkGetSize64 = 0x80081272UL;   // _IOR(0x12, 114, size_t)

    /// <summary>BLKSSZGET — 逻辑块扇区大小（int 出参，字节）。DIO 对齐基准（512e vs 4Kn）。</summary>
    internal const ulong BlkSszGet = 0x1268UL;   // _IO(0x12, 104)

    /// <summary>BLKDISCARD — 区间丢弃提示（u64[2] 出参：start/len 字节——SSD TRIM / 释放块回收提示）。
    /// RM-05：优化非正确性（B1 零基纪律独立成立——失败仅损失寿命优化）。</summary>
    internal const ulong BlkDiscard = 0x127FUL;   // _IO(0x12, 119)

    /// <summary>ioctl(2)（Linux）——块设备几何查询（BLKGETSIZE64/BLKSSZGET）。返回 0 成功 / -1 失败。</summary>
    [LibraryImport(NativeLibraries.Libc, EntryPoint = "ioctl", SetLastError = true)]
    internal static partial int Ioctl(int fd, ulong cmd, void* arg);

    /// <summary>
    /// posix_fadvise(2) - 访问提示（预取/回收决策）。返回 0 成功（POSIX 不设 errno，返回错误码）。
    /// </summary>
    /// <param name="fd">文件描述符。</param>
    /// <param name="offset">区间起点。</param>
    /// <param name="len">区间长度（0=到 EOF）。</param>
    /// <param name="advice">POSIX_FADV_*（1=Random 2=Sequential 3=WillNeed 4=DontNeed）。</param>
    [LibraryImport(NativeLibraries.Libc, EntryPoint = "posix_fadvise", SetLastError = false)]
    internal static partial int PosixFadvise(int fd, long offset, long len, int advice);

    /// <summary>POSIX_FADV_NORMAL。</summary>
    internal const int PosixFadvNormal = 0;

    /// <summary>POSIX_FADV_RANDOM——禁用预读。</summary>
    internal const int PosixFadvRandom = 1;

    /// <summary>POSIX_FADV_SEQUENTIAL——激进预读。</summary>
    internal const int PosixFadvSequential = 2;

    /// <summary>POSIX_FADV_WILLNEED——预读入页缓存。</summary>
    internal const int PosixFadvWillNeed = 3;

    /// <summary>POSIX_FADV_DONTNEED——可回收页缓存。</summary>
    internal const int PosixFadvDontNeed = 4;

    // ══ copy_file_range（Linux）══

    /// <summary>
    /// copy_file_range(2) - 内核内文件间拷贝（Linux 4.5+；跨 fs 时等价用户态回退）。返回实际拷贝字节数，失败 -1。
    /// </summary>
    [LibraryImport(NativeLibraries.Libc, EntryPoint = "copy_file_range", SetLastError = true)]
    internal static partial nint CopyFileRange(
        int fdIn, long* offIn, int fdOut, long* offOut, nuint len, uint flags);

    // ══ preadv / pwritev（Linux 向量 IO）══

    /// <summary>iovec——preadv/pwritev 的缓冲描述（Linux LP64 布局：base 指针 + len）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct IoVec
    {
        public void* Base;
        public nuint Len;
    }

    /// <summary>preadv(2) - 向量读（readv + 显式偏移）。返回总读取字节数，失败 -1。</summary>
    [LibraryImport(NativeLibraries.Libc, EntryPoint = "preadv", SetLastError = true)]
    internal static partial nint Preadv(int fd, IoVec* iov, int iovcnt, long offset);

    /// <summary>pwritev(2) - 向量写（writev + 显式偏移）。返回总写入字节数，失败 -1。</summary>
    [LibraryImport(NativeLibraries.Libc, EntryPoint = "pwritev", SetLastError = true)]
    internal static partial nint Pwritev(int fd, IoVec* iov, int iovcnt, long offset);

    // ══ flock / dup ══

    /// <summary>flock(2) - 整文件 advisory 锁（macOS 唯一后端；Linux 卷锁后端）。返回 0 成功，-1 失败。</summary>
    [LibraryImport(NativeLibraries.Libc, EntryPoint = "flock", SetLastError = true)]
    internal static partial int Flock(int fd, int operation);

    /// <summary>LOCK_SH——共享。</summary>
    internal const int LockSh = 1;

    /// <summary>LOCK_EX——排他。</summary>
    internal const int LockEx = 2;

    /// <summary>LOCK_NB——非阻塞。</summary>
    internal const int LockNb = 4;

    /// <summary>LOCK_UN——释放。</summary>
    internal const int LockUn = 8;

    /// <summary>dup(2) - 复刻文件描述符（同一 open file description——OFD 锁/映射独立持有语义的基石）。失败 -1。</summary>
    [LibraryImport(NativeLibraries.Libc, EntryPoint = "dup", SetLastError = true)]
    internal static partial int Dup(int fd);

    // ══ F_OFD_SETLK（Linux 字节范围锁——避开 fcntl 进程级陷阱）══

    /// <summary>struct flock（Linux LP64 布局：32 字节）。★ <see cref="LPid"/> 在 OFD 命令下必须置 0。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct FLock
    {
        /// <summary>l_type：F_RDLCK=0 / F_WRLCK=1 / F_UNLCK=2。</summary>
        public short LType;

        /// <summary>l_whence：SEEK_SET=0（本层恒用绝对偏移）。</summary>
        public short LWhence;

        /// <summary>l_start：区间起点（绝对偏移）。</summary>
        public long LStart;

        /// <summary>l_len：区间长度（0=到 EOF；负数=回溯——本层不用）。</summary>
        public long LLen;

        /// <summary>l_pid：OFD 命令下必须置 0（非零在老内核返回 EINVAL；F_OFD_GETLK 会覆写）。</summary>
        public int LPid;
    }

    /// <summary>F_RDLCK——共享锁。</summary>
    internal const short FRdlck = 0;

    /// <summary>F_WRLCK——排他锁。</summary>
    internal const short FWrlck = 1;

    /// <summary>F_UNLCK——解锁。</summary>
    internal const short FUnlck = 2;

    /// <summary>F_OFD_GETLK(36)——探测（内核 ≥3.15；EINVAL=不支持）。</summary>
    internal const int FOfdGetlk = 36;

    /// <summary>F_OFD_SETLK(37)——非阻塞获取。</summary>
    internal const int FOfdSetlk = 37;

    /// <summary>F_OFD_SETLKW(38)——阻塞获取。</summary>
    internal const int FOfdSetlkw = 38;

    /// <summary>FICLONE(94)——macOS 整文件引用克隆（写时复制）。</summary>
    internal const int Ficlone = 94;

    /// <summary>
    /// FcntlFlock - fcntl(2) 传 ref FLock 的便捷入口（F_OFD_SETLK(W)/FICLONE 等）。
    /// </summary>
    [LibraryImport(NativeLibraries.Libc, EntryPoint = "fcntl", SetLastError = true)]
    internal static partial int FcntlFlock(int fd, int cmd, ref FLock arg);

    /// <summary>FcntlIntPtr - fcntl(2) 传整型参数的便捷入口（FICLONE 传源 fd 等）。</summary>
    [LibraryImport(NativeLibraries.Libc, EntryPoint = "fcntl", SetLastError = true)]
    internal static partial int FcntlIntPtr(int fd, int cmd, nint arg);

    // ══ mmap 族（Core/IO 磁盘映射的手工 Unix 路径）══

    /// <summary>PROT_READ。</summary>
    internal const int ProtRead = 0x1;

    /// <summary>PROT_WRITE。</summary>
    internal const int ProtWrite = 0x2;

    /// <summary>MAP_SHARED。</summary>
    internal const int MapShared = 0x01;

    /// <summary>MS_SYNC（同步刷回）。</summary>
    internal const int MsSync = 4;

    /// <summary>mmap(2) - 建立内存映射。失败返回 MAP_FAILED（-1）；offset 须页对齐。</summary>
    [LibraryImport(NativeLibraries.Libc, EntryPoint = "mmap", SetLastError = true)]
    internal static partial void* Mmap(void* addr, nuint length, int prot, int flags, int fd, long offset);

    /// <summary>munmap(2) - 解除映射。返回 0 成功。</summary>
    [LibraryImport(NativeLibraries.Libc, EntryPoint = "munmap", SetLastError = true)]
    internal static partial int Munmap(void* addr, nuint length);

    /// <summary>msync(2) - 脏页写回。返回 0 成功。</summary>
    [LibraryImport(NativeLibraries.Libc, EntryPoint = "msync", SetLastError = true)]
    internal static partial int Msync(void* addr, nuint length, int flags);
}
