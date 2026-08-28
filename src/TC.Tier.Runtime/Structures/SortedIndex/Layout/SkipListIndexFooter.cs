using System.Runtime.InteropServices;

namespace TC.Tier.Runtime.Structures.SortedIndex.Layout;

/// <summary>
/// <see cref="SkipListIndex{TKey}"/> 主存储帧 footer（32B）：W + CRC64 总验收（CRC 覆盖 Header + Body + Footer 前 24B）。
/// </summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = FooterSize)]
public struct SkipListIndexFooter
{
    internal const uint FooterMagic = RecordMagic.SkipListIndexFooter; // "SLFT"

    private const int FooterSize = 32;

    /// <summary>帧尾 magic 字段（偏移 0——ValidEquals(FooterMagic) 校验）。</summary>
    [FieldOffset(0), ValidEquals(FooterMagic)]
    public uint Magic;

    /// <summary>保留字段（偏移 4——对齐填充）。</summary>
    [FieldOffset(4)] public uint Reserved;

    /// <summary>水位 W：帧内容 = record 流 [?, W) 的折叠；重放只需 (W, End)。</summary>
    [FieldOffset(8)] public LogicalAddress Watermark;

    /// <summary>CRC64（覆盖 Header + Body + Footer 前 24B）。</summary>
    [FieldOffset(24)] public ulong Crc;
}