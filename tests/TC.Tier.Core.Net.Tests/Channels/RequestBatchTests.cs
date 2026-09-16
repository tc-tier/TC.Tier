using System.Net;
using FluentAssertions;
using TC.Tier.Core.Net;
using TC.Tier.Core.Net.Channels;
using Xunit;

namespace TC.Tier.Core.Net.Tests.Channels;

/// <summary>
/// 协议级批量/管道（二期-E5 NETGAP-033——验证矩阵 E5 行）：
/// 批量信封往返（多项/单项/空项）、一次往返逐项应答有序、单项异常不炸整批（逐项错误标记）、
/// 管道（多批并发在途）。
/// </summary>
public class RequestBatchTests
{
    private const byte BatchDomain = 0x74;   // 注册区（0x60-0xAF 使用方自管号——测试域）

    private sealed class EchoHandler : IRequestHandler
    {
        public void OnRequest(NodeId from, ReadOnlyMemory<byte> payload, IReplyContext reply)
            => _ = reply.ReplyAsync(payload.ToArray()).AsTask();
    }

    /// <summary>E5：批量信封编解码往返——3 项/空项/单/多。</summary>
    [Fact]
    public void EncodeDecode_RoundTrip()
    {
        var items = new ReadOnlyMemory<byte>[]
        {
            new byte[] { 0x01, 0x02 },
            Array.Empty<byte>(),
            new byte[] { 0xF0, 0xF1, 0xF2 },
        };
        var encoded = RequestBatch.EncodeRequest(items);
        var decoded = RequestBatch.DecodeRequest(encoded);
        decoded.Should().HaveCount(3);
        decoded[0].ToArray().Should().Equal(new byte[] { 0x01, 0x02 });
        decoded[1].Length.Should().Be(0);
        decoded[2].ToArray().Should().Equal(new byte[] { 0xF0, 0xF1, 0xF2 });
    }

    /// <summary>E5：畸形批量防御——计数为负/截断抛。</summary>
    [Fact]
    public void Decode_Malformed_Throws()
    {
        var negative = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
        var actNeg = () => RequestBatch.DecodeRequest(negative);
        actNeg.Should().Throw<ArgumentOutOfRangeException>();

        var truncated = new byte[] { 0x01, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00 };
        var actTrunc = () => RequestBatch.DecodeRequest(truncated);
        actTrunc.Should().Throw<InvalidOperationException>("截断");
    }

    /// <summary>E5 集成：一次往返批量 3 项——逐项有序回程。</summary>
    [Fact]
    public async Task Batch_Integration_ItemsInOrder()
    {
        var port = ReservePort();
        var serverId = NodeId.NewRandom();
        var server = new ClusterTransport(serverId,
            TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, port), new Dictionary<NodeId, IPEndPoint>()));
        server.Start();
        server.RegisterRequestHandler(BatchDomain, new RequestBatchHandler(new EchoHandler()));

        var client = new ClusterTransport(NodeId.NewRandom(),
            TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, 0), new Dictionary<NodeId, IPEndPoint>()));
        client.Start();
        try
        {
            await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port)).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

            var items = new List<ReadOnlyMemory<byte>>
            {
                new byte[] { 0x11 },
                new byte[] { 0x22, 0x33 },
                new byte[] { 0x44, 0x55, 0x66 },
            };
            var batch = RequestBatch.EncodeRequest(items);
            var respBytes = await client.SendRequestAsync(serverId, BatchDomain, batch).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
            var results = RequestBatch.DecodeResponse(respBytes);

            results.Should().HaveCount(3);
            for (var i = 0; i < items.Count; i++)
                results[i].ToArray().Should().Equal(items[i].ToArray(), $"item {i} 有序回程");
        }
        finally
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    /// <summary>E5：管道——两批并发在途均完成（CorrId 独立配对）。</summary>
    [Fact]
    public async Task Pipeline_ConcurrentBatches_BothComplete()
    {
        var port = ReservePort();
        var serverId = NodeId.NewRandom();
        var server = new ClusterTransport(serverId,
            TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, port), new Dictionary<NodeId, IPEndPoint>()));
        server.Start();
        server.RegisterRequestHandler(BatchDomain, new RequestBatchHandler(new EchoHandler()));

        var client = new ClusterTransport(NodeId.NewRandom(),
            TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, 0), new Dictionary<NodeId, IPEndPoint>()));
        client.Start();
        try
        {
            await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port)).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

            var batch = RequestBatch.EncodeRequest(new ReadOnlyMemory<byte>[] { new byte[] { 0x01 } });
            var t1 = client.SendRequestAsync(serverId, BatchDomain, batch).AsTask();
            var t2 = client.SendRequestAsync(serverId, BatchDomain, batch).AsTask();
            await Task.WhenAll(t1, t2).WaitAsync(TimeSpan.FromSeconds(5));

            RequestBatch.DecodeResponse(t1.Result).Should().HaveCount(1);
            RequestBatch.DecodeResponse(t2.Result).Should().HaveCount(1);
        }
        finally
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    /// <summary>E5：单项异常——逐项错误标记不炸整批。</summary>

    /// <summary>E5：单项异常——逐项错误标记不炸整批（StrictEcho：空载荷抛；批量应答逐项 status）。</summary>
    [Fact]
    public void BatchItem_Error_MarkedNotThrown()
    {
        var batchHandler = new RequestBatchHandler(new StrictEchoHandler());
        var captured = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var capture = new CaptureReply(captured);

        var items = new ReadOnlyMemory<byte>[]
        {
            new byte[] { 0x01 },
            Array.Empty<byte>(),          // StrictEcho 抛——该项错误标记
            new byte[] { 0x02 },
        };
        batchHandler.OnRequest(default, RequestBatch.EncodeRequest(items), capture);

        var respBytes = captured.Task.Result;
        var count = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(respBytes);
        count.Should().Be(3, "整批不炸——逐项 status");
        var cursor = 4;
        var statuses = new List<byte>();
        var payloads = new List<byte[]>();
        for (var i = 0; i < count; i++)
        {
            statuses.Add(respBytes[cursor]);
            var len = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(respBytes.AsSpan(cursor + 1));
            payloads.Add(respBytes.AsSpan(cursor + 5, len).ToArray());
            cursor += 5 + len;
        }
        statuses.Should().Equal(new byte[] { 0, 1, 0 }, "item1 错误标记、其余 ok");
        payloads[0].Should().Equal(new byte[] { 0x01 });
        payloads[2].Should().Equal(new byte[] { 0x02 });
    }

    private sealed class StrictEchoHandler : IRequestHandler
    {
        public void OnRequest(NodeId from, ReadOnlyMemory<byte> payload, IReplyContext reply)
        {
            if (payload.Length == 0) throw new InvalidOperationException("empty");
            _ = reply.ReplyAsync(payload.ToArray()).AsTask();
        }
    }

    private sealed class CaptureReply(TaskCompletionSource<byte[]> captured) : IReplyContext
    {
        public NodeId Peer => default;
        public byte ProtocolId => BatchDomain;
        public ulong CorrelationId => 0;

        public ValueTask ReplyAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default)
        {
            captured.TrySetResult(payload.ToArray());
            return ValueTask.CompletedTask;
        }
    }

    private static int ReservePort()
    {
        var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        socket.Close();
        return port;
    }
}
