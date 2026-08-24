namespace TC.Tier.Runtime.Storage.Checkpoint;

/// <summary>
/// Checkpoint 门面——主引擎只认这个接口，不感知内部 Coordinator 实现。
/// <para>★ 设计裁定（2026-08-21，用户）：**默认提供扫描，自定义存储需要自己注入**——引擎默认装配
///   扫盘只读切面（<see cref="ScanCheckpoint"/>：HasSnapshot 恒 false、Writer 返 NoopWriter 接口占位、
///   Reader 扫盘流式重建）；需要快照加速/自定义持久化的存储方，注入自己的 <see cref="ICheckpoint"/>
///   实现（带真 Writer，快照导出/恢复）。</para>
/// <para>★ Writer/Reader：段表导出/恢复的公开协议。</para>
/// <para>★ HasSnapshot()：是否有预存快照可加速恢复（false → 回退扫盘）。</para>
/// <para>★ 自定义切面：直接实现本接口（不需要碰 internal 的 Coordinator）。</para>
/// </summary>
public interface ICheckpoint : IDisposable, IAsyncDisposable
{
    /// <summary>段表写入器（快照导出）。扫盘切面返 NoopWriter（空操作）。</summary>
    IAddressTableWriter Writer { get; }

    /// <summary>段表读取器（恢复输入）。扫盘切面返 StreamingSegmentReader，快照切面返 CoordinatorReader。</summary>
    IAddressTableReader Reader { get; }
    /// <summary>是否有预存快照可加速恢复。false = 回退扫盘。</summary>
    bool HasSnapshot { get; }
}
