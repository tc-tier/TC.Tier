using System.Collections.Concurrent;
using TC.Tier.Core.IO;

namespace TC.Tier.Core.Net.AdversarialTests;

/// <summary>记录型业务状态机（apply 单 worker 回调——按 index 记录命令）。</summary>
internal sealed class RecordingMachine : IStateMachine
{
    public readonly ConcurrentDictionary<long, byte[]> Applied = new();

    public ValueTask ApplyAsync(long index, ReadOnlyMemory<byte> command, CancellationToken ct = default)
    {
        Applied[index] = command.ToArray();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// 对抗节点（FsRaftStore 真持久化语义夹具 × 引擎全家——介质形态由 rig 装配的传输形态决定）。
/// <para>★ 重加入语义：Kill 释放引擎/传输/存储句柄但保留 <see cref="Fs"/>（每节点私有内存卷）——
/// 同一卷同一目录重建 = 同介质同存储恢复（快照区 + 已提交日志自动恢复）。</para>
/// </summary>
internal sealed class AdversarialNode : IAsyncDisposable
{
    public required NodeId Id { get; init; }
    public required IFileSystem Fs { get; init; }
    public required IProtocolTransport Transport { get; init; }
    public required FsRaftStore Store { get; init; }
    public required RecordingMachine Machine { get; init; }
    public required ApplyPipeline Pipeline { get; init; }
    public required RaftStateMachine Raft { get; init; }

    /// <summary>取证日志（失败转储）。</summary>
    public MemoryLogger? Logger { get; init; }

    private int _killed;

    /// <summary>启动（pipeline → raft）。</summary>
    public async Task StartAsync(ClusterConfig config)
    {
        await Pipeline.StartAsync().ConfigureAwait(false);
        await Raft.StartAsync(config).ConfigureAwait(false);
    }

    /// <summary>掉线：进程关（优雅 Dispose——数据已落盘）+ 网络断（传输释放）。</summary>
    public async Task KillAsync()
    {
        if (Interlocked.Exchange(ref _killed, 1) != 0) return;
        await Raft.DisposeAsync().ConfigureAwait(false);
        await Pipeline.DisposeAsync().ConfigureAwait(false);
        await Transport.DisposeAsync().ConfigureAwait(false);
        Store.Dispose();   // 句柄释放——内存卷保留（重加入复用）
    }

    public async ValueTask DisposeAsync() => await KillAsync().ConfigureAwait(false);

    /// <summary>
    /// 引擎全家装配（两介质 rig 共用——传输形态由调用方注入）。
    /// </summary>
    public static async Task<AdversarialNode> BuildAsync(NodeId id, IFileSystem fs,
        IProtocolTransport transport, RaftOptions options, int seed, bool verbose = false)
    {
        var store = new FsRaftStore(fs, "node");
        await store.InitializeAsync().ConfigureAwait(false);
        var machine = new RecordingMachine();
        var logger = verbose ? new MemoryLogger(id.ToString()[..6]) : null;
        var pipeline = new ApplyPipeline(store, machine, ApplyPipelineOptions.Default, logger: logger);
        var raft = new RaftStateMachine(id, store, transport, pipeline,
            options.WithRandom(new Random(seed)), logger: logger);
        pipeline.SetConfigCallback(raft.PostConfigChanged);
        return new AdversarialNode
        {
            Id = id, Fs = fs, Transport = transport, Store = store,
            Machine = machine, Pipeline = pipeline, Raft = raft, Logger = logger,
        };
    }
}

/// <summary>对抗 rig 抽象（spec-09 §3 场景真源——介质形态由实现装配）。</summary>
internal interface IAdversarialRig : IAsyncDisposable
{
    /// <summary>介质形态标识（失败信息区分）。</summary>
    string WireLabel { get; }

    /// <summary>故障注入面（节点对定向——延迟/分区/丢包/乱序）。</summary>
    ITransportFaultInjector Faults { get; }

    /// <summary>节点表（Kill/Rejoin 更新槽位）。</summary>
    IReadOnlyList<AdversarialNode> Nodes { get; }

    /// <summary>掉线（网络断 + 进程关——同介质同存储可重加入）。</summary>
    Task KillNodeAsync(int index);

    /// <summary>重加入（同介质同存储重建——快照区 + 已提交日志自动恢复）。</summary>
    Task<AdversarialNode> RejoinNodeAsync(int index);
}
