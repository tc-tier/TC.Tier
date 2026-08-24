namespace TC.Tier.Runtime.Structures.ProbingIndex;

public abstract partial class ProbingIndexBase<TKey> where TKey : unmanaged, IEquatable<TKey>
{
    protected override void DisposeOverride(bool disposing)
    {
    }

    protected override async ValueTask DisposeOverrideAsync(bool disposing)
    {
        await ValueTask.CompletedTask;
    }
}
