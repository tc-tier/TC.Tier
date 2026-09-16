namespace TC.Tier.Core.Net.Raft;

/// <summary>
/// 可重建 apply 管道标记（spec-03——follower 快照安装后重建业务状态：导入 → 重放快照帧流
/// → appliedIndex 重置到 N₀ → 增量接管）。ApplyPipeline 实现。
/// </summary>
public interface IRebuildableApplySink
{
    /// <summary>快照导入完成后重建业务状态（调用方保证存储层导入已完成——本方法重放帧流 + 增量）。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask RebuildFromSnapshotAsync(CancellationToken cancellationToken = default);
}
