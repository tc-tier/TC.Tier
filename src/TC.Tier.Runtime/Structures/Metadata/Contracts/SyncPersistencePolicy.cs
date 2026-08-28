namespace TC.Tier.Runtime.Structures.Metadata.Contracts;

/// <summary>100% 同步策略——每次 Write 都立即落盘。</summary>
public sealed class SyncPersistencePolicy : IPersistencePolicy
{
    /// <summary>100% 同步策略——任何版本号都立即落盘（恒 true）。</summary>
    /// <param name="version">当前版本号。</param>
    /// <returns>恒为 true（每次 Write 都触发 Persist）。</returns>
    public bool ShouldPersist(long version) => true;
}