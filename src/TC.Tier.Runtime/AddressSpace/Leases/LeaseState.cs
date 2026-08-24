namespace TC.Tier.Runtime.AddressSpace.Leases;

/// <summary>操作 lease 状态——CAS 守护。终态（Committed/RolledBack/Finalized）语义等价：Finalize 已收敛、Extents 已释放；Dispose 守卫只判 != Active。</summary>
public enum LeaseState
{
    /// <summary>
    /// Lease 处于活跃状态，尚未提交或回滚。
    /// </summary>
    Active = 0,
    /// <summary>
    /// Lease 已提交（全员 chunk 走提交路径——最后增量者触发）。
    /// </summary>
    Committed = 1,
    /// <summary>
    /// Lease 已回滚（整体 Rollback 触发）。
    /// </summary>
    RolledBack = 2,
    /// <summary>
    /// 混合方向终态——部分 chunk 提交、部分回滚，回滚者补满 doneMask 时自动收敛。
    /// </summary>
    Finalized = 3,
}