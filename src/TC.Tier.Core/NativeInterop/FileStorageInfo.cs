using System.Runtime.InteropServices;

namespace TC.Tier.Core.NativeInterop;

/// <summary>
/// 文件存储信息结构体，用于获取文件系统的扇区大小和对齐信息。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FileStorageInfo
{
    /// <summary>
    /// 逻辑扇区大小（字节数）。
    /// </summary>
    public uint LogicalBytesPerSector;
    /// <summary>
    /// 物理扇区大小（字节数）。
    /// </summary>
    public uint PhysicalBytesPerSectorForAtomicity;
    /// <summary>
    /// 物理扇区大小（字节数），用于性能优化。
    /// </summary>
    public uint PhysicalBytesPerSectorForPerformance;
    /// <summary>
    /// 逻辑扇区大小（字节数），用于性能优化。
    /// </summary>
    public uint FileSystemEffectivePhysicalBytesPerSectorForAtomicity;
    /// <summary>
    /// 文件系统的扇区对齐标志。
    /// </summary>
    public uint Flags;
    /// <summary>
    /// 文件系统的扇区对齐偏移量（字节数）。
    /// </summary>
    public uint ByteOffsetForSectorAlignment;
    /// <summary>
    /// 文件系统的分区对齐偏移量（字节数）。
    /// </summary>
    public uint ByteOffsetForPartitionAlignment;
}