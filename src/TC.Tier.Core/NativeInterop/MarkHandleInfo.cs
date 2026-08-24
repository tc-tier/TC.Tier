using System.Runtime.InteropServices;

namespace TC.Tier.Core.NativeInterop;

/// <summary>
/// MarkHandleInfo 结构体用于标记句柄信息，通常用于与 USN 日志相关的操作。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MarkHandleInfo
{
    /// <summary>
    /// USN 日志源信息（0x1 表示启用 USN 日志标记）。
    /// </summary>
    public uint UsnSourceInfo;
    /// <summary>
    /// 卷句柄（DeviceIoControl 输入参数，通常为卷的句柄）。
    /// </summary>
    public IntPtr VolumeHandle;
    /// <summary>
    /// 句柄信息（0x1 表示启用 USN 日志标记）。
    /// </summary>
    public uint HandleInfo;
}