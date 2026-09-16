using FluentAssertions;
using TC.Tier.Core.Net;
using TC.Tier.Core.Net.Channels;
using Xunit;

namespace TC.Tier.Core.Net.Tests.Channels;

/// <summary>
/// DatagramDispatcher 契约测试（spec-12 §5.1/§3.5——注册分流/bearer 表/分发异常隔离/可观测钩子）。
/// </summary>
public class DatagramDispatcherTests
{
    private sealed class CapturingHandler(TaskCompletionSource<(NodeId From, byte[] Payload)> tcs) : IDatagramHandler
    {
        public void OnDatagram(NodeId from, ReadOnlyMemory<byte> payload) => tcs.TrySetResult((from, payload.ToArray()));
    }

    private sealed class ThrowingHandler : IDatagramHandler
    {
        public void OnDatagram(NodeId from, ReadOnlyMemory<byte> payload) => throw new InvalidOperationException("使用方 handler 故障");
    }

    private sealed class SleepingHandler : IDatagramHandler
    {
        public void OnDatagram(NodeId from, ReadOnlyMemory<byte> payload) => Thread.Sleep(20);
    }

    // ══ 注册分流（§3.5 五区制）══

    [Theory]
    [InlineData((byte)0x01)]        // 内部核心区（Raft）
    [InlineData((byte)0x00)]        // 内部核心区（管理）
    [InlineData((byte)0x50)]        // 隔离区 Ⅰ
    [InlineData((byte)0xB0)]        // 隔离区 Ⅱ
    [InlineData((byte)0xFF)]        // 保留
    public void 公开口_非注册区_一律抛(byte protocolId)
    {
        var dispatcher = new DatagramDispatcher();
        var act = () => dispatcher.RegisterUser(protocolId, new ThrowingHandler());
        act.Should().Throw<ArgumentOutOfRangeException>("公开口只放行注册区 0x60-0xAF（§3.5 分流）");
    }

    [Theory]
    [InlineData((byte)0x60)]        // 注册区下界
    [InlineData((byte)0xAF)]        // 注册区上界
    public void 公开口_注册区边界_放行(byte protocolId)
    {
        var dispatcher = new DatagramDispatcher();
        var act = () => dispatcher.RegisterUser(protocolId, new ThrowingHandler());
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData((byte)0x50)]
    [InlineData((byte)0x64)]        // 注册区号走内部口 = 抛
    [InlineData((byte)0xFF)]
    public void 内部口_核心区外_一律抛(byte protocolId)
    {
        var dispatcher = new DatagramDispatcher();
        var act = () => dispatcher.RegisterCore(protocolId, new ThrowingHandler());
        act.Should().Throw<ArgumentOutOfRangeException>("内部口只放行核心区 0x00-0x4F（机制面专用）");
    }

    [Fact]
    public void 重复注册_抛()
    {
        var dispatcher = new DatagramDispatcher();
        dispatcher.RegisterUser(0x64, new ThrowingHandler());
        var act = () => dispatcher.RegisterUser(0x64, new CapturingHandler(new()));
        act.Should().Throw<InvalidOperationException>();
    }

    // 两注册口合法区间（注册区 0x60-0xAF ∩ 核心区 0x00-0x4F = ∅）不相交——
    // 同号跨口重复在结构上不可能（五区制 §3.5 的设计意图：公开口拿不到内部号）。

    // ══ bearer 声明表 ══

    [Fact]
    public void bearer查询_注册可查_未注册false()
    {
        var dispatcher = new DatagramDispatcher();
        dispatcher.RegisterUser(0x64, new ThrowingHandler(), DatagramBearer.Udp);

        dispatcher.TryGetBearer(0x64, out var bearer).Should().BeTrue();
        bearer.Should().Be(DatagramBearer.Udp);
        dispatcher.TryGetBearer(0x65, out _).Should().BeFalse();
    }

    // ══ 分发（异常隔离/未知协议/慢回调）══

    [Fact]
    public async Task 分发_载荷到达来源正确()
    {
        var dispatcher = new DatagramDispatcher();
        var received = new TaskCompletionSource<(NodeId, byte[])>();
        dispatcher.RegisterUser(0x64, new CapturingHandler(received));

        var from = NodeId.NewRandom();
        dispatcher.Dispatch(from, 0x64, new byte[] { 7, 7 });
        var got = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        got.Item1.Should().Be(from);
        got.Item2.Should().Equal(new byte[] { 7, 7 });
    }

    [Fact]
    public void 分发_未注册协议_静默丢弃_钩子计数()
    {
        int unknown = 0;
        var dispatcher = new DatagramDispatcher(onUnknownProtocol: _ => unknown++);

        dispatcher.Dispatch(NodeId.NewRandom(), 0x90, new byte[] { 1 });
        unknown.Should().Be(1);
    }

    [Fact]
    public void 分发_handler异常_不外泄()
    {
        var dispatcher = new DatagramDispatcher();
        dispatcher.RegisterUser(0x65, new ThrowingHandler());

        var act = () => dispatcher.Dispatch(NodeId.NewRandom(), 0x65, new byte[] { 1 });
        act.Should().NotThrow("handler 异常隔离在分发器——不外泄到介质/发送方");
    }

    [Fact]
    public void 分发_慢回调_钩子触发_未配置阈值零钩子()
    {
        int slow = 0;
        var withThreshold = new DatagramDispatcher(TimeSpan.FromMilliseconds(1), onSlowDispatch: _ => slow++);
        withThreshold.RegisterUser(0x64, new SleepingHandler());   // 20ms > 1ms
        withThreshold.Dispatch(NodeId.NewRandom(), 0x64, new byte[] { 1 });
        slow.Should().BeGreaterThanOrEqualTo(1);

        var withoutThreshold = new DatagramDispatcher(onSlowDispatch: _ => slow++);
        withoutThreshold.RegisterUser(0x64, new SleepingHandler());
        int before = slow;
        withoutThreshold.Dispatch(NodeId.NewRandom(), 0x64, new byte[] { 1 });
        slow.Should().Be(before, "阈值未配置 = 不检测——慢回调分发零钩子（InProcess 直排热路径零计时成本）");
    }
}
