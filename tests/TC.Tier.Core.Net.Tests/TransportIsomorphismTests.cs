using System.Collections.Concurrent;
using System.Net;
using System.Runtime.Versioning;
using FluentAssertions;
using TC.Tier.Core.Net;
using TC.Tier.Core.Net.Channels;
using TC.Tier.Core.Net.Transport;
using TC.Tier.Core.Net.Transport.Quic;
using Xunit;
using Skip = Xunit.Skip;

namespace TC.Tier.Core.Net.Tests;

/// <summary>
/// 介质同构契约测试（spec-12 §12 同构门缩小版——D2-T4 首轮）：同一套消费面场景
/// 不改一行跑在 TCP 与 InProcess 两介质上（<see cref="IProtocolTransport"/> 节点端点完整面驱动）。
/// <para>断言口径 = 消费面语义（数据报到达/尽力丢弃/注入矩阵/注册分流）；
/// 介质实现细节（TCP 链路事件/连接管理）不入同构断言。</para>
/// </summary>
public class TransportIsomorphismTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);

    /// <summary>链路建立时限（判例 2026-09-02：混跑调度毛刺下 loopback 握手可超 5s——传输层
    /// 退避重试恒会建立，测试耐心是唯一变量——建立等待与消息等待分档）。</summary>
    private static readonly TimeSpan EstablishLimit = TimeSpan.FromSeconds(15);

    // ══ 介质装配 rig（同一消费面——装配差异全吸收在此）══

    public interface IMediumRig : IAsyncDisposable
    {
        Task<(IProtocolTransport A, IProtocolTransport B, NodeId AId, NodeId BId)> CreatePairAsync();
    }

    public sealed class TcpMedium : IMediumRig
    {
        private ClusterTransport? _low, _high;

        public async Task<(IProtocolTransport, IProtocolTransport, NodeId, NodeId)> CreatePairAsync()
        {
            var a = NodeId.NewRandom();
            var b = NodeId.NewRandom();
            (NodeId Low, NodeId High) = a.CompareTo(b) < 0 ? (a, b) : (b, a);

            _high = new ClusterTransport(High,
                TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, 0), new Dictionary<NodeId, IPEndPoint> { [Low] = new(IPAddress.Loopback, 0) }));
            _high.Start();
            _low = new ClusterTransport(Low,
                TransportOptions.Default(null, new Dictionary<NodeId, IPEndPoint> { [High] = _high.LocalEndPoint! }));
            _low.Start();

            var lowUp = new TaskCompletionSource();
            var highUp = new TaskCompletionSource();
            _low.PeerConnected += _ => lowUp.TrySetResult();
            _high.PeerConnected += _ => highUp.TrySetResult();
            await lowUp.Task.WaitAsync(EstablishLimit);
            await highUp.Task.WaitAsync(EstablishLimit);
            return (_low, _high, Low, High);
        }

        public async ValueTask DisposeAsync()
        {
            if (_low is not null) await _low.DisposeAsync();
            if (_high is not null) await _high.DisposeAsync();
        }

        public override string ToString() => "TCP";
    }

    public sealed class InProcessMedium : IMediumRig
    {
        private InProcessTransportHub? _hub;

        public Task<(IProtocolTransport, IProtocolTransport, NodeId, NodeId)> CreatePairAsync()
        {
            _hub = new InProcessTransportHub();
            var aId = NodeId.NewRandom();
            var bId = NodeId.NewRandom();
            var a = _hub.Register(aId);
            var b = _hub.Register(bId);
            a.Start();
            b.Start();
            return Task.FromResult<(IProtocolTransport, IProtocolTransport, NodeId, NodeId)>((a, b, aId, bId));
        }

        public ValueTask DisposeAsync() => _hub?.DisposeAsync() ?? ValueTask.CompletedTask;

        public override string ToString() => "InProcess";
    }

    public static TheoryData<IMediumRig> Media => new() { new TcpMedium(), new InProcessMedium(), new QuicMedium() };

    /// <summary>
    /// QUIC 介质装配（spec-11 W5——同构门第三介质）。★ MsQuic 运行时依赖（Windows 11 内置；
    /// Linux 须 libmsquic）——不可用 = 运行时 Skip（环境依赖介质门控；xunit 2.9 Assert.Skip）。
    /// </summary>
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    [SupportedOSPlatform("windows")]
    public sealed class QuicMedium : IMediumRig
    {
        private QuicTransport? _low, _high;

        public async Task<(IProtocolTransport, IProtocolTransport, NodeId, NodeId)> CreatePairAsync()
        {
            Skip.IfNot(System.Net.Quic.QuicListener.IsSupported, "MsQuic 运行时不可用——QUIC 介质场景跳过。");
            var a = NodeId.NewRandom();
            var b = NodeId.NewRandom();
            (NodeId Low, NodeId High) = a.CompareTo(b) < 0 ? (a, b) : (b, a);

            _high = new QuicTransport(High,
                TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, 0), new Dictionary<NodeId, IPEndPoint>()));
            _high.Start();
            _low = new QuicTransport(Low,
                TransportOptions.Default(null, new Dictionary<NodeId, IPEndPoint> { [High] = _high.LocalEndPoint! }));
            _low.Start();

            var lowUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var highUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _low.PeerConnected += _ => lowUp.TrySetResult();
            _high.PeerConnected += _ => highUp.TrySetResult();

            // 首次发送触发惰性拨号
            await _low.SendDatagramAsync(High, 0x60, new byte[] { 0x00 });
            await Task.WhenAll(lowUp.Task, highUp.Task).WaitAsync(TimeSpan.FromSeconds(10));
            return (_low, _high, Low, High);
        }

        public async ValueTask DisposeAsync()
        {
            if (_low is not null) await _low.DisposeAsync();
            if (_high is not null) await _high.DisposeAsync();
        }

        public override string ToString() => "QUIC";
    }

    public sealed class CapturingHandler(TaskCompletionSource<(NodeId From, byte[] Payload)> tcs) : IDatagramHandler
    {
        public void OnDatagram(NodeId from, ReadOnlyMemory<byte> payload) => tcs.TrySetResult((from, payload.ToArray()));
    }

    public sealed class ThrowingHandler : IDatagramHandler
    {
        public void OnDatagram(NodeId from, ReadOnlyMemory<byte> payload) => throw new InvalidOperationException("使用方 handler 故障");
    }

    [SkippableTheory]
    [MemberData(nameof(Media))]
    public async Task 数据报_双向往返载荷完整(IMediumRig medium)
    {
        await using (medium)
        {
            var (a, b, aId, bId) = await medium.CreatePairAsync();
            var atB = new TaskCompletionSource<(NodeId, byte[])>();
            var atA = new TaskCompletionSource<(NodeId, byte[])>();
            b.RegisterProtocol(0x64, new CapturingHandler(atB));
            a.RegisterProtocol(0x64, new CapturingHandler(atA));

            await a.SendDatagramAsync(bId, 0x64, new byte[] { 1, 2, 3 });
            var got = await atB.Task.WaitAsync(WaitLimit);
            got.Item1.Should().Be(aId);
            got.Item2.Should().Equal(new byte[] { 1, 2, 3 });

            await b.SendDatagramAsync(aId, 0x64, new byte[] { 9 });
            var back = await atA.Task.WaitAsync(WaitLimit);
            back.Item1.Should().Be(bId);
            back.Item2.Should().Equal(new byte[] { 9 });
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Media))]
    public async Task 数据报_目标未知_静默丢弃不抛(IMediumRig medium)
    {
        await using (medium)
        {
            var (a, _, _, _) = await medium.CreatePairAsync();
            var act = () => a.SendDatagramAsync(NodeId.NewRandom(), 0x64, new byte[] { 1 }).AsTask();
            await act.Should().NotThrowAsync("目标不可达 = 尽力送达静默丢弃（§5.1）");
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Media))]
    public async Task 数据报_未注册协议_丢弃_后续可用(IMediumRig medium)
    {
        await using (medium)
        {
            var (a, b, _, bId) = await medium.CreatePairAsync();
            var received = new TaskCompletionSource<(NodeId, byte[])>();
            b.RegisterProtocol(0x64, new CapturingHandler(received));

            await a.SendDatagramAsync(bId, 0x90, new byte[] { 1 });   // 未注册
            await Task.Delay(200);                                     // 丢弃时间窗（介质投递异步性）
            received.Task.IsCompleted.Should().BeFalse();

            await a.SendDatagramAsync(bId, 0x64, new byte[] { 2 });    // 已注册照常到达
            (await received.Task.WaitAsync(WaitLimit)).Item2.Should().Equal(new byte[] { 2 });
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Media))]
    public async Task 数据报_handler异常_不外泄不致命(IMediumRig medium)
    {
        await using (medium)
        {
            var (a, b, _, bId) = await medium.CreatePairAsync();
            b.RegisterProtocol(0x65, new ThrowingHandler());
            var received = new TaskCompletionSource<(NodeId, byte[])>();
            b.RegisterProtocol(0x64, new CapturingHandler(received));

            var act = () => a.SendDatagramAsync(bId, 0x65, new byte[] { 1 }).AsTask();
            await act.Should().NotThrowAsync("handler 异常隔离在接收侧——不外泄到发送方");
            await a.SendDatagramAsync(bId, 0x64, new byte[] { 2 });   // 同一节点其他协议域照常
            (await received.Task.WaitAsync(WaitLimit)).Item2.Should().Equal(new byte[] { 2 });
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Media))]
    public async Task 注册_内部号走公开口_抛(IMediumRig medium)
    {
        await using (medium)
        {
            var (_, b, _, _) = await medium.CreatePairAsync();
            var act = () => b.RegisterProtocol(0x01, new ThrowingHandler());   // 0x01 = 内部核心区（Raft）
            act.Should().Throw<ArgumentOutOfRangeException>("公开口只放行注册区 0x60-0xAF（§3.5 分流——全介质单源校验）");
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Media))]
    public async Task 注册_重复注册_抛(IMediumRig medium)
    {
        await using (medium)
        {
            var (_, b, _, _) = await medium.CreatePairAsync();
            b.RegisterProtocol(0x64, new ThrowingHandler());
            var act = () => b.RegisterProtocol(0x64, new CapturingHandler(new()));
            act.Should().Throw<InvalidOperationException>();
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Media))]
    public async Task 注入_丢包全丢_Reset后可达(IMediumRig medium)
    {
        await using (medium)
        {
            var (a, b, aId, bId) = await medium.CreatePairAsync();
            var received = new TaskCompletionSource<(NodeId, byte[])>();
            b.RegisterProtocol(0x64, new CapturingHandler(received));

            a.Faults.Drop(aId, bId, 1.0);
            await a.SendDatagramAsync(bId, 0x64, new byte[] { 1 });
            await Assert.ThrowsAsync<TimeoutException>(() => received.Task.WaitAsync(TimeSpan.FromMilliseconds(400)));

            a.Faults.Reset();
            await a.SendDatagramAsync(bId, 0x64, new byte[] { 2 });
            (await received.Task.WaitAsync(WaitLimit)).Item2.Should().Equal(new byte[] { 2 });
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Media))]
    public async Task 注入_延迟_到达变慢(IMediumRig medium)
    {
        await using (medium)
        {
            var (a, b, aId, bId) = await medium.CreatePairAsync();
            var received = new TaskCompletionSource<(NodeId, byte[])>();
            b.RegisterProtocol(0x64, new CapturingHandler(received));

            a.Faults.SetLatency(aId, bId, TimeSpan.FromMilliseconds(150));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await a.SendDatagramAsync(bId, 0x64, new byte[] { 5 });
            await received.Task.WaitAsync(WaitLimit);
            sw.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(120));
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Media))]
    public async Task 注入_分区_不可达_愈合后可达(IMediumRig medium)
    {
        await using (medium)
        {
            var (a, b, aId, bId) = await medium.CreatePairAsync();
            var received = new TaskCompletionSource<(NodeId, byte[])>();
            b.RegisterProtocol(0x64, new CapturingHandler(received));

            a.Faults.Partition([aId], [bId]);
            await a.SendDatagramAsync(bId, 0x64, new byte[] { 1 });
            await Assert.ThrowsAsync<TimeoutException>(() => received.Task.WaitAsync(TimeSpan.FromMilliseconds(400)));

            a.Faults.Reset();
            // TCP 断链重连由拨号退避驱动（期间发送 = 尽力丢弃），消费面以重试自愈——
            // §5.1 协议域自愈形态（同构断言口径，不依赖介质的连接事件差异）。
            // ★ 愈合窗口 = 建立时限档（判例 2026-09-02：重连握手在混跑调度毛刺下可超 5s——
            //   与初始建立同耐心）
            var healDeadline = System.Diagnostics.Stopwatch.StartNew();
            while (!received.Task.IsCompleted && healDeadline.Elapsed < EstablishLimit)
            {
                await a.SendDatagramAsync(bId, 0x64, new byte[] { 2 });
                await Task.Delay(100);
            }
            (await received.Task.WaitAsync(EstablishLimit)).Item2.Should().Equal(new byte[] { 2 });
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Media))]
    public async Task Dispose后发送_抛ObjectDisposed(IMediumRig medium)
    {
        await using (medium)
        {
            var (a, _, _, bId) = await medium.CreatePairAsync();
            await a.DisposeAsync();
            var act = () => a.SendDatagramAsync(bId, 0x64, new byte[] { 1 }).AsTask();
            await act.Should().ThrowAsync<ObjectDisposedException>();
        }
    }

    // ══ 请求回调（spec-12 §5.2——两介质消费面零差别）══

    private sealed class EchoRequestHandler : IRequestHandler
    {
        public void OnRequest(NodeId from, ReadOnlyMemory<byte> payload, IReplyContext reply)
            => _ = reply.ReplyAsync(payload.ToArray()).AsTask();   // CA2012：转 Task 尽力回显——直排/读循环上下文立即回程
    }

    private sealed class SilentRequestHandler : IRequestHandler
    {
        public void OnRequest(NodeId from, ReadOnlyMemory<byte> payload, IReplyContext reply)
        {
        }   // 不回复 = 对端超时
    }

    [SkippableTheory]
    [MemberData(nameof(Media))]
    public async Task 请求回调_应答往返载荷完整(IMediumRig medium)
    {
        await using (medium)
        {
            var (a, b, _, bId) = await medium.CreatePairAsync();
            b.RegisterRequestHandler(0x64, new EchoRequestHandler());

            // ★ at-least-once 重试 + 长预算（判例 2026-09-02：混跑下首轮往返超 3s 缺省超时——
            //   去重窗口保证不重复执行，载荷断言不受重试影响）
            var reply = await a.SendRequestAsync(bId, 0x64, new byte[] { 9, 8, 7 },
                new RequestOptions(Retry: new RetryPolicy(MaxAttempts: 20, Backoff: TimeSpan.FromMilliseconds(200),
                    TotalBudget: TimeSpan.FromSeconds(10))));
            reply.Should().Equal(new byte[] { 9, 8, 7 });
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Media))]
    public async Task 请求回调_不回复_超时抛(IMediumRig medium)
    {
        await using (medium)
        {
            var (a, b, _, bId) = await medium.CreatePairAsync();
            b.RegisterRequestHandler(0x64, new SilentRequestHandler());

            var act = async () => await a.SendRequestAsync(bId, 0x64, new byte[] { 1 }, new RequestOptions(TimeSpan.FromMilliseconds(300)));
            await act.Should().ThrowAsync<TimeoutException>("无应答 = 超时（§5.2 单发 + 超时——at-most-once 缺省）");
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Media))]
    public async Task 请求回调_取消传播(IMediumRig medium)
    {
        await using (medium)
        {
            var (a, b, _, bId) = await medium.CreatePairAsync();
            b.RegisterRequestHandler(0x64, new SilentRequestHandler());

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            var act = async () => await a.SendRequestAsync(bId, 0x64, new byte[] { 1 }, new RequestOptions(TimeSpan.FromSeconds(10)), cts.Token);
            await act.Should().ThrowAsync<OperationCanceledException>();
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Media))]
    public async Task 请求回调_目标未知_抛NetIOException(IMediumRig medium)
    {
        await using (medium)
        {
            var (a, _, _, _) = await medium.CreatePairAsync();
            var act = async () => await a.SendRequestAsync(NodeId.NewRandom(), 0x64, new byte[] { 1 }, new RequestOptions(TimeSpan.FromMilliseconds(300)));
            await act.Should().ThrowAsync<NetIOException>("调用方有应答期待——不静默浪费超时窗");
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Media))]
    public async Task 请求回调_未注册协议_超时(IMediumRig medium)
    {
        await using (medium)
        {
            var (a, _, _, bId) = await medium.CreatePairAsync();
            var act = async () => await a.SendRequestAsync(bId, 0x90, new byte[] { 1 }, new RequestOptions(TimeSpan.FromMilliseconds(300)));
            await act.Should().ThrowAsync<TimeoutException>("未注册协议的请求被丢弃——对端视角即无应答超时");
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Media))]
    public async Task 请求回调_注入丢包_超时后Reset可达(IMediumRig medium)
    {
        await using (medium)
        {
            var (a, b, aId, bId) = await medium.CreatePairAsync();
            b.RegisterRequestHandler(0x64, new EchoRequestHandler());

            a.Faults.Drop(aId, bId, 1.0);
            var dropped = async () => await a.SendRequestAsync(bId, 0x64, new byte[] { 1 }, new RequestOptions(TimeSpan.FromMilliseconds(300)));
            await dropped.Should().ThrowAsync<TimeoutException>("注入丢弃 = 静默（真实网络丢包语义——发送方无从得知，两介质同构）");

            a.Faults.Reset();
            var reply = await a.SendRequestAsync(bId, 0x64, new byte[] { 2 });
            reply.Should().Equal(new byte[] { 2 });
        }
    }

    // ══ 投递语义（spec-12 §5.2——at-most-once 缺省 ∥ at-least-once 同 CorrId 重发+去重重放）══

    /// <summary>计数不回复 handler——测试主体控制应答时序（确定性，不依赖介质抖动）。</summary>
    private sealed class CountingDeferredHandler(ConcurrentQueue<IReplyContext> replies) : IRequestHandler
    {
        public int Served;

        public void OnRequest(NodeId from, ReadOnlyMemory<byte> payload, IReplyContext reply)
        {
            Interlocked.Increment(ref Served);
            replies.Enqueue(reply);
        }
    }

    private sealed class CountingImmediateHandler : IRequestHandler
    {
        public int Served;

        public void OnRequest(NodeId from, ReadOnlyMemory<byte> payload, IReplyContext reply)
        {
            Interlocked.Increment(ref Served);
            _ = reply.ReplyAsync(payload.ToArray()).AsTask();   // CA2012：转 Task 尽力回显
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Media))]
    public async Task 投递语义_atMostOnce缺省_不重发(IMediumRig medium)
    {
        await using (medium)
        {
            var (a, b, _, bId) = await medium.CreatePairAsync();
            var replies = new ConcurrentQueue<IReplyContext>();
            var handler = new CountingDeferredHandler(replies);
            b.RegisterRequestHandler(0x64, handler);

            var act = async () => await a.SendRequestAsync(bId, 0x64, new byte[] { 1 }, new RequestOptions(TimeSpan.FromMilliseconds(300)));
            await act.Should().ThrowAsync<TimeoutException>();
            handler.Served.Should().Be(1, "at-most-once 缺省——单发 + 超时，可靠是显式选择（§5.2）");
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Media))]
    public async Task 投递语义_atLeastOnce_重发不重复执行(IMediumRig medium)
    {
        await using (medium)
        {
            var (a, b, _, bId) = await medium.CreatePairAsync();
            var replies = new ConcurrentQueue<IReplyContext>();
            var handler = new CountingDeferredHandler(replies);
            b.RegisterRequestHandler(0x64, handler);

            // 重发节奏 100ms；等"至少两次到达"（首达 + 重发被去重拦截）——满载下不绑时间点
            //   （判例：250ms 硬编码观察点在混跑负载下击穿——首帧建立/首达超 250ms 时观察为 0）。
            // ★ 预算必须覆盖本测试自身观测窗（≤4s）+ 负载慢发送（判例 2026-09-02：800ms 额度在
            //   观测/慢发送下耗尽——请求先于人工应答死透）
            var request = a.SendRequestAsync(bId, 0x64, new byte[] { 5 },
                new RequestOptions(Retry: new RetryPolicy(MaxAttempts: 60, Backoff: TimeSpan.FromMilliseconds(100),
                    TotalBudget: TimeSpan.FromSeconds(10))));
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (DateTime.UtcNow < deadline && handler.Served == 0)
                await Task.Delay(25);
            var dedupDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (DateTime.UtcNow < dedupDeadline && handler.Served == 1 && replies.IsEmpty)
                await Task.Delay(25);
            handler.Served.Should().Be(1, "重发同 CorrId 到达——去重窗口拦截（不重复执行）");
            replies.Count.Should().Be(1);

            await replies.Single().ReplyAsync(new byte[] { 6 });
            var reply = await request.AsTask().WaitAsync(WaitLimit);
            reply.Should().Equal(new byte[] { 6 });
            handler.Served.Should().Be(1, "全程单次执行——at-least-once 的应答端保证");
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Media))]
    public async Task 投递语义_atLeastOnce_应答延迟_重放缓存不重复执行(IMediumRig medium)
    {
        await using (medium)
        {
            var (a, b, aId, bId) = await medium.CreatePairAsync();
            var handler = new CountingImmediateHandler();
            b.RegisterRequestHandler(0x64, handler);

            a.Faults.SetLatency(bId, aId, TimeSpan.FromMilliseconds(400));   // 应答方向延迟——首个应答在途时重发到达
            // ★ 预算须覆盖注入延迟（400ms）+ 负载慢发送（判例 2026-09-02：800ms 额度耗尽于
            //   首达延迟本身——请求死透无应答可重放）
            var reply = await a.SendRequestAsync(bId, 0x64, new byte[] { 7 },
                new RequestOptions(Retry: new RetryPolicy(MaxAttempts: 60, Backoff: TimeSpan.FromMilliseconds(100),
                    TotalBudget: TimeSpan.FromSeconds(10))));
            reply.Should().Equal(new byte[] { 7 });
            handler.Served.Should().Be(1, "重发到达命中去重缓存——重放响应，不重复执行");
        }
    }

    // ══ 流式会话（spec-12 §5.3——两介质消费面零差别；背压传导 / Reset / 未注册拒绝）══

    private sealed class CollectingAcceptor(TaskCompletionSource<IWireStream> accepted) : IStreamAcceptor
    {
        public void OnStream(NodeId from, IWireStream stream, ReadOnlyMemory<byte> openPayload) => accepted.TrySetResult(stream);
    }

    [SkippableTheory]
    [MemberData(nameof(Media))]
    public async Task 流式_开流写读收尾(IMediumRig medium)
    {
        await using (medium)
        {
            var (a, b, _, bId) = await medium.CreatePairAsync();
            var accepted = new TaskCompletionSource<IWireStream>();
            b.RegisterStreamAcceptor(0x64, new CollectingAcceptor(accepted));

            var stream = await a.OpenStreamAsync(bId, 0x64);
            var peerStream = await accepted.Task.WaitAsync(WaitLimit);
            stream.Peer.Should().Be(bId);
            peerStream.ProtocolId.Should().Be(0x64);

            var frames = new byte[][] { new byte[] { 1 }, new byte[] { 2, 2 }, new byte[] { 3, 3, 3 } };
            foreach (var frame in frames) await stream.WriteAsync(frame);
            await stream.CompleteAsync();

            int received = 0;
            await foreach (var frame in peerStream.ReadAllAsync())
            {
                frame.ToArray().Should().Equal(frames[received]);
                received++;
            }
            received.Should().Be(frames.Length, "Complete 后读枚举自然结束——可靠有序全达");
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Media))]
    public async Task 流式_未注册acceptor_开流失败(IMediumRig medium)
    {
        await using (medium)
        {
            var (a, _, _, bId) = await medium.CreatePairAsync();
            var act = () => a.OpenStreamAsync(bId, 0x90).AsTask();
            if (a is ClusterTransport)
            {
                // TCP：对端 Reset 拒绝——发起端开流失败（Reset/超时二一）
                (await act.Should().ThrowAsync<Exception>("未注册 acceptor 的协议域拒绝开流")).Which.Should().BeAssignableTo<NetIOException>();
            }
            else
            {
                await act.Should().ThrowAsync<NetIOException>("内存直路由立即拒绝");
            }
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Media))]
    public async Task 流式_背压传导_读端不读写端挂起_消费后完成(IMediumRig medium)
    {
        await using (medium)
        {
            var (a, b, _, bId) = await medium.CreatePairAsync();
            var accepted = new TaskCompletionSource<IWireStream>();
            b.RegisterStreamAcceptor(0x64, new CollectingAcceptor(accepted));

            var stream = await a.OpenStreamAsync(bId, 0x64);
            var peerStream = await accepted.Task.WaitAsync(WaitLimit);

            // 读端不读——写端持续写直至背压窗口 + 传输缓冲满 → WriteAsync 挂起
            const int frameSize = 256 * 1024;
            var frame = new byte[frameSize];
            var writing = WriteManyAsync(stream, frame, 512).AsTask();   // 128MB 总量——远超窗口
            await Task.Delay(1500);
            writing.IsCompleted.Should().BeFalse("读端不消费——背压传导到写端 await（§5.3/§7）");

            var consuming = ConsumeAllAsync(peerStream);
            await Task.WhenAll(writing, consuming).WaitAsync(TimeSpan.FromSeconds(30));
            (await consuming).Should().Be(512L * frameSize, "消费开始后写完成——总量全达（O(单帧) 流过）");
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Media))]
    public async Task 流式_Reset中止_对端写终止(IMediumRig medium)
    {
        await using (medium)
        {
            var (a, b, _, bId) = await medium.CreatePairAsync();
            var accepted = new TaskCompletionSource<IWireStream>();
            b.RegisterStreamAcceptor(0x64, new CollectingAcceptor(accepted));

            var stream = await a.OpenStreamAsync(bId, 0x64);
            var peerStream = await accepted.Task.WaitAsync(WaitLimit);
            await stream.WriteAsync(new byte[] { 1 });

            await peerStream.DisposeAsync();   // Reset
            await Task.Delay(300);             // Reset 到达传播

            var act = () => stream.WriteAsync(new byte[] { 2 }).AsTask();
            await act.Should().ThrowAsync<NetIOException>("对端 Reset——写立即终止（不跨恢复）");
        }
    }

    /// <summary>
    /// ★ TCP 专属回归（2026-09-01 dumpasync 实锤的楔死根因）：流写在途背压挂起时，
    /// 数据报发送调用必须有界返回——旧缺陷 = PeerLink 写锁覆盖 await socket 写：流写挂起持锁、
    /// 数据报写者排队等锁饿死（全量套件楔死的共同形态：流写挂 socket 写 + 数据报等锁）。
    /// 写队列化+双优先分流后：写者入队即返回（控制帧走高优先队列插队——不等流写者）。
    /// <para>★ 断言面 = 发送调用返回（到达需对端读——读侧流控窗口另案）；消费后数据报到达+流全达。</para>
    /// <para>★ 只跑 TCP（socket 背压形态——InProcess 直排无此机制；同构面零差别不适用）。</para>
    /// </summary>
    [Fact]
    public async Task 流式背压_TCP_数据报发送不饿死()
    {
        await using var medium = new TcpMedium();
        var (a, b, _, bId) = await medium.CreatePairAsync();

        // B 侧：数据报 handler + 流 acceptor（不消费——背压形态）
        var datagramReceived = new TaskCompletionSource<(NodeId From, byte[] Payload)>(TaskCreationOptions.RunContinuationsAsynchronously);
        b.RegisterProtocol(0x65, new CapturingHandler(datagramReceived));
        var accepted = new TaskCompletionSource<IWireStream>(TaskCreationOptions.RunContinuationsAsynchronously);
        b.RegisterStreamAcceptor(0x64, new CollectingAcceptor(accepted));

        var stream = await a.OpenStreamAsync(bId, 0x64);
        var peerStream = await accepted.Task.WaitAsync(WaitLimit);

        // A 侧：流写挂起（读端不读——socket 发送缓冲满）+ 并发数据报发送
        var writing = WriteManyAsync(stream, new byte[256 * 1024], 512).AsTask();   // 128MB 总量——远超窗口
        await Task.Delay(1500);
        writing.IsCompleted.Should().BeFalse("读端不消费——流写背压挂起（前置形态）");

        // ★ 缺陷锁定：数据报发送调用有界返回（旧写锁形态 = 等锁挂到流写完成——5s 界内必超时）
        var send = a.SendDatagramAsync(bId, 0x65, new byte[] { 0xAA, 0xBB }).AsTask();
        await send.WaitAsync(TimeSpan.FromSeconds(5));

        // 消费开始——流完成 + 数据报有界到达
        var consuming = ConsumeAllAsync(peerStream);
        await Task.WhenAll(writing, consuming).WaitAsync(TimeSpan.FromSeconds(30));
        (await consuming).Should().Be(512L * 256 * 1024, "消费开始后流写完成——总量全达");

        var (from, payload) = await datagramReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        from.Should().Be(a.Self);
        payload.Should().Equal(new byte[] { 0xAA, 0xBB }, "消费后数据报有界到达（读侧流控窗口另案）");
    }

    [SkippableTheory]
    [MemberData(nameof(Media))]
    public async Task 形态契约_三形态同节点并用互不干扰(IMediumRig medium)
    {
        await using (medium)
        {
            var (a, b, _, bId) = await medium.CreatePairAsync();

            var datagram = new TaskCompletionSource<(NodeId, byte[])>();
            b.RegisterProtocol(0x64, new CapturingHandler(datagram));
            var accepted = new TaskCompletionSource<IWireStream>();
            b.RegisterStreamAcceptor(0x65, new CollectingAcceptor(accepted));
            b.RegisterRequestHandler(0x66, new EchoRequestHandler());

            // 流式在途（长写）与数据报/请求回调并发——三者共用链路/读循环互不阻塞
            var stream = await a.OpenStreamAsync(bId, 0x65);
            var peerStream = await accepted.Task.WaitAsync(WaitLimit);
            var streamWriting = WriteManyAsync(stream, new byte[64 * 1024], 64);

            await a.SendDatagramAsync(bId, 0x64, new byte[] { 1 });
            (await datagram.Task.WaitAsync(WaitLimit)).Item2.Should().Equal(new byte[] { 1 });

            var reply = await a.SendRequestAsync(bId, 0x66, new byte[] { 2 });
            reply.Should().Equal(new byte[] { 2 });

            var consumed = await ConsumeAllAsync(peerStream);
            await streamWriting.AsTask();
            consumed.Should().Be(64L * 1024 * 64, "流式完成后总量全达——三形态按帧路由互不干扰");
        }
    }

    private static async ValueTask WriteManyAsync(IWireStream stream, byte[] frame, int count)
    {
        for (int i = 0; i < count; i++) await stream.WriteAsync(frame);
        await stream.CompleteAsync();
    }

    private static async Task<long> ConsumeAllAsync(IWireStream stream)
    {
        long total = 0;
        await foreach (var frame in stream.ReadAllAsync()) total += frame.Length;
        return total;
    }
}
