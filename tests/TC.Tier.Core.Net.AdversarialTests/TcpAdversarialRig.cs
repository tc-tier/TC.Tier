using System.Net;
using System.Net.Sockets;
using TC.Tier.Core.IO;

namespace TC.Tier.Core.Net.AdversarialTests;

/// <summary>
/// TCP 对抗 rig（spec-12 §4.1 成员制全网格）：每节点独立 <see cref="ClusterTransport"/>
/// （显式端口 + 已知端点表——按 NodeId 序"较小方拨号"构图，每对恰一方持有对端端点，
/// 成员制拨号归属规则下无双向拨号竞态）；故障注入扇出到全部在线传输（分区/丢包需
/// 双向真断——单传输注入会留单向活链路）。
/// <para>★ 重加入：同端口同存储重建（端口预保留——断链重连由退避驱动）。</para>
/// </summary>
internal sealed class TcpAdversarialRig : IAdversarialRig
{
    private readonly ClusterConfig _config;
    private readonly RaftOptions _raft;
    private readonly NodeId[] _ids;        // NodeId 字典序升序——较小方拨号构图
    private readonly int[] _ports;
    private readonly IFileSystem[] _fses;
    private readonly AdversarialNode?[] _nodes;
    private readonly List<ClusterTransport> _transports = [];
    private readonly FanOutFaults _faults;
    private int _seedOffset;

    public string WireLabel => "TCP";
    public ITransportFaultInjector Faults => _faults;
    public IReadOnlyList<AdversarialNode> Nodes => _nodes!;

    private TcpAdversarialRig(int count, RaftOptions? raft)
    {
        _raft = raft ?? RaftOptions.Default;
        var members = new ClusterMember[count];
        var unsorted = new NodeId[count];
        for (var i = 0; i < count; i++)
        {
            unsorted[i] = NodeId.NewRandom();
            members[i] = new ClusterMember(unsorted[i], "");
        }
        _ids = unsorted.OrderBy(id => id).ToArray();   // 字节字典序全序（拨号归属同序）
        _config = new ClusterConfig(members);
        _ports = ReservePorts(count);
        _fses = new IFileSystem[count];
        for (var i = 0; i < count; i++) _fses[i] = TierFs.New("memory:");
        _nodes = new AdversarialNode?[count];
        _faults = new FanOutFaults(_transports);
    }

    public static async Task<IAdversarialRig> CreateAsync(int count, RaftOptions? raft = null)
    {
        var rig = new TcpAdversarialRig(count, raft);
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

    /// <summary>节点 i 的已知端点表（仅更大 NodeId——较小方拨号；链接建立后双向通信）。</summary>
    private Dictionary<NodeId, IPEndPoint> PeersOf(int index)
    {
        var peers = new Dictionary<NodeId, IPEndPoint>();
        for (var j = index + 1; j < _ids.Length; j++)
            peers[_ids[j]] = new IPEndPoint(IPAddress.Loopback, _ports[j]);
        return peers;
    }

    private Task<AdversarialNode> BuildAsync(int index)
    {
        var transport = new ClusterTransport(_ids[index],
            TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, _ports[index]), PeersOf(index)));
        transport.Start();
        _transports.Add(transport);
        return AdversarialNode.BuildAsync(_ids[index], _fses[index], transport, _raft, 200 + index + Interlocked.Increment(ref _seedOffset) * 97, verbose: true);
    }

    public async Task KillNodeAsync(int index)
    {
        if (_nodes[index] is not { } node) return;
        await node.KillAsync().ConfigureAwait(false);
        if (node.Transport is ClusterTransport t) _transports.Remove(t);   // 摘除扇出面（Kill 后不再注入）
    }

    public async Task<AdversarialNode> RejoinNodeAsync(int index)
    {
        var node = await BuildAsync(index).ConfigureAwait(false);   // 同端口同存储（扇出面重新加入）
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
    }

    /// <summary>注入扇出（全部在线传输——节点对定向字典在发送路径各自查询，扇出保证双向真断）。</summary>
    private sealed class FanOutFaults(List<ClusterTransport> transports) : ITransportFaultInjector
    {
        public void SetLatency(NodeId a, NodeId b, TimeSpan? latency)
        {
            foreach (var h in transports.ToArray()) h.Faults.SetLatency(a, b, latency);
        }

        public void Partition(IEnumerable<NodeId> groupA, IEnumerable<NodeId> groupB)
        {
            foreach (var h in transports.ToArray()) h.Faults.Partition(groupA, groupB);
        }

        public void Drop(NodeId from, NodeId to, double rate)
        {
            foreach (var h in transports.ToArray()) h.Faults.Drop(from, to, rate);
        }

        public void Reorder(NodeId from, NodeId to, bool enable)
        {
            foreach (var h in transports.ToArray()) h.Faults.Reorder(from, to, enable);
        }

        public void Reset()
        {
            foreach (var h in transports.ToArray()) h.Faults.Reset();
        }
    }

    /// <summary>端口预保留（bind 后释放——TCP 对抗装配的既有形态；loopback 立即复用竞态可忽略）。</summary>
    private static int[] ReservePorts(int count)
    {
        var listeners = new TcpListener[count];
        var ports = new int[count];
        try
        {
            for (var i = 0; i < count; i++)
            {
                listeners[i] = new TcpListener(IPAddress.Loopback, 0);
                listeners[i].Start();
                ports[i] = ((IPEndPoint)listeners[i].LocalEndpoint).Port;
            }
        }
        finally
        {
            foreach (var l in listeners)
            {
                try { l.Stop(); } catch { /* 尽力清理 */ }
            }
        }
        return ports;
    }
}
