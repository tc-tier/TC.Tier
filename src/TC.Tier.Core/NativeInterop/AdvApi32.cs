using System.Runtime.InteropServices;

namespace TC.Tier.Core.NativeInterop;

/// <summary>
/// Advapi32.dll 原生函数封装（权限 / 令牌）。
/// </summary>
internal static partial class AdvApi32
{
    /// <summary>
    /// LookupPrivilegeValue — 查找权限名称对应的 LUID（本地唯一标识符）。
    /// </summary>
    /// <param name="systemName">系统名称，通常为 null 表示本地系统</param>
    /// <param name="name">权限名称</param>
    /// <param name="luid">返回的 LUID（本地唯一标识符）</param>
    /// <returns>如果成功返回 true，否则返回 false</returns>
    [LibraryImport(NativeLibraries.Advapi32, EntryPoint = "LookupPrivilegeValueW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool LookupPrivilegeValue(string? systemName, string name, ref Luid luid);

    /// <summary>
    /// OpenProcessToken — 打开指定进程的访问令牌。
    /// </summary>
    /// <param name="processHandle">进程句柄</param>
    /// <param name="desiredAccess">所需的访问权限</param>
    /// <param name="tokenHandle">返回的令牌句柄</param>
    /// <returns>如果成功返回 true，否则返回 false</returns>
    [LibraryImport(NativeLibraries.Advapi32, EntryPoint = "OpenProcessToken", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    /// <summary>
    /// AdjustTokenPrivileges — 调整访问令牌的权限。
    /// </summary>
    /// <param name="tokenHandle">令牌句柄</param>
    /// <param name="disableAllPrivileges">是否禁用所有权限</param>
    /// <param name="newState">新的权限状态</param>
    /// <param name="bufferLength">缓冲区长度</param>
    /// <param name="previousState">先前的权限状态</param>
    /// <param name="returnLength">返回的长度</param>
    /// <returns></returns>
    [LibraryImport(NativeLibraries.Advapi32, EntryPoint = "AdjustTokenPrivileges", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AdjustTokenPrivileges(
        IntPtr tokenHandle, int disableAllPrivileges,
        ref TokenPrivileges newState, int bufferLength,
        IntPtr previousState, IntPtr returnLength);
}
