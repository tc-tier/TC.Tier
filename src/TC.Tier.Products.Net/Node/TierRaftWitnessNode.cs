using TC.Tier.Core.Logging;
using TC.Tier.Core.Net.Raft;

namespace TC.Tier.Products.Net.Node;

/// <summary>
/// TierRaftWitnessNode——witness（见证者）产品节点（三期-F2 装配面）：
/// 共识投票成员（计入选主/提交多数派）而不存全量数据——高水位断言流（<see cref="WitnessHighWaterStore"/>）
/// 代替日志体、无状态机、无快照，永不成为 leader（MongoDB arbitrator / PolarDB witness 同位）。
/// <para>★ 与 <see cref="TierRaftNode"/> 的分工：witness 无日志体/无 apply 管道/无 Swarm/
/// 无宿主调度（快照压缩与反熵均不适用）——完整产品节点的装配链在此不成立，故独立轻量形态；
/// 引擎语义（投票/断言流/不自荐）同一 <see cref="RaftStateMachine"/>。</para>
/// <para>★ 引导：空盘 witness 经 <c>TierRaftNodeBuilder.WithJoinAsWitness</c> 加入既有集群
/// （JoinReq AsWitness 档——leader 受理即配置提交，断言流随后到达）；本地配置 [self witness]
/// 是引擎 witness 门的依据（IsWitness——内容不落盘、投票不自荐）。</para>
/// </summary>
public sealed class TierRaftWitnessNode : IAsyncDisposable
{
    private readonly RaftStateMachine _raft;
    private readonly WitnessHighWaterStore _store;
    private readonly IAsyncDisposable? _transportOwner;   // 装配器内建传输（随节点链尾收尾——调用方注入 = null）
    private readonly ILogger? _logger;
    private int _disposed;

    internal TierRaftWitnessNode(NodeId id, RaftStateMachine raft, WitnessHighWaterStore store,
        IAsyncDisposable? transportOwner, ILogger? logger)
    {
        Id = id;
        _raft = raft;
        _store = store;
        _transportOwner = transportOwner;
        Raft = raft;
        Store = store;
        _logger = logger;
    }

    /// <summary>节点标识。</summary>
    public NodeId Id { get; }

    /// <summary>raft 状态机（投票/角色/leader 观测面——witness 永非 leader，IsLeader 恒 false）。</summary>
    public RaftStateMachine Raft { get; }

    /// <summary>高水位存储（断言流观测面——LastLogIndex/LastTerm 单调不降）。</summary>
    public WitnessHighWaterStore Store { get; }

    /// <summary>释放（raft → 传输——幂等；witness 无日志/管道/调度循环，收尾面天然小）。</summary>
    /// <returns>raft 与（内建形态）传输均已释放。</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;   // 幂等（显式 + await using 双路径）
        await _raft.DisposeAsync().ConfigureAwait(false);
        if (_transportOwner is not null)
            await _transportOwner.DisposeAsync().ConfigureAwait(false);
        _logger?.LogInformation("TierRaftWitnessNode {Id} disposed", Id);
    }
}
