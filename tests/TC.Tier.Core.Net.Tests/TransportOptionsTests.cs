using System.Net;
using FluentAssertions;
using TC.Tier.Core.Net;
using Xunit;

namespace TC.Tier.Core.Net.Tests;

/// <summary>
/// TransportOptions 契约测试（spec-12 §4.5——八参数全量/With 链派生/装配期校验 fail-fast）。
/// </summary>
public class TransportOptionsTests
{
    private static TransportOptions NewDefault() =>
        TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, 9000), new Dictionary<NodeId, IPEndPoint>());

    // ══ §4.5 八参数缺省锁定 ══

    [Fact]
    public void 缺省_八参数全量锁定()
    {
        var options = NewDefault();

        options.HandshakeTimeout.Should().Be(TimeSpan.FromSeconds(3));
        options.ReconnectInitialDelay.Should().Be(TimeSpan.FromMilliseconds(100));
        options.ReconnectBackoffFactor.Should().Be(2.0);
        options.ReconnectMaxDelay.Should().Be(TimeSpan.FromSeconds(2));
        options.EffectiveKeepaliveInterval.Should().Be(TimeSpan.FromSeconds(10));
        options.KeepaliveMaxUnanswered.Should().Be(3);
        options.UdpMaxDatagramBytes.Should().Be(1200);
        options.EffectiveSlowDispatchThreshold.Should().Be(TimeSpan.FromMilliseconds(10));
        options.ClusterTag.Should().Be(TransportOptions.DefaultClusterTag);
        options.EnableKeepalive.Should().BeFalse();
        options.UdpListenEndPoint.Should().BeNull();
    }

    [Fact]
    public void 生效值_显式配置优先于缺省()
    {
        var options = NewDefault() with
        {
            KeepaliveInterval = TimeSpan.FromSeconds(2),
            SlowDispatchThreshold = TimeSpan.FromMilliseconds(50),
        };

        options.EffectiveKeepaliveInterval.Should().Be(TimeSpan.FromSeconds(2));
        options.EffectiveSlowDispatchThreshold.Should().Be(TimeSpan.FromMilliseconds(50));
    }

    // ══ With 链（目标值变更 + 其余不变——不可变派生）══

    [Fact]
    public void With链_目标值变更其余不变()
    {
        var baseline = NewDefault();

        var tagged = baseline.WithClusterTag(0x5443);
        tagged.ClusterTag.Should().Be(0x5443);
        tagged.HandshakeTimeout.Should().Be(baseline.HandshakeTimeout, "With 只改目标值——其余保持源值");
        tagged.Peers.Should().BeSameAs(baseline.Peers);
        baseline.ClusterTag.Should().Be(0u, "源实例不可变——With 返回新实例");

        var reconnected = baseline.WithReconnect(TimeSpan.FromMilliseconds(5), 1.5, TimeSpan.FromMilliseconds(500));
        reconnected.ReconnectInitialDelay.Should().Be(TimeSpan.FromMilliseconds(5));
        reconnected.ReconnectBackoffFactor.Should().Be(1.5);
        reconnected.ReconnectMaxDelay.Should().Be(TimeSpan.FromMilliseconds(500));

        var udp = baseline.WithUdp(new IPEndPoint(IPAddress.Loopback, 9001), 900);
        udp.UdpListenEndPoint.Should().Be(new IPEndPoint(IPAddress.Loopback, 9001));
        udp.UdpMaxDatagramBytes.Should().Be(900);

        var keepalive = baseline.WithKeepalive(true, TimeSpan.FromSeconds(1), 5);
        keepalive.EnableKeepalive.Should().BeTrue();
        keepalive.EffectiveKeepaliveInterval.Should().Be(TimeSpan.FromSeconds(1));
        keepalive.KeepaliveMaxUnanswered.Should().Be(5);

        baseline.WithSlowDispatchThreshold(TimeSpan.FromMilliseconds(1)).EffectiveSlowDispatchThreshold
            .Should().Be(TimeSpan.FromMilliseconds(1));
        baseline.WithListen(null).ListenEndPoint.Should().BeNull();
        baseline.WithHandshakeTimeout(TimeSpan.FromSeconds(9)).HandshakeTimeout.Should().Be(TimeSpan.FromSeconds(9));
    }

    // ══ 装配期校验（矛盾配置 fail-fast——with 派生不重跑构造，故校验在 Validate 收敛、
    //    传输构造唯一消费入口调用）══

    public static TheoryData<string, Func<TransportOptions>> InvalidConfigurations => new()
    {
        { "握手超时非正", () => TransportOptions.Default(null, new Dictionary<NodeId, IPEndPoint>()) with { HandshakeTimeout = TimeSpan.Zero } },
        { "退避初值非正", () => TransportOptions.Default(null, new Dictionary<NodeId, IPEndPoint>()) with { ReconnectInitialDelay = TimeSpan.Zero } },
        { "退避封顶低于初值", () => TransportOptions.Default(null, new Dictionary<NodeId, IPEndPoint>()) with { ReconnectMaxDelay = TimeSpan.FromMilliseconds(1) } },
        { "退避倍率小于1", () => TransportOptions.Default(null, new Dictionary<NodeId, IPEndPoint>()) with { ReconnectBackoffFactor = 0.5 } },
        { "保活阈值小于1", () => TransportOptions.Default(null, new Dictionary<NodeId, IPEndPoint>()) with { KeepaliveMaxUnanswered = 0 } },
        { "UDP 预算非正", () => TransportOptions.Default(null, new Dictionary<NodeId, IPEndPoint>()) with { UdpMaxDatagramBytes = 0 } },
        { "慢回调阈值非正", () => TransportOptions.Default(null, new Dictionary<NodeId, IPEndPoint>()) with { SlowDispatchThreshold = TimeSpan.Zero } },
        { "去重窗口字节软预算非正", () => TransportOptions.Default(null, new Dictionary<NodeId, IPEndPoint>()) with { RequestDedupMaxBytes = 0 } },
    };

    [Theory]
    [MemberData(nameof(InvalidConfigurations))]
    public void 矛盾配置_Validate抛(string _, Func<TransportOptions> build)
    {
        var options = build();
        var act = () => options.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void 矛盾配置_传输构造期抛()
    {
        var options = TransportOptions.Default(null, new Dictionary<NodeId, IPEndPoint>()) with { ReconnectBackoffFactor = 0.5 };
        var act = () => new ClusterTransport(NodeId.NewRandom(), options);
        act.Should().Throw<ArgumentOutOfRangeException>("选项唯一消费入口 = 传输构造（装配期 fail-fast）");
    }

    [Fact]
    public void 地址表_Empty哨兵_Validate抛()
    {
        var peers = new Dictionary<NodeId, IPEndPoint> { [NodeId.Empty] = new(IPAddress.Loopback, 9000) };
        var act = () => TransportOptions.Default(null, peers).Validate();
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void 地址表_null端点_Validate抛()
    {
        var peers = new Dictionary<NodeId, IPEndPoint> { [NodeId.NewRandom()] = null! };
        var act = () => TransportOptions.Default(null, peers).Validate();
        act.Should().Throw<ArgumentNullException>();
    }
}
