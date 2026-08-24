namespace TC.Tier.Runtime.Structures.Log;

/// <summary>
/// Log 恢复提示（上层注入的已知水位，加速恢复）。
/// <para>★ Log 专属，顺序追加日志语义。三级回退：本 hints → engine 水位 → OpenCursor 扫盘验帧。</para>
/// </summary>
public readonly struct  LogRecoveryHints
{
    /// <summary>已知写游标（上层快照场景注入；优先于扫盘）。</summary>
    public LogicalAddress? TailAddress { get; init; }

    /// <summary>已知头截断边界（retention 场景注入）。</summary>
    public LogicalAddress? BeginAddress { get; init; }

    /// <summary>已知落盘边界。</summary>
    public LogicalAddress? FlushedUntilAddress { get; init; }

    /// <summary>已知 commit 边界（EntryLog 场景注入）。</summary>
    public LogicalAddress? CommittedOffset { get; init; }

    /// <summary>已知文件大小（非地址，保留 long）。</summary>
    public long? FileSize { get; init; }
}
