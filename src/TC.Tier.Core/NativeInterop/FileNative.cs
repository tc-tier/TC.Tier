using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TC.Tier.Core.NativeInterop;
/// <summary>
/// 内核原生互操作封装。提供文件大小、预分配等原生 API 封装。
/// <para>★ 跨平台统一入口——所有 <see cref="SafeFileHandle"/> 平台 P/Invoke 收口于此，
///   避免 <c>ManagedLocalStorageDevice</c> 散落平台分支（改一处即全平台生效）。</para>
/// <para>★ <see langword="internal"/>——Core.IO 的实现底座，编译期封堵外部直调（外部用
///   <c>IFileSystem</c>/<c>IFileHandle</c>，见 docs/native-interop.md §0 映射表）。</para>
/// </summary>
internal static unsafe class FileNative
{
    /// <summary>
    /// 获取文件的物理大小（跨平台原生 API）。
    /// </summary>
    /// <param name="handle">文件安全句柄。</param>
    /// <param name="logger">可选日志记录器——原生 API 失败退回时记录警告。</param>
    /// <returns>文件物理大小（字节）。</returns>
    /// <remarks>
    /// ★ 平台分发：
    /// <para>- Windows: <see cref="Kernel32.GetFileSizeEx"/>（直接内核调用，读 MFT 元数据）。</para>
    /// <para>- Linux/macOS: <see cref="LibC.Lseek"/>(fd, 0, SEEK_END)（读 inode 元数据）。</para>
    /// <para>- 任意平台原生失败: 退回 <see cref="RandomAccess.GetLength"/>（托管封装，内部也是同族系统调用）。</para>
    /// </remarks>
    public static long GetFileSize(SafeFileHandle handle, ILogger? logger = null)
    {
        // === Windows: GetFileSizeEx ===
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (Kernel32.GetFileSizeEx(handle, out var size)) return size;
            // GetFileSizeEx 失败 → 记录后退回
            var err = Marshal.GetLastWin32Error();
            logger?.LogWarning("GetFileSizeEx failed (errno={Err}), falling back to RandomAccess.GetLength", err);
            return RandomAccess.GetLength(handle);
        }

