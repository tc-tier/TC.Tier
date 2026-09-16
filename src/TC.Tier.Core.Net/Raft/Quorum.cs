namespace TC.Tier.Core.Net.Raft;

/// <summary>
/// 多数派判定（spec-01 §5.3——多数派 = ⌊N/2⌋+1；spec-02 §5 commitIndex 推进依据）。
/// <para>纯函数——活动配置决定集群大小（spec-04：任意时刻多数派恰为当时活动配置的多数派）。</para>
/// </summary>
public static class Quorum
{
    /// <summary>多数派阈值（⌊N/2⌋+1——N=1 → 1；N=2 → 2；N=3 → 2；N=5 → 3）。</summary>
    /// <param name="clusterSize">集群大小 N（须 &gt; 0）。</param>
    /// <returns>构成多数派所需的票数阈值。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="clusterSize"/> 非正。</exception>
    public static int Majority(int clusterSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(clusterSize);
        return clusterSize / 2 + 1;
    }

    /// <summary>granted 是否构成多数派（granted 含自己一票——候选计票 = 1 + 授权应答数）。</summary>
    /// <param name="granted">已收到的授权票数（含候选自己）。</param>
    /// <param name="clusterSize">集群大小 N（须 &gt; 0）。</param>
    /// <returns>true = granted ≥ 多数派阈值。</returns>
    public static bool HasMajority(int granted, int clusterSize) => granted >= Majority(clusterSize);
}
