namespace TC.Tier.Runtime.Structures.Metadata.Contracts;

/// <summary>
/// 异步后台策略——Write 只更新内存，后台批量落盘。
/// flushInterval=0 表示不自动触发（靠 Prepare/Persist 显式调）。
/// </summary>
public sealed class AsyncPersistencePolicy : IPersistencePolicy
{
    /// <summary>不在 Write 里触发落盘（恒 false）——后台线程批量落盘 / 靠 Prepare、Persist 显式触发。</summary>
    /// <param name="version">当前版本号。</param>
    /// <returns>恒为 false。</returns>
    public bool ShouldPersist(long version) => false;  // 不在 Write 里触发，后台线程批量落盘
}