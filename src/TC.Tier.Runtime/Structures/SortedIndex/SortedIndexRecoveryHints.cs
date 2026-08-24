namespace TC.Tier.Runtime.Structures.SortedIndex;

/// <summary>
/// 比较族恢复 hints——重放窗口 [Begin, End)（设计稿 §4 两段式：组合层锚点触发 + index 自建）。
/// <para>★ 组合层职责：恢复 Ring 后从 opaque 取锚点 W（无锚点/损坏 → W = Ring BeginAddress），
///   连同 Ring 尾一起经 <c>Initialize(hints)</c> 注入——结构层水位正位通道。</para>
/// <para>★ 默认（无窗口）= 空结构首开，不重放；降级全量重建不是第二条路——W=Begin 走同一条 ScanAsync 路径。</para>
/// </summary>
public readonly struct SortedIndexRecoveryHints(LogicalAddress begin, LogicalAddress end)
{
    public LogicalAddress Begin { get; } = begin;
    public LogicalAddress End { get; } = end;

    /// <summary>窗口有效性——End &gt; Begin 才重放（默认 default hints = Empty/Empty = 不重放）。</summary>
    public bool HasReplayWindow => End > Begin;
}
