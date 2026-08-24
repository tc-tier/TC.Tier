using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TC.Tier.Core.NativeInterop;

/// <summary>
/// Kernel32 IO 扩展（Core/IO 专用新增：LockFileEx / UnlockFileEx / DuplicateHandle）。
/// </summary>
internal static unsafe partial class Kernel32
{
    /// <summary>
    /// OVERLAPPED——LockFileEx/UnlockFileEx 的区间起点载体（Offset/OffsetHigh 传区间起点的低/高 32 位）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Overlapped
    {
        internal nuint Internal;
        internal nuint InternalHigh;
        internal uint OffsetLow;
        internal uint OffsetHigh;
        internal nuint EventHandle;
    }

    /// <summary>LOCKFILE_FAIL_IMMEDIATELY——非阻塞（不能获取立即返回失败）。</summary>
    internal const uint LockFileFailImmediately = 0x00000001;

    /// <summary>LOCKFILE_EXCLUSIVE_LOCK——排他（缺省为共享）。</summary>
    internal const uint LockFileExclusiveLock = 0x00000002;

    /// <summary>
    /// LockFileEx - Windows 原生字节范围锁（mandatory：锁同时阻止未持锁句柄的读写——本层 advisory 契约的强化端）。
    /// </summary>
    [LibraryImport(NativeLibraries.Kernel32, EntryPoint = "LockFileEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool LockFileEx(
        SafeFileHandle hFile, uint dwFlags, uint dwReserved,
        uint nNumberOfBytesToLockLow, uint nNumberOfBytesToLockHigh,
        ref Overlapped lpOverlapped);

    /// <summary>UnlockFileEx - 释放字节范围锁（区间须与 Lock 精确配对）。</summary>
    [LibraryImport(NativeLibraries.Kernel32, EntryPoint = "UnlockFileEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnlockFileEx(
        SafeFileHandle hFile, uint dwReserved,
        uint nNumberOfBytesToUnlockLow, uint nNumberOfBytesToUnlockHigh,
        ref Overlapped lpOverlapped);

    /// <summary>DUPLICATE_SAME_ACCESS——复刻句柄继承源句柄访问权。</summary>
    internal const uint DuplicateSameAccess = 0x00000002;

    /// <summary>
    /// DuplicateHandle - OS 句柄复刻（映射生命周期独立于父句柄的 Windows 实现基石——
    /// 复刻句柄与源指向同一 file object，独立关闭互不影响）。
    /// </summary>
    [LibraryImport(NativeLibraries.Kernel32, EntryPoint = "DuplicateHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DuplicateHandle(
        nint hSourceProcessHandle, SafeFileHandle hSourceHandle,
        nint hTargetProcessHandle, out nint lpTargetHandle,
        uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwOptions);

    // ══ 内存映射（手工路径——绕开 BCL FileStream 与 OVERLAPPED 复刻句柄的 IOCP 绑定不兼容）══

    /// <summary>PAGE_READONLY。</summary>
    internal const uint PageReadOnly = 0x02;

    /// <summary>PAGE_READWRITE。</summary>
    internal const uint PageReadWrite = 0x04;

    /// <summary>FILE_MAP_READ。</summary>
    internal const uint FileMapRead = 0x0004;

    /// <summary>FILE_MAP_READ | FILE_MAP_WRITE。</summary>
    internal const uint FileMapReadWrite = 0x0006;

    /// <summary>CreateFileMappingW - 在文件句柄上创建 section 对象（OVERLAPPED 句柄合法，无 IOCP 牵涉）。失败返回 IntPtr.Zero。</summary>
    [LibraryImport(NativeLibraries.Kernel32, EntryPoint = "CreateFileMappingW", SetLastError = true)]
    internal static partial nint CreateFileMapping(
        SafeFileHandle hFile, nint lpAttributes, uint flProtect,
        uint dwMaximumSizeHigh, uint dwMaximumSizeLow, nuint lpName);

    /// <summary>MapViewOfFile - 映射视图。★ dwFileOffset 必须对齐到系统分配粒度（通常 64K，非页大小）。失败返回 IntPtr.Zero。</summary>
    [LibraryImport(NativeLibraries.Kernel32, EntryPoint = "MapViewOfFile", SetLastError = true)]
    internal static partial nint MapViewOfFile(nint hFileMappingObject, uint dwDesiredAccess,
        uint dwFileOffsetHigh, uint dwFileOffsetLow, nuint dwNumberOfBytesToMap);

    /// <summary>FlushViewOfFile - 视图脏页写回（msync 语义）。</summary>
    [LibraryImport(NativeLibraries.Kernel32, EntryPoint = "FlushViewOfFile", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FlushViewOfFile(nint lpBaseAddress, nuint dwNumberOfBytesToFlush);

    /// <summary>UnmapViewOfFile - 解除视图映射。</summary>
    [LibraryImport(NativeLibraries.Kernel32, EntryPoint = "UnmapViewOfFile", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnmapViewOfFile(nint lpBaseAddress);
}
