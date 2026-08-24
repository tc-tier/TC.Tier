namespace TC.Tier.Runtime.Storage.Compact;

/// <summary>
/// Compact 类型——Full 全量整理 / Keep 区间整理。
/// </summary>
public enum CompactType : byte
{
    /// <summary>全量 Compact：[MinAddress, CommittedTail] 全部已提交数据搬迁。</summary>
    Full = 0,
    /// <summary>区间 Compact：keepRanges 指定的多个区间线性合并。</summary>
    Keep = 1,
    /// <summary>RangeCompact：同段号临时镜像组原子晋升。</summary>
    Range = 2,
}
