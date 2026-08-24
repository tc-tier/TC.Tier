using System.Runtime.InteropServices;

namespace TC.Tier.Runtime.Structures.SortedIndex.Layout;

/// <summary>
/// <see cref="SkipListIndex{TKey}"/> 主存储帧格式（结构私有——独有 magic，配错数据文件在头校验即失败）。
/// </summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 20)]
public struct SkipListIndexHeader
{
    public const uint Magic = RecordMagic.SkipListIndexHeader; // "SLHD"
    public const ushort CurrentVersion = (ushort)((1 << 8) | 0); // major=1, minor=0


    [FieldOffset(0), ValidEquals(Magic)] public uint MagicValue;

    [FieldOffset(4), ValidEquals(CurrentVersion)]
    public ushort Version;

    [FieldOffset(6), ValidEquals(SortedIndexConstants.DefaultFlags)]
    public ushort Flags;

    /// <summary>族别（KindSorted）——体几何解释权。</summary>
    [FieldOffset(8)] public ushort Kind;

    [FieldOffset(10)] public ushort Reserved;

    /// <summary>体长（几何块 + 结构内容——不含头尾；读侧定界）。</summary>
    [FieldOffset(12)] public long BodyLength;
}