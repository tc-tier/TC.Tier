using System.Runtime.InteropServices;

namespace TC.Tier.Runtime.Structures.SortedIndex.Layout;

/// <summary>
/// <see cref="BTreeIndex{TKey}"/> 主存储帧 footer（32B）：W + CRC64 总验收（CRC 覆盖 Header + Body + Footer 前 24B）。
/// </summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = FooterSize)]
public struct BTreeIndexFooter
{
    internal const uint FooterMagic = RecordMagic.BTreeIndexFooter; // "BIFT"

    private const int FooterSize = 32;

    [FieldOffset(0), ValidEquals(FooterMagic)]
    public uint Magic;

    [FieldOffset(4)] public uint Reserved;

    /// <summary>水位 W：帧内容 = record 流 [?, W) 的折叠；重放只需 (W, End)。</summary>
    [FieldOffset(8)] public LogicalAddress Watermark;

    /// <summary>CRC64（覆盖 Header + Body + Footer 前 24B）。</summary>
    [FieldOffset(24)] public ulong Crc;
}