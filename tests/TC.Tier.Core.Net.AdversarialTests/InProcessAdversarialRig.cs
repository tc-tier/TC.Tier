using TC.Tier.Core.IO;

namespace TC.Tier.Core.Net.AdversarialTests;

/// <summary>
/// InProcess 对抗 rig（spec-12 §7 同构基准——语义参考实现主场）：一枢纽一集群域，
/// 节点经 <see cref="InProcessTransportHub.Register"/> 注册；故障注入 = 枢纽级注入面
/// （分区/丢包/延迟/乱序与 TCP 同构语义）。
/// <para>★ 每节点私有内存卷（TierFs memory:）——FsRaftStore 真持久化语义（重启恢复），
/// 重加入同卷同目录重建。</para>
/// </summary>
internal sealed class InProcessAdversarialRig : IAdversarialRig
{
    private readonly InProcessTransportHub _hub = new();
    private readonly ClusterConfig _config;
    private readonly RaftOptions _raft;
    private readonly NodeId[] _ids;
    private readonly IFileSystem[] _fses;
    private readonly AdversarialNode?[] _nodes;
    private int _seedOffset;

    public string WireLabel => "InProcess";
    public ITransportFaultInjector Faults => _hub.Faults;
    public IReadOnlyList<AdversarialNode> Nodes => _nodes!;

    private InProcessAdversarialRig(int count, RaftOptions? raft)
    {
        _raft = raft ?? RaftOptions.Default;
        _ids = new NodeId[count];
        var members = new ClusterMember[count];
        for (var i = 0; i < count; i++)
        {
            _ids[i] = NodeId.NewRandom();
            members[i] = new ClusterMember(_ids[i], "");
        }
        _config = new ClusterConfig(members);
        _fses = new IFileSystem[count];
        for (var i = 0; i < count; i++) _fses[i] = TierFs.New("memory:");
        _nodes = new AdversarialNode?[count];
    }

    public static async Task<IAdversarialRig> CreateAsync(int count, RaftOptions? raft = null)
    {
        var rig = new InProcessAdversarialRig(count, raft);
        try
        {
            for (var i = 0; i < count; i++)
            {
                rig._nodes[i] = await rig.BuildAsync(i).ConfigureAwait(false);
                await rig._nodes[i]!.StartAsync(rig._config).ConfigureAwait(false);
            }
            return rig;
        }
        catch
        {
            await rig.DisposeAsync();
            throw;
        }
    }

    private Task<AdversarialNode> BuildAsync(int index)
    {
        var transport = _hub.Register(_ids[index]);
        transport.Start();
        return AdversarialNode.BuildAsync(_ids[index], _fses[index], transport, _raft, 100 + index + Interlocked.Increment(ref _seedOffset) * 97, verbose: true);
    }

    public async Task KillNodeAsync(int index)
    {
        if (_nodes[index] is not { } node) return;
        await node.KillAsync().ConfigureAwait(false);
    }

    public async Task<AdversarialNode> RejoinNodeAsync(int index)
    {
        var node = await BuildAsync(index).ConfigureAwait(false);
        await node.StartAsync(_config).ConfigureAwait(false);
        _nodes[index] = node;
        return node;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var n in _nodes)
        {
            if (n is null) continue;
            try { await n.DisposeAsync().ConfigureAwait(false); } catch { /* 尽力清理 */ }
        }
        foreach (var fs in _fses)
        {
            try { fs.Dispose(); } catch { /* 尽力清理 */ }
        }
        await _hub.DisposeAsync().ConfigureAwait(false);
    }
}
