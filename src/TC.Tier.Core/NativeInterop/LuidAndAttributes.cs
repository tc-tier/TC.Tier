using System.Runtime.InteropServices;

namespace TC.Tier.Core.NativeInterop;

/// <summary>
/// LUID 与属性组合（权限令牌用）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct LuidAndAttributes
{
    /// <summary>
    /// LUID（本地唯一标识符）。
    /// </summary>
    public Luid Luid;

    /// <summary>
    /// 属性。
    /// </summary>
    public uint Attributes;
}