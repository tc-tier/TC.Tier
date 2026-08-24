namespace TC.Tier.Runtime.Storage;

/// <summary>magic 定位方向（地址序）。</summary>
public enum MagicDirection
{
    /// <summary>地址最小匹配点（最老存活）。</summary>
    First,

    /// <summary>地址最大匹配点（最新）。</summary>
    Last,
}