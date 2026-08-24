using System.Runtime.InteropServices;

namespace TC.Tier.Core.NativeInterop;

/// <summary>
/// Windows NUMA 处理器组亲和性结构（GroupAffinity）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GroupAffinity
{
    /// <summary>
    /// 处理器掩码（每位对应一个逻辑处理器，1=允许，0=禁止）。
    /// </summary>
    public ulong Mask;

    /// <summary>
    /// 处理器组号（NUMA 插槽索引，0..N-1）。
    /// </summary>
    public uint Group;

    /// <summary>
    /// 保留字段（必须为 0）。
    /// </summary>
    public uint Reserved1;

    /// <summary>
    /// 保留字段（必须为 0）。
    /// </summary>
    public uint Reserved2;

    /// <summary>
    /// 保留字段（必须为 0）。
    /// </summary>
    public uint Reserved3;
}