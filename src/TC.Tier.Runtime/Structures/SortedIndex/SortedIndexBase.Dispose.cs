namespace TC.Tier.Runtime.Structures.SortedIndex;

public abstract partial class SortedIndexBase<TKey> where TKey : unmanaged, IEquatable<TKey>
{
    protected override void DisposeOverride(bool disposing)
    {
    }

    protected override async ValueTask DisposeOverrideAsync(bool disposing)
    {
        await ValueTask.CompletedTask;
    }
}
