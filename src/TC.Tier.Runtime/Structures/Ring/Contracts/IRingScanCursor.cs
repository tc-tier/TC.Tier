namespace TC.Tier.Runtime.Structures.Ring.Contracts;

/// <summary>
/// Ring 扫描游标接口——继承 Shared <see cref="IStructureScanCursor"/>，扩展 Ring 专属地址/header 成员。
/// <para>★ 全 LogicalAddress（base.md §2.2）。</para>
/// </summary>
public interface IRingScanCursor : IStructureScanCursor
{
    LogicalAddress CurrentAddress { get; }
    LogicalAddress NextAddress { get; }
    LogicalAddress BeginAddress { get; }
    LogicalAddress EndAddress { get; }
    RingRecordFields GetFields();
    int CurrentRecordSize { get; }
}
