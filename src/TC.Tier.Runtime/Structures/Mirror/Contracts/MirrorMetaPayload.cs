using TC.Tier.CodeGen;
using TC.Tier.Core.Primitives;
using System.Runtime.InteropServices;

namespace TC.Tier.Runtime.Structures.Mirror.Contracts;

/// <summary>
/// MirrorBase 的 meta 水位 Payload（版本链两端 + tx seq）。
/// <para>PagedMirror 各页链头不经 meta（恢复扫盘按 PageId 重建——meta 只加速水位/seq 裁决）。</para>
/// </summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = PayloadSize)]
public struct MirrorMetaPayload
{
    private const int PayloadSize = 48;

    /// <summary>最高版本号对应地址（单链链头；多链为最后写入 record 地址）。</summary>
    [FieldOffset(0)]  public LogicalAddress HighestVersionAddress;

    /// <summary>最低版本号对应地址（链尾/最老，头截断回收边界）。</summary>
    [FieldOffset(16)] public LogicalAddress LowestVersionAddress;

    /// <summary>当前已提交 seq（2PC）。</summary>
    [FieldOffset(32)] public long LastCommittedSeq;

    /// <summary>最近 Prepare 的 seq（恢复裁决悬干用）。</summary>
    [FieldOffset(40)] public long LastPreparedSeq;
}
