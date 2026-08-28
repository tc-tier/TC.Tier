namespace TC.Tier.Runtime.Structures.SortedIndex;

public partial class BTreeIndex<TKey> where TKey : unmanaged, IEquatable<TKey>
{
    /// <summary>条目数（Volatile 读——写者维护计数）。</summary>
    public override long EntryCount => Volatile.Read(ref _entryCount);

    /// <summary>索引内存占用估算（字节——节点容量 × 1024 常数估算）。</summary>
    public override long IndexSize => _nodeSize * 1024L;
}
