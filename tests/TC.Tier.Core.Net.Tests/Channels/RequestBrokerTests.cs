using FluentAssertions;
using TC.Tier.Core.Net;
using TC.Tier.Core.Net.Channels;
using Xunit;

namespace TC.Tier.Core.Net.Tests.Channels;

/// <summary>
/// RequestBroker 契约测试（spec-12 §5.2——CorrId 生成/pending 有界/终态清理防泄漏/
/// 分发异常隔离）。
/// </summary>
public class RequestBrokerTests
{
    private sealed class EchoHandler : IRequestHandler
    {
        public int Served;

        public void OnRequest(NodeId from, ReadOnlyMemory<byte> payload, IReplyContext reply)
        {
            Interlocked.Increment(ref Served);
            _ = reply.ReplyAsync(payload).AsTask();   // CA2012：ValueTask 转 Task 尽力回显（直排介质立即完成）
        }
    }

    private sealed class SilentHandler : IRequestHandler
    {
        public void OnRequest(NodeId from, ReadOnlyMemory<byte> payload, IReplyContext reply)
        {
        }
    }

    private sealed class ThrowingHandler : IRequestHandler
    {
        public void OnRequest(NodeId from, ReadOnlyMemory<byte> payload, IReplyContext reply)
            => throw new InvalidOperationException("使用方 handler 故障");
    }

    private sealed class StubReply(NodeId peer, byte protocolId, ulong corrId) : IReplyContext
    {
        public NodeId Peer => peer;
        public byte ProtocolId => protocolId;
        public ulong CorrelationId => corrId;

