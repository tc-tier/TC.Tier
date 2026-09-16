using System.Net;
using FluentAssertions;
using TC.Tier.Core.Net;
using TC.Tier.Core.Net.Channels;
using TC.Tier.Core.Net.Transport;
using Xunit;

namespace TC.Tier.Core.Net.Tests.Transport;

/// <summary>
/// 运行时配置热更新（二期-D6 NETGAP-011——验证矩阵 D6 行）：Update/Get 快照往返与隔离、
/// 校验整体拒绝（不部分生效）、请求超时旋钮热效应（调小即超时、调大即通过）。
/// </summary>
public class RuntimeTunablesTests
{
    private static int ReservePort()
    {
        var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        socket.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        var port = ((System.Net.IPEndPoint)socket.LocalEndPoint!).Port;
        socket.Close();
        return port;
    }

    private sealed class DelayedEchoHandler(TimeSpan delay) : IRequestHandler
    {
        public void OnRequest(NodeId from, ReadOnlyMemory<byte> payload, IReplyContext reply)
            => _ = Task.Run(async () =>   // 延迟应答（使用方管理的后台形态）
            {
                try
                {
                    await Task.Delay(delay);
                    await reply.ReplyAsync(payload.ToArray());
                }
                catch { /* 链路已断——尽力语义 */ }
            });
    }

    /// <summary>D6：Update/Get 往返 + 快照隔离（改写返回快照不影响内部旋钮）。</summary>
    [Fact]
    public void Update_Get_RoundTrip_AndSnapshotIsolation()
    {
        var port = ReservePort();
        var transport = new ClusterTransport(NodeId.NewRandom(),
            TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, port), new Dictionary<NodeId, IPEndPoint>()));
        try
        {
            var snapshot = transport.UpdateRuntimeTunables(t =>
            {
                t.RequestTimeout = TimeSpan.FromSeconds(5);
                t.HandshakeTimeout = TimeSpan.FromSeconds(4);
                t.ReconnectBackoffFactor = 3.0;
            });

            snapshot.RequestTimeout.Should().Be(TimeSpan.FromSeconds(5));
            snapshot.HandshakeTimeout.Should().Be(TimeSpan.FromSeconds(4));
            snapshot.ReconnectBackoffFactor.Should().Be(3.0);

            var read = transport.GetRuntimeTunables();
            read.RequestTimeout.Should().Be(TimeSpan.FromSeconds(5), "内部旋钮已生效");

            snapshot.RequestTimeout = TimeSpan.FromSeconds(99);   // 改写外部快照
            transport.GetRuntimeTunables().RequestTimeout.Should().Be(TimeSpan.FromSeconds(5),
                "快照与内部状态隔离——外部不可变更旋钮");
        }
        finally { transport.DisposeAsync().AsTask().Wait(); }
    }

    /// <summary>D6：校验整体拒绝——非法旋钮抛且不部分生效（先前合法更新保持）。</summary>
    [Fact]
    public void Update_Invalid_RejectsWhole_KeepsPrevious()
    {
        var port = ReservePort();
        var transport = new ClusterTransport(NodeId.NewRandom(),
            TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, port), new Dictionary<NodeId, IPEndPoint>()));
        try
        {
            transport.UpdateRuntimeTunables(t => t.RequestTimeout = TimeSpan.FromSeconds(5));

            var act = () => transport.UpdateRuntimeTunables(t =>
            {
                t.RequestTimeout = TimeSpan.FromSeconds(6);   // 合法
                t.HandshakeTimeout = TimeSpan.Zero;           // 非法
            });
            act.Should().Throw<ArgumentOutOfRangeException>("非法旋钮整体拒绝");
            transport.GetRuntimeTunables().RequestTimeout.Should().Be(TimeSpan.FromSeconds(5),
                "合法旋钮不因同批非法项回滚——整体拒绝语义（先前状态保持）");
        }
        finally { transport.DisposeAsync().AsTask().Wait(); }
    }

    /// <summary>D6：请求超时旋钮热效应——调小后慢应答即超时；调大后同一慢应答通过。</summary>
    [Fact]
    public async Task RequestTimeout_HotUpdate_ChangesEffectiveWait()
    {
        var port = ReservePort();
        var serverId = NodeId.NewRandom();
        var server = new ClusterTransport(serverId,
            TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, port), new Dictionary<NodeId, IPEndPoint>()));
        server.Start();
        var client = new ClusterTransport(NodeId.NewRandom(),
            TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, 0), new Dictionary<NodeId, IPEndPoint>()));
        try
        {
            client.UpdateRuntimeTunables(t => t.RequestTimeout = TimeSpan.FromMilliseconds(2000));
            server.RegisterRequestHandler(0x70, new DelayedEchoHandler(TimeSpan.FromMilliseconds(800)));

            await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port)).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

            // 调小至 300ms——800ms 慢应答超时
            client.UpdateRuntimeTunables(t => t.RequestTimeout = TimeSpan.FromMilliseconds(300));
            var slow = async () => await client.SendRequestAsync(serverId, 0x70, new byte[] { 0x01 }).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
            (await slow.Should().ThrowAsync<TimeoutException>().WaitAsync(TimeSpan.FromSeconds(5)))
                .Which.Message.Should().NotBeNull("300ms 等待窗内无应答——热更新即时生效");

            // 调大至 5s——同一慢应答通过
            client.UpdateRuntimeTunables(t => t.RequestTimeout = TimeSpan.FromSeconds(5));
            var ok = await client.SendRequestAsync(serverId, 0x70, new byte[] { 0x01 }).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
            ok.Should().Equal(new byte[] { 0x01 }, "旋钮调大后同一延迟应答在窗内");
        }
        finally
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
        }
    }
}
