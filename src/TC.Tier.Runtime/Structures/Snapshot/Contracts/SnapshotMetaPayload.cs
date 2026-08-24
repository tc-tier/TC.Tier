using TC.Tier.CodeGen;
using TC.Tier.Core.Primitives;
using System.Runtime.InteropServices;

namespace TC.Tier.Runtime.Structures.Snapshot.Contracts;

/// <summary>
/// SnapshotBase 的 meta 水位 Payload（三水位 + 提交水位 + tx seq）。
/// <para>★ CommittedWriteAddress 为 Abort 新增：ConfirmCommitted 时的 WriteAddress，Abort 尾截断到此处。</para>
/// </summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = PayloadSize)]
public struct SnapshotMetaPayload
{
    private const int PayloadSize = 80;

    /// <summary>逻辑写尾（非对齐）。</summary>
    [FieldOffset(0)]  public LogicalAddress WriteAddress;

    /// <summary>物理写尾（扇区对齐）。</summary>
    [FieldOffset(16)] public LogicalAddress PhysicalWriteAddress;

    /// <summary>逻辑截断点。</summary>
    [FieldOffset(32)] public LogicalAddress TruncatedAddress;

    /// <summary>★ Abort 回退点：ConfirmCommitted 时的 WriteAddress。</summary>
    [FieldOffset(48)] public LogicalAddress CommittedWriteAddress;

    /// <summary>当前已提交 seq（2PC）。</summary>
    [FieldOffset(64)] public long LastCommittedSeq;

    /// <summary>最近 Prepare 的 seq。</summary>
    [FieldOffset(72)] public long LastPreparedSeq;
}
