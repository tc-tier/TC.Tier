using System.Runtime.InteropServices;

namespace TC.Tier.Runtime.Structures.Log.Contracts;

/// <summary>
/// LogMeta Payload 区结构化首部（4 LogicalAddress = 64B + LastCommittedSeq 8B + LastPreparedSeq 8B = 80B）——水位数据。
/// <para>★ [BinaryLayout] → 源生成器生成 LogMetaPayloadCodec.Write/Read。</para>
/// <para>★ 地址全部 LogicalAddress（承自 spec 27 第一不变量，大小绝不参与）。</para>
/// <para>★ PreparedTailAddress（2PC Abort 回退点）：Prepare 时刻的 pre-prepare 尾快照——
///   本轮事务窗口数据的起点。Abort/恢复裁决据此 TruncateSuffix 回退；Empty = 无待回滚窗口。</para>
/// <para>参见 base.md §3 D。</para>
/// </summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 80)]
public struct LogMetaPayload
{
    /// <summary>头回收边界（= engine.MinAddress）。</summary>
    [FieldOffset(0)]  public LogicalAddress BeginAddress;

    /// <summary>写游标（= engine.AllocatedTail）。</summary>
    [FieldOffset(16)]  public LogicalAddress TailAddress;

    /// <summary>EntryLog group commit 落盘边界（Log 自管，引擎不提供）。</summary>
    [FieldOffset(32)]  public LogicalAddress CommittedOffset;

    /// <summary>当前已提交序号（恢复/运行时读）。-1 = 未参与事务。</summary>
    [FieldOffset(48)]  public long LastCommittedSeq;

    /// <summary>最近一次 Prepare 的 seq。恢复时判定悬空事务用。-1 = 从未 Prepare。</summary>
    [FieldOffset(56)]  public long LastPreparedSeq;

    /// <summary>★ 提交边界尾（2PC Abort 回退点）。Empty = 无既有提交边界（首事务）。
    /// <para>语义：最近一次已确认提交（ConfirmCommitted）对应的尾——其后的追加属于当前事务窗口。
    /// Prepare 随 meta 同块持久化；恢复裁决：LastPreparedSeq &gt; LastCommittedSeq（悬干）→
    /// Abort 按此地址 TruncateSuffix 回退（跨崩溃可用）。</para></summary>
    [FieldOffset(64)]  public LogicalAddress PreparedTailAddress;
}
