namespace TC.Tier.Runtime.AddressSpace.Leases;

/// <summary>
/// lease 追踪接口——用于诊断和泄漏报告。
/// </summary>
public interface ITrackedLease
{
    /// <summary>lease 唯一标识（诊断用）。</summary>
    Guid Id { get; }

    /// <summary>创建时间戳（TickCount64, ms，诊断用）。</summary>
    long CreatedTimestampMs { get; }

    /// <summary>当前状态：Active/Committed/RolledBack。</summary>
    LeaseState State { get; }

    /// <summary>起始地址。</summary>
    LogicalAddress Start { get; }

    /// <summary>结束地址。</summary>
    LogicalAddress End { get; }

    /// <summary>lease 占住的段 ID 列表（诊断/泄漏报告用）。</summary>
    IEnumerable<int> SegIds { get; }

    /// <summary>回滚 lease（ForceRelease 调——释放占住的区间，防地址空间锁死）。</summary>
    void Rollback();
}
