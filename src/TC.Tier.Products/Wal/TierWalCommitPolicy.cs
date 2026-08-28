using TC.Tier.Core.Logging;
using TC.Tier.Runtime.Structures.Log;

namespace TC.Tier.Products.Wal;

/// <summary>
/// TierWAL 组提交策略——三维度阈值（Options 映射；GroupCommitPolicy 语义同构）。
/// <para>★ 0 值 = 该维度立即满足（每次 Append 触发）；-1ms = 禁用时间维度。</para>
/// </summary>
internal sealed class GroupCommitThresholdPolicy : ICommitPolicy
{
    private readonly long _maxUnflushedBytes;
    private readonly int _maxUnflushedCount;
    private readonly TimeSpan _interval;

    public GroupCommitThresholdPolicy(TierWalOptions options)
    {
        _maxUnflushedBytes = options.MaxUnflushedBytes;
        _maxUnflushedCount = options.MaxUnflushedCount;
        _interval = options.CommitInterval;
    }

    public bool ShouldCommit(in CommitSnapshot s) =>
        s.UnflushedBytes >= _maxUnflushedBytes ||
        (_interval != TimeSpan.FromMilliseconds(-1) && s.SinceLastCommit >= _interval) ||
        s.UnflushedCount >= _maxUnflushedCount;
}

/// <summary>
/// opaque 登记器——TierWAL 与提交策略间的接线（策略在 EntryLog 构造期创建，TierWAL 在恢复后构造；
/// Attach 前 no-op——启动期间不会触发提交策略）。
/// </summary>
internal sealed class OpaqueStager(ILogger? logger)
{
    private TierWal? _wal;
    private readonly ILogger? _logger = logger;

    public void Attach(TierWal wal) => _wal = wal;

    /// <summary>stage opaque（提交前序列化容器 → SetOpaqueMeta 随水位落盘）。</summary>
    public void Stage()
    {
        if (_wal is null) return;   // 启动期间（TierWal 未构造）无提交路径
        try
        {
            _wal.StageOpaque();
        }
        catch (Exception ex)
        {
            // ★ stage 失败冒泡给 EntryLog 提交链（LastCommitError 查询面）——不静默吞
            _logger?.LogWarning(ex, "TierWAL stage opaque 失败——提交链将冒泡");
            throw;
        }
    }

    /// <summary>自动提交水位推进（同步链——stage 后 EntryLog 立即落盘）。</summary>
    public void OnAutoCommitted()
    {
        if (_wal is null) return;
        _wal.OnAutoCommitted();
    }
}

/// <summary>
/// 提交策略包装——inner 判定触发时先 stage opaque（容器随本次水位提交原子落盘）。
/// <para>★ 使 opaque 与 EntryLog 所有自动提交路径对齐（OnAppended 提前提交 + 后台时间循环），
///   显式 <see cref="TierWal.CommitAsync"/> 自 stage——三层提交面 opaque 恒最新。</para>
/// </summary>
internal sealed class OpaqueStagingCommitPolicy(OpaqueStager stager, ICommitPolicy inner) : ICommitPolicy
{
    public bool ShouldCommit(in CommitSnapshot snapshot)
    {
        if (!inner.ShouldCommit(snapshot)) return false;
        stager.Stage();           // ★ writeLock 内（OnAppended）——stage 只做序列化 + SetOpaqueMeta（策略缓冲锁内）
        stager.OnAutoCommitted(); // ★ 同步提交链（stage 后紧随落盘）——推进 raft 可应答水位
        return true;
    }
}
