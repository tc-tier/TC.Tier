namespace TC.Tier.Products.Wal;

/// <summary>
/// TierWAL 恢复 hints——外部主动注入的已知水位（地址级，同 <c>LogRecoveryHints</c> 语义）。
/// <para>★ 地址 = 事实：TierWAL 不解析/不推导逻辑地址结构，hints 直接透传底层 EntryLog
///   （恢复优先级统一：① hints ② meta ③ 扫盘——底层模板裁决）。</para>
/// <para>★ 通常无需提供（default 让底层自恢复）；冷启动已知水位（如进程外归档）时注入。</para>
/// </summary>
public readonly struct WalRecoveryHints
{
    /// <summary>已知写游标（已落盘尾地址）。</summary>
    public LogicalAddress? TailAddress { get; init; }

    /// <summary>已知头截断边界（引擎 MinAddress）。</summary>
    public LogicalAddress? BeginAddress { get; init; }

    /// <summary>已知 commit 边界（仅 EntryLog 接受）。</summary>
    public LogicalAddress? CommittedOffset { get; init; }
}
