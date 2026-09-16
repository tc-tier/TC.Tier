namespace TC.Tier.Runtime.Structures.Ring.Contracts;

/// <summary>
/// Ring 扫描游标工厂委托签名
/// </summary>
/// <param name="beginAddress">扫描起点（default = 从 BeginAddress 开始）。</param>
/// <param name="endAddress">扫描终点（default = 到当前尾）。</param>
/// <returns>覆盖 [beginAddress, endAddress) 区间的扫描游标实例。</returns>
public delegate TRingScanCursor RingCursorFactory<out TRingScanCursor>(LogicalAddress beginAddress, LogicalAddress endAddress)
    where TRingScanCursor : IRingScanCursor;
/// <summary>
/// Ring 快照读写器工厂委托签名
/// </summary>
/// <param name="begin">导出区间起始逻辑地址。</param>
/// <param name="end">导出区间结束逻辑地址（不含）。</param>
/// <returns>覆盖 [begin, end) 区间的快照读取器实例。</returns>
public delegate IRingSnapshotReader RingSnapshotReaderFactory(LogicalAddress begin, LogicalAddress end);
/// <summary>
/// Ring 快照写入器工厂委托签名
/// </summary>
/// <param name="begin">导入区间起始逻辑地址。</param>
/// <param name="end">导入区间结束逻辑地址。</param>
/// <returns>覆盖 [begin, end) 区间的快照写入器实例。</returns>
public delegate IRingSnapshotWriter RingSnapshotWriterFactory(LogicalAddress begin, LogicalAddress end);
