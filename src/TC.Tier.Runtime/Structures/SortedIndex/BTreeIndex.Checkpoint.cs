namespace TC.Tier.Runtime.Structures.SortedIndex;

/// <summary>
/// BTreeIndex 计数 partial——EntryCount（Volatile 读）与 IndexSize 估算。
/// </summary>
public partial class BTreeIndex<TKey> where TKey : unmanaged, IEquatable<TKey>
{
    /// <summary>条目数（Volatile 读——写者维护计数）。</summary>
    public override long EntryCount => Volatile.Read(ref _entryCount);

    /// <summary>索引内存占用估算（驻留节点数 × 节点尺寸——真实计数，无上限驻留缓存下即全量节点）。</summary>
    public override long IndexSize => (long)_nodeCache.Count * _nodeSize;
}
