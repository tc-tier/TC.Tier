using System.Runtime.InteropServices;

namespace TC.Tier.Core.NativeInterop;

/// <summary>
/// TokenPrivileges 结构体表示访问令牌的权限信息。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct TokenPrivileges
{
    /// <summary>
    /// 权限数量（通常为 1）。
    /// </summary>
    public uint PrivilegeCount;
    /// <summary>
    /// 权限数组（通常只有一个元素）。
    /// </summary>
    public LuidAndAttributes Privileges;
}