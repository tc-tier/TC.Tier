using TC.Tier.Core.Logging;
using TC.Tier.Core.Net.Wire;

namespace TC.Tier.Core.Net.Swarm;

/// <summary>
/// Swarm 反熵对账（spec-12 §6.1 增量——复制家族设计件 C：把"分发"升级为"持续收敛"）。
/// <para>★ 单轮形态（<see cref="RunOnceAsync"/>）：全局根比对（相等 = 整体一致零块传输）→
/// 不等 = 叶子层区间批量比对定位差异块 → 经 <see cref="SwarmSync.DownloadBlocksAsync"/> 定向
/// 只拉差异块（校验和+换源全复用）→ 写回本地可变源（<see cref="ISwarmBlockSource.TryWriteBlock"/>）。
/// 只读比对协议（EntropyProbe）零写路径；修复块过 <see cref="SwarmManifest.VerifyBlock"/> 防线。</para>
/// <para>★ 触发归装配面：周期（<see cref="SwarmOptions.AntiEntropyInterval"/>，缺省关闭）×
/// 对端轮转（Majority 档 = leader 周期发起；LeaderLocal/Swarm-only 档 = 各节点对 known peers
/// 轮转——HyParView 提供成员表）。反熵不救已丢的写（LeaderLocal 未复制尾部——异步复制物理边界），
/// 只收敛已分发内容的漂移。</para>
/// </summary>
public sealed class SwarmAntiEntropy
{
    private readonly SwarmSync _swarm;
    private readonly ILogger? _logger;

    /// <summary>构造（零 IO——复用装配面 SwarmSync 的传输/handler）。</summary>
    /// <param name="swarm">多源同步组件（已 StartAsync——对账应答经其 0x03 handler）。</param>
    /// <param name="logger">日志（可选）。</param>
    public SwarmAntiEntropy(SwarmSync swarm, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(swarm);
        _swarm = swarm;
        _logger = logger;
    }

    /// <summary>反熵单轮：与对端比对指定内容 → 漂移块修复（本地源只读/未持 = 直接跳过）。</summary>
    /// <param name="local">本地块源（需支持校验和读取与块写回——能力降级见接口默认实现）。</param>
    /// <param name="peer">对端（比对/拉取源）。</param>
    /// <param name="manifest">对账内容清单（两端同 Id 同校验和基线）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>修复的块号集合（空 = 已一致或无可修复漂移）。</returns>
    public async ValueTask<IReadOnlyList<long>> RunOnceAsync(ISwarmBlockSource local, NodeId peer,
        SwarmManifest manifest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(manifest);
        if (!local.TryGetChecksums(manifest.Id, out var mine)) return [];   // 本地未持/不支持对账——无可收敛面

        // 1) 全局根比对（相等 = 整体一致零块传输）
        var theirsRoot = await ProbeAsync(peer, manifest.Id, level: 0, 0, 0, ct).ConfigureAwait(false);
        if (theirsRoot is null || theirsRoot.Length == 0) return [];   // 对端未持——无对账面
        var mineRoot = SwarmMerkle.ComputeGlobalRoot(mine);
        if (theirsRoot[0] == mineRoot) return [];

        // 2) 叶子层区间批量比对（每子树一请求）定位差异块
        var theirsLeaves = new uint[manifest.BlockCount];
        var subtreeCount = SwarmMerkle.ComputeSubtreeRoots(mine).Length;
        for (var s = 0; s < subtreeCount; s++)
        {
            var start = s * SwarmMerkle.SubtreeFanout;
            var count = Math.Min(SwarmMerkle.SubtreeFanout, manifest.BlockCount - start);
            var leaves = await ProbeAsync(peer, manifest.Id, level: 1, start, count, ct).ConfigureAwait(false);
            if (leaves is null || leaves.Length != count)
                return [];   // 对端响应异常——本轮放弃（下轮重试）
            leaves.CopyTo(theirsLeaves, start);
        }
        var diff = SwarmMerkle.DiffBlocks(mine, theirsLeaves);
        if (diff is null || diff.Count == 0) return [];   // 长度/根分歧已除外——防御

        // 3) 差异块定向拉取（只传差异块——校验和防线复用）+ 写回。
        //    ★ 分歧意味着至少一方坏：若 peer 侧块坏（校验拦截）且无其他源——本轮放弃，
        //    已修复部分保留（local 好数据不会被 peer 坏块覆盖——校验先于写回）。
        var repaired = new List<long>();
        try
        {
            await foreach (var (index, block) in _swarm.DownloadBlocksAsync(manifest, [peer], diff, null, ct).ConfigureAwait(false))
            {
                if (local.TryWriteBlock(manifest.Id, index, block))
                    repaired.Add(index);
            }
        }
        catch (SwarmDownloadException)
        {
            // 部分块对端耗尽——已修复部分保留，其余下轮重试
        }
        _logger?.LogInformation("Swarm 反熵修复：manifest={Id} peer={Peer} drift={Drift} repaired={Repaired}",
            manifest.Id, peer, diff.Count, repaired.Count);
        return repaired;
    }

    /// <summary>对账探测（应答空集/解码失败/传输失败 = null——发起方降级处置）。</summary>
    private async Task<uint[]?> ProbeAsync(NodeId peer, Opaque16 manifestId, byte level, int rangeStart, int rangeCount,
        CancellationToken ct)
    {
        try
        {
            var len = SwarmMessageCodec.EncodePooled(new EntropyProbeReq
            {
                ManifestId = manifestId,
                Level = level,
                RangeStart = rangeStart,
                RangeCount = rangeCount,
            }, out var buffer);
            try
            {
                var respBytes = await _swarm.Transport.SendRequestAsync(peer, ProtocolIds.SwarmSync,
                    buffer.AsMemory(0, len), null, ct).ConfigureAwait(false);
                if (!SwarmMessageCodec.TryDecode(respBytes, out var msg) || msg is not EntropyProbeResp resp)
                    return null;
                return resp.Hashes;
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            return null;   // 对端不可达——本轮放弃（下轮重试）
        }
    }
}
