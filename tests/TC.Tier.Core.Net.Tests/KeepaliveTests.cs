using System.Net;
using FluentAssertions;
using TC.Tier.Core.Net;
using TC.Tier.Core.Net.Channels;
using TC.Tier.Core.Net.Transport;
using TC.Tier.Core.Net.Wire;
using TC.Tier.Core.Primitives;
using Xunit;

namespace TC.Tier.Core.Net.Tests;

using TC.Tier.Core.Net.Tests.Fixtures;

/// <summary>
/// 传输级保活契约测试（spec-11 §2.3——默认关闭/协商开启/互答稳态不误断）。
/// <para>断连路径（连续无应答 → Close）由 <see cref="KeepaliveTrackerTests"/> 纯逻辑覆盖——
///   loopback 真套接字无法构造半开死链（内核感知断连），集成侧验证稳态不误断。</para>
/// </summary>
public class KeepaliveTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);

    private static (NodeId Low, NodeId High) NewOrderedIds()
    {
        var a = NodeId.NewRandom();
        var b = NodeId.NewRandom();
        return a.CompareTo(b) < 0 ? (a, b) : (b, a);
    }

    private static async Task<(ClusterTransport Low, ClusterTransport High, NodeId LowId, NodeId HighId)> SetupPairAsync(
        bool lowKeepalive, bool highKeepalive, RecordingMetricsSink? lowSink = null, RecordingMetricsSink? highSink = null)
    {
        var (lowId, highId) = NewOrderedIds();

        var knownOfHigh = new Dictionary<NodeId, IPEndPoint> { [lowId] = new(IPAddress.Loopback, 0) };
        var highOptions = TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, 0), knownOfHigh) with
        {
            EnableKeepalive = highKeepalive,
            KeepaliveInterval = TimeSpan.FromMilliseconds(30),
        };
        var high = new ClusterTransport(highId, highOptions, hub: highSink?.ToHub());
        high.Start();
        var listenEndPoint = high.LocalEndPoint!;

        var lowOptions = TransportOptions.Default(null, new Dictionary<NodeId, IPEndPoint> { [highId] = listenEndPoint }) with
        {
            EnableKeepalive = lowKeepalive,
            KeepaliveInterval = TimeSpan.FromMilliseconds(30),
        };
        var low = new ClusterTransport(lowId, lowOptions, hub: lowSink?.ToHub());
        low.Start();

        var lowUp = new TaskCompletionSource();
        var highUp = new TaskCompletionSource();
        low.PeerConnected += _ => lowUp.TrySetResult();
        high.PeerConnected += _ => highUp.TrySetResult();
        await lowUp.Task.WaitAsync(WaitLimit);
        await highUp.Task.WaitAsync(WaitLimit);

        return (low, high, lowId, highId);
    }

    private static async Task DisposeBothAsync(ClusterTransport? a, ClusterTransport? b)
    {
        if (a is not null) await a.DisposeAsync();
        if (b is not null) await b.DisposeAsync();
    }

    private sealed class EchoHandler : IDatagramHandler
    {
        public int Count;

        public void OnDatagram(NodeId from, ReadOnlyMemory<byte> payload) => Interlocked.Increment(ref Count);
    }

    [Fact]
    public async Task 双端开启保活_稳态不误断()
    {
        var lowSink = new RecordingMetricsSink();
        var highSink = new RecordingMetricsSink();
        var (low, high, lowId, highId) = await SetupPairAsync(lowKeepalive: true, highKeepalive: true, lowSink: lowSink, highSink: highSink);
        try
        {
            var handler = new EchoHandler();
            high.RegisterProtocol(0x64, handler);

            // ~700ms ≈ 23 个保活周期——期间每 50ms 发一帧数据报（业务流量与保活并存）
            for (int i = 0; i < 14; i++)
            {
                await low.SendDatagramAsync(highId, 0x64, new byte[] { (byte)i });
                await Task.Delay(50);
            }

            handler.Count.Should().BeGreaterOrEqualTo(10, "稳态链路保活不误断");
            lowSink.CountOf("net.keepalive_drops").Should().Be(0);
            highSink.CountOf("net.keepalive_drops").Should().Be(0);
        }
        finally
        {
            await DisposeBothAsync(low, high);
        }
    }

    [Fact]
    public async Task 单端开启_协商不成立_无保活循环()
    {
        var lowSink = new RecordingMetricsSink();
        var highSink = new RecordingMetricsSink();
        var (low, high, lowId, highId) = await SetupPairAsync(lowKeepalive: true, highKeepalive: false, lowSink: lowSink, highSink: highSink);
        try
        {
            var handler = new EchoHandler();
            high.RegisterProtocol(0x64, handler);

            for (int i = 0; i < 8; i++)
            {
                await low.SendDatagramAsync(highId, 0x64, new byte[] { (byte)i });
                await Task.Delay(50);
            }

            handler.Count.Should().BeGreaterOrEqualTo(5, "交集为空——双方都不得启用保活行为（spec-11 §1.4）");
            lowSink.CountOf("net.keepalive_drops").Should().Be(0);
            highSink.CountOf("net.keepalive_drops").Should().Be(0);
        }
        finally
        {
            await DisposeBothAsync(low, high);
        }
    }
}
