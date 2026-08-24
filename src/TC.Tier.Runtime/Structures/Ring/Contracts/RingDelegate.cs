namespace TC.Tier.Runtime.Structures.Ring.Contracts;

/// <summary>
/// Ring 扫描游标工厂委托签名
/// </summary>
public delegate TRingScanCursor RingCursorFactory<out TRingScanCursor>(LogicalAddress beginAddress, LogicalAddress endAddress)
    where TRingScanCursor : IRingScanCursor;
/// <summary>
/// Ring 快照读写器工厂委托签名
/// </summary>
public delegate IRingSnapshotReader RingSnapshotReaderFactory(LogicalAddress begin, LogicalAddress end);
/// <summary>
/// Ring 快照写入器工厂委托签名
/// </summary>
public delegate IRingSnapshotWriter RingSnapshotWriterFactory(LogicalAddress begin, LogicalAddress end);
