namespace TC.Tier.Runtime.Structures.SortedIndex.Contracts;

/// <summary>
/// 索引扫描游标——产出 (key, value 地址) 对。
/// <para>★ 泛型 TKey：key 从 RecordStore 读回（FASTER 判等闭环），类型与索引一致。</para>
/// <para>★ 位置化：先 <see cref="SeekLowerBound"/> 定位再 MoveNext 前向迭代——范围查询
///   [from, to) = Seek(from) + 逐条推进至 key ≥ to 停（无需从头跳过被略键）。</para>
/// </summary>
/// <typeparam name="TKey">key 类型（unmanaged）。</typeparam>
public interface IIndexScanCursor<TKey> : IStructureScanCursor where TKey : unmanaged, IEquatable<TKey>
{
    /// <summary>当前 entry 的 key（从 RecordStore 读回真 key，非占位符）。</summary>
    TKey CurrentKey { get; }

    /// <summary>当前 entry 的 value 逻辑地址（指向 record）。</summary>
    LogicalAddress CurrentValue { get; }

    /// <summary>
    /// 定位到首个 key ≥ <paramref name="key"/> 的条目（lower_bound；可在迭代前或迭代中调用——重新定位）。
    /// </summary>
    /// <param name="key">起始键（含）。</param>
    /// <returns>true = 已定位（Current* 即首个 ≥ key 的条目，MoveNext 产出其后续条目）；
    /// false = 无 ≥ key 的条目（游标置于末尾，后续 MoveNext 恒 false）。</returns>
    bool SeekLowerBound(TKey key);
}
