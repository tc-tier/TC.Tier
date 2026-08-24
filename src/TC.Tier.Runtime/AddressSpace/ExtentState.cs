namespace TC.Tier.Runtime.AddressSpace;

/// <summary>
/// 段内区间状态。
/// </summary>
internal enum ExtentState : byte
{
    /// <summary>区间已提交（Committed），可读。</summary>
    Committed,

    /// <summary>区间已租用（Leased），正在写入中，尚不可读。</summary>
    Leased,

    /// <summary>区间正在回收（Reclaiming），尚不可读。</summary>
    Reclaiming,

    /// <summary>操作失败——永久洞，不可覆写，需 Compact 消除。</summary>
    Aborted,

    /// <summary>Append 失败——分配了但没写成功，有地址，可被 Write 覆写重复使用。</summary>
    Wasted,
}
