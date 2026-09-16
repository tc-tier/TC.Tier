using System.Collections.Concurrent;
using System.Net;
using TC.Tier.Core.Net;
using TC.Tier.Core.Net.Channels;
using TC.Tier.Core.Net.Wire;

namespace TC.Tier.Core.Net.Tests.Fixtures;

/// <summary>
/// ClusterTransport 装配共享夹具（单一真源——单元套件与对抗项目经 csproj Compile 链接共用）：
/// 一对互联节点的装配/释放 + 计数收集 handler。
/// </summary>
public static class ClusterTransportRig
{
    /// <summary>装配等待上限（单元套件口径——失败快速暴露不悬挂；对抗项目自行放宽）。</summary>
    public static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);

    /// <summary>装配一对互联节点：较大方监听（port 0），较小方拨号——双向 PeerConnected 后返回。</summary>
    public static async Task<(ClusterTransport Low, ClusterTransport High, NodeId LowId, NodeId HighId)> SetupPairAsync(
        RecordingMetricsSink? lowSink = null, RecordingMetricsSink? highSink = null, TimeSpan? waitLimit = null)
    {
        var (lowId, highId) = NewOrderedIds();
        var limit = waitLimit ?? WaitLimit;

        var knownOfHigh = new Dictionary<NodeId, IPEndPoint>
        {
            [lowId] = new(IPAddress.Loopback, 0),   // 端点占位——较大方不拨号，仅 IsKnownPeer 校验用
        };
        var high = new ClusterTransport(highId, TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, 0), knownOfHigh), hub: highSink?.ToHub());
        high.Start();
        var listenEndPoint = high.LocalEndPoint!;

        var low = new ClusterTransport(lowId, TransportOptions.Default(null, new Dictionary<NodeId, IPEndPoint> { [highId] = listenEndPoint }), hub: lowSink?.ToHub());
        low.Start();

        var lowUp = new TaskCompletionSource();
        var highUp = new TaskCompletionSource();
        low.PeerConnected += _ => lowUp.TrySetResult();
        high.PeerConnected += _ => highUp.TrySetResult();
        await lowUp.Task.WaitAsync(limit);
        await highUp.Task.WaitAsync(limit);

        return (low, high, lowId, highId);
    }

    /// <summary>成对释放（任一可为 null）。</summary>
    public static async Task DisposeBothAsync(ClusterTransport? a, ClusterTransport? b)
    {
        if (a is not null) await a.DisposeAsync();
        if (b is not null) await b.DisposeAsync();
    }

    /// <summary>生成一对有序节点 id（Low &lt; High——装配假定较大方监听）。</summary>
    public static (NodeId Low, NodeId High) NewOrderedIds()
    {
        var a = NodeId.NewRandom();
        var b = NodeId.NewRandom();
        return a.CompareTo(b) < 0 ? (a, b) : (b, a);
    }
}

/// <summary>计数收集 handler：收满 expected 条触发 done（守恒验证面——无丢失/无重复由调用方断言）。</summary>
public sealed class CollectingHandler(ConcurrentBag<byte> sink, TaskCompletionSource done, int expected) : IDatagramHandler
{
    private int _seen;

    public void OnDatagram(NodeId from, ReadOnlyMemory<byte> payload)
    {
        sink.Add(payload.Span[0]);
        if (Interlocked.Increment(ref _seen) == expected) done.TrySetResult();
    }
}
