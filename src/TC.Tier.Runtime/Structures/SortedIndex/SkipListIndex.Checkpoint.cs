namespace TC.Tier.Runtime.Structures.SortedIndex;

public partial class SkipListIndex<TKey> where TKey : unmanaged, IEquatable<TKey>
{
    /// <summary>条目数（Volatile 读——写者维护计数）。</summary>
    public override long EntryCount => Volatile.Read(ref _entryCount);

    /// <summary>索引内存占用估算（字节——条目数 × 节点均值 128B）。</summary>
    public override long IndexSize => _entryCount * 128L;
}
