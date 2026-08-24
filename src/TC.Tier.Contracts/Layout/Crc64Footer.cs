using System.Runtime.InteropServices;

namespace TC.Tier.Contracts.Layout;

/// <summary>
/// CRC64 校验码尾部结构体。
/// </summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = FooterSize)]
public struct Crc64Footer
{
    private const int FooterSize = 8;

    /// <summary>
    /// CRC64 校验码。
    /// </summary>
    [FieldOffset(0)] public ulong Crc;
}
