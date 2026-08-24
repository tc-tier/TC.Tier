namespace TC.Tier.Contracts.Structures;

/// <summary>
/// IIndex——索引装配点最小公共协议（TierKV 消费面，设计稿 §3.3）。
/// <para>★ 两族（SortedIndex/ProbingIndex）各自实现，不设公共基类——选族即选消费形态：
///   有序遍历/range scan 选比较族，极省内存点查选探测族。</para>
/// <para>★ 生命周期经 LifecycleBase；恢复重建经 IKeyResolver.ScanAsync 自建（设计稿 §4，
///   拉流循环内聚在族恢复核心）；checkpoint 水位锚点上报随族实现（搭 Ring opaque）。</para>
/// </summary>
public interface IIndex<in TKey> where TKey : unmanaged, IEquatable<TKey>
{
    /// <summary>点查 key → value 的逻辑地址（Empty = 不存在）。</summary>
    LogicalAddress Find(TKey key);

    /// <summary>插入条目（key → valueAddress），返回插入后地址。</summary>
    LogicalAddress Insert(TKey key, LogicalAddress valueAddress, LogicalAddress beginAddress);

    /// <summary>删除条目。</summary>
    bool Delete(TKey key);

    /// <summary>条目数。</summary>
    long EntryCount { get; }

    /// <summary>索引内存占用（字节）。</summary>
    long IndexSize { get; }
}
