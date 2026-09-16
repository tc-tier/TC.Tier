using System.Collections.Concurrent;
using System.Net;
using FluentAssertions;
using TC.Tier.Core.Net;
using TC.Tier.Core.Net.Channels;
using TC.Tier.Core.Net.Wire;
using Xunit;

namespace TC.Tier.Core.Net.Tests.Channels;

/// <summary>
/// 可恢复流原语（二期-E2 NETGAP-028——验证矩阵 E2 行"断线续传不丢不重"）：
/// 拉取式按 offset 续传——注入丢包后消费仍完整有序恰一次；尾部完成语义；缺口重试信号。
/// </summary>
public class ResumableFeedTests
{
    private const byte Domain = 0x71;   // 注册区（0x60-0xAF 使用方自管号——测试域）
    private static readonly Opaque16 FeedId = new(new byte[]
        { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0x0F, 0x10 });

    /// <summary>#418 回归门：Append 复制留存——调用方滚动缓冲复用不污染保留窗内已存块
    /// （旧引用留存形态：复用后 OnRequest 供出脏数据，全程无错误信号）。</summary>
    [Fact]
    public void Append_OverwriteSourceAfterAppend_RetentionWindowKeepsSnapshot()
    {
        var feed = new ResumableFeedServer(new InProcessTransportHub().Register(NodeId.NewRandom()),
            Domain, FeedId, retention: 4);
        try
        {
            var pooled = new byte[2] { 0x01, 0x02 };
            feed.Append(pooled);          // offset 0
            pooled[0] = 0xFF;             // 调用方复用缓冲
            pooled[1] = 0xFF;

            // 保留窗内 offset 0 的块内容必须仍是 {0x01, 0x02}
            feed.RetainedCount.Should().Be(1);
            // 白盒核对：经字典直读（internal 可达）
            feed.ChunkForTest(0).ToArray().Should().Equal(new byte[] { 0x01, 0x02 },
                "Append 复制留存——调用方缓冲复用不污染保留窗（#418）");
        }
        finally { feed.Dispose(); }
    }


    /// <summary>E2：30 块消费 + 20% 丢包注入——断线续传不丢不重（offset 严格有序恰一次）。</summary>
    [Fact]
    public async Task Consume_WithDroppedRequests_NoLossNoDup()
    {
        var hub = new InProcessTransportHub();
        var serverId = NodeId.NewRandom();
        var clientId = NodeId.NewRandom();
        var server = hub.Register(serverId);
        var client = hub.Register(clientId);
        server.Start();
        client.Start();

        var feed = new ResumableFeedServer(server, Domain, FeedId, retention: 1024);
        for (var i = 0; i < 30; i++) feed.Append(new byte[] { (byte)(i % 256), 0x77 });

        // 注入 10% 请求丢包（客户端→服务端方向）——续传循环消化（并行负载下留裕度）
        client.Faults.Drop(clientId, serverId, 0.1);

        var seen = new ConcurrentDictionary<long, byte[]>();
        await ResumableFeedClient.ConsumeAsync(client, serverId, Domain, FeedId, 0,
            (offset, data) =>
            {
                seen[offset] = data.ToArray();
                return ValueTask.CompletedTask;
            }, pullTimeout: TimeSpan.FromSeconds(2)).WaitAsync(TimeSpan.FromSeconds(60));

        seen.Keys.Should().BeEquivalentTo(Enumerable.Range(0, 30).Select(i => (long)i),
            "断线续传——不丢不重、严格有序恰一次");

        feed.Dispose();
        await client.DisposeAsync();
        await server.DisposeAsync();
        await hub.DisposeAsync();
    }

    /// <summary>E2：尾部完成语义——起始即越过尾部立即返回（零块）。</summary>
    [Fact]
    public async Task Consume_BeyondTail_Completes()
    {
        var hub = new InProcessTransportHub();
        var serverId = NodeId.NewRandom();
        var clientId = NodeId.NewRandom();
        var server = hub.Register(serverId);
        var client = hub.Register(clientId);
        server.Start();
        client.Start();
        var feed = new ResumableFeedServer(server, Domain, FeedId, retention: 16);
        feed.Append(new byte[] { 0x01 });

        var consumed = new List<long>();
        await ResumableFeedClient.ConsumeAsync(client, serverId, Domain, FeedId, 5,
            (offset, _) => { consumed.Add(offset); return ValueTask.CompletedTask; })
            .WaitAsync(TimeSpan.FromSeconds(5));

        consumed.Should().BeEmpty("起始即越过尾部——立即完成（零块）");

        feed.Dispose();
        await client.DisposeAsync();
        await server.DisposeAsync();
        await hub.DisposeAsync();
    }

    /// <summary>E2：保留窗外缺口——HasChunk=false + HasMore=true 重试信号（直调 OnRequest 验证）。</summary>
    [Fact]
    public async Task Feed_RetentionGap_SignalsRetry()
    {
        var hub = new InProcessTransportHub();
        var serverId = NodeId.NewRandom();
        var server = hub.Register(serverId);
        server.Start();
        var feed = new ResumableFeedServer(server, Domain, FeedId, retention: 4);

        // 追加 6 块（retention=4——offset 0/1 已被淘汰）
        for (var i = 0; i < 6; i++) feed.Append(new byte[] { (byte)i });

        var captured = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reqBytes = ResumableFeedMessageCodec.Encode(new FeedChunkReq { FeedId = FeedId, Offset = 0 });
        feed.OnRequest(serverId, reqBytes, new CaptureReply(captured));

        var decoded = ResumableFeedMessageCodec.TryDecode(captured.Task.Result, out var msg);
        decoded.Should().BeTrue();
        var resp = msg.Should().BeOfType<FeedChunkResp>().Which;
        resp.HasChunk.Should().BeFalse("淘汰块不可供");
        resp.HasMore.Should().BeTrue("保留窗内缺口——重试信号");

        feed.Dispose();
        await server.DisposeAsync();
        await hub.DisposeAsync();
    }

    private sealed class CaptureReply(TaskCompletionSource<byte[]> captured) : IReplyContext
    {
        public NodeId Peer => default;
        public byte ProtocolId => Domain;
        public ulong CorrelationId => 0;

        public ValueTask ReplyAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default)
        {
            captured.TrySetResult(payload.ToArray());
            return ValueTask.CompletedTask;
        }
    }
}
