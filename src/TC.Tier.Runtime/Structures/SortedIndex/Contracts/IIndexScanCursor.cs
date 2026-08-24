namespace TC.Tier.Runtime.Structures.SortedIndex.Contracts;

/// <summary>
/// 索引扫描游标——产出 (key, value 地址) 对。
/// <para>★ 泛型 TKey：key 从 RecordStore 读回（FASTER 判等闭环），类型与索引一致。</para>
/// </summary>
/// <typeparam name="TKey">key 类型（unmanaged）。</typeparam>
public interface IIndexScanCursor<out TKey> : IStructureScanCursor where TKey : unmanaged, IEquatable<TKey>
{
    /// <summary>当前 entry 的 key（从 RecordStore 读回真 key，非占位符）。</summary>
    TKey CurrentKey { get; }

    /// <summary>当前 entry 的 value 逻辑地址（指向 record）。</summary>
    LogicalAddress CurrentValue { get; }
}
