namespace TC.Tier.Runtime.Structures.Metadata.Contracts;

/// <summary>
/// 数据落盘策略——决定 Write() 后何时触发 Persist（落盘）。
/// 与 meta 4 模式正交（meta 是水位持久化，这是数据 record 落盘时机）。
/// </summary>
public interface IPersistencePolicy
{
    /// <summary>Write(version) 后判定是否立即 Persist（落盘）。</summary>
    bool ShouldPersist(long version);
}