        // === Linux/macOS: lseek(fd, 0, SEEK_END) ===
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var success = false;
            try
            {
                handle.DangerousAddRef(ref success);
                if (!success) goto Fallback;
                var fd = handle.DangerousGetHandle().ToInt32();
                if (fd < 0) goto Fallback;
                var size = LibC.Lseek(fd, 0, NativeConstants.SeekEnd);
                if (size >= 0) return size;
                // lseek 返回 -1 → 记录后退回
                var err = Marshal.GetLastPInvokeError();
                logger?.LogWarning("lseek(SEEK_END) failed (errno={Err}), falling back to RandomAccess.GetLength", err);
            }
            catch (Exception ex)
            {
                // P/Invoke 异常 → 记录后退回
                logger?.LogWarning(ex, "lseek(SEEK_END) threw, falling back to RandomAccess.GetLength");
            }
            finally
            {
                if (success) handle.DangerousRelease();
            }
        }

        // === 最终退回 ===
        Fallback:
        return RandomAccess.GetLength(handle);
    }

    /// <summary>
    /// ★ 预分配文件磁盘块（跨平台真实物理分配，非稀疏文件）。
    /// <para>★ 平台分发：</para>
    /// <para>- Windows: <see cref="Kernel32.SetFileSize"/>（SetFilePointerEx + SetEndOfFile + SetFileValidData）。
    ///   SetFileValidData 需 <c>SE_MANAGE_VOLUME_NAME</c> 特权（"执行卷维护任务"）；
    ///   无特权时失败 → 降级 <see cref="RandomAccess.SetLength"/>（SetEndOfFile，产生稀疏文件，非真实分配）。</para>
    /// <para>- Linux: <see cref="LibC.Fallocate"/>（mode=0，真实分配磁盘块，不写零）。
    ///   tmpfs/overlayfs 返回 EINVAL/EOPNOTSUPP → 降级 <see cref="RandomAccess.SetLength"/>（ftruncate，稀疏文件）。</para>
    /// <para>- macOS: <see cref="LibC.TryPreallocate"/>（fcntl F_PREALLOCATE，先 F_ALLOCATECONTIG 连续后 F_ALLOCATEALL 非连续）。
    ///   网络盘/不支持 → 降级 <see cref="RandomAccess.SetLength"/>（ftruncate，稀疏文件）。</para>
    /// <para>★ 返回值语义：</para>
    /// <para>- <see cref="PreallocateResult.RealAlloc"/>：真实物理分配成功（磁盘块已预留，写性能最优）</para>
    /// <para>- <see cref="PreallocateResult.SparseFallback"/>：降级为稀疏文件（逻辑大小已设，但未真实分配，写时按需分配）</para>
    /// <para>- <see cref="PreallocateResult.Failed"/>：完全失败（异常已记录，调用方应继续——预分配是 best-effort）</para>
    /// </summary>
    /// <param name="handle">已打开的文件安全句柄（须有写权限）。</param>
    /// <param name="size">预分配字节数（设为文件最终大小）。</param>
    /// <param name="logger">可选日志——降级/失败时记录警告。</param>
    /// <returns>预分配结果（见返回值语义）。</returns>
    public static PreallocateResult PreallocateFile(SafeFileHandle handle, long size, ILogger? logger = null)
    {
        if (size <= 0) return PreallocateResult.RealAlloc;  // 无需预分配

        // === Windows: SetFileSize（SetFileValidData，真实分配，需特权） ===
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                if (Kernel32.SetFileSize(handle, size))
                    return PreallocateResult.RealAlloc;
                var err = Marshal.GetLastWin32Error();
                // SetFileValidData 常见失败：ERROR_PRIVILEGE_NOT_HELD (1314) —— 无 SE_MANAGE_VOLUME_NAME
                logger?.LogWarning("SetFileValidData failed (errno={Err}, likely missing SE_MANAGE_VOLUME_NAME privilege), falling back to sparse", err);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "SetFileSize threw, falling back to sparse");
            }
            // 降级：RandomAccess.SetLength（SetEndOfFile，稀疏文件）
            return SparseFallback(handle, size, logger);
        }

        // === Linux: fallocate（真实分配磁盘块） ===
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var success = false;
            try
            {
                handle.DangerousAddRef(ref success);
                if (!success) goto Fallback;
                var fd = handle.DangerousGetHandle().ToInt32();
                if (fd < 0) goto Fallback;
                // mode=0：分配磁盘块并推进 EOF（非稀疏，真实物理分配）
                if (LibC.Fallocate(fd, mode: 0, offset: 0, len: size) == 0)
                    return PreallocateResult.RealAlloc;
                var err = Marshal.GetLastPInvokeError();
                // EINVAL (22)=tmpfs/overlayfs 不支持；EOPNOTSUPP (95)=文件系统不支持
                logger?.LogWarning("fallocate failed (errno={Err}, likely tmpfs/overlayfs), falling back to sparse", err);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "fallocate threw, falling back to sparse");
            }
            finally
            {
                if (success) handle.DangerousRelease();
            }
            return SparseFallback(handle, size, logger);
        }

        // === macOS: fcntl(F_PREALLOCATE, fstore_t) 真实分配（先连续后非连续）===
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            if (LibC.TryPreallocate(handle, size, logger))
                return PreallocateResult.RealAlloc;
            // F_PREALLOCATE 失败（网络盘 EINVAL / 不支持）→ 降级 ftruncate（稀疏）
            return SparseFallback(handle, size, logger);
        }

        // === 其他平台：无原生预分配，直接降级 ===
        Fallback:
        return SparseFallback(handle, size, logger);
    }

    /// <summary>降级：稀疏标记（Windows FSCTL_SET_SPARSE——SetLength 元数据化，免 NTFS 即时簇分配）
    /// + RandomAccess.SetLength（SetEndOfFile/ftruncate，产生稀疏文件）。</summary>
    internal static PreallocateResult SparseFallback(SafeFileHandle handle, long size, ILogger? logger)
    {
        // Windows：先标记稀疏再 SetLength——非稀疏 SetEndOfFile 扩展 = 即时簇分配（RM-41 同根因，IS-01）。
        // SetSparse 自身含非 Windows 守卫；失败仅降级（非稀疏 SetLength 仍正确，只是慢）。
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                if (!Kernel32.SetSparse(handle))
                    logger?.LogWarning("稀疏标记失败（FSCTL_SET_SPARSE），回退非稀疏 SetLength（NTFS 即时簇分配）");
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "稀疏标记异常，回退非稀疏 SetLength");
            }
        }
        try
        {
            RandomAccess.SetLength(handle, size);
            return PreallocateResult.SparseFallback;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Sparse fallback (RandomAccess.SetLength) also failed");
            return PreallocateResult.Failed;
        }
    }

    /// <summary>
    /// full 档物理占位保证：真实分配全部 <paramref name="size"/> 字节（创建时付成本——
    /// 与 <see cref="PreallocateFile"/> 的 best-effort 降级不同，本方法不允许静默降级为稀疏）。
    /// <para>Windows：<see cref="Kernel32.SetFileSize"/>（非稀疏 SetEndOfFile 即时分配簇 +
    ///   SetFileValidData 特权标记 valid）；SetFileValidData 无特权失败也接受——分配事实已达成。</para>
    /// <para>Linux：<c>fallocate(mode:0)</c>；macOS：F_PREALLOCATE；不支持/失败 → 分块零写物化。</para>
    /// </summary>
    /// <returns>物理占位是否达成（false = 调用方应 fail-fast——不允许静默降级）。</returns>
    public static bool EnsurePhysicalAllocation(SafeFileHandle handle, long size, ILogger? logger = null)
    {
        if (size <= 0) return true;

        // === Windows: SetFileSize（SetEndOfFile 即时分配 + SetFileValidData 特权标记 valid）===
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                if (Kernel32.SetFileSize(handle, size)) return true;
                var err = Marshal.GetLastWin32Error();
                logger?.LogWarning(
                    "SetFileValidData failed (errno={Err}, likely missing SE_MANAGE_VOLUME_NAME)——非稀疏 SetEndOfFile 已即时分配，full 档达成",
                    err);
                return true;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "SetFileSize threw——full 档物理占位失败");
                return false;
            }
        }

        // === Linux: fallocate（真实分配磁盘块）→ 失败零写物化 ===
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var success = false;
            try
            {
                handle.DangerousAddRef(ref success);
                if (!success) return false;
                var fd = handle.DangerousGetHandle().ToInt32();
                if (fd >= 0 && LibC.Fallocate(fd, mode: 0, offset: 0, len: size) == 0)
                    return true;
                logger?.LogWarning("fallocate failed (errno={Err})——full 档转零写物化", Marshal.GetLastPInvokeError());
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "fallocate threw——full 档转零写物化");
            }
            finally
            {
                if (success) handle.DangerousRelease();
            }
            return WriteZeroes(handle, size, logger);
        }

        // === macOS: F_PREALLOCATE（先连续后非连续）→ 失败零写物化 ===
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            if (LibC.TryPreallocate(handle, size, logger)) return true;
            logger?.LogWarning("F_PREALLOCATE failed——full 档转零写物化");
            return WriteZeroes(handle, size, logger);
        }

        // === 其他平台：零写物化 ===
        return WriteZeroes(handle, size, logger);
    }

    /// <summary>分块零写物化（full 档兜底——无原生预分配/不支持时的物理占位）。</summary>
    private static bool WriteZeroes(SafeFileHandle handle, long size, ILogger? logger)
    {
        try
        {
            var zero = new byte[1 << 20];
            long offset = 0;
            while (offset < size)
            {
                var chunk = (int)Math.Min(zero.Length, size - offset);
                RandomAccess.Write(handle, zero.AsSpan(0, chunk), offset);
                offset += chunk;
            }
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "零写物化失败——full 档物理占位失败");
            return false;
        }
    }

    /// <summary>
    /// ★ 刷盘到稳定存储（跨平台）。
    /// <para>Windows: <see cref="RandomAccess.FlushToDisk"/>（FlushFileBuffers）。</para>
    /// <para>Linux: <see cref="RandomAccess.FlushToDisk"/>（fsync）。</para>
    /// <para>macOS: <see cref="LibC.TryFullFsync"/>（F_FULLFSYNC，唯一真落盘保证；
    ///   <see cref="RandomAccess.FlushToDisk"/> 的 fsync() 在 macOS 不刷设备缓存，dotnet/runtime#28444）。</para>
    /// <para>★ 业务层调此方法，不判断平台。</para>
    /// </summary>
    /// <param name="handle">文件安全句柄。</param>
    /// <param name="logger">可选日志（macOS F_FULLFSYNC 失败时抛 IOException 含日志）。</param>
    public static void FlushToDisk(SafeFileHandle handle, ILogger? logger = null)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            LibC.TryFullFsync(handle, logger);  // macOS: F_FULLFSYNC（失败抛 IOException）
        else
            RandomAccess.FlushToDisk(handle);   // Win/Linux: FlushFileBuffers/fsync
    }

    public static void MoveFileDurably(string sourcePath, string destinationPath, bool overwrite)
    {
        if (OperatingSystem.IsWindows())
        {
            const uint moveFileReplaceExisting = 0x1;
            const uint moveFileWriteThrough = 0x8;
            var flags = moveFileWriteThrough | (overwrite ? moveFileReplaceExisting : 0);
            if (!Kernel32.MoveFileEx(sourcePath, destinationPath, flags))
            {
                var error = Marshal.GetLastWin32Error();
                throw new IOException(
                    $"MoveFileEx failed from '{sourcePath}' to '{destinationPath}', error={error}.");
            }
            return;
        }

        File.Move(sourcePath, destinationPath, overwrite);
        var sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(sourcePath))!;
        var destinationDirectory = Path.GetDirectoryName(Path.GetFullPath(destinationPath))!;
        FlushDirectory(sourceDirectory);
        if (!string.Equals(sourceDirectory, destinationDirectory, StringComparison.Ordinal))
            FlushDirectory(destinationDirectory);
    }

    public static void DeleteFileDurably(string path)
    {
        File.Delete(path);
        if (!OperatingSystem.IsWindows())
            FlushDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    }

    public static void FlushParentDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
            FlushDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    }

    private static void FlushDirectory(string directoryPath)
    {
        var fd = LibC.Open(directoryPath, flags: 0, mode: 0);
        using var handle = LibC.WrapFileDescriptor(fd);
        if (LibC.Fsync(fd) != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new IOException($"fsync directory '{directoryPath}' failed, errno={error}.");
        }
    }

    /// <summary>
    /// ★ WriteThrough 模式下是否无需显式刷盘（跨平台业务决策）。
    /// <para>Windows/Linux + WriteThrough：每次写已落盘（内核同步写），Flush 是 no-op。</para>
    /// <para>macOS + WriteThrough：无原生写透，仍需 F_FULLFSYNC 兜底。</para>
    /// </summary>
    public static bool WriteThroughImpliesFlushed =>
        !RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    // ════════════════════════════════════════════════════════════
    // 区域：跨平台 SafeFileHandle 打开
    // ════════════════════════════════════════════════════════════

    private static readonly FileOptions WindowsNoBuffering = (FileOptions)0x20000000;

    /// <summary>
    /// ★ 跨平台文件句柄打开——所有平台 SafeFileHandle 创建收口于此。
    /// 上层调用方提供业务参数（<paramref name="disableBuffering"/>、<paramref name="enableLinuxDirectIo"/>），
    /// 本方法自动转为平台特定机制——上层不再需要判断系统平台。
    /// </summary>
    /// <param name="path">文件路径。</param>
    /// <param name="mode">文件模式（Open/Create/OpenOrCreate 等）。</param>
    /// <param name="access">访问权限（Read/Write/ReadWrite）。</param>
    /// <param name="options">文件选项（Asynchronous/WriteThrough 等）——已由上层拼好。</param>
    /// <param name="share">文件共享模式。</param>
    /// <param name="disableBuffering">禁用文件缓冲。
    ///   <para>Windows: 添加 FILE_FLAG_NO_BUFFERING。</para>
    ///   <para>Linux: 若 <paramref name="enableLinuxDirectIo"/> 为 true，走 O_DIRECT P/Invoke；否则 buffered。</para>
    ///   <para>macOS: fcntl(F_NOCACHE) hint。</para>
    /// </param>
    /// <param name="enableLinuxDirectIo">Linux 专用：启用 O_DIRECT（须 <paramref name="disableBuffering"/> 为 true）。</param>
    /// <param name="logger">可选日志。</param>
    /// <returns>已打开的 SafeFileHandle（平台差异已处理）。</returns>
    public static SafeFileHandle OpenHandle(
        string path,
        FileMode mode,
        FileAccess access,
        FileOptions options,
        FileShare share,
        bool disableBuffering,
        bool enableLinuxDirectIo,
        ILogger? logger)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (disableBuffering)
                options |= WindowsNoBuffering;
            return File.OpenHandle(path, mode, access, share, options, preallocationSize: 0);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            if (disableBuffering && enableLinuxDirectIo)
            {
                if (access == FileAccess.Read)
                {
                    const int oRdOnly = 0x0;
                    int fd = LibC.Open(path, oRdOnly | NativeConstants.ODirect, 0);
                    return LibC.WrapFileDescriptor(fd);
                }
                bool enableSync = options.HasFlag(FileOptions.WriteThrough);
                return LibC.OpenDirectHandle(path, enableSync);
            }
            return File.OpenHandle(path, mode, access, share, options, preallocationSize: 0);
        }

        var handle = File.OpenHandle(path, mode, access, share, options, preallocationSize: 0);
        if (disableBuffering)
            LibC.TryEnableNoCache(handle, logger);
        return handle;
    }

    /// <summary>
    /// ★ 探测文件系统对 unbuffered I/O 的实际支持程度（真探测，含容器场景）。
    /// <para>★ 返回 NativeInterop internal 原始枚举 <see cref="UnbufferedIoSupport"/>（不引用上层
    ///   Device 层的公开 <c>DirectIoMode</c>，避免 NativeInterop→Device 反向依赖）。上层 Device 层
    ///   调用后用 <c>DirectIoModeMapping</c>.FromSupport 映射成公开 <c>DirectIoMode</c>。</para>
    /// <para>★ 真探测机制（全平台，覆盖容器 overlay/tmpfs 静默吞 flag 的陷阱）：</para>
    /// <para>- Windows: <see cref="ProbeWindowsUnbuffered"/> —— GetVolumeInformationW 查 FS name + 压缩标志，
    ///   NTFS/ReFS 非压缩 → Supported；网络重定向器/压缩卷 → Ignored。</para>
    /// <para>- Linux: <see cref="ProbeLinuxUnbuffered"/> —— fstatfs(fd) 读 f_type magic，
    ///   overlay(0x794c7630)/tmpfs(0x01021994)/ramfs(0x858458f6) → Ignored（★ 关键：open 成功但 flag 被静默吞）；
    ///   ext4/xfs/btrfs → Supported；未知 FS → BestEffort（保守）。</para>
    /// <para>- macOS: <see cref="ProbeDarwinUnbuffered"/> —— fstatfs(fd) 读 f_fstypename 字符串，
    ///   apfs/hfs/msdos/udf → BestEffort（F_NOCACHE hint）；nfs/smbfs/autofs → Ignored。</para>
    /// <para>★ 容器场景修复：旧实现只 open(O_DIRECT) try/catch 兜底，抓不住 overlay/tmpfs
    ///   （现代内核 FS_RAM_BASED：open 成功但 flag 被静默吞，走 page cache）→ 本方法用 f_type magic
    ///   判定，Docker/K8s 默认 overlay 挂载正确识别为 Ignored。</para>
    /// </summary>
    /// <param name="handle">已打开的文件句柄（用于 fstatfs/getfsstat；Windows 用路径）。</param>
    /// <param name="filePath">文件路径（Windows GetVolumeInformation 取卷根用）。</param>
    /// <param name="disableBuffering">上层是否请求禁用缓冲。</param>
    /// <param name="enableLinuxDirectIo">Linux 是否启用 O_DIRECT。</param>
    /// <param name="logger">可选日志——探测降级/失败时记录警告。</param>
    public static UnbufferedIoSupport ProbeUnbufferedIo(
        SafeFileHandle handle, string filePath, bool disableBuffering, bool enableLinuxDirectIo, ILogger? logger = null)
    {
        if (!disableBuffering) return UnbufferedIoSupport.NotRequested;

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return ProbeWindowsUnbuffered(filePath, logger);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return ProbeLinuxUnbuffered(handle, enableLinuxDirectIo, logger);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return ProbeDarwinUnbuffered(handle, logger);
        }
        catch (Exception ex)
        {
            // 探测本身异常（不应阻断业务）→ 保守降级 BestEffort，记录警告
            logger?.LogWarning(ex, "ProbeUnbufferedIo threw on {Path}, falling back to BestEffort", filePath);
            return UnbufferedIoSupport.BestEffort;
        }

        return UnbufferedIoSupport.BestEffort;  // 未知平台保守降级
    }

    /// <summary>
    /// Windows 探测：GetVolumeInformationW 查卷的 FS name + 压缩标志。
    /// <para>NTFS/ReFS 非压缩 → Supported（FILE_FLAG_NO_BUFFERING 实际生效）；
    /// 压缩卷（FILE_FILE_COMPRESSION/FILE_VOLUME_IS_COMPRESSED）→ Ignored（与 NO_BUFFERING 不兼容）；
    /// 网络重定向器/CscFS → Ignored（NO_BUFFERING 静默吞，走 page cache）。</para>
    /// <para>★ 已知限制：无 FILE_SUPPORTS_UNBUFFERED_IO 标志位，靠 FS name + 压缩标志推断。
    ///   微软文档对 UNC/SMB 行为故意模糊，本探测无法 100% 判定网络盘，保守归 Ignored。</para>
    /// </summary>
    private static UnbufferedIoSupport ProbeWindowsUnbuffered(string filePath, ILogger? logger)
    {
        // 取卷根路径（如 "C:\"），UNC 路径（\\server\share）取前两段
        string root;
        if (filePath.Length >= 2 && filePath[1] == ':')
            root = filePath.Substring(0, 2) + Path.DirectorySeparatorChar;
        else
            root = filePath;  // UNC 或其他——GetVolumeInformationW 自行解析

        // FS name 缓冲区（FILE_FS_NAME_BUFFER_SIZE 默认 256）
        const int FsNameBufSize = 256;
        IntPtr fsNameBuf = Marshal.AllocHGlobal(FsNameBufSize * 2);  // WCHAR
        try
        {
            if (!Kernel32.GetVolumeInformation(root,
                    IntPtr.Zero, 0,
                    out _, out _,
                    out uint fsFlags,
                    fsNameBuf, FsNameBufSize))
            {
                var err = Marshal.GetLastWin32Error();
                logger?.LogWarning("GetVolumeInformation failed (errno={Err}) on {Root}, falling back to BestEffort", err, root);
                return UnbufferedIoSupport.BestEffort;
            }

            // 压缩卷：与 NO_BUFFERING 不兼容 → Ignored
            // ★ 注意区分：FILE_FILE_COMPRESSION(0x10) 是"卷支持文件压缩"的能力位（所有 NTFS 都置），
            //   并不代表文件已压缩——单个文件压缩才与 NO_BUFFERING 冲突，卷级能力位不算。
            //   真正的拒绝信号是 FILE_VOLUME_IS_COMPRESSED(0x8000)（卷本身压缩，DoubleSpace 时代）。
            if ((fsFlags & NativeConstants.FileVolumeIsCompressed) != 0)
            {
                logger?.LogWarning("Volume {Root} is volume-compressed (flags=0x{Flags:X}), NO_BUFFERING incompatible → Ignored", root, fsFlags);
                return UnbufferedIoSupport.Ignored;
            }

            string fsName = Marshal.PtrToStringUni(fsNameBuf) ?? "";
            // NTFS/ReFS → 真正支持；网络重定向器（CscFS）/未知 → 保守 Ignored
            if (fsName.Equals("NTFS", StringComparison.OrdinalIgnoreCase) ||
                fsName.Equals("ReFS", StringComparison.OrdinalIgnoreCase))
                return UnbufferedIoSupport.Supported;

            logger?.LogWarning("Volume {Root} filesystem '{FsName}' not confirmed NO_BUFFERING-supported → Ignored", root, fsName);
            return UnbufferedIoSupport.Ignored;
        }
        finally
        {
            Marshal.FreeHGlobal(fsNameBuf);
        }
    }

    /// <summary>
    /// Linux 探测：fstatfs(fd) 读 f_type magic。
    /// <para>★ 关键修复：overlay/tmpfs/ramfs 在现代内核（FS_RAM_BASED）下 open(O_DIRECT) 成功但 flag 被静默吞，
    ///   旧的 try/catch 兜底抓不住 → 本方法用 f_type magic 判定，正确归 Ignored。</para>
    /// <para>ext4/xfs/btrfs → Supported；overlay/tmpfs/ramfs → Ignored；未知 FS → BestEffort（保守）。</para>
    /// </summary>
    private static UnbufferedIoSupport ProbeLinuxUnbuffered(SafeFileHandle handle, bool enableLinuxDirectIo, ILogger? logger)
    {
        // enableLinuxDirectIo=false 时，OpenHandle 走 buffered 路径（未加 O_DIRECT）→ NotRequested
        if (!enableLinuxDirectIo) return UnbufferedIoSupport.NotRequested;

        var success = false;
        try
        {
            handle.DangerousAddRef(ref success);
            if (!success) goto Fallback;
            int fd = handle.DangerousGetHandle().ToInt32();
            if (fd < 0) goto Fallback;

            if (LibC.Fstatfs(fd, out var s) != 0)
            {
                var err = Marshal.GetLastPInvokeError();
                logger?.LogWarning("fstatfs failed (errno={Err}), falling back to BestEffort", err);
                goto Fallback;
            }

            // 按文件系统 magic 判定
            return s.FType switch
            {
                NativeConstants.OverlayfsSuperMagic => UnbufferedIoSupport.Ignored,  // Docker/K8s 容器默认
                NativeConstants.TmpfsMagic => UnbufferedIoSupport.Ignored,           // 内存 FS
                NativeConstants.RamfsMagic => UnbufferedIoSupport.Ignored,           // 内存 FS
                NativeConstants.Ext4SuperMagic => UnbufferedIoSupport.Supported,
                NativeConstants.XfsSuperMagic => UnbufferedIoSupport.Supported,
                NativeConstants.BtrfsSuperMagic => UnbufferedIoSupport.Supported,
                _ => UnbufferedIoSupport.BestEffort,  // 未知 FS 保守降级
            };
        }
        finally
        {
            if (success) handle.DangerousRelease();
        }

        Fallback:
        return UnbufferedIoSupport.BestEffort;
    }

    /// <summary>
    /// macOS 探测：fstatfs(fd) 读 f_fstypename 字符串。
    /// <para>apfs/hfs/msdos/udf → BestEffort（F_NOCACHE hint，对齐非强制）；
    /// nfs/smbfs/autofs → Ignored（hint 无效）；未知 → BestEffort（保守）。</para>
    /// </summary>
    private static unsafe UnbufferedIoSupport ProbeDarwinUnbuffered(SafeFileHandle handle, ILogger? logger)
    {
        var success = false;
        try
        {
            handle.DangerousAddRef(ref success);
            if (!success) goto Fallback;
            int fd = handle.DangerousGetHandle().ToInt32();
            if (fd < 0) goto Fallback;

            if (LibC.FstatfsDarwin(fd, out var s) != 0)
            {
                var err = Marshal.GetLastPInvokeError();
                logger?.LogWarning("fstatfs(darwin) failed (errno={Err}), falling back to BestEffort", err);
                goto Fallback;
            }

            string fsType = LibC.ReadDarwinFstypename(ref s);
            // macOS F_NOCACHE 实际生效的本地 FS → BestEffort；网络/虚拟 FS → Ignored
            return fsType switch
            {
                "apfs" => UnbufferedIoSupport.BestEffort,
                "hfs" => UnbufferedIoSupport.BestEffort,
                "msdos" => UnbufferedIoSupport.BestEffort,
                "udf" => UnbufferedIoSupport.BestEffort,
                "nfs" => UnbufferedIoSupport.Ignored,
                "smbfs" => UnbufferedIoSupport.Ignored,
                "afpfs" => UnbufferedIoSupport.Ignored,
                "autofs" => UnbufferedIoSupport.Ignored,
                _ => UnbufferedIoSupport.BestEffort,  // 未知 FS 保守
            };
        }
        finally
        {
            if (success) handle.DangerousRelease();
        }

        Fallback:
        return UnbufferedIoSupport.BestEffort;
    }


    // ════════════════════════════════════════════════════════════
    // 区域：废弃 shim —— 仅供已废弃的 ManagedLocalStorageDevice 用，待其删除后移除
    // ════════════════════════════════════════════════════════════
    // ⚠️ 已知的层泄漏：本方法返回上层 Device.DirectIoMode，违反 NativeInterop→Device 单向依赖。
    // 仅因 ManagedLocalStorageDevice（已废弃，底层重写中）仍在用而保留——严禁新代码调用。
    // 新代码用 ProbeUnbufferedIo（返回 NativeInterop internal UnbufferedIoSupport，无层泄漏）+
    // DirectIoModeMapping.FromSupport 映射。




    /// <summary>
    /// ★ 获取文件【真实磁盘分配字节】（区分稀疏/预分配空洞，对应 du/磁盘占用空间）。
    /// <para>★ 与 <see cref="GetFileSize"/> 的区别：GetFileSize 返回逻辑大小（含预分配空洞，等同 FileInfo.Length）；
    ///   本方法返回文件系统**实际分配的磁盘块总字节**，稀疏/预分配空洞不计入。</para>
    /// <para>★ 用途：冷启动恢复时区分"预分配了但未写满"的段——避免把预分配 1GB 误报为已写 1GB。</para>
    /// <para>★ 平台分发：</para>
    /// <para>- Windows: <c>Kernel32.GetFileInformationByHandleEx</c>(FileStandardInfo) →
    ///   <see cref="FileStandardInfo.AllocatedSize"/>（分配簇总字节，最精准）。</para>
    /// <para>- Linux: <see cref="LibC.Fstat"/> → st_blocks * 512（POSIX 标准，单位固定 512 字节）。</para>
    /// <para>- macOS: fstat 布局与 Linux 不同，降级到 <see cref="GetFileSize"/>（逻辑大小作下界，偏保守）。</para>
    /// <para>- 任意平台原生失败：降级到 <see cref="GetFileSize"/>（逻辑大小作下界）。</para>
    /// <para>★ <b>已知限制（预分配场景，跨平台）</b>：<see cref="PreallocateFile"/> 的语义是"真实分配磁盘块"
    ///   （Windows SetFileValidData/SetEndOfFile、Linux fallocate mode=0 都是真实分配）。预分配生效后，
    ///   AllocatedSize/st_blocks = 预分配大小，<b>无法区分"预分配的块"与"已写入的块"</b>——这是预分配机制
    ///   的固有特性，非 API 用错。故<b>预分配段的真实写入量必须由上层 hint 提供</b>
    ///   （上层 Initialize 时传入的写入水位）。未预分配的普通段，AllocatedSize = 真实写入量，正确。</para>
    /// <para>★ <b>降级语义</b>：失败时返回逻辑大小（偏保守，可能把预分配空洞算进去）。调用方据此判断
    ///   "无法 100% 正确"——上层若有精确的写入水位（如 flushedUntilAddress），应优先用上层 hint。</para>
    /// </summary>
    /// <param name="handle">文件安全句柄。</param>
    /// <param name="logger">可选日志——原生失败降级时记录警告。</param>
    /// <returns>真实磁盘分配字节（成功）；逻辑大小（降级，偏保守下界）。</returns>
    public static long GetFileAllocatedDiskSize(SafeFileHandle handle, ILogger? logger = null)
    {
        // === Windows: GetFileInformationByHandleEx(FileStandardInfo) → AllocatedSize ===
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (Kernel32.GetFileInformationByHandleEx(handle, FileInfoByHandleClass.FileStandardInfo,
                    out FileStandardInfo std, (uint)System.Runtime.InteropServices.Marshal.SizeOf<FileStandardInfo>()))
            {
                return std.AllocatedSize;
            }
            var err = Marshal.GetLastWin32Error();
            logger?.LogWarning("GetFileInformationByHandleEx(FileStandardInfo) failed (errno={Err}), falling back to logical size", err);
            return GetFileSize(handle, logger);  // 降级：逻辑大小作下界
        }

        // === Linux: fstat → st_blocks * 512 ===
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var success = false;
            try
            {
                handle.DangerousAddRef(ref success);
                if (!success) goto Fallback;
                var fd = handle.DangerousGetHandle().ToInt32();
                if (fd < 0) goto Fallback;
                if (LibC.Fstat(fd, out var st) == 0)
                    return st.StBlocks * 512;  // POSIX: st_blocks 单位固定 512 字节
                var err = Marshal.GetLastPInvokeError();
                logger?.LogWarning("fstat failed (errno={Err}), falling back to logical size", err);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "fstat threw, falling back to logical size");
            }
            finally
            {
                if (success) handle.DangerousRelease();
            }
        }

        // === macOS / 其它：降级到逻辑大小（macOS stat struct 布局不同，暂保守降级）===
        Fallback:
        return GetFileSize(handle, logger);
    }

    /// <summary>
    /// ★ 枚举文件中已分配（有数据）的物理区间列表——Compact 搬迁时排除 PunchHole 空洞用。
    /// <para>★ 平台分发：</para>
    /// <para>- Linux: lseek(SEEK_DATA/SEEK_HOLE) 交替迭代</para>
    /// <para>- Windows: DeviceIoControl(FSCTL_QUERY_ALLOCATED_RANGES)</para>
    /// <para>- macOS / 兜底: 返回 [(0, fileSize)]（视为全部 allocated，无空洞信息）</para>
    /// <para>★ 不在列表内的区域 = 空洞（sparse，磁盘块已归还 FS）。</para>
    /// </summary>
    /// <param name="handle">文件安全句柄。</param>
    /// <param name="fileSize">文件逻辑大小（FileInfo.Length），用于界定枚举范围。</param>
    /// <param name="logger">可选日志。</param>
    /// <returns>已分配区间列表 [(start, end), ...]；降级时返回单个 (0, fileSize)。</returns>
    public static List<(long Start, long End)> EnumerateAllocatedRanges(
        SafeFileHandle handle, long fileSize, ILogger? logger = null)
    {
        // === Linux: lseek(SEEK_DATA / SEEK_HOLE) ===
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return EnumerateAllocatedRangesLinux(handle, fileSize, logger);

        // === Windows: FSCTL_QUERY_ALLOCATED_RANGES ===
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return EnumerateAllocatedRangesWindows(handle, fileSize, logger);

        // === macOS / 兜底：视为全部 allocated ===
        return new List<(long, long)> { (0, fileSize) };
    }

    /// <summary>
    /// Linux 实现——lseek(SEEK_DATA) + lseek(SEEK_HOLE) 交替迭代。
    /// <para>★ 语义：SEEK_DATA 找下一个有数据的偏移；SEEK_HOLE 找下一个空洞偏移。</para>
    /// <para>★ 文件末尾视为空洞（lseek 返回 fileSize 或 ENXUP）。</para>
    /// </summary>
    private static List<(long Start, long End)> EnumerateAllocatedRangesLinux(
        SafeFileHandle handle, long fileSize, ILogger? logger)
    {
        var ranges = new List<(long, long)>();
        var success = false;
        try
        {
            handle.DangerousAddRef(ref success);
            if (!success) goto Fallback;
            int fd = handle.DangerousGetHandle().ToInt32();
            if (fd < 0) goto Fallback;

            long pos = 0;
            while (pos < fileSize)
            {
                // lseek(SEEK_DATA, pos)——找下一个有数据的偏移（跳过 pos 处的空洞）
                long dataStart = LibC.Lseek(fd, pos, LibC.SEEK_DATA);
                if (dataStart == -1)
                {
                    // ENXIO = 后面没数据了
                    if (Marshal.GetLastPInvokeError() == 6) break;
                    logger?.LogWarning("lseek(SEEK_DATA, {Pos}) failed (errno={Err}), aborting enumeration", pos, Marshal.GetLastPInvokeError());
                    goto Fallback;
                }
                if (dataStart >= fileSize) break;

                // lseek(SEEK_HOLE, dataStart)——找下一个空洞（数据区间终点）
                long holeStart = LibC.Lseek(fd, dataStart, LibC.SEEK_HOLE);
                if (holeStart == -1)
                {
                    logger?.LogWarning("lseek(SEEK_HOLE, {DataStart}) failed (errno={Err}), treating rest as data", dataStart, Marshal.GetLastPInvokeError());
                    ranges.Add((dataStart, fileSize));
                    break;
                }
                long dataEnd = Math.Min(holeStart, fileSize);
                ranges.Add((dataStart, dataEnd));
                pos = dataEnd;
            }
            return ranges;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "EnumerateAllocatedRangesLinux threw, falling back");
            goto Fallback;
        }
        finally
        {
            if (success) handle.DangerousRelease();
        }

    Fallback:
        return new List<(long, long)> { (0, fileSize) };
    }

    /// <summary>
    /// Windows 实现——FSCTL_QUERY_ALLOCATED_RANGES 查询 allocated 区间。
    /// <para>★ 返回 QUERY_ALLOCATED_RANGES 数组（每条 16B = offset 8B + length 8B）。</para>
    /// </summary>
    private static unsafe List<(long Start, long End)> EnumerateAllocatedRangesWindows(
        SafeFileHandle handle, long fileSize, ILogger? logger)
    {
        // FSCTL_QUERY_ALLOCATED_RANGES = CTL_CODE(FILE_DEVICE_FILE_SYSTEM=9, function=51,
        //     METHOD_NEITHER=3, FILE_READ_ACCESS=1)
        uint fsctlQueryAllocatedRanges = Kernel32.CtlCode(
            NativeConstants.FileDeviceFileSystem,
            NativeConstants.FsctlQueryAllocatedRangesFunction, 3, 1);

        // 输入参数：查询范围 [0, fileSize)，struct = { long offset; long length; }
        var queryRange = stackalloc byte[16];
        Marshal.WriteInt64((IntPtr)queryRange, 0, 0);
        Marshal.WriteInt64((IntPtr)queryRange, 8, fileSize);

        // 输出缓冲区：初始 4KB（256 条 × 16B），不够就扩容重试
        int bufSize = 4096;
        byte[] buf = new byte[bufSize];
        uint bytesReturned = 0;

        bool ok;
        fixed (byte* pBuf = buf)
        {
            ok = Kernel32.DeviceIoControlQueryAllocatedRanges(
                handle, fsctlQueryAllocatedRanges,
                queryRange, 16, pBuf, bufSize, ref bytesReturned, IntPtr.Zero);
        }

        if (!ok)
        {
            int err = Marshal.GetLastWin32Error();
            // ERROR_MORE_DATA (234) = 缓冲区不够，需要扩容重试
            if (err != 234)
            {
                logger?.LogWarning("FSCTL_QUERY_ALLOCATED_RANGES failed (errno={Err}), falling back to full allocated", err);
                return new List<(long, long)> { (0, fileSize) };
            }
            // 扩容到 64KB 重试（够 4096 条区间）
            bufSize = 65536;
            buf = new byte[bufSize];
            bytesReturned = 0;
            fixed (byte* pBuf = buf)
            {
                ok = Kernel32.DeviceIoControlQueryAllocatedRanges(
                    handle, fsctlQueryAllocatedRanges,
                    queryRange, 16, pBuf, bufSize, ref bytesReturned, IntPtr.Zero);
            }
            if (!ok)
            {
                logger?.LogWarning("FSCTL_QUERY_ALLOCATED_RANGES retry failed (errno={Err}), falling back", Marshal.GetLastWin32Error());
                return new List<(long, long)> { (0, fileSize) };
            }
        }

        // 解析输出：每条 16B = offset(long) + length(long)
        var ranges = new List<(long, long)>();
        int recordCount = (int)(bytesReturned / 16);
        for (int i = 0; i < recordCount; i++)
        {
            long offset = BitConverter.ToInt64(buf, i * 16);
            long length = BitConverter.ToInt64(buf, i * 16 + 8);
            ranges.Add((offset, offset + length));
        }

        return ranges;
    }

    /// <summary>
    /// ★ 释放文件区间磁盘块（跨平台打洞 PunchHole）。
    /// <para>★ 语义：把 [offset, offset+length) 区间的磁盘块归还文件系统，区域变稀疏，
    ///   读返回零，文件大小（FileInfo.Length）不变（KEEP_SIZE）。回收状态存于文件系统，
    ///   扫盘重建时自然保留（§5.2 maxOffset 推理链依赖此不变量）。</para>
    /// <para>★ 平台分发：</para>
    /// <para>- Linux: <see cref="LibC.Fallocate"/>(FALLOC_FL_PUNCH_HOLE | KEEP_SIZE)。</para>
    /// <para>- Windows: <see cref="Kernel32.SetSparse"/> + <see cref="Kernel32.SetZeroData"/>（FSCTL_SET_ZERO_DATA）。</para>
    /// <para>- macOS: fcntl(F_PUNCHHOLE)（TODO 暂未实现，返回 Unsupported）。</para>
    /// <para>- tmpfs/裸盘/不支持: 退化为 ZeroFilled（memset 归零，无磁盘块归还）或 Unsupported。</para>
    /// </summary>
    public static PunchResult PunchHole(SafeFileHandle handle, long offset, long length, ILogger? logger = null)
    {
        if (length <= 0) return PunchResult.Punched;

        // === Linux: fallocate(PUNCH_HOLE | KEEP_SIZE) ===
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var success = false;
            try
            {
                handle.DangerousAddRef(ref success);
                if (!success) goto ZeroFallback;
                var fd = handle.DangerousGetHandle().ToInt32();
                if (fd < 0) goto ZeroFallback;
                var mode = LibC.FALLOC_FL_PUNCH_HOLE | LibC.FALLOC_FL_KEEP_SIZE;  // 0x03
                if (LibC.Fallocate(fd, mode, offset, length) == 0)
                    return PunchResult.Punched;
                var err = Marshal.GetLastPInvokeError();
                // EINVAL (22)=tmpfs/overlayfs 不支持；EOPNOTSUPP (95)=文件系统不支持
                logger?.LogWarning("fallocate(PUNCH_HOLE) failed (errno={Err}, likely tmpfs/overlayfs), zero-filling instead", err);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "fallocate(PUNCH_HOLE) threw, zero-filling instead");
            }
            finally
            {
                if (success) handle.DangerousRelease();
            }
            goto ZeroFallback;
        }

        // === Windows: FSCTL_SET_SPARSE + FSCTL_SET_ZERO_DATA ===
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                // 1. 先设稀疏（已是稀疏则 no-op）
                if (!Kernel32.SetSparse(handle))
                {
                    var err = Marshal.GetLastWin32Error();
                    logger?.LogWarning("FSCTL_SET_SPARSE failed (errno={Err}), zero-filling instead", err);
                    goto ZeroFallback;
                }
                // 2. 打洞清零
                if (Kernel32.SetZeroData(handle, offset, length))
                    return PunchResult.Punched;
                var err2 = Marshal.GetLastWin32Error();
                logger?.LogWarning("FSCTL_SET_ZERO_DATA failed (errno={Err}), zero-filling instead", err2);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Windows PunchHole threw, zero-filling instead");
            }
            goto ZeroFallback;
        }

        // === macOS: fcntl(F_PUNCHHOLE) — TODO 暂未实现 ===
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            logger?.LogWarning("macOS F_PUNCHHOLE not yet implemented, zero-filling instead");
            goto ZeroFallback;
        }

        // === 其他平台：不支持 ===
        logger?.LogWarning("PunchHole not supported on this platform, zero-filling instead");
        goto ZeroFallback;

        // === 退化：memset 归零（语义正确，但未归还磁盘块）===
        ZeroFallback:
        try
        {
            ZeroFill(handle, offset, length, logger);
            return PunchResult.ZeroFilled;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Zero-fill fallback also failed");
            return PunchResult.Failed;
        }
    }

    /// <summary>退化路径：用普通写入把区间填零（语义正确，未归还磁盘块）。</summary>
    private static void ZeroFill(SafeFileHandle handle, long offset, long length, ILogger? logger)
    {
        // 分块写零（避免大 buffer 分配）
        const int chunkSize = 64 * 1024;
        var chunk = new byte[Math.Min(length, chunkSize)];  // 默认全零
        long written = 0;
        while (written < length)
        {
            var toWrite = (int)Math.Min(length - written, chunkSize);
            RandomAccess.Write(handle, chunk.AsSpan(0, toWrite), offset + written);
            written += toWrite;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  文件元数据 (xattr / ADS) — best-effort
    // ═══════════════════════════════════════════════════════════════

    // ★ fs 元数据平面主键（评审决议 统一命名 TC_TIER——与 IFileSystem.MetadataName 同值，
    //   层级方向不反向引用，契约测试断言防漂移）。fs 级（CreateFile/Stat）与句柄级 xattr 同一逻辑键——两平面互见。
    //   旧值 "TC_METAP" 注：引擎侧仍以字符串字面量消费（EngineMeta 段元数据，经泛型按名通道）——与本主键独立并存，引擎迁移后退役。
    //   ⚠️ Linux/macOS 的 xattr 名必须带命名空间前缀（user.）——裸名被内核拒绝（EOPNOTSUPP），
    //   由 ToSystemXattrName 在 LibC 调用边界补前缀（实际落盘 user.TC_TIER）；Windows ADS 流名 TC_TIER。
    internal const string XattrName = "TC_TIER";
    private const string XattrNamespacePrefix = "user.";
    private const uint AdsCreateAlways = 2;          // CREATE_ALWAYS
    private const uint AdsOpenExisting = 3;          // OPEN_EXISTING

    /// <summary>Linux/macOS xattr 名字补命名空间前缀（user.）——已带前缀则原样返回。
    /// Windows ADS 无命名空间概念，不走本方法。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string ToSystemXattrName(string attName)
        => attName.StartsWith(XattrNamespacePrefix, StringComparison.Ordinal) ? attName : XattrNamespacePrefix + attName;

    /// <summary>
    /// Write metadata via xattr (Linux/macOS) or ADS (Windows)——best-effort。
    /// <para>★ Windows 原生实现：CreateFile(filePath:tc_meta, GENERIC_WRITE, CREATE_ALWAYS) + WriteFile，
    ///   不走托管 File.WriteAllBytes（与 NativeInterop 收口原则一致）。</para>
    /// </summary>
    /// <param name="filePath">宿主文件路径（ADS 附加到此文件）。</param>
    /// <param name="data">元数据字节。</param>
    /// <param name="attName">属性名称（默认 <c>XattrName</c>）。</param>
    /// <param name="logger">可选日志——写入失败时记录警告。</param>
    /// <returns>true = 原生写入成功。</returns>
    public static bool WriteFileMeta(string filePath, ReadOnlySpan<byte> data, string attName = XattrName, ILogger? logger = null)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            try
            {
                fixed (byte* ptr = data)
                {
                    return LibC.Setxattr(filePath, ToSystemXattrName(attName), ptr, (ulong)data.Length, 0) == 0;
                }
            }
            catch (Exception ex) { logger?.LogWarning(ex, "Setxattr failed on {Path}", filePath); return false; }
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var adsPath = filePath + ":" + attName;
            SafeFileHandle? h = null;
            try
            {
                h = Kernel32.CreateFile(adsPath, NativeConstants.GenericWrite, 0,
                    IntPtr.Zero, AdsCreateAlways, NativeConstants.FileAttributeNormal, IntPtr.Zero);
                if (h.IsInvalid)
                {
                    var err = Marshal.GetLastWin32Error();
                    logger?.LogWarning("ADS CreateFile(GENERIC_WRITE) failed (errno={Err}) on {Path}", err, adsPath);
                    return false;
                }
                fixed (byte* ptr = data)
                {
                    if (!Kernel32.WriteFile(h, (IntPtr)ptr, (uint)data.Length, out uint written, null) || written != data.Length)
                    {
                        var err = Marshal.GetLastWin32Error();
                        logger?.LogWarning("ADS WriteFile failed/incomplete (errno={Err}, written={Written}/{Total}) on {Path}",
                            err, written, data.Length, adsPath);
                        return false;
                    }
                }
                // ★ CreateAlways 重写 ADS 后立即读可能因缓存未刷读到 null——FlushFileBuffers 强制持久化
                try { RandomAccess.FlushToDisk(h); }
                catch (Exception fex) { logger?.LogWarning(fex, "ADS FlushToDisk failed on {Path}", adsPath); }
                return true;
            }
            catch (Exception ex) { logger?.LogWarning(ex, "ADS write threw on {Path}", adsPath); return false; }
            finally { h?.Dispose(); }
        }

        return false;
    }

    /// <summary>
    /// ★ span 形态元数据读取（零分配——调用方缓冲；2K 元数据上界场景 stackalloc 直配）。
    /// <para>两通道天然支持调用方缓冲：Linux/macOS = getxattr 两段式（size 探测 → 拷入 destination）；
    /// Windows = ADS GetFileSizeEx + ReadFile 入 destination。</para>
    /// </summary>
    /// <param name="filePath">宿主文件路径（ADS 附加到此文件）。</param>
    /// <param name="destination">调用方提供的目标缓冲（零分配读取）。</param>
    /// <param name="attName">属性名称（默认 <c>XattrName</c>）。</param>
    /// <param name="logger">可选日志——读取失败时记录警告。</param>
    /// <returns>有效长度（&gt;0 成功）；0 = 无元数据/通道缺失；-1 = 值超 destination 或读取失败。</returns>
    public static int ReadFileMeta(string filePath, Span<byte> destination, string attName, ILogger? logger = null)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            try
            {
                var xname = ToSystemXattrName(attName);
                var size = LibC.Getxattr(filePath, xname, (byte[]?)null, 0);
                if (size <= 0) return 0;
                if (size > destination.Length) return -1;
                unsafe
                {
                    fixed (byte* ptr = destination)
                    {
                        var actual = LibC.Getxattr(filePath, xname, ptr, (ulong)size);
                        return actual == size ? (int)size : -1;
                    }
                }
            }
            catch (Exception ex) { logger?.LogWarning(ex, "Getxattr(span) failed on {Path}", filePath); return -1; }
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var adsPath = filePath + ":" + attName;
            SafeFileHandle? h = null;
            try
            {
                h = Kernel32.CreateFile(adsPath, NativeConstants.GenericRead, 0,
                    IntPtr.Zero, AdsOpenExisting, NativeConstants.FileAttributeNormal, IntPtr.Zero);
                if (h.IsInvalid)
                {
                    var err = Marshal.GetLastWin32Error();
                    if (err is not (2 or 3))   // 2/3 = ADS 不存在属正常（首次）——静默
                        logger?.LogWarning("ADS CreateFile(GENERIC_READ, span) failed (errno={Err}) on {Path}", err, adsPath);
                    return 0;
                }
                if (!Kernel32.GetFileSizeEx(h, out var size) || size <= 0) return 0;
                if (size > destination.Length) return -1;
                unsafe
                {
                    fixed (byte* ptr = destination)
                    {
                        if (!Kernel32.ReadFile(h, (IntPtr)ptr, (uint)size, out uint read, null) || read != size)
                            return -1;
                    }
                }
                return (int)size;
            }
            catch (Exception ex) { logger?.LogWarning(ex, "ADS read(span) threw on {Path}", adsPath); return -1; }
            finally { h?.Dispose(); }
        }

        return 0;
    }

    /// <summary>
    /// 元数据读取（返回精确尺寸托管数组——句柄级 xattr 公共 API / 探针用；
    /// 元数据热路径用 <see cref="ReadFileMeta(string, Span{byte}, string, ILogger?)"/> span 形态）。
    /// </summary>
    /// <param name="filePath">宿主文件路径（ADS 附加到此文件）。</param>
    /// <param name="attName">属性名称（默认 <c>XattrName</c>）。</param>
    /// <param name="logger">可选日志——读取失败时记录警告（ADS 首次不存在静默）。</param>
    /// <returns>元数据字节（精确尺寸托管数组）；无元数据/读取失败 = null。</returns>
   public static byte[]? ReadFileMeta(string filePath, string attName = XattrName, ILogger? logger = null)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            try
            {
                var xname = ToSystemXattrName(attName);
                long size = LibC.Getxattr(filePath, xname, (byte[]?)null, 0);
                if (size <= 0) return null;
                byte[] buf = new byte[size];
                long actual = LibC.Getxattr(filePath, xname, buf, (ulong)size);
                return actual == size ? buf : null;
            }
            catch (Exception ex) { logger?.LogWarning(ex, "Getxattr failed on {Path}", filePath); return null; }
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var adsPath = filePath + ":" + attName;
            SafeFileHandle? h = null;
            try
            {
                h = Kernel32.CreateFile(adsPath, NativeConstants.GenericRead, 0,
                    IntPtr.Zero, AdsOpenExisting, NativeConstants.FileAttributeNormal, IntPtr.Zero);
                if (h.IsInvalid)
                {
                    var err = Marshal.GetLastWin32Error();
                    // ERROR_FILE_NOT_FOUND(2)/ERROR_PATH_NOT_FOUND(3)：ADS 不存在属正常（首次），记 Debug 不警告
                    if (err is 2 or 3) { logger?.LogDebug("ADS not found on {Path} (first write?)", adsPath); }
                    else { logger?.LogWarning("ADS CreateFile(GENERIC_READ) failed (errno={Err}) on {Path}", err, adsPath); }
                    return null;
                }
                if (!Kernel32.GetFileSizeEx(h, out var size) || size <= 0) return null;
                var buf = new byte[size];
                fixed (byte* ptr = buf)
                {
                    if (!Kernel32.ReadFile(h, (IntPtr)ptr, (uint)size, out uint read, null) || read != size)
                    {
                        var err = Marshal.GetLastWin32Error();
                        logger?.LogWarning("ADS ReadFile failed/incomplete (errno={Err}, read={Read}/{Size}) on {Path}",
                            err, read, size, adsPath);
                        return null;
                    }
                }
                return buf;
            }
            catch (Exception ex) { logger?.LogWarning(ex, "ADS read threw on {Path}", adsPath); return null; }
            finally { h?.Dispose(); }
        }

        return null;
    }

    /// <summary>
    /// Delete metadata from xattr (Linux/macOS) or ADS (Windows)——best-effort。
    /// <para>★ Windows 原生实现：Kernel32.DeleteFile(filePath:tc_meta)，不走托管 File.Delete。</para>
    /// </summary>
    /// <param name="filePath">宿主文件路径。</param>
    /// <param name="attName">属性名称。</param>
    /// <param name="logger">可选日志——删除失败时记录警告（文件不存在静默）。</param>
    public static void DeleteFileMeta(string filePath, string attName = XattrName, ILogger? logger = null)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            try { LibC.Removexattr(filePath, ToSystemXattrName(attName)); }
            catch (Exception ex) { logger?.LogWarning(ex, "Removexattr failed on {Path}", filePath); }
            return;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var adsPath = filePath + ":" + attName;
            try
            {
                if (!Kernel32.DeleteFile(adsPath))
                {
                    var err = Marshal.GetLastWin32Error();
                    // ERROR_FILE_NOT_FOUND(2)/ERROR_PATH_NOT_FOUND(3)：ADS 不存在属正常（已删/未写过）
                    if (err != 2 && err != 3)
                        logger?.LogWarning("ADS DeleteFile failed (errno={Err}) on {Path}", err, adsPath);
                }
            }
            catch (Exception ex) { logger?.LogWarning(ex, "ADS delete threw on {Path}", adsPath); }
        }
    }

    /// <summary>
    /// 探测文件系统是否支持扩展属性（xattr/ADS）——决定 meta 写入策略。
    /// <para>★ 模式照搬 <see cref="ProbeUnbufferedIo"/>：try/catch 包裹，失败保守降级（安全侧倾斜）。</para>
    /// <para>★ 实现方式：在 probe 文件上试 <see cref="WriteFileMeta"/> + <see cref="ReadFileMeta(string, string, ILogger?)"/> 读回校验。
    ///   写入失败（EOPNOTSUPP/EACCES/ENOTSUP）或读回数据不一致 → <see cref="FileMetaSupport.Unsupported"/>。</para>
    /// <para>★ 决策影响：Supported → 主路径走 per-segment xattr/ADS（段前写 + 段满写真实偏移）；
    ///   Unsupported → 回退异步边车后台写（device 级集中文件，自适应频率）。</para>
    /// </summary>
    /// <param name="probeFilePath">探测用的宿主文件路径（不存在时自动创建——探测的是文件系统能力，
    ///   不是文件存在性；EngineMeta 首次惰性探测时 sidecar 文件尚未建，旧契约"须已存在"导致
    ///   setxattr ENOENT / ADS CreateFile 失败恒判 Unsupported，xattr 主路径从未生效）。</param>
    /// <param name="attName">属性名称（默认 <c>XattrName</c>）。</param>
    /// <param name="logger">可选日志——探测失败/异常时记录警告。</param>
    /// <returns>支持程度：<see cref="FileMetaSupport.Supported"/> 或 <see cref="FileMetaSupport.Unsupported"/>。</returns>
    public static FileMetaSupport ProbeFileMetaSupport(string probeFilePath, string attName = XattrName, ILogger? logger = null)
    {
        // ★ 探测文件不存在 → 先创建空文件（探测文件系统能力，宿主文件存在与否与能力无关）
        if (!File.Exists(probeFilePath))
        {
            try { File.WriteAllBytes(probeFilePath, Array.Empty<byte>()); }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "ProbeFileMetaSupport: probe file create failed on {Path}", probeFilePath);
                return FileMetaSupport.Unsupported;
            }
        }

        // 探测 payload：固定魔数 "TC_METAP"，读回校验防"读到旧 xattr"误判成功
        Span<byte> buf = stackalloc byte[16];
        Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(buf), 0x54435F4D45544150L);
        ReadOnlySpan<byte> probeData = buf;

        try
        {
            // ① 写入 xattr/ADS（best-effort，失败返回 false）
            if (!WriteFileMeta(probeFilePath, probeData,attName, logger))
            {
                logger?.LogWarning("ProbeFileMetaSupport: WriteFileMeta failed on {Path}, falling back to sidecar async mode", probeFilePath);
                return FileMetaSupport.Unsupported;
            }

            // ② 读回校验（防读到旧 xattr 误判成功）
            byte[]? readBack = ReadFileMeta(probeFilePath, attName,logger);
            if (readBack is null || readBack.Length < 8)
            {
                logger?.LogWarning("ProbeFileMetaSupport: ReadFileMeta returned null/short on {Path}, falling back to sidecar async mode", probeFilePath);
                return FileMetaSupport.Unsupported;
            }

            long readMagic = Unsafe.ReadUnaligned<long>(ref readBack[0]);
            if (readMagic != 0x54435F4D45544150L)
            {
                logger?.LogWarning("ProbeFileMetaSupport: magic mismatch on {Path} (got 0x{Magic:X}), falling back to sidecar async mode", probeFilePath, readMagic);
                return FileMetaSupport.Unsupported;
            }

            return FileMetaSupport.Supported;
        }
        catch (Exception ex)
        {
            // 探测本身异常（不应阻断业务）→ 安全侧倾斜，归 Unsupported 走异步边车
            logger?.LogWarning(ex, "ProbeFileMetaSupport threw on {Path}, falling back to sidecar async mode", probeFilePath);
            return FileMetaSupport.Unsupported;
        }
    }
}