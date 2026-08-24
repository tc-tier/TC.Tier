namespace TC.Tier.Runtime.Structures.Metadata.Contracts;

/// <summary>100% 同步策略——每次 Write 都立即落盘。</summary>
public sealed class SyncPersistencePolicy : IPersistencePolicy
{
    public bool ShouldPersist(long version) => true;
}