using System.Runtime.InteropServices;

namespace TC.Tier.Runtime.Structures.Ring.Contracts;

/// <summary>
/// RingMeta Payload 区结构化首部——持久化层 5 指针(LogicalAddress) + LastCommittedSeq + LastPreparedSeq + OverflowTail + CommittedTail。
/// <para>★ 全 LogicalAddress（base.md §2.3/§2.7）——大小不参与地址，根除位打包毒点。</para>
/// <para>★ [BinaryLayout] → 源生成器生成 RingMetaPayloadCodec.Write/Read。</para>
/// <para>★ HeadAddress/SafeHeadAddress（内存水位层）不存——恢复时从 BeginAddress 重建。</para>
/// <para>★ ClosedUntilAddress（簿记层）不存——恢复时初始化为 SafeHeadAddress。</para>
/// <para>★ CommittedTailAddress（D2 Abort 回退点）：Prepare 开窗时的尾快照——本轮悬干数据之前的
///   最后边界（= 上一已确认提交对应的尾）。Abort/恢复裁决据此 TruncateSuffix 回退；Empty = 无待回滚窗口。</para>
/// <para>★ public：IRingMetaPolicy 为 public 接口，参数/返回类型须同级可见（CS0050/CS0051）。</para>
/// <para>参见 base.md §2.7。</para>
/// </summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = PayloadSize)]
public struct RingMetaPayload
{
    /// <summary>6 LogicalAddress(16B each) + LastCommittedSeq(8B) + LastPreparedSeq(8B) + OverflowTailAddress(16B) + KeySize(4B) = 132B。</summary>
    private const int PayloadSize = 132;
    /// <summary>数据区起点（FieldOffset 0）——恢复锚点（GetDistance 原点，内存水位从它重建）。</summary>
    [FieldOffset(0)]   public LogicalAddress BeginAddress;
    /// <summary>落盘边界水位（FieldOffset 16）——此地址前的数据已写引擎，恢复可信数据上限。</summary>
    [FieldOffset(16)]  public LogicalAddress FlushedUntilAddress;
    /// <summary>安全只读水位（FieldOffset 32）——epoch 保护下的 readonly 区推进目标。</summary>
    [FieldOffset(32)]  public LogicalAddress SafeReadOnlyAddress;
    /// <summary>只读水位（FieldOffset 48）——此地址前页面冻结，等待 flush 后驱逐。</summary>
    [FieldOffset(48)]  public LogicalAddress ReadOnlyAddress;
    /// <summary>写游标（FieldOffset 64）——下一条 record 的分配地址。</summary>
    [FieldOffset(64)]  public LogicalAddress TailAddress;
    /// <summary>最近一次已确认提交（ConfirmCommitted）的 seq（FieldOffset 80）。-1 = 未参与事务。</summary>
    [FieldOffset(80)]  public long LastCommittedSeq;
    /// <summary>最近一次 Prepare 的 seq。恢复时判定悬空事务用。-1 = 从未 Prepare。</summary>
    [FieldOffset(88)]  public long LastPreparedSeq;
    /// <summary>溢出引擎写游标（LogicalAddress）；未启用溢出时为 Empty。</summary>
    [FieldOffset(96)]  public LogicalAddress OverflowTailAddress;
    /// <summary>★ key 定长字节数（sizeof(TKey)）盘上锚点——打开时与实例类型校验，
    /// 防拿错特化（如 RingOfLong 开 RingOfGuid 的卷）静默错乱（设计稿 §1.3）。</summary>
    [FieldOffset(112)] public int KeySize;
    /// <summary>★ 提交边界尾（D2 Abort 回退点）。Empty = 无既有提交边界（首事务）。
    /// <para>语义：最近一次已确认提交（ConfirmCommitted）对应的尾——其后的写入属于当前事务窗口。
    /// Prepare 随 meta 同块持久化；恢复裁决：LastPreparedSeq &gt; LastCommittedSeq（悬干）→
    /// Abort 按此地址 TruncateSuffix 回退（跨崩溃可用）。</para></summary>
    [FieldOffset(116)] public LogicalAddress CommittedTailAddress;
}
