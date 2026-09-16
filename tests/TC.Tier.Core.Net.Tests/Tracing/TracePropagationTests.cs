using System.Collections.Concurrent;
using System.Text;
using FluentAssertions;
using TC.Tier.Core.Net.Channels;
using TC.Tier.Core.Net.Wire;
using TC.Tier.Core.Tracing;
using Xunit;

namespace TC.Tier.Core.Net.Tests.Tracing;

/// <summary>
/// 跨节点 trace 串联 + 升级矩阵（二期-I4/H2——验收：跨节点同 trace 串联、
/// 混版 N-1 兼容、特性位门控）：
/// ① SpanContext 线格式往返 + 前缀加/剥（含不匹配透传）；
/// ② RequestBroker 分发恢复服务端 span（parent = 线上上下文）+ 无 trace 零 span；
/// ③ ITracer default 传播契约（未覆写 = 无传播，不破坏既有实现）；
/// ④ 特性位升级矩阵：Trace 位交集生效、旧对端（无位）不联动、保留位拒收语义保持。
/// </summary>
public class TracePropagationTests
{
    /// <summary>收集型 tracer（外部 tracer 形态——覆写传播对，记录 BeginSpan 事件）。</summary>
    private sealed class CollectingTracer : ITracer
    {
        public bool IsEnabled => true;
        public ISpan? Current { get; private set; }
        public readonly ConcurrentQueue<(string Name, SpanKind Kind, byte[]? ParentWire)> Begun = new();

        public ISpan BeginSpan(string name, SpanKind kind = SpanKind.Internal)
        {
            Begun.Enqueue((name, kind, null));
            return new RecordingSpan();
        }

        public ISpan? BeginSpan(string name, SpanKind kind, ReadOnlySpan<byte> parentWireContext)
        {
            Begun.Enqueue((name, kind, parentWireContext.ToArray()));
            return new RecordingSpan();
        }

        public byte[]? CaptureContext()
        {
            var ctx = new byte[SpanContextCodec.ContextSize];
            ctx[0] = 0xAA;
            return ctx;
        }

        private sealed class RecordingSpan : ISpan
        {
            public void Dispose() { }
            public void SetTag(string key, string? value) { }
            public void SetTag(string key, long value) { }
            public void RecordException(Exception ex) { }
            public void SetStatus(SpanStatus status, string? description = null) { }
            public void AddEvent(string name) { }
        }
    }

    private static byte[] SampleContextWire(byte seed = 0x01)
    {
        var ctx = new byte[SpanContextCodec.ContextSize];
        Array.Fill(ctx, seed);
        return ctx;
    }

    /// <summary>验收①：线格式往返 + 前缀加/剥 + marker 不匹配原样透传。</summary>
    [Fact]
    public void SpanContext_WireRoundTrip_PrefixStrip()
    {
        var wire = SampleContextWire(0x5A);
        var ok = SpanContextCodec.TryDecode(wire, out var ctx);
        ok.Should().BeTrue();
        ctx.TraceId.Should().HaveCount(16).And.OnlyContain(b => b == 0x5A);
        ctx.SpanId.Should().HaveCount(8).And.OnlyContain(b => b == 0x5A);
        ctx.Flags.Should().Be(0x5A);

        var payload = new byte[] { 0x11, 0x22, 0x33 };
        var traced = SpanContextCodec.AttachPrefix(payload, wire);
        traced[0].Should().Be(SpanContextCodec.Marker);
        traced.Length.Should().Be(SpanContextCodec.PrefixSize + 3);

        var stripped = SpanContextCodec.TryStripPrefix(traced, out var wireCtx, out var consumed);
        stripped.Should().BeTrue();
        consumed.Should().Be(SpanContextCodec.PrefixSize);
        wireCtx.TraceId.Should().HaveCount(16);
        traced.AsSpan(consumed).ToArray().Should().Equal(payload, "剥离后业务载荷零变化");

        // 未协商对端收到同载荷（marker 巧合/无 marker）——原样透传
        SpanContextCodec.TryStripPrefix(payload, out _, out _).Should().BeFalse("无前缀不误剥");
        var badMarker = (byte[])traced.Clone();
        badMarker[0] = 0x00;
        SpanContextCodec.TryStripPrefix(badMarker, out _, out _).Should().BeFalse("marker 不匹配不剥");
        var truncated = new byte[] { SpanContextCodec.Marker, 0x01 };
        SpanContextCodec.TryStripPrefix(truncated, out _, out _).Should().BeFalse("缺长不剥");
    }

