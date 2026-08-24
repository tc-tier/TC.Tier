namespace TC.Tier.Contracts.Storage;

/// <summary>
/// 表示快照模式的枚举类型，用于指定数据读取时的锁定策略。
/// </summary>
public enum SnapshotMode
{
    /// <summary>
    /// 区间读锁：扫描前锁定整个 [start,end)，数据完全不变。
    /// <para>用于冷启动恢复、备份、Compact 数据搬迁。</para>
    /// </summary>
    Consistent,

    /// <summary>
    /// 游标读锁：每次 Read 只锁当前段，读完释放。
    /// <para>允许脏读（两次 Read 间数据可被 Write/PunchHole 修改），
    /// 用于在线巡检、统计扫描。</para>
    /// </summary>
    DirtyRead,
}