using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TC.Tier.Core.NativeInterop;

/// <summary>
/// Kernel32.dll 原生函数封装（文件 IO / IOCP / 线程 / NUMA / 内存锁）。
/// </summary>
internal static unsafe partial class Kernel32
{
    // ════════════════════════════════════════════════════════════
    // 区域：文件 IO P/Invoke
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// CreateFile — 创建或打开文件（Unicode 版）。
    /// </summary>
    /// <param name="fileName">文件名</param>
    /// <param name="desiredAccess">访问权限</param>
    /// <param name="shareMode">共享模式</param>
    /// <param name="securityAttributes">安全属性指针</param>
    /// <param name="creationDisposition">创建方式</param>
    /// <param name="flagsAndAttributes">文件属性和标志</param>
    /// <param name="templateFile">模板文件句柄</param>
    /// <returns>如果成功返回文件句柄，否则返回 null</returns>
    [LibraryImport(NativeLibraries.Kernel32, EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true)]
    internal static partial SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition,
        uint flagsAndAttributes, IntPtr templateFile);

    [LibraryImport(NativeLibraries.Kernel32, EntryPoint = "MoveFileExW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool MoveFileEx(
        string existingFileName,
        string newFileName,
        uint flags);

    /// <summary>
    /// ReadFile — 从文件同步/重叠读取（IOCP 路径用）。
    /// </summary>
    /// <param name="file">文件句柄</param>
    /// <param name="buffer">缓冲区指针</param>
    /// <param name="numberOfBytesToRead">要读取的字节数</param>
    /// <param name="numberOfBytesRead">实际读取的字节数</param>
    /// <param name="overlapped">重叠结构指针</param>
    /// <returns>如果成功返回 true，否则返回 false</returns>
    [LibraryImport(NativeLibraries.Kernel32, EntryPoint = "ReadFile", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ReadFile(
        SafeFileHandle file, IntPtr buffer, uint numberOfBytesToRead,
        out uint numberOfBytesRead, NativeOverlapped* overlapped);

    /// <summary>
    /// WriteFile — 向文件同步/重叠写入（IOCP 路径用）。
    /// </summary>
    /// <param name="file">文件句柄</param>
    /// <param name="buffer">缓冲区指针</param>
    /// <param name="numberOfBytesToWrite">要写入的字节数</param>
    /// <param name="numberOfBytesWritten">实际写入的字节数</param>
    /// <param name="overlapped">重叠结构指针</param>
    /// <returns>如果成功返回 true，否则返回 false</returns>
    [LibraryImport(NativeLibraries.Kernel32, EntryPoint = "WriteFile", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WriteFile(
        SafeFileHandle file, IntPtr buffer, uint numberOfBytesToWrite,
        out uint numberOfBytesWritten, NativeOverlapped* overlapped);

    /// <summary>
    /// CreateIoCompletionPort — 创建或关联 IOCP 完成端口。
    /// </summary>
    /// <param name="file">文件句柄</param>
    /// <param name="existingCompletionPort">现有的 IOCP 完成端口句柄，如果为 null 则创建新的完成端口</param>
    /// <param name="completionKey">完成键，用于标识关联的文件句柄</param>
    /// <param name="numberOfConcurrentThreads">并发线程数</param>
    /// <returns>如果成功返回 IOCP 完成端口句柄，否则返回 IntPtr.Zero</returns>
    [LibraryImport(NativeLibraries.Kernel32, EntryPoint = "CreateIoCompletionPort", SetLastError = true)]
    internal static partial IntPtr CreateIoCompletionPort(
        SafeFileHandle file, IntPtr existingCompletionPort,
        UIntPtr completionKey, uint numberOfConcurrentThreads);

    /// <summary>
    /// GetQueuedCompletionStatus — 从 IOCP 完成端口获取完成的 IO 操作。
    /// </summary>
    /// <param name="completionPort">IOCP 完成端口句柄</param>
    /// <param name="numberOfBytesTransferred">传输的字节数</param>
    /// <param name="completionKey">完成键</param>
    /// <param name="overlapped">重叠结构指针</param>
    /// <param name="milliseconds">超时时间（毫秒）</param>
    /// <returns>如果成功返回 true，否则返回 false</returns>
    [LibraryImport(NativeLibraries.Kernel32, EntryPoint = "GetQueuedCompletionStatus", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetQueuedCompletionStatus(
        IntPtr completionPort, out uint numberOfBytesTransferred,
        out IntPtr completionKey, out NativeOverlapped* overlapped, uint milliseconds);

    /// <summary>
    /// GetFileSizeEx — 获取文件大小（64 位）。
    /// </summary>
    /// <param name="file">文件句柄</param>
    /// <param name="fileSize">文件大小</param>
    /// <returns>如果成功返回 true，否则返回 false</returns>
    [LibraryImport(NativeLibraries.Kernel32, EntryPoint = "GetFileSizeEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileSizeEx(SafeFileHandle file, out long fileSize);

    /// <summary>
    /// SetFilePointer — 设置文件指针位置（32 位）。
    /// </summary>
    /// <param name="file">文件句柄</param>
    /// <param name="distanceToMove">移动的距离</param>
    /// <param name="distanceToMoveHigh">高 32 位的移动距离</param>
    /// <param name="moveMethod">移动方法</param>
    /// <returns>新的文件指针位置</returns>
    [LibraryImport(NativeLibraries.Kernel32, EntryPoint = "SetFilePointer", SetLastError = true)]
    internal static partial uint SetFilePointer(
        SafeFileHandle file, int distanceToMove, ref int distanceToMoveHigh, MoveMethod moveMethod);

    /// <summary>
    /// SetEndOfFile — 设置文件结束位置（截断文件）。
    /// </summary>
    /// <param name="file">文件句柄</param>
    /// <returns>如果成功返回 true，否则返回 false</returns>
    [LibraryImport(NativeLibraries.Kernel32, EntryPoint = "SetEndOfFile", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetEndOfFile(SafeFileHandle file);

    /// <summary>
    /// 获取磁盘卷的扇区/簇信息（用于计算文件对齐）。
    /// </summary>
    /// <param name="rootPathName">磁盘卷的根路径名</param>
    /// <param name="sectorsPerCluster">每个簇的扇区数</param>
    /// <param name="bytesPerSector">每个扇区的字节数</param>
    /// <param name="numberOfFreeClusters">可用簇的数量</param>
    /// <param name="totalNumberOfClusters">总簇的数量</param>
    /// <returns>如果成功返回 true，否则返回 false</returns>
    [LibraryImport(NativeLibraries.Kernel32, EntryPoint = "GetDiskFreeSpaceW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetDiskFreeSpace(
        string rootPathName, out uint sectorsPerCluster, out uint bytesPerSector,
        out uint numberOfFreeClusters, out uint totalNumberOfClusters);

    /// <summary>
    /// GetVolumeInformationW — 查询卷的文件系统信息（DirectIO 能力探测用）。
    /// <para>★ 用途：取 <paramref name="fileSystemFlags"/>（含
    ///   <see cref="NativeConstants.FileFileCompression"/> 等）判定 FILE_FLAG_NO_BUFFERING 是否兼容；
    ///   取 fileSystemNameBuffer（如 "NTFS"/"ReFS"/"CscFS"）判定是否网络重定向器卷。</para>
    /// <para>★ DirectIO 判定：无"FILE_SUPPORTS_UNBUFFERED_IO"标志位，靠 FS name + 压缩标志推断——
    ///   NTFS/ReFS 非压缩 → Supported；网络/压缩 → Ignored/降级。</para>
    /// </summary>
    /// <param name="rootPathName">卷根路径（如 "C:\"）。</param>
    /// <param name="volumeNameBuffer">卷名缓冲区（可传 IntPtr.Zero 跳过）。</param>
    /// <param name="volumeNameSize">卷名缓冲区大小。</param>
    /// <param name="volumeSerialNumber">输出：卷序列号。</param>
    /// <param name="maximumComponentLength">输出：最大路径组件长度。</param>
    /// <param name="fileSystemFlags">输出：文件系统标志（FILE_FILE_COMPRESSION 等）。</param>
    /// <param name="fileSystemNameBuffer">文件系统名缓冲区（"NTFS" 等）。</param>
    /// <param name="fileSystemNameSize">文件系统名缓冲区大小。</param>
    /// <returns>成功返回 true；失败返回 false（Marshal.GetLastWin32Error 取错误码）。</returns>
    [LibraryImport(NativeLibraries.Kernel32, EntryPoint = "GetVolumeInformationW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetVolumeInformation(
        string rootPathName,
        IntPtr volumeNameBuffer, uint volumeNameSize,
        out uint volumeSerialNumber, out uint maximumComponentLength,
        out uint fileSystemFlags,
        IntPtr fileSystemNameBuffer, uint fileSystemNameSize);


    /// <summary>
    /// GetFileInformationByHandleEx — 获取文件句柄的扩展信息（文件存储信息 / 文件 ID / 文件属性等）。
    /// </summary>
    /// <param name="file">文件句柄</param>
    /// <param name="infoClass">信息类别</param>
    /// <param name="fileStorageInfo">文件存储信息</param>
    /// <param name="bufferSize">缓冲区大小</param>
    /// <returns>如果成功返回 true，否则返回 false</returns>
    [LibraryImport(NativeLibraries.Kernel32, EntryPoint = "GetFileInformationByHandleEx",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileInformationByHandleEx(
        SafeFileHandle file, FileInfoByHandleClass infoClass, out FileStorageInfo fileStorageInfo, uint bufferSize);

    /// <summary>
    /// GetFileInformationByHandleEx(FileStandardInfo) 重载——查文件真实分配大小（区分稀疏/预分配空洞）。
    /// 返回 <see cref="FileStandardInfo.AllocatedSize"/>（物理分配）vs <see cref="FileStandardInfo.EndOfFile"/>（逻辑大小）。
    /// </summary>
    [LibraryImport(NativeLibraries.Kernel32, EntryPoint = "GetFileInformationByHandleEx",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileInformationByHandleEx(
        SafeFileHandle file, FileInfoByHandleClass infoClass, out FileStandardInfo fileStandardInfo, uint bufferSize);

    /// <summary>
    /// DeleteFileW — 删除文件（Unicode 版）。
    /// </summary>
    /// <param name="fileName">要删除的文件名</param>
    /// <returns>如果成功返回 true，否则返回 false</returns>
    [LibraryImport(NativeLibraries.Kernel32, EntryPoint = "DeleteFileW", StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteFile(string fileName);

    // ════════════════════════════════════════════════════════════
    // 区域：线程与 NUMA P/Invoke
    // ════════════════════════════════════════════════════════════

    /// <summary>GetCurrentThread — 获取当前线程伪句柄。</summary>
    [LibraryImport(NativeLibraries.Kernel32, EntryPoint = "GetCurrentThread")]
    private static partial IntPtr GetCurrentThread();

    /// <summary>
    /// GetCurrentThreadId — 获取当前线程 ID（非伪句柄）。
    /// </summary>
    /// <returns>当前线程的 ID</returns>
    [LibraryImport(NativeLibraries.Kernel32, EntryPoint = "GetCurrentThreadId")]
    internal static partial uint GetCurrentThreadId();

    /// <summary>
    /// GetCurrentProcessorNumber — 获取当前线程运行的处理器编号（NUMA 机上为 socket 内核编号）。
    /// </summary>
    /// <returns>当前线程运行的处理器编号</returns>
    [LibraryImport(NativeLibraries.Kernel32, EntryPoint = "GetCurrentProcessorNumber", SetLastError = true)]
    private static partial uint GetCurrentProcessorNumber();

    /// <summary>GetActiveProcessorCount — 获取指定组的活跃处理器数。</summary>
    [LibraryImport(NativeLibraries.Kernel32, EntryPoint = "GetActiveProcessorCount", SetLastError = true)]
    private static partial uint GetActiveProcessorCount(uint groupNumber);

    /// <summary>GetActiveProcessorGroupCount — 获取活跃处理器组数（NUMA 插槽数）。</summary>
    [LibraryImport(NativeLibraries.Kernel32, EntryPoint = "GetActiveProcessorGroupCount", SetLastError = true)]
    private static partial ushort GetActiveProcessorGroupCount();

    /// <summary>SetThreadGroupAffinity — 设置线程的处理器组亲和性。</summary>
    [LibraryImport(NativeLibraries.Kernel32, EntryPoint = "SetThreadGroupAffinity", SetLastError = true)]
    private static partial int SetThreadGroupAffinity(IntPtr thread, ref GroupAffinity groupAffinity,
        ref GroupAffinity previousGroupAffinity);

    /// <summary>
    /// GetThreadGroupAffinity — 获取线程的处理器组亲和性。
    /// </summary>
    /// <param name="thread">线程句柄</param>
    /// <param name="previousGroupAffinity">线程的处理器组亲和性信息</param>
    /// <returns>如果成功返回 true，否则返回 false</returns>
    [LibraryImport(NativeLibraries.Kernel32, EntryPoint = "GetThreadGroupAffinity", SetLastError = true)]
    private static partial int GetThreadGroupAffinity(IntPtr thread, ref GroupAffinity previousGroupAffinity);

    // ════════════════════════════════════════════════════════════
    // 区域：进程与句柄 P/Invoke
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// GetCurrentProcess — 获取当前进程伪句柄。
    /// </summary>
    /// <returns>当前进程的伪句柄</returns>
    [LibraryImport(NativeLibraries.Kernel32, EntryPoint = "GetCurrentProcess", SetLastError = true)]
    internal static partial IntPtr GetCurrentProcess();

    /// <summary>
    /// CloseHandle — 关闭句柄（文件 / 进程 / 线程 / IOCP 等）。
    /// </summary>
    /// <param name="handle">要关闭的句柄</param>
    /// <returns>如果成功返回 true，否则返回 false</returns>
    [LibraryImport(NativeLibraries.Kernel32, EntryPoint = "CloseHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(IntPtr handle);

    /// <summary>
    /// DeviceIoControl — 设备控制函数（对卷启用 USN 日志标记权限用）。
    /// </summary>
    /// <param name="device">设备句柄</param>
    /// <param name="ioControlCode">控制码</param>
    /// <param name="inBuffer">输入缓冲区指针</param>
    /// <param name="inBufferSize">输入缓冲区大小</param>
    /// <param name="outBuffer">输出缓冲区指针</param>
    /// <param name="outBufferSize">输出缓冲区大小</param>
    /// <param name="bytesReturned">返回的字节数</param>
    /// <param name="overlapped">重叠结构指针</param>
    /// <returns>如果成功返回 true，否则返回 false</returns>
    [LibraryImport(NativeLibraries.Kernel32, EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(
        SafeFileHandle device, uint ioControlCode,
        void* inBuffer, int inBufferSize, IntPtr outBuffer, int outBufferSize,
        ref uint bytesReturned, IntPtr overlapped);

    /// <summary>
    /// DeviceIoControl 的 byte* outBuffer 版本——FSCTL_QUERY_ALLOCATED_RANGES 用。
    /// </summary>
    [LibraryImport(NativeLibraries.Kernel32, EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool DeviceIoControlQueryAllocatedRanges(
        SafeFileHandle device, uint ioControlCode,
        void* inBuffer, int inBufferSize, byte* outBuffer, int outBufferSize,
        ref uint bytesReturned, IntPtr overlapped);

    /// <summary>
    /// SetFilePointerEx — 设置文件指针位置（64 位）。
    /// </summary>
    /// <param name="file">要设置文件指针的文件句柄</param>
    /// <param name="distanceToMove">要移动的距离</param>
    /// <param name="newFilePointer">新的文件指针位置</param>
    /// <param name="moveMethod">移动方法</param>
    /// <returns>如果成功返回 true，否则返回 false</returns>
    [LibraryImport(NativeLibraries.Kernel32, EntryPoint = "SetFilePointerEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFilePointerEx(SafeFileHandle file, long distanceToMove, out long newFilePointer,
        uint moveMethod);

    /// <summary>
    /// SetFileValidData — 设置文件有效数据长度（扩展文件时，允许跳过写零填充，需 SeManageVolumePrivilege 权限）。
    /// </summary>
    /// <param name="file">要设置有效数据长度的文件句柄</param>
    /// <param name="validDataLength">有效数据长度</param>
    /// <returns>如果成功返回 true，否则返回 false</returns>
    [LibraryImport(NativeLibraries.Kernel32, EntryPoint = "SetFileValidData", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileValidData(SafeFileHandle file, long validDataLength);

    // ════════════════════════════════════════════════════════════
    // 区域：内存锁 P/Invoke（VirtualLock / VirtualUnlock）
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// VirtualLock — 锁定内存到物理内存（禁止 swap）。
    /// </summary>
    /// <param name="address">要锁定的内存地址</param>
    /// <param name="size">要锁定的内存大小</param>
    /// <returns>如果成功返回 true，否则返回 false</returns>
    [LibraryImport(NativeLibraries.Kernel32, EntryPoint = "VirtualLock", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool VirtualLock(void* address, nuint size);

    /// <summary>
    /// VirtualUnlock — 解锁物理内存。
    /// </summary>
    /// <param name="address">要解锁的内存地址</param>
    /// <param name="size">要解锁的内存大小</param>
    /// <returns>如果成功返回 true，否则返回 false</returns>
    [LibraryImport(NativeLibraries.Kernel32, EntryPoint = "VirtualUnlock", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool VirtualUnlock(void* address, nuint size);

    // ════════════════════════════════════════════════════════════
    // 区域：托管封装方法（线程亲和 / NUMA / 权限 / 文件大小）
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 线程轮询绑核（[socket, core] 顺序）。NUMA 机上索引 0..N-1 映射到 socket0 的 core0..coreN-1。
    /// </summary>
    /// <param name="threadIndex">线程索引（从 0 起）。</param>
    public static void AffinitizeThreadRoundRobin(uint threadIndex)
    {
        var processorCount = GetActiveProcessorCount(NativeConstants.AllProcessorGroups);
        var processorGroupCount = GetActiveProcessorGroupCount();
        var procsPerGroup = processorCount / processorGroupCount;

        GroupAffinity affinity = default;
        GroupAffinity oldAffinity = default;

        var thread = GetCurrentThread();
        GetThreadGroupAffinity(thread, ref affinity);

        threadIndex %= processorCount;
        affinity.Mask = (ulong)1L << ((int)(threadIndex % procsPerGroup));
        affinity.Group = threadIndex / procsPerGroup;

        if (SetThreadGroupAffinity(thread, ref affinity, ref oldAffinity) == 0)
            throw new InvalidOperationException("无法绑定线程亲和性");
    }

    /// <summary>获取处理器组数（NUMA 插槽数）与每组处理器数。</summary>
    public static (uint groupCount, uint procsPerGroup) GetNumGroupsProcsPerGroup()
    {
        var processorCount = GetActiveProcessorCount(NativeConstants.AllProcessorGroups);
        var processorGroupCount = GetActiveProcessorGroupCount();
        return (processorGroupCount, processorCount / processorGroupCount);
    }

    /// <summary>
    /// 线程分片绑核（[core, socket] 顺序）。NUMA 机上索引 0..N-1 映射到各 socket 的 core0。
    /// </summary>
    /// <param name="threadIndex">线程索引（从 0 起）。</param>
    /// <param name="processorGroupCount">NUMA 插槽数。</param>
    public static void AffinitizeThreadShardedNuma(uint threadIndex, ushort processorGroupCount)
    {
        var processorCount = GetActiveProcessorCount(NativeConstants.AllProcessorGroups);
        var procsPerGroup = processorCount / processorGroupCount;
        threadIndex = procsPerGroup * (threadIndex % processorGroupCount) + (threadIndex / processorGroupCount);
        AffinitizeThreadRoundRobin(threadIndex);
    }

    /// <summary>进程权限是否已成功启用（缓存，避免重复调内核）。</summary>
    private static bool? _processPrivilegeEnabled;

    /// <summary>启用进程级 SeManageVolumePrivilege 权限（卷管理用）。非 Windows 返回 false。</summary>
    public static bool EnableProcessPrivileges()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
        if (_processPrivilegeEnabled.HasValue) return _processPrivilegeEnabled.Value;

        TokenPrivileges privileges = default;
        privileges.PrivilegeCount = 1;
        privileges.Privileges.Attributes = NativeConstants.SePrivilegeEnabled;

        Luid luid = default;
        if (!AdvApi32.LookupPrivilegeValue(null, "SeManageVolumePrivilege", ref luid))
        {
            _processPrivilegeEnabled = false;
            return false;
        }

        privileges.Privileges.Luid = luid;

        if (!AdvApi32.OpenProcessToken(GetCurrentProcess(), NativeConstants.TokenAdjustPrivileges, out var token))
        {
            _processPrivilegeEnabled = false;
            return false;
        }

        if (!AdvApi32.AdjustTokenPrivileges(token, 0, ref privileges, 0, IntPtr.Zero, IntPtr.Zero) ||
            Marshal.GetLastWin32Error() != 0)
        {
            CloseHandle(token);
            _processPrivilegeEnabled = false;
            return false;
        }

        CloseHandle(token);
        _processPrivilegeEnabled = true;
        return true;
    }

    /// <summary>
    /// CtlCode — 生成设备控制码（DeviceIoControl 用）。
    /// </summary>
    /// <param name="deviceType">设备类型</param>
    /// <param name="function">功能码</param>
    /// <param name="method">方法</param>
    /// <param name="access">访问权限</param>
    /// <returns>生成的设备控制码</returns>
    internal static uint CtlCode(uint deviceType, uint function, uint method, uint access)
        => (deviceType << 16) | (access << 14) | (function << 2) | method;

    /// <summary>
    /// 启用卷级 USN 日志标记权限（需要 SeManageVolumePrivilege 权限）。非 Windows 返回 false。
    /// </summary>
    /// <param name="fileName">文件名</param>
    /// <param name="handle">文件句柄</param>
    /// <returns>是否成功启用权限</returns>
    internal static bool EnableVolumePrivileges(string fileName, SafeFileHandle handle)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
        if (_processPrivilegeEnabled == false) return false;

        var volumePath = string.Concat(@"\\.\", fileName.AsSpan(0, 2));
        const uint creationDisposition = unchecked((uint)FileMode.Open);
        var volumeHandle = CreateFile(volumePath, 0, 0, IntPtr.Zero, creationDisposition,
            NativeConstants.FileAttributeNormal, IntPtr.Zero);
        MarkHandleInfo info;
        info.UsnSourceInfo = 0x1;
        info.VolumeHandle = volumeHandle.DangerousGetHandle();
        info.HandleInfo = 0x1;

        uint bytesReturned = 0;
        var result = DeviceIoControl(handle,
            CtlCode(NativeConstants.FileDeviceFileSystem, NativeConstants.FsctlMarkHandleInfoFunction, 0, 0),
            &info, sizeof(MarkHandleInfo), IntPtr.Zero, 0, ref bytesReturned, IntPtr.Zero);

        volumeHandle.Close();
        return result;
    }

    /// <summary>
    /// 设置文件大小（扩展文件时，允许跳过写零填充，需 SeManageVolumePrivilege 权限）。非 Windows 返回 false。
    /// </summary>
    /// <param name="fileHandle">文件句柄</param>
    /// <param name="fileSize">文件大小</param>
    /// <returns>是否成功设置文件大小</returns>
    public static bool SetFileSize(SafeFileHandle fileHandle, long fileSize)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;

        if (!SetFilePointerEx(fileHandle, fileSize, out _, 0)) return false;
        return SetEndOfFile(fileHandle) && SetFileValidData(fileHandle, fileSize);
    }

    /// <summary>
    /// 将 Win32 错误码转换为 HRESULT（用于 COM / .NET 异常处理）。
    /// </summary>
    internal static int MakeHrFromErrorCode(int errorCode) => unchecked((int)0x80070000) | errorCode;

    // ════════════════════════════════════════════════════════════
    // 区域：Windows 稀疏文件打洞（FSCTL_SET_SPARSE + FSCTL_SET_ZERO_DATA）
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 将文件标记为稀疏（FSCTL_SET_SPARSE）。打洞前必须先设稀疏，否则 FSCTL_SET_ZERO_DATA 报错。
    /// 已是稀疏文件返回 true（no-op）；非 Windows 返回 false。
    /// </summary>
    internal static bool SetSparse(SafeFileHandle handle)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
        uint bytesReturned = 0;
        // 已是稀疏 → ERROR_INVALID_PARAMETER (87)，视为成功
        var ok = DeviceIoControl(handle,
            CtlCode(NativeConstants.FileDeviceFileSystem, NativeConstants.FsctlSetSparseFunction, 0, 0),
            null, 0, IntPtr.Zero, 0, ref bytesReturned, IntPtr.Zero);
        return ok || Marshal.GetLastWin32Error() == 87;  // 87 = 已稀疏
    }

    /// <summary>
    /// 将 [offset, offset+length) 区间清零并归还磁盘块（FSCTL_SET_ZERO_DATA）。
    /// 调用前须先 <see cref="SetSparse"/>。非 Windows 返回 false。
    /// </summary>
    internal static bool SetZeroData(SafeFileHandle handle, long offset, long length)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
        FileZeroDataInformation info;
        info.Offset = offset;
        info.BeyondFinalOffset = offset + length;
        uint bytesReturned = 0;
        // FSCTL_SET_ZERO_DATA = CTL_CODE(9, 50, METHOD_BUFFERED=0, FILE_WRITE_ACCESS=2)
        return DeviceIoControl(handle,
            CtlCode(NativeConstants.FileDeviceFileSystem, NativeConstants.FsctlSetZeroDataFunction, 0, /*FILE_WRITE_ACCESS*/2),
            &info, sizeof(FileZeroDataInformation), IntPtr.Zero, 0, ref bytesReturned, IntPtr.Zero);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileZeroDataInformation
    {
        public long Offset;
        public long BeyondFinalOffset;
    }
}