using System.Runtime.InteropServices;

namespace TC.Tier.Products.Wal;

/// <summary>
/// TierWAL opaque 容器头——TierWAL 三段式的"头部"（设计决策：只记最大/最小 long 及其对应地址，
/// 段是底层的概念，上层地址空间无限；raft 元数据预留区 = opaque 剩余）。
/// <para>布局：<c>[Magic 4B][Version 2B][pad 2B][TailIndex 8B][TailAddress 16B][HeadIndex 8B][HeadAddress 16B]</c></para>
/// <para>★ TailIndex/TailAddress = 最后一条已分配 entry（持久化水位——opaque 随显式提交落盘）；
///   HeadIndex/HeadAddress = 头截断边界（第一条存活 entry）。</para>
/// </summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 56)]
internal struct WalOpaqueHeader
{
    internal const uint Magic = RecordMagic.WalOpaque;
    internal const ushort CurrentVersion = (ushort)((1 << 8) | 0);

    [FieldOffset(0)] internal uint MagicValue;
    [FieldOffset(4)] internal ushort Version;
    [FieldOffset(8)] internal long TailIndex;
    [FieldOffset(16)] internal LogicalAddress TailAddress;
    [FieldOffset(32)] internal long HeadIndex;
    [FieldOffset(40)] internal LogicalAddress HeadAddress;
}
