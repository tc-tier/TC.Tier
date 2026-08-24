using System.Runtime.InteropServices;

namespace TC.Tier.Core.NativeInterop;

/// <summary>
/// LUID（本地唯一标识符，权限令牌用）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Luid
{
    /// <summary>
    /// 低 32 位。
    /// </summary>
    public uint LowPart;
    /// <summary>
    /// 高 32 位。
    /// </summary>
    public int HighPart;
}