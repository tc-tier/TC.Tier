using TC.Tier.CodeGen;
using TC.Tier.Core.Primitives;
using System.Runtime.InteropServices;

namespace TC.Tier.Runtime.Structures.Metadata.Contracts;

/// <summary>
/// MetadataBase 的 meta 水位 Payload（版本链两端 + tx seq）。
/// <para>开启 meta 时 O(1) 查 HighestVersionAddress/LowestVersionAddress 做截断定位；关闭扫盘按版本号定位。</para>
/// </summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = PayloadSize)]
public struct MetadataMetaPayload
{
    private const int PayloadSize = 48;

    /// <summary>最高版本号对应地址（链头/当前版本）。</summary>
    [FieldOffset(0)]  public LogicalAddress HighestVersionAddress;

    /// <summary>最低版本号对应地址（链尾/最老版本，头截断回收边界）。</summary>
    [FieldOffset(16)] public LogicalAddress LowestVersionAddress;

    /// <summary>当前已提交 seq（2PC）。</summary>
    [FieldOffset(32)] public long LastCommittedSeq;

    /// <summary>最近 Prepare 的 seq（恢复裁决悬干用）。</summary>
    [FieldOffset(40)] public long LastPreparedSeq;
}
