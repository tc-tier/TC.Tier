using System.Runtime.InteropServices;

namespace TC.Tier.Runtime.Structures.Log;

/// <summary>
/// Log PageFrame header（8B）——源生成器自动生成 LogPageFrameHeaderCodec。
/// <para>PageFrame = [header 8B][entry data][CRC32C footer 4B]。Log append-only 无需 frameAddress。</para>
/// </summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 8)]
internal struct LogPageFrameHeader
{
    /// <summary>"LPGF" — LogPageFrame magic。</summary>
    [FieldOffset(0), ValidEquals(RecordMagic.LogPageFrame)]
    public uint MagicValue;

    /// <summary>页内有效数据字节数（不含 header/footer）。</summary>
    [FieldOffset(4)]
    public int DataLength;
}