    /// <summary>验收②：分发恢复服务端 span——parent = 线上上下文；无 trace 零 span。</summary>
    [Fact]
    public async Task DispatchRequest_RestoresServerSpan_ParentFromWire()
    {
        var tracer = new CollectingTracer();
        var broker = new RequestBroker(16, tracer: tracer);
        byte[]? seenPayload = null;
        broker.RegisterUserHandler(0x70, new DelegateHandler(p =>
        {
            seenPayload = p.ToArray();
            return ValueTask.CompletedTask;
        }));

        var business = new byte[] { 0x0B, 0x0E };
        var contextWire = SampleContextWire(0x77);
        var reply = new StubReply();
        broker.DispatchRequest(NodeId.NewRandom(), 0x70, 42, business, reply, contextWire);

        seenPayload.Should().Equal(business, "trace 前缀由 PeerLink 剥离——broker 见业务载荷");
        tracer.Begun.Should().Contain(e => e.Name == "net.request" && e.Kind == SpanKind.Server,
            "分发处恢复服务端 span");
        tracer.Begun.Single(e => e.Name == "net.request").ParentWire.Should().Equal(contextWire,
            "跨节点串联：parent = 线上传播的上下文");

        // 无 trace 上下文——零新增 span
        var before = tracer.Begun.Count;
        broker.DispatchRequest(NodeId.NewRandom(), 0x70, 43, business, new StubReply());
        await Task.Delay(20);
        tracer.Begun.Count.Should().Be(before, "无线上下文不开 span（零开销路径）");
        seenPayload.Should().Equal(business);
    }

    /// <summary>验收③：ITracer default 契约——未覆写 = 父上下文被忽略、Capture 为 null。</summary>
    [Fact]
    public void TracerDefaultContract_NoPropagationWithoutOverride()
    {
        ITracer plain = new PlainTracer();
        var span = plain.BeginSpan("x", SpanKind.Server, SampleContextWire());
        span.Should().NotBeNull("default 实现退化为无父 BeginSpan");
        plain.CaptureContext().Should().BeNull("default 实现无传播能力——不破坏既有 tracer");
    }

    /// <summary>只实现基本契约的 tracer（未覆写传播对——default 方法生效）。</summary>
    private sealed class PlainTracer : ITracer
    {
        public bool IsEnabled => true;
        public ISpan? Current => null;
        public ISpan BeginSpan(string name, SpanKind kind = SpanKind.Internal) => NullSpan.Instance;
    }

    /// <summary>验收④：升级矩阵——Trace 位交集生效；v1 位清单语义保持。</summary>
    [Fact]
    public void UpgradeMatrix_TraceFeatureIntersects_LegacyPeerUnaffected()
    {
        const byte modern = HandshakeFeatures.Trace | HandshakeFeatures.Keepalive;
        const byte legacy = HandshakeFeatures.Keepalive;   // N-1 对端（不识 Trace）

        (modern & legacy).Should().Be(HandshakeFeatures.Keepalive, "交集协商——旧对端不见 Trace 位");
        (modern & modern).Should().Be(modern, "同代对端全位生效");

        // Trace 位不在保留掩码内（通告合法）；bit4-7 仍拒收
        ((HandshakeFeatures.Trace & HandshakeFeatures.ReservedMask) == 0)
            .Should().BeTrue("Trace = bit3 已收编出保留区");
        ((byte)0xF0 & HandshakeFeatures.ReservedMask).Should().Be(HandshakeFeatures.ReservedMask);

        // 版本协商矩阵：同代选同版；N-1 区间收敛到 1
        HandshakeCodec.TryNegotiateVersion(1, 1, 1, 1, out var v1).Should().BeTrue();
        v1.Should().Be(1);
        HandshakeCodec.TryNegotiateVersion(1, 2, 1, 1, out var v2).Should().BeTrue();
        v2.Should().Be(1, "交集最高——N-1 对端收敛到旧版");
        HandshakeCodec.TryNegotiateVersion(2, 2, 1, 1, out _).Should().BeFalse("N-2 对端无交集即拒绝");
    }

    private sealed class DelegateHandler(Func<byte[], ValueTask> onPayload) : IRequestHandler
    {
        public void OnRequest(NodeId from, ReadOnlyMemory<byte> payload, IReplyContext reply)
            => _ = onPayload(payload.ToArray()).AsTask();
    }

    private sealed class StubReply : IReplyContext
    {
        public NodeId Peer => NodeId.NewRandom();
        public byte ProtocolId => 0x70;
        public ulong CorrelationId => 0;
        public ValueTask ReplyAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default) => ValueTask.CompletedTask;
    }
}