        public ValueTask ReplyAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default) => ValueTask.CompletedTask;
    }

    [Fact]
    public void CorrId生成_单调且唯一()
    {
        var broker = new RequestBroker(16);
        var seen = new HashSet<ulong>();
        for (int i = 0; i < 100; i++)
        {
            var pending = broker.BeginRequest();
            seen.Add(pending.CorrelationId).Should().BeTrue();
            broker.Abandon(pending);   // 清理（容量 16——不清则满）
        }
    }

    [Fact]
    public void 关联表满_BeginRequest抛()
    {
        var broker = new RequestBroker(2);
        broker.BeginRequest();
        broker.BeginRequest();
        RequestBroker.PendingRequest Begin() => broker.BeginRequest();
        var act = Begin;
        act.Should().Throw<InvalidOperationException>("在途超限 fail-fast——静默排队掩盖并发失控");
    }

    [Fact]
    public async Task 等待_应答到达_完成()
    {
        var broker = new RequestBroker(4);
        var pending = broker.BeginRequest();
        var waiting = broker.WaitAsync(pending, TimeSpan.FromSeconds(5), CancellationToken.None);
        waiting.IsCompleted.Should().BeFalse();

        broker.OnResponse(pending.CorrelationId, new byte[] { 7, 7 });
        var result = await waiting;
        result.Should().Equal(new byte[] { 7, 7 });
        broker.PendingCount.Should().Be(0, "应答完成即移除——防泄漏");
    }

    [Fact]
    public async Task 等待_超时_抛TimeoutException()
    {
        var broker = new RequestBroker(4);
        var pending = broker.BeginRequest();
        var act = () => broker.WaitAsync(pending, TimeSpan.FromMilliseconds(50), CancellationToken.None);
        await act.Should().ThrowAsync<TimeoutException>();
        broker.Abandon(pending);   // 调用方终态清理（介质 finally 承担——broker 侧 Abandon 幂等）
    }

    [Fact]
    public async Task 等待_取消_传播()
    {
        var broker = new RequestBroker(4);
        var pending = broker.BeginRequest();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var act = () => broker.WaitAsync(pending, TimeSpan.FromSeconds(5), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void 迟到应答_未知CorrId_忽略()
    {
        var broker = new RequestBroker(4);
        var act = () => broker.OnResponse(0xDEADBEEF, new byte[] { 1 });
        act.Should().NotThrow("超时后的应答迟到——忽略（at-most-once 无重放）");
    }

    [Fact]
    public void Abandon_幂等()
    {
        var broker = new RequestBroker(4);
        var pending = broker.BeginRequest();
        broker.Abandon(pending);
        var act = () => broker.Abandon(pending);
        act.Should().NotThrow();
    }

    [Fact]
    public void 注册分流_公开口内部号抛_重复抛()
    {
        var broker = new RequestBroker(4);
        ((Action)(() => broker.RegisterUserHandler(0x01, new SilentHandler())))
            .Should().Throw<ArgumentOutOfRangeException>();
        broker.RegisterUserHandler(0x64, new SilentHandler());
        ((Action)(() => broker.RegisterUserHandler(0x64, new SilentHandler())))
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void 分发_未注册协议_静默丢弃()
    {
        var broker = new RequestBroker(4);
        var act = () => broker.DispatchRequest(NodeId.NewRandom(), 0x90, 1, new byte[] { 1 }, new StubReply(NodeId.NewRandom(), 0x90, 1));
        act.Should().NotThrow();
    }

    [Fact]
    public void 分发_handler异常_不外泄_应答上下文完整()
    {
        var broker = new RequestBroker(4);
        var handler = new ThrowingHandler();
        broker.RegisterUserHandler(0x65, handler);
        var peer = NodeId.NewRandom();

        var act = () => broker.DispatchRequest(peer, 0x65, 9, new byte[] { 1 }, new StubReply(peer, 0x65, 9));
        act.Should().NotThrow("handler 异常隔离——不回复 = 对端超时（§5.2）");
    }

    [Fact]
    public async Task 分发_handler收到的上下文完整()
    {
        var broker = new RequestBroker(4);
        IReplyContext? seen = null;
        var handler = new CallbackHandler(ctx => seen = ctx);
        broker.RegisterUserHandler(0x64, handler);
        var peer = NodeId.NewRandom();

        broker.DispatchRequest(peer, 0x64, 77, new byte[] { 3 }, new StubReply(peer, 0x64, 77));
        await Task.Yield();
        seen.Should().NotBeNull();
        seen!.Peer.Should().Be(peer);
        seen.ProtocolId.Should().Be(0x64);
        seen.CorrelationId.Should().Be(77);
    }

    [Fact]
    public async Task 应答早于等待_直排竞态_等待立即完成()
    {
        // InProcess 直排介质实测形态：DeliverRequestAsync 同步完成整个回程——
        // 应答到达时调用方尚未进入 WaitAsync（句柄持完成通道，表只是路由索引）
        var broker = new RequestBroker(4);
        var pending = broker.BeginRequest();
        broker.OnResponse(pending.CorrelationId, new byte[] { 5 });

        var result = await broker.WaitAsync(pending, TimeSpan.FromSeconds(1), CancellationToken.None);
        result.Should().Equal(new byte[] { 5 });
        broker.PendingCount.Should().Be(0);
    }

    // ══ 发送协调（SendAsync——at-most-once ∥ at-least-once 重发）══

    [Fact]
    public async Task SendAsync_atMostOnce_单发无重试()
    {
        var broker = new RequestBroker(4);
        int sends = 0;
        var act = async () => await broker.SendAsync(
            (corrId, ct) => { sends++; return ValueTask.CompletedTask; },
            TimeSpan.FromMilliseconds(50), retry: null, CancellationToken.None);
        await act.Should().ThrowAsync<TimeoutException>();
        sends.Should().Be(1, "缺省 at-most-once——单发 + 超时");
    }

    [Fact]
    public async Task SendAsync_atLeastOnce_次数耗尽_超时抛()
    {
        var broker = new RequestBroker(4);
        int sends = 0;
        var act = async () => await broker.SendAsync(
            (corrId, ct) => { sends++; return ValueTask.CompletedTask; },
            timeout: TimeSpan.FromSeconds(5),
            retry: new RetryPolicy(MaxAttempts: 3, Backoff: TimeSpan.FromMilliseconds(50)),
            CancellationToken.None);
        await act.Should().ThrowAsync<TimeoutException>();
        sends.Should().Be(3, "重发同 CorrId 直至次数上限");
        broker.PendingCount.Should().Be(0, "终态清理");
    }

    [Fact]
    public async Task SendAsync_介质发送失败_外泄不重试()
    {
        var broker = new RequestBroker(4);
        int sends = 0;
        var act = async () => await broker.SendAsync(
            (corrId, ct) => { sends++; throw new NetIOException("链路断"); },
            timeout: TimeSpan.FromSeconds(1),
            retry: new RetryPolicy(MaxAttempts: 5, Backoff: TimeSpan.FromMilliseconds(50)),
            CancellationToken.None);
        await act.Should().ThrowAsync<NetIOException>("链路断重发也断——重试只针对应答未达");
        sends.Should().Be(1);
    }

    [Fact]
    public async Task SendAsync_矛盾RetryPolicy_抛()
    {
        var broker = new RequestBroker(4);
        var act = async () => await broker.SendAsync((_, _) => ValueTask.CompletedTask,
            TimeSpan.FromSeconds(1), new RetryPolicy(MaxAttempts: 0), CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    private sealed class CallbackHandler(Action<IReplyContext> onCalled) : IRequestHandler
    {
        public void OnRequest(NodeId from, ReadOnlyMemory<byte> payload, IReplyContext reply) => onCalled(reply);
    }
}
