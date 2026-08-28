using System.Runtime.InteropServices;

namespace TC.Tier.Products.Wal;

/// <summary>
/// TierWAL 快照传输帧 header——一致性点 N₀（导出开始时 PersistedIndex；导入方据此从 N₀+1 补增量）。
/// <para>布局：<c>[Magic 4B][Version 2B][SnapshotIndex 8B]</c></para>
/// </summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 14)]
internal struct WalSnapshotHeader
{
    internal const uint Magic = RecordMagic.WalSnapshotHeader;
    internal const ushort CurrentVersion = (ushort)((1 << 8) | 0);

    [FieldOffset(0)] internal uint MagicValue;
    [FieldOffset(4)] internal ushort Version;
    [FieldOffset(6)] internal long SnapshotIndex;
}
