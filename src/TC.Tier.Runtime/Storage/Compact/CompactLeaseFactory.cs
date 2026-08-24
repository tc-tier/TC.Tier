namespace TC.Tier.Runtime.Storage.Compact;

/// <summary>
/// Compact lease 构造委托——子系统不认识 <see cref="SegmentTable"/>、自己造不了 lease；
/// 恢复/续传时把 marker 记录的区间交给此委托，由基类（引擎）造出新 lease。
/// </summary>
/// <param name="start">整理区间起始地址。</param>
/// <param name="end">整理区间结束地址。</param>
/// <returns>地址分配器造出的新 Compact lease。</returns>
public delegate CompactLease CompactLeaseFactory(LogicalAddress start, LogicalAddress end);