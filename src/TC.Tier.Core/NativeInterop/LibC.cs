using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TC.Tier.Core.NativeInterop;

/// <summary>
/// Unix libc 原生互操作（open / fcntl / mlock / munlock）。
/// <para>全部 [LibraryImport] 源生成器编译期 marshalling（NativeAOT 友好）。</para>
/// <para>库名统一引用 <see cref="NativeLibraries.Libc"/>。</para>
/// </summary>
internal static unsafe partial class LibC
{
    /// <summary>★ O_DSYNC 可用性探测结果缓存。Linux 2.4.20+（2002 年）普遍可用,首次解析后缓存。</summary>
    private static int _oDsyncProbed; // 0=未探测,1=可用,2=不可用

    // ══ libc P/Invoke 声明（统一 [LibraryImport] + NativeLibraries.Libc 常量）══
    /// <summary>
    /// open(2) - 打开文件，返回文件描述符。
    /// </summary>
    /// <param name="pathname">文件路径</param>
    /// <param name="flags">打开标志</param>
    /// <param name="mode">文件模式</param>
    /// <returns>返回文件描述符</returns>
    [LibraryImport(NativeLibraries.Libc, EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int Open(string pathname, int flags, int mode);
    /// <summary>
    /// fcntl(2) - 操作文件描述符，执行各种控制命令。
    /// </summary>
    /// <param name="fd">文件描述符</param>
    /// <param name="cmd">控制命令</param>
    /// <param name="arg">命令参数</param>
    /// <returns>返回结果</returns>
    [LibraryImport(NativeLibraries.Libc, EntryPoint = "fcntl", SetLastError = true)]
    internal static partial int Fcntl(int fd, int cmd, nint arg);

    /// <summary>
    /// lseek(2) - 设置文件描述符的读写偏移。用于 lseek(fd, 0, SEEK_END) 获取文件大小。
    /// </summary>
    /// <param name="fd">文件描述符。</param>
    /// <param name="offset">相对偏移量（SEEK_END 模式下被忽略）。</param>
    /// <param name="whence">定位基准：SEEK_SET=0 / SEEK_CUR=1 / SEEK_END=2。</param>
    /// <returns>成功返回新的文件偏移（字节）；失败返回 -1（调用方查 errno）。</returns>
    [LibraryImport(NativeLibraries.Libc, EntryPoint = "lseek", SetLastError = true)]
    internal static partial long Lseek(int fd, long offset, int whence);

    [LibraryImport(NativeLibraries.Libc, EntryPoint = "fsync", SetLastError = true)]
    internal static partial int Fsync(int fd);

    /// <summary>
    /// mlock(2) - 锁定内存到物理内存，防止被交换到磁盘。
    /// </summary>
    /// <param name="addr">内存地址</param>
    /// <param name="len">内存长度</param>
    /// <returns>返回结果</returns>
    [LibraryImport(NativeLibraries.Libc, EntryPoint = "mlock", SetLastError = true)]
    internal static partial int MLock(void* addr, nuint len);

    /// <summary>
    /// munlock(2) - 解锁内存，允许被交换到磁盘。
    /// </summary>
    /// <param name="addr">内存地址</param>
    /// <param name="len">内存长度</param>
    /// <returns>返回结果</returns>
    [LibraryImport(NativeLibraries.Libc, EntryPoint = "munlock", SetLastError = true)]
    internal static partial int MunLock(void* addr, nuint len);

    /// <summary>
    /// fallocate(2) - 预分配文件磁盘块（Linux 专用）。
    /// <para>mode=0（默认）：从 offset 起分配 len 字节的真实磁盘块（不写零，<see cref="FALLOC_FL_KEEP_SIZE"/> 不置位则推进文件 EOF）。
    /// 这是真实物理分配（非稀疏文件），用于写性能优化（避免写时元数据扩展 + 碎片化）。</para>
    /// <para>支持 ext4/xfs/btrfs；tmpfs/overlayfs 返回 EINVAL（调用方降级处理）。</para>
    /// </summary>
    /// <param name="fd">文件描述符</param>
    /// <param name="mode">模式（0=分配并推进 EOF；<see cref="FALLOC_FL_KEEP_SIZE"/>=分配但保持 EOF）</param>
    /// <param name="offset">起始偏移</param>
    /// <param name="len">预分配长度</param>
    /// <returns>0 成功；-1 失败（errno 见 EINVAL=不支持/EOPNOTSUPP=文件系统不支持）</returns>
    [LibraryImport(NativeLibraries.Libc, EntryPoint = "fallocate", SetLastError = true)]
    internal static partial int Fallocate(int fd, int mode, long offset, long len);

    /// <summary>FALLOC_FL_KEEP_SIZE — 预分配但保持文件大小不变（用于在 EOF 内预分配空洞）。</summary>
    internal const int FALLOC_FL_KEEP_SIZE = 0x01;

    /// <summary>FALLOC_FL_PUNCH_HOLE — 释放区间磁盘块（打洞，区域变稀疏，读返回零）。须与 KEEP_SIZE 组合。</summary>
    internal const int FALLOC_FL_PUNCH_HOLE = 0x02;

    /// <summary>
    /// statvfs(2) 返回结构体（POSIX，glibc/Linux x64 布局）。
    /// <para>★ <see cref="FrSize"/>（f_frsize）是基本块大小，用于查 DIO 对齐扇区大小；
    ///   <see cref="FBsize"/>（f_bsize）是文件系统首选块大小，f_frsize 更接近物理扇区。</para>
    /// <para>★ 长度必须与 C struct statvfs 精确匹配（out 参数传递指针，长度不匹配导致内存破坏/AccessViolation）。</para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct StatvfsData
    {
        public ulong FBsize;    // f_bsize: 文件系统首选块大小
        public ulong FrSize;    // f_frsize: 基本块大小（DIO 对齐用）
        public ulong FBlocks;
        public ulong FBfree;
        public ulong FBavail;
        public ulong FFiles;
        public ulong FFfree;
        public ulong FFavail;
        public ulong FFsid;
        public ulong FFlag;     // f_flag: 挂载标志
        public ulong FNamemax;  // f_namemax: 最大文件名长度
        private ulong _spare0;  // __f_spare[0..1]
        private ulong _spare1;  // __f_spare[2..3]
        private ulong _spare2;  // __f_spare[4..5]
    }

    /// <summary>
    /// statvfs(2) — 查询文件系统信息（Linux/macOS 共享，POSIX 标准）。
    /// <para>用于查 <c>f_frsize</c>（基本块大小）作 DIO 对齐扇区大小的近似。</para>
    /// </summary>
    /// <param name="path">文件或目录路径（须已存在）。</param>
    /// <param name="sv">输出：文件系统信息结构体。</param>
    /// <returns>0 成功；-1 失败（errno 见 ENOENT=路径不存在）。</returns>
    [LibraryImport(NativeLibraries.Libc, EntryPoint = "statvfs", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int Statvfs(string path, out StatvfsData sv);

    // ══ Linux managed helpers（原 Linux.cs，保持公共 API）══


    /// <summary>
    /// 将 open 返回的文件描述符 fd 包装为 SafeFileHandle，确保在 SafeFileHandle 释放时关闭 fd。
    /// </summary>
    /// <param name="fd">文件描述符</param>
    /// <returns>返回 SafeFileHandle</returns>
    /// <exception cref="IOException">当文件描述符无效时抛出</exception>
    internal static SafeFileHandle WrapFileDescriptor(int fd)
    {
        return fd < 0 ? throw new IOException($"open() failed, errno={Marshal.GetLastPInvokeError()}") : new SafeFileHandle(fd, ownsHandle: true);
    }

   /// <summary>
   /// 在 Linux 上以 DirectIO 模式打开文件，返回 SafeFileHandle。
   /// </summary>
   /// <param name="path">文件路径</param>
   /// <param name="enableSync">是否启用同步标志</param>
   /// <returns>返回 SafeFileHandle</returns>
    internal static SafeFileHandle OpenDirectHandle(string path, bool enableSync = false)
    {
        var flags = NativeConstants.ORdwr | NativeConstants.OCreat | NativeConstants.ODirect;
        if (enableSync) flags |= ResolveSyncFlag();
        var fd = Open(path, flags, NativeConstants.FileMode0644);
        return WrapFileDescriptor(fd);
    }

    /// <summary>
    /// 解析 O_DSYNC 可用性，返回适合的同步标志（O_DSYNC 或 O_SYNC）。
    /// </summary>
    /// <returns>适合的同步标志（O_DSYNC 或 O_SYNC）</returns>
    private static int ResolveSyncFlag()
    {
        var probed = Interlocked.CompareExchange(ref _oDsyncProbed, 0, 0);
        switch (probed)
        {
            case 1:
                return NativeConstants.ODsync;
            case 2:
                return NativeConstants.OSync;
            default:
                Interlocked.Exchange(ref _oDsyncProbed, 1);
                return NativeConstants.ODsync;
        }
    }

    // ══ macOS managed helpers（原 macOS.cs，保持公共 API）══

    /// <summary>
    /// macOS 特有的 fcntl(fd, F_NOCACHE) —— 禁止文件缓存，避免占用系统页缓存。
    /// </summary>
    /// <param name="handle">文件句柄</param>
    /// <param name="logger">可选的日志记录器</param>
    internal static void TryEnableNoCache(SafeFileHandle handle, ILogger? logger = null)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var success = false;
        try
        {
            handle.DangerousAddRef(ref success);
            if (!success)
            {
                logger?.LogError("F_NOCACHE DangerousAddRef failed");
                return;
            }
            var raw = handle.DangerousGetHandle();
            if (raw == IntPtr.Zero || raw == new IntPtr(-1)) return;
            int fd = raw.ToInt32();
            if (fd < 0) return;
            int rc = Fcntl(fd, NativeConstants.FNocache, 1);
            if (rc != 0)
            {
                var err = Marshal.GetLastPInvokeError();
                logger?.LogError("device.macos", err, $"F_NOCACHE fcntl returned {rc}, errno={err}");
            }
        }
        catch (Exception ex)
        {
            logger?.LogError("device.macos", ex.HResult & 0xFFFF, $"F_NOCACHE exception: {ex.Message}");
        }
        finally
        {
            if (success) handle.DangerousRelease();
        }
    }

    /// <summary>
    /// macOS 特有的 fcntl(fd, F_FULLFSYNC) —— 强制文件系统刷盘，确保数据和元数据都落盘。
    /// </summary>
    /// <param name="handle">文件句柄</param>
    /// <param name="logger">可选的日志记录器</param>
    /// <returns>如果操作成功，返回 true；否则抛出 IOException</returns>
    /// <exception cref="IOException"></exception>
    internal static bool TryFullFsync(SafeFileHandle handle, ILogger? logger = null)
    {
        if (!OperatingSystem.IsMacOS()) return true;
        var success = false;
        try
        {
            handle.DangerousAddRef(ref success);
            if (!success)
            {
                logger?.LogError("F_FULLFSYNC DangerousAddRef failed");
                throw new IOException("F_FULLFSYNC failed: DangerousAddRef failed");
            }
            IntPtr raw = handle.DangerousGetHandle();
            if (raw == IntPtr.Zero || raw == new IntPtr(-1))
            {
                logger?.LogError("F_FULLFSYNC invalid handle");
                throw new IOException("F_FULLFSYNC failed: invalid handle");
            }
            int fd = raw.ToInt32();
            if (fd < 0)
            {
                logger?.LogError($"F_FULLFSYNC negative fd={fd}");
                throw new IOException($"F_FULLFSYNC failed: negative fd={fd}");
            }
            int rc = Fcntl(fd, NativeConstants.FFullfsync, 0);
            if (rc != 0)
            {
                int err = Marshal.GetLastPInvokeError();
                logger?.LogError("device.macos", err, $"F_FULLFSYNC fcntl returned {rc}, errno={err}");
                throw new IOException($"F_FULLFSYNC failed: errno={err}");
            }
            return true;
        }
        catch (IOException) { throw; }
        catch (Exception ex)
        {
            logger?.LogError("device.macos", ex.HResult & 0xFFFF, $"F_FULLFSYNC exception: {ex.Message}");
            throw new IOException("F_FULLFSYNC failed", ex);
        }
        finally
        {
            if (success) handle.DangerousRelease();
        }
    }

    /// <summary>
    /// fstore_t — macOS <c>fcntl(fd, F_PREALLOCATE, &amp;fstore)</c> 的参数结构体。
    /// <para>布局对齐 Apple &lt;sys/fcntl.h&gt;（fstore_t：flags + posmode + offset + length + bytesalloc）。</para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct FStore
    {
        public int Flags;            // F_ALLOCATECONTIG / F_ALLOCATEALL
        public int PosMode;          // F_PEOFPOSMODE=1（相对物理 EOF）
        public long Offset;          // 起始偏移（PosMode=EOF 时通常 0）
        public long Length;          // 预分配长度
        public long BytesAllocated;  // 输出：实际分配字节数
    }

    /// <summary>
    /// macOS 文件预分配：<c>fcntl(fd, F_PREALLOCATE, &amp;fstore)</c>。真实分配磁盘块（非稀疏）。
    /// <para>★ 两级尝试（Apple 官方推荐模式）：</para>
    /// <para>① <c>F_ALLOCATECONTIG</c>（连续分配，性能最优，但碎片化时易失败）</para>
    /// <para>② 失败则 <c>F_ALLOCATEALL</c>（非连续，允许碎片，成功率更高）</para>
    /// <para>★ 降级：连续 + 非连续都失败（网络盘 EINVAL / 文件系统不支持）→ 调用方降级 ftruncate（稀疏）。</para>
    /// <para>非 macOS 返回 false（调用方走 Linux/Win 路径）。</para>
    /// </summary>
    /// <param name="handle">文件安全句柄</param>
    /// <param name="size">预分配字节数</param>
    /// <param name="logger">可选日志</param>
    /// <returns>true=真实分配成功；false=需降级（调用方走稀疏 ftruncate）</returns>
    internal static bool TryPreallocate(SafeFileHandle handle, long size, ILogger? logger = null)
    {
        if (!OperatingSystem.IsMacOS()) return false;
        var success = false;
        try
        {
            handle.DangerousAddRef(ref success);
            if (!success)
            {
                logger?.LogError("F_PREALLOCATE DangerousAddRef failed");
                return false;
            }
            var raw = handle.DangerousGetHandle();
            if (raw == IntPtr.Zero || raw == new IntPtr(-1)) return false;
            int fd = raw.ToInt32();
            if (fd < 0) return false;

            // ① 连续分配（性能最优，碎片化时失败）
            var store = new FStore
            {
                Flags = NativeConstants.FAllocateContig,
                PosMode = NativeConstants.FPeofposmode,
                Offset = 0,
                Length = size
            };
            if (FcntlStore(fd, NativeConstants.FPreallocate, ref store) == 0 && store.BytesAllocated > 0)
                return true;

            // ② 非连续分配（允许碎片，成功率更高）
            store.Flags = NativeConstants.FAllocateAll;
            store.BytesAllocated = 0;
            if (FcntlStore(fd, NativeConstants.FPreallocate, ref store) == 0 && store.BytesAllocated > 0)
                return true;

            var err = Marshal.GetLastPInvokeError();
            logger?.LogWarning("F_PREALLOCATE failed (contiguous + non-contiguous), errno={Err}, caller should fall back to ftruncate", err);
            return false;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "F_PREALLOCATE threw, caller should fall back to ftruncate");
            return false;
        }
        finally
        {
            if (success) handle.DangerousRelease();
        }
    }

    /// <summary>fcntl(2) 带 ref struct 参数的 P/Invoke（F_PREALLOCATE 需传 fstore_t 引用）。</summary>
    [LibraryImport(NativeLibraries.Libc, EntryPoint = "fcntl", SetLastError = true)]
    private static partial int FcntlStore(int fd, int cmd, ref FStore arg);

    // ══ fstat / stat —— 真实磁盘占用（st_blocks * 512，区分稀疏/预分配空洞）══

    /// <summary>
    /// Linux x86-64 <c>struct stat</c>（glibc/bits/struct_stat.h 布局）。
    /// <para>★ 只需 <see cref="StBlocks"/>（真实分配块数，单位 512 字节）和 <see cref="StSize"/>（逻辑大小），
    ///   但必须精确匹配 C struct 全部字段顺序/类型，否则 fstat 写越界致 AccessViolation。</para>
    /// <para>★ <see cref="StBlocks"/> * 512 = 文件真实磁盘占用（POSIX 标准，与文件系统块大小无关）。
    ///   稀疏文件空洞区域不计入 st_blocks，故能区分预分配空洞。</para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct LinuxStat64
    {
        public ulong StDev;      // st_dev: 设备号
        public ulong StIno;      // st_ino: inode 号
        public ulong StNlink;    // st_nlink: 硬链接数
        public uint StMode;      // st_mode: 文件类型+权限
        public uint StUid;       // st_uid
        public uint StGid;       // st_gid
        public int _pad0;        // __pad0
        public ulong StRdev;     // st_rdev
        public long StSize;      // st_size: 逻辑大小（等同 FileInfo.Length）
        public long StBlksize;   // st_blksize: 文件系统首选块大小
        public long StBlocks;    // st_blocks: 真实分配块数（单位 512 字节，POSIX 标准）
        public LinuxTimespec StAtim;   // st_atim
        public LinuxTimespec StMtim;   // st_mtim
        public LinuxTimespec StCtim;   // st_ctim
        public long _unused0, _unused1, _unused2;  // __glibc_reserved
    }

    /// <summary>Linux <c>struct timespec</c>（fstat 输出内嵌）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct LinuxTimespec
    {
        public long TvSec;
        public long TvNsec;
    }

    /// <summary>
    /// fstat(2) — 查文件 inode 信息（Linux）。返回 <see cref="LinuxStat64"/>，
    /// 取 st_blocks * 512 得真实磁盘占用。
    /// </summary>
    [LibraryImport(NativeLibraries.Libc, EntryPoint = "fstat", SetLastError = true)]
    internal static partial int Fstat(int fd, out LinuxStat64 st);

    // ══ fstatfs — 文件系统类型探测（DirectIO 能力判定用）══

    /// <summary>Linux <c>struct __fsid_t</c>（fstatfs 输出内嵌，f_fsid）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct LinuxFsid
    {
        public int Val0;
        public int Val1;
    }

    /// <summary>
    /// Linux x86-64/ARM64 <c>struct statfs</c>（glibc &amp; musl 一致布局，64-bit 专用）。
    /// <para>★ 关键字段 <see cref="FType"/>（f_type）= 文件系统 magic，用于判定 DirectIO 能力：
    ///   overlayfs(0x794c7630)/tmpfs(0x01021994)/ramfs(0x858458f6) 静默吞 O_DIRECT flag → 走 page cache；
    ///   ext4(0xef53)/xfs(0x58465342)/btrfs(0x9123683e) 真正支持 O_DIRECT。</para>
    /// <para>★ 仅 x64/arm64 RID（本项目唯一支持平台），f_type 为 long（8B），布局稳定。
    ///   32-bit ABI 不同（statfs vs statfs64），本项目不涉及。</para>
    /// <para>★ 长度必须与 C struct 精确匹配（out 参数传指针，不匹配致 AccessViolation）。</para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct LinuxStatfs
    {
        public long FType;        // f_type: 文件系统 magic（DirectIO 判定核心字段）
        public long FBsize;       // f_bsize: 最优传输块大小
        public long FBlocks;      // f_blocks: 总数据块
        public long FBfree;       // f_bfree: 空闲块
        public long FBavail;      // f_bavail: 非特权用户可用块
        public long FFiles;       // f_files: 总文件节点
        public long FFfree;       // f_ffree: 空闲文件节点
        public LinuxFsid FFsid;   // f_fsid: 文件系统 ID
        public long FNameLen;     // f_namelen: 最大文件名长度
        public long FFrsize;      // f_frsize: 基本块大小
        public long FFlags;       // f_flags: 挂载标志
        public long FSpare0;      // f_spare[0..1]
        public long FSpare1;      // f_spare[2..3]
        public long FSpare2;      // f_spare[4..5]
        public long FSpare3;      // f_spare[6..7]
    }

    /// <summary>
    /// fstatfs(2)（Linux）— 查文件所在文件系统类型。取 <see cref="LinuxStatfs.FType"/> magic
    /// 判定 DirectIO 是否被静默吞（overlay/tmpfs 容器场景）。
    /// </summary>
    [LibraryImport(NativeLibraries.Libc, EntryPoint = "fstatfs", SetLastError = true)]
    internal static partial int Fstatfs(int fd, out LinuxStatfs buf);

    /// <summary>macOS &lt;sys/mount.h&gt; <c>fsid_t</c>（fstatfs 输出内嵌）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DarwinFsid
    {
        public int Val0;
        public int Val1;
    }

    /// <summary>
    /// macOS <c>struct statfs</c>（&lt;sys/mount.h&gt; 布局，与 Linux 完全不同）。
    /// <para>★ 关键字段 <see cref="FFstypename"/>（f_fstypename，16 字节 char 数组）= 文件系统类型名，
    ///   用于判定 F_NOCACHE hint 是否生效：apfs/hfs/msdos/udf 接受 hint（BestEffort）；
    ///   nfs/smbfs/autofs 忽略（Buffered）。</para>
    /// <para>★ macOS f_type 数值不稳定，必须匹配 f_fstypename 字符串（非 Linux 的 magic 数值）。</para>
    /// <para>★ MFSTYPENAMELEN=16, MNAMELEN=MAXPATHLEN(1024)。用 unsafe fixed byte 数组
    ///   （LibraryImport 源生成器不支持 string/byte[] 作 out 参数封送 struct，SYSLIB1051），
    ///   调用方用 <see cref="ReadDarwinFstypename"/> 把 fixed byte 数组解码为字符串。</para>
    /// <para>★ STORAGE-022 (#242)：字段顺序严格对照 XNU bsd/sys/mount.h 的
    ///   __DARWIN_STRUCT_STATFS64（LP64）。FFstypename 在 FOwner/FType/FFlags/FSSubtype **之后**，
    ///   而非之前——旧顺序读 FFstypename 偏移错位，读到 FSyncWrites 等字段的字节，得到乱码。</para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct DarwinStatfs
    {
        public uint FBsize;       // f_bsize: 基本块大小
        public int FIosize;       // f_iosize: 最优传输块大小（XNU: int32_t）
        public ulong FBlocks;     // f_blocks: 总数据块
        public ulong FBfree;      // f_bfree: 空闲块
        public ulong FBavail;     // f_bavail: 非特权用户可用块
        public ulong FFiles;      // f_files: 总文件节点
        public ulong FFfree;      // f_ffree: 空闲文件节点
        public DarwinFsid FFsid;  // f_fsid: 文件系统 ID（int[2] = 8B）
        public uint FOwner;       // f_owner: 挂载者 uid（uid_t = uint32_t）
        public uint FType;        // f_type: 文件系统类型（数值不稳定，用 FFstypename 判定）
        public uint FFlags;       // f_flags: 挂载标志
        public uint FSSubtype;    // f_fssubtype: 文件系统子类型
        public fixed byte FFstypename[16];   // f_fstypename[MFSTYPENAMELEN=16]: 文件系统类型名（NUL 结尾 ASCII）
        public fixed byte FMntonname[1024];    // f_mntonname[MAXPATHLEN=1024]: 挂载点路径
        public fixed byte FMntfromname[1024];  // f_mntfromname[MAXPATHLEN=1024]: 挂载源
        public uint FFlagsExt;    // f_flags_ext: 扩展挂载标志
        public fixed uint FReserved[7];   // f_reserved[7]: 保留（占位，保持总大小与 XNU 一致）
    }

    /// <summary>
    /// 把 <see cref="DarwinStatfs.FFstypename"/> 的 fixed byte 数组解码为字符串（取到首个 NUL）。
    /// </summary>
    internal static unsafe string ReadDarwinFstypename(ref DarwinStatfs s)
    {
        // fixed byte[] 无法直接转 string，逐字节读到首个 NUL
        var bytes = new byte[16];
        int len = 0;
        for (int i = 0; i < 16; i++)
        {
            byte b = s.FFstypename[i];  // fixed buffer 索引器需 unsafe 上下文
            if (b == 0) break;
            bytes[len++] = b;
        }
        return System.Text.Encoding.ASCII.GetString(bytes, 0, len);
    }

    /// <summary>
    /// fstatfs(2)（macOS）— 查文件所在文件系统类型。取 <see cref="DarwinStatfs.FFstypename"/> 字符串
    /// 判定 F_NOCACHE hint 是否生效。
    /// </summary>
    [LibraryImport(NativeLibraries.Libc, EntryPoint = "fstatfs", SetLastError = true)]
    internal static unsafe partial int FstatfsDarwin(int fd, out DarwinStatfs buf);

    // ══ xattr (extended attributes) — Linux 文件系统级元数据 ══

    internal const int XATTR_CREATE = 1;
    internal const int XATTR_REPLACE = 2;

    [LibraryImport(NativeLibraries.Libc, EntryPoint = "setxattr", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial long Setxattr(string path, string name, byte* value, ulong size, int flags);

    [LibraryImport(NativeLibraries.Libc, EntryPoint = "getxattr", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial long Getxattr(string path, string name, byte[]? value, ulong size);

    /// <summary>指针形态（span 调用方缓冲——零分配读取路径用；与 byte[] 重载同 EntryPoint）。</summary>
    [LibraryImport(NativeLibraries.Libc, EntryPoint = "getxattr", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static unsafe partial long Getxattr(string path, string name, byte* value, ulong size);

    [LibraryImport(NativeLibraries.Libc, EntryPoint = "removexattr", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int Removexattr(string path, string name);

    // ══ lseek SEEK_DATA/SEEK_HOLE — 查询稀疏文件的物理空洞位置 ══
    // 用于 Compact 搬迁时排除 PunchHole 打的洞（只搬有数据的区间）。
    // 语义：SEEK_DATA 找下一个有数据的偏移；SEEK_HOLE 找下一个空洞偏移。

    /// <summary>lseek whence：定位到下一个有数据的偏移（跳过空洞）。</summary>
    internal const int SEEK_DATA = 3;

    /// <summary>lseek whence：定位到下一个空洞的偏移（文件末尾视为空洞）。</summary>
    internal const int SEEK_HOLE = 4;
}
