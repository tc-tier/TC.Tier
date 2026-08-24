namespace TC.Tier.Runtime.Structures.SortedIndex;

public partial class SkipListIndex<TKey> where TKey : unmanaged, IEquatable<TKey>
{
    public override long EntryCount => Volatile.Read(ref _entryCount);
    public override long IndexSize => _entryCount * 128L;
}
