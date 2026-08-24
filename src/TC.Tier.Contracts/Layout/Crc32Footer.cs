using System.Runtime.InteropServices;

namespace TC.Tier.Contracts.Layout;

/// <summary>
/// Crc32 Footer（unified-binary-layout.md §1.1 Footer 固定部分）。
/// 共用于：FixedBlock / PageMirror / StreamMeta / LogMeta。
/// <para>Footer = padding(可变, 全零) + Crc32(4B)。padding 由 Header.PaddingLength 决定。</para>
/// <para>CRC 覆盖 = Header + Payload + padding。</para>
/// </summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 4)]
public struct Crc32Footer
{
    /// <summary>
    /// CRC32 校验码。
    /// </summary>
    [FieldOffset(0)] public uint Crc;
}
