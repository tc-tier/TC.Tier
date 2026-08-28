using System.Runtime.InteropServices;

namespace TC.Tier.Products.Wal;

/// <summary>
/// TierWAL 快照传输帧 footer——总验收：条数/总长/CRC（CRC 增量覆盖 Header + 全部 payload 帧字节，
/// footer 自身不参与）。
/// <para>布局：<c>[Magic 4B][EntryCount 8B][TotalPayload 8B][Crc32C 4B]</c></para>
/// </summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct WalSnapshotFooter
{
    internal const uint Magic = RecordMagic.WalSnapshotFooter;

    [FieldOffset(0)] internal uint MagicValue;
    [FieldOffset(4)] internal long EntryCount;
    [FieldOffset(12)] internal long TotalPayload;
    [FieldOffset(20)] internal uint Crc;
}
