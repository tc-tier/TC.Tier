using TC.Tier.Core.Logging;
using TC.Tier.Core.Net.Channels;
using TC.Tier.Core.Net.Swarm;

namespace TC.Tier.Core.Net.Raft;

/// <summary>
/// 快照多源传输（spec-12 §6.1 × spec-03 §2——ISnapshotTransfer 的 swarm 数据面实现：
/// 导出 = 块化当前快照发布供拉取 + 握手协调数据（manifest + holders）；导入 = 按握手面
/// 收到的协调数据多源拉取重装导入 + 完成后向 source 上报持有（多源从字面成立））。
/// <para>★ 时序（与单源同构）：leader 先导出（发布块源）→ 再发 InstallSnapshotReq 握手
/// （manifest+holders 随 RPC）→ follower 多源拉取导入 → 应答；产物与单源流式路径逐字节
/// 一致（对拍契约见测试）。</para>
/// <para>★ 持有者表：leader 导出 = 换代 <see cref="SwarmSync.ResetHolders(TC.Tier.Core.Net.Swarm.SwarmContentDomain, TC.Tier.Core.Net.Opaque16)"/>（清表+登记本端）→
/// Holders = <see cref="SwarmSync.GetHolders(TC.Tier.Core.Net.Swarm.SwarmContentDomain, TC.Tier.Core.Net.Opaque16)"/>（含本端兜底）；follower 导入完成 = Announce
/// 上报（spec-12 §6.1 增量——多源候选由持有表供给，块内容寻址 + CRC32C 校验保证任意
/// 持有者副本等价）。</para>
/// </summary>
public sealed class SnapshotSwarmTransfer : ISnapshotTransfer
{
    private readonly IProtocolTransport _transport;
    private readonly SnapshotSwarmSync _sync;
    private readonly IRaftStore _store;
    private readonly int _blockSize;
    private readonly ILogger? _logger;

    /// <summary>构造（零 IO——SwarmSync 已由装配面 StartAsync 挂载 0x03 取块 handler）。</summary>
    /// <param name="transport">节点端点完整面（Self = 持有者身份）。</param>
    /// <param name="sync">快照多源同步装配件（复用装配面的 SwarmSync——本件不自行挂载）。</param>
    /// <param name="store">存储端口（快照条目源 / 导入目标）。</param>
    /// <param name="blockSize">块大小（≤ 线协议防御界——运行参数装配期传入）。</param>
    /// <param name="logger">日志（可选）。</param>
    public SnapshotSwarmTransfer(IProtocolTransport transport, SnapshotSwarmSync sync, IRaftStore store,
        int blockSize, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(sync);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSize);
        _transport = transport;
        _sync = sync;
        _store = store;
        _blockSize = blockSize;
        _logger = logger;
    }

    /// <summary>SwarmSync（持有表/上报面——装配面换届清表接线的对接点）。</summary>
    public SwarmSync Swarm => _sync.Swarm;

    /// <inheritdoc/>
    public async ValueTask<SnapshotCoordination?> ExportSnapshotAsync(NodeId target, CancellationToken cancellationToken = default)
    {
        // ★ 快照创建 = 本实现策略（与单源同构）：导出前把快照点推进到已持久化尾（快照 =
        //   已持久化日志前缀——只含已提交数据）
        await _store.TruncatePrefixToAsync(_store.PersistedIndex, cancellationToken).ConfigureAwait(false);
        var n0 = _store.SnapshotIndex;

        var manifest = await _sync.PublishSnapshotAsync(n0, _blockSize, cancellationToken).ConfigureAwait(false);
        // ★ 换代清表（spec-12 §6.1 增量——新 manifest = 新代：旧代 Announce 迟到不污染新代表）
        _sync.Swarm.ResetHolders(manifest.Id);
        var coordination = new SnapshotCoordination
        {
            ManifestId = manifest.Id,
            BlockSize = manifest.BlockSize,
            TotalBytes = manifest.TotalBytes,
            Checksums = manifest.Checksums,
            Holders = [.. _sync.Swarm.GetHolders(manifest.Id)],   // 含本端（空表兜底 [本端]）
        };
        _logger?.LogDebug("快照多源导出：target={Target} n0={N0} blocks={Count} holders={Holders}",
            target, n0, manifest.BlockCount, coordination.Holders.Length);
        return coordination;
    }

    /// <inheritdoc/>
    /// <param name="source">导出方（当前 leader——安装完成后向其上报持有）。</param>
    /// <param name="coordination">快照握手协调数据（null = 形态混装——抛 InvalidOperationException）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>完成时快照已多源拉取并导入存储，且已向 source 上报持有（尽力）。</returns>
    /// <exception cref="InvalidOperationException"><paramref name="coordination"/> 为 null（swarm 导入方要求非空协调数据）。</exception>
    public async ValueTask ImportSnapshotAsync(NodeId source, SnapshotCoordination? coordination, CancellationToken cancellationToken = default)
    {
        if (coordination is null)
            throw new InvalidOperationException(
                "swarm 形态导入收到空协调数据（两形态混装——集群装配须一致：swarm 导入方要求 InstallSnapshot RPC 携带 manifest+holders）。");
        await _sync.InstallSnapshotAsync(coordination.ToManifest(), coordination.Holders, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        // ★ 安装完成上报（spec-12 §6.1 增量——SetSource 切换到本地导入产物后本端已能服务
        //   GetBlock；向 source（= 当前 leader）单边声明持有，多源从字面成立；尽力送达——
        //   丢失由换届重报覆盖）
        await _sync.Swarm.AnnounceSourceAsync(source, coordination.ManifestId, cancellationToken).ConfigureAwait(false);
    }
}
