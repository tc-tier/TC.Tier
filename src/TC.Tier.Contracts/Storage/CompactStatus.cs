namespace TC.Tier.Contracts.Storage;

/// <summary>
/// 表示 Compact 操作的状态。
/// </summary>
public enum CompactStatus
{
    /// <summary>Phase 1 拷贝中（搬迁数据到 .compact 临时文件，可取消）。</summary>
    Copying,
    /// <summary>Phase 2 提交中（rename + 段表 + 处理旧段，不可取消）。</summary>
    Committing,
    /// <summary>成功完成。</summary>
    Completed,
    /// <summary>取消中（正在回滚——删 .compact + 释放锁）。</summary>
    Cancelling,
    /// <summary>失败 / 取消已回滚完成。</summary>
    Faulted,
}