namespace TC.Tier.Runtime.Structures.Ring.Contracts;

/// <summary>
/// Ring 快照接口——上层导出/导入 Ring 页池数据的统一抽象。
/// <para>★ pull/push 分离：Reader/Writer 分别对应上层 pull/push 模式，支持 GB/TB。</para>
/// <para>★ 对齐 WAL 的 Snapshot 范式（pull/push 模式，IAsyncDisposable）。</para>
/// </summary>
public interface IRingSnapshot
{
    /// <summary>
    /// 创建快照读取器（上层 pull 数据从 Ring 导出）。
    /// </summary>
    /// <param name="begin">快照起始地址。</param>
    /// <param name="end">快照结束地址。</param>
    /// <returns>快照读取器实例。</returns>
    IRingSnapshotReader Reader(LogicalAddress begin, LogicalAddress end);
    /// <summary>
    /// 创建快照写入器（上层 push 数据填回 Ring 页池）。
    /// </summary>
    /// <param name="begin">快照起始地址。</param>
    /// <param name="end">快照结束地址。</param>
    /// <returns>快照写入器实例。</returns>
    IRingSnapshotWriter Writer(LogicalAddress begin, LogicalAddress end);
}