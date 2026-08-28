using System.Runtime.InteropServices;

namespace TC.Tier.Products.Wal;

/// <summary>
/// TierWAL 快照传输帧头（payload 区每条 entry 一帧）——自描述长度：
/// <c>[PayloadLength 4B][payload]</c>（流式传输无总长前置——导入侧循环读帧，len 非法
/// （footer magic 开头）即进入 Footer 相位）。
/// </summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 4)]
internal struct WalSnapshotFrameHeader
{
    [FieldOffset(0)] internal int PayloadLength;
}
