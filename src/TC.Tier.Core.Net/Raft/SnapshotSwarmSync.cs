using TC.Tier.Core.Logging;
using TC.Tier.Core.Net.Swarm;

namespace TC.Tier.Core.Net.Raft;

/// <summary>
/// 快照多源同步装配（spec-12 §6.1 消费者——SwarmSync 首消费件）：把快照条目流块化
/// （<see cref="SnapshotBlockizer"/>）后走多源并行拉取；与单源流式路径
/// （<see cref="SnapshotStreamTransfer"/>）共享同一存储端口（IRaftStore 快照读/导入面），
/// 产物（目标快照区）逐字节一致——对拍契约见测试。
/// <para>★ 角色对称（spec-12 §4 对称节点）：每节点既可作为 holder（<see cref="PublishSnapshotAsync"/>
/// 发布块化内容供拉取）也可作为下载方（<see cref="InstallSnapshotAsync"/> 多源拉取重装导入）；
/// holder 列表与 manifest 的交换是装配/握手面（InstallSnapshot RPC 携带——归 T6 引擎接线），
/// 本件只提供机制（发布 → 取 manifest；收到 manifest + holders → 安装）。</para>
/// <para>★ 下载侧重装 O(快照字节) 缓冲（无序块按几何偏移落位后顺序解析导入——导入面仍逐条流式消费）；
/// 传输线经 <see cref="SwarmSync"/> 逐块流式（O(块) 驻留）。</para>
/// </summary>
public sealed class SnapshotSwarmSync
{
    private readonly SwarmSync _swarm;
    private readonly IRaftStore _store;
    private readonly ILogger? _logger;

    /// <summary>多源同步组件（持有表/上报面——装配面换届清表接线的对接点）。</summary>
    public SwarmSync Swarm => _swarm;

    /// <summary>
    /// 基线上报（spec-12 §6.1 增量 S4——触发②启动恢复 / 触发③换届重报共用）：
    /// 本端恢复/持有快照基线（SnapshotIndex &gt; 0）时，向已知 leader 单边声明
    /// （manifest Id = 覆盖点确定性映射——与发布侧同代；消息幂等，重复无害）。
    /// <para>★ 无基线/未装配上报面 = 空操作；尽力送达——失败由下次上报覆盖。</para>
    /// </summary>
    /// <param name="leader">上报目标（当前已知 leader——未知时由装配面等 LeaderChanged 后补发）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>完成时上报已尽力送达（无基线/未装配上报面 = 空操作立即完成）。</returns>
    public async Task AnnounceBaselineAsync(NodeId leader, CancellationToken cancellationToken = default)
    {
        var n0 = _store.SnapshotIndex;
        if (n0 <= 0) return;   // 无基线——无从上报（多源机制的成员资格 = 持有内容）
        await _swarm.AnnounceSourceAsync(leader, SnapshotBlockizer.ManifestIdFor(n0), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>构造（零 IO——不自行挂载/启动，复用装配面已挂载的 <see cref="SwarmSync"/>）。</summary>
    /// <param name="swarm">多源同步组件（已 StartAsync——取块 handler 挂载 0x03）。</param>
    /// <param name="store">存储端口（快照条目源 / 导入目标）。</param>
    /// <param name="logger">日志（可选）。</param>
    public SnapshotSwarmSync(SwarmSync swarm, IRaftStore store, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(swarm);
        ArgumentNullException.ThrowIfNull(store);
        _swarm = swarm;
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// 发布快照（holder/source 侧）：块化当前快照 → 挂到 <see cref="SwarmSync.SetSource"/> 供拉取。
    /// </summary>
    /// <param name="snapshotIndex">快照覆盖点 N₀（读 [1..N₀] 条目）。</param>
    /// <param name="blockSize">块大小（≤ 线协议防御界）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>块清单（装配面携给下载方——manifest Id = 覆盖点承载）。</returns>
    public async ValueTask<SwarmManifest> PublishSnapshotAsync(long snapshotIndex, int blockSize, CancellationToken cancellationToken = default)
    {
        var content = await SnapshotBlockizer.BuildContentAsync(_store, snapshotIndex, blockSize, cancellationToken)
            .ConfigureAwait(false);
        _swarm.SetSource(new SnapshotSwarmBlockSource(content));
        _logger?.LogDebug("快照多源发布：snapshot={Index} blocks={Count} bytes={Bytes}",
            snapshotIndex, content.Manifest.BlockCount, content.Manifest.TotalBytes);
        return content.Manifest;
    }

    /// <summary>
    /// 安装快照（下载方侧）：多源拉取块 → 按几何偏移重装帧化字节流 → 逐条解析 → 导入存储。
    /// </summary>
    /// <param name="manifest">块清单（权威方供给——holder 列表随握手面给出）。</param>
    /// <param name="holders">持有者列表（可含本端）。</param>
    /// <param name="onBlockCompleted">块完成回调（块号 + 来源——块源归属观测；null = 无）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>完成时全部块已拉取重装并逐条导入存储（单块全源耗尽抛 <see cref="SwarmDownloadException"/>）。</returns>
    public async ValueTask InstallSnapshotAsync(SwarmManifest manifest, IReadOnlyList<NodeId> holders,
        Action<long, NodeId>? onBlockCompleted = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(holders);
        var content = await DownloadAsync(manifest, holders, onBlockCompleted, cancellationToken).ConfigureAwait(false);
        var snapshotIndex = SnapshotBlockizer.SnapshotIndexOf(manifest.Id);
        await _store.ImportSnapshotAsync(snapshotIndex, SnapshotBlockizer.ParseEntriesAsync(content, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>多源拉取并重装（无序块按几何偏移落位——O(快照字节) 缓冲）。</summary>
    private async ValueTask<byte[]> DownloadAsync(SwarmManifest manifest, IReadOnlyList<NodeId> holders,
        Action<long, NodeId>? onBlockCompleted, CancellationToken cancellationToken=default)
    {
        if (manifest.TotalBytes > int.MaxValue)
            throw new InvalidOperationException(
                $"快照内容 {manifest.TotalBytes} 字节超单块内存重装上限 {int.MaxValue}（磁盘背衬形态后续接 Core.IO）。");
        var content = new byte[manifest.TotalBytes];
        await foreach (var (index, block) in _swarm.DownloadAsync(manifest, holders, onBlockCompleted, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            block.CopyTo(content.AsSpan((int)manifest.BlockOffset(index)));
        }
        return content;
    }
}